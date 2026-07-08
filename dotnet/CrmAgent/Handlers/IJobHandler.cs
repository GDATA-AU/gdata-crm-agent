using CrmAgent.Models;

namespace CrmAgent.Handlers;

/// <summary>
/// Executes a single job. Implementations must honour the cancellation token,
/// report progress via the callback, and return a <see cref="HandlerResult"/> with a
/// non-null <see cref="HandlerResult.BlobName"/> for non-preview jobs
/// (<see cref="HandlerResult.PreviewRows"/> for preview jobs).
/// </summary>
public interface IJobHandler
{
    /// <summary>Maximum number of rows returned inline for preview jobs (no blob output).</summary>
    public const int PreviewRowLimit = 100;

    Task<HandlerResult> ExecuteAsync(Job job, Action<JobProgress> onProgress, CancellationToken ct);
}
