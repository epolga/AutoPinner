namespace AutoPinner.Utils;

/// <summary>
/// Minimal token-rate limiter: enforces a minimum interval between successive
/// permits. In daemon mode the worker calls WaitAsync() before each pin attempt
/// so the cadence stays under whatever POST_INTERVAL_SECONDS is configured.
/// </summary>
public sealed class RateLimiter
{
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _lastTickUtc = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RateLimiter(TimeSpan minInterval)
    {
        _minInterval = minInterval;
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sinceLast = DateTimeOffset.UtcNow - _lastTickUtc;
            if (sinceLast < _minInterval)
            {
                var wait = _minInterval - sinceLast;
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            _lastTickUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
