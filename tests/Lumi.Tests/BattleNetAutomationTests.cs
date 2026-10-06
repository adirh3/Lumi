#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Lumi.Services;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

[Collection("Desktop automation")]
public sealed class BattleNetAutomationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void InspectMinimizedWindow_RecoversAndReportsReadinessWithoutTakingFocus()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_BATTLENET_UI_TESTS") != "1",
            "Opt-in read-only test against an already open Battle.net client.");
        Skip.If(GetForegroundWindow() == IntPtr.Zero, "Requires an unlocked desktop to establish the foreground sentinel.");
        using var process = Process.GetProcessesByName("Battle.net")
            .Single(candidate => candidate.MainWindowHandle != IntPtr.Zero);
        using var foregroundApp = new UIAutomationDesktopTests.NativeFixture();
        foregroundApp.ShowAsForegroundSentinel();
        using var service = new UIAutomationService();
        var window = process.MainWindowHandle;
        ShowWindow(window, 7);
        Assert.True(IsIconic(window), "The regression starts from an actually minimized window.");
        var foreground = GetForegroundWindow();
        Assert.Equal(new IntPtr(foregroundApp.WindowHandle), foreground);
        var watch = Stopwatch.StartNew();
        var snapshot = service.InspectWindow($"hwnd:0x{window:X}");
        output.WriteLine(snapshot);
        output.WriteLine($"MINIMIZED_STATUS_VERIFIED_MS={watch.Elapsed.TotalMilliseconds:F2}");
        Assert.False(IsIconic(window));
        Assert.Contains("Play: Heroes of the Storm", snapshot);
        Assert.Equal(foreground, GetForegroundWindow());
    }

    [SkippableFact]
    public void Click_NavigatesTheGamePageAndObservationShowsItsPrimaryAction()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_BATTLENET_UI_TESTS") != "1",
            "Opt-in navigation-only test against an already open Battle.net client.");
        var process = Process.GetProcessesByName("Battle.net")
            .Single(candidate => candidate.MainWindowHandle != IntPtr.Zero);
        using (process)
        using (var automation = new UIA3Automation())
        using (var service = new UIAutomationService())
        {
            var window = process.MainWindowHandle;
            var title = $"hwnd:0x{window:X}";
            var root = automation.FromHandle(window);
            var foreground = GetForegroundWindow();

            string? PrimaryAction(string game)
            {
                var condition = new AndCondition(
                    automation.ConditionFactory.ByControlType(ControlType.Button),
                    new OrCondition(
                        automation.ConditionFactory.ByName($"Play: {game}", PropertyConditionFlags.MatchSubstring),
                        automation.ConditionFactory.ByName($"Install: {game}", PropertyConditionFlags.MatchSubstring),
                        automation.ConditionFactory.ByName($"Update: {game}", PropertyConditionFlags.MatchSubstring)));
                return root.FindFirstDescendant(condition)?.Name;
            }

            string WaitForGame(string game)
            {
                string? action = null;
                Assert.True(SpinWait.SpinUntil(() => (action = PrimaryAction(game)) is not null,
                    TimeSpan.FromSeconds(5)), $"The content panel must actually switch to {game}, not just mark its tab selected.");
                return action!;
            }

            void ActivateNatively(string id)
            {
                var tab = root.FindFirstDescendant(automation.ConditionFactory.ByAutomationId(id));
                Assert.NotNull(tab);
                Assert.Equal(ControlType.TabItem, tab.ControlType);
                Assert.True(tab.Patterns.LegacyIAccessible.IsSupported);
                tab.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
            }

            try
            {
                ActivateNatively("game-nav-btn-Hero");
                var heroesAction = WaitForGame("Heroes of the Storm");
                ActivateNatively("game-nav-btn-Fen");
                WaitForGame("Diablo IV");

                var watch = Stopwatch.StartNew();
                using var click = JsonDocument.Parse(service.ExecuteSteps(title,
                [
                    new() { Action = "click", Target = "id:game-nav-btn-Hero" },
                    new() { Action = "wait", Target = "name:" + heroesAction, TimeoutMs = 5000 }
                ]));
                output.WriteLine(click.RootElement.ToString());
                Assert.True(click.RootElement.GetProperty("success").GetBoolean(), click.RootElement.ToString());
                Assert.NotNull(PrimaryAction("Heroes of the Storm"));
                Assert.Contains(heroesAction, click.RootElement.GetProperty("observation").GetString());
                Assert.Equal(foreground, GetForegroundWindow());
                output.WriteLine($"NAVIGATION_VERIFIED_MS={watch.Elapsed.TotalMilliseconds:F2}");
            }
            finally
            {
                // Navigation only: never press the primary Install/Play/Update buttons.
                ActivateNatively("game-nav-btn-Hero");
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}
#endif
