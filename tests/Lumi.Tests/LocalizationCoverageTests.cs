using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.EmbeddedApps;
using Xunit;

namespace Lumi.Tests;

public sealed class LocalizationCoverageTests
{
    [Fact]
    public void EnglishAndHebrewHaveMatchingKeysAndFormatArguments()
    {
        var english = ReadCatalog("en");
        var hebrew = ReadCatalog("he");
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), hebrew.Keys.Order(StringComparer.Ordinal));

        foreach (var (key, value) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(hebrew[key]), $"Empty Hebrew translation: {key}");
            Assert.Equal(Arguments(value), Arguments(hebrew[key]));
            if (Arguments(value).Length > 0)
            {
                _ = CompositeFormat.Parse(value);
                _ = CompositeFormat.Parse(hebrew[key]);
            }
        }
    }

    [Fact]
    public void DesktopMarkupReferencesOnlyExistingTranslationKeys()
    {
        var keys = ReadCatalog("en");
        var root = Path.Combine(RepositoryRoot(), "src", "Lumi");
        var files = Directory.EnumerateFiles(Path.Combine(root, "Views"), "*.axaml", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "App.axaml"));
        var references = 0;
        foreach (var file in files)
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{\w+:Str\s+([A-Za-z_]\w*)"))
            {
                references++;
                Assert.True(keys.ContainsKey(match.Groups[1].Value),
                    $"Unknown translation {match.Groups[1].Value} in {Path.GetRelativePath(root, file)}");
            }
        }
        Assert.True(references > 0);
    }

    [Fact]
    public void DesktopCodeReferencesOnlyExistingLiteralTranslationKeys()
    {
        var keys = ReadCatalog("en");
        var root = Path.Combine(RepositoryRoot(), "src", "Lumi");
        var files = new[] { "Views", "ViewModels", "Services", "Localization", "Models" }
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(root, "*.cs"));
        foreach (var file in files)
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file),
                         "\\bLoc\\.Get\\(\"([A-Za-z_]\\w*)\"\\s*[,)]"))
            {
                Assert.True(keys.ContainsKey(match.Groups[1].Value),
                    $"Unknown translation {match.Groups[1].Value} in {Path.GetRelativePath(root, file)}");
            }
        }
    }

    [Fact]
    public void HebrewPresentationKeepsCanonicalIdsAndUserContent()
    {
        try
        {
            Loc.Load("he");
            Assert.Equal("צ׳אט חדש", new Chat().Title);
            Assert.Equal("ניהול הפרויקטים של Lumi",
                ToolDisplayHelper.GetFriendlyToolDisplay("manage_projects", null, """{"action":"list"}""").Name);
            Assert.Equal("ניהול הפרויקטים של Lumi", ToolDisplayHelper.FormatToolStatusName("manage_projects"));
            Assert.Equal("ברקע", ToolDisplayHelper.GetSubagentModeLabel("""{"mode":"background"}"""));

            var job = new BackgroundJob { ScheduleType = BackgroundJobScheduleTypes.Daily, DailyTime = "08:15" };
            Assert.Equal("בכל יום בשעה 08:15", BackgroundJobSchedule.Describe(job));
            Assert.Equal("daily", job.ScheduleType);
            Assert.False(BackgroundJobSchedule.TryValidateCronExpression("invalid", out var error));
            Assert.Equal(Loc.Jobs_CronFiveFields, error);
            Assert.Equal("יש להזין כתובת בסיס.", ByokConfigHelper.ValidateBaseUrl(""));

            var file = new GitFileChange
            {
                RelativePath = "example.cs", FullPath = Path.Combine(TestProfile.Root, "example.cs"),
                RepoRoot = TestProfile.Root, RepoRelativePath = "example.cs",
                Kind = GitChangeKind.Modified, StatusCode = " M"
            };
            Assert.Equal("Modified", file.KindLabel);
            Assert.Equal("שונה", file.DisplayKindLabel);
            Assert.Equal("project", CapabilityOrigin.Project.Id);
            Assert.Equal("פרויקט", CapabilityOrigin.Project.Label);

            var skill = new Skill
            {
                Name = "Document Creator", IsBuiltIn = true,
                Description = Loc.GetEnglish(nameof(Loc.BuiltIn_DocumentCreator_Description))
            };
            Assert.Equal("יוצר מסמכי Word, Excel ו-PowerPoint לפי התיאור שלך", skill.DisplayDescription);
            Assert.StartsWith("Creates Word", skill.Description);
            Assert.Equal("Document Creator", skill.Name);
            skill.Description = "My own description";
            Assert.Equal("My own description", skill.DisplayDescription);
            skill.IsBuiltIn = false;
            skill.Description = Loc.GetEnglish(nameof(Loc.BuiltIn_DocumentCreator_Description));
            Assert.Equal(skill.Description, skill.DisplayDescription);
            Assert.DoesNotContain("DisplayDescription", JsonSerializer.Serialize(skill));
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Fact]
    public async Task BuiltInSkillMetadataAndLoadedInstructionsDoNotFollowTheDisplayLanguage()
    {
        var skill = new Skill
        {
            Name = "Code Helper",
            Description = Loc.GetEnglish(nameof(Loc.BuiltIn_CodeHelper_Description)),
            Content = "# Original instructions\nKeep this content unchanged.",
            IsBuiltIn = true
        };
        var agent = new LumiAgent
        {
            Name = "Coding Lumi",
            Description = Loc.GetEnglish(nameof(Loc.BuiltIn_CodingLumi_Description)),
            SystemPrompt = "Keep this original system prompt.",
            IsBuiltIn = true
        };
        var store = new DataStore(new AppData { Skills = [skill], Agents = [agent] });
        string? advertisedDescription = null;
        string? loadedInstructions = null;
        try
        {
            foreach (var language in new[] { "en", "he" })
            {
                Loc.Load(language);
                var catalog = new LumiCapabilityProvider(store).Load();
                var capability = Assert.Single(catalog, item => item.Kind == CapabilityKind.Skill);
                Assert.Equal(skill.Description, capability.Description);
                Assert.Equal(skill.Content, capability.Content);
                Assert.Equal(skill.DisplayDescription, capability.DisplayDescription);
                Assert.False(JsonSerializer.SerializeToElement(capability).TryGetProperty("DisplayDescription", out _));
                var persona = Assert.Single(catalog, item => item.Kind == CapabilityKind.Agent);
                Assert.Equal(agent.Description, persona.Description);
                Assert.Equal(agent.SystemPrompt, persona.Content);
                Assert.Equal(agent.DisplayDescription, persona.DisplayDescription);

                var provider = new LumiSkillProvider(_ => Task.FromResult<IReadOnlyList<Skill>>([skill]));
                var descriptor = Assert.Single(await provider.ListSkillsAsync());
                var markdown = await provider.ReadSkillAsync(descriptor.Name);
                Assert.Equal("code-helper", descriptor.Name);
                Assert.Equal($"{skill.Name}: {skill.Description}", descriptor.Description);
                Assert.Contains(skill.Content, markdown);
                advertisedDescription ??= descriptor.Description;
                loadedInstructions ??= markdown;
                Assert.Equal(advertisedDescription, descriptor.Description);
                Assert.Equal(loadedInstructions, markdown);

                if (language == "he")
                    Assert.NotEqual(capability.Description, capability.DisplayDescription);
            }
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Fact]
    public void WeeklySchedulesLocalizeOnlyTheirDisplayText()
    {
        var job = new BackgroundJob
        {
            ScheduleType = BackgroundJobScheduleTypes.Weekly,
            DaysOfWeek = "Mon,Tue,Wed,Thu,Fri",
            DailyTime = "08:15"
        };
        try
        {
            Loc.Load("en");
            var canonical = BackgroundJobSchedule.Describe(job, localize: false);
            Assert.Equal("Every weekdays at 08:15", canonical);
            Assert.Equal(canonical, BackgroundJobSchedule.Describe(job));

            Loc.Load("he");
            Assert.Equal("בכל יום שני עד שישי בשעה 08:15", BackgroundJobSchedule.Describe(job));
            Assert.Equal(canonical, BackgroundJobSchedule.Describe(job, localize: false));
            Assert.Equal("Mon,Tue,Wed,Thu,Fri", job.DaysOfWeek);
            Assert.Equal("weekly", job.ScheduleType);
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Fact]
    public void JobWakeFallbacksAreLocalizableWithoutChangingParsingOrAuthoredText()
    {
        var missing = RemoteJobWake.Parse(null, "", "משימת רקע", "התקבל אות השכמה.");
        Assert.Equal("משימת רקע", missing.JobName);
        Assert.Equal("התקבל אות השכמה.", missing.WakeSignal);
        const string content = """
            Background job triggered: My job
            Job instructions:
            Keep my instructions
            Trigger context:
            Keep my signal
            Respond as Lumi
            """;
        Assert.True(RemoteJobWake.IsJobWake("Lumi Job - My job", content));
        var parsed = RemoteJobWake.Parse("Lumi Job - My job", content, "משימת רקע", "התקבל אות השכמה.");
        Assert.Equal("My job", parsed.JobName);
        Assert.Equal("Keep my instructions", parsed.Instructions);
        Assert.Equal("Keep my signal", parsed.WakeSignal);
        Assert.Equal("Background job", RemoteJobWake.Parse(null, "").JobName);
    }

    [Fact]
    public async Task SettingsSearchResolvesItsTitlesInTheSelectedLanguage()
    {
        try
        {
            Loc.Load("en");
            var memory = new Memory { Key = "Remembered context", Category = "General", Content = "My own words" };
            var data = new AppData { Memories = [memory] };
            var service = new GlobalSearchService(() => data, chatContent: null);
            Assert.Contains(await service.SearchAsync("theme"),
                result => result.Category == GlobalSearchCategory.Settings && result.Title == "Theme");

            Loc.Load("he");
            var results = await service.SearchAsync("ערכת נושא");
            Assert.Contains(results, result => result.Category == GlobalSearchCategory.Settings
                && result.Title == "ערכת נושא" && result.Subtitle == Loc.Settings_Appearance
                && result.SettingsPageIndex == 3);
            var memoryResult = Assert.Single(await service.SearchAsync("Remembered context"),
                result => result.Category == GlobalSearchCategory.Memories);
            Assert.StartsWith("[כללי]", memoryResult.Subtitle);
            Assert.Equal("General", memory.Category);
            Assert.Equal("Work", Loc.GetMemoryCategoryDisplay("Work"));
            Assert.Contains(await service.SearchAsync("כללי"),
                result => result.Category == GlobalSearchCategory.Memories && result.Item == memory);
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Fact]
    public async Task EmbeddedHostSuppliesHebrewStartupErrorsBeforeJavaScriptRuns()
    {
        try
        {
            Loc.Load("he");
            await using var host = new EmbeddedAppHost(
                new EmbeddedAppLaunch(EmbeddedAppKind.McpApp, "Example <app>", Html: "<html>Example</html>"),
                new JsonObject(), (_, _, _) => Task.FromResult<JsonNode?>(null));
            using var http = new HttpClient();
            var page = await http.GetStringAsync(host.Url);
            Assert.Contains("lang=\"he\"", page);
            Assert.Contains("dir=\"rtl\"", page);
            Assert.Contains("<title>Example &lt;app&gt;</title>", page);
            var labelsAttribute = Regex.Match(page, "data-lumi-labels=\"([^\"]+)\"");
            Assert.True(labelsAttribute.Success);
            using var labels = JsonDocument.Parse(WebUtility.HtmlDecode(labelsAttribute.Groups[1].Value));
            Assert.Equal("היישום לא הופעל.", labels.RootElement.GetProperty("startFailed").GetString());
            Assert.Equal("הבקשה נכשלה ({0})", labels.RootElement.GetProperty("requestFailed").GetString());
        }
        finally
        {
            Loc.Load("en");
        }
    }

    private static string[] Arguments(string value) =>
        Regex.Matches(value, @"(?<!\{)\{(\d+)(?:,[^{}:]+)?(?::[^{}]+)?\}(?!\})")
            .Select(static match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();

    private static Dictionary<string, string> ReadCatalog(string language)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Lumi", "Resources", "Strings", language + ".json")));
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in json.RootElement.EnumerateObject())
        {
            Assert.True(entries.TryAdd(entry.Name, entry.Value.GetString()!), $"Duplicate translation key: {entry.Name}");
        }
        return entries;
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Lumi repository root was not found.");
    }
}
