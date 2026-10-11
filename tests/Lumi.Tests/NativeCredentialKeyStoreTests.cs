using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Services.Byok;
using Xunit;
using NativePlatform = Lumi.Services.Byok.NativeCredentialKeyStore.NativePlatform;
using ProcessResult = Lumi.Services.Byok.NativeCredentialKeyStore.ProcessResult;

namespace Lumi.Tests;

public sealed class NativeCredentialKeyStoreTests
{
    [Theory]
    [InlineData((int)NativePlatform.MacOS, true, true)]
    [InlineData((int)NativePlatform.Linux, true, true)]
    [InlineData((int)NativePlatform.MacOS, false, false)]
    [InlineData((int)NativePlatform.Linux, false, false)]
    [InlineData((int)NativePlatform.Unsupported, true, false)]
    public void IsSupported_RequiresPlatformAndExecutable(int platformKind, bool available, bool supported)
    {
        var platform = (NativePlatform)platformKind;
        var runner = new RecordingRunner();
        var store = new NativeCredentialKeyStore(platform, _ => available ? "/test/native-tool" : null, runner.Run);
        Assert.Equal(supported, store.IsSupported);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Linux_UsesArgumentListAndExactStdin_ForStoreReadAndDelete()
    {
        var runner = new RecordingRunner();
        var store = Create(NativePlatform.Linux, runner);
        const string secret = "api-key-with-trailing-space ";
        await store.SetAsync("Endpoint.123.ApiKey", secret);
        var write = Assert.Single(runner.Commands);
        Assert.Equal(new[] { "store", "--label=Lumi BYOK API key", "service", "Lumi.Byok", "account", "Lumi.Byok.Endpoint.123.ApiKey" },
            write.Info.ArgumentList);
        Assert.Equal(secret, write.Input);
        AssertSafeProcessInfo(write.Info, secret);

        runner.Result = new ProcessResult(0, secret);
        Assert.Equal(secret, await store.GetAsync("Endpoint.123.ApiKey"));
        Assert.Equal(new[] { "lookup", "service", "Lumi.Byok", "account", "Lumi.Byok.Endpoint.123.ApiKey" },
            runner.Commands[1].Info.ArgumentList);
        Assert.Null(runner.Commands[1].Input);
        runner.Result = new ProcessResult(0);
        await store.DeleteAsync("Endpoint.123.ApiKey");
        Assert.Equal(new[] { "clear", "service", "Lumi.Byok", "account", "Lumi.Byok.Endpoint.123.ApiKey" },
            runner.Commands[2].Info.ArgumentList);
    }

    [Fact]
    public async Task MacOS_UsesOneQuotedStdinCommand_WithoutSecretInProcessArguments()
    {
        var runner = new RecordingRunner();
        var store = Create(NativePlatform.MacOS, runner);
        const string secret = "api\"key\\tail;$HOME";
        await store.SetAsync("Endpoint.123.ApiKey", secret);
        var write = Assert.Single(runner.Commands);
        Assert.Equal(new[] { "-i", "-q" }, write.Info.ArgumentList);
        var input = Assert.IsType<string>(write.Input);
        Assert.Equal("add-generic-password -U -s \"Lumi.Byok\" -a \"Lumi.Byok.Endpoint.123.ApiKey\" -w \"api\\\"key\\\\tail;$HOME\"\n",
            input);
        Assert.Equal(1, input.Count(ch => ch == '\n'));
        Assert.DoesNotContain('\r', input);
        AssertSafeProcessInfo(write.Info, secret);

        runner.Result = new ProcessResult(0, secret + "\n");
        Assert.Equal(secret, await store.GetAsync("Endpoint.123.ApiKey"));
        Assert.Equal(new[] { "find-generic-password", "-s", "Lumi.Byok", "-a", "Lumi.Byok.Endpoint.123.ApiKey", "-w" },
            runner.Commands[1].Info.ArgumentList);
        runner.Result = new ProcessResult(0);
        await store.DeleteAsync("Endpoint.123.ApiKey");
        Assert.Equal(new[] { "delete-generic-password", "-s", "Lumi.Byok", "-a", "Lumi.Byok.Endpoint.123.ApiKey" },
            runner.Commands[2].Info.ArgumentList);
    }

    [Fact]
    public async Task MacOS_QuotesCredentialIdentity_WithoutCreatingAnotherCommand()
    {
        var runner = new RecordingRunner();
        var store = Create(NativePlatform.MacOS, runner);
        await store.SetAsync("Endpoint.\"quoted\\id", "api-key");
        Assert.Contains("-a \"Lumi.Byok.Endpoint.\\\"quoted\\\\id\"", Assert.Single(runner.Commands).Input);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("Endpoint.id\nanother-command", "api-key"));
        Assert.Single(runner.Commands);
    }

    [Theory]
    [InlineData((int)NativePlatform.MacOS, 44)]
    [InlineData((int)NativePlatform.Linux, 1)]
    public async Task MissingEntry_ReadReturnsNull_DeleteIsIdempotent(int platformKind, int exit)
    {
        var platform = (NativePlatform)platformKind;
        var runner = new RecordingRunner { Result = new ProcessResult(exit) };
        var store = Create(platform, runner);
        Assert.Null(await store.GetAsync("Endpoint.missing.ApiKey"));
        await store.DeleteAsync("Endpoint.missing.ApiKey");
        Assert.Equal(2, runner.Commands.Count);
    }

