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
    private const string InternalPluginName = "LootHunter";
    private const string PluginName = "LootHunter";

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

    private readonly ICallGateSubscriber<bool> wrathIpcReady;
    private readonly ICallGateSubscriber<string, string, Guid?> wrathRegisterForLease;
    private readonly ICallGateSubscriber<Guid, bool, WrathSetResult> wrathSetAutoRotationState;
    private readonly ICallGateSubscriber<Guid, WrathSetResult> wrathSetCurrentJobAutoRotationReady;
    private readonly ICallGateSubscriber<bool> wrathIsCurrentJobAutoRotationReady;
    private readonly ICallGateSubscriber<Guid, WrathAutoRotationConfigOption, object, WrathSetResult> wrathSetAutoRotationConfigState;
    private readonly ICallGateSubscriber<Guid, object> wrathReleaseControl;
    private readonly ICallGateSubscriber<string, string?> bossGetPreset;
    private readonly ICallGateSubscriber<string, bool, bool> bossCreatePreset;
    private readonly ICallGateSubscriber<string> bossGetActivePreset;
    private readonly ICallGateSubscriber<string, bool> bossSetActivePreset;
    private readonly ICallGateSubscriber<bool> bossClearActivePreset;
    private readonly ITargetManager targetManager;
    private readonly IPlayerState playerState;
    private readonly Configuration configuration;

    private uint preparedClassJobId;
    private Guid? wrathLease;
    private ActiveCombatProvider activeProvider;

    public CombatProvider(
        IDalamudPluginInterface pluginInterface,
        ITargetManager targetManager,
        IPlayerState playerState,
        Configuration configuration)
    {
        this.targetManager = targetManager;
        this.playerState = playerState;
        this.configuration = configuration;
        wrathIpcReady = pluginInterface.GetIpcSubscriber<bool>("WrathCombo.IPCReady");
        wrathRegisterForLease = pluginInterface.GetIpcSubscriber<string, string, Guid?>("WrathCombo.RegisterForLease");
        wrathSetAutoRotationState = pluginInterface.GetIpcSubscriber<Guid, bool, WrathSetResult>("WrathCombo.SetAutoRotationState");
        wrathSetCurrentJobAutoRotationReady = pluginInterface.GetIpcSubscriber<Guid, WrathSetResult>("WrathCombo.SetCurrentJobAutoRotationReady");
        wrathIsCurrentJobAutoRotationReady = pluginInterface.GetIpcSubscriber<bool>("WrathCombo.IsCurrentJobAutoRotationReady");
        wrathSetAutoRotationConfigState = pluginInterface.GetIpcSubscriber<Guid, WrathAutoRotationConfigOption, object, WrathSetResult>("WrathCombo.SetAutoRotationConfigState");
        wrathReleaseControl = pluginInterface.GetIpcSubscriber<Guid, object>("WrathCombo.ReleaseControl");
        bossGetPreset = pluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");
        bossCreatePreset = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        bossGetActivePreset = pluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
        bossSetActivePreset = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        bossClearActivePreset = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
    }

    public string Name => activeProvider switch
    {
        ActiveCombatProvider.WrathCombo => "WrathCombo",
        ActiveCombatProvider.BossModReborn => "BossModReborn",
        _ => IsWrathAvailable ? "WrathCombo" : "BossModReborn",
    };

    public bool IsAvailable
        => IsWrathAvailable || IsBossModAvailable;

    public string? AvailabilityError
        => IsAvailable
            ? null
            : "WrathCombo IPC is unavailable. Install/enable WrathCombo, or install/enable BossModReborn with preset IPC available.";

    private bool IsWrathAvailable
        => wrathIpcReady.HasFunction
           && wrathRegisterForLease.HasFunction
           && wrathSetAutoRotationState.HasFunction
           && wrathSetCurrentJobAutoRotationReady.HasFunction
           && wrathIsCurrentJobAutoRotationReady.HasFunction
           && wrathSetAutoRotationConfigState.HasFunction
           && wrathReleaseControl.HasAction
           && SafeWrathIpcReady();

    private bool IsBossModAvailable
        => bossGetPreset.HasFunction
           && bossCreatePreset.HasFunction
           && bossGetActivePreset.HasFunction
           && bossSetActivePreset.HasFunction
           && bossClearActivePreset.HasFunction;

    public string? PrepareForSession()
    {
        EndSession();

        if (IsWrathAvailable)
        {
            wrathLease = PrepareWrathForSession();
            if (wrathLease is not null)
            {
                activeProvider = ActiveCombatProvider.WrathCombo;
                return null;
            }
        }

        if (!IsBossModAvailable)
            return IsWrathAvailable
                ? "WrathCombo IPC is available, but it could not grant LootHunter autorotation control."
                : AvailabilityError;

        var bossModError = PrepareBossModForSession();
        if (bossModError is null)
            activeProvider = ActiveCombatProvider.BossModReborn;
        return bossModError;
    }

    public async Task<CombatResult> KillAsync(IBattleNpc target, CancellationToken cancellationToken)
    {
        if (activeProvider == ActiveCombatProvider.None)
        {
            if (PrepareForSession() is { Length: > 0 } prepareError)
                return new(false, prepareError);
        }

        return activeProvider == ActiveCombatProvider.WrathCombo
            ? await KillWithWrathAsync(target, cancellationToken)
            : await KillWithBossModAsync(target, cancellationToken);
    }

    public void EndSession()
    {
        var lease = wrathLease;
        wrathLease = null;
        activeProvider = ActiveCombatProvider.None;
        if (lease is not null)
            ReleaseWrathLease(lease.Value);
    }

    private string? PrepareBossModForSession()
    {
        var requestedPreset = configuration.BossModPresetName.Trim();
        if (!string.IsNullOrWhiteSpace(requestedPreset))
        {
            preparedClassJobId = 0;
            return SafeBossGetPreset(requestedPreset) is null
                ? $"BossModReborn autorotation preset '{requestedPreset}' was not found."
                : null;
        }

        if (!playerState.IsLoaded)
            return "Player job data is not loaded yet; LootHunter cannot prepare BossModReborn combat.";

        var classJobId = playerState.ClassJob.RowId;
        if (!RotationModuleByClassJob.TryGetValue(classJobId, out var moduleType))
            return $"LootHunter does not have a BossModReborn rotation mapping for class/job ID {classJobId}. Set a BossModReborn preset explicitly in LootHunter settings.";

        // Avoid rewriting BossMod's user preset database on every kill in the same job.
        if (preparedClassJobId == classJobId && SafeBossGetPreset(LootHunterPresetName) is not null)
            return null;

        try
        {
            var lootHunterPreset = BuildLootHunterPreset(classJobId, moduleType);
            if (!bossCreatePreset.InvokeFunc(lootHunterPreset, true))
            {
                preparedClassJobId = 0;
                return $"BossModReborn rejected LootHunter's combat preset for class/job ID {classJobId}. Set a BossModReborn preset explicitly in LootHunter settings.";
            }

            if (SafeBossGetPreset(LootHunterPresetName) is null)
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

    private async Task<CombatResult> KillWithWrathAsync(IBattleNpc target, CancellationToken cancellationToken)
    {
        if (target.CurrentHp == 0)
            return new(true);

        if (wrathLease is null)
            return new(false, "LootHunter's WrathCombo autorotation lease is no longer active.");

        targetManager.Target = target;
        if (!await WaitForWrathJobReadyAsync(cancellationToken))
            return new(false, "WrathCombo did not finish preparing the current job for autorotation.");

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, configuration.CombatTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.CurrentHp == 0)
                return new(true);

            if (targetManager.Target?.GameObjectId != target.GameObjectId)
                targetManager.Target = target;

            await Task.Delay(100, cancellationToken);
        }

        return new(false, $"Combat timed out after {configuration.CombatTimeoutSeconds} seconds.");
    }

    private async Task<CombatResult> KillWithBossModAsync(IBattleNpc target, CancellationToken cancellationToken)
    {
        if (PrepareBossModForSession() is { Length: > 0 } prepareError)
            return new(false, prepareError);
        if (target.CurrentHp == 0)
            return new(true);

        var originalPreset = SafeBossGetActivePreset();
        var configuredPreset = configuration.BossModPresetName.Trim();
        var requestedPreset = string.IsNullOrWhiteSpace(configuredPreset) ? LootHunterPresetName : configuredPreset;
        var changedPreset = !string.Equals(originalPreset, requestedPreset, StringComparison.OrdinalIgnoreCase);

        if (changedPreset && !bossSetActivePreset.InvokeFunc(requestedPreset))
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
                        bossSetActivePreset.InvokeFunc(originalPreset);
                    else
                        bossClearActivePreset.InvokeFunc();
                }
                catch
                {
                    // Combat has already finished/aborted; restoration failure should not mask its result.
                }
            }
        }
    }

    private Guid? PrepareWrathForSession()
    {
        Guid? lease = null;
        try
        {
            lease = wrathRegisterForLease.InvokeFunc(InternalPluginName, PluginName);
            if (lease is null)
                return null;

            if (!IsSuccessful(wrathSetAutoRotationState.InvokeFunc(lease.Value, true)))
            {
                ReleaseWrathLease(lease.Value);
                return null;
            }
            if (!IsSuccessful(wrathSetCurrentJobAutoRotationReady.InvokeFunc(lease.Value)))
            {
                ReleaseWrathLease(lease.Value);
                return null;
            }

            if (!SetWrathConfig(lease.Value, WrathAutoRotationConfigOption.InCombatOnly, false)
                || !SetWrathConfig(lease.Value, WrathAutoRotationConfigOption.OnlyAttackInCombat, false)
                || !SetWrathConfig(lease.Value, WrathAutoRotationConfigOption.DpsAlwaysHardTarget, true)
                || !SetWrathConfig(lease.Value, WrathAutoRotationConfigOption.HealerAlwaysHardTarget, true))
            {
                ReleaseWrathLease(lease.Value);
                return null;
            }
            return lease;
        }
        catch
        {
            if (lease is not null)
                ReleaseWrathLease(lease.Value);
            return null;
        }
    }

    private async Task<bool> WaitForWrathJobReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (wrathIsCurrentJobAutoRotationReady.InvokeFunc())
                    return true;
            }
            catch
            {
                return false;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    private bool SetWrathConfig(Guid lease, WrathAutoRotationConfigOption option, object value)
    {
        try { return IsSuccessful(wrathSetAutoRotationConfigState.InvokeFunc(lease, option, value)); }
        catch { return false; }
    }

    private void ReleaseWrathLease(Guid lease)
    {
        try { wrathReleaseControl.InvokeAction(lease); }
        catch { }
    }

    private bool SafeWrathIpcReady()
    {
        try { return wrathIpcReady.InvokeFunc(); }
        catch { return false; }
    }

    private static bool IsSuccessful(WrathSetResult result)
        => result is WrathSetResult.Okay or WrathSetResult.OkayWorking;

    private string? SafeBossGetPreset(string name)
    {
        try { return bossGetPreset.HasFunction ? bossGetPreset.InvokeFunc(name) : null; }
        catch { return null; }
    }

    private string? SafeBossGetActivePreset()
    {
        try { return bossGetActivePreset.HasFunction ? bossGetActivePreset.InvokeFunc() : null; }
        catch { return null; }
    }

    private enum WrathSetResult
    {
        Okay = 0,
        OkayWorking = 1,
    }

    private enum WrathAutoRotationConfigOption
    {
        InCombatOnly = 0,
        OnlyAttackInCombat = 13,
        DpsAlwaysHardTarget = 19,
        HealerAlwaysHardTarget = 20,
    }

    private enum ActiveCombatProvider
    {
        None,
        WrathCombo,
        BossModReborn,
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
