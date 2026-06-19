using Microsoft.Extensions.Hosting;

using SerenCluster.Configuration;

namespace SerenCluster;

/// <summary>
/// Background service that drives <see cref="JetsonClusterClient.RefreshAsync"/>
/// - once eagerly at startup, then on the configured interval.
/// </summary>
/// <remarks>
/// Failure-driven invalidation (worker call fails → mark node offline) is
/// handled by callers, not here. This service only handles the periodic
/// "did anything come back online" rediscovery.
///
/// Implementation note: <see cref="ExecuteAsync"/> kicks off the eager
/// startup refresh asynchronously rather than awaiting it before scheduling
/// the loop. That way a slow-to-respond node doesn't delay startup of
/// the rest of the host. The first loop iteration sleeps for the full
/// interval, NOT for "interval - startup time," so worst case the second
/// refresh is at <c>startup + interval</c> rather than overlapping.
/// </remarks>
public sealed class JetsonDiscoveryService : BackgroundService
{
    private readonly JetsonClusterClient _cluster;
    private readonly TimeSpan _interval;
    private readonly Action<string> _log;

    public JetsonDiscoveryService(
        JetsonClusterClient cluster,
        ClusterOptions options,
        Action<string>? log = null)
    {
        _cluster = cluster;
        _interval = options.RefreshInterval;
        _log = log ?? (msg => Console.WriteLine($"[discovery] {msg}"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log("startup eager refresh");
        try
        {
            await _cluster.RefreshAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"startup refresh threw: {ex.GetType().Name}: {ex.Message}");
            // Don't bail - empty snapshots are valid (everything routes 503
            // until the next refresh succeeds). Loop continues below.
        }

        _log($"periodic refresh every {_interval.TotalMinutes:F0} minutes");
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _cluster.RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Don't propagate - periodic failures are expected when
                    // nodes go down. Cluster client logs per-node already.
                    _log($"periodic refresh threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown - clean exit.
        }
    }
}
