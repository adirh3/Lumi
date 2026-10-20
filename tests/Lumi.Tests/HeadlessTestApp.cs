using Avalonia.Headless;
using Lumi;

[assembly: AvaloniaTestApplication(typeof(Lumi.Tests.HeadlessTestApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]

namespace Lumi.Tests;

public sealed class HeadlessTestApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // Tests create their own windows.
    }
}
