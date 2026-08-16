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
    private readonly ICallGateSubscriber<string, object> executeCommand;
    private readonly ICallGateSubscriber<object> abort;
    private readonly IClientState clientState;
    private readonly Configuration configuration;

    public TravelService(IDalamudPluginInterface pluginInterface, IClientState clientState, Configuration configuration)
    {
        this.clientState = clientState;
        this.configuration = configuration;
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        teleport = pluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        executeCommand = pluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
        abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
    }

    public bool IsAvailable => isBusy.HasFunction && teleport.HasFunction && executeCommand.HasAction;
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

    public async Task<bool> TeleportByLifestreamCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (!IsAvailable || IsBusy)
            return false;

        command = NormalizeCommand(command);
        if (string.IsNullOrWhiteSpace(command))
            return false;

        executeCommand.InvokeAction(command);

        var started = false;
        var startDeadline = DateTime.UtcNow.AddSeconds(10);
        var completionDeadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.TeleportTimeoutSeconds));
        while (DateTime.UtcNow < completionDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var busy = isBusy.InvokeFunc();
            started |= busy;

            if (started && !busy)
                return true;
            if (!started && DateTime.UtcNow >= startDeadline)
                return false;

            await Task.Delay(250, cancellationToken);
        }

        return started && !IsBusy;
    }

    private static string NormalizeCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith("/li ", StringComparison.OrdinalIgnoreCase))
            return command[4..].Trim();
        if (command.Equals("/li", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return command;
    }

    public void Abort()
    {
        if (abort.HasAction)
            abort.InvokeAction();
    }
}
