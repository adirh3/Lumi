using System.Net;
using System.Runtime.InteropServices;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteDevTunnelCliTests
{
    private const string MicrosoftIdentity =
        """{"status":"Logged in","provider":"microsoft","username":"owner@example.com","objectId":"owner-id","tenantId":"tenant-id"}""";
    private static readonly Uri OfficialSource = RemoteDevTunnelCli.GetDownloadUri("windows", Architecture.X64);

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

    [Theory]
    [InlineData("""{"status":"Not logged in"}""")]
    [InlineData("""{"status":"Logged in","provider":"github"}""")]
    public async Task MissingMicrosoftSignInOpensBrowserLoginAndRechecksTheAccount(string initial)
    {
        var calls = new List<string[]>();
        var prompted = false;
        Task<string> Run(string[] arguments, CancellationToken token)
        {
            calls.Add(arguments);
            return Task.FromResult(calls.Count == 1 ? initial : calls.Count == 2 ? "{}" : MicrosoftIdentity);
        }
        var result = await RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
            Run, () => prompted = true, CancellationToken.None);
        Assert.True(prompted);
        Assert.Equal("owner@example.com", result.Username);
        Assert.Equal(3, calls.Count);
        Assert.Equal(["user", "show", "--json"], calls[0]);
        Assert.Equal(RemoteDevTunnelHost.BrowserSignInArguments, calls[1]);
        Assert.Equal(calls[0], calls[2]);
    }

    [Fact]
    public async Task WindowsSignInUsesIntegratedAuthenticationBeforeOpeningTheBrowser()
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
            Run, () => { }, CancellationToken.None, preferIntegratedWindowsAuth: true);

        Assert.Equal("owner@example.com", result.Username);
        Assert.Equal(RemoteDevTunnelHost.IntegratedWindowsSignInArguments, calls[1]);
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task FailedIntegratedAuthenticationFallsBackToBrowserSignIn()
    {
        var calls = new List<string[]>();
        Task<string> Run(string[] arguments, CancellationToken token)
        {
            calls.Add(arguments);
            if (calls.Count == 1)
                return Task.FromResult("""{"status":"Not logged in"}""");
            if (arguments.SequenceEqual(RemoteDevTunnelHost.IntegratedWindowsSignInArguments))
                throw new InvalidOperationException("Integrated authentication unavailable.");
            return Task.FromResult(calls.Count == 3 ? "{}" : MicrosoftIdentity);
        }

        var result = await RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
            Run, () => { }, CancellationToken.None, preferIntegratedWindowsAuth: true);

        Assert.Equal("owner@example.com", result.Username);
        Assert.Equal(RemoteDevTunnelHost.IntegratedWindowsSignInArguments, calls[1]);
        Assert.Equal(RemoteDevTunnelHost.BrowserSignInArguments, calls[2]);
        Assert.Equal(["user", "show", "--json"], calls[3]);
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
        Assert.Equal("owner-id", identity.ObjectId);
    }

    [Fact]
    public async Task CanceledSignInDoesNotProceedToAccountVerificationOrTunnelCreation()
    {
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
                (_, _) => ++calls == 1
                    ? Task.FromResult("""{"status":"Not logged in"}""")
                    : Task.FromCanceled<string>(new CancellationToken(true)),
                () => { }, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WrongProviderAfterLoginStillFailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemoteDevTunnelHost.EnsureMicrosoftIdentityAsync(
                (_, _) => Task.FromResult("""{"status":"Logged in","provider":"github"}"""),
                () => { }, CancellationToken.None));
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
