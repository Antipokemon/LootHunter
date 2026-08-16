using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LootHunter.Services;

namespace LootHunter.IPC;

public sealed class NavigationService : INavigationService
{
    private const int MaxPathRecoveryAttempts = 2;

    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveClose;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> moveDirect;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathRunning;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPointReachable;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<object> cancelPathfinding;
    private readonly IObjectTable objectTable;
    private readonly Configuration configuration;
    private readonly IPluginLog log;

    public NavigationService(
        IDalamudPluginInterface pluginInterface,
        IObjectTable objectTable,
        Configuration configuration,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.configuration = configuration;
        this.log = log;
        navReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveClose = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        moveDirect = pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        nearestPointReachable = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        stop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        cancelPathfinding = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
    }

    public bool IsAvailable => navReady.HasFunction && moveClose.HasFunction && moveDirect.HasAction && pathRunning.HasFunction;
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
        float arrivalTolerance = 1.5f,
        bool horizontalArrival = false)
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

        var currentDistance = DistanceToDestination(player.Position, snapped.Value, horizontalArrival);
        if (currentDistance <= stopDistance + arrivalTolerance)
            return NavigationMoveResult.Arrived;

        async Task<bool> StartPathAsync()
            => await StopAsync(cancellationToken)
               && await WaitUntilReadyAsync(cancellationToken)
               && moveClose.InvokeFunc(snapped.Value, fly, stopDistance);

        var recoveryAttempts = 0;
        async Task<bool> RecoverPathAsync(string reason)
        {
            while (recoveryAttempts < MaxPathRecoveryAttempts)
            {
                recoveryAttempts++;
                log.Warning(
                    "Navigation {Reason}; requesting a fresh route from the current position ({Attempt}/{Maximum}).",
                    reason,
                    recoveryAttempts,
                    MaxPathRecoveryAttempts);
                await Task.Delay(250, cancellationToken);
                if (await StartPathAsync())
                    return true;

                reason = "recovery request was rejected";
            }

            return false;
        }

        if (!await StartPathAsync()
            && !await RecoverPathAsync("request was rejected"))
            return NavigationMoveResult.Failed;

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.NavigationTimeoutSeconds));
        var lastProgressAt = DateTime.UtcNow;
        var bestDistance = currentDistance;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (interruptRequested?.Invoke() == true)
            {
                await StopAsync(cancellationToken);
                return NavigationMoveResult.Interrupted;
            }

            player = objectTable.LocalPlayer;
            if (player is null)
                return NavigationMoveResult.Failed;

            currentDistance = DistanceToDestination(player.Position, snapped.Value, horizontalArrival);
            if (currentDistance <= stopDistance + arrivalTolerance)
            {
                await StopAsync(cancellationToken);
                return NavigationMoveResult.Arrived;
            }

            if (currentDistance < bestDistance - 1f)
            {
                bestDistance = currentDistance;
                lastProgressAt = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - lastProgressAt > TimeSpan.FromSeconds(Math.Max(5, configuration.NavigationStallSeconds)))
            {
                if (!await RecoverPathAsync("stalled"))
                {
                    await StopAsync(cancellationToken);
                    return NavigationMoveResult.Failed;
                }

                player = objectTable.LocalPlayer;
                if (player is null)
                    return NavigationMoveResult.Failed;
                bestDistance = DistanceToDestination(player.Position, snapped.Value, horizontalArrival);
                lastProgressAt = DateTime.UtcNow;
                continue;
            }

            if (!IsRunning)
            {
                // SimpleMove can have a short handoff between pathfinding and movement.
                // Give that handoff one framework beat before deciding the route ended.
                await Task.Delay(250, cancellationToken);
                player = objectTable.LocalPlayer;
                if (player is null)
                    return NavigationMoveResult.Failed;

                currentDistance = DistanceToDestination(player.Position, snapped.Value, horizontalArrival);
                if (currentDistance <= stopDistance + arrivalTolerance)
                    return NavigationMoveResult.Arrived;
                if (IsRunning)
                    continue;
                if (!await RecoverPathAsync("ended before arrival"))
                    return NavigationMoveResult.Failed;

                bestDistance = currentDistance;
                lastProgressAt = DateTime.UtcNow;
            }

            await Task.Delay(150, cancellationToken);
        }

        await StopAsync(cancellationToken);
        return NavigationMoveResult.Failed;
    }

    private static float DistanceToDestination(Vector3 current, Vector3 destination, bool horizontalOnly)
        => horizontalOnly
            ? Vector2.Distance(new Vector2(current.X, current.Z), new Vector2(destination.X, destination.Z))
            : Vector3.Distance(current, destination);

    public async Task<NavigationMoveResult> MoveToMovingTargetAsync(
        Func<Vector3?> targetPosition,
        float stopDistance,
        CancellationToken cancellationToken,
        Func<bool>? interruptRequested = null,
        float arrivalTolerance = 0.25f)
    {
        if (!await WaitUntilReadyAsync(cancellationToken) || !moveDirect.HasAction)
            return NavigationMoveResult.Failed;

        stopDistance = Math.Max(0.5f, stopDistance);
        arrivalTolerance = Math.Max(0f, arrivalTolerance);

        if (!await StopAsync(cancellationToken))
            return NavigationMoveResult.Failed;

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.NavigationTimeoutSeconds));
        var lastProgressAt = DateTime.UtcNow;
        var nextDestinationUpdate = DateTime.MinValue;
        var bestDistance = float.MaxValue;
        var recoveryAttempts = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (interruptRequested?.Invoke() == true)
            {
                await StopAsync(cancellationToken);
                return NavigationMoveResult.Interrupted;
            }

            var player = objectTable.LocalPlayer;
            var destination = targetPosition();
            if (player is null || destination is null)
            {
                await StopAsync(cancellationToken);
                return NavigationMoveResult.Failed;
            }

            // Ground combat movement ignores elevation. The target's live position is
            // refreshed below so the follower never chases an obsolete pathfind result.
            var distance = DistanceToDestination(player.Position, destination.Value, horizontalOnly: true);
            if (distance <= stopDistance + arrivalTolerance)
            {
                await StopAsync(cancellationToken);
                return NavigationMoveResult.Arrived;
            }

            if (distance < bestDistance - 0.5f)
            {
                bestDistance = distance;
                lastProgressAt = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - lastProgressAt > TimeSpan.FromSeconds(Math.Max(5, configuration.NavigationStallSeconds)))
            {
                if (recoveryAttempts >= MaxPathRecoveryAttempts)
                {
                    await StopAsync(cancellationToken);
                    return NavigationMoveResult.Failed;
                }

                var restarted = false;
                while (!restarted && recoveryAttempts < MaxPathRecoveryAttempts)
                {
                    recoveryAttempts++;
                    log.Warning(
                        "Navigation to a moving target stalled; requesting a fresh route ({Attempt}/{Maximum}).",
                        recoveryAttempts,
                        MaxPathRecoveryAttempts);
                    restarted = await StopAsync(cancellationToken)
                                && await WaitUntilReadyAsync(cancellationToken)
                                && moveClose.InvokeFunc(destination.Value, false, stopDistance);
                    if (!restarted)
                        await Task.Delay(250, cancellationToken);
                }

                if (!restarted)
                    return NavigationMoveResult.Failed;

                bestDistance = distance;
                lastProgressAt = DateTime.UtcNow;
                nextDestinationUpdate = DateTime.UtcNow.AddSeconds(2);
                continue;
            }

            if (DateTime.UtcNow >= nextDestinationUpdate)
            {
                moveDirect.InvokeAction([destination.Value], false);
                nextDestinationUpdate = DateTime.UtcNow.AddMilliseconds(200);
            }

            await Task.Delay(100, cancellationToken);
        }

        await StopAsync(cancellationToken);
        return NavigationMoveResult.Failed;
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopPath(cancelPending: false);

            // SimpleMove installs a completed path during vnavmesh's next framework
            // update. Yield once, then stop again so a result that completed during
            // the first Stop cannot resurrect the route we just cancelled.
            await Task.Delay(50, cancellationToken);
            StopPath(cancelPending: false);

            if (!IsRunning)
                return true;
            if (DateTime.UtcNow >= deadline)
            {
                // Exceptionally slow pathfinding cannot be allowed to take control
                // later. Fall back to vnavmesh's global cancellation endpoint.
                Stop();
                await Task.Delay(50, cancellationToken);
                StopPath(cancelPending: false);
                return !IsRunning;
            }
        }
    }

    public void Stop() => StopPath(cancelPending: true);

    private void StopPath(bool cancelPending)
    {
        try
        {
            // Cancel a pending SimpleMove request before clearing its current
            // waypoints. Reversing this order leaves a race where the completed
            // request installs the old path immediately after Path.Stop.
            if (cancelPending
                && pathfindInProgress.HasFunction
                && pathfindInProgress.InvokeFunc()
                && cancelPathfinding.HasAction)
                cancelPathfinding.InvokeAction();

            if (stop.HasAction)
                stop.InvokeAction();
        }
        catch
        {
            // IPC can disappear during plugin unload; Stop must remain safe during teardown.
        }
    }
}
