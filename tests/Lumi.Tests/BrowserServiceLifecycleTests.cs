using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Lumi.Services;
#if WINDOWS
using Microsoft.Web.WebView2.Core;
#endif
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

    [Theory]
    [InlineData(0xBB, true, true, false, false, 1)]
    [InlineData(0x6B, true, true, false, false, 1)]
    [InlineData(0xBD, true, true, false, false, -1)]
    [InlineData(0x6D, true, true, false, false, -1)]
    [InlineData(0xBB, false, true, false, false, 0)]
    [InlineData(0xBB, true, false, false, false, 0)]
    [InlineData(0xBB, true, true, true, false, 0)]
    [InlineData(0xBB, true, true, false, true, 0)]
    [InlineData(0x30, true, true, false, false, 0)]
    public void BrowserAcceleratorOnlyMapsGlobalScaleShortcuts(
        int virtualKey,
        bool isKeyDown,
        bool controlDown,
        bool altDown,
        bool windowsDown,
        int expected)
    {
        Assert.Equal(
            expected,
            BrowserAcceleratorShortcut.GetUiScaleDelta(
                virtualKey,
                isKeyDown,
                controlDown,
                altDown,
                windowsDown));
    }

#if WINDOWS
    [Theory]
    [InlineData(CoreWebView2ScriptDialogKind.Alert, false)]
    [InlineData(CoreWebView2ScriptDialogKind.Confirm, false)]
    [InlineData(CoreWebView2ScriptDialogKind.Prompt, false)]
    [InlineData(CoreWebView2ScriptDialogKind.Beforeunload, true)]
    public void ScriptDialogPolicyOnlyAcceptsBeforeUnload(
        CoreWebView2ScriptDialogKind kind,
        bool expected)
    {
        Assert.Equal(expected, BrowserService.ShouldAcceptScriptDialog(kind));
    }
#endif

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
