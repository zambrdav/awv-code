using System.Collections.Concurrent;
using System.Threading.Channels;
using SmartRoutingAPI.Contracts;

namespace SmartRoutingAPI.Services;

/// <summary>The kind of work a queued optimisation job performs.</summary>
public enum OptimizationKind
{
    Create,
    JobChange,
    Metadata,
    Preview
}

/// <summary>An immutable unit of optimisation work handed to the background worker.</summary>
public sealed record OptimizationWorkItem(
    Guid JobId,
    OptimizationKind Kind,
    RouteDTO? CreateDto,
    int? RouteId,
    RouteJobPatch? Patch,
    RouteMetadataDTO? Metadata,
    bool SaveToDb);

/// <summary>
/// Queue for long-running route optimisations. Write endpoints enqueue and return
/// <c>202 Accepted</c> immediately; the request thread is never blocked on VROOM (§3.2 P1 / §4.4).
/// </summary>
public interface IRouteOptimizationQueue
{
    OptimizationJobResponse EnqueueCreate(RouteDTO dto);
    OptimizationJobResponse EnqueueJobChange(int routeId, RouteJobPatch patch, bool saveToDb);
    OptimizationJobResponse EnqueueMetadata(int routeId, RouteMetadataDTO metadata);
    OptimizationJobResponse? Get(Guid jobId);
}

/// <summary>
/// In-memory implementation backed by an unbounded <see cref="Channel{T}"/> and a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> status store. Registered as a singleton and
/// consumed by <see cref="RouteOptimizationWorker"/>. Job state is process-local (lost on restart);
/// swap for a durable store (Redis/DB/Hangfire) if cross-instance durability is required.
/// </summary>
public sealed class RouteOptimizationQueue : IRouteOptimizationQueue
{
    private const int MaxRetainedJobs = 1000;

    private readonly Channel<OptimizationWorkItem> _channel =
        Channel.CreateUnbounded<OptimizationWorkItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    internal ChannelReader<OptimizationWorkItem> Reader => _channel.Reader;

    public OptimizationJobResponse EnqueueCreate(RouteDTO dto)
        => Enqueue(new OptimizationWorkItem(Guid.NewGuid(), OptimizationKind.Create, dto, null, null, null, true));

    public OptimizationJobResponse EnqueueJobChange(int routeId, RouteJobPatch patch, bool saveToDb)
        => Enqueue(new OptimizationWorkItem(
            Guid.NewGuid(),
            saveToDb ? OptimizationKind.JobChange : OptimizationKind.Preview,
            null, routeId, patch, null, saveToDb));

    public OptimizationJobResponse EnqueueMetadata(int routeId, RouteMetadataDTO metadata)
        => Enqueue(new OptimizationWorkItem(Guid.NewGuid(), OptimizationKind.Metadata, null, routeId, null, metadata, true));

    public OptimizationJobResponse? Get(Guid jobId)
        => _jobs.TryGetValue(jobId, out var state) ? state.ToResponse() : null;

    // -- internal state transitions used by the worker -----------------------------------------

    internal void MarkRunning(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var state)) state.Status = OptimizationStatus.Running;
    }

    internal void MarkSucceeded(Guid jobId, RouteResponse result)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            state.Status = OptimizationStatus.Succeeded;
            state.Result = result;
            state.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    internal void MarkFailed(Guid jobId, string error)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            state.Status = OptimizationStatus.Failed;
            state.Error = error;
            state.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    private OptimizationJobResponse Enqueue(OptimizationWorkItem item)
    {
        var state = new JobState { CreatedAt = DateTimeOffset.UtcNow, Status = OptimizationStatus.Queued };
        _jobs[item.JobId] = state;

        PruneIfNeeded();

        // Unbounded channel writes never fail synchronously.
        _channel.Writer.TryWrite(item);
        return state.ToResponse(item.JobId);
    }

    private void PruneIfNeeded()
    {
        if (_jobs.Count <= MaxRetainedJobs) return;

        var oldestCompleted = _jobs
            .Where(kv => kv.Value.CompletedAt.HasValue)
            .OrderBy(kv => kv.Value.CompletedAt)
            .Take(_jobs.Count - MaxRetainedJobs)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldestCompleted) _jobs.TryRemove(key, out _);
    }

    private sealed class JobState
    {
        public OptimizationStatus Status { get; set; }
        public RouteResponse? Result { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }

        public OptimizationJobResponse ToResponse(Guid? id = null)
            => new(id ?? Guid.Empty, Status, Result, Error, CreatedAt, CompletedAt);
    }
}

/// <summary>
/// Hosted background worker that drains <see cref="RouteOptimizationQueue"/> and runs each job in
/// its own DI scope (so every job gets a fresh scoped <c>DbContext</c> and service instance).
/// </summary>
public sealed class RouteOptimizationWorker : BackgroundService
{
    private readonly RouteOptimizationQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RouteOptimizationWorker> _logger;

    public RouteOptimizationWorker(
        IRouteOptimizationQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<RouteOptimizationWorker> logger)
    {
        _queue = (RouteOptimizationQueue)queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(OptimizationWorkItem item, CancellationToken ct)
    {
        _queue.MarkRunning(item.JobId);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<RouteOptimizationService>();

            var route = item.Kind switch
            {
                OptimizationKind.Create =>
                    await service.CreateRouteInternalAsync(item.CreateDto!, ct),
                OptimizationKind.JobChange or OptimizationKind.Preview =>
                    await service.CalculateRouteWithJobChangesAsync(
                        item.RouteId!.Value, item.Patch!.AddJobIds, item.Patch!.RemoveJobIds, item.SaveToDb, ct),
                OptimizationKind.Metadata =>
                    await service.UpdateRouteMetadataAsync(item.RouteId!.Value, item.Metadata!, ct),
                _ => null
            };

            if (route is null)
            {
                _queue.MarkFailed(item.JobId, "Route not found or could not be calculated.");
                return;
            }

            _queue.MarkSucceeded(item.JobId, route.ToResponse());
        }
        catch (ArgumentException ex)
        {
            // Client-caused (invalid ids) — surface the message.
            _queue.MarkFailed(item.JobId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Optimisation job {JobId} ({Kind}) failed.", item.JobId, item.Kind);
            _queue.MarkFailed(item.JobId, "Route optimisation failed.");
        }
    }
}
