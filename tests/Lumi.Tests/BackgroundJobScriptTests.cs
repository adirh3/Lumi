using System.Reflection;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobScriptTests
{
    [Fact]
    public async Task RunScriptTriggerAsync_ProgressSaveFailure_TerminatesProcess()
    {
        var store = new DataStore(new AppData());
        using var chatViewModel = new ChatViewModel(store, TestCopilot.Shared);
        using var service = new BackgroundJobService(store, chatViewModel);
        service.JobsChanged += static () => throw new IOException("Simulated progress save failure.");

        var markerPath = Path.Combine(Path.GetTempPath(), $"lumi-job-orphan-{Guid.NewGuid():N}.txt");
        var job = new BackgroundJob
        {
            Name = "Save failure test",
            TriggerType = BackgroundJobTriggerTypes.Script,
            ScriptLanguage = BackgroundJobScriptLanguages.Command,
            ScriptContent = CreateDelayedMarkerScript(markerPath)
        };

        try
        {
            await Assert.ThrowsAsync<IOException>(() => InvokeRunScriptTriggerAsync(service, job));
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    private static async Task InvokeRunScriptTriggerAsync(
        BackgroundJobService service,
        BackgroundJob job)
    {
        var method = typeof(BackgroundJobService).GetMethod(
            "RunScriptTriggerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RunScriptTriggerAsync was not found.");

        var task = (Task)(method.Invoke(service, [job, DateTimeOffset.Now, CancellationToken.None])
            ?? throw new InvalidOperationException("RunScriptTriggerAsync returned null."));

        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string CreateDelayedMarkerScript(string markerPath)
    {
        if (OperatingSystem.IsWindows())
            return $"@echo off\r\nping 127.0.0.1 -n 3 >nul\r\necho completed>\"{markerPath}\"";

        var escapedPath = markerPath.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        return $"sleep 2\nprintf completed > '{escapedPath}'";
    }
}
