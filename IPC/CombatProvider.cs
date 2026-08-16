using System.Text.Json.Nodes;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LootHunter.Services;

namespace LootHunter.IPC;

public sealed class CombatProvider : ICombatProvider
{
    private const string DefaultBossModPresetName = "VBM Default";
    private const string LootHunterPresetName = "LootHunter Combat";

    private readonly ICallGateSubscriber<string, string?> getPreset;
    private readonly ICallGateSubscriber<string, bool, bool> createPreset;
    private readonly ICallGateSubscriber<string> getActivePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;
    private readonly ITargetManager targetManager;
    private readonly Configuration configuration;

    public CombatProvider(IDalamudPluginInterface pluginInterface, ITargetManager targetManager, Configuration configuration)
    {
        this.targetManager = targetManager;
        this.configuration = configuration;
        getPreset = pluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");
        createPreset = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        getActivePreset = pluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
        setActivePreset = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        clearActivePreset = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
    }

    public string Name => "BossModReborn";

    public bool IsAvailable
        => getPreset.HasFunction
           && createPreset.HasFunction
           && getActivePreset.HasFunction
           && setActivePreset.HasFunction
           && clearActivePreset.HasFunction;

    public string? AvailabilityError
        => IsAvailable
            ? null
            : "BossModReborn preset IPC is unavailable. Install/enable BossModReborn and make sure its preset IPC is available.";

    public string? PrepareForSession()
    {
        if (!IsAvailable)
            return AvailabilityError;

        var requestedPreset = configuration.BossModPresetName.Trim();
        if (!string.IsNullOrWhiteSpace(requestedPreset))
            return SafeGetPreset(requestedPreset) is null
                ? $"BossModReborn autorotation preset '{requestedPreset}' was not found."
                : null;

        try
        {
            var sourcePreset = getPreset.InvokeFunc(DefaultBossModPresetName);
            if (string.IsNullOrWhiteSpace(sourcePreset))
                return $"BossModReborn preset '{DefaultBossModPresetName}' was not found, so LootHunter could not create its combat preset.";

            var lootHunterPreset = BuildLootHunterPreset(sourcePreset);
            if (lootHunterPreset is null)
                return $"BossModReborn preset '{DefaultBossModPresetName}' could not be converted into a LootHunter combat preset.";

            if (!createPreset.InvokeFunc(lootHunterPreset, true))
                return "BossModReborn rejected the LootHunter combat preset.";

            return null;
        }
        catch (Exception ex)
        {
            return $"BossModReborn combat preset setup failed: {ex.Message}";
        }
    }

    public async Task<CombatResult> KillAsync(IBattleNpc target, CancellationToken cancellationToken)
    {
        if (PrepareForSession() is { Length: > 0 } prepareError)
            return new(false, prepareError);
        if (target.CurrentHp == 0)
            return new(true);

        var originalPreset = SafeGetActivePreset();
        var configuredPreset = configuration.BossModPresetName.Trim();
        var requestedPreset = string.IsNullOrWhiteSpace(configuredPreset) ? LootHunterPresetName : configuredPreset;
        var changedPreset = !string.Equals(originalPreset, requestedPreset, StringComparison.OrdinalIgnoreCase);

        if (changedPreset && !setActivePreset.InvokeFunc(requestedPreset))
            return new(false, $"BossModReborn could not activate autorotation preset '{requestedPreset}'.");

        try
        {
            // LootHunter owns target selection. The built-in LootHunter preset converts BossMod's
            // Targeting strategy to Manual so neutral overworld mobs can be opened on directly.
            targetManager.Target = target;

            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.CombatTimeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.CurrentHp == 0)
                    return new(true);

                // Reassert the intended target if another game/plugin action temporarily changed it.
                if (targetManager.Target?.GameObjectId != target.GameObjectId)
                    targetManager.Target = target;

                await Task.Delay(100, cancellationToken);
            }

            return new(false, $"Combat timed out after {configuration.CombatTimeoutSeconds} seconds.");
        }
        finally
        {
            if (changedPreset)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(originalPreset))
                        setActivePreset.InvokeFunc(originalPreset);
                    else
                        clearActivePreset.InvokeFunc();
                }
                catch
                {
                    // Combat has already finished/aborted; restoration failure should not mask its result.
                }
            }
        }
    }

    private string? SafeGetPreset(string name)
    {
        try { return getPreset.HasFunction ? getPreset.InvokeFunc(name) : null; }
        catch { return null; }
    }

    private string? SafeGetActivePreset()
    {
        try { return getActivePreset.HasFunction ? getActivePreset.InvokeFunc() : null; }
        catch { return null; }
    }

    private static string? BuildLootHunterPreset(string serializedPreset)
    {
        var root = JsonNode.Parse(serializedPreset) as JsonObject;
        if (root is null)
            return null;

        root["Name"] = LootHunterPresetName;

        if (root["Modules"] is not JsonObject modules)
            return null;

        var manualTargetingTracks = 0;
        foreach (var module in modules)
        {
            if (module.Value is not JsonArray settings)
                continue;

            foreach (var node in settings)
            {
                if (node is not JsonObject setting)
                    continue;

                var track = setting["Track"]?.GetValue<string>();
                if (!string.Equals(track, "Targeting", StringComparison.OrdinalIgnoreCase))
                    continue;

                setting["Option"] = "Manual";
                manualTargetingTracks++;
            }
        }

        return manualTargetingTracks > 0 ? root.ToJsonString() : null;
    }
}
