using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Lumi.Mobile.Browser;

internal sealed class BrowserHtmlPreview(string path, string fileName) : NativeControlHost
{
    private readonly byte[] _bytes = File.ReadAllBytes(path);

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent) =>
        new JSObjectControlHandle(BrowserInterop.CreateHtmlPreview(_bytes, fileName));

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control is JSObjectControlHandle handle)
            BrowserInterop.DestroyHtmlPreview(handle.Object);
        base.DestroyNativeControlCore(control);
    }
}
