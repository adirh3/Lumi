using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Discover;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

public sealed class CustomizeCapabilitiesTests
{
    [Fact]
    public void ExternalItems_AreGroupedBySourceWithPluginsFirst()
    {
        Loc.Load("en");
        var vm = new ExternalCapabilitiesViewModel(CapabilityKind.Skill);

        vm.Update(Snapshot(
            Skill("triage", CapabilityOrigin.Personal),
            Skill("azure:deploy", CapabilityOrigin.ForPlugin("azure"), plugin: "azure"),
            Skill("azure:diagnose", CapabilityOrigin.ForPlugin("azure"), plugin: "azure"),
            Skill("ai-ready:check", CapabilityOrigin.ForPlugin("ai-ready"), plugin: "ai-ready"),
            Skill("tone", CapabilityOrigin.Lumi),
            Agent("reporter", CapabilityOrigin.ForPlugin("azure"), plugin: "azure")));

        Assert.Equal(["AI Ready", "Azure", Loc.External_GroupCopilot], vm.Groups.Select(static group => group.Title));
        // People read a part's own name; the plugin is the group it sits in.
        Assert.Equal(["deploy", "diagnose"], vm.Groups[1].Items.Select(static item => item.Name));
        Assert.True(vm.Groups[0].IsPlugin);
        Assert.False(vm.Groups[2].IsPlugin);
        Assert.Equal(4, vm.TotalCount);
    }

    [Fact]
    public void ExternalItems_SearchHidesGroupsWithoutMatches()
    {
        Loc.Load("en");
        var vm = new ExternalCapabilitiesViewModel(CapabilityKind.Skill);
        vm.Update(Snapshot(
            Skill("triage", CapabilityOrigin.Personal, "Sort incoming issues"),
            Skill("azure:deploy", CapabilityOrigin.ForPlugin("azure"), "Ship to Azure", plugin: "azure")));

        vm.ApplySearch("issues");

        var group = Assert.Single(vm.Groups);
        Assert.Equal("triage", Assert.Single(group.Items).Descriptor.Name);
        Assert.True(vm.HasAnyItems);

        vm.ApplySearch("zzqqxx");
        Assert.False(vm.HasGroups);
        Assert.True(vm.HasAnyItems);

        vm.ApplySearch("");
        Assert.Equal(2, vm.Groups.Count);
    }

    [Fact]
    public void ExternalItems_OpenRevealsTheItemAndSurvivesARefresh()
    {
        Loc.Load("en");
        var vm = new ExternalCapabilitiesViewModel(CapabilityKind.Skill);
        var deploy = Skill("azure:deploy", CapabilityOrigin.ForPlugin("azure"), "Ship to Azure", plugin: "azure");
        vm.Update(Snapshot(deploy, Skill("triage", CapabilityOrigin.Personal)));
        var opened = new List<string>();
        vm.Opened += item => opened.Add(item.Descriptor.Name);

        vm.Groups.SelectMany(static group => group.Items).Single(static item => item.Descriptor.Name == "azure:deploy").OpenCommand.Execute(null);

        Assert.True(vm.IsViewing);
        Assert.Equal("Azure", vm.DetailSourceTitle);
        Assert.True(vm.DetailIsPlugin);
        Assert.Contains("Azure", vm.DetailNotice);
        Assert.True(vm.Groups[0].IsExpanded);
        Assert.True(vm.OpenItem!.IsSelected);
        Assert.Equal(["azure:deploy"], opened);
        Assert.Equal("/azure:deploy", Assert.Single(vm.DetailFacts).Value);

        // A later snapshot keeps the open item and its expanded group.
        vm.Update(Snapshot(deploy, Skill("triage", CapabilityOrigin.Personal)));
        Assert.Equal("azure:deploy", vm.OpenItem?.Descriptor.Name);
        Assert.True(vm.Groups[0].IsExpanded);

        // Uninstalling the plugin closes it.
        vm.Update(Snapshot(Skill("triage", CapabilityOrigin.Personal)));
        Assert.False(vm.IsViewing);
        Assert.DoesNotContain(vm.Groups.SelectMany(static group => group.Items), static item => item.Descriptor.Name == "azure:deploy");
    }

    [Fact]
    public void ExternalItems_ManageInDiscoverNamesThePlugin()
    {
        Loc.Load("en");
        var vm = new ExternalCapabilitiesViewModel(CapabilityKind.Agent);
        vm.Update(Snapshot(Agent("reporter", CapabilityOrigin.ForPlugin("azure"), plugin: "azure")));
        string? managed = null;
        vm.ManageInDiscoverRequested += plugin => managed = plugin;

        vm.Groups[0].ManageCommand.Execute(null);

        Assert.Equal("azure", managed);
    }

