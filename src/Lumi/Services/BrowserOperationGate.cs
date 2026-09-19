using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services;

/// <summary>Serializes a tab's complete tool calls; nested work uses private core methods.</summary>
internal sealed class BrowserOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal async Task<string> RunAsync(Func<Task<string>> operation)
    {
        await _semaphore.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
