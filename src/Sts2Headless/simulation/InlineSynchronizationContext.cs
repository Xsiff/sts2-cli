namespace Sts2Headless;

/// <summary>
/// Synchronization context that executes continuations inline immediately.
/// Task.Yield() posts to SynchronizationContext.Current — by executing inline,
/// the yield becomes a no-op and the entire async chain runs synchronously.
/// Uses a recursion guard to queue nested posts and drain them after.
/// </summary>
internal class InlineSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
    private bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_executing)
        {
            _queue.Enqueue((d, state));
            return;
        }

        _executing = true;
        try
        {
            d(state);
            while (_queue.Count > 0)
            {
                var (cb, st) = _queue.Dequeue();
                cb(st);
            }
        }
        finally
        {
            _executing = false;
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        d(state);
    }

    public void Pump()
    {
        while (_queue.Count > 0)
        {
            var (cb, st) = _queue.Dequeue();
            _executing = true;
            try
            {
                cb(st);
            }
            finally
            {
                _executing = false;
            }
        }
    }
}
