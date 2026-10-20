using System.Text;
using System.Text.RegularExpressions;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Sharing;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// "Copy for chat": the compact Lumi code, the snippet that carries it through Teams or Slack, and the
/// clipboard offer that picks it up on the other side.
/// </summary>
public sealed class CapabilityChatShareTests
{
    private static DataStore NewStore(AppData? data = null)
        => new(
            data ?? new AppData { Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false, OfferCopiedCapabilities = true } },
            Path.Combine(Path.GetTempPath(), "LumiChatShareTests", Guid.NewGuid().ToString("N")));

    private static Skill ToneGuide() => new()
    {
        Name = "Tone Guide",
        Description = "Friendly, direct replies · שלום 👋",
        Content = "# Tone\n\nBe warm and direct.\n\n- Lead with the answer\n- Use plain words\n",
        IconGlyph = "🎨"
    };

    /// <summary>
    /// What a teammate sent. Unique per test: copies made by this process are remembered (so a sharer
    /// is never offered their own share) and other tests here copy skills too.
    /// </summary>
    private static Skill TeammateSkill() => new()
    {
        Name = "Standup Notes",
        Description = "Turns bullet points into a standup update.",
        Content = "Summarise yesterday, today and blockers. " + Guid.NewGuid().ToString("N"),
        IconGlyph = "📝"
    };

    /// <summary>A long, realistic skill: prose, lists and code, the way people actually write them.</summary>
    private static string LongInstructions()
    {
        var topics = new[] { "pull requests", "release notes", "incident reviews", "design docs", "API changes", "test plans" };
        var builder = new StringBuilder("# Engineering writing guide\n\n");
        for (var i = 0; i < 180; i++)
        {
            var topic = topics[i % topics.Length];
            builder.Append("## ").Append(i + 1).Append(". Writing ").Append(topic).Append('\n');
            builder.Append("When the user asks for help with ").Append(topic)
                .Append(", first read the surrounding context, then summarise the change in one sentence. ")
                .Append("Prefer concrete examples over adjectives, and link to the source (item ").Append(i * 7 % 97).Append(").\n");
            if (i % 5 == 0)
                builder.Append("```bash\ngit log --oneline -n ").Append(i + 3).Append(" -- src/\n```\n");
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static (DataStore Store, LumiAgent Agent) StoreWithResearchLumi()
    {
        var store = NewStore();
        var skill = new Skill { Name = "Citations", Description = "Cite sources.", Content = "Always cite.", IconGlyph = "🔖" };
        var server = new McpServer
        {
            Name = "Brave Search",
            Command = "npx",
            Args = ["-y", "@modelcontextprotocol/server-brave-search"],
            Env = new() { ["BRAVE_API_KEY"] = "super-secret-brave-key" }
        };
        var agent = new LumiAgent
        {
            Name = "Research Buddy",
            Description = "Digs deep and cites everything.",
            SystemPrompt = "Research carefully.",
            IconGlyph = "🔎",
            SkillIds = [skill.Id],
            McpServerIds = [server.Id]
        };
        store.Data.Skills.Add(skill);
        store.Data.McpServers.Add(server);
        store.Data.Agents.Add(agent);
        return (store, agent);
    }

    private static string SnippetFor(CapabilityShare share)
        => ShareSheetViewModel.ComposeChatSnippet(share.Pack, ShareCode.Encode(CapabilityPackWriter.WriteCompact(share))).Text;

    // ── The code ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Code_RoundTripsTheTextExactly()
    {
        var text = "---\nname: tone-guide\ndescription: \"Friendly · שלום 👋\"\n---\n\nBe warm.\n";

        var code = ShareCode.Encode(text.Replace("\n", "\r\n"));
        var decoded = ShareCode.Decode(code);

        Assert.Equal(ShareCodeStatus.Decoded, decoded.Status);
        Assert.Equal(text, decoded.Text);
    }

    [Fact]
    public void Code_IsOneChatSafeLineAndMuchSmallerThanTheText()
    {
        var text = CapabilityPackWriter.ForSkill(new Skill { Name = "Writing Guide", Content = LongInstructions() }).Text;

        var code = ShareCode.Encode(text);

        Assert.Matches(@"^lumi1\.[A-Za-z0-9_-]+$", code);
        Assert.True(text.Length > 20_000, $"fixture should be large, was {text.Length}");
        Assert.True(code.Length < text.Length / 2, $"code {code.Length} chars for {text.Length} chars of text");
    }

    public static TheoryData<string, Func<string, string>> WhatChatsDoToCodes => new()
    {
        { "surrounded by the message", code => "📘 Tone Guide · Lumi skill\nCopy the code below.\n```lumi\n" + code + "\n```\nthanks!" },
        { "windows line endings", code => "Here you go:\r\n```\r\n" + code + "\r\n```\r\n" },
        { "wrapped at 76 columns", code => string.Join("\n", Chunk(code, 76)) },
        { "wrapped then followed by a word", code => string.Join("\n", Chunk(code, 60)) + "\nthanks" },
        { "zero-width spaces", code => string.Join("\u200B", Chunk(code, 20)) },
        { "soft hyphens and a BOM", code => "\uFEFF" + string.Join("\u00AD", Chunk(code, 33)) },
        { "a later mention of a newer Lumi", code => "Works with lumi2.0 too?\n" + code },
        { "indented inside a quote", code => "> " + code }
    };

    [Theory]
    [MemberData(nameof(WhatChatsDoToCodes))]
    public void Code_SurvivesWhatChatsDoToIt(string scenario, Func<string, string> mangle)
    {
        var text = CapabilityPackWriter.ForSkill(ToneGuide()).Text;

        var decoded = ShareCode.Decode(mangle(ShareCode.Encode(text)));

        Assert.True(decoded.Status == ShareCodeStatus.Decoded, scenario);
        Assert.Equal(PackText.NormalizeNewlines(text), decoded.Text);
    }

    [Fact]
    public void Code_ThatWasCutShortOrChangedIsReportedAsDamaged()
    {
        var code = ShareCode.Encode(CapabilityPackWriter.ForSkill(ToneGuide()).Text);
        var middle = code.Length / 2;
        var altered = code[..middle] + (code[middle] == 'A' ? 'B' : 'A') + code[(middle + 1)..];

        Assert.Equal(ShareCodeStatus.Damaged, ShareCode.Decode(code[..^12]).Status);
        Assert.Equal(ShareCodeStatus.Damaged, ShareCode.Decode(altered).Status);
    }

    [Fact]
    public void Code_FromANewerLumiAndProseMentionsAreToldApart()
    {
        var body = ShareCode.Encode("---\nname: x\n---\n\nBody\n")[ShareCode.Prefix.Length..];

        Assert.Equal(ShareCodeStatus.NewerVersion, ShareCode.Decode("lumi2." + body).Status);
        Assert.False(ShareCode.Contains("We moved to lumi1.2 last week, and lumi2.0 is next."));
        Assert.Equal(ShareCodeStatus.NotFound, ShareCode.Decode("We moved to lumi1.2 last week.").Status);
        Assert.False(ShareCode.Contains("illumi1.aaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [Fact]
    public void Code_WithInvisibleCharactersInsideItsPrefixIsStillFound()
    {
        var code = ShareCode.Encode(CapabilityPackWriter.ForSkill(ToneGuide()).Text);

        var mangled = "lu\u200Bmi1\u00AD." + code[ShareCode.Prefix.Length..];

        Assert.True(ShareCode.Contains(mangled));
        Assert.Equal(ShareCodeStatus.Decoded, ShareCode.Decode(mangled).Status);
    }

    [Fact]
    public void Code_ThatIsLongAndWrappedIntoHundredsOfLinesStillDecodes()
    {
        var random = new Random(11);
        var words = Enumerable.Range(0, 300)
            .Select(_ => new string(Enumerable.Range(0, random.Next(3, 10)).Select(_ => (char)random.Next('a', 'z' + 1)).ToArray()))
            .ToArray();
        var content = string.Join(' ', Enumerable.Range(0, 40_000).Select(_ => words[random.Next(words.Length)]));
        var text = CapabilityPackWriter.ForSkill(new Skill { Name = "Word Soup", Content = content }).Text;
        var code = ShareCode.Encode(text);
        var lines = Chunk(code, 76).ToList();

        var decoded = ShareCode.Decode(string.Join("\n", lines) + "\nthanks");

        Assert.True(lines.Count > 400, $"fixture should wrap into many lines, was {lines.Count}");
        Assert.Equal(ShareCodeStatus.Decoded, decoded.Status);
        Assert.Equal(PackText.NormalizeNewlines(text), decoded.Text);
    }

    [Fact]
    public void Code_CraftedToInflateHugelyIsRejectedQuickly()
    {
        var zeros = new byte[40_000_000];
        var compressed = new byte[BrotliEncoderMax(zeros.Length)];
        Assert.True(System.IO.Compression.BrotliEncoder.TryCompress(zeros, compressed, out var written, quality: 1, window: 22));
        var payload = new byte[4 + written];
        compressed.AsSpan(0, written).CopyTo(payload.AsSpan(4));
        var code = ShareCode.Prefix + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hostile = string.Join("\n", Enumerable.Repeat(code + string.Concat(Enumerable.Repeat("\na", 400)), 20));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = ShareCode.Decode(hostile);
        watch.Stop();

        Assert.Equal(ShareCodeStatus.Damaged, result.Status);
        Assert.True(watch.ElapsedMilliseconds < 3_000, $"took {watch.ElapsedMilliseconds} ms");
    }

    private static int BrotliEncoderMax(int length) => System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(length);

    // ── Reading codes ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reader_ASkillSnippetFromChatImportsTheSameSkill()
    {
        var share = CapabilityPackWriter.ForSkill(ToneGuide());

        var result = CapabilityPackReader.Read(SnippetFor(share));

        Assert.True(result.Success, result.Error.ToString());
        var skill = Assert.Single(result.Pack!.Skills);
        Assert.Equal(CapabilityPackFormat.SkillMarkdown, result.Pack.Format);
        Assert.Equal("Tone Guide", skill.Name);
        Assert.Equal(ToneGuide().Description, skill.Description);
        Assert.Equal(ToneGuide().Content.TrimEnd(), skill.Content.TrimEnd());
        Assert.Equal("🎨", skill.IconGlyph);
    }

    [Fact]
    public void Reader_ALumiSnippetCarriesTheWholeBundleWithoutSecrets()
    {
        var (store, agent) = StoreWithResearchLumi();
        var share = CapabilityPackWriter.ForLumi(agent, store.Data);
        var snippet = SnippetFor(share);

        var fromCode = CapabilityPackReader.Read(snippet);
        var fromFile = CapabilityPackReader.Read(share.Text);

        Assert.True(fromCode.Success, fromCode.Error.ToString());
        var pack = fromCode.Pack!;
        var expected = fromFile.Pack!;
        Assert.Equal(SharedCapabilityKind.Lumi, pack.Kind);
        Assert.Equal(expected.Name, pack.Name);
        Assert.Equal(expected.Description, pack.Description);
        Assert.Equal(expected.IconGlyph, pack.IconGlyph);
        Assert.Equal(expected.Lumi!.SystemPrompt, pack.Lumi!.SystemPrompt);
        Assert.Equal(expected.Lumi.ToolNames, pack.Lumi.ToolNames);
        Assert.Equal(expected.Lumi.SkillSlugs, pack.Lumi.SkillSlugs);
        Assert.Equal(expected.Lumi.McpServerNames, pack.Lumi.McpServerNames);
        Assert.Equal(
            expected.Skills.Select(static skill => (skill.Slug, skill.Name, skill.Description, skill.Content, skill.IconGlyph)),
            pack.Skills.Select(static skill => (skill.Slug, skill.Name, skill.Description, skill.Content, skill.IconGlyph)));
        var server = Assert.Single(pack.McpServers);
        Assert.Equal(["BRAVE_API_KEY"], server.EnvKeys);
        Assert.Equal(expected.McpServers[0].CommandLine, server.CommandLine);
        Assert.DoesNotContain("super-secret-brave-key", snippet);
        Assert.DoesNotContain("super-secret-brave-key", ShareCode.Decode(snippet).Text);
    }

    [Fact]
    public void Reader_CompactPackDropsOnlyTheProse()
    {
        var (store, agent) = StoreWithResearchLumi();
        var share = CapabilityPackWriter.ForLumi(agent, store.Data);

        var compact = CapabilityPackWriter.WriteCompact(share);

        Assert.True(compact.Length < share.Text.Length);
        Assert.DoesNotContain("Lumi capability pack", compact);
        var pack = CapabilityPackReader.Read(compact).Pack!;
        Assert.Equal("Research Buddy", pack.Name);
        Assert.Equal("Research carefully.", pack.Lumi!.SystemPrompt);
        Assert.Single(pack.Skills);
        Assert.Single(pack.McpServers);
    }

    [Fact]
    public void Reader_DamagedCodesAndDocumentsThatMentionCodesAreHandledHonestly()
    {
        var code = ShareCode.Encode(CapabilityPackWriter.ForSkill(ToneGuide()).Text);
        var aboutCodes = "---\nname: sharing-help\ndescription: Explains codes.\n---\n\nA code looks like " + code + "\n";

        Assert.Equal(PackReadError.DamagedCode, CapabilityPackReader.Read("```lumi\n" + code[..^9] + "\n```").Error);
        var document = CapabilityPackReader.Read(aboutCodes);
        Assert.True(document.Success);
        Assert.Equal("Sharing Help", document.Pack!.Name);
    }

    // ── The snippet on the clipboard ───────────────────────────────────────────────────────────

    [Fact]
    public void Snippet_SaysWhatItIsInTextAndHtmlWithTheCodeInACodeBlock()
    {
        var skill = ToneGuide();
        skill.Name = "<b>Tone</b> & \"Co\"";
        var share = CapabilityPackWriter.ForSkill(skill);
        var code = ShareCode.Encode(CapabilityPackWriter.WriteCompact(share));

        var (text, html) = ShareSheetViewModel.ComposeChatSnippet(share.Pack, code);

        Assert.StartsWith("🎨 <b>Tone</b> & \"Co\" · " + Lumi.Localization.Loc.ChatSnippet_KindSkill + "\n", text);
        Assert.Contains(Lumi.Localization.Loc.ChatSnippet_Hint, text);
        Assert.EndsWith("```lumi\n" + code + "\n```\n", text);
        Assert.Contains("&lt;b&gt;Tone&lt;/b&gt; &amp; &quot;Co&quot;", html);
        Assert.DoesNotContain("<b><b>", html);
        Assert.EndsWith("<pre><code>" + code + "</code></pre>", html);
        Assert.Contains("&#127912;", html);
        Assert.All(html, static ch => Assert.True(ch < 128, $"non-ASCII {(int)ch} in clipboard HTML"));
    }

    [Fact]
    public void WindowsHtmlClipboard_OffsetsPointAtTheFragmentInUtf8Bytes()
    {
        const string fragment = "<p>🎨 <b>שלום</b></p><pre><code>lumi1.abc</code></pre>";

        var bytes = ClipboardHelper.BuildWindowsHtmlClipboard(fragment);
        var header = Encoding.ASCII.GetString(bytes, 0, 120);
        int Offset(string name) => int.Parse(Regex.Match(header, name + @":(\d{10})").Groups[1].Value);

        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment")));
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes, Offset("StartHTML"), 6));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
    }

    // ── Sending: one click, unless something deserves a look first ─────────────────────────────

    [Fact]
    public async Task QuickCopy_CopiesStraightAwayUnlessSomethingNeedsAReview()
    {
        var store = NewStore();
        var sheet = new ShareSheetViewModel(store, static () => []);
        var server = new McpServer { Name = "Local Tools", Command = @"C:\Users\adir\tools\server.exe" };
        var withKey = new McpServer { Name = "Brave", Command = "npx", Env = new() { ["BRAVE_API_KEY"] = "secret" } };

        Assert.NotNull(await sheet.QuickCopyForChatAsync(ToneGuide()));
        Assert.NotNull(await sheet.QuickCopyForChatAsync(withKey));
        Assert.False(sheet.IsOpen);

        Assert.Null(await sheet.QuickCopyForChatAsync(ToneGuide(), hasUnsavedEdits: true));
        Assert.True(sheet.IsOpen);
        Assert.True(sheet.HasUnsavedEdits);

        sheet.CloseCommand.Execute(null);
        Assert.Null(await sheet.QuickCopyForChatAsync(server));
        Assert.True(sheet.IsOpen);
        Assert.Contains(sheet.PrivacyNotes, note => note.Code?.Contains(@"C:\Users\adir\tools\server.exe", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ShareSheet_OffersCopyForChatFirstAndWarnsAboutVeryLongCodes()
    {
        var store = NewStore();
        var sheet = new ShareSheetViewModel(store, static () => []);

        sheet.OpenFor(ToneGuide());
        Assert.Equal(Lumi.Localization.Loc.Share_CopySkillMd, sheet.CopyTextLabel);
        Assert.False(sheet.IsChatCodeLong);

        var random = new Random(7);
        var noise = string.Concat(Enumerable.Range(0, 30_000).Select(_ => (char)random.Next('!', '~')));
        sheet.OpenFor(new Skill { Name = "Noise", Content = noise });
        Assert.True(sheet.IsChatCodeLong);
        Assert.Contains(sheet.ChatCodeSize, sheet.ChatCodeNote);
    }

    [Fact]
    public void MainViewModel_CopyForChatConfirmsWithANotice()
    {
        var store = NewStore();
        var skill = ToneGuide();
        store.Data.Skills.Add(skill);
        var vm = new MainViewModel(store, TestCopilot.Shared, new UpdateService());

        vm.SkillsVM.CopySkillForChatCommand.Execute(skill);

        Assert.False(vm.ShareVM.IsOpen);
        Assert.True(vm.NoticeVM.IsShown);
        Assert.False(vm.NoticeVM.IsOffer);
        Assert.Equal("🎨", vm.NoticeVM.Card!.Glyph);
        Assert.Contains("Tone Guide", vm.NoticeVM.Title);
    }

    // ── Receiving: the clipboard offer ─────────────────────────────────────────────────────────

    private sealed class FakeClipboard
    {
        public string? Text { get; set; }
        public int Reads { get; private set; }

        public Task<string?> ReadAsync()
        {
            Reads++;
            return Task.FromResult(Text);
        }
    }

    [Fact]
    public async Task Notice_OffersACopiedCapabilityAndPreviewOpensTheReceipt()
    {
        var recipient = NewStore();
        var (senderStore, agent) = StoreWithResearchLumi();
        var snippet = SnippetFor(CapabilityPackWriter.ForLumi(agent, senderStore.Data));
        var clipboard = new FakeClipboard { Text = snippet };
        var notice = new CapabilityNoticeViewModel(recipient, clipboard.ReadAsync, static () => true);
        string? previewed = null;
        notice.PreviewRequested += text => previewed = text;

        await notice.CheckClipboardAsync();

        Assert.True(notice.IsShown);
        Assert.True(notice.IsOffer);
        Assert.Contains("Research Buddy", notice.Title);
        Assert.Equal("🔎", notice.Card!.Glyph);
        Assert.Contains(Lumi.Localization.Loc.Capability_KindLumi, notice.Detail);

        notice.PreviewCommand.Execute(null);
        Assert.False(notice.IsShown);
        Assert.Equal(snippet, previewed);

        var import = new ImportSheetViewModel(recipient);
        import.OpenWithText(previewed!, Lumi.Localization.Loc.Import_FromClipboard);
        Assert.True(import.IsReviewing);
        Assert.Equal("Research Buddy", import.Card!.Name);
        Assert.True(import.IsRiskCaution);

        // Coming back again with the same code does not ask twice.
        clipboard.Text = "something else";
        await notice.CheckClipboardAsync();
        clipboard.Text = snippet;
        await notice.CheckClipboardAsync();
        Assert.False(notice.IsShown);
    }

    [Fact]
    public async Task Notice_StaysQuietForOwnCopiesDeclinedOffersAndWhatYouAlreadyHave()
    {
        var store = NewStore();
        var skill = TeammateSkill();
        var ownSnippet = SnippetFor(CapabilityPackWriter.ForSkill(new Skill { Name = "Mine", Content = "Mine." }));
        CapabilityNoticeViewModel.RememberOwnCopy(ownSnippet);
        var clipboard = new FakeClipboard { Text = ownSnippet };
        var notice = new CapabilityNoticeViewModel(store, clipboard.ReadAsync, static () => true);

        await notice.CheckClipboardAsync();
        Assert.False(notice.IsShown);

        clipboard.Text = SnippetFor(CapabilityPackWriter.ForSkill(skill));
        await notice.CheckClipboardAsync();
        Assert.True(notice.IsShown);
        notice.DismissCommand.Execute(null);
        Assert.False(notice.IsShown);

        clipboard.Text = "hello";
        await notice.CheckClipboardAsync();
        clipboard.Text = SnippetFor(CapabilityPackWriter.ForSkill(skill));
        await notice.CheckClipboardAsync();
        Assert.False(notice.IsShown);

        store.Data.Skills.Add(new Skill { Name = "Weekly Report", Content = "Report." });
        clipboard.Text = SnippetFor(CapabilityPackWriter.ForSkill(new Skill { Name = "Weekly Report", Content = "Report." }));
        await notice.CheckClipboardAsync();
        Assert.False(notice.IsShown);

        // A plain SKILL.md could be on the clipboard for any reason; only Lumi's own formats are offered.
        clipboard.Text = CapabilityPackWriter.ForSkill(new Skill { Name = "Plain", Content = "Plain." }).Text;
        await notice.CheckClipboardAsync();
        Assert.False(notice.IsShown);
    }

    [Fact]
    public async Task Notice_DoesNotOfferALumiYouAlreadyHave()
    {
        var (store, agent) = StoreWithResearchLumi();
        var snippet = SnippetFor(CapabilityPackWriter.ForLumi(agent, store.Data));
        // Copied back out of a chat: not byte-for-byte what this Lumi put on the clipboard.
        var clipboard = new FakeClipboard { Text = "Adir wrote:\n" + snippet.Replace("\n", "\r\n") };
        var notice = new CapabilityNoticeViewModel(store, clipboard.ReadAsync, static () => true);

        await notice.CheckClipboardAsync();

        Assert.False(notice.IsShown);
    }

    [Fact]
    public async Task Notice_AnOfferDisappearsWhenTheClipboardIsEmptied()
    {
        var store = NewStore();
        var clipboard = new FakeClipboard { Text = SnippetFor(CapabilityPackWriter.ForSkill(TeammateSkill())) };
        var notice = new CapabilityNoticeViewModel(store, clipboard.ReadAsync, static () => true);

        await notice.CheckClipboardAsync();
        Assert.True(notice.IsShown);

        clipboard.Text = null;
        await notice.CheckClipboardAsync();
        Assert.False(notice.IsShown);
    }

    [Fact]
    public async Task Notice_ReadsNothingWhenTurnedOffOrWhileASheetIsOpen()
    {
        var store = NewStore();
        var clipboard = new FakeClipboard { Text = SnippetFor(CapabilityPackWriter.ForSkill(TeammateSkill())) };
        var canOffer = false;
        var notice = new CapabilityNoticeViewModel(store, clipboard.ReadAsync, () => canOffer);

        await notice.CheckClipboardAsync();
        store.Data.Settings.OfferCopiedCapabilities = false;
        canOffer = true;
        await notice.CheckClipboardAsync();

        Assert.Equal(0, clipboard.Reads);
        Assert.False(notice.IsShown);

        store.Data.Settings.OfferCopiedCapabilities = true;
        await notice.CheckClipboardAsync();
        Assert.True(notice.IsShown);
    }

    [Fact]
    public async Task Notice_AnOfferDisappearsWhenTheClipboardMovesOn()
    {
        var store = NewStore();
        var clipboard = new FakeClipboard { Text = SnippetFor(CapabilityPackWriter.ForSkill(TeammateSkill())) };
        var notice = new CapabilityNoticeViewModel(store, clipboard.ReadAsync, static () => true);

        await notice.CheckClipboardAsync();
        Assert.True(notice.IsShown);

        clipboard.Text = "https://example.com";
        await notice.CheckClipboardAsync();
        Assert.False(notice.IsShown);
    }

    [Fact]
    public void MainViewModel_AnImportHidesAClipboardOfferThatNoLongerApplies()
    {
        var store = NewStore();
        var vm = new MainViewModel(store, TestCopilot.Shared, new UpdateService());
        vm.NoticeVM.IsOffer = true;
        vm.NoticeVM.IsShown = true;

        vm.ImportVM.OpenWithText(SnippetFor(CapabilityPackWriter.ForSkill(TeammateSkill())), "Clipboard");
        vm.ImportVM.ConfirmCommand.Execute(null);

        Assert.Contains(store.Data.Skills, skill => skill.Name == "Standup Notes");
        Assert.False(vm.NoticeVM.IsShown);
    }

    [Fact]
    public void Setting_IsOnByDefaultExceptOnMacAndSurvivesSaving()
    {
        Assert.Equal(!OperatingSystem.IsMacOS(), new UserSettings().OfferCopiedCapabilities);

        var data = new AppData { Settings = new UserSettings { OfferCopiedCapabilities = false } };
        Assert.False(AppDataSnapshotFactory.CreateIndexSnapshot(data).Settings.OfferCopiedCapabilities);
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