    [Fact]
    public void ExternalItems_KeepUnchangedGroupsWhenAnotherPluginChanges()
    {
        Loc.Load("en");
        var vm = new ExternalCapabilitiesViewModel(CapabilityKind.Skill);
        CapabilityDescriptor[] azure =
        [
            Skill("azure:deploy", CapabilityOrigin.ForPlugin("azure"), plugin: "azure"),
            Skill("azure:diagnose", CapabilityOrigin.ForPlugin("azure"), plugin: "azure"),
        ];
        vm.Update(Snapshot(azure));
        var group = Assert.Single(vm.Groups);
        var items = group.Items.ToArray();
        var changes = 0;
        vm.Groups.CollectionChanged += (_, _) => changes++;
        group.Items.CollectionChanged += (_, _) => changes++;

        // Every section switch re-reads the catalog; the same items must not rebuild anything.
        vm.Update(Snapshot(azure));
        Assert.Equal(0, changes);

        // Installing another plugin adds its group and leaves Azure's cards and rows alone.
        vm.Update(Snapshot([.. azure, Skill("csharp:msbuild", CapabilityOrigin.ForPlugin("csharp"), plugin: "csharp")]));
        Assert.Equal(["Azure", "Csharp"], vm.Groups.Select(static candidate => candidate.Title));
        Assert.Same(group, vm.Groups[0]);
        Assert.Equal(items, group.Items);

        vm.Update(Snapshot(azure));
        Assert.Same(group, Assert.Single(vm.Groups));
        Assert.Equal(items, group.Items);
    }

