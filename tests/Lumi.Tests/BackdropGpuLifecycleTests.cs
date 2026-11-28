#if WINDOWS
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using StrataTheme.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

[Collection(nameof(NativeWindowTestSession))]
public sealed class BackdropGpuLifecycleTests(ITestOutputHelper output, NativeWindowTestSession session)
{
    [SkippableFact]
    public async Task RemovingTheLastGlassReleasesItsGpuCaptureWithoutAnotherGlassRender()
    {
        Skip.IfNot(Environment.UserInteractive, "The GPU lifetime check needs an interactive desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.RunAsync(async () =>
        {
            var retired = (StrataBackdropBlur.RetiredGpuResources)typeof(StrataBackdropBlur)
                .GetField("s_retired", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var before = retired.Count;
            var probe = new FrostProbe();
            var glass = new StrataBackdropBlur { BlurRadius = 8 };
            var content = new Grid { Children = { new Stripes(), glass, probe } };
            var window = new Window
            {
                Title = "Lumi isolated GPU capture lifetime",
                Width = 320,
                Height = 200,
                Content = content,
            };
            try
            {
                window.Show();
                await probe.Frosted.Task.WaitAsync(timeout.Token);
                output.WriteLine("The backdrop rendered on the GPU and blurred the red/blue stripes.");

                content.Children.Remove(glass);
                probe.InvalidateVisual();
                var compositor = ElementComposition.GetElementVisual(probe)!.Compositor;
                await compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(timeout.Token);
                probe.InvalidateVisual();
                await compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(timeout.Token);

                output.WriteLine($"Retired GPU captures: before={before}, after removing the last glass={retired.Count}.");
                Assert.Equal(before, retired.Count);
            }
            finally
            {
                window.Close();
            }
        }, timeout.Token);
    }

    private sealed class Stripes : Control
    {
        public override void Render(DrawingContext context)
        {
            for (var x = 0; x < Bounds.Width; x += 8)
                context.DrawRectangle((int)(x / 8) % 2 == 0 ? Brushes.Red : Brushes.Blue,
                    null, new Rect(x, 0, 8, Bounds.Height));
        }
    }

    private sealed class FrostProbe : Control
    {
        public TaskCompletionSource Frosted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Render(DrawingContext context) => context.Custom(new ProbeOperation(this, new Rect(Bounds.Size)));

        private sealed class ProbeOperation(FrostProbe owner, Rect bounds) : ICustomDrawOperation
        {
            public Rect Bounds => bounds;
            public bool HitTest(Point point) => false;
            public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                    return;
                using var lease = feature.Lease();
                if (lease.GrContext is null || lease.SkSurface is null)
                    return;
                using var image = lease.SkSurface.Snapshot();
                using var bitmap = SKBitmap.FromImage(image);
                var pixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                if (pixel.Red > 40 && pixel.Blue > 40 && pixel.Green < 30)
                    owner.Frosted.TrySetResult();
            }
        }
    }
}
#endif
