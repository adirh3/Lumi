namespace Lumi.Mobile.Services;

/// <summary>
/// Decides when the phone tries to reach Lumi again after a failed or closed connection. The live
/// stream and the launch handshake both consult the same policy, so one owner decides how hard the
/// phone works while the PC is unreachable.
/// </summary>
public interface IRemoteReconnectPolicy
{
    /// <summary>
    /// The pause before attempt number <paramref name="consecutiveFailures"/> + 1, or null when the
    /// phone should stop trying until something changes (network back, app reopened, Retry).
    /// </summary>
    TimeSpan? NextDelay(int consecutiveFailures);

    /// <summary>
    /// Waits for <paramref name="delay"/>, or for a reconnect trigger when it is null. Any trigger
    /// ends the wait early.
    /// </summary>
    Task WaitAsync(TimeSpan? delay, CancellationToken cancellationToken);

    /// <summary>A connection delivered its bootstrap: the failure streak is over.</summary>
    void OnConnected();
}

/// <summary>
/// The phone client's standalone behaviour: exponential pauses from 1 to 10 seconds, retrying for
/// as long as the stream is wanted. The app installs <see cref="RemoteConnectionSupervisor"/>.
/// </summary>
public sealed class FixedRetryPolicy : IRemoteReconnectPolicy
{
    public static FixedRetryPolicy Instance { get; } = new();

    public TimeSpan? NextDelay(int consecutiveFailures) =>
        TimeSpan.FromMilliseconds(Math.Min(
            1000 * Math.Pow(2, Math.Max(0, consecutiveFailures - 1)),
            10_000));

    public Task WaitAsync(TimeSpan? delay, CancellationToken cancellationToken) =>
        Task.Delay(delay ?? Timeout.InfiniteTimeSpan, cancellationToken);

    public void OnConnected()
    {
    }
}
