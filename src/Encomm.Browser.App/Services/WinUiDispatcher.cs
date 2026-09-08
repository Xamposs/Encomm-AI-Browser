using Encomm.Browser.Engine.Abstractions;
using Microsoft.UI.Dispatching;

namespace Encomm.Browser.App.Services;

/// <summary>
/// WinUI DispatcherQueue wrapped as an engine-agnostic
/// <see cref="IUiDispatcher"/>. The engine adapter uses this to
/// marshal UI-thread work without depending on Microsoft.UI directly.
/// </summary>
public sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiDispatcher(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public void Post(Action action)
    {
        if (action is null) return;
        if (_queue.HasThreadAccess)
        {
            try { action(); } catch { /* swallowed; engine must not throw into UI loop */ }
        }
        else
        {
            var queued = _queue.TryEnqueue(() =>
            {
                try { action(); } catch { /* swallowed */ }
            });
            if (!queued)
            {
                // Queue is shutting down or full. Fall back to direct call.
                try { action(); } catch { /* swallowed */ }
            }
        }
    }

    public Task<T> RunAsync<T>(Func<Task<T>> func)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.TryEnqueue(async () =>
        {
            try
            {
                var result = await func().ConfigureAwait(false);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }
}
