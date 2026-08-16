using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LootHunter.Services;

namespace LootHunter.IPC;

public sealed class CombatProvider : ICombatProvider
{
    private readonly ICallGateSubscriber<string> getActivePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;
    private readonly ITargetManager targetManager;
    private readonly Configuration configuration;

    public CombatProvider(IDalamudPluginInterface pluginInterface, ITargetManager targetManager, Configuration configuration)
    {
        this.targetManager = targetManager;
        this.configuration = configuration;
        getActivePreset = pluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
        setActivePreset = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        clearActivePreset = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
    }

    public string Name => "BossModReborn";
    public bool IsAvailable => getActivePreset.HasFunction && setActivePreset.HasFunction;

    public string? AvailabilityError
    {
        get
        {
            if (!IsAvailable)
                return "BossModReborn IPC is unavailable.";
            if (string.IsNullOrWhiteSpace(configuration.BossModPresetName) && string.IsNullOrWhiteSpace(SafeGetActivePreset()))
                return "BossModReborn has no active autorotation preset. Activate one, or set a preset name in LootHunter settings.";
            return null;
        }
    }

    public async Task<CombatResult> KillAsync(IBattleNpc target, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return new(false, AvailabilityError);
        if (target.CurrentHp == 0)
            return new(true);

        var originalPreset = SafeGetActivePreset();
        var requestedPreset = configuration.BossModPresetName.Trim();
        var changedPreset = false;

        if (!string.IsNullOrWhiteSpace(requestedPreset) && !string.Equals(originalPreset, requestedPreset, StringComparison.OrdinalIgnoreCase))
        {
            if (!setActivePreset.InvokeFunc(requestedPreset))
                return new(false, $"BossModReborn autorotation preset '{requestedPreset}' was not found.");
            changedPreset = true;
        }
        else if (string.IsNullOrWhiteSpace(requestedPreset) && string.IsNullOrWhiteSpace(originalPreset))
        {
            return new(false, "BossModReborn has no active autorotation preset.");
        }

        try
        {
            targetManager.Target = target;
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.CombatTimeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.CurrentHp == 0)
                    return new(true);
                await Task.Delay(100, cancellationToken);
            }

            return new(false, $"Combat timed out after {configuration.CombatTimeoutSeconds} seconds.");
        }
        finally
        {
            if (changedPreset)
            {
                if (!string.IsNullOrWhiteSpace(originalPreset) && setActivePreset.HasFunction)
                    setActivePreset.InvokeFunc(originalPreset);
                else if (clearActivePreset.HasFunction)
                    clearActivePreset.InvokeFunc();
            }
        }
    }

    private string? SafeGetActivePreset()
    {
        try { return getActivePreset.HasFunction ? getActivePreset.InvokeFunc() : null; }
        catch { return null; }
    }
}
