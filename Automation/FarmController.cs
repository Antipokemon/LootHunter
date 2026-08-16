using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using LootHunter.Models;
using LootHunter.Services;

namespace LootHunter.Automation;

public sealed class FarmController
{
    private readonly Configuration configuration;
    private readonly IInventoryService inventory;
    private readonly IMobDropDatabase database;
    private readonly IRoutePlanner planner;
    private readonly ILevelSafetyService levelSafety;
    private readonly ITravelService travel;
    private readonly INavigationService navigation;
    private readonly IMountService mount;
    private readonly ITargetService targets;
    private readonly ICombatProvider combat;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IDutyState dutyState;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly FarmSession session = new();
    private CancellationTokenSource? cancellation;
    private Task? runTask;
    private volatile bool pauseRequested;
    private readonly Dictionary<uint, uint> sessionStartCounts = [];
    private readonly Dictionary<uint, uint> goals = [];
    private readonly HashSet<MobSourceKey> excludedSources = [];

    public FarmController(
        Configuration configuration,
        IInventoryService inventory,
        IMobDropDatabase database,
        IRoutePlanner planner,
        ILevelSafetyService levelSafety,
        ITravelService travel,
        INavigationService navigation,
        IMountService mount,
        ITargetService targets,
        ICombatProvider combat,
        IClientState clientState,
        IObjectTable objectTable,
        IDutyState dutyState,
        IFramework framework,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.inventory = inventory;
        this.database = database;
        this.planner = planner;
        this.levelSafety = levelSafety;
        this.travel = travel;
        this.navigation = navigation;
        this.mount = mount;
        this.targets = targets;
        this.combat = combat;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.dutyState = dutyState;
        this.framework = framework;
        this.log = log;
    }

    public FarmSession Session => session;

    public Task StartAsync(LootList list)
    {
        if (runTask is { IsCompleted: false })
            return runTask;

        session.Reset();
        excludedSources.Clear();
        pauseRequested = false;
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        // Run the full automation coroutine through Dalamud's framework scheduler.
        // IObjectTable, ITargetManager, IAetheryteList and several game-state APIs are
        // main-thread-only, and IFramework.Run keeps async continuations on that thread.
        runTask = framework.Run(() => RunAsync(CloneList(list), cancellation.Token), cancellation.Token);
        return runTask;
    }

    public void Pause()
    {
        if (!session.IsRunning || session.State == FarmState.Paused)
            return;
        pauseRequested = true;
        navigation.Stop();
        SetState(FarmState.Paused, "Paused");
    }

    public void Resume()
    {
        if (!session.IsRunning || !pauseRequested)
            return;
        pauseRequested = false;
        SetState(FarmState.Replanning, "Resuming");
    }

    public void Stop()
    {
        pauseRequested = false;
        navigation.Stop();
        travel.Abort();
        cancellation?.Cancel();
    }

