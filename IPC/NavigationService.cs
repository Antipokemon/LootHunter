using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LootHunter.Services;

namespace LootHunter.IPC;

public sealed class NavigationService : INavigationService
{
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveClose;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathRunning;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPointReachable;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<object> cancelPathfinding;
    private readonly IObjectTable objectTable;
    private readonly Configuration configuration;

    public NavigationService(IDalamudPluginInterface pluginInterface, IObjectTable objectTable, Configuration configuration)
    {
        this.objectTable = objectTable;
        this.configuration = configuration;
        navReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveClose = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        nearestPointReachable = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        stop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        cancelPathfinding = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
    }

    public bool IsAvailable => navReady.HasFunction && moveClose.HasFunction && pathRunning.HasFunction;
    public bool IsReady => IsAvailable && navReady.InvokeFunc();
    public bool IsRunning => IsAvailable && ((pathfindInProgress.HasFunction && pathfindInProgress.InvokeFunc()) || pathRunning.InvokeFunc());

    public async Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return false;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReady)
                return true;
            await Task.Delay(250, cancellationToken);
        }
        return IsReady;
    }

    public Vector3? SnapToFloor(Vector3 destination)
    {
        if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z))
            return null;

        var playerY = objectTable.LocalPlayer?.Position.Y ?? destination.Y;
        var unknownHeight = MathF.Abs(destination.Y) < 0.001f;
        var probe = new Vector3(destination.X, unknownHeight ? playerY : destination.Y, destination.Z);

        // Wiki/map fallback locations only contain X/Z. For those, prefer a reachable
        // navmesh point with a broad vertical search rather than treating Y=0 as real ground.
        if (unknownHeight && nearestPointReachable.HasFunction)
        {
            var reachable = nearestPointReachable.InvokeFunc(probe, 35f, 500f);
            if (reachable is not null)
                return reachable.Value;
        }

        // For static world positions, require a landable floor first. Do not return the raw
        // coordinate if vnavmesh cannot validate it; doing so can send the player underground.
        if (pointOnFloor.HasFunction)
        {
            var floor = pointOnFloor.InvokeFunc(probe, false, 20f)
                ?? pointOnFloor.InvokeFunc(probe, false, 60f);
            if (floor is not null)
                return floor.Value;
        }

        if (nearestPointReachable.HasFunction)
        {
            var reachable = nearestPointReachable.InvokeFunc(probe, 35f, unknownHeight ? 500f : 120f);
            if (reachable is not null)
                return reachable.Value;
        }

        return null;
    }

    public async Task<NavigationMoveResult> MoveToAsync(
        Vector3 destination,
        float stopDistance,
        bool fly,
        CancellationToken cancellationToken,
        Func<bool>? interruptRequested = null,
        float arrivalTolerance = 1.5f)
    {
        if (!await WaitUntilReadyAsync(cancellationToken))
            return NavigationMoveResult.Failed;

        var snapped = SnapToFloor(destination);
        if (snapped is null)
            return NavigationMoveResult.Failed;

        var player = objectTable.LocalPlayer;
        if (player is null)
            return NavigationMoveResult.Failed;

        stopDistance = Math.Max(0.5f, stopDistance);
        arrivalTolerance = Math.Max(0f, arrivalTolerance);

        var currentDistance = Vector3.Distance(player.Position, snapped.Value);
        if (currentDistance <= stopDistance + arrivalTolerance)
            return NavigationMoveResult.Arrived;

        Stop();
        if (!moveClose.InvokeFunc(snapped.Value, fly, stopDistance))
            return NavigationMoveResult.Failed;

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.NavigationTimeoutSeconds));
        var lastProgressAt = DateTime.UtcNow;
        var bestDistance = currentDistance;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (interruptRequested?.Invoke() == true)
            {
                Stop();
                return NavigationMoveResult.Interrupted;
            }

            player = objectTable.LocalPlayer;
            if (player is null)
                return NavigationMoveResult.Failed;

            currentDistance = Vector3.Distance(player.Position, snapped.Value);
            if (currentDistance <= stopDistance + arrivalTolerance)
            {
                Stop();
                return NavigationMoveResult.Arrived;
            }

            if (currentDistance < bestDistance - 1f)
            {
                bestDistance = currentDistance;
                lastProgressAt = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - lastProgressAt > TimeSpan.FromSeconds(Math.Max(5, configuration.NavigationStallSeconds)))
            {
                Stop();
                return NavigationMoveResult.Failed;
            }

            if (!IsRunning)
                return currentDistance <= stopDistance + arrivalTolerance
                    ? NavigationMoveResult.Arrived
                    : NavigationMoveResult.Failed;

            await Task.Delay(150, cancellationToken);
        }

        Stop();
        return NavigationMoveResult.Failed;
    }

    public void Stop()
    {
        try
        {
            if (stop.HasAction)
                stop.InvokeAction();

            if (pathfindInProgress.HasFunction && pathfindInProgress.InvokeFunc() && cancelPathfinding.HasAction)
                cancelPathfinding.InvokeAction();
        }
        catch
        {
            // IPC can disappear during plugin unload; Stop must remain safe during teardown.
        }
    }
}
