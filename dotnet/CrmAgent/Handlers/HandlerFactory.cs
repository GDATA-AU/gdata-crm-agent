using CrmAgent.Models;

namespace CrmAgent.Handlers;

/// <summary>
/// Resolves the appropriate <see cref="IJobHandler"/> for a given job type.
/// </summary>
public sealed class HandlerFactory
{
    private readonly IServiceProvider _services;

    public HandlerFactory(IServiceProvider services)
    {
        _services = services;
    }

    public IJobHandler GetHandler(Job job) => job.Type switch
    {
        JobType.Sql => _services.GetRequiredService<SqlHandler>(),
        JobType.RestApi => _services.GetRequiredService<RestApiHandler>(),
        _ => throw new InvalidOperationException($"Unknown job type: \"{job.Type}\""),
    };
}
