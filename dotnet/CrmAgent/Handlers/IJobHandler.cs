using CrmAgent.Models;

namespace CrmAgent.Handlers;

/// <summary>
/// Interface that all job handlers implement.
/// </summary>
public interface IJobHandler
{
    Task<HandlerResult> ExecuteAsync(Job job, Action<JobProgress> onProgress, CancellationToken ct);
}
