using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace LootHunter.Services;

public sealed class MountService(IObjectTable objectTable, ICondition condition, IPluginLog log) : IMountService
{
    private const uint MountRouletteGeneralAction = 9;
    private const uint DismountGeneralAction = 23;

    public bool IsMounted => condition[ConditionFlag.Mounted] || objectTable.LocalPlayer?.CurrentMount is not null;
    public bool IsInFlight => condition[ConditionFlag.InFlight] || condition[ConditionFlag.Diving];
    public bool CanFly => GetCanFly();

    public async Task<bool> MountAsync(CancellationToken cancellationToken)
    {
        if (IsMounted)
            return true;

        var deadline = DateTime.UtcNow.AddSeconds(8);
        var nextAttempt = DateTime.MinValue;
        var attemptCount = 0;
        var lastActionStatus = uint.MaxValue;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsMounted)
            {
                log.Information("Mount completed after {AttemptCount} action attempt(s).", attemptCount);
                return true;
            }

            if (DateTime.UtcNow >= nextAttempt)
            {
                try
                {
                    lastActionStatus = GetMountActionStatus();
                    if (lastActionStatus == 0)
                    {
                        attemptCount++;
                        var accepted = UseMountRoulette();
                        log.Information(
                            "Mount action attempt {AttemptCount}: accepted={Accepted}, mounted={Mounted}.",
                            attemptCount,
                            accepted,
                            IsMounted);
                        nextAttempt = DateTime.UtcNow.AddMilliseconds(accepted ? 1500 : 500);
                    }
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Failed to invoke Mount Roulette.");
                    return false;
                }
                if (nextAttempt <= DateTime.UtcNow)
                    nextAttempt = DateTime.UtcNow.AddMilliseconds(500);
            }

            await Task.Delay(100, cancellationToken);
        }

        log.Warning(
            "Mount timed out: attempts={AttemptCount}, actionStatus={ActionStatus}, mounted={Mounted}.",
            attemptCount,
            lastActionStatus,
            IsMounted);
        return IsMounted;
    }

    public async Task<bool> DismountAsync(CancellationToken cancellationToken)
    {
        if (!IsMounted)
            return true;

        var deadline = DateTime.UtcNow.AddSeconds(12);
        var nextAttempt = DateTime.MinValue;
        var attemptCount = 0;
        var lastActionStatus = uint.MaxValue;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsMounted)
            {
                log.Information("Dismount completed after {AttemptCount} action attempt(s).", attemptCount);
                return true;
            }

            if (DateTime.UtcNow >= nextAttempt)
            {
                try
                {
                    lastActionStatus = GetDismountActionStatus();
                    if (lastActionStatus == 0)
                    {
                        attemptCount++;
                        var accepted = UseDismount();
                        log.Information(
                            "Dismount action attempt {AttemptCount}: accepted={Accepted}, mounted={Mounted}, inFlight={InFlight}.",
                            attemptCount,
                            accepted,
                            IsMounted,
                            IsInFlight);
                    }
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Failed to dismount.");
                    return false;
                }
                nextAttempt = DateTime.UtcNow.AddMilliseconds(250);
            }

            await Task.Delay(100, cancellationToken);
        }

        var position = objectTable.LocalPlayer is { } player
            ? player.Position.ToString()
            : "unavailable";
        log.Warning(
            "Dismount timed out: attempts={AttemptCount}, actionStatus={ActionStatus}, mounted={Mounted}, inFlight={InFlight}, position={Position}.",
            attemptCount,
            lastActionStatus,
            IsMounted,
            IsInFlight,
            position);
        return !IsMounted;
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

    private static unsafe uint GetMountActionStatus()
    {
        var manager = ActionManager.Instance();
        return manager == null
            ? uint.MaxValue
            : manager->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralAction);
    }

    private static unsafe uint GetDismountActionStatus()
    {
        var manager = ActionManager.Instance();
        return manager == null
            ? uint.MaxValue
            : manager->GetActionStatus(ActionType.GeneralAction, DismountGeneralAction);
    }

    private static unsafe bool UseDismount()
    {
        var manager = ActionManager.Instance();
        return manager != null && manager->UseAction(ActionType.GeneralAction, DismountGeneralAction);
    }
}
