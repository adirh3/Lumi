#if LUMI_PWA_RENDER_WORKER
using SkiaSharp.Internals;

namespace Lumi.Mobile.Browser;

internal static class BrowserRenderWorker
{
    // The Skia handle registry's ReaderWriterLockSlim signals a wait handle on the render thread.
    // Use SkiaSharp's platform-lock seam to protect the same short critical sections without it.
    internal static void ConfigureSkia() => PlatformLock.Factory = static () => new HandleLock();

    private sealed class HandleLock : IPlatformLock
    {
        private readonly object _gate = new();

        public void EnterReadLock() => Monitor.Enter(_gate);
        public void ExitReadLock() => Monitor.Exit(_gate);
        public void EnterWriteLock() => Monitor.Enter(_gate);
        public void ExitWriteLock() => Monitor.Exit(_gate);
        public void EnterUpgradeableReadLock() => Monitor.Enter(_gate);
        public void ExitUpgradeableReadLock() => Monitor.Exit(_gate);
    }
}
#endif
