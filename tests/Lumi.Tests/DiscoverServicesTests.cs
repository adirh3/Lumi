using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Services.Capabilities;
using Lumi.Services.Discover;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class DiscoverServicesTests
{
    [Theory]
    [InlineData("github/awesome-copilot", "github", "awesome-copilot", null)]
    [InlineData("GitHub: github/copilot-plugins", "github", "copilot-plugins", null)]
    [InlineData("https://github.com/microsoft/azure-skills.git", "microsoft", "azure-skills", null)]
    [InlineData("owner/repo#v1.2", "owner", "repo", "v1.2")]
    public void GitHubRepoRef_ReadsTheFormsMarketplacesAreWrittenIn(string text, string owner, string repo, string? reference)
    {
        var parsed = GitHubRepoRef.TryParse(text);

        Assert.NotNull(parsed);
        Assert.Equal(owner, parsed!.Owner);
        Assert.Equal(repo, parsed.Repo);
        Assert.Equal(reference, parsed.Ref);
    }

    [Fact]
    public void GitHubRepoRef_CombinesRelativeFoldersWithoutEscapingTheRepository()
    {
        var root = new GitHubRepoRef("github", "awesome-copilot", Path: "plugins/azure");

        Assert.Equal("plugins/azure/skills", root.Under("./skills/").Path);
        Assert.Equal("skills/x", root.Under("../../skills/x").Path);
        Assert.Null(GitHubRepoRef.TryParse("not a repo"));
    }

    [Fact]
    public void MarketplaceManifest_ReadsPathAndGitHubSourcesWithTheirDetails()
    {
        const string json = """
            {
              "name": "demo",
              "metadata": { "pluginRoot": "./plugins" },
              "plugins": [
                { "name": "local", "source": "local", "description": "In this repo", "version": "1.0.0" },
                { "name": "explicit", "source": "./elsewhere/explicit" },
                {
                  "name": "remote",
                  "description": "Elsewhere",
                  "author": { "name": "Ada", "url": "https://github.com/ada" },
                  "repository": { "url": "https://github.com/ada/remote" },
                  "keywords": ["one"], "tags": ["two", "one"],
                  "source": { "source": "github", "repo": "ada/remote", "ref": "v2", "path": "plugin" }
                },
                { "source": "nameless" }
              ]
            }
            """;

        var listings = MarketplaceManifest.Parse(json);

        Assert.Equal(["local", "explicit", "remote"], listings.Select(static listing => listing.Name));
        Assert.Equal("plugins/local", listings[0].Source!.RelativePath);
        Assert.Equal("elsewhere/explicit", listings[1].Source!.RelativePath);
        var remote = listings[2];
        Assert.Equal("Ada", remote.Author);
        Assert.Equal("https://github.com/ada", remote.AuthorUrl);
        Assert.Equal("https://github.com/ada/remote", remote.Repository);
        Assert.Equal(["one", "two"], remote.Keywords);
        Assert.Equal(new GitHubRepoRef("ada", "remote", "v2", "plugin"), remote.Source!.Repository);

        var marketplace = new GitHubRepoRef("me", "market");
        Assert.Equal(new GitHubRepoRef("me", "market", Path: "plugins/local"), listings[0].Source!.Resolve(marketplace));
    }

    [Fact]
    public async Task PluginInspector_UsesExplicitListsAndConventionalFolders()
    {
        var files = new Dictionary<string, string>
        {
            ["raw/me/market/HEAD/plugins/demo/plugin.json"] = """
                {
                  "name": "demo",
                  "author": { "name": "Grace" },
                  "extensions": {
                    "com.github.copilot": { "logo": "assets/logo.png" },
                    "com.github.awesome-copilot": {
                      "skills": ["./skills/alpha/", "./skills/beta/"],
                      "agents": ["./agents/reporter.agent.md"]
                    }
                  }
                }
                """,
            ["raw/me/market/HEAD/plugins/demo/.mcp.json"] = """
                { "mcpServers": { "files": { "command": "npx", "args": ["-y", "files"] }, "web": { "url": "https://example.com/mcp" } } }
                """,
            ["raw/me/market/HEAD/plugins/demo/hooks/hooks.json"] = "{}",
        };
        var inspector = new PluginInspector(new GitHubRepoReader(new HttpClient(new FakeGitHub(files)), static () => null));

        var contents = await inspector.InspectAsync(new GitHubRepoRef("me", "market", Path: "plugins/demo"), CancellationToken.None);

        Assert.Equal(["alpha", "beta"], contents.Skills);
        Assert.Equal(["reporter"], contents.Agents);
        Assert.Equal(["files", "web"], contents.McpServers.Select(static server => server.Name));
        Assert.Equal("npx -y files", contents.McpServers[0].Summary);
        Assert.Equal("https://example.com/mcp", contents.McpServers[1].Summary);
        Assert.True(contents.HasHooks);
        Assert.Equal("Grace", contents.Author);
        Assert.Equal("https://raw.githubusercontent.com/me/market/HEAD/plugins/demo/assets/logo.png", contents.LogoUrl);
    }

    [Fact]
    public async Task PluginInspector_ListsASkillsFolderWhenTheManifestPointsAtOne()
    {
        var files = new Dictionary<string, string>
        {
            ["raw/ms/azure/HEAD/.plugin/plugin.json"] = """{ "name": "azure", "skills": "./skills/", "mcpServers": "./.mcp.json" }""",
            ["raw/ms/azure/HEAD/.mcp.json"] = """{ "mcpServers": { "azure": { "command": "npx", "args": ["-y", "@azure/mcp@latest"] } } }""",
            ["api/ms/azure/skills"] = """[ { "name": "deploy", "type": "dir" }, { "name": "README.md", "type": "file" }, { "name": "diagnose", "type": "dir" } ]""",
        };
        var inspector = new PluginInspector(new GitHubRepoReader(new HttpClient(new FakeGitHub(files)), static () => null));

        var contents = await inspector.InspectAsync(new GitHubRepoRef("ms", "azure"), CancellationToken.None);

        Assert.Equal(["deploy", "diagnose"], contents.Skills);
        Assert.Equal("azure", Assert.Single(contents.McpServers).Name);
        Assert.False(contents.HasHooks);
    }

    [Fact]
    public void McpRegistry_ParsesWrappedEntriesWithGitHubDetails()
    {
        const string json = """
            {
              "servers": [
                {
                  "server": {
                    "name": "microsoftdocs/mcp",
                    "description": "Docs",
                    "version": "1.0.0",
                    "repository": { "url": "https://github.com/MicrosoftDocs/mcp" },
                    "remotes": [ { "type": "sse", "url": "https://learn.microsoft.com/api/mcp" } ],
                    "_meta": { "io.modelcontextprotocol.registry/publisher-provided": { "github": {
                      "displayName": "Microsoft Learn", "ownerAvatarUrl": "https://avatars.githubusercontent.com/u/1?v=4",
                      "stargazerCount": 1940, "license": "MIT", "readme": "# Hi" } } }
                  },
                  "_meta": { "io.modelcontextprotocol.registry/official": { "status": "active", "isLatest": true } }
                },
                { "server": { "name": "gone/server" }, "_meta": { "io.modelcontextprotocol.registry/official": { "status": "deleted" } } },
                { "server": { "name": "io.github.someone/weather-mcp-server", "version": "2.0.0" } }
              ],
              "metadata": { "nextCursor": "abc", "count": 3, "total": 426 }
            }
            """;

        var page = McpRegistryClient.ParsePage(json);

        Assert.Equal("abc", page.NextCursor);
        Assert.Equal(426, page.Total);
        Assert.Equal(2, page.Servers.Count);
        var learn = page.Servers[0];
        Assert.Equal("Microsoft Learn", learn.Title);
        Assert.Equal("MicrosoftDocs", learn.Publisher);
        Assert.Equal(1940, learn.Stars);
        Assert.Equal("MIT", learn.License);
        Assert.Equal("# Hi", learn.Readme);
        Assert.Equal("Weather", page.Servers[1].Title);
        Assert.Equal("someone", page.Servers[1].Publisher);
    }

    [Theory]
    [InlineData("microsoftdocs/mcp", "Microsoftdocs")]
    [InlineData("io.github.acme/mcp-server-sql-tools", "SQL Tools")]
    [InlineData("com.example/ai-helper", "AI Helper")]
    public void McpRegistry_FriendlyNamesComeFromTheMeaningfulPart(string name, string expected)
        => Assert.Equal(expected, McpRegistryClient.FriendlyName(name));

    [Fact]
    public void McpMapper_PrefersAHostedEndpoint()
    {
        var server = Server(
            packages: [Package("npm", "@acme/mcp")],
            remotes: [new McpRegistryRemote("streamable-http", "https://acme.example/mcp", [])]);

        var plan = McpRegistryMapper.Plan(server, static _ => true)!;

        Assert.Equal("remote", plan.Runtime);
        Assert.Equal("remote", plan.Server.ServerType);
        Assert.Equal("https://acme.example/mcp", plan.Server.Url);
        Assert.Equal("acme/server", plan.Server.CatalogId);
        Assert.False(plan.NeedsSetup);
    }

    [Fact]
    public void McpMapper_RunsAnNpmPackageThroughNpxAndAsksForRequiredValues()
    {
        var package = Package(
            "npm",
            "@acme/mcp",
            version: "1.2.3",
            packageArguments: [new McpRegistryArgument("positional", null, "{org}", null, true, null, [new McpRegistryInput("org", "Org name", true, false, null)])],
            environment: [new McpRegistryInput("ACME_TOKEN", "Token", true, true, null), new McpRegistryInput("ACME_REGION", null, false, false, "eu")]);

        var plan = McpRegistryMapper.Plan(Server(packages: [package]), static _ => true)!;

        Assert.Equal(OperatingSystem.IsWindows() ? "npx.cmd" : "npx", plan.Server.Command);
        Assert.Equal(["-y", "@acme/mcp@1.2.3", "{org}"], plan.Server.Args);
        Assert.Equal("", plan.Server.Env["ACME_TOKEN"]);
        Assert.Equal("eu", plan.Server.Env["ACME_REGION"]);
        Assert.True(plan.NeedsSetup);
        Assert.Equal(["org", "ACME_TOKEN"], plan.RequiredInputs.Select(static input => input.Name));
    }

    [Fact]
    public void McpMapper_UsesUvxWithItsRuntimeArgumentsForPythonPackages()
    {
        var package = Package(
            "pypi",
            "terravision",
            version: "0.53.0",
            runtimeArguments: [new McpRegistryArgument("named", "--from", "terravision[mcp]", null, false, null, [])],
            packageArguments: [new McpRegistryArgument("positional", null, "mcp", null, false, null, [])]);

        var plan = McpRegistryMapper.Plan(Server(packages: [package]), static _ => true)!;

        Assert.Equal("uvx", plan.Server.Command);
        Assert.Equal(["--from", "terravision[mcp]", "terravision", "mcp"], plan.Server.Args);
        Assert.False(plan.NeedsSetup);
    }

    [Fact]
    public void McpMapper_PicksAPackageWhoseRunnerIsInstalled()
    {
        var plan = McpRegistryMapper.Plan(
            Server(packages: [Package("npm", "@acme/mcp"), Package("oci", "ghcr.io/acme/mcp", version: "2")]),
            static command => command == "docker")!;

        Assert.Equal("docker", plan.Server.Command);
        Assert.Equal(["run", "-i", "--rm", "ghcr.io/acme/mcp:2"], plan.Server.Args);
    }

    [Fact]
    public void McpMapper_AsksForKeysAnEnvironmentTemplateNeedsAndLeavesOutOptionalOnes()
    {
        // The shape registries publish for servers such as Firecrawl and Chroma: the template's
        // variables, not the environment variable itself, say what is required.
        const string json = """
            { "servers": [ { "server": {
              "name": "acme/server",
              "packages": [ {
                "registryType": "npm", "identifier": "acme-mcp", "transport": { "type": "stdio" },
                "environmentVariables": [
                  { "name": "ACME_API_KEY", "description": "API key", "value": "{api_key}",
                    "variables": { "api_key": { "isRequired": true, "isSecret": true } } },
                  { "name": "ACME_CLIENT", "value": "{client}", "variables": { "client": { "description": "Client type" } } },
                  { "name": "ACME_LEVEL", "value": "{level}", "variables": { "level": { "default": "info" } } }
                ]
              } ]
            } } ] }
            """;

        var plan = McpRegistryMapper.Plan(Assert.Single(McpRegistryClient.ParsePage(json).Servers), static _ => true)!;

        Assert.Equal("{api_key}", plan.Server.Env["ACME_API_KEY"]);
        Assert.False(plan.Server.Env.ContainsKey("ACME_CLIENT"));
        Assert.Equal("info", plan.Server.Env["ACME_LEVEL"]);
        Assert.True(plan.NeedsSetup);
        var key = Assert.Single(plan.RequiredInputs);
        Assert.Equal("ACME_API_KEY", key.Name);
        Assert.True(key.IsSecret);
    }

    [Fact]
    public void McpMapper_KeepsAHeaderTemplateWhoseVariableIsRequired()
    {
        var remote = new McpRegistryRemote("streamable-http", "https://shots.example/mcp",
        [
            new McpRegistryInput("Authorization", null, false, false, "Bearer {access_key}")
            {
                Variables = [new McpRegistryInput("access_key", "Access key", true, true, null)],
            },
            new McpRegistryInput("X-Region", null, false, false, "{region}")
            {
                Variables = [new McpRegistryInput("region", null, false, false, null)],
            },
        ]);

        var plan = McpRegistryMapper.Plan(Server(remotes: [remote]), static _ => true)!;

        Assert.Equal("Bearer {access_key}", plan.Server.Headers["Authorization"]);
        Assert.False(plan.Server.Headers.ContainsKey("X-Region"));
        Assert.True(plan.NeedsSetup);
        Assert.Equal("Authorization", Assert.Single(plan.RequiredInputs).Name);
    }

    [Theory]
    [InlineData("ai-ready", "AI Ready")]
    [InlineData("atlassian-twg-cli", "Atlassian TWG CLI")]
    [InlineData("github-copilot-dotnet", "GitHub Copilot .NET")]
    [InlineData("Already Named", "Already Named")]
    public void DisplayNames_HumanizeSlugsWithAcronyms(string slug, string expected)
        => Assert.Equal(expected, DisplayNames.Humanize(slug));

    [Theory]
    [InlineData("1.2.88", "1.2.87", true)]
    [InlineData("1.0.0", "1.2.87", false)]
    [InlineData("2.0", "1.9.9", true)]
    [InlineData("1.2.0", "1.2.0-beta", true)]
    [InlineData("1.2.0-beta", "1.2.0", false)]
    [InlineData("v1.3", "1.2", true)]
    [InlineData("latest", "1.0.0", false)]
    [InlineData("1.0.0", null, false)]
    public void DisplayNames_OfferOnlyProvablyNewerVersions(string? candidate, string? current, bool expected)
        => Assert.Equal(expected, DisplayNames.IsNewerVersion(candidate, current));

    [Fact]
    public void PluginInstallLocator_ReadsTheRuntimeConfigAndFallsBackToItsLayout()
    {
        var home = Path.Combine(Path.GetTempPath(), $"lumi-plugin-home-{Guid.NewGuid():N}");
        var recorded = Path.Combine(home, "custom", "azure");
        var conventional = Path.Combine(home, "installed-plugins", "copilot-plugins", "workiq");
        Directory.CreateDirectory(recorded);
        Directory.CreateDirectory(conventional);
        File.WriteAllText(Path.Combine(home, "config.json"), $$"""
            // User settings belong in settings.json.
            {
              "installedPlugins": [
                { "name": "azure", "marketplace": "awesome-copilot", "cache_path": {{System.Text.Json.JsonSerializer.Serialize(recorded)}} },
              ]
            }
            """);

        try
        {
            var locations = PluginInstallLocator.Read(home);

            Assert.Equal(recorded, PluginInstallLocator.Resolve(locations, home, "azure", "awesome-copilot"));
            Assert.Equal(conventional, PluginInstallLocator.Resolve(locations, home, "workiq", "copilot-plugins"));
            Assert.Null(PluginInstallLocator.Resolve(locations, home, "missing", "awesome-copilot"));
            Assert.Empty(PluginInstallLocator.Read(Path.Combine(home, "nowhere")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData("plugin", "azure:deploy", null, "azure")]
    [InlineData("plugin", "deploy", @"C:\home\installed-plugins\awesome-copilot\azure\skills\deploy\SKILL.md", "azure")]
    [InlineData("personal-copilot", "azure:deploy", null, null)]
    [InlineData("plugin", "deploy", null, null)]
    public void CapabilityProvider_AttributesPartsToTheirPlugin(string source, string name, string? path, string? expected)
        => Assert.Equal(expected, CopilotSdkCapabilityProvider.ResolvePluginName(source, name, path));

    [Fact]
    public void Discover_CleansReadmesForThePreview()
    {
        const string readme = """
            # Title
            [![build](https://img.shields.io/x.svg)](https://ci) ![logo](logo.png)
            <p align="center">Hello</p>
            <!-- hidden -->


            Body text.

            ```json title="config"
            { "servers": {} }
            ```

            ```mermaid
            graph LR
            ```
            """;

        var cleaned = DiscoverViewModel.CleanReadme(readme, int.MaxValue);

        Assert.DoesNotContain("shields.io", cleaned);
        Assert.DoesNotContain("<p", cleaned);
        Assert.DoesNotContain("hidden", cleaned);
        Assert.Contains("Hello", cleaned);
        Assert.DoesNotContain("\n\n\n", cleaned);
        // Code stays in its blocks without a language to highlight; diagrams keep theirs.
        Assert.Contains("```\n{ \"servers\": {} }\n```", cleaned);
        Assert.Contains("```mermaid\ngraph LR\n```", cleaned);
        Assert.EndsWith("…", DiscoverViewModel.CleanReadme(new string('a', 50) + "\n\n" + new string('b', 50), 60));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  github ", "github")]
    [InlineData("microsoft learn", "microsoft")]
    public void Discover_SendsTheRegistryItsOnePhrase(string? query, string? expected)
        => Assert.Equal(expected, DiscoverViewModel.RegistrySearchTerm(query));

    private static McpRegistryServer Server(
        IReadOnlyList<McpRegistryPackage>? packages = null,
        IReadOnlyList<McpRegistryRemote>? remotes = null)
        => new("acme/server", "Acme", "Does things", "1.0.0", "acme", null, null, null, null, null, null,
            packages ?? [], remotes ?? []);

    private static McpRegistryPackage Package(
        string registryType,
        string identifier,
        string? version = null,
        IReadOnlyList<McpRegistryArgument>? runtimeArguments = null,
        IReadOnlyList<McpRegistryArgument>? packageArguments = null,
        IReadOnlyList<McpRegistryInput>? environment = null)
        => new(registryType, identifier, version, "stdio", runtimeArguments ?? [], packageArguments ?? [], environment ?? []);

    /// <summary>Serves raw.githubusercontent.com files and api.github.com folder listings from memory.</summary>
    private sealed class FakeGitHub(IReadOnlyDictionary<string, string> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var key = uri.Host switch
            {
                "raw.githubusercontent.com" => "raw" + Uri.UnescapeDataString(uri.AbsolutePath),
                "api.github.com" => "api/" + Uri.UnescapeDataString(uri.AbsolutePath)["/repos/".Length..].Replace("/contents", "", StringComparison.Ordinal),
                _ => "",
            };

            return Task.FromResult(files.TryGetValue(key.TrimEnd('/'), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