    private async Task RunAsync(LootList list, CancellationToken token)
    {
        try
        {
            SetState(FarmState.Validating, "Validating loot list and dependencies");
            ValidatePreflight(list);
            BuildGoals(list);
            UpdateProgress();

            if (CalculateRequiredQuantities().Count == 0)
            {
                SetState(FarmState.Completed, "Loot list is already complete");
                return;
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(token);

                var required = CalculateRequiredQuantities();
                UpdateProgress();
                if (required.Count == 0)
                {
                    SetState(FarmState.Completed, "Loot list complete");
                    return;
                }

                SetState(FarmState.Planning, "Building the next farm route");
                var plan = planner.BuildPlan(list, required, clientState.TerritoryType, excludedSources);
                if (plan.Targets.Count == 0)
                    throw new InvalidOperationException("No usable monster source could be planned for the remaining loot list.");

                var attemptedTarget = false;
                foreach (var target in plan.Targets)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(token);

                    required = CalculateRequiredQuantities();
                    if (!target.RelevantDropItemIds.Any(required.ContainsKey))
                        continue;

                    attemptedTarget = true;
                    if (!await FarmTargetAsync(target, token))
                        continue;

                    required = CalculateRequiredQuantities();
                    UpdateProgress();
                    if (required.Count == 0)
                    {
                        SetState(FarmState.Completed, "Loot list complete");
                        return;
                    }

                    // Re-plan as soon as inventory changes or a shared-drop target is no longer useful.
                    break;
                }

                if (!attemptedTarget)
                    throw new InvalidOperationException("All remaining planned targets were unavailable or unsafe.");

                SetState(FarmState.Replanning, "Re-evaluating remaining quantities and route");
            }
        }
        catch (OperationCanceledException)
        {
            SetState(FarmState.Idle, "Stopped");
        }
        catch (Exception ex)
        {
            session.LastError = ex;
            SetState(FarmState.Error, ex.Message);
            log.Error(ex, "LootHunter farm session failed.");
        }
        finally
        {
            navigation.Stop();
            travel.Abort();
            pauseRequested = false;
        }
    }

    private void ValidatePreflight(LootList list)
    {
        if (!clientState.IsLoggedIn || objectTable.LocalPlayer is null)
            throw new InvalidOperationException("Log into a character before starting LootHunter.");
        if (objectTable.LocalPlayer.CurrentHp == 0)
            throw new InvalidOperationException("Your character is dead.");
        if (dutyState.ContentFinderCondition.RowId != 0)
            throw new InvalidOperationException("LootHunter only farms ordinary open-world monsters and cannot start inside a duty.");
        if (!levelSafety.IsCombatJob(out var jobError))
            throw new InvalidOperationException(jobError);
        if (!database.IsReady)
            throw new InvalidOperationException(database.IsLoading
                ? "Monster-drop data is still loading. Try again in a moment."
                : database.LoadError ?? "Monster-drop data is not ready.");
        database.RefreshTravelDestinations();
        if (!travel.IsAvailable)
            throw new InvalidOperationException("Lifestream IPC is unavailable. Install and enable Lifestream.");
        if (!navigation.IsAvailable)
            throw new InvalidOperationException("vnavmesh IPC is unavailable. Install and enable vnavmesh.");
        if (!combat.IsAvailable)
            throw new InvalidOperationException(combat.AvailabilityError ?? "BossModReborn IPC is unavailable.");
        if (combat.AvailabilityError is { Length: > 0 } combatError)
            throw new InvalidOperationException(combatError);
        if (inventory.GetFreeNormalInventorySlots() <= 0)
            throw new InvalidOperationException("Your normal inventory is full.");

        var entries = list.Items.Where(x => x.Enabled && x.ItemId != 0 && x.Quantity > 0).ToList();
        if (entries.Count == 0)
            throw new InvalidOperationException("The selected loot list has no enabled items with a positive quantity.");

        foreach (var entry in entries)
        {
            var sources = database.GetSourcesForItem(entry.ItemId)
                .Where(x => entry.PreferredBNpcNameId is null || x.BNpcNameId == entry.PreferredBNpcNameId)
                .Where(x => entry.PreferredTerritoryId is null || x.TerritoryId == entry.PreferredTerritoryId)
                .ToList();
            if (sources.Count == 0)
                throw new InvalidOperationException($"No usable open-world monster source was found for {database.GetItemName(entry.ItemId)}.");

            foreach (var source in sources.Where(x => x.MobLevel is null).Take(1))
                session.AddWarning($"{source.MobName}: level is unknown in the static drop data; LootHunter will verify the live monster level before combat.");
        }
    }

    private void BuildGoals(LootList list)
    {
        sessionStartCounts.Clear();
        goals.Clear();

        foreach (var entry in list.Items.Where(x => x.Enabled && x.ItemId != 0 && x.Quantity > 0))
        {
            var current = inventory.GetItemCount(entry.ItemId);
            sessionStartCounts[entry.ItemId] = current;
            goals[entry.ItemId] = list.QuantityMode == QuantityMode.GatherAdditional
                ? SaturatingAdd(current, entry.Quantity)
                : entry.Quantity;
        }
    }

    private async Task<bool> FarmTargetAsync(FarmTarget target, CancellationToken token)
    {
        session.CurrentMobName = target.MobName;
        session.CurrentTerritoryName = target.TerritoryName;
        session.CurrentItemId = target.ItemId;
        session.CurrentClusterCount = target.Clusters.Count;

        var staticSafety = levelSafety.Check(target);
        if (!staticSafety.IsSafe)
        {
            var warning = staticSafety.Message;
            session.AddWarning(warning);
            if (configuration.SkipUnsafeTargets)
            {
                excludedSources.Add(new MobSourceKey(target.BNpcNameId, target.TerritoryId));
                session.StatusMessage = $"Skipping {target.MobName}: {warning}";
                return false;
            }
        }

        if (clientState.TerritoryType != target.TerritoryId)
        {
            if (target.NearestAetheryte is null)
            {
                session.AddWarning($"{target.MobName}: no unlocked aetheryte was found for {target.TerritoryName}.");
                excludedSources.Add(new MobSourceKey(target.BNpcNameId, target.TerritoryId));
                return false;
            }

            SetState(FarmState.Teleporting, $"Teleporting to {target.NearestAetheryte.Name} for {target.MobName}");
            if (!await travel.TeleportNearAsync(target, token))
            {
                session.AddWarning($"Could not teleport to {target.TerritoryName} for {target.MobName}.");
                excludedSources.Add(new MobSourceKey(target.BNpcNameId, target.TerritoryId));
                return false;
            }
        }

        SetState(FarmState.WaitingForZone, "Waiting for navigation data");
        if (!await navigation.WaitUntilReadyAsync(token))
        {
            session.AddWarning($"vnavmesh did not become ready in {target.TerritoryName}; skipping this source for the current session.");
            excludedSources.Add(new MobSourceKey(target.BNpcNameId, target.TerritoryId));
            return false;
        }

        foreach (var cluster in OrderClustersByDistance(target.Clusters))
        {
            token.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(token);
            session.CurrentClusterIndex = cluster.AreaIndex;

            for (var cycle = 1; cycle <= Math.Max(1, configuration.MaxEmptyClusterCycles); cycle++)
            {
                var sawMobThisCycle = false;
                var orderedPoints = OrderSpawnPointsByDistance(cluster.SpawnPoints);

                foreach (var spawnPoint in orderedPoints)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(token);

                    if (!TargetStillUseful(target))
                        return true;

                    var localTarget = targets.FindTarget(target.BNpcNameId, objectTable.LocalPlayer?.Position, 90f);
                    if (localTarget is not null)
                    {
                        sawMobThisCycle = true;
                        if (!await KillAndRecordAsync(target, localTarget, token))
                            return false;
                        if (!TargetStillUseful(target))
                            return true;

                        // Prefer additional nearby spawns before moving again.
                        while ((localTarget = targets.FindTarget(target.BNpcNameId, objectTable.LocalPlayer?.Position, 90f)) is not null)
                        {
                            token.ThrowIfCancellationRequested();
                            await WaitIfPausedAsync(token);
                            if (!await KillAndRecordAsync(target, localTarget, token))
                                return false;
                            if (!TargetStillUseful(target))
                                return true;
                        }
                    }

                    var snapped = navigation.SnapToFloor(spawnPoint) ?? spawnPoint;
                    var player = objectTable.LocalPlayer;
                    if (player is null)
                        return false;

                    var distance = Vector3.Distance(player.Position, snapped);
                    if (configuration.AutoMount && distance >= configuration.AutoMountMinimumDistance && !mount.IsMounted)
                    {
                        SetState(FarmState.Mounting, $"Mounting for {distance:F0} yalms of travel");
                        await mount.MountAsync(token);
                    }

                    SetState(FarmState.Navigating, $"Moving through {target.MobName} spawn cluster {cluster.AreaIndex}/{target.Clusters.Count}");
                    var fly = configuration.UseFlight && mount.IsMounted && mount.CanFly;
                    if (!await navigation.MoveToAsync(snapped, 8f, fly, token))
                        continue;

                    SetState(FarmState.SearchingForMob, $"Searching for {target.MobName}");
                    var battleNpc = targets.FindTarget(target.BNpcNameId, snapped, 75f)
                        ?? targets.FindTarget(target.BNpcNameId, objectTable.LocalPlayer?.Position, 90f);
                    if (battleNpc is null)
                        continue;

                    sawMobThisCycle = true;
                    if (!await KillAndRecordAsync(target, battleNpc, token))
                        return false;
                    if (!TargetStillUseful(target))
                        return true;
                }

                if (!sawMobThisCycle)
                    session.EmptySpawnCycles++;

                if (!TargetStillUseful(target))
                    return true;

                if (cycle < Math.Max(1, configuration.MaxEmptyClusterCycles))
                {
                    SetState(FarmState.SearchingForMob,
                        sawMobThisCycle
                            ? $"Waiting {configuration.RespawnWaitSeconds}s for {target.MobName} respawns"
                            : $"No {target.MobName} found; waiting {configuration.RespawnWaitSeconds}s before another cluster pass");
                    await DelayWithPauseAsync(TimeSpan.FromSeconds(Math.Max(1, configuration.RespawnWaitSeconds)), token);
                }
            }
        }

        session.AddWarning($"{target.MobName}: all known spawn clusters were exhausted; route will be re-planned.");
        if (HasAlternateSource(target))
            excludedSources.Add(new MobSourceKey(target.BNpcNameId, target.TerritoryId));
        else
            await DelayWithPauseAsync(TimeSpan.FromSeconds(Math.Max(1, configuration.RespawnWaitSeconds)), token);
        return true;
    }

    private async Task<bool> KillAndRecordAsync(FarmTarget farmTarget, IBattleNpc battleNpc, CancellationToken token)
    {
        navigation.Stop();

        var observedSafety = levelSafety.CheckObserved(battleNpc);
        if (!observedSafety.IsSafe)
        {
            session.AddWarning(observedSafety.Message);
            if (configuration.SkipUnsafeTargets)
            {
                excludedSources.Add(new MobSourceKey(farmTarget.BNpcNameId, farmTarget.TerritoryId));
                return false;
            }
        }

        var player = objectTable.LocalPlayer;
        if (player is null)
            return false;

        if (Vector3.Distance(player.Position, battleNpc.Position) > configuration.CombatApproachDistance + 1f)
        {
            SetState(FarmState.Navigating, $"Approaching {farmTarget.MobName}");
            if (!await navigation.MoveToAsync(battleNpc.Position, Math.Max(1.5f, configuration.CombatApproachDistance), false, token))
            {
                session.AddWarning($"Could not approach {farmTarget.MobName}; skipping this source for the current session.");
                excludedSources.Add(new MobSourceKey(farmTarget.BNpcNameId, farmTarget.TerritoryId));
                return false;
            }
        }

        if (mount.IsMounted)
        {
            SetState(FarmState.Mounting, "Dismounting for combat");
            if (!await mount.DismountAsync(token))
            {
                session.AddWarning($"Could not dismount before fighting {farmTarget.MobName}; skipping this source for the current session.");
                excludedSources.Add(new MobSourceKey(farmTarget.BNpcNameId, farmTarget.TerritoryId));
                return false;
            }
        }

        var before = farmTarget.RelevantDropItemIds.ToDictionary(x => x, inventory.GetItemCount);
        targets.SetTarget(battleNpc);
        SetState(FarmState.EngagingMob, $"Engaging {farmTarget.MobName}");
        var result = await combat.KillAsync(battleNpc, token);
        if (!result.Success)
        {
            session.AddWarning(result.Error ?? $"Combat failed against {farmTarget.MobName}.");
            excludedSources.Add(new MobSourceKey(farmTarget.BNpcNameId, farmTarget.TerritoryId));
            return false;
        }

        session.Kills++;
        SetState(FarmState.WaitingForLoot, "Waiting for loot to settle");
        await DelayWithPauseAsync(TimeSpan.FromMilliseconds(Math.Max(250, configuration.LootSettleMilliseconds)), token);

        foreach (var itemId in farmTarget.RelevantDropItemIds)
        {
            var after = inventory.GetItemCount(itemId);
            var prior = before.GetValueOrDefault(itemId);
            if (after > prior)
                session.DropsObtained += checked((int)Math.Min((uint)int.MaxValue, after - prior));
        }

        UpdateProgress();
        return true;
    }

    private bool HasAlternateSource(FarmTarget target)
    {
        var required = CalculateRequiredQuantities();
        foreach (var itemId in target.RelevantDropItemIds.Where(required.ContainsKey))
        {
            if (database.GetSourcesForItem(itemId).Any(source =>
                    (source.BNpcNameId != target.BNpcNameId || source.TerritoryId != target.TerritoryId) &&
                    !excludedSources.Contains(new MobSourceKey(source.BNpcNameId, source.TerritoryId))))
                return true;
        }
        return false;
    }

    private bool TargetStillUseful(FarmTarget target)
    {
        var required = CalculateRequiredQuantities();
        return target.RelevantDropItemIds.Any(required.ContainsKey);
    }

    private Dictionary<uint, uint> CalculateRequiredQuantities()
    {
        var result = new Dictionary<uint, uint>();
        foreach (var (itemId, goal) in goals)
        {
            var current = inventory.GetItemCount(itemId);
            if (current < goal)
                result[itemId] = goal - current;
        }
        return result;
    }

    private void UpdateProgress()
    {
        session.SetProgress(goals
            .Select(x =>
            {
                var current = inventory.GetItemCount(x.Key);
                return new ItemProgress(
                    x.Key,
                    database.GetItemName(x.Key),
                    current,
                    x.Value,
                    current >= x.Value ? 0u : x.Value - current);
            })
            .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase));
    }

    private IReadOnlyList<SpawnCluster> OrderClustersByDistance(IReadOnlyList<SpawnCluster> clusters)
    {
        var position = objectTable.LocalPlayer?.Position;
        return position is null
            ? clusters
            : clusters.OrderBy(x => Vector3.Distance(position.Value, x.Center)).ToList();
    }

    private IReadOnlyList<Vector3> OrderSpawnPointsByDistance(IReadOnlyList<Vector3> points)
    {
        var position = objectTable.LocalPlayer?.Position;
        return position is null
            ? points
            : points.OrderBy(x => Vector3.Distance(position.Value, x)).ToList();
    }

    private async Task WaitIfPausedAsync(CancellationToken token)
    {
        while (pauseRequested)
        {
            token.ThrowIfCancellationRequested();
            if (session.State != FarmState.Paused)
                SetState(FarmState.Paused, "Paused");
            await Task.Delay(100, token);
        }
    }

    private async Task DelayWithPauseAsync(TimeSpan delay, CancellationToken token)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            await WaitIfPausedAsync(token);
            var slice = remaining > TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : remaining;
            await Task.Delay(slice, token);
            remaining -= slice;
        }
    }

    private void SetState(FarmState state, string message)
    {
        session.State = state;
        session.StatusMessage = message;
    }

    private static uint SaturatingAdd(uint left, uint right)
        => uint.MaxValue - left < right ? uint.MaxValue : left + right;

    private static LootList CloneList(LootList source)
        => new()
        {
            Id = source.Id,
            Name = source.Name,
            Enabled = source.Enabled,
            QuantityMode = source.QuantityMode,
            Items = source.Items.Select(x => new LootListEntry
            {
                ItemId = x.ItemId,
                Quantity = x.Quantity,
                Enabled = x.Enabled,
                PreferredBNpcNameId = x.PreferredBNpcNameId,
                PreferredTerritoryId = x.PreferredTerritoryId,
            }).ToList(),
        };
}
