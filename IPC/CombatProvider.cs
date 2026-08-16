using System.Text.Json.Nodes;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LootHunter.Services;

namespace LootHunter.IPC;

public sealed class CombatProvider : ICombatProvider
{
    private const string LootHunterPresetName = "LootHunter Combat";

    // BossModReborn's distributed VBM Default currently enables these rotation modules.
    // LootHunter creates a minimal per-job user preset instead of trying to read a
    // distributed default through Presets.Get, because BossMod can hide default presets
    // from the visible preset collection exposed by that IPC endpoint.
    private static readonly IReadOnlyDictionary<uint, string> RotationModuleByClassJob = new Dictionary<uint, string>
    {
        [1] = "BossMod.Autorotation.xan.PLD",   // GLA
        [2] = "BossMod.Autorotation.xan.MNK",   // PGL
        [3] = "BossMod.Autorotation.VeynWAR",   // MRD
        [4] = "BossMod.Autorotation.xan.DRG",   // LNC
        [5] = "BossMod.Autorotation.xan.BRD",   // ARC
        [6] = "BossMod.Autorotation.xan.WHM",   // CNJ
        [7] = "BossMod.Autorotation.xan.BLM",   // THM
        [19] = "BossMod.Autorotation.xan.PLD",
        [20] = "BossMod.Autorotation.xan.MNK",
        [21] = "BossMod.Autorotation.VeynWAR",
        [22] = "BossMod.Autorotation.xan.DRG",
        [23] = "BossMod.Autorotation.xan.BRD",
        [24] = "BossMod.Autorotation.xan.WHM",
        [25] = "BossMod.Autorotation.xan.BLM",
        [26] = "BossMod.Autorotation.xan.SMN",  // ACN
        [27] = "BossMod.Autorotation.xan.SMN",
        [28] = "BossMod.Autorotation.xan.SCH",
        [29] = "BossMod.Autorotation.xan.NIN",  // ROG
        [30] = "BossMod.Autorotation.xan.NIN",
        [31] = "BossMod.Autorotation.xan.MCH",
        [32] = "BossMod.Autorotation.xan.DRK",
        [33] = "BossMod.Autorotation.xan.AST",
        [34] = "BossMod.Autorotation.xan.SAM",
        [35] = "BossMod.Autorotation.xan.RDM",
        [37] = "BossMod.Autorotation.xan.GNB",
        [38] = "BossMod.Autorotation.xan.DNC",
        [39] = "BossMod.Autorotation.xan.RPR",
        [40] = "BossMod.Autorotation.xan.SGE",
        [41] = "BossMod.Autorotation.xan.VPR",
        [42] = "BossMod.Autorotation.xan.PCT",
    };

    private readonly ICallGateSubscriber<string, string?> getPreset;
    private readonly ICallGateSubscriber<string, bool, bool> createPreset;
    private readonly ICallGateSubscriber<string> getActivePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;
    private readonly ITargetManager targetManager;
    private readonly IPlayerState playerState;
    private readonly Configuration configuration;

    private uint preparedClassJobId;

    public CombatProvider(
        IDalamudPluginInterface pluginInterface,
        ITargetManager targetManager,
        IPlayerState playerState,
        Configuration configuration)
    {
        this.targetManager = targetManager;
        this.playerState = playerState;
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
        {
            preparedClassJobId = 0;
            return SafeGetPreset(requestedPreset) is null
                ? $"BossModReborn autorotation preset '{requestedPreset}' was not found."
                : null;
        }

        if (!playerState.IsLoaded)
            return "Player job data is not loaded yet; LootHunter cannot prepare BossModReborn combat.";

        var classJobId = playerState.ClassJob.RowId;
        if (!RotationModuleByClassJob.TryGetValue(classJobId, out var moduleType))
            return $"LootHunter does not have a BossModReborn rotation mapping for class/job ID {classJobId}. Set a BossModReborn preset explicitly in LootHunter settings.";

        // Avoid rewriting BossMod's user preset database on every kill in the same job.
        if (preparedClassJobId == classJobId && SafeGetPreset(LootHunterPresetName) is not null)
            return null;

        try
        {
            var lootHunterPreset = BuildLootHunterPreset(classJobId, moduleType);
            if (!createPreset.InvokeFunc(lootHunterPreset, true))
            {
                preparedClassJobId = 0;
                return $"BossModReborn rejected LootHunter's combat preset for class/job ID {classJobId}. Set a BossModReborn preset explicitly in LootHunter settings.";
            }

            if (SafeGetPreset(LootHunterPresetName) is null)
            {
                preparedClassJobId = 0;
                return "BossModReborn created the LootHunter combat preset but did not expose it afterward.";
            }

            preparedClassJobId = classJobId;
            return null;
        }
        catch (Exception ex)
        {
            preparedClassJobId = 0;
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
            // LootHunter owns target selection. Its generated BossMod preset uses Manual
            // targeting where the job module supports that strategy.
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

    private static string BuildLootHunterPreset(uint classJobId, string moduleType)
    {
        var strategies = new JsonArray();

        // Match the useful combat defaults distributed by BossModReborn, but only for the
        // current job. Keeping the preset small avoids coupling LootHunter to every rotation
        // module in BossMod's default preset database.
        if (classJobId is 2 or 20) // PGL/MNK
        {
            AddStrategy(strategies, "RoF", "Automatic");
            AddStrategy(strategies, "BH", "Automatic");
            AddStrategy(strategies, "RoW", "Automatic");
            AddStrategy(strategies, "AOE", "AOE");
            AddStrategy(strategies, "Targeting", "Manual");
        }
        else if (classJobId is 3 or 21) // MRD/WAR - VeynWAR has no Targeting track
        {
            AddStrategy(strategies, "AOE", "AutoFinishCombo");
        }
        else if (classJobId is 7 or 25 or 40) // THM/BLM and SGE have no Buffs track in VBM Default
        {
            AddStrategy(strategies, "AOE", "AOE");
            AddStrategy(strategies, "Targeting", "Manual");
        }
        else
        {
            AddStrategy(strategies, "Buffs", "Automatic");
            AddStrategy(strategies, "AOE", "AOE");
            AddStrategy(strategies, "Targeting", "Manual");
        }

        var modules = new JsonObject
        {
            [moduleType] = strategies,
        };

        var root = new JsonObject
        {
            ["Name"] = LootHunterPresetName,
            ["Modules"] = modules,
        };

        return root.ToJsonString();
    }

    private static void AddStrategy(JsonArray strategies, string track, string option)
    {
        strategies.Add(new JsonObject
        {
            ["Track"] = track,
            ["Option"] = option,
        });
    }
}
