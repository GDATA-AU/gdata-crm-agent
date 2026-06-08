namespace CrmAgent.Services;

/// <summary>
/// Tracks whether a job is currently executing and for how long, so the
/// <see cref="WatchdogService"/> can detect a wedged job (one that has blown past its
/// deadline without honouring cancellation) and force a process restart.
///
/// A single instance is shared between the <see cref="AgentWorker"/> (writer) and the
/// <see cref="WatchdogService"/> (reader). All access is thread-safe.
/// </summary>
public sealed class AgentLiveness
{
    private readonly TimeProvider _clock;
    private long _jobStartTimestamp;
    private volatile string? _activeJobId;

    public AgentLiveness(TimeProvider clock) => _clock = clock;

    /// <summary>The id of the job currently executing, or <c>null</c> when the agent is idle.</summary>
    public string? ActiveJobId => _activeJobId;

    /// <summary>Records that a job has started executing. Must be paired with <see cref="EndJob"/>.</summary>
    public void BeginJob(string jobId)
    {
        // Stamp the start time before publishing the job id so any reader that observes a
        // non-null ActiveJobId also observes the matching start timestamp.
        Interlocked.Exchange(ref _jobStartTimestamp, _clock.GetTimestamp());
        _activeJobId = jobId;
    }

    /// <summary>Records that the active job has finished (completed, failed, or cancelled).</summary>
    public void EndJob() => _activeJobId = null;

    /// <summary>
    /// Elapsed wall-clock time since the active job began, or <c>null</c> when no job is running.
    /// </summary>
    public TimeSpan? ActiveJobElapsed
    {
        get
        {
            if (_activeJobId is null) return null;
            return _clock.GetElapsedTime(Interlocked.Read(ref _jobStartTimestamp));
        }
    }
}
