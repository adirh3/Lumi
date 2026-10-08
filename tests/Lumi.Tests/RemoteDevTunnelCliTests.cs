using System.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteDevTunnelCliTests
{
    private const string MicrosoftIdentity =
        """{"status":"Logged in","provider":"microsoft","username":"owner@example.com","objectId":"owner-id","tenantId":"tenant-id"}""";
    private static readonly Uri OfficialSource = RemoteDevTunnelCli.GetDownloadUri("windows", Architecture.X64);

    [Fact]
    public void SignInAllowsTimeForMicrosoftAccountSelectionAndAuthenticatorApproval() =>
        Assert.Equal(TimeSpan.FromMinutes(15), RemoteDevTunnelCli.SignInTimeout);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCliWaitsForExplicitConsentBeforeInstalling(bool approve)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installs = 0;
        var operation = RemoteDevTunnelCli.ResolveWithConsentAsync(
            null,
            token => decision.Task.WaitAsync(token),
            _ => { installs++; return Task.FromResult("verified-cli"); },
            CancellationToken.None);
        Assert.False(operation.IsCompleted);
        Assert.Equal(0, installs);

        decision.SetResult(approve);
        Assert.Equal(approve ? "verified-cli" : null, await operation);
        Assert.Equal(approve ? 1 : 0, installs);
    }

    [Fact]
    public async Task ExistingCliSkipsBothConsentAndInstallation()
    {
        var resolved = await RemoteDevTunnelCli.ResolveWithConsentAsync(
            "existing-cli",
            _ => throw new InvalidOperationException("Existing CLI must not prompt."),
            _ => throw new InvalidOperationException("Existing CLI must not reinstall."),
            CancellationToken.None);
        Assert.Equal("existing-cli", resolved);
    }

    [Fact]
    public async Task CanceledPendingConsentCannotInstallAfterALateApproval()
    {
        using var cancellation = new CancellationTokenSource();
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installs = 0;
        var operation = RemoteDevTunnelCli.ResolveWithConsentAsync(
            null, token => decision.Task.WaitAsync(token),
            _ => { installs++; return Task.FromResult("cli"); },
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        decision.SetResult(true);
        Assert.Equal(0, installs);
    }

    [Fact]
    public void ExistingPortableOrPathCliTakesPrecedenceOverTheManagedCache()
    {
        using var files = new Files();
        var name = OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel";
        var portable = files.Create("portable", name);
        var installed = files.Create("installed", name);
        var cached = files.Create("cache", name);
        var path = Path.GetDirectoryName(installed)!;
        Assert.Equal(portable, RemoteDevTunnelCli.FindExistingCli(portable, cached, path));
        File.Delete(portable);
        Assert.Equal(installed, RemoteDevTunnelCli.FindExistingCli(portable, cached, path));
        Assert.Equal(cached, RemoteDevTunnelCli.FindExistingCli(portable, cached, ""));
        File.Delete(cached);
        Assert.Null(RemoteDevTunnelCli.FindExistingCli(portable, cached, ""));
    }

    [Theory]
    [InlineData("windows", Architecture.X64, "devtunnel.exe")]
    [InlineData("windows", Architecture.Arm64, "devtunnel.exe")]
    [InlineData("macos", Architecture.X64, "osx-x64-devtunnel")]
    [InlineData("macos", Architecture.Arm64, "osx-arm64-devtunnel")]
    [InlineData("linux", Architecture.X64, "linux-x64-devtunnel")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64-devtunnel")]
    public void PlatformDownloadsUseOnlyTheOfficialMicrosoftDistribution(
        string platform, Architecture architecture, string fileName)
    {
        var uri = RemoteDevTunnelCli.GetDownloadUri(platform, architecture);
        Assert.True(RemoteDevTunnelCli.IsOfficialDownloadUri(uri));
        Assert.EndsWith("/cli/" + fileName, uri.AbsoluteUri);
    }

    [Fact]
    public void UnsupportedArchitectureDoesNotDownloadAnIncompatibleBinary() =>
        Assert.Throws<PlatformNotSupportedException>(() =>
            RemoteDevTunnelCli.GetDownloadUri("linux", Architecture.X86));

    [Theory]
    [InlineData("http://tunnelsassetsprod.blob.core.windows.net/cli/devtunnel.exe")]
    [InlineData("https://other.blob.core.windows.net/cli/devtunnel.exe")]
    [InlineData("https://tunnelsassetsprod.blob.core.windows.net/cli/other.exe")]
    [InlineData("https://tunnelsassetsprod.blob.core.windows.net/cli/devtunnel.exe?override=1")]
    [InlineData("https://user@tunnelsassetsprod.blob.core.windows.net/cli/devtunnel.exe")]
    [InlineData("/cli/devtunnel.exe")]
    public void UnexpectedDownloadLocationsAreRejected(string value) =>
        Assert.False(RemoteDevTunnelCli.IsOfficialDownloadUri(new Uri(value, UriKind.RelativeOrAbsolute)));

    [Fact]
    public async Task DownloadIsPublishedOnlyAfterVerification()
    {
        using var files = new Files();
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        });
        var destination = Path.Combine(files.Root, "cache", "devtunnel");
        var verified = false;
        var progress = new List<string>();
        await RemoteDevTunnelCli.InstallAsync(
            OfficialSource, destination, client,
            async (temporary, token) =>
            {
                Assert.False(File.Exists(destination));
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(temporary, token));
                verified = true;
            },
            progress.Add, CancellationToken.None);
        Assert.True(verified);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(2, progress.Count);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(destination)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrCanceledVerificationLeavesNoInstalledOrPartialCli(bool cancel)
    {
        using var files = new Files();
        using var cancellation = new CancellationTokenSource();
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        });
        var destination = Path.Combine(files.Root, "cache", "devtunnel");
        Task Verify(string path, CancellationToken token)
        {
            Assert.True(File.Exists(path));
            if (cancel)
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }
            throw new InvalidOperationException("Signature rejected.");
        }
        var operation = RemoteDevTunnelCli.InstallAsync(
            OfficialSource, destination, client, Verify, _ => { }, cancellation.Token);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectsAndOversizedResponsesNeverReachVerification(bool oversized)
    {
        using var files = new Files();
        using var client = Client(_ =>
        {
            var response = new HttpResponseMessage(oversized ? HttpStatusCode.OK : HttpStatusCode.Redirect)
            {
                Content = new ByteArrayContent([1])
            };
            if (oversized)
                response.Content.Headers.ContentLength = RemoteDevTunnelCli.MaximumDownloadBytes + 1;
            else
                response.Headers.Location = new Uri("https://untrusted.example/cli.exe");
            return response;
        });
        var destination = Path.Combine(files.Root, "cache", "devtunnel");
        var verified = false;
        var operation = RemoteDevTunnelCli.InstallAsync(
            OfficialSource, destination, client,
            (_, _) => { verified = true; return Task.CompletedTask; },
            _ => { }, CancellationToken.None);
        if (oversized)
            await Assert.ThrowsAsync<InvalidDataException>(() => operation);
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => operation);
        Assert.False(verified);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!));
    }

    [Fact]
    public async Task StreamingDownloadLimitIsEnforcedWithoutTrustingContentLength()
    {
        using var allowed = new MemoryStream(new byte[8]);
        using var accepted = new MemoryStream();
        Assert.Equal(8, await RemoteDevTunnelCli.CopyDownloadAsync(allowed, accepted, 8, CancellationToken.None));
        using var excessive = new MemoryStream(new byte[9]);
        using var rejected = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RemoteDevTunnelCli.CopyDownloadAsync(excessive, rejected, 8, CancellationToken.None));
        Assert.True(rejected.Length <= 8);
    }

    [SkippableFact]
    public async Task WindowsRejectsAnUnsignedDownloadBeforeItCanRun()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        using var files = new Files();
        var path = files.Create("unsigned", "devtunnel.exe");
        await File.WriteAllBytesAsync(path, [(byte)'M', (byte)'Z', 0, 0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemoteDevTunnelCli.VerifyDownloadedCliAsync(path, CancellationToken.None));
    }

    [SkippableFact]
    public async Task WindowsAcceptsMicrosoftSignedDownloadWithInheritedPowerShellCoreModules()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var path = Environment.GetEnvironmentVariable("LUMI_DEVTUNNEL_SIGNED_TEST_FILE");
        Skip.If(string.IsNullOrWhiteSpace(path) || !File.Exists(path),
            "Set LUMI_DEVTUNNEL_SIGNED_TEST_FILE to a verified official download for the signature smoke test.");
        var coreModules = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "Modules");
        Skip.IfNot(Directory.Exists(coreModules), "PowerShell Core is not installed.");
        var previous = Environment.GetEnvironmentVariable("PSModulePath");
        Environment.SetEnvironmentVariable("PSModulePath", coreModules);
        try
        {
            await RemoteDevTunnelCli.VerifyDownloadedCliAsync(path!, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", previous);
        }
    }

    [Theory]
    [InlineData("""{"status":"Not logged in"}""")]
    [InlineData("""{"status":"Login token expired","provider":"microsoft"}""")]
    [InlineData("""{"status":"Logged in","provider":"github"}""")]
    public async Task MissingMicrosoftSignInWaitsForUserActionWithoutOpeningAnything(string initial)
    {
        var calls = new List<string[]>();
        var prompted = false;
        Task<string> Run(string[] arguments, CancellationToken token)
        {
            calls.Add(arguments);
            return Task.FromResult(initial);
        }
        var result = await RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
            Run, () => prompted = true, CancellationToken.None);
        Assert.False(prompted);
        Assert.Null(result);
        Assert.Single(calls);
        Assert.Equal(["user", "show", "--json"], calls[0]);
    }

    [Fact]
    public async Task ExplicitSignInUsesTheNormalDesktopFlowWithoutForcingDeviceCodesOrSilentWindowsAuth()
    {
        var calls = new List<string[]>();
        Task<string> Run(string[] arguments, CancellationToken token)
        {
            calls.Add(arguments);
            return Task.FromResult(calls.Count == 1
                ? """{"status":"Not logged in"}"""
                : calls.Count == 2 ? "{}" : MicrosoftIdentity);
        }

        var result = await RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
            Run, () => { }, CancellationToken.None, signIn: true);

        Assert.Equal("owner@example.com", result!.Value.Username);
        Assert.Equal(["user", "login", "--entra", "--json"], calls[1]);
        Assert.Equal(RemoteDevTunnelHost.DefaultSignInArguments, calls[1]);
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task ExplicitReauthenticationDoesNotTrustAnExpiredCachedAccount()
    {
        var calls = new List<string[]>();
        Task<string> Run(string[] arguments, CancellationToken token)
        {
            calls.Add(arguments);
            if (calls.Count == 1)
                return Task.FromResult(MicrosoftIdentity);
            return Task.FromResult(calls.Count == 2 ? "{}" : MicrosoftIdentity);
        }

        var result = await RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
            Run, () => { }, CancellationToken.None, signIn: true);

        Assert.Equal("owner@example.com", result!.Value.Username);
        Assert.Equal(RemoteDevTunnelHost.DefaultSignInArguments, calls[1]);
        Assert.Equal(["user", "show", "--json"], calls[2]);
    }

    [Fact]
    public async Task BrowserFallbackExplicitlyUsesDeviceCodesAndRechecksTheAccount()
    {
        var calls = new List<string[]>();
        Task<string> Run(string[] args, CancellationToken token)
        {
            calls.Add(args);
            return Task.FromResult(calls.Count == 1
                ? """{"status":"Not logged in"}"""
                : calls.Count == 2 ? "{}" : MicrosoftIdentity);
        }
        var identity = await RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
            Run, () => { }, CancellationToken.None, signIn: true, useDeviceCode: true);
        Assert.Equal("owner-id", identity!.Value.ObjectId);
        Assert.Equal(RemoteDevTunnelHost.DeviceSignInArguments, calls[1]);
        Assert.Equal(3, calls.Count);
        Assert.DoesNotContain(calls.SelectMany(args => args), arg => arg == "--use-integrated-windows-auth");
    }

    [Fact]
    public async Task SignInTimeoutDoesNotStartAnotherHiddenLoginAttempt()
    {
        var calls = new List<string[]>();
        Task<string> Run(string[] arguments, CancellationToken token)
        {
            calls.Add(arguments);
            if (calls.Count == 1)
                return Task.FromResult("""{"status":"Not logged in"}""");
            throw new OperationCanceledException("Device sign-in expired.");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
                Run, () => { }, CancellationToken.None, signIn: true));

        Assert.Equal(2, calls.Count);
        Assert.Equal(RemoteDevTunnelHost.DefaultSignInArguments, calls[1]);
    }

    [Fact]
    public void WindowsLoginKeepsHiddenConsoleContextForTheBrokerWithoutShowingATerminal()
    {
        var signIn = RemoteDevTunnelCli.CreateCommandStartInfo("devtunnel", RemoteDevTunnelHost.DefaultSignInArguments);
        Assert.False(signIn.UseShellExecute);
        Assert.True(signIn.RedirectStandardOutput);
        Assert.True(signIn.RedirectStandardError);
        Assert.Equal(!OperatingSystem.IsWindows(), signIn.CreateNoWindow);
        Assert.Equal(OperatingSystem.IsWindows() ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            signIn.WindowStyle);
        var command = RemoteDevTunnelCli.CreateCommandStartInfo("devtunnel", ["user", "show", "--json"]);
        Assert.True(command.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Normal, command.WindowStyle);
    }

    [Fact]
    public async Task ExistingMicrosoftSignInDoesNotPromptAgain()
    {
        var calls = 0;
        var identity = await RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
            (_, _) => { calls++; return Task.FromResult(MicrosoftIdentity); },
            () => throw new InvalidOperationException("Unexpected sign-in prompt."),
            CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("owner-id", identity!.Value.ObjectId);
    }

    [Fact]
    public async Task CanceledSignInDoesNotProceedToAccountVerificationOrTunnelCreation()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
                (_, _) =>
                {
                    if (++calls == 1)
                        return Task.FromResult("""{"status":"Not logged in"}""");
                    cancellation.Cancel();
                    return Task.FromCanceled<string>(cancellation.Token);
                },
                () => { }, cancellation.Token, signIn: true));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WrongProviderAfterLoginStillFailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
                (_, _) => Task.FromResult("""{"status":"Logged in","provider":"github"}"""),
                () => { }, CancellationToken.None, signIn: true));
    }

    [Theory]
    [InlineData("https://microsoft.com/devicelogin")]
    [InlineData("https://www.microsoft.com/devicelogin")]
    [InlineData("https://login.microsoftonline.com/common/oauth2/deviceauth")]
    [InlineData("https://login.microsoft.com/device")]
    public void DevicePromptAlwaysProvidesAValidatedMicrosoftUrlAndCode(string url)
    {
        var prompt = RemoteDevTunnelHost.ParseDeviceSignIn(
            $"To sign in, use a web browser to open the page {url}\nand enter the code ABC123XYZ to authenticate.");
        Assert.Equal(new RemoteDevTunnelSignIn(url, "ABC123XYZ"), prompt);
    }

    [Theory]
    [InlineData("http://microsoft.com/devicelogin")]
    [InlineData("https://microsoft.com.attacker.test/devicelogin")]
    [InlineData("https://user@microsoft.com/devicelogin")]
    [InlineData("https://microsoft.com/devicelogin?redirect=attacker")]
    [InlineData("https://microsoft.com/devicelogin#fragment")]
    [InlineData("https://microsoft.com:8443/devicelogin")]
    [InlineData("https://login.microsoftonline.com/another/path")]
    public void UnexpectedLoginLinksCannotBeOpened(string url)
    {
        Assert.False(RemoteDevTunnelHost.IsAllowedSignInUrl(url));
        Assert.Null(RemoteDevTunnelHost.ParseDeviceSignIn(
            $"Open {url} and enter the code ABC123XYZ"));
    }

    [Theory]
    [InlineData("Error: not logged in", true)]
    [InlineData("Login required.", true)]
    [InlineData("HTTP 401 Unauthorized", true)]
    [InlineData("The authentication token has expired.", true)]
    [InlineData("AADSTS50076: MFA required.", true)]
    [InlineData("Please log in again.", true)]
    [InlineData("Error connecting to relay: network unavailable.", false)]
    [InlineData("The tunnel access policy is invalid (403).", false)]
    public void ExpiredAuthenticationRequiresUserActionRatherThanAnInfiniteRetry(string error, bool expected) =>
        Assert.Equal(expected, RemoteDevTunnelCli.IsAuthenticationFailure(error));

    [Theory]
    [InlineData("The network is unavailable.", true)]
    [InlineData("Request returned503 ServiceUnavailable.", true)]
    [InlineData("Connection reset by peer.", true)]
    [InlineData("Error resolving host: no such host.", true)]
    [InlineData("Unrecognized option '--new-flag'.", false)]
    [InlineData("Tunnel already exists (409).", false)]
    [InlineData("Forbidden: this account cannot manage the tunnel.", false)]
    public void OnlyTransientCommandFailuresAreAutomaticallyRetried(string error, bool expected) =>
        Assert.Equal(expected, RemoteDevTunnelCli.IsTransientFailure(error));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessPromptsAreVisibleBeforeExitAndCancellationDrainsTheOwnedProcess(bool cancel)
    {
        using var files = new Files();
        using var cancellation = new CancellationTokenSource();
        Directory.CreateDirectory(files.Root);
        var release = Path.Combine(files.Root, "release");
        var stdout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/bin/sh";
        var args = OperatingSystem.IsWindows()
            ? new[] { "-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Out.Write('login-prompt'); [Console]::Out.Flush(); " +
                "[Console]::Error.Write('login-url'); [Console]::Error.Flush(); " +
                "while (!(Test-Path -LiteralPath $env:LUMI_TEST_CLI_RELEASE)) { Start-Sleep -Milliseconds 10 }" }
            : ["-c", "printf 'login-prompt'; printf 'login-url' >&2; " +
                "while [ ! -f \"$LUMI_TEST_CLI_RELEASE\" ]; do sleep 0.01; done"];
        var info = RemoteDevTunnelCli.CreateStartInfo(executable, args);
        info.Environment["LUMI_TEST_CLI_RELEASE"] = release;
        var running = RemoteDevTunnelCli.RunProcessAsync(
            info, TimeSpan.FromSeconds(20), cancellation.Token,
            reportOutput: line =>
            {
                if (line == "login-prompt")
                    stdout.TrySetResult();
                if (line == "login-url")
                    stderr.TrySetResult();
            });
        try
        {
            await Task.WhenAll(stdout.Task, stderr.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            }
            else
            {
                await File.WriteAllTextAsync(release, "finish");
                var result = await running;
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("login-prompt", result.Output);
                Assert.Contains("login-url", result.Error);
            }
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task ErrorLineNotificationsPreserveEveryLineAndBoundTheRetainedTail()
    {
        var lines = new[] { "First host error", new string('x', 2500), "Last host error" };
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            string.Join(Environment.NewLine, lines) + Environment.NewLine));
        using var reader = new StreamReader(stream);
        var reported = new List<string>();
        var tail = await RemoteDevTunnelCli.ReadErrorLinesAsync(reader, CancellationToken.None, reported.Add);
        Assert.Equal(lines, reported);
        Assert.Equal(2000, tail.Length);
        Assert.EndsWith("Last host error" + Environment.NewLine, tail);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Handler(respond));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LumiDevTunnelCliTests", Guid.NewGuid().ToString("N"));

        public string Create(string directory, string name)
        {
            var folder = Path.Combine(Root, directory);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "test-cli");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
