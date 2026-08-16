using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace LootHunter.Services;

public sealed unsafe class MountService(IObjectTable objectTable, IPluginLog log) : IMountService
{
    private const uint MountRouletteGeneralAction = 9;

    public bool IsMounted => objectTable.LocalPlayer?.CurrentMount is not null;
    public bool CanFly
    {
        get
        {
            var state = PlayerState.Instance();
            return state != null && state->IsLoaded && state->CanFly;
        }
    }

    public async Task<bool> MountAsync(CancellationToken cancellationToken)
    {
        if (IsMounted)
            return true;

        try
        {
            var manager = ActionManager.Instance();
            if (manager == null || !manager->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction))
                return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to invoke Mount Roulette.");
            return false;
        }

        return await WaitForMountedStateAsync(true, cancellationToken);
    }

    public async Task<bool> DismountAsync(CancellationToken cancellationToken)
    {
        if (!IsMounted)
            return true;

        try
        {
            var manager = ActionManager.Instance();
            if (manager == null || !manager->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction))
                return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to dismount.");
            return false;
        }

        return await WaitForMountedStateAsync(false, cancellationToken);
    }

    private async Task<bool> WaitForMountedStateAsync(bool mounted, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsMounted == mounted)
                return true;
            await Task.Delay(100, cancellationToken);
        }
        return IsMounted == mounted;
    }
}
