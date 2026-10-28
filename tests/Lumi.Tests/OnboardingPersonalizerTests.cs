using Lumi.Localization;
using System.Globalization;
using Lumi.Services;
using Lumi.Services.Onboarding;
using Xunit;

namespace Lumi.Tests;

public sealed class OnboardingPersonalizerTests
{
    private static readonly WorkspaceSnapshot Workspace = new(
        [
            new DiscoveredProject("Lumi", @"E:\Git\Lumi", DiscoveredProjectKind.Code, DateTimeOffset.Now,
                "C# · Avalonia", "adirh3/Lumi", "A personal agent for your desktop."),
            new DiscoveredProject("Wedding", @"C:\Users\me\Documents\Wedding", DiscoveredProjectKind.Folder,
                DateTimeOffset.Now.AddDays(-3), DocumentCount: 12),
        ],
        ["VS Code", "Steam", "Krita"],
        ["github.com", "reddit.com"],
        new WorkspaceDocuments(Recent: 14, Pdfs: 4, Office: 6, Downloads: 230));

    public OnboardingPersonalizerTests() => Loc.Load("en");

    [Theory]
    [InlineData("es")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ar")]
    public void PersonalizationKeepsTheSelectedLanguageVariant(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        Assert.Contains($"Write every user-facing string in {culture.EnglishName}.",
            OnboardingPersonalizer.BuildSystemPrompt(culture));
    }

    [Fact]
    public void ParsesTheModelsAnswerAndKeepsOnlyWellFormedPieces()
    {
        const string raw = """
            Here you go:
            ```json
            {
              "observation": "  You've been deep in   Lumi lately, and Krita says you draw too.  ",
              "questions": [
                {"slot": "work", "question": "What do you build?", "options": ["Desktop apps", "Tools, utilities", "Something else", "Design", "An option far too long to be shown on a chip", "Desktop apps"]},
                {"slot": "help", "question": "Where should I help?", "options": ["Code review", "Planning"]},
                {"slot": "interests", "question": "Fun outside work?", "options": ["Gaming", "Digital art", "Anime", "Other"]}
              ],
              "starters": [
                {"title": "Tour Lumi's code.", "prompt": "Walk me through how Lumi is organized.", "project": "lumi", "icon": "🧭"},
                {"title": "Plan the wedding", "prompt": "Help me plan the next wedding tasks.", "project": "Unknown", "icon": "notanemoji"},
                {"title": "", "prompt": "Missing a title."}
              ]
            }
            ```
            """;

        var fallback = OnboardingPersonalizer.Fallback("Adir", Workspace);
        var personalization = OnboardingPersonalizer.Parse(raw, Workspace, fallback);

        Assert.NotNull(personalization);
        Assert.True(personalization.IsPersonalized);
        Assert.Equal("You've been deep in Lumi lately, and Krita says you draw too.", personalization.Observation);

        var work = personalization.Questions[0];
        Assert.Equal(OnboardingSlots.Work, work.Slot);
        Assert.Equal("What do you build?", work.Text);
        Assert.Equal(new[] { "Desktop apps", "Tools utilities", "Design" }, work.Options);

        // Too few usable options: the slot keeps its built-in question.
        Assert.Equal(fallback.Questions[1], personalization.Questions[1]);
        Assert.Equal(new[] { "Gaming", "Digital art", "Anime" }, personalization.Questions[2].Options);

        Assert.Equal(3, personalization.Starters.Count);
        var tour = personalization.Starters[0];
        Assert.Equal("Tour Lumi's code", tour.Title);
        Assert.Equal("Lumi", tour.ProjectName);
        Assert.Equal("🧭", tour.Icon);
        Assert.Null(personalization.Starters[1].ProjectName);
        Assert.Equal("✨", personalization.Starters[1].Icon);
        Assert.Contains(personalization.Starters[2], fallback.Starters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I could not do that.")]
    [InlineData("{}")]
    [InlineData("{\"observation\": 42, \"questions\": \"nope\"}")]
    public void UnusableAnswersFallBack(string? raw)
        => Assert.Null(OnboardingPersonalizer.Parse(raw, Workspace, OnboardingPersonalizer.Fallback("Adir", Workspace)));

    [Fact]
    public void TheFallbackIsPersonalFromLocalDiscoveryAlone()
    {
        var fallback = OnboardingPersonalizer.Fallback("Adir", Workspace);

        Assert.False(fallback.IsPersonalized);
        Assert.Contains("Lumi", fallback.Observation);
        Assert.Contains("Wedding", fallback.Observation);
        Assert.Equal(OnboardingSlots.All, fallback.Questions.Select(question => question.Slot));
        Assert.All(fallback.Questions, question => Assert.True(question.Options.Count >= 5));
        Assert.Contains(fallback.Starters, starter => starter.ProjectName == "Lumi");
        Assert.Contains(fallback.Starters, starter => starter.ProjectName == "Wedding");
        Assert.Contains(fallback.Starters, starter => starter.Title == Loc.Chat_SuggestionA);

        var empty = OnboardingPersonalizer.Fallback("Adir", WorkspaceSnapshot.Empty);
        Assert.Contains("Adir", empty.Observation);
        Assert.All(empty.Starters, starter => Assert.Null(starter.ProjectName));
    }

    [Fact]
    public void StartersOnlyOpenInProjectsThatWereSetUp()
    {
        OnboardingStarterIdea[] preferred =
        [
            new("Tour Lumi", "Walk me through Lumi.", "Lumi", "🧭"),
            new("Plan the wedding", "Plan the wedding.", "Wedding", "💍"),
            new("Plan my week", "Plan my week.", null, "🗓️"),
        ];

        var starters = OnboardingPersonalizer.SelectStarters(preferred, Workspace, ["Lumi"]);

        Assert.Equal(3, starters.Count);
        Assert.Equal("Tour Lumi", starters[0].Title);
        Assert.Equal("Plan my week", starters[1].Title);
        Assert.DoesNotContain(starters, starter => starter.ProjectName == "Wedding");
        Assert.Equal(starters.Count, starters.Select(starter => starter.Title).Distinct().Count());
    }

    [Fact]
    public void EachProjectGetsOneStarterBeforeAnyGetsASecond()
    {
        OnboardingStarterIdea[] preferred =
        [
            new("Review Lumi", "Review Lumi's latest changes.", "Lumi", "🔎"),
            new("Fix Lumi's tests", "Fix Lumi's failing tests.", "Lumi", "🧪"),
            new("Plan the wedding", "Plan the wedding.", "Wedding", "💍"),
        ];

        var starters = OnboardingPersonalizer.SelectStarters(preferred, Workspace, ["Lumi", "Wedding"]);

        Assert.Equal(new[] { "Review Lumi", "Plan the wedding", Loc.Chat_SuggestionA }, starters.Select(starter => starter.Title));
    }

    [Theory]
    [InlineData(OnboardingSlots.Work, "Software development, Design")]
    [InlineData(OnboardingSlots.Work, "I run a small bakery")]
    [InlineData(OnboardingSlots.Help, "Coding & debugging, Writing & email, Research")]
    [InlineData(OnboardingSlots.Help, "Planning my day")]
    [InlineData(OnboardingSlots.Interests, "Gaming, Anime, Sports & fitness")]
    public void EveryAnswerBecomesAMemoryThatLumiKeeps(string slot, string answer)
    {
        var memory = OnboardingPersonalizer.BuildDurableMemory(slot, "Adir", answer);

        Assert.NotNull(memory);
        Assert.Contains("Adir", memory.Content);
        Assert.True(MemoryAgentService.EvaluateMemoryCandidate(memory.Key, memory.Content, memory.Category).ShouldSave,
            $"'{memory.Key}: {memory.Content}' would be archived by memory maintenance");
    }

    // Expectations go through the active templates: another test may have loaded a different language.
    [Fact]
    public void AnswersReadNaturallyInsideAMemory()
    {
        Assert.Equal(string.Format(Loc.Onboarding_MemoryWork, "Adir", "software development" + Loc.Onboarding_ListAnd + "product design"),
            OnboardingPersonalizer.BuildDurableMemory(OnboardingSlots.Work, "Adir", "Software development, Product design")?.Content);
        Assert.Equal(string.Format(Loc.Onboarding_MemoryHelp, "Adir", "code review, project planning" + Loc.Onboarding_ListAnd + "research"),
            OnboardingPersonalizer.BuildDurableMemory(OnboardingSlots.Help, "Adir", "Code review, Project planning, Research")?.Content);
        Assert.Equal(string.Format(Loc.Onboarding_MemoryInterests, "Adir", "PC gaming, YouTube" + Loc.Onboarding_ListAnd + "Home Assistant"),
            OnboardingPersonalizer.BuildDurableMemory(OnboardingSlots.Interests, "Adir", "PC gaming, YouTube, Home Assistant")?.Content);
    }

    [Fact]
    public void PicksThatAreNotLastingFactsAreLeftOutOfTheMemory()
    {
        var memory = OnboardingPersonalizer.BuildDurableMemory(OnboardingSlots.Interests, "Adir", "Gaming, debugging my repo today, Other");

        Assert.NotNull(memory);
        Assert.Equal(string.Format(Loc.Onboarding_MemoryInterests, "Adir", "gaming"), memory.Content);
        Assert.Null(OnboardingPersonalizer.BuildDurableMemory(OnboardingSlots.Interests, "Adir", "Something else"));
    }

    [Fact]
    public void OptionsLumiCouldNotRememberAreNeverOffered()
    {
        const string raw = """
            {"questions": [
              {"slot": "work", "question": "What do you do?", "options": ["Software development", "Design", "Research"]},
              {"slot": "help", "question": "Where should I help?", "options": ["Code review", "Fix bug reports", "Fix build errors", "Trip planning", "Writing"]},
              {"slot": "interests", "question": "For fun?", "options": ["Gaming", "Theme parks", "Music", "Travel"]}
            ]}
            """;

        var personalization = OnboardingPersonalizer.Parse(raw, Workspace, OnboardingPersonalizer.Fallback("Adir", Workspace));

        Assert.NotNull(personalization);
        Assert.Equal(new[] { "Code review", "Trip planning", "Writing" }, personalization.Questions[1].Options);
        Assert.Equal(new[] { "Gaming", "Music", "Travel" }, personalization.Questions[2].Options);
    }

    [Fact]
    public void AnswersThatReadAsAnotherFactAreLeftOut()
    {
        // Lumi keeps one memory per topic, so these would replace the preferred theme or programming language.
        Assert.Equal(string.Format(Loc.Onboarding_MemoryInterests, "Adir", "gaming"),
            OnboardingPersonalizer.BuildDurableMemory(OnboardingSlots.Interests, "Adir", "Gaming, Theme parks")?.Content);
        Assert.Null(OnboardingPersonalizer.BuildDurableMemory(OnboardingSlots.Help, "Adir", "Picking a programming language"));

        // Saying what your job is stays a line-of-work answer.
        Assert.NotNull(OnboardingPersonalizer.BuildDurableMemory(OnboardingSlots.Work, "Adir", "I work as a nurse"));
    }

    [Fact]
    public void EveryBuiltInOptionCanBeRemembered()
    {
        foreach (var question in OnboardingPersonalizer.DefaultQuestions())
        {
            foreach (var option in question.Options)
                Assert.True(OnboardingPersonalizer.BuildDurableMemory(question.Slot, "Adir", option) is not null, $"{question.Slot}: {option}");
        }
    }

    [Fact]
    public void ThePromptSummarizesTheWorkspaceWithoutPaths()
    {
        var prompt = OnboardingPersonalizer.BuildPrompt("Adir", Workspace);

        Assert.Contains("Lumi (C# · Avalonia)", prompt);
        Assert.Contains("A personal agent for your desktop.", prompt);
        Assert.Contains("Wedding (12 recent documents)", prompt);
        Assert.Contains("VS Code, Steam, Krita", prompt);
        Assert.Contains("github.com, reddit.com", prompt);
        Assert.DoesNotContain(@"E:\Git", prompt);
        Assert.DoesNotContain("Documents\\Wedding", prompt);

        Assert.Contains("chose not to share", OnboardingPersonalizer.BuildPrompt("Adir", WorkspaceSnapshot.Empty));
    }
}
