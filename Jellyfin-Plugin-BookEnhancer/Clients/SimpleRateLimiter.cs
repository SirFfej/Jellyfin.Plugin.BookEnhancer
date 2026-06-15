namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class SimpleRateLimiter
{
    private readonly Queue<DateTime> _timestamps = new();
    private readonly object _lock = new();
    private int _maxRequests;
    private TimeSpan _window;

    public SimpleRateLimiter(int maxRequests, TimeSpan window)
    {
        _maxRequests = maxRequests;
        _window = window;
    }

    public void Configure(int maxRequests, TimeSpan window)
    {
        lock (_lock)
        {
            _maxRequests = maxRequests;
            _window = window;
        }
    }

    public Task WaitAsync(CancellationToken ct = default)
    {
        return WaitCoreAsync(null, ct);
    }

    public Task<bool> TryWaitAsync(TimeSpan maxWait, CancellationToken ct = default)
    {
        return WaitCoreAsync(maxWait, ct);
    }

    private async Task<bool> WaitCoreAsync(TimeSpan? maxWait, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan? waitTime;

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                while (_timestamps.Count > 0 && now - _timestamps.Peek() > _window)
                    _timestamps.Dequeue();

                if (_timestamps.Count < _maxRequests)
                {
                    _timestamps.Enqueue(now);
                    return true;
                }

                waitTime = _window - (now - _timestamps.Peek());
                if (waitTime.Value <= TimeSpan.Zero)
                {
                    _timestamps.Enqueue(now);
                    return true;
                }
            }

            if (maxWait.HasValue && waitTime.Value > maxWait.Value)
                return false;

            await Task.Delay(waitTime.Value, ct).ConfigureAwait(false);
        }
    }
}
