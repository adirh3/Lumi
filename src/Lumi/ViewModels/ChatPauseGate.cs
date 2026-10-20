namespace Lumi.ViewModels;

/// <summary>Holds an original turn submission during setup; never holds tool callbacks.</summary>
internal sealed class ChatPauseGate
{
    private readonly object _sync = new();
    private TaskCompletionSource? _resume;
    private int _waiting;

    public event Action? WaitingChanged;

    public bool IsWaiting
    {
        get { lock (_sync) return _waiting > 0; }
    }

    public void SetPaused(bool paused)
    {
        TaskCompletionSource? resume = null;
        lock (_sync)
        {
            if (paused)
                _resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            else
            {
                resume = _resume;
                _resume = null;
            }
        }
        resume?.TrySetResult();
    }

    public void CancelWaiters()
    {
        TaskCompletionSource? canceled;
        lock (_sync)
        {
            canceled = _resume;
            if (canceled is not null)
                _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        canceled?.TrySetCanceled();
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            Task resume;
            lock (_sync)
            {
                if (_resume is null)
                    return;

                resume = _resume.Task;
                _waiting++;
            }
            WaitingChanged?.Invoke();
            try
            {
                await resume.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync) _waiting--;
                WaitingChanged?.Invoke();
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
