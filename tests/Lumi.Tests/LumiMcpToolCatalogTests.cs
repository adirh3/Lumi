using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Lumi.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace Lumi.Tests;

public sealed class LumiMcpToolCatalogTests
{
    [Fact]
    public void OfficialSdkToolType_ContainsCoreLumiWorkflowTools()
    {
        var toolNames = typeof(LumiMcpTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet();

        Assert.Contains("lumi_launch", toolNames);
        Assert.Contains("lumi_create_chat", toolNames);
        Assert.Contains("lumi_open_chat", toolNames);
        Assert.Contains("lumi_send_message", toolNames);
        Assert.Contains("lumi_read_transcript", toolNames);
        Assert.Contains("lumi_read_activity", toolNames);
        Assert.Contains("lumi_list_instances", toolNames);
        Assert.Contains("lumi_configure_feature", toolNames);
        Assert.Contains("lumi_run_harness", toolNames);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void LaunchReuseCompatibility_MatchesRequestedOnboardingMode(
        bool showOnboarding,
        bool isOnboarded,
        bool expected)
    {
        var status = new
        {
            bridgeAvailable = true,
            status = new
            {
                app = new
                {
                    isOnboarded
                }
            }
        };
        var method = typeof(LumiMcpToolHandlers).GetMethod(
            "IsExistingLaunchCompatible",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var actual = Assert.IsType<bool>(method.Invoke(null, [status, showOnboarding]));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task OfficialSdkStdioServer_ListsLumiTools()
    {
        var serverAssemblyPath = FindRunnableMcpAssembly(typeof(LumiMcpTools).Assembly.Location);
        Assert.True(File.Exists(serverAssemblyPath));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{serverAssemblyPath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start());
        try
        {
            await WriteJsonLineAsync(process, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            var initialize = await ReadJsonLineAsync(process);
            using (var initializeDocument = JsonDocument.Parse(initialize))
                Assert.Equal("init", initializeDocument.RootElement.GetProperty("id").GetString());

            await WriteJsonLineAsync(process, """
                {"jsonrpc":"2.0","method":"notifications/initialized","params":{}}
                """);
            await WriteJsonLineAsync(process, """
                {"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}
                """);
            var list = await ReadJsonLineAsync(process);

            using var document = JsonDocument.Parse(list);
            var tools = document.RootElement
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToHashSet();

            Assert.Contains("lumi_status", tools);
            Assert.Contains("lumi_list_instances", tools);
            Assert.Contains("lumi_load_fixture", tools);
            Assert.Contains("lumi_read_activity", tools);

            var toolByName = document.RootElement
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .ToDictionary(
                    tool => tool.GetProperty("name").GetString()!,
                    tool => tool);
            var activityProperties = toolByName["lumi_read_activity"]
                .GetProperty("inputSchema")
                .GetProperty("properties");
            Assert.True(activityProperties.TryGetProperty("sections", out _));
            Assert.True(activityProperties.TryGetProperty("roles", out _));
            Assert.True(activityProperties.TryGetProperty("toolNames", out _));
            Assert.True(activityProperties.TryGetProperty("query", out _));
            Assert.True(activityProperties.TryGetProperty("maxContentChars", out _));

            var launchProperties = toolByName["lumi_launch"]
                .GetProperty("inputSchema")
                .GetProperty("properties");
            Assert.True(launchProperties.TryGetProperty("targetAppDataDir", out _));
            Assert.True(launchProperties.TryGetProperty("showOnboarding", out _));

            var instanceProperties = toolByName["lumi_list_instances"]
                .GetProperty("inputSchema")
                .GetProperty("properties");
            Assert.True(instanceProperties.TryGetProperty("appDataDir", out _));

            var listChatProperties = toolByName["lumi_list_chats"]
                .GetProperty("inputSchema")
                .GetProperty("properties");
            Assert.True(listChatProperties.TryGetProperty("projectName", out _));
            Assert.True(listChatProperties.TryGetProperty("sortBy", out _));
            Assert.True(listChatProperties.TryGetProperty("offset", out _));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static async Task WriteJsonLineAsync(Process process, string json)
    {
        await process.StandardInput.WriteLineAsync(json.Trim());
        await process.StandardInput.FlushAsync();
    }

    private static async Task<string> ReadJsonLineAsync(Process process)
    {
        var readTask = process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed == readTask && await readTask is { } line)
            return line;

        throw new TimeoutException("Timed out waiting for MCP server JSON response.");
    }

    private static string FindRunnableMcpAssembly(string referencedAssemblyPath)
    {
        foreach (var candidate in GetMcpAssemblyCandidates(referencedAssemblyPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
                continue;

            var runtimeConfigPath = Path.ChangeExtension(candidate, ".runtimeconfig.json");
            if (File.Exists(runtimeConfigPath))
                return candidate;
        }

        throw new FileNotFoundException("Could not find a runnable Lumi.Mcp project output assembly.", referencedAssemblyPath);
    }

    private static IEnumerable<string> GetMcpAssemblyCandidates(string referencedAssemblyPath)
    {
        var fileName = Path.GetFileName(referencedAssemblyPath);
        var repoRoot = FindRepositoryRoot();
        foreach (var configuration in GetBuildConfigurations(referencedAssemblyPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(repoRoot, "src", "Lumi.Mcp", "bin", configuration, "net11.0", fileName);
        }
    }

    private static IEnumerable<string> GetBuildConfigurations(string referencedAssemblyPath)
    {
        var directory = Path.GetDirectoryName(referencedAssemblyPath);
        while (directory is not null)
        {
            var info = new DirectoryInfo(directory);
            if (string.Equals(info.Parent?.Name, "bin", StringComparison.OrdinalIgnoreCase))
                yield return info.Name;

            directory = info.Parent?.FullName;
        }

        yield return "Release";
        yield return "Debug";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Lumi repository root.");
    }
}
