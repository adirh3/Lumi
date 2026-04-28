using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class FileSearchServiceTests
{
    // ── GlobMatch ───────────────────────────────────────────────────

    [Theory]
    [InlineData("hello.txt", "*.txt", true)]
    [InlineData("hello.txt", "*.cs", false)]
    [InlineData("hello.txt", "hello.*", true)]
    [InlineData("hello.txt", "hello.txt", true)]
    [InlineData("hello.txt", "HELLO.TXT", true)] // case-insensitive
    [InlineData("hello.txt", "h?llo.txt", true)]
    [InlineData("hello.txt", "h??lo.txt", true)]
    [InlineData("hello.txt", "h???o.txt", true)] // ? matches exactly one char: e, l, l
    [InlineData("a", "*", true)]
    [InlineData("", "*", true)]
    [InlineData("abc", "a*c", true)]
    [InlineData("abc", "a*d", false)]
    [InlineData("abcdef", "a*c*f", true)]
    [InlineData("abcdef", "a*c*g", false)]
    [InlineData("src/foo/bar.cs", "src/*/bar.cs", true)]
    [InlineData("src/foo/bar.cs", "src/*/*.cs", true)]
    public void GlobMatch_BasicPatterns(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, FileSearchService.GlobMatch(text, pattern));
    }

    [Theory]
    [InlineData("bin", "[Bb]in", true)]
    [InlineData("Bin", "[Bb]in", true)]
    [InlineData("xin", "[Bb]in", false)]
    [InlineData("obj", "[Oo]bj", true)]
    [InlineData("Obj", "[Oo]bj", true)]
    [InlineData("Debug", "[Dd]ebug", true)]
    [InlineData("debug", "[Dd]ebug", true)]
    [InlineData("Release", "[Rr]elease", true)]
    [InlineData("release", "[Rr]elease", true)]
    public void GlobMatch_CharacterClasses(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, FileSearchService.GlobMatch(text, pattern));
    }

    [Theory]
    [InlineData("a", "[a-z]", true)]
    [InlineData("m", "[a-z]", true)]
    [InlineData("z", "[a-z]", true)]
    [InlineData("A", "[a-z]", true)] // case-insensitive
    [InlineData("5", "[a-z]", false)]
    [InlineData("5", "[0-9]", true)]
    [InlineData("a", "[0-9]", false)]
    public void GlobMatch_CharacterRanges(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, FileSearchService.GlobMatch(text, pattern));
    }

    [Theory]
    [InlineData("a", "[!b]", true)]
    [InlineData("b", "[!b]", false)]
    [InlineData("a", "[^b]", true)]
    [InlineData("b", "[^b]", false)]
    [InlineData("c", "[!a-b]", true)]
    [InlineData("a", "[!a-b]", false)]
    public void GlobMatch_NegatedCharacterClasses(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, FileSearchService.GlobMatch(text, pattern));
    }

    [Theory]
    [InlineData("bin/Debug/net11.0/app.dll", "[Bb]in/[Dd]ebug/*", true)]
    [InlineData("Bin/Release/net11.0/app.dll", "[Bb]in/[Rr]elease/*", true)]
    [InlineData("obj/Debug/net11.0/app.dll", "[Oo]bj/*", true)]
    public void GlobMatch_CharacterClassesInPaths(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, FileSearchService.GlobMatch(text, pattern));
    }

    // ── ScoreMatch ──────────────────────────────────────────────────

    [Fact]
    public void ScoreMatch_ExactFileName_ScoresHighest()
    {
        var parts = new[] { "FluentSearch.cs" };
        var exactScore = FileSearchService.ScoreMatch("src/FluentSearch.cs", parts);
        var containsScore = FileSearchService.ScoreMatch("src/FluentSearchHelper.cs", parts);

        Assert.True(exactScore > containsScore, $"Exact ({exactScore}) should beat contains ({containsScore})");
    }

    [Fact]
    public void ScoreMatch_FileNameWithoutExtension_ScoresHigh()
    {
        var parts = new[] { "FluentSearch" };
        var noExtScore = FileSearchService.ScoreMatch("src/FluentSearch.cs", parts);
        var prefixScore = FileSearchService.ScoreMatch("src/FluentSearchHelper.cs", parts);

        Assert.True(noExtScore > prefixScore, $"Exact name ({noExtScore}) should beat prefix ({prefixScore})");
    }

    [Fact]
    public void ScoreMatch_FileNamePrefix_BeatsContains()
    {
        var parts = new[] { "Chat" };
        var prefixScore = FileSearchService.ScoreMatch("ViewModels/ChatViewModel.cs", parts);
        var containsScore = FileSearchService.ScoreMatch("ViewModels/MyChatHelper.cs", parts);

        Assert.True(prefixScore > containsScore, $"Prefix ({prefixScore}) should beat contains ({containsScore})");
    }

    [Fact]
    public void ScoreMatch_FileNameContains_BeatsPathOnly()
    {
        var parts = new[] { "search" };
        var fileNameScore = FileSearchService.ScoreMatch("src/FileSearch.cs", parts);
        var pathOnlyScore = FileSearchService.ScoreMatch("search/utils/helper.cs", parts);

        Assert.True(fileNameScore > pathOnlyScore, $"Filename match ({fileNameScore}) should beat path-only ({pathOnlyScore})");
    }

    [Fact]
    public void ScoreMatch_ShallowerPath_BreaksTies()
    {
        var parts = new[] { "app.cs" };
        var shallowScore = FileSearchService.ScoreMatch("src/app.cs", parts);
        var deepScore = FileSearchService.ScoreMatch("src/deep/nested/dir/app.cs", parts);

        Assert.True(shallowScore > deepScore, $"Shallow ({shallowScore}) should beat deep ({deepScore})");
    }

    [Fact]
    public void ScoreMatch_NoMatch_ReturnsZero()
    {
        var parts = new[] { "nonexistent" };
        Assert.Equal(0, FileSearchService.ScoreMatch("src/something.cs", parts));
    }

    [Fact]
    public void ScoreMatch_MultiTerm_AllMustMatch()
    {
        var parts = new[] { "chat", "view" };
        Assert.True(FileSearchService.ScoreMatch("ViewModels/ChatView.axaml", parts) > 0);
        Assert.Equal(0, FileSearchService.ScoreMatch("ViewModels/Settings.axaml", parts));
    }

    [Fact]
    public void ScoreMatch_MultiTerm_AllInFileName_GetsBonus()
    {
        var parts = new[] { "Chat", "View" };
        var allInFile = FileSearchService.ScoreMatch("src/ChatView.axaml", parts);
        var splitAcross = FileSearchService.ScoreMatch("ViewModels/ChatHelper.cs", parts);

        Assert.True(allInFile > splitAcross, $"All-in-filename ({allInFile}) should beat split ({splitAcross})");
    }

    [Fact]
    public void ScoreMatch_CoverageBonus_ShorterFilenameWins()
    {
        // "fluentsearch" covers 80% of "FluentSearch.cs" but only 34% of "FluentSearchBenchmarksConfig.cs"
        var parts = new[] { "fluentsearch" };
        var shortScore = FileSearchService.ScoreMatch("src/FluentSearch.cs", parts);
        var longScore = FileSearchService.ScoreMatch("src/FluentSearchBenchmarksConfig.cs", parts);

        Assert.True(shortScore > longScore,
            $"Short filename ({shortScore}) should beat long filename ({longScore}) due to coverage");
    }

    [Fact]
    public void ScoreMatch_SourceFileBonus()
    {
        var parts = new[] { "fluentsearch" };
        var csScore = FileSearchService.ScoreMatch("src/FluentSearchLanguage.cs", parts);
        var mdScore = FileSearchService.ScoreMatch("src/FluentSearchReadme.md", parts);

        Assert.True(csScore > mdScore, $"Source file ({csScore}) should beat non-source ({mdScore})");
    }

    [Fact]
    public void ScoreMatch_BlastProject_FluentSearchRanking()
    {
        // Simulate real Blast project file structure — the user's actual scenario
        var query = new[] { "fluentsearch" };

        var scores = new (string path, int score)[]
        {
            ("Blast.FileSearchApp.WindowsSearcher/FluentSearch.cs", 0),
            ("Blast.Core/FluentSearchLanguage.cs", 0),
            ("Blast.Benchmarks/FluentSearchBenchmarksConfig.cs", 0),
            ("Blast.Accounts/Microsoft/FluentSearchTokenProvider.cs", 0),
            ("Blast.IndexerService/FluentSearchPipeJsonSerializerContext.cs", 0),
            ("Blast.Logics/FluentSearchAppearanceUtils.cs", 0),
            ("Blast.Logics/IFluentSearchApp.cs", 0),
            ("Blast.Logics/Settings/MainSettingPages/FluentSearchThemeManager.cs", 0),
            ("Blast.Tests/FluentSearchIndexerDeterminismTests.cs", 0),
            ("Blast.UI.Core/SFX/FluentSearchSFX.cs", 0),
            ("Blast.UI.Core/Themes/FluentSearchThemeDescriptor.cs", 0),
            ("Blast.UI.Core/Themes/IFluentSearchTheme.cs", 0),
            (".codex/skills/fluentsearch-azure-error-fixer/SKILL.md", 0),
            (".codex/skills/fluentsearch-csharp-plugin-publisher/SKILL.md", 0),
            (".github/Skills/fluentsearch-translate/SKILL.md", 0),
        };

        for (var i = 0; i < scores.Length; i++)
            scores[i].score = FileSearchService.ScoreMatch(scores[i].path, query);

        // Sort by score descending
        Array.Sort(scores, (a, b) =>
        {
            var cmp = b.score.CompareTo(a.score);
            return cmp != 0 ? cmp : a.path.Length.CompareTo(b.path.Length);
        });

        // FluentSearch.cs MUST be first — it's the exact name match
        Assert.Contains("FluentSearch.cs", scores[0].path);
        Assert.True(scores[0].path.EndsWith("FluentSearch.cs"),
            $"First result should be FluentSearch.cs, got: {scores[0].path}");

        // FluentSearch.cs must score strictly higher than all FluentSearch*.cs prefix matches
        var topScore = scores[0].score;
        for (var i = 1; i < scores.Length; i++)
        {
            Assert.True(scores[i].score < topScore,
                $"FluentSearch.cs ({topScore}) must beat {scores[i].path} ({scores[i].score})");
        }

        // Source files (.cs) must rank above SKILL.md files (path-only matches)
        var lastCsIndex = -1;
        var firstMdIndex = int.MaxValue;
        for (var i = 0; i < scores.Length; i++)
        {
            if (scores[i].path.EndsWith(".cs")) lastCsIndex = i;
            if (scores[i].path.EndsWith(".md") && i < firstMdIndex) firstMdIndex = i;
        }
        Assert.True(lastCsIndex < firstMdIndex,
            $"All .cs files (last at idx {lastCsIndex}) should rank above .md files (first at idx {firstMdIndex})");

        // Shorter filenames (higher coverage) should rank above longer ones in the same tier
        var langScore = FileSearchService.ScoreMatch("Blast.Core/FluentSearchLanguage.cs", query);
        var benchScore = FileSearchService.ScoreMatch("Blast.Benchmarks/FluentSearchBenchmarksConfig.cs", query);
        Assert.True(langScore > benchScore,
            $"FluentSearchLanguage.cs ({langScore}) should beat FluentSearchBenchmarksConfig.cs ({benchScore})");
    }

    [Fact]
    public void ScoreMatch_PathSegmentExact_BeatsLoosePathContains()
    {
        var parts = new[] { "viewmodels" };
        var exactSegmentScore = FileSearchService.ScoreMatch("src/ViewModels/ChatViewModel.cs", parts);
        var loosePathScore = FileSearchService.ScoreMatch("src/MyViewModelsHelpers/ChatViewModel.cs", parts);

        Assert.True(exactSegmentScore > loosePathScore,
            $"Exact path segment ({exactSegmentScore}) should beat loose path contains ({loosePathScore})");
    }

    [Fact]
    public void ScoreMatch_TypoStillRanksBestFilenameHighest()
    {
        var parts = new[] { "fluentserch" };
        var bestScore = FileSearchService.ScoreMatch("src/FluentSearch.cs", parts);
        var weakerScore = FileSearchService.ScoreMatch("src/FluentSearchBenchmarksConfig.cs", parts);

        Assert.True(bestScore > weakerScore,
            $"Best typo candidate ({bestScore}) should beat longer weaker filename ({weakerScore})");
    }

    [Fact]
    public void ScoreMatch_TransposedTypoStillMatchesFileName()
    {
        var score = FileSearchService.ScoreMatch("src/SearchService.cs", new[] { "serach" });

        Assert.True(score > 0, $"Transposed typo should match SearchService.cs, got score {score}");
    }

    [Fact]
    public void ScoreMatch_TypoInsideLongFileNameStillMatches()
    {
        var score = FileSearchService.ScoreMatch("src/FileSearchService.cs", new[] { "fileserach" });
        var distractorScore = FileSearchService.ScoreMatch("src/FileStorageService.cs", new[] { "fileserach" });

        Assert.True(score > 0, $"Typo inside long filename should match FileSearchService.cs, got score {score}");
        Assert.True(score > distractorScore,
            $"FileSearchService.cs ({score}) should beat FileStorageService.cs ({distractorScore})");
    }

    [Fact]
    public void ScoreMatch_TypoInsideCamelCaseSegmentStillMatches()
    {
        var score = FileSearchService.ScoreMatch("src/ViewModels/ChatViewModel.cs", new[] { "viewmodle" });

        Assert.True(score > 0, $"Typo inside CamelCase segment should match ChatViewModel.cs, got score {score}");
    }

    [Fact]
    public void ScoreMatch_AcronymInitialsBeatLooseSubsequence()
    {
        var parts = new[] { "law" };
        var acronymScore = FileSearchService.ScoreMatch("src/LogAnalyticsWorkspace.cs", parts);
        var looseScore = FileSearchService.ScoreMatch("src/LaunchWindow.cs", parts);

        Assert.True(acronymScore > 0, $"Acronym query should match LogAnalyticsWorkspace.cs, got score {acronymScore}");
        Assert.True(acronymScore > looseScore,
            $"Initials match ({acronymScore}) should beat loose subsequence ({looseScore})");
    }

    [Fact]
    public void ScoreMatch_ExactShortNameBeatsAcronymExpansion()
    {
        var parts = new[] { "law" };
        var exactScore = FileSearchService.ScoreMatch("src/Law.cs", parts);
        var acronymScore = FileSearchService.ScoreMatch("src/LogAnalyticsWorkspace.cs", parts);

        Assert.True(exactScore > acronymScore,
            $"Exact short name ({exactScore}) should beat acronym expansion ({acronymScore})");
    }

    [Fact]
    public void ScoreMatch_SeparatorInitialsMatchAcronymQuery()
    {
        var score = FileSearchService.ScoreMatch("src/Log-Analytics_Workspace.cs", new[] { "law" });

        Assert.True(score > 0, $"Acronym query should match separator-delimited filename, got score {score}");
    }

    [Fact]
    public void ScoreMatch_DiacriticsAreIgnored()
    {
        var score = FileSearchService.ScoreMatch("src/R\u00e9sum\u00e9Parser.cs", new[] { "resume" });

        Assert.True(score > 0, $"Diacritic-insensitive query should match R\u00e9sum\u00e9Parser.cs, got score {score}");
    }

    [Fact]
    public void ScoreMatch_PathSeparatorQueryMatchesNestedPath()
    {
        var score = FileSearchService.ScoreMatch("src/Services/ChatViewModel.cs", new[] { "services/chat" });

        Assert.True(score > 0, $"Path separator query should match nested file path, got score {score}");
    }

    [Fact]
    public void Search_AcronymQuery_RanksCamelCaseFileFirst()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "ViewModels"));
            File.WriteAllText(Path.Combine(tempDir, "src", "ViewModels", "ChatViewModel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "ViewModels", "ConversationViewModel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "ViewModels", "ChatVirtualMachine.cs"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "cvm");

            Assert.Equal(3, results.Count);
            Assert.Equal("ChatViewModel.cs", Path.GetFileName(results[0].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_TypoInsideLongFileNameRanksExpectedFirst()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "Services"));
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "ViewModels"));
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "FileSearchService.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "FileStorageService.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "ViewModels", "SearchSettingsView.axaml"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "fileserach");

            Assert.NotEmpty(results);
            Assert.Equal("FileSearchService.cs", Path.GetFileName(results[0].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_AcronymInitialsRanksLogAnalyticsWorkspaceFirst()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "Services"));
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "LogAnalyticsWorkspace.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "LaunchWindow.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "LegalAdvice.cs"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "law");

            Assert.NotEmpty(results);
            Assert.Equal("LogAnalyticsWorkspace.cs", Path.GetFileName(results[0].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_ExactShortNameRanksBeforeAcronymExpansion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "Services"));
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "LogAnalyticsWorkspace.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "Law.cs"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "law");

            Assert.NotEmpty(results);
            Assert.Equal("Law.cs", Path.GetFileName(results[0].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_SeparatorInitialsRankExpectedFirst()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "Services"));
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "Log-Analytics_Workspace.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "LaunchWindow.cs"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "law");

            Assert.NotEmpty(results);
            Assert.Equal("Log-Analytics_Workspace.cs", Path.GetFileName(results[0].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_MultiTermPrefixAndTypoRanksExpectedFirst()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "Services"));
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "LogAnalyticsWorkspace.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "LogAnalyticsWriter.cs"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "log workspce");

            Assert.NotEmpty(results);
            Assert.Equal("LogAnalyticsWorkspace.cs", Path.GetFileName(results[0].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_PathAcronymInitialsCanMatchNestedPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "Log", "Analytics"));
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "Launch"));
            File.WriteAllText(Path.Combine(tempDir, "src", "Log", "Analytics", "Workspace.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Launch", "Window.cs"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "law");

            Assert.NotEmpty(results);
            Assert.Equal(
                Path.Combine("src", "Log", "Analytics", "Workspace.cs"),
                results[0].RelativePath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── IsHardcodedIgnoredPath ──────────────────────────────────────

    [Theory]
    [InlineData(".git/config", true)]
    [InlineData("src/.git/config", true)]
    [InlineData("node_modules/package/index.js", true)]
    [InlineData("bin/Debug/app.dll", true)]
    [InlineData("obj/Debug/app.dll", true)]
    [InlineData(".vs/settings.json", true)]
    [InlineData("__pycache__/module.pyc", true)]
    [InlineData(".next/build/page.js", true)]
    [InlineData("dist/bundle.js", true)]
    [InlineData(".nuget/packages.config", true)]
    [InlineData("packages/SomePackage/lib/net6.0/Some.dll", true)]
    [InlineData("src/Services/MyService.cs", false)]
    [InlineData("src/binary-parser/parser.cs", false)] // "binary-parser" ≠ "bin"
    [InlineData("objective-c/main.m", false)] // "objective-c" ≠ "obj"
    public void IsHardcodedIgnoredPath_Works(string path, bool expected)
    {
        Assert.Equal(expected, FileSearchService.IsHardcodedIgnoredPath(path));
    }

    // ── ParseGitIgnoreLines ─────────────────────────────────────────

    [Fact]
    public void ParseGitIgnoreLines_BasicParsing()
    {
        var lines = new[]
        {
            "# comment",
            "",
            "*.log",
            "[Bb]in/",
            "!important.log",
            "/build/",
            "  ",
            "dist"
        };

        var rules = FileSearchService.ParseGitIgnoreLines(lines);

        Assert.Equal(5, rules.Count);

        // *.log — not negation, not dir-only
        Assert.Equal("*.log", rules[0].Pattern);
        Assert.False(rules[0].IsNegation);
        Assert.False(rules[0].IsDirectoryOnly);

        // [Bb]in/ — dir-only, slash stripped
        Assert.Equal("[Bb]in", rules[1].Pattern);
        Assert.False(rules[1].IsNegation);
        Assert.True(rules[1].IsDirectoryOnly);

        // !important.log — negation
        Assert.Equal("important.log", rules[2].Pattern);
        Assert.True(rules[2].IsNegation);
        Assert.False(rules[2].IsDirectoryOnly);

        // /build/ — dir-only, leading slash stripped
        Assert.Equal("build", rules[3].Pattern);
        Assert.False(rules[3].IsNegation);
        Assert.True(rules[3].IsDirectoryOnly);

        // dist — plain
        Assert.Equal("dist", rules[4].Pattern);
        Assert.False(rules[4].IsNegation);
        Assert.False(rules[4].IsDirectoryOnly);
    }

    // ── GitIgnoreMatches ────────────────────────────────────────────

    [Theory]
    [InlineData("app.log", "*.log", false, false, true)]
    [InlineData("src/app.log", "*.log", false, false, true)]
    [InlineData("app.cs", "*.log", false, false, false)]
    [InlineData("bin/Debug/app.dll", "[Bb]in", false, true, true)]  // dir-only matches dir segment
    [InlineData("Bin/Debug/app.dll", "[Bb]in", false, true, true)]
    [InlineData("src/bin/Debug/app.dll", "[Bb]in", false, true, true)]
    [InlineData("binfile.txt", "[Bb]in", false, true, false)]  // dir-only doesn't match filename
    [InlineData("obj/Debug/app.dll", "[Oo]bj", false, true, true)]
    [InlineData("debug/app.dll", "[Dd]ebug", false, true, true)]
    [InlineData("Debug/app.dll", "[Dd]ebug", false, true, true)]
    public void GitIgnoreMatches_Works(string path, string pattern, bool isNegation, bool isDirOnly, bool expected)
    {
        var rule = new GitIgnoreRule(pattern, isNegation, isDirOnly);
        Assert.Equal(expected, FileSearchService.GitIgnoreMatches(path, rule));
    }

    // ── IsIgnoredPath (integration) ─────────────────────────────────

    [Fact]
    public void IsIgnoredPath_GitIgnoreRules_Override()
    {
        var rules = FileSearchService.ParseGitIgnoreLines(new[]
        {
            "*.log",
            "!important.log"
        });

        // *.log matches → ignored, then !important.log matches → un-ignored
        Assert.True(FileSearchService.IsIgnoredPath("debug.log", rules));
        Assert.False(FileSearchService.IsIgnoredPath("important.log", rules));
        Assert.False(FileSearchService.IsIgnoredPath("src/app.cs", rules));
    }

    [Fact]
    public void IsIgnoredPath_CSharpGitIgnore_IgnoresBinObj()
    {
        // Standard Visual Studio .gitignore patterns
        var rules = FileSearchService.ParseGitIgnoreLines(new[]
        {
            "[Dd]ebug/",
            "[Rr]elease/",
            "[Bb]in/",
            "[Oo]bj/",
            "*.user",
            "*.suo",
        });

        // These should be ignored via gitignore rules (AND hardcoded)
        Assert.True(FileSearchService.IsIgnoredPath("bin/Debug/net11.0/App.dll", rules));
        Assert.True(FileSearchService.IsIgnoredPath("Bin/Release/App.dll", rules));
        Assert.True(FileSearchService.IsIgnoredPath("obj/Debug/net11.0/App.dll", rules));
        Assert.True(FileSearchService.IsIgnoredPath("src/MyProject/bin/Debug/App.dll", rules));

        // Source files should NOT be ignored
        Assert.False(FileSearchService.IsIgnoredPath("src/Services/FileSearchService.cs", rules));
        Assert.False(FileSearchService.IsIgnoredPath("Blast.FileSearchApp.WindowsSearcher/FluentSearch.cs", rules));
    }

    [Fact]
    public void IsIgnoredPath_UserBugReport_FluentSearchNotIgnored()
    {
        // Simulate the Blast project's typical .gitignore
        var rules = FileSearchService.ParseGitIgnoreLines(new[]
        {
            "[Dd]ebug/",
            "[Rr]elease/",
            "x64/",
            "x86/",
            "[Bb]in/",
            "[Oo]bj/",
            "*.user",
            "*.suo",
            "*.log",
            ".vs/",
            "packages/",
            "TestResults/",
        });

        // The file that wasn't being found -- must NOT be ignored
        Assert.False(FileSearchService.IsIgnoredPath(
            "Blast.FileSearchApp.WindowsSearcher/FluentSearch.cs", rules));

        // And it must match the query (score > 0)
        Assert.True(FileSearchService.ScoreMatch(
            "Blast.FileSearchApp.WindowsSearcher/FluentSearch.cs", new[] { "fluentsearch.cs" }) > 0);

        // Build output should be ignored
        Assert.True(FileSearchService.IsIgnoredPath(
            "Blast.FileSearchApp.WindowsSearcher/bin/Debug/net8.0/FluentSearch.dll", rules));
    }

    // ── End-to-end search with temp directory ───────────────────────

    [Fact]
    public void Search_FindsFiles_InTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            // Create test structure
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "Services"));
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "ViewModels"));
            Directory.CreateDirectory(Path.Combine(tempDir, "bin", "Debug"));
            Directory.CreateDirectory(Path.Combine(tempDir, ".git", "objects"));

            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "FileSearch.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "DataStore.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "ViewModels", "ChatViewModel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "bin", "Debug", "app.dll"), "");
            File.WriteAllText(Path.Combine(tempDir, ".git", "objects", "pack"), "");

            var service = new FileSearchService();

            // Search for "FileSearch" — should find FileSearch.cs, not bin/Debug/app.dll
            var results = service.Search(tempDir, "FileSearch");
            Assert.Single(results);
            Assert.Contains(results, r => r.RelativePath.Contains("FileSearch.cs"));

            // Search for "cs" — should find all .cs files, not bin or .git
            var csResults = service.Search(tempDir, "cs");
            Assert.Equal(3, csResults.Count);
            Assert.DoesNotContain(csResults, r => r.RelativePath.Contains("bin"));
            Assert.DoesNotContain(csResults, r => r.RelativePath.Contains(".git"));

            // Empty query returns all non-ignored files
            var allResults = service.Search(tempDir, "");
            Assert.Equal(3, allResults.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_RespectsGitIgnore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            Directory.CreateDirectory(Path.Combine(tempDir, "logs"));
            Directory.CreateDirectory(Path.Combine(tempDir, "output"));

            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "*.log\noutput/\n");
            File.WriteAllText(Path.Combine(tempDir, "src", "app.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "logs", "debug.log"), "");
            File.WriteAllText(Path.Combine(tempDir, "logs", "info.txt"), "");
            File.WriteAllText(Path.Combine(tempDir, "output", "result.txt"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "");

            // Should find app.cs, info.txt, and .gitignore, but NOT debug.log (*.log) or output/result.txt (output/)
            Assert.Equal(3, results.Count);
            Assert.Contains(results, r => r.RelativePath.Contains("app.cs"));
            Assert.Contains(results, r => r.RelativePath.Contains("info.txt"));
            Assert.Contains(results, r => r.RelativePath.Contains(".gitignore"));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_SkipsIgnoredDirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            // Create a structure where bin/ has many files — these should never be visited
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            Directory.CreateDirectory(Path.Combine(tempDir, "bin", "Debug", "net11.0"));
            Directory.CreateDirectory(Path.Combine(tempDir, "node_modules", "some-pkg"));

            File.WriteAllText(Path.Combine(tempDir, "src", "app.cs"), "");
            for (var i = 0; i < 50; i++)
            {
                File.WriteAllText(Path.Combine(tempDir, "bin", "Debug", "net11.0", $"file{i}.dll"), "");
                File.WriteAllText(Path.Combine(tempDir, "node_modules", "some-pkg", $"index{i}.js"), "");
            }

            var service = new FileSearchService();
            var results = service.Search(tempDir, "");

            // Only src/app.cs should be found — bin and node_modules are skipped entirely
            Assert.Single(results);
            Assert.Equal("app.cs", Path.GetFileName(results[0].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_ResultsAreRanked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "deep", "nest"));
            Directory.CreateDirectory(Path.Combine(tempDir, "lib"));

            // Create files with varying relevance to query "app"
            File.WriteAllText(Path.Combine(tempDir, "src", "deep", "nest", "app.cs"), ""); // exact, deep
            File.WriteAllText(Path.Combine(tempDir, "lib", "app.cs"), "");                 // exact, shallow
            File.WriteAllText(Path.Combine(tempDir, "src", "appHelper.cs"), "");            // prefix
            File.WriteAllText(Path.Combine(tempDir, "src", "myapp.cs"), "");                // contains

            var service = new FileSearchService();
            var results = service.Search(tempDir, "app");

            Assert.Equal(4, results.Count);

            // First result should be the shallowest exact filename match
            Assert.Equal("app.cs", Path.GetFileName(results[0].RelativePath));
            Assert.Contains("lib", results[0].RelativePath); // lib/app.cs is shallower than src/deep/nest/app.cs
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_EqualScoresUseAlphabeticalTieBreak()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));

            File.WriteAllText(Path.Combine(tempDir, "src", "CApp.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "BApp.cs"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "app");

            Assert.Equal(2, results.Count);
            Assert.Equal("BApp.cs", Path.GetFileName(results[0].RelativePath));
            Assert.Equal("CApp.cs", Path.GetFileName(results[1].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_FluentSearch_RankedFirst()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            // Simulate the Blast project structure
            Directory.CreateDirectory(Path.Combine(tempDir, "Blast.FileSearchApp.WindowsSearcher"));
            Directory.CreateDirectory(Path.Combine(tempDir, "Blast.Core", "Search"));

            File.WriteAllText(Path.Combine(tempDir, "Blast.FileSearchApp.WindowsSearcher", "FluentSearch.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "Blast.Core", "Search", "FluentSearchConfig.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "Blast.Core", "Search", "IFluentSearchProvider.cs"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "fluentsearch.cs");

            // FluentSearch.cs (exact filename) should be ranked first
            Assert.True(results.Count >= 1);
            Assert.Equal("FluentSearch.cs", Path.GetFileName(results[0].RelativePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_RespectsMaxResults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            for (var i = 0; i < 30; i++)
                File.WriteAllText(Path.Combine(tempDir, $"file{i:D2}.txt"), "");

            var service = new FileSearchService();
            var results = service.Search(tempDir, "", maxResults: 10);
            Assert.Equal(10, results.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_NonExistentDirectory_ReturnsEmpty()
    {
        var service = new FileSearchService();
        var results = service.Search(@"C:\NonExistent\Path\12345", "test");
        Assert.Empty(results);
    }

    // ── Caching ─────────────────────────────────────────────────────

    [Fact]
    public void Search_SecondCallUsesCachedIndex()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            File.WriteAllText(Path.Combine(tempDir, "src", "app.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "helper.cs"), "");

            var service = new FileSearchService();

            // First call builds the index
            var r1 = service.Search(tempDir, "app");
            Assert.Single(r1);

            // Add a new file — should NOT appear because index is cached
            File.WriteAllText(Path.Combine(tempDir, "src", "newfile.cs"), "");
            var r2 = service.Search(tempDir, "newfile");
            Assert.Empty(r2); // cache doesn't know about it yet

            // Invalidate cache — now it should appear
            service.InvalidateCache();
            var r3 = service.Search(tempDir, "newfile");
            Assert.Single(r3);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_DifferentDirectory_RebuildsIndex()
    {
        var dir1 = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        var dir2 = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir1);
            Directory.CreateDirectory(dir2);
            File.WriteAllText(Path.Combine(dir1, "file1.cs"), "");
            File.WriteAllText(Path.Combine(dir2, "file2.cs"), "");

            var service = new FileSearchService();

            var r1 = service.Search(dir1, "file");
            Assert.Single(r1);
            Assert.Contains("file1.cs", r1[0].RelativePath);

            // Switching directory rebuilds index
            var r2 = service.Search(dir2, "file");
            Assert.Single(r2);
            Assert.Contains("file2.cs", r2[0].RelativePath);
        }
        finally
        {
            try { Directory.Delete(dir1, true); } catch { }
            try { Directory.Delete(dir2, true); } catch { }
        }
    }

    // ── Incremental narrowing ───────────────────────────────────────

    [Fact]
    public void Search_IncrementalNarrowing_FiltersFromPrevious()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            File.WriteAllText(Path.Combine(tempDir, "src", "ChatViewModel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "ChatView.axaml"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "SettingsViewModel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Channel.cs"), "");

            var service = new FileSearchService();

            // "ch" — matches ChatViewModel.cs, ChatView.axaml, Channel.cs
            var r1 = service.Search(tempDir, "ch");
            Assert.Equal(3, r1.Count);

            // "cha" — narrows from previous (still 3: Chat*, Channel)
            var r2 = service.Search(tempDir, "cha");
            Assert.Equal(3, r2.Count);

            // "chat" — narrows further (2: ChatViewModel.cs, ChatView.axaml)
            var r3 = service.Search(tempDir, "chat");
            Assert.Equal(2, r3.Count);
            Assert.All(r3, r => Assert.Contains("Chat", r.RelativePath));

            // "chatv" — narrows to just 2 (ChatViewModel.cs, ChatView.axaml)
            var r4 = service.Search(tempDir, "chatv");
            Assert.Equal(2, r4.Count);

            // "chatviewmodel" — narrows to 1
            var r5 = service.Search(tempDir, "chatviewmodel");
            Assert.Single(r5);
            Assert.Contains("ChatViewModel.cs", r5[0].RelativePath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_NewQueryResetsPreviousResults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            File.WriteAllText(Path.Combine(tempDir, "src", "ChatViewModel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "SettingsViewModel.cs"), "");

            var service = new FileSearchService();

            // Search for "chat" — narrows
            var r1 = service.Search(tempDir, "chat");
            Assert.Single(r1);

            // Completely different query "settings" — does NOT narrow from "chat" results
            var r2 = service.Search(tempDir, "settings");
            Assert.Single(r2);
            Assert.Contains("Settings", r2[0].RelativePath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_EmptyQueryResetsPreviousState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "app.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "helper.cs"), "");

            var service = new FileSearchService();

            // Query then empty
            service.Search(tempDir, "app");
            var r = service.Search(tempDir, "");
            Assert.Equal(2, r.Count); // returns all files
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Search_DeterministicResults_RegardlessOfIntermediateQueries()
    {
        // Reproduces bug: type → delete to # → retype → different results
        // Root cause: different intermediate query sequences (due to cancellation timing)
        // produced different candidate sets when there was a candidate limit.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            Directory.CreateDirectory(Path.Combine(tempDir, "lib"));
            Directory.CreateDirectory(Path.Combine(tempDir, "tools"));

            // Create enough files that a candidate limit could truncate results
            File.WriteAllText(Path.Combine(tempDir, "src", "ChatViewModel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "ChatView.axaml"), "");
            File.WriteAllText(Path.Combine(tempDir, "lib", "ChatService.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "tools", "ChatAnalyzer.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "SettingsViewModel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Channel.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "src", "Config.cs"), "");
            File.WriteAllText(Path.Combine(tempDir, "lib", "Cache.cs"), "");

            var service = new FileSearchService();

            // Path 1: type "chat" one char at a time
            service.Search(tempDir, "c");
            service.Search(tempDir, "ch");
            service.Search(tempDir, "cha");
            var path1Results = service.Search(tempDir, "chat");

            // Clear (empty query resets)
            service.Search(tempDir, "");

            // Path 2: type "chat" directly (simulating skipped intermediate queries)
            var path2Results = service.Search(tempDir, "chat");

            // Must produce identical results
            Assert.Equal(path1Results.Count, path2Results.Count);
            for (var i = 0; i < path1Results.Count; i++)
            {
                Assert.Equal(path1Results[i].RelativePath, path2Results[i].RelativePath);
            }

            // Clear again
            service.Search(tempDir, "");

            // Path 3: different intermediate steps: "ch" → "chat" (skipping c, cha)
            service.Search(tempDir, "ch");
            var path3Results = service.Search(tempDir, "chat");

            Assert.Equal(path1Results.Count, path3Results.Count);
            for (var i = 0; i < path1Results.Count; i++)
            {
                Assert.Equal(path1Results[i].RelativePath, path3Results[i].RelativePath);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── Gitignore caching ───────────────────────────────────────────

    [Fact]
    public void GetGitIgnoreRules_CachesPerDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "*.log\n");

            var service = new FileSearchService();
            var rules1 = service.GetGitIgnoreRules(tempDir);
            var rules2 = service.GetGitIgnoreRules(tempDir);

            Assert.Same(rules1, rules2); // Same cached instance
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
