using System.Text.Json.Nodes;
using Dalamud.Game.Command;
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
    private readonly ICallGateSubscriber<int> bossForbiddenZonesCount;
    private readonly ICallGateSubscriber<string> bossAiGetPreset;
    private readonly ICallGateSubscriber<string, object> bossAiSetPreset;
    private readonly ICommandManager commandManager;
    private readonly ITargetManager targetManager;
    private readonly IPlayerState playerState;
    private readonly Configuration configuration;

    private uint preparedClassJobId;
    private Guid? wrathLease;
    private ActiveCombatProvider activeProvider;
    private bool avoidanceActive;
    private string? activePresetBeforeAvoidance;
    private string aiPresetBeforeAvoidance = string.Empty;

    public CombatProvider(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        ITargetManager targetManager,
        IPlayerState playerState,
        Configuration configuration)
    {
        this.commandManager = commandManager;
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
        bossForbiddenZonesCount = pluginInterface.GetIpcSubscriber<int>("BossMod.Hints.ForbiddenZonesCount");
        bossAiGetPreset = pluginInterface.GetIpcSubscriber<string>("BossMod.AI.GetPreset");
        bossAiSetPreset = pluginInterface.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset");
    }

    public string Name => activeProvider switch
    {
        ActiveCombatProvider.WrathCombo => "Wrath Combo",
        ActiveCombatProvider.BossModReborn => "BossMod Reborn",
        _ => IsWrathAvailable ? "Wrath Combo" : "BossMod Reborn",
    };

    public bool IsAvailable
        => IsWrathAvailable || IsBossModAvailable;

    public string? AvailabilityError
        => IsAvailable
            ? null
            : "Wrath Combo IPC is unavailable. Install/enable Wrath Combo, or install/enable BossMod Reborn with preset IPC available.";

    public bool IsAreaAvoidanceAvailable
        => bossForbiddenZonesCount.HasFunction
           && bossAiGetPreset.HasFunction
           && bossAiSetPreset.HasAction
           && commandManager.Commands.ContainsKey("/bmrai");

    public bool IsControllingMovement => avoidanceActive;

    public bool IsAvoidingAreaAttack => avoidanceActive && SafeBossForbiddenZonesCount() > 0;

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

    public string? BeginEncounter()
    {
        if (!configuration.AvoidAreaAttacks || avoidanceActive)
            return null;
        if (!IsAreaAvoidanceAvailable)
            return "BossModReborn AI is unavailable; LootHunter cannot avoid area attacks.";

        activePresetBeforeAvoidance = SafeBossGetActivePreset();
        aiPresetBeforeAvoidance = SafeBossGetAiPreset();

        try
        {
            // With Wrath handling actions, an empty BossMod AI preset keeps BossMod focused
            // on movement and its outdoor forbidden zones. The BossMod combat fallback uses
            // the same preset for both movement and actions.
            var encounterPreset = activeProvider == ActiveCombatProvider.BossModReborn
                ? GetRequestedBossModPresetName()
                : string.Empty;
            bossAiSetPreset.InvokeAction(encounterPreset);

            if (!commandManager.ProcessCommand("/bmrai on"))
            {
                RestoreBossModStateAfterAvoidance();
                return "BossModReborn rejected the command to enable AI movement.";
            }

            avoidanceActive = true;
            return null;
        }
        catch (Exception ex)
        {
            RestoreBossModStateAfterAvoidance();
            return $"BossModReborn AI movement could not start: {ex.Message}";
        }
    }

    public void SetMovementPaused(bool paused)
    {
        if (!avoidanceActive)
            return;
        try { commandManager.ProcessCommand(paused ? "/bmrai off" : "/bmrai on"); }
        catch { }
    }

    public void EndEncounter()
    {
        if (!avoidanceActive)
            return;

        avoidanceActive = false;
        try { commandManager.ProcessCommand("/bmrai off"); }
        catch { }
        RestoreBossModStateAfterAvoidance();
    }

    public void EndSession()
    {
        EndEncounter();
        var lease = wrathLease;
        wrathLease = null;
        activeProvider = ActiveCombatProvider.None;
        if (lease is not null)
        {
            SetWrathRotationEnabled(lease.Value, false);
            ReleaseWrathLease(lease.Value);
        }
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

        if (wrathLease is not { } lease)
            return new(false, "LootHunter's WrathCombo autorotation lease is no longer active.");

        targetManager.Target = target;
        if (!await WaitForWrathJobReadyAsync(cancellationToken))
            return new(false, "WrathCombo did not finish preparing the current job for autorotation.");
        cancellationToken.ThrowIfCancellationRequested();
        if (wrathLease != lease)
            return new(false, "LootHunter's WrathCombo autorotation lease ended before combat began.");
        if (!SetWrathRotationEnabled(lease, true))
            return new(false, "WrathCombo could not enable autorotation for combat.");

        try
        {
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
        finally
        {
            // The lease remains prepared between kills, but autorotation must not run
            // while LootHunter is traveling past other monsters or approaching the next one.
            if (wrathLease == lease)
                SetWrathRotationEnabled(lease, false);
        }
    }

    private async Task<CombatResult> KillWithBossModAsync(IBattleNpc target, CancellationToken cancellationToken)
    {
        if (PrepareBossModForSession() is { Length: > 0 } prepareError)
            return new(false, prepareError);
        if (target.CurrentHp == 0)
            return new(true);

        var originalPreset = SafeBossGetActivePreset();
        var requestedPreset = GetRequestedBossModPresetName();
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

            // Keep the lease dormant while LootHunter travels. KillWithWrathAsync enables
            // autorotation only after navigation has stopped inside combat range.
            if (!SetWrathRotationEnabled(lease.Value, false))
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
            if (!SetWrathRotationEnabled(lease.Value, false))
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

    private bool SetWrathRotationEnabled(Guid lease, bool enabled)
    {
        try { return IsSuccessful(wrathSetAutoRotationState.InvokeFunc(lease, enabled)); }
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

    private string SafeBossGetAiPreset()
    {
        try { return bossAiGetPreset.HasFunction ? bossAiGetPreset.InvokeFunc() : string.Empty; }
        catch { return string.Empty; }
    }

    private int SafeBossForbiddenZonesCount()
    {
        try { return bossForbiddenZonesCount.HasFunction ? bossForbiddenZonesCount.InvokeFunc() : 0; }
        catch { return 0; }
    }

    private string GetRequestedBossModPresetName()
    {
        var configuredPreset = configuration.BossModPresetName.Trim();
        return string.IsNullOrWhiteSpace(configuredPreset) ? LootHunterPresetName : configuredPreset;
    }

    private void RestoreBossModStateAfterAvoidance()
    {
        try
        {
            if (bossAiSetPreset.HasAction)
                bossAiSetPreset.InvokeAction(aiPresetBeforeAvoidance);
        }
        catch { }

        try
        {
            if (!string.IsNullOrWhiteSpace(activePresetBeforeAvoidance))
                bossSetActivePreset.InvokeFunc(activePresetBeforeAvoidance);
            else if (bossClearActivePreset.HasFunction)
                bossClearActivePreset.InvokeFunc();
        }
        catch { }

        activePresetBeforeAvoidance = null;
        aiPresetBeforeAvoidance = string.Empty;
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
