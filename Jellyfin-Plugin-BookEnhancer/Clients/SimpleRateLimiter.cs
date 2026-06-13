namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class SimpleRateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _timestamps = new();
    private readonly object _lock = new();

    public SimpleRateLimiter(int maxRequests, TimeSpan window)
    {
        _maxRequests = maxRequests;
        _window = window;
    }

    public async Task WaitAsync(CancellationToken ct = default)
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
                    return;
                }

                waitTime = _window - (now - _timestamps.Peek());
            }

            if (waitTime.Value > TimeSpan.Zero)
                await Task.Delay(waitTime.Value, ct).ConfigureAwait(false);
        }
    }
}
