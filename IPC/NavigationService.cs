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
        if (!pointOnFloor.HasFunction)
            return destination;

        var playerY = objectTable.LocalPlayer?.Position.Y ?? destination.Y;
        var probe = new Vector3(destination.X, destination.Y == 0 ? playerY : destination.Y, destination.Z);
        return pointOnFloor.InvokeFunc(probe, true, 20f)
            ?? pointOnFloor.InvokeFunc(probe, true, 80f)
            ?? destination;
    }

    public async Task<bool> MoveToAsync(Vector3 destination, float stopDistance, bool fly, CancellationToken cancellationToken)
    {
        if (!await WaitUntilReadyAsync(cancellationToken))
            return false;

        var snapped = SnapToFloor(destination) ?? destination;
        var player = objectTable.LocalPlayer;
        if (player is null)
            return false;
        if (Vector3.Distance(player.Position, snapped) <= stopDistance)
            return true;

        Stop();
        if (!moveClose.InvokeFunc(snapped, fly, Math.Max(0.5f, stopDistance)))
            return false;

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.NavigationTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            player = objectTable.LocalPlayer;
            if (player is null)
                return false;

            if (Vector3.Distance(player.Position, snapped) <= stopDistance + 1.5f)
            {
                Stop();
                return true;
            }

            if (!IsRunning)
                return Vector3.Distance(player.Position, snapped) <= stopDistance + 3f;

            await Task.Delay(150, cancellationToken);
        }

        Stop();
        return false;
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
