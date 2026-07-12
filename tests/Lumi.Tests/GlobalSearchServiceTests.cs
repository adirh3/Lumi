using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class GlobalSearchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 22, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SearchAsync_UsesPersistedChatSnapshotForHistoryMatches()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Weekly sync",
            UpdatedAt = Now.AddHours(-2)
        };

        var service = CreateService(
            new AppData { Chats = [chat] },
            new Dictionary<Guid, ChatSearchSnapshot>
            {
                [chat.Id] = new()
                {
                    Version = "persisted-1",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Text = "We discussed the application rollout plan in detail.",
                            Timestamp = Now.AddHours(-1)
                        }
                    ]
                }
            });

        var results = await service.SearchAsync("applic");

        var match = Assert.Single(results);
        Assert.Equal(GlobalSearchCategory.Chats, match.Category);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_FastModeSkipsColdPersistedHistoryUntilFullPass()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Weekly sync",
            UpdatedAt = Now.AddHours(-2)
        };

        var providerCalls = 0;
        var snapshot = new ChatSearchSnapshot
        {
            Version = "persisted-fast-1",
            Messages =
            [
                new ChatSearchMessage
                {
                    Text = "We discussed the application rollout plan in detail.",
                    Timestamp = Now.AddHours(-1)
                }
            ]
        };

        var service = new GlobalSearchService(
            () => new AppData { Chats = [chat] },
            _ =>
            {
                providerCalls++;
                return snapshot;
            },
            () => Now);

        var fastResults = await service.SearchAsync("applic", GlobalSearchExecutionMode.Fast);
        Assert.Empty(fastResults);
        Assert.Equal(0, providerCalls);

        var fullResults = await service.SearchAsync("applic", GlobalSearchExecutionMode.Full);
        var match = Assert.Single(fullResults);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task PreparedSearch_ReusesOneDataSnapshotAcrossProgressivePhases()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Alpha planning",
            UpdatedAt = Now.AddHours(-2)
        };
        var dataCalls = 0;
        var service = new GlobalSearchService(
            () =>
            {
                dataCalls++;
                return new AppData { Chats = [chat] };
            },
            _ => new ChatSearchSnapshot { Version = "empty" },
            () => Now);

        var prepared = service.PrepareSearch("alpha");
        await service.SearchAsync(prepared, GlobalSearchExecutionMode.Preview);
        await service.SearchAsync(prepared, GlobalSearchExecutionMode.Fast);
        await service.SearchAsync(prepared, GlobalSearchExecutionMode.Interactive);
        await service.SearchAsync(prepared, GlobalSearchExecutionMode.Full);

        Assert.Equal(1, dataCalls);
    }

    [Fact]
    public async Task SearchAsync_PreviewModeSkipsColdPersistedHistoryUntilFullPass()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Weekly sync",
            UpdatedAt = Now.AddHours(-2)
        };

        var providerCalls = 0;
        var snapshot = new ChatSearchSnapshot
        {
            Version = "persisted-preview-1",
            Messages =
            [
                new ChatSearchMessage
                {
                    Text = "We discussed the application rollout plan in detail.",
                    Timestamp = Now.AddHours(-1)
                }
            ]
        };

        var service = new GlobalSearchService(
            () => new AppData { Chats = [chat] },
            _ =>
            {
                providerCalls++;
                return snapshot;
            },
            () => Now);

        var previewResults = await service.SearchAsync("applic", GlobalSearchExecutionMode.Preview);
        Assert.Empty(previewResults);
        Assert.Equal(0, providerCalls);

        var fullResults = await service.SearchAsync("applic", GlobalSearchExecutionMode.Full);
        var match = Assert.Single(fullResults);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task SearchAsync_PreviewModeSkipsSkillContentUntilFullPass()
    {
        var skill = new Skill
        {
            Name = "Document Creator",
            Description = "Creates polished user-facing files.",
            Content = "Can produce Word documents, Excel workbooks, and PowerPoint decks.",
            CreatedAt = Now.AddDays(-2)
        };

        var service = CreateService(new AppData { Skills = [skill] });

        var previewResults = await service.SearchAsync("excel", GlobalSearchExecutionMode.Preview);
        Assert.Empty(previewResults);

        var interactiveResults = await service.SearchAsync("excel", GlobalSearchExecutionMode.Interactive);
        var interactiveMatch = Assert.Single(interactiveResults);
        Assert.Equal(GlobalSearchCategory.Skills, interactiveMatch.Category);
        Assert.True(interactiveMatch.IsContentMatch);

        var fullResults = await service.SearchAsync("excel", GlobalSearchExecutionMode.Full);
        var match = Assert.Single(fullResults);
        Assert.Equal(GlobalSearchCategory.Skills, match.Category);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_InteractiveModeBoundsColdPersistedHistoryReads()
    {
        var chats = Enumerable.Range(0, 70)
            .Select(index => new Chat
            {
                Id = Guid.NewGuid(),
                Title = $"Chat {index}",
                UpdatedAt = Now.AddMinutes(-index)
            })
            .ToList();

        var providerCalls = 0;
        var service = new GlobalSearchService(
            () => new AppData { Chats = chats },
            _ =>
            {
                providerCalls++;
                return new ChatSearchSnapshot
                {
                    Version = $"persisted-interactive-{providerCalls}",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Text = "needle appears in persisted history.",
                            Timestamp = Now
                        }
                    ]
                };
            },
            () => Now);

        var interactiveResults = await service.SearchAsync("needle", GlobalSearchExecutionMode.Interactive);

        Assert.NotEmpty(interactiveResults);
        Assert.InRange(providerCalls, 1, 16);
    }

    [Fact]
    public async Task SearchAsync_FastModeUsesCachedHistoryAfterFullPass()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Weekly sync",
            UpdatedAt = Now.AddHours(-2)
        };

        var providerCalls = 0;
        var snapshot = new ChatSearchSnapshot
        {
            Version = "persisted-fast-2",
            Messages =
            [
                new ChatSearchMessage
                {
                    Text = "We discussed the application rollout plan in detail.",
                    Timestamp = Now.AddHours(-1)
                }
            ]
        };

        var service = new GlobalSearchService(
            () => new AppData { Chats = [chat] },
            _ =>
            {
                providerCalls++;
                return snapshot;
            },
            () => Now);

        await service.SearchAsync("applic", GlobalSearchExecutionMode.Full);
        Assert.Equal(1, providerCalls);

        var fastResults = await service.SearchAsync("applic", GlobalSearchExecutionMode.Fast);
        var match = Assert.Single(fastResults);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task SearchAsync_MergesTitleAndContentMatchesForChats()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Alpha planning",
            UpdatedAt = Now.AddHours(-3)
        };

        var service = CreateService(
            new AppData { Chats = [chat] },
            new Dictionary<Guid, ChatSearchSnapshot>
            {
                [chat.Id] = new()
                {
                    Version = "persisted-2",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Text = "Beta rollout is ready for the next milestone.",
                            Timestamp = Now.AddHours(-2)
                        }
                    ]
                }
            });

        var results = await service.SearchAsync("alpha beta");

        var match = Assert.Single(results);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_MatchesTermsDeepInsideLongChatContent()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Lumi Issue 13 Fix Plan",
            UpdatedAt = Now.AddHours(-3)
        };
        var content = $"design {string.Join(' ', Enumerable.Repeat("filler", 140))} memory";
        var service = CreateService(
            new AppData { Chats = [chat] },
            new Dictionary<Guid, ChatSearchSnapshot>
            {
                [chat.Id] = new()
                {
                    Version = "long-content",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Text = content,
                            Timestamp = Now.AddHours(-2)
                        }
                    ]
                }
            });

        var results = await service.SearchAsync("memory design");

        var match = Assert.Single(results);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_MatchesTermAtEndOfSingleOversizedMessage()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Long response",
            UpdatedAt = Now.AddHours(-3)
        };
        var service = CreateService(
            new AppData { Chats = [chat] },
            new Dictionary<Guid, ChatSearchSnapshot>
            {
                [chat.Id] = new()
                {
                    Version = "oversized-tail",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Role = "assistant",
                            Text = $"{new string('x', 7_000)} tailneedle",
                            Timestamp = Now.AddHours(-2)
                        }
                    ]
                }
            });

        var results = await service.SearchAsync("tailneedle");

        Assert.Same(chat, Assert.Single(results).Item);
    }

    [Fact]
    public async Task SearchAsync_PrioritizesDirectUserMessageMatchOverMixedTitleAndAssistantMatch()
    {
        var userMatch = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Lumi Issue 13 Fix Plan",
            UpdatedAt = Now.AddHours(-2)
        };
        var mixedMatch = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Memory Leak Investigation",
            UpdatedAt = Now.AddHours(-1)
        };
        var service = CreateService(
            new AppData { Chats = [mixedMatch, userMatch] },
            new Dictionary<Guid, ChatSearchSnapshot>
            {
                [userMatch.Id] = new()
                {
                    Version = "user-match",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Role = "user",
                            Text = "Please inspect the memory system and design a robust fix.",
                            Timestamp = Now.AddHours(-2)
                        }
                    ]
                },
                [mixedMatch.Id] = new()
                {
                    Version = "mixed-match",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Role = "assistant",
                            Text = "We should design a diagnostic workflow.",
                            Timestamp = Now.AddHours(-1)
                        }
                    ]
                }
            });

        var results = (await service.SearchAsync("memory design"))
            .Where(static result => result.Category == GlobalSearchCategory.Chats)
            .ToList();

        Assert.Equal(userMatch, results[0].Item);
        Assert.Equal(mixedMatch, results[1].Item);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("reasoning")]
    [InlineData("system")]
    public async Task SearchAsync_IgnoresInternalTranscriptNoise(string role)
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Unrelated chat",
            UpdatedAt = Now
        };
        var service = CreateService(
            new AppData { Chats = [chat] },
            new Dictionary<Guid, ChatSearchSnapshot>
            {
                [chat.Id] = new()
                {
                    Version = $"noise-{role}",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Role = role,
                            Text = "memory design",
                            Timestamp = Now
                        }
                    ]
                }
            });

        var results = await service.SearchAsync("memory design");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_FuzzyTypoRanksClosestTitleFirst()
    {
        var bestSkill = new Skill
        {
            Name = "Search Assistant",
            Description = "Finds the right information fast.",
            CreatedAt = Now.AddDays(-10)
        };

        var distractor = new Skill
        {
            Name = "Research Assistant",
            Description = "Helps summarize research notes.",
            CreatedAt = Now.AddDays(-1)
        };

        var service = CreateService(new AppData { Skills = [bestSkill, distractor] });

        var results = await service.SearchAsync("serach");

        Assert.Equal(bestSkill.Name, results.First().Title);
    }

    [Fact]
    public async Task SearchAsync_CompactQueryMatchesAcrossSeparators()
    {
        var skill = new Skill
        {
            Name = "File Search Service",
            Description = "Finds files from a workspace.",
            CreatedAt = Now.AddDays(-3)
        };

        var service = CreateService(new AppData { Skills = [skill] });

        var results = await service.SearchAsync("filesearch");

        var match = Assert.Single(results);
        Assert.Equal(skill.Name, match.Title);
    }

    [Fact]
    public async Task SearchAsync_AcronymQueryMatchesBetweenSeparators()
    {
        var best = new Skill
        {
            Name = "Log Analytics Workspace",
            Description = "Azure logs and metrics.",
            CreatedAt = Now.AddDays(-3)
        };

        var loose = new Skill
        {
            Name = "Launch Window",
            Description = "Release timing helper.",
            CreatedAt = Now.AddDays(-1)
        };

        var service = CreateService(new AppData { Skills = [loose, best] });

        var results = await service.SearchAsync("law");

        Assert.NotEmpty(results);
        Assert.Equal(best.Name, results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_MidTermTypoMatchesAcrossSeparators()
    {
        var skill = new Skill
        {
            Name = "Log Analytics Workspace",
            Description = "Azure logs and metrics.",
            CreatedAt = Now.AddDays(-3)
        };

        var service = CreateService(new AppData { Skills = [skill] });

        var results = await service.SearchAsync("analyticworkspce");

        var match = Assert.Single(results);
        Assert.Equal(skill.Name, match.Title);
    }

    [Fact]
    public async Task SearchAsync_IncompleteWordMatchesLongerContentWord()
    {
        var skill = new Skill
        {
            Name = "Release Assistant",
            Description = "Helps with launches.",
            Content = "Prepare deployment checklists, release notes, and rollout comms.",
            CreatedAt = Now.AddDays(-5)
        };

        var service = CreateService(new AppData { Skills = [skill] });

        var results = await service.SearchAsync("deploy");

        var match = Assert.Single(results);
        Assert.Equal(skill.Name, match.Title);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_SkillQueryMatchesTermsAcrossNameAndContent()
    {
        var skill = new Skill
        {
            Name = "Document Creator",
            Description = "Creates polished user-facing files.",
            Content = "Can produce Word documents, Excel workbooks, and PowerPoint decks.",
            CreatedAt = Now.AddDays(-2)
        };

        var service = CreateService(new AppData { Skills = [skill] });

        var results = await service.SearchAsync("document excel");

        var match = Assert.Single(results);
        Assert.Equal(GlobalSearchCategory.Skills, match.Category);
        Assert.Equal(skill.Name, match.Title);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_AgentQueryMatchesTermsAcrossNameAndPrompt()
    {
        var agent = new LumiAgent
        {
            Name = "Code Reviewer",
            Description = "Reviews implementation quality.",
            SystemPrompt = "Focus on security vulnerabilities, correctness, and performance regressions.",
            CreatedAt = Now.AddDays(-4)
        };

        var service = CreateService(new AppData { Agents = [agent] });

        var results = await service.SearchAsync("review security");

        var match = Assert.Single(results);
        Assert.Equal(GlobalSearchCategory.Lumis, match.Category);
        Assert.Equal(agent.Name, match.Title);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_DiacriticsAreIgnoredForSkillNames()
    {
        var skill = new Skill
        {
            Name = "R\u00e9sum\u00e9 Writer",
            Description = "Drafts professional CV content.",
            CreatedAt = Now.AddDays(-7)
        };

        var service = CreateService(new AppData { Skills = [skill] });

        var results = await service.SearchAsync("resume");

        var match = Assert.Single(results);
        Assert.Equal(skill.Name, match.Title);
    }

    [Fact]
    public async Task SearchAsync_RecencyBreaksTitleTiesForChats()
    {
        var older = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Alpha planning",
            UpdatedAt = Now.AddDays(-20)
        };

        var newer = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Alpha review",
            UpdatedAt = Now.AddHours(-4)
        };

        var service = CreateService(new AppData { Chats = [older, newer] });

        var results = (await service.SearchAsync("alpha"))
            .Where(static match => match.Category == GlobalSearchCategory.Chats)
            .ToList();

        Assert.Equal(newer, results[0].Item);
        Assert.Equal(older, results[1].Item);
    }

    private static GlobalSearchService CreateService(
        AppData data,
        IReadOnlyDictionary<Guid, ChatSearchSnapshot>? chatSnapshots = null)
    {
        return new GlobalSearchService(
            () => data,
            chat =>
            {
                if (chatSnapshots is not null
                    && chatSnapshots.TryGetValue(chat.Id, out var snapshot))
                {
                    return snapshot;
                }

                return new ChatSearchSnapshot { Version = $"empty:{chat.Id}" };
            },
            () => Now);
    }
}
