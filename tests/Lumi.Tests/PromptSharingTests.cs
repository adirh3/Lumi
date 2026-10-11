using System.Text.Json;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Sharing;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class PromptSharingTests
{
    private static SharedPrompt Example() => new()
    {
        Name = "Create an expense dashboard",
        Description = "Create monthly charts without changing the source file.",
        Content = "Read {{expense_file}} without changing it. Create monthly and category charts in {{output_folder}}. Highlight recurring charges.",
        Inputs =
        [
            new("expense_file", "Expense export", "Your own bank export.", PromptInputKind.File),
            new("output_folder", "Output folder", "Where to save the workbook.", PromptInputKind.Folder)
        ],
        Requirements = ["An expense export and a tool that can create an Excel workbook."]
    };

    private static DataStore Store() => new(new AppData
    {
        Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false, OfferCopiedCapabilities = false }
    }, Path.Combine(Path.GetTempPath(), "LumiPromptShareTests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void PromptPack_TextAndChatCodeRoundTripWithoutImportingConfiguration()
    {
        var prompt = Example() with { Content = Example().Content + "\nKeep markdown fences: ```json\n{}\n```" };
        var share = CapabilityPackWriter.ForPrompt(prompt);
        Assert.StartsWith("---\nlumi-pack: 2\nkind: prompt\n", share.Text);
        Assert.Contains(PromptSharing.SetupGuidance, share.Text);

        foreach (var text in new[] { share.Text, CapabilityPackWriter.WriteCompact(share),
                     ShareCode.Encode(CapabilityPackWriter.WriteCompact(share)) })
        {
            var read = CapabilityPackReader.Read(text);
            Assert.True(read.Success, read.Detail);
            Assert.Equal(SharedCapabilityKind.Prompt, read.Pack!.Kind);
            Assert.Equal(prompt.Content, read.Pack.Prompt!.Content);
            Assert.Equal(prompt.Inputs, read.Pack.Prompt.Inputs);
            Assert.Equal(prompt.Requirements, read.Pack.Prompt.Requirements);
            Assert.Empty(read.Pack.Skills);
            Assert.Empty(read.Pack.McpServers);
            Assert.Null(read.Pack.Lumi);
        }
    }

    [Fact]
    public void ExistingCapabilityPacksStillUseVersionOne()
    {
        var share = CapabilityPackWriter.ForMcpServer(new McpServer { Name = "Search", Command = "npx", Args = ["-y", "search"] });
        Assert.StartsWith("---\nlumi-pack: 1\n", share.Text);
        Assert.True(CapabilityPackReader.Read(share.Text).Success);
    }

    [Fact]
    public void PromptPackRejectsMixedCapabilitiesInsteadOfSilentlyInstallingThem()
    {
        var text = CapabilityPackWriter.ForPrompt(Example()).Text
            + "\n```json lumi:mcp\n{\"mcpServers\":{\"surprise\":{\"command\":\"run-me\"}}}\n```\n";
        var result = CapabilityPackReader.Read(text);
        Assert.Equal(PackReadError.InvalidPrompt, result.Error);
        Assert.Null(result.Pack);
    }

    [Fact]
    public void PromptSourceKeepsRefinementsButNotPrivateContextOrLaterWork()
    {
        var user = new ChatMessage
        {
            Content = @"Create a dashboard from C:\Users\Sender\bank.csv. Email account is sender@example.com.",
            Attachments = [@"C:\Users\Sender\bank.csv"]
        };
        var result = new ChatMessage
        {
            Role = "assistant",
            Content = "Created the monthly dashboard with recurring charges.",
            ActiveSkills = [new SkillReference { Name = "Spreadsheet creator" }],
            ActiveMcpServerNames = ["Excel bridge"]
        };
        var chat = new Chat
        {
            Messages =
            [
                new() { Role = "system", Content = "PRIVATE_PROJECT_RULES" },
                user,
                new() { Role = "reasoning", Content = "PRIVATE_REASONING" },
                new() { Role = "tool", Content = "PRIVATE_COMMAND", ToolOutput = "PRIVATE_TOOL_OUTPUT" },
                new() { Role = "assistant", ParentToolCallId = "child", Content = "PRIVATE_CHILD_WORK" },
                new() { Content = "Keep the original file unchanged and use monthly charts." },
                result,
                new() { Content = "UNRELATED_LATER_TASK" }
            ]
        };
        var snapshot = chat.Messages.Select(static message => message.Clone()).ToList();
        var source = PromptDraftBuilder.Create(chat, result.Id, "Windows");

        Assert.Contains("Keep the original file unchanged", source.Text);
        Assert.Contains("SELECTED RESULT", source.Text);
        Assert.Contains("Source platform: Windows", source.Text);
        Assert.Contains("Excel bridge", source.Text);
        Assert.Contains("Contents are NOT included", source.Text);
        Assert.DoesNotContain(@"C:\Users\Sender", source.Text);
        Assert.DoesNotContain("sender@example.com", source.Text);
        Assert.DoesNotContain("PRIVATE_", source.Text);
        Assert.DoesNotContain("UNRELATED_LATER_TASK", source.Text);
        Assert.Equal(snapshot.Select(static message => message.Content), chat.Messages.Select(static message => message.Content));
    }

    [Theory]
    [InlineData("PASSWORD=super-private-value", "super-private-value")]
    [InlineData("Use API_KEY: 'super-private-value'.", "super-private-value")]
    [InlineData("URL https://admin:super-private-value@service.example.com/data?token=another-secret", "super-private-value")]
    [InlineData("Open `/home/sender/private export.csv`.", "/home/sender")]
    [InlineData("Open \"C:\\Users\\Sender Name\\private.csv\".", "Sender Name")]
    [InlineData("Call http://localhost:9123/private", "localhost:9123")]
    public void PromptSourceSanitizesMachineDetailsAndCredentials(string text, string privateValue)
    {
        Assert.DoesNotContain(privateValue, PromptDraftBuilder.Sanitize(text));
    }

    [Theory]
    [InlineData("Connect to the service.\nAuthorization: Bearer lumi-fixture-value-246")]
    [InlineData("Connect to the service.\nAuthorization: Basic lumi-fixture-value-246")]
    [InlineData("Use my API key: lumi-fixture-value-246")]
    public void PromptSourceRemovesWholeAuthorizationValuesAndFriendlyKeyLabels(string text)
    {
        Assert.DoesNotContain("lumi-fixture-value-246", PromptDraftBuilder.Sanitize(text));
    }

    [Fact]
    public void GeneratedPromptInspectionDoesNotMistakeEscapedRegexForASenderLocation()
    {
        var prompt = new SharedPrompt
        {
            Name = "Validate a version number",
            Description = "Match a dotted numeric version.",
            Content = """Validate version numbers with the regular expression \d+\.\d+\.\d+."""
        };
        Assert.False(PromptDraftBuilder.HasPersonalLocation(prompt));
    }

    [Fact]
    public void PromptSourcePreservesRequiredFileFormatsWithoutPersonalNamesOrLocations()
    {
        var safe = PromptDraftBuilder.Sanitize(
            """Convert "C:\Users\Sender\private-source.md" to "D:\PrivateExports\private-slides.pptx". Use one slide per section.""");
        Assert.Contains(".md", safe);
        Assert.Contains(".pptx", safe);
        Assert.Contains("one slide per section", safe);
        Assert.DoesNotContain("Sender", safe);
        Assert.DoesNotContain("private-", safe);
        Assert.False(PromptDraftBuilder.HasPersonalLocation(safe));
    }

    [Fact]
    public void PromptSourceKeepsAttachmentFormatsWithoutExportingAttachmentNames()
    {
        var result = new ChatMessage { Role = "assistant", Content = "The report is ready." };
        var chat = new Chat
        {
            Messages =
            [
                new() { Content = "Create a report from these files.", Attachments = [@"C:\Users\Sender\private.pdf", @"C:\Users\Sender\private.xlsx"] },
                result
            ]
        };
        var source = PromptDraftBuilder.Create(chat, result.Id, "Windows");
        Assert.Contains(".pdf", source.Text);
        Assert.Contains(".xlsx", source.Text);
        Assert.DoesNotContain("private.", source.Text);
        Assert.DoesNotContain("Sender", source.Text);
    }

    [Fact]
    public void PromptInspectionChecksDecodedFieldsAndAllowsSecureEnvironmentReferences()
    {
        var prompt = Example() with { Content = "Use Authorization: Bearer ${SERVICE_TOKEN} from existing secure settings.", Inputs = [] };
        Assert.False(PromptDraftBuilder.HasCredentialText(prompt));
        Assert.True(PromptDraftBuilder.HasCredentialText(prompt with
        {
            Requirements = ["Use API key: lumi-fixture-value-246"]
        }));
        Assert.True(PromptDraftBuilder.HasPersonalLocation(prompt with
        {
            Inputs = [new("source_file", "Source file", @"C:\Users\Sender\private.csv", PromptInputKind.File)]
        }));
    }

    [Fact]
    public void PromptSourceIsBoundedAndReportsShortenedHistory()
    {
        var chat = new Chat();
        for (var i = 0; i < 20; i++)
            chat.Messages.Add(new ChatMessage { Content = new string('x', 14_000) });
        var result = new ChatMessage { Role = "assistant", Content = "The selected final result." };
        chat.Messages.Add(result);
        var source = PromptDraftBuilder.Create(chat, result.Id, "Linux");
        Assert.True(source.WasShortened);
        Assert.True(source.Text.Length <= PromptDraftBuilder.MaxSourceLength);
        Assert.Contains(result.Content, source.Text);
    }

    [Fact]
    public void PromptSourceRefusesStreamingOrMissingResults()
    {
        var message = new ChatMessage { Role = "assistant", Content = "Still working", IsStreaming = true };
        var chat = new Chat { Messages = [message] };
        Assert.Throws<ArgumentException>(() => PromptDraftBuilder.Create(chat, message.Id, "Windows"));
        Assert.Throws<ArgumentException>(() => PromptDraftBuilder.Create(chat, Guid.NewGuid(), "Windows"));
    }

    [Fact]
    public void PromptValidationRejectsMissingDuplicateOrSecretInputs()
    {
        Assert.Throws<ArgumentException>(() => PromptSharing.Normalize(Example() with { Inputs = [] }));
        Assert.Throws<ArgumentException>(() => PromptSharing.Normalize(Example() with { Inputs = [Example().Inputs[0], Example().Inputs[0]] }));
        Assert.Throws<ArgumentException>(() => PromptSharing.Normalize(new SharedPrompt
        {
            Name = "Connect", Description = "", Content = "Use {{api_key}}",
            Inputs = [new("api_key", "API key", "Paste it here")]
        }));
        Assert.Throws<JsonException>(() => PromptSharing.Parse("""{"name":"Incomplete"}"""));
    }

    [Fact]
    public void PersonalizationUsesRecipientValuesOnceAndAlwaysIncludesSetupChecks()
    {
        var text = PromptSharing.Personalize(Example(), new Dictionary<string, string>
        {
            ["expense_file"] = "/home/recipient/{{output_folder}}.csv",
            ["output_folder"] = "/home/recipient/reports"
        });
        Assert.Contains("/home/recipient/{{output_folder}}.csv", text);
        Assert.Contains("in /home/recipient/reports", text);
        Assert.Contains("operating system", text);
        Assert.Contains("platform-specific", text);
        Assert.Contains("sender's approvals", text);
        Assert.Contains("Prerequisites to check", text);
    }

    [Fact]
    public void LightweightPromptGenerationSupportsByokAndHasNoToolsOrDiscovery()
    {
        var provider = new ProviderConfig { BaseUrl = "https://example.com/v1", ApiKey = "fixture-key" };
        var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
        {
            Model = "fixture-model",
            Provider = provider,
            SystemPrompt = PromptDraftBuilder.SystemPrompt
        });
        Assert.Same(provider, config.Provider);
        Assert.Equal("fixture-model", config.Model);
        Assert.False(config.EnableConfigDiscovery);
        Assert.False(config.EnableSessionStore);
        Assert.Empty(config.AvailableTools!);
        Assert.Null(config.CustomAgents);
        Assert.Null(config.SkillDirectories);
    }

    [Fact]
    public async Task ShareSheetGeneratesAnEditablePackAndUpdatesInputsWithoutAnotherLlmCall()
    {
        var sheet = new ShareSheetViewModel(Store(), static () => []);
        var calls = 0;
        await sheet.OpenForPromptAsync(new("Visible transcript", 3, false), (_, _) =>
        {
            calls++;
            return Task.FromResult(Example());
        });
        Assert.True(sheet.IsPrompt);
        Assert.True(sheet.CanShare);
        Assert.Equal(2, sheet.PromptInputs.Count);

        sheet.PromptContent += " Use {{currency}} for currency labels.";
        sheet.PromptName = "My improved dashboard";
        var read = CapabilityPackReader.Read(sheet.ShareText);
        Assert.True(read.Success, read.Detail);
        Assert.Equal("My improved dashboard", read.Pack!.Name);
        Assert.Equal(3, read.Pack.Prompt!.Inputs.Count);
        Assert.Equal(1, calls);
        sheet.CloseCommand.Execute(null);
    }

    [Fact]
    public async Task ShareSheetFailuresAreVisibleAndNeverReturnAnOriginalPromptFallback()
    {
        var sheet = new ShareSheetViewModel(Store(), static () => []);
        await sheet.OpenForPromptAsync(new("Visible transcript", 2, false), (_, _) =>
            Task.FromException<SharedPrompt>(new TimeoutException("Model unavailable")));
        Assert.True(sheet.IsOpen);
        Assert.True(sheet.IsStatusError);
        Assert.Contains("Model unavailable", sheet.StatusMessage);
        Assert.False(sheet.CanShare);
        Assert.Empty(sheet.ShareText);
        Assert.False(sheet.CopyForChatCommand.CanExecute(null));
        sheet.CloseCommand.Execute(null);
    }

    [Fact]
    public async Task ClosingShareSheetCancelsGenerationAndIgnoresLateResults()
    {
        var sheet = new ShareSheetViewModel(Store(), static () => []);
        var completion = new TaskCompletionSource<SharedPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var opening = sheet.OpenForPromptAsync(new("Visible transcript", 2, false), (_, ct) =>
        {
            token = ct;
            return completion.Task;
        });
        Assert.True(sheet.IsPreparingPrompt);
        Assert.False(sheet.CopyForChatCommand.CanExecute(null));
        sheet.CloseCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        completion.SetResult(Example());
        await opening;
        Assert.False(sheet.IsOpen);
        Assert.Empty(sheet.ShareText);
    }

    [Fact]
    public void ImportPromptRequiresPersonalInputsButDoesNotAddAnythingToTheLibrary()
    {
        var store = Store();
        var sheet = new ImportSheetViewModel(store);
        sheet.OpenWithText(CapabilityPackWriter.ForPrompt(Example()).Text, "From a teammate");
        Assert.True(sheet.IsPrompt);
        Assert.True(sheet.IsReviewing);
        Assert.False(sheet.CanConfirm);
        Assert.Contains("operating system", sheet.PromptPreviewText);
        Assert.All(sheet.PromptInputs, input => Assert.Empty(input.Value));
        Assert.Empty(store.Data.Chats);
        Assert.Empty(store.Data.Skills);
        Assert.Empty(store.Data.McpServers);
        Assert.Empty(store.Data.Agents);
    }

    [Fact]
    public void ImportPromptUsesOnlyRecipientFilesAndProducesAnUnsentDraft()
    {
        var file = Path.GetTempFileName();
        try
        {
            var store = Store();
            var sheet = new ImportSheetViewModel(store);
            PromptUseRequest? request = null;
            sheet.PromptUseRequested += value => request = value;
            sheet.OpenWithText(CapabilityPackWriter.ForPrompt(Example()).Text, "From a teammate");
            sheet.PromptInputs[0].Value = file;
            sheet.PromptInputs[1].Value = Path.GetTempPath();
            Assert.True(sheet.CanConfirm);
            sheet.ConfirmCommand.Execute(null);

            Assert.NotNull(request);
            Assert.Contains(file, request.Text);
            Assert.Equal([file], request.Attachments);
            Assert.False(sheet.IsOpen);
            Assert.Empty(store.Data.Chats);
            Assert.Empty(store.Data.Skills);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ImportPromptKeepsTheReviewOpenWhenTheRecipientFileIsMissing()
    {
        var sheet = new ImportSheetViewModel(Store());
        var used = false;
        sheet.PromptUseRequested += _ => used = true;
        sheet.OpenWithText(CapabilityPackWriter.ForPrompt(Example()).Text, "From a teammate");
        sheet.PromptInputs[0].Value = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        sheet.PromptInputs[1].Value = Path.GetTempPath();
        sheet.ConfirmCommand.Execute(null);
        Assert.False(used);
        Assert.True(sheet.IsOpen);
        Assert.True(sheet.IsReviewing);
        Assert.NotEmpty(sheet.PromptUseError!);
    }

    [Fact]
    public async Task ClipboardNoticeOffersPromptsEvenThoughTheyAreNotLibraryItems()
    {
        var store = Store();
        store.Data.Settings.OfferCopiedCapabilities = true;
        var code = ShareCode.Encode(CapabilityPackWriter.WriteCompact(CapabilityPackWriter.ForPrompt(Example())));
        var notice = new CapabilityNoticeViewModel(store, () => Task.FromResult<string?>(code), static () => true);
        await notice.CheckClipboardAsync();
        Assert.True(notice.IsOffer);
        Assert.True(notice.IsShown);
        Assert.Equal(SharedCapabilityKind.Prompt, notice.Card!.Kind);
    }

    [Fact]
    public async Task UsingSharedPromptOpensAFreshRecipientDraftWithoutSendingOrChangingTheSource()
    {
        using var session = HeadlessTestSession.Start();
        var store = Store();
        var source = new Chat
        {
            Title = "Existing conversation",
            Messages = [new() { Content = "My existing request" }, new() { Role = "assistant", Content = "My existing result" }]
        };
        store.Data.Chats.Add(source);
        ChatViewModel? before = null;
        ChatViewModel? after = null;
        string? newDraft = null;
        string? oldDraft = null;
        await session.Dispatch(() =>
        {
            using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false, skipOnboarding: true);
            before = main.ChatVM;
            before.CurrentChat = source;
            before.PromptText = "My unsent follow-up";
            main.ImportVM.OpenWithText(CapabilityPackWriter.ForPrompt(new SharedPrompt
            {
                Name = "Create something", Description = "A standalone task.", Content = "Create a useful report."
            }).Text, "From a teammate");
            main.ImportVM.ConfirmCommand.Execute(null);
            after = main.ChatVM;
            newDraft = after.PromptText;
            oldDraft = before.PromptText;
        }, CancellationToken.None);
        Assert.NotSame(before, after);
        Assert.Contains("Create a useful report.", newDraft);
        Assert.Contains("operating system", newDraft);
        Assert.Equal("My unsent follow-up", oldDraft);
        Assert.Equal(["My existing request", "My existing result"], source.Messages.Select(static message => message.Content));
        Assert.Single(store.Data.Chats);
        Assert.False(after!.IsBusy);
        Assert.False(after.IsStreaming);
    }

    [Fact]
    public void ExplicitComposerPastePreviewsPromptEvenWhenClipboardDetectionIsOff()
    {
        var store = Store();
        using var vm = new ChatViewModel(store, TestCopilot.Shared);
        var before = vm.PromptText = "My current draft";
        string? offered = null;
        vm.PromptImportRequested += text => offered = text;
        var code = ShareCode.Encode(CapabilityPackWriter.WriteCompact(CapabilityPackWriter.ForPrompt(Example())));
        Assert.True(vm.TryImportPastedPrompt(code));
        Assert.Equal(code, offered);
        Assert.Equal(before, vm.PromptText);
        Assert.False(vm.TryImportPastedPrompt("ordinary pasted text"));
    }

    [Fact]
    public void PromptSourceIncludesHumanDecisionsAndQuotesButNeverQuestionCredentials()
    {
        var result = new ChatMessage { Role = "assistant", Content = "The backup is ready." };
        var chat = new Chat
        {
            Messages =
            [
                new() { Content = "Set up a local backup." },
                new() { Role = "tool", ToolName = "ask_question", QuestionText = "Which days?", ToolOutput = "User answered: Monday to Friday" },
                new() { Role = "tool", ToolName = "ask_question", QuestionText = "What is your API key?", ToolOutput = "User answered: totally-private-value" },
                new() { Content = "Keep that choice.", ReplyTo = new MessageReply { Quote = "Retain seven dated copies." } },
                result
            ]
        };
        var source = PromptDraftBuilder.Create(chat, result.Id, "Windows");
        Assert.Contains("Monday to Friday", source.Text);
        Assert.Contains("Retain seven dated copies", source.Text);
        Assert.DoesNotContain("totally-private-value", source.Text);
    }

    [Fact]
    public void InvalidPlaceholderSyntaxCannotBecomeAReadyToUsePrompt()
    {
        Assert.Throws<ArgumentException>(() => PromptSharing.Normalize(new SharedPrompt
        {
            Name = "Task", Description = "", Content = "Use {{MissingFile}}."
        }));
    }

    [Fact]
    public async Task EditingAPlainPasswordIntoAPromptBlocksCopyUntilItIsRemoved()
    {
        var sheet = new ShareSheetViewModel(Store(), static () => []);
        await sheet.OpenForPromptAsync(new("Visible transcript", 2, false), (_, _) => Task.FromResult(Example()));
        sheet.PromptContent += "\nPASSWORD=totally-private-value";
        Assert.False(sheet.CanShare);
        Assert.True(sheet.IsStatusError);
        Assert.Empty(sheet.ShareText);
        sheet.PromptContent = Example().Content;
        Assert.True(sheet.CanShare);
        Assert.False(sheet.IsStatusError);
        sheet.CloseCommand.Execute(null);
    }

    [Fact]
    public void ChatActionsShareTheLatestCompletedResultWithoutSendingAnything()
    {
        using var vm = new ChatViewModel(Store(), TestCopilot.Shared);
        var completed = new ChatMessage { Role = "assistant", Content = "Completed useful result." };
        vm.CurrentChat = new Chat
        {
            Messages =
            [
                new() { Content = "Create something useful." },
                completed,
                new() { Content = "An unrelated later request." },
                new() { Role = "assistant", Content = "Still streaming.", IsStreaming = true }
            ]
        };
        PromptShareSource? source = null;
        vm.SharePromptRequested += (snapshot, _) => source = snapshot;
        vm.ShareLatestPromptCommand.Execute(null);
        Assert.NotNull(source);
        Assert.Contains(completed.Content, source.Text);
        Assert.DoesNotContain("An unrelated later request", source.Text);
        Assert.DoesNotContain("Still streaming", source.Text);
        Assert.False(vm.IsBusy);
    }
}
