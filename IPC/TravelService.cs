using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LootHunter.Models;
using LootHunter.Services;

namespace LootHunter.IPC;

public sealed class TravelService : ITravelService
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;
    private readonly ICallGateSubscriber<object> abort;
    private readonly IClientState clientState;
    private readonly Configuration configuration;

    public TravelService(IDalamudPluginInterface pluginInterface, IClientState clientState, Configuration configuration)
    {
        this.clientState = clientState;
        this.configuration = configuration;
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        teleport = pluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
    }

    public bool IsAvailable => isBusy.HasFunction && teleport.HasFunction;
    public bool IsBusy => IsAvailable && isBusy.InvokeFunc();

    public async Task<bool> TeleportNearAsync(FarmTarget target, CancellationToken cancellationToken)
    {
        if (clientState.TerritoryType == target.TerritoryId)
            return true;
        if (!IsAvailable || target.NearestAetheryte is not { } destination)
            return false;
        if (IsBusy)
            return false;

        var startingTerritory = clientState.TerritoryType;
        if (!teleport.InvokeFunc(destination.AetheryteId, destination.SubIndex))
            return false;

        var sawBusy = false;
        var sawTerritoryChange = false;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.TeleportTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var busy = isBusy.HasFunction && isBusy.InvokeFunc();
            sawBusy |= busy;
            sawTerritoryChange |= clientState.TerritoryType != startingTerritory;

            if (clientState.TerritoryType == target.TerritoryId && !busy && (sawBusy || sawTerritoryChange))
                return true;

            await Task.Delay(250, cancellationToken);
        }

        return clientState.TerritoryType == target.TerritoryId && !IsBusy;
    }

    public void Abort()
    {
        if (abort.HasAction)
            abort.InvokeAction();
    }
}
