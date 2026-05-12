namespace CrmAgent.Services;

/// <summary>
/// Tracks consecutive failures and returns the appropriate delay based on a
/// configurable tier ladder.  Two instances are used in the agent poll loop:
/// one for auth failures (401/403) and one for transient errors (5xx/network).
/// </summary>
public sealed class BackoffStrategy
{
    private readonly TimeSpan _baseInterval;
    private readonly int _attemptsBeforeBackoff;
    private readonly TimeSpan[] _tiers;

    private int _consecutiveFailures;

    /// <param name="baseInterval">
    /// The normal poll interval used while failure count is below
    /// <paramref name="attemptsBeforeBackoff"/>.
    /// </param>
    /// <param name="attemptsBeforeBackoff">
    /// Number of failures at the base interval before escalating to the tier
    /// ladder.  Set to 0 to start escalating immediately.
    /// </param>
    /// <param name="tiers">
    /// Escalation tiers applied after <paramref name="attemptsBeforeBackoff"/>
    /// failures.  The last tier is the cap and repeats indefinitely.
    /// Must contain at least one entry.
    /// </param>
    public BackoffStrategy(TimeSpan baseInterval, int attemptsBeforeBackoff, TimeSpan[] tiers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(attemptsBeforeBackoff);

        if (tiers.Length == 0)
            throw new ArgumentException("At least one backoff tier is required.", nameof(tiers));

        _baseInterval = baseInterval;
        _attemptsBeforeBackoff = attemptsBeforeBackoff;
        _tiers = tiers;
    }

    /// <summary>Number of consecutive failures recorded so far.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Records a failure and returns the delay to wait before the next attempt.
    /// </summary>
    public TimeSpan RecordFailure()
    {
        _consecutiveFailures++;

        if (_consecutiveFailures <= _attemptsBeforeBackoff)
            return _baseInterval;

        var tierIndex = _consecutiveFailures - _attemptsBeforeBackoff - 1;
        if (tierIndex >= _tiers.Length)
            tierIndex = _tiers.Length - 1;

        return _tiers[tierIndex];
    }

    /// <summary>
    /// Resets the failure counter after a successful request.
    /// </summary>
    public void Reset()
    {
        _consecutiveFailures = 0;
    }
}
