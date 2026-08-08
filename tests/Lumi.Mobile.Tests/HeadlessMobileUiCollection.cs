using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// Avalonia's headless session owns process-wide statics, so UI tests must not run in parallel.
/// </summary>
[CollectionDefinition("Headless mobile UI", DisableParallelization = true)]
public sealed class HeadlessMobileUiCollection;
