using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Xunit;

namespace Lumi.Tests;

internal sealed class HeadlessTestSession : IDisposable
{
    /// <summary>
    /// <c>[Trait(HostTrait, OwnHost)]</c> runs a class in a test host of its own
    /// (tools/Test-LumiTestsInBatches.ps1). Every class that starts its own session needs it.
    /// </summary>
    internal const string HostTrait = "Host", OwnHost = "Own";

    private readonly HeadlessUnitTestSession _inner;
    private readonly bool _ownsSession;

    private HeadlessTestSession(HeadlessUnitTestSession inner, bool ownsSession = true)
    {
        _inner = inner;
        _ownsSession = ownsSession;
    }

    public static HeadlessTestSession Start()
        => Start(typeof(HeadlessTestApp));

    public static HeadlessTestSession Start(Type appType)
    {
        ArgumentNullException.ThrowIfNull(appType);

        // Reuse the UI thread for cached geometries while retaining PerTest application isolation.
        if (appType == typeof(HeadlessTestApp))
            return new HeadlessTestSession(
                HeadlessUnitTestSession.GetOrStartForAssembly(appType.Assembly),
                ownsSession: false);

        return new HeadlessTestSession(StartOwn(appType));
    }

    /// <summary>
    /// A session of the test's own, for another app (Skia drawing, a clipboard) or a thread of its own.
    /// Avalonia keeps what the first session in a process set up for every later one, whatever its app:
    /// GlyphRun keeps that session's renderer (text drawn by the shared drawing-free session's stubs can't
    /// be drawn by Skia, so it is silently left out) and TextDecorations belong to its thread. Whether a
    /// test then fails depends on which classes ran before it in the host, so a class that starts its own
    /// session must run in a host of its own.
    /// </summary>
    public static HeadlessUnitTestSession StartOwn(Type appType)
    {
        RequireOwnHost();
        return HeadlessUnitTestSession.StartNew(appType, AvaloniaTestIsolationLevel.PerTest);
    }

    private static void RequireOwnHost()
    {
        var testClass = new StackTrace().GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType)
            .Select(OutermostType)
            .FirstOrDefault(IsTestClass);
        if (testClass is null || HasOwnHostTrait(testClass))
            return;

        throw new InvalidOperationException(
            $"{testClass.Name} starts an Avalonia session of its own, so it must run in a test host of its own: " +
            $"mark it [Trait(\"{HostTrait}\", \"{OwnHost}\")]. Avalonia keeps state from the first session in a " +
            "process (GlyphRun's renderer, TextDecorations' thread), so sharing a host makes it depend on which " +
            "classes ran before it.");
    }

    private static Type? OutermostType(Type? type)
    {
        while (type is { IsNested: true } && type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            type = type.DeclaringType;
        return type;
    }

    private static bool IsTestClass(Type? type)
        => type is not null
           && type.Assembly == typeof(HeadlessTestSession).Assembly
           && type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
               .Any(method => method.IsDefined(typeof(FactAttribute), inherit: true));

    private static bool HasOwnHostTrait(Type testClass)
        => testClass.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments is [{ Value: HostTrait }, { Value: OwnHost }]);

    public Task Dispatch(Action action, CancellationToken cancellationToken)
    {
        return _inner.Dispatch(action, cancellationToken);
    }

    // Avalonia has no Func<Task> overload: forwarding it directly returns Task<Task> as Task.
    // Select the async generic overload so the dispatcher pumps and propagates the whole operation.
    public Task Dispatch(Func<Task> action, CancellationToken cancellationToken)
    {
        return _inner.Dispatch<int>(async () =>
        {
            await action();
            return 0;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (!_ownsSession)
            return;

        try
        {
            _inner.Dispose();
        }
        catch (NullReferenceException)
        {
            // Avalonia.Headless can throw during PerTest teardown after
            // the test body has completed. Keep assertions meaningful while
            // avoiding random suite failures from the external harness cleanup.
        }
    }
}
