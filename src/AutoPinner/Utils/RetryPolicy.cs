namespace AutoPinner.Utils;

/// <summary>
/// Exponential backoff with jitter for transient HTTP failures.
/// Used by PinterestClient on 429 / 5xx / network errors.
/// </summary>
public sealed class RetryPolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _initialDelay;
    private readonly Random _rng = Random.Shared;

    public RetryPolicy(int maxAttempts = 5, TimeSpan? initialDelay = null)
    {
        _maxAttempts = maxAttempts;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(2);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<int, Task<T>> operation,
        Func<Exception, bool> isTransient,
        CancellationToken ct = default)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                return await operation(attempt).ConfigureAwait(false);
            }
            catch (Exception ex) when (isTransient(ex) && attempt < _maxAttempts)
            {
                last = ex;
                var jitterMs = _rng.Next(0, 500);
                var delay = TimeSpan.FromMilliseconds(_initialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) + jitterMs);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException("RetryPolicy exhausted with no captured exception.");
    }
}