    [Theory]
    [InlineData((int)NativePlatform.MacOS, 44, null)]
    [InlineData((int)NativePlatform.MacOS, 44, "")]
    [InlineData((int)NativePlatform.Linux, 1, null)]
    [InlineData((int)NativePlatform.Linux, 1, "")]
    public async Task NullOrEmptySecret_DeletesInsteadOfStoring(int platformKind, int missingExit, string? secret)
    {
        var platform = (NativePlatform)platformKind;
        var runner = new RecordingRunner { Result = new ProcessResult(missingExit) };
        var store = Create(platform, runner);
        await store.SetAsync("Endpoint.123.ApiKey", secret);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(platform == NativePlatform.MacOS ? "delete-generic-password" : "clear", command.Info.ArgumentList[0]);
        Assert.Null(command.Input);
    }

    [Theory]
    [InlineData((int)NativePlatform.MacOS, 1)]
    [InlineData((int)NativePlatform.Linux, 1)]
    public async Task RealNativeErrors_AreNotMistakenForMissingEntries_AndAreSanitized(int platformKind, int exit)
    {
        var platform = (NativePlatform)platformKind;
        var runner = new RecordingRunner { Result = new ProcessResult(exit, "private-key", "private-native-error") };
        var store = Create(platform, runner);
        var readError = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("Endpoint.123.ApiKey"));
        var writeError = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync("Endpoint.123.ApiKey", "private-input"));
        var deleteError = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync("Endpoint.123.ApiKey"));
        foreach (var error in new[] { readError, writeError, deleteError })
        {
            Assert.Contains("try again", error.Message);
            Assert.DoesNotContain("private", error.ToString());
        }
        Assert.DoesNotContain("private", runner.Result.ToString());
    }

    [Theory]
    [InlineData((int)NativePlatform.MacOS, 44)]
    [InlineData((int)NativePlatform.Linux, 1)]
    public async Task FailedWrite_DoesNotTreatMissingExitAsSuccess(int platformKind, int exit)
    {
        var platform = (NativePlatform)platformKind;
        var runner = new RecordingRunner { Result = new ProcessResult(exit) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(platform, runner).SetAsync("Endpoint.123.ApiKey", "api-key"));
    }

    [Fact]
    public async Task UnavailableStorage_ReportsUnsupported_AndCannotPretendToSave()
    {
        var runner = new RecordingRunner();
        var store = new NativeCredentialKeyStore(NativePlatform.Linux, _ => null, runner.Run);
        Assert.False(store.IsSupported);
        Assert.Null(await store.GetAsync("Endpoint.123.ApiKey"));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.SetAsync("Endpoint.123.ApiKey", "api-key"));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.DeleteAsync("Endpoint.123.ApiKey"));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task NativeTimeout_IsBoundedAndActionable()
    {
        CancellationToken processCancellation = default;
        var store = new NativeCredentialKeyStore(NativePlatform.Linux, _ => "/test/secret-tool",
            async (_, _, token) =>
            {
                processCancellation = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ProcessResult(0);
            }, TimeSpan.FromMilliseconds(100));
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            store.SetAsync("Endpoint.123.ApiKey", "private-key").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(processCancellation.IsCancellationRequested);
        Assert.Contains("Unlock", error.Message);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData("api\nkey")]
    [InlineData("api\rkey")]
    [InlineData("api\0key")]
    [InlineData("api-\u00e9-key")]
    public async Task NonPrintableSecrets_AreRejectedWithoutStartingAProcess(string secret)
    {
        var runner = new RecordingRunner();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(NativePlatform.MacOS, runner).SetAsync("Endpoint.123.ApiKey", secret));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task MacOS_OverlongInteractiveCommand_IsRejectedRatherThanTruncated()
    {
        var runner = new RecordingRunner();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(NativePlatform.MacOS, runner).SetAsync("Endpoint.123.ApiKey", new string('"', 2048)));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void Factory_ChoosesThePlatformStore()
    {
        if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsCredentialKeyStore>(SecureKeyStoreFactory.Instance);
        else
            Assert.IsType<NativeCredentialKeyStore>(SecureKeyStoreFactory.Instance);
    }

    private static NativeCredentialKeyStore Create(NativePlatform platform, RecordingRunner runner)
        => new(platform, executable => platform == NativePlatform.MacOS ? executable : "/test/secret-tool", runner.Run);

    private static void AssertSafeProcessInfo(ProcessStartInfo info, string secret)
    {
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Empty(info.Arguments);
        Assert.DoesNotContain(secret, string.Join(" ", info.ArgumentList));
    }

    private sealed class RecordingRunner
    {
        public ProcessResult Result { get; set; } = new(0);
        public List<(ProcessStartInfo Info, string? Input)> Commands { get; } = [];
        public Task<ProcessResult> Run(ProcessStartInfo info, string? input, CancellationToken cancellationToken)
        {
            Commands.Add((info, input));
            Assert.True(cancellationToken.CanBeCanceled);
            return Task.FromResult(Result);
        }
    }
}
