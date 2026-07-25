using System;
using System.Threading;
using System.Threading.Tasks;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The visible per-command timer must be a property of the command, not of the control that happens
/// to be displaying it. Both cards previously derived their live readout from a clock started when
/// the control loaded, so switching/reopening a chat recreated the control and restarted the clock —
/// a command running for 45s suddenly read "0s". They now anchor to an authoritative start instant.
/// The terminal card additionally accepted <c>DurationMs</c> without ever rendering it, so a finished
/// shell command reported no time at all once its live clock stopped.
/// </summary>
[Collection("Headless UI")]
public sealed class ToolCallElapsedClockTests
{
    [Fact]
    public async Task ToolCall_AnchoredClock_SurvivesControlRecreation()
    {
        string? elapsed = null;

        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            // A freshly constructed control stands in for the one rebuilt after a chat switch.
            var card = new StrataAiToolCall { ToolName = "powershell", Status = StrataAiToolCallStatus.InProgress };
            card.RunningSince = DateTimeOffset.UtcNow.AddSeconds(-45);
            elapsed = card.ElapsedText;
        }, CancellationToken.None);

        Assert.Equal("45s", elapsed);
    }

    [Fact]
    public async Task ToolCall_WithoutAnchor_FallsBackToLocalClock()
    {
        string? elapsed = null;

        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var card = new StrataAiToolCall { ToolName = "powershell", Status = StrataAiToolCallStatus.InProgress };
            card.RunningSince = null;
            elapsed = card.ElapsedText;
        }, CancellationToken.None);

        Assert.Equal("", elapsed);
    }

    [Fact]
    public async Task ToolCall_FinishedCall_ShowsNoLiveClock()
    {
        string? elapsed = null;

        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var card = new StrataAiToolCall { ToolName = "powershell", Status = StrataAiToolCallStatus.Completed };
            card.RunningSince = DateTimeOffset.UtcNow.AddSeconds(-45);
            elapsed = card.ElapsedText;
        }, CancellationToken.None);

        Assert.Equal("", elapsed);
    }

    [Fact]
    public async Task Terminal_AnchoredClock_SurvivesControlRecreation()
    {
        string? elapsed = null;

        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var card = new StrataTerminalPreview { Command = "Start-Sleep 90", Status = StrataAiToolCallStatus.InProgress };
            card.RunningSince = DateTimeOffset.UtcNow.AddSeconds(-90);
            elapsed = card.ElapsedText;
        }, CancellationToken.None);

        Assert.Equal("1m 30s", elapsed);
    }

    [Theory]
    [InlineData(2680.4, "2.68s")]
    [InlineData(558.8, "559 ms")]
    [InlineData(0, "")]
    public async Task Terminal_FinishedCommand_ShowsFrozenDuration(double durationMs, string expected)
    {
        string? duration = null;

        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var card = new StrataTerminalPreview
            {
                Command = "Get-Date",
                Status = StrataAiToolCallStatus.Completed,
                DurationMs = durationMs,
            };
            duration = card.DurationText;
        }, CancellationToken.None);

        Assert.Equal(expected, duration);
    }

    [Fact]
    public async Task Terminal_RunningCommand_ReportsNoDurationYet()
    {
        string? duration = null;
        string? backgroundDuration = null;

        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var running = new StrataTerminalPreview
            {
                Command = "Start-Sleep 90",
                Status = StrataAiToolCallStatus.InProgress,
                DurationMs = 2680.4,
            };
            duration = running.DurationText;

            // The tool call returned but the shell it launched is still alive: the launch time is not
            // the command's duration, so nothing is reported until the process actually finishes.
            var background = new StrataTerminalPreview
            {
                Command = "npm run dev",
                Status = StrataAiToolCallStatus.Completed,
                IsRunningInBackground = true,
                DurationMs = 2680.4,
            };
            backgroundDuration = background.DurationText;
        }, CancellationToken.None);

        Assert.Equal("", duration);
        Assert.Equal("", backgroundDuration);
    }
}