    [Fact]
    public void CopySkill_BringsItsFilesAndAvoidsTakenNames()
    {
        var root = TempFolder();
        try
        {
            var folder = Path.Combine(root, "installed-plugins", "awesome-copilot", "azure", "skills", "deploy");
            Directory.CreateDirectory(Path.Combine(folder, "scripts"));
            var skillFile = Path.Combine(folder, "SKILL.md");
            File.WriteAllText(skillFile, "---\nname: deploy\ndescription: Ship it\n---\n# Deploy\nRun scripts/run.ps1.");
            File.WriteAllText(Path.Combine(folder, "scripts", "run.ps1"), "Write-Output ok");
            var store = new DataStore(new AppData { Skills = [new Skill { Name = "deploy" }] }, Path.Combine(root, "skills"));

            var copy = ExternalCapabilityCopier.CopySkill(store, new CapabilityDescriptor
            {
                Kind = CapabilityKind.Skill,
                Name = "azure:deploy",
                Origin = CapabilityOrigin.ForPlugin("azure"),
                SourcePath = skillFile,
                PluginName = "azure",
            });

            Assert.NotNull(copy);
            Assert.Equal("deploy (copy)", copy!.Name);
            Assert.Equal("Ship it", copy.Description);
            Assert.StartsWith("# Deploy", copy.Content);
            var bundle = store.GetSkillBundleDirectory(copy.Id);
            Assert.Contains(bundle, copy.Content);
            Assert.Equal("Write-Output ok", File.ReadAllText(Path.Combine(bundle, "scripts", "run.ps1")));
            Assert.False(File.Exists(Path.Combine(bundle, "SKILL.md")));
            Assert.Contains(copy, store.Data.Skills);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadMcpServer_FollowsThePointerAndExpandsThePluginRoot()
    {
        var root = TempFolder();
        try
        {
            var plugin = Path.Combine(root, "installed-plugins", "awesome-copilot", "azure");
            Directory.CreateDirectory(plugin);
            var config = Path.Combine(plugin, ".mcp.json");
            File.WriteAllText(config, """
                {
                  "mcpServers": {
                    "azure": { "command": "node", "args": ["${PLUGIN_ROOT}/server.js"], "env": { "MODE": "fast" }, "tools": ["list", "deploy"] },
                    "web": { "url": "https://example.com/mcp", "headers": { "X-Key": "1" } },
                    "work": { "type": "http", "url": "https://work.example/mcp", "oauthClientId": "client", "auth": { "redirectPort": 12798 } }
                  }
                }
                """);

            var local = ExternalCapabilityCopier.ReadMcpServer(Server("azure", config, "/mcpServers/azure"));
            var remote = ExternalCapabilityCopier.ReadMcpServer(Server("web", config, pointer: null));

            Assert.Equal("node", local!.Command);
            Assert.Equal([plugin + "/server.js"], local.Args);
            Assert.Equal("fast", local.Env["MODE"]);
            Assert.Equal(["list", "deploy"], local.Tools);
            Assert.Equal("remote", remote!.ServerType);
            Assert.Equal("https://example.com/mcp", remote.Url);
            Assert.Null(ExternalCapabilityCopier.ReadMcpServer(Server("missing", config, "/mcpServers/missing")));

            var store = new DataStore(new AppData { McpServers = [new McpServer { Name = "azure" }] }, Path.Combine(root, "skills"));
            var copy = ExternalCapabilityCopier.CopyMcpServer(store, Server("azure", config, "/mcpServers/azure"));
            Assert.Equal("azure (copy)", copy!.Name);

            // A server with its own sign-in is shown, but a copy could not sign in, so none is made.
            Assert.NotNull(ExternalCapabilityCopier.ReadMcpServer(Server("work", config, "/mcpServers/work"), out var signsIn));
            Assert.True(signsIn);
            Assert.Null(ExternalCapabilityCopier.CopyMcpServer(store, Server("work", config, "/mcpServers/work")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExternalItems_NoticesNameWhereTheItemLivesAndOnlyOfferCopiesThatCanBeMade()
    {
        Loc.Load("en");
        var root = TempFolder();
        try
        {
            var file = Path.Combine(root, "SKILL.md");
            File.WriteAllText(file, "Review carefully.");
            var vm = new ExternalCapabilitiesViewModel(CapabilityKind.Skill);
            vm.Update(Snapshot(
                Skill("triage", CapabilityOrigin.Personal) with { SourcePath = file },
                Skill("tone", CapabilityOrigin.Workspace),
                Skill("review", CapabilityOrigin.BuiltIn)));

            void Open(string name) => vm.Open(vm.Groups.SelectMany(static group => group.Items).Single(item => item.Descriptor.Name == name));

            Open("triage");
            Assert.True(vm.CanCopy);
            Assert.Equal(string.Format(Loc.External_NoticeOtherSkill, Loc.External_PlaceCopilot), vm.DetailNotice);

            // Without a file there is nothing to copy, so the notice does not offer one.
            Open("tone");
            Assert.False(vm.CanCopy);
            Assert.Equal(string.Format(Loc.External_NoticeOtherNoCopy, Loc.External_PlaceFolder), vm.DetailNotice);

            Open("review");
            Assert.Equal(Loc.External_NoticeBuiltIn, vm.DetailNotice);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UniqueName_KeepsFreeNamesAndNumbersRepeatedCopies()
    {
        Assert.Equal("Planner", ExternalCapabilityCopier.UniqueName(" Planner ", ["Other"]));
        Assert.Equal("Planner (copy)", ExternalCapabilityCopier.UniqueName("Planner", ["planner"]));
        Assert.Equal("Planner (copy 2)", ExternalCapabilityCopier.UniqueName("Planner", ["Planner", "Planner (copy)"]));
        Assert.Equal("Copy", ExternalCapabilityCopier.UniqueName("  ", []));
    }

    [Fact]
    public async Task Discover_ListsRegistryServersAndAddsThemAsLumiServers()
    {
        Loc.Load("en");
        const string page = """
            {
              "servers": [
                { "server": { "name": "acme/weather", "title": "Weather", "description": "Forecasts", "version": "1.0.0",
                              "remotes": [ { "type": "streamable-http", "url": "https://weather.example/mcp" } ] } },
                { "server": { "name": "acme/search", "title": "Search", "version": "2.0.0",
                              "packages": [ { "registryType": "npm", "identifier": "@acme/search", "transport": { "type": "stdio" },
                                              "environmentVariables": [ { "name": "SEARCH_KEY", "isRequired": true, "isSecret": true } ] } ] } }
              ],
              "metadata": { "count": 2, "total": 2 }
            }
            """;
        var requests = new List<Uri>();
        var store = new DataStore(
            new AppData { Settings = new UserSettings { McpRegistryUrls = ["https://registry.test"] } },
            Path.Combine(Path.GetTempPath(), $"lumi-discover-{Guid.NewGuid():N}"));
        var vm = new DiscoverViewModel(
            store,
            new PluginCatalogService(static () => null, static () => { }),
            new McpRegistryClient(new HttpClient(new StubHandler(request =>
            {
                requests.Add(request.RequestUri!);
                return page;
            }))),
            static () => null);
        var setupRequests = new List<(Guid Id, bool NeedsSetup)>();
        vm.OpenMcpServerRequested += (id, needsSetup) => setupRequests.Add((id, needsSetup));

        await vm.EnsureLoadedAsync();

        // Plugins come from the Copilot runtime, which this test never connects.
        Assert.True(vm.IsWaitingForCopilot);
        Assert.Equal("https://registry.test/v0.1/servers?limit=40&version=latest", Assert.Single(requests).ToString());
        Assert.Equal(["Weather", "Search"], vm.ServerPreview.Select(static card => card.Title));
        Assert.Equal("2", vm.ServerCountText);

        // A hosted server is ready as soon as it is added.
        vm.ServerPreview[0].OpenCommand.Execute(null);
        await vm.AddCommand.ExecuteAsync(null);
        var weather = Assert.Single(store.Data.McpServers);
        Assert.Equal("acme/weather", weather.CatalogId);
        Assert.Equal("https://weather.example/mcp", weather.Url);
        Assert.True(vm.ServerPreview[0].IsAdded);
        Assert.Empty(setupRequests);
        Assert.Equal("1", vm.InstalledCountText);

        // One that needs a key opens in the MCP editor to finish setup.
        vm.ServerPreview[1].OpenCommand.Execute(null);
        await vm.AddCommand.ExecuteAsync(null);
        var search = store.Data.McpServers.Single(server => server.CatalogId == "acme/search");
        Assert.Equal("", search.Env["SEARCH_KEY"]);
        Assert.Equal((search.Id, true), Assert.Single(setupRequests));

        // Adding again is a no-op: the card already shows it as added.
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Equal(2, store.Data.McpServers.Count);
    }

    [Fact]
    public async Task Discover_ReportsATimedOutRegistry_AndItsCommandsDoNotThrow()
    {
        Loc.Load("en");
        var store = new DataStore(
            new AppData { Settings = new UserSettings { McpRegistryUrls = ["https://registry.test"] } },
            Path.Combine(Path.GetTempPath(), $"lumi-discover-{Guid.NewGuid():N}"));
        // HttpClient reports its timeout as a cancellation that nobody asked for.
        var stalled = new HttpClient(new StallingHandler()) { Timeout = TimeSpan.FromMilliseconds(50) };
        var vm = new DiscoverViewModel(
            store,
            new PluginCatalogService(static () => null, static () => { }),
            new McpRegistryClient(stalled),
            static () => null);

        await vm.EnsureLoadedAsync();

        Assert.True(vm.HasServerError);
        Assert.Empty(vm.ServerPreview);
        Assert.False(vm.IsLoadingServers);

        // Commands awaiting the same timeout report it; a rethrown cancellation would close Lumi.
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.True(vm.HasServerError);

        vm.NewRegistry = "https://other.test";
        await vm.AddRegistryCommand.ExecuteAsync(null);
        Assert.Equal(string.Format(Loc.Discover_RegistryUnreachable, Loc.Discover_TimedOut), vm.SourceError);
        Assert.DoesNotContain("https://other.test", store.Data.Settings.McpRegistryUrls);
    }

    [Theory]
    [InlineData(0, MainWindow.NavChatButton)]
    [InlineData(2, MainWindow.NavCustomizeButton)]                  // Projects are set up in Customize; the chat list only switches them
    [InlineData(MainViewModel.LibraryNavIndex, MainWindow.NavLibraryButton)]
    [InlineData(1, MainWindow.NavJobsButton)]
    [InlineData(3, MainWindow.NavCustomizeButton)]
    [InlineData(4, MainWindow.NavCustomizeButton)]
    [InlineData(6, MainWindow.NavCustomizeButton)]
    [InlineData(MainViewModel.DiscoverNavIndex, MainWindow.NavCustomizeButton)]
    [InlineData(7, MainWindow.NavSettingsButton)]
    [InlineData(MainViewModel.MemoriesNavIndex, MainWindow.NavSettingsButton)] // Memories is a Settings page
    public void Nav_EveryPageLightsOnePlaceInTheBar(int page, int button)
    {
        Assert.Equal(button, MainWindow.NavButtonForPage(page));
        Assert.Equal(button == MainWindow.NavCustomizeButton, MainViewModel.IsCustomizeIndex(page));
    }

    private static CapabilitySnapshot Snapshot(params CapabilityDescriptor[] capabilities)
        => new(CapabilityQuery.Empty, capabilities, isComplete: true);

    private static CapabilityDescriptor Skill(string name, CapabilityOrigin origin, string? description = null, string? plugin = null)
        => new() { Kind = CapabilityKind.Skill, Name = name, Origin = origin, Description = description, PluginName = plugin };

    private static CapabilityDescriptor Agent(string name, CapabilityOrigin origin, string? plugin = null)
        => new() { Kind = CapabilityKind.Agent, Name = name, Origin = origin, Content = "You report.", PluginName = plugin };

    private static CapabilityDescriptor Server(string name, string config, string? pointer)
        => new()
        {
            Kind = CapabilityKind.McpServer,
            Name = name,
            Origin = CapabilityOrigin.ForPlugin("azure"),
            ConfigFilePath = config,
            ConfigPointer = pointer,
            PluginName = "azure",
        };

    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumi-customize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
    }

    /// <summary>A server that never answers.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The request should have timed out.");
        }
    }
}
