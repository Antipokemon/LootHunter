using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace LootHunter.Services;

public sealed class MountService(IObjectTable objectTable, ICondition condition, IPluginLog log) : IMountService
{
    private const uint MountRouletteGeneralAction = 9;

    public bool IsMounted => condition[ConditionFlag.Mounted] || objectTable.LocalPlayer?.CurrentMount is not null;
    public bool IsInFlight => condition[ConditionFlag.InFlight] || condition[ConditionFlag.Diving];
    public bool CanFly => GetCanFly();

    public async Task<bool> MountAsync(CancellationToken cancellationToken)
    {
        if (IsMounted)
            return true;

        try
        {
            if (!UseMountRoulette())
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

        var deadline = DateTime.UtcNow.AddSeconds(6);
        var nextAttempt = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsMounted)
                return true;

            // Mount Roulette cannot dismount while the character is still flagged
            // as airborne. The combat landing route ends at floor level; wait for
            // that state transition, then retry the toggle until it is accepted.
            if (!IsInFlight && DateTime.UtcNow >= nextAttempt)
            {
                try
                {
                    UseMountRoulette();
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Failed to dismount.");
                    return false;
                }
                nextAttempt = DateTime.UtcNow.AddMilliseconds(500);
            }

            await Task.Delay(100, cancellationToken);
        }

        return !IsMounted;
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

    private static unsafe bool GetCanFly()
    {
        var state = PlayerState.Instance();
        return state != null && state->IsLoaded && state->CanFly;
    }

    private static unsafe bool UseMountRoulette()
    {
        var manager = ActionManager.Instance();
        return manager != null && manager->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction);
    }
}
