using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class BrowserServiceLifecycleTests
{
    [Fact]
    public void InvalidStateDetectionRecognizesDisposedWebViewController()
    {
        var exception = new InvalidOperationException(
            "Controller is disposed.",
            new COMException(
                "The group or resource is not in the correct state.",
                unchecked((int)0x8007139F)));

        Assert.True(BrowserService.IsWebViewInvalidState(exception));
    }

    [Fact]
    public void InvalidStateDetectionDoesNotHideUnrelatedFailures()
    {
        var exception = new InvalidOperationException(
            "Unexpected browser failure.",
            new COMException("Access denied.", unchecked((int)0x80070005)));

        Assert.False(BrowserService.IsWebViewInvalidState(exception));
    }

    [Fact]
    public async Task ThemeUpdatesRemainSafeAfterDisposal()
    {
        var service = new BrowserService();

        service.SetTheme(isDark: false);
        await service.DisposeAsync();
        service.SetTheme(isDark: true);
        await service.DisposeAsync();
    }
}
