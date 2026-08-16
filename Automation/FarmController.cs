using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using LootHunter.Models;
using LootHunter.Services;

namespace LootHunter.Automation;

public sealed class FarmController
{
    private const float CombatArrivalTolerance = 0.25f;
    private const float CombatReapproachTolerance = 0.75f;
    private const float MountedCombatHandoffDistance = 8f;
    private static readonly TimeSpan CombatPursuitGrace = TimeSpan.FromMilliseconds(750);

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
    private readonly Dictionary<MobSourceKey, int> navigationFailures = [];
    private LootList? activeList;

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
    public Guid? ActiveListId => session.IsRunning ? activeList?.Id : null;
    public bool RequiredPluginsAvailable
        => travel.IsAvailable
           && navigation.IsAvailable
           && combat.IsAvailable
           && (!configuration.AvoidAreaAttacks || combat.IsAreaAvoidanceAvailable);
    public IReadOnlyList<PluginRequirementStatus> PluginRequirements
    {
        get
        {
            var requirements = new List<PluginRequirementStatus>
            {
                new(
                    "Lifestream",
                    true,
                    travel.IsAvailable,
                    travel.IsAvailable ? "Ready for teleportation." : "Install and enable Lifestream.",
                    "Lifestream"),
                new(
                    "vnavmesh",
                    true,
                    navigation.IsAvailable,
                    navigation.IsAvailable ? "Ready for pathfinding and movement." : "Install and enable vnavmesh.",
                    "vnavmesh"),
            };

            if (combat.Name == "BossMod Reborn")
            {
                var available = combat.IsAvailable
                                && (!configuration.AvoidAreaAttacks || combat.IsAreaAvoidanceAvailable);
                requirements.Add(new(
                    "BossMod Reborn",
                    true,
                    available,
                    available
                        ? configuration.AvoidAreaAttacks
                            ? "Ready for combat rotation and area-attack avoidance."
                            : "Ready for combat rotation."
                        : configuration.AvoidAreaAttacks
                            ? "Install and enable BossMod Reborn for combat rotation and area-attack avoidance."
                            : combat.AvailabilityError ?? "Install and enable BossMod Reborn for combat rotation.",
                    "BossMod Reborn"));
            }
            else
            {
                requirements.Add(new(
                    "Wrath Combo",
                    true,
                    combat.IsAvailable,
                    combat.IsAvailable
                        ? "Ready for combat rotation."
                        : combat.AvailabilityError ?? "Install and enable Wrath Combo for combat rotation.",
                    "Wrath Combo"));

                if (configuration.AvoidAreaAttacks)
                {
                    requirements.Add(new(
                        "BossMod Reborn",
                        true,
                        combat.IsAreaAvoidanceAvailable,
                        combat.IsAreaAvoidanceAvailable
                            ? "Ready for area-attack avoidance."
                            : "Install and enable BossMod Reborn for area-attack avoidance.",
                        "BossMod Reborn"));
                }
            }

            return requirements;
        }
    }

    public Task StartAsync(LootList list)
    {
        if (runTask is { IsCompleted: false })
            return runTask;

        session.Reset();
        excludedSources.Clear();
        navigationFailures.Clear();
        pauseRequested = false;
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        activeList = list;
        // Run the full automation coroutine through Dalamud's framework scheduler.
        // IObjectTable, ITargetManager, IAetheryteList and several game-state APIs are
        // main-thread-only, and IFramework.Run keeps async continuations on that thread.
        runTask = framework.Run(() => RunAsync(list, cancellation.Token), cancellation.Token);
        return runTask;
    }

    public void Pause()
    {
        if (!session.IsRunning || session.State == FarmState.Paused)
            return;
        pauseRequested = true;
        navigation.Stop();
        combat.SetMovementPaused(true);
        SetState(FarmState.Paused, "Paused");
    }

    public void Resume()
    {
        if (!session.IsRunning || !pauseRequested)
            return;
        pauseRequested = false;
        combat.SetMovementPaused(false);
        SetState(FarmState.Replanning, "Resuming");
    }

    public void Stop()
    {
        pauseRequested = false;
        navigation.Stop();
        travel.Abort();
        combat.EndSession();
        cancellation?.Cancel();
    }

    private async Task RunAsync(LootList list, CancellationToken token)
    {
        try
        {
            SetState(FarmState.Validating, "Validating loot list and dependencies");
            await ValidatePreflightAsync(list, token);
            BuildGoals(list);
            UpdateProgress();

            if (CalculateRequiredQuantities().Count == 0)
            {
                await CompleteAsync("Loot list is already complete", token);
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
                    await CompleteAsync("Loot list complete", token);
                    return;
                }

                await EnsureRequiredSourcesReadyAsync(required.Keys, token);

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
                        await CompleteAsync("Loot list complete", token);
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
            combat.EndSession();
            pauseRequested = false;
            activeList = null;
        }
    }

    private async Task CompleteAsync(string completionMessage, CancellationToken token)
    {
        navigation.Stop();
        combat.EndSession();

        if (!configuration.TeleportOnCompletion)
        {
            SetState(FarmState.Completed, completionMessage);
            return;
        }

        var command = configuration.GetCompletionTeleportCommand();
        var label = configuration.GetCompletionTeleportLabel();
        if (string.IsNullOrWhiteSpace(command))
        {
            session.AddWarning("Completion teleport is set to Custom, but no Lifestream destination was entered.");
            SetState(FarmState.Completed, completionMessage);
            return;
        }

        SetState(FarmState.Teleporting, $"Returning to {label}");
        if (await travel.TeleportByLifestreamCommandAsync(command, token))
            SetState(FarmState.Completed, $"{completionMessage}; returned to {label}");
        else
        {
            session.AddWarning($"The loot list completed, but Lifestream could not travel to {label}.");
            SetState(FarmState.Completed, completionMessage);
        }
    }

    private async Task EnsureRequiredSourcesReadyAsync(IEnumerable<uint> requiredItemIds, CancellationToken token)
    {
        var itemIds = requiredItemIds.Distinct().ToList();
        var unresolved = itemIds.Where(itemId => database.GetSourcesForItem(itemId).Count == 0).ToList();
        if (unresolved.Count > 0)
        {
            SetState(FarmState.Validating, unresolved.Count == 1
                ? $"Looking up monster location for {database.GetItemName(unresolved[0])}"
                : $"Looking up monster locations for {unresolved.Count} newly added items");
            await database.EnsureSourcesResolvedAsync(unresolved, token);
        }

        database.RefreshTravelDestinations(itemIds);
    }

    private async Task ValidatePreflightAsync(LootList list, CancellationToken token)
    {
        if (!list.Enabled)
            throw new InvalidOperationException("Enable the selected loot list before starting LootHunter.");
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
        if (!travel.IsAvailable)
            throw new InvalidOperationException("Lifestream IPC is unavailable. Install and enable Lifestream.");
        if (!navigation.IsAvailable)
            throw new InvalidOperationException("vnavmesh IPC is unavailable. Install and enable vnavmesh.");
        if (!combat.IsAvailable)
            throw new InvalidOperationException(combat.AvailabilityError ?? "BossModReborn IPC is unavailable.");
        if (configuration.AvoidAreaAttacks && !combat.IsAreaAvoidanceAvailable)
            throw new InvalidOperationException("BossModReborn AI is unavailable. Install and enable BossModReborn or disable area-attack avoidance in Combat settings.");
        if (combat.PrepareForSession() is { Length: > 0 } combatError)
            throw new InvalidOperationException(combatError);
        if (inventory.GetFreeNormalInventorySlots() <= 0)
            throw new InvalidOperationException("Your normal inventory is full.");

        var entries = list.Items.Where(x => x.Enabled && x.ItemId != 0 && x.Quantity > 0).ToList();
        if (entries.Count == 0)
            throw new InvalidOperationException("The selected loot list has no enabled items with a positive quantity.");

        var unresolved = entries
            .Select(x => x.ItemId)
            .Where(itemId => database.GetSourcesForItem(itemId).Count == 0)
            .Distinct()
            .ToList();
        if (unresolved.Count > 0)
        {
            SetState(FarmState.Validating, unresolved.Count == 1
                ? $"Looking up monster location for {database.GetItemName(unresolved[0])}"
                : $"Looking up monster locations for {unresolved.Count} items");
            await database.EnsureSourcesResolvedAsync(unresolved, token);
        }

        var entryItemIds = entries.Select(x => x.ItemId).Distinct().ToList();
        SetState(FarmState.Validating, entryItemIds.Count == 1
            ? $"Verifying monster locations for {database.GetItemName(entryItemIds[0])}"
            : $"Verifying monster locations for {entryItemIds.Count} items");
        await database.EnsureSourcesResolvedAsync(entryItemIds, token, includeKnownSources: true);

        database.RefreshTravelDestinations(entryItemIds);

        foreach (var entry in entries)
        {
            var sources = database.GetSourcesForItem(entry.ItemId)
                .Where(x => entry.PreferredBNpcNameId is null || x.BNpcNameId == entry.PreferredBNpcNameId)
                .Where(x => entry.PreferredTerritoryId is null || x.TerritoryId == entry.PreferredTerritoryId)
                .ToList();
            if (sources.Count == 0)
            {
                var fallbackError = database.GetResolutionError(entry.ItemId);
                throw new InvalidOperationException(
                    $"No usable open-world monster source was found for {database.GetItemName(entry.ItemId)}" +
                    (string.IsNullOrWhiteSpace(fallbackError) ? "." : $". MonsterLoot fallback: {fallbackError}"));
            }

            foreach (var source in sources.Where(x => x.MobLevel is null).Take(1))
                session.AddWarning($"{source.MobName}: level is unknown in the static drop data; LootHunter will verify the live monster level before combat.");
        }
    }

    private void BuildGoals(LootList list)
    {
        sessionStartCounts.Clear();
        goals.Clear();

        SyncGoals(list);
    }

    private void SyncGoals(LootList list)
    {
        var entries = list.Items
            .Where(x => x.Enabled && x.ItemId != 0 && x.Quantity > 0)
            .GroupBy(x => x.ItemId)
            .Select(x => x.Last())
            .ToList();
        var activeItemIds = entries.Select(x => x.ItemId).ToHashSet();

        foreach (var removedItemId in goals.Keys.Where(x => !activeItemIds.Contains(x)).ToList())
            goals.Remove(removedItemId);

        foreach (var entry in entries)
        {
            var current = inventory.GetItemCount(entry.ItemId);
            sessionStartCounts.TryAdd(entry.ItemId, current);
            goals[entry.ItemId] = list.QuantityMode == QuantityMode.GatherAdditional
                ? SaturatingAdd(sessionStartCounts[entry.ItemId], entry.Quantity)
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

        // Inventory is authoritative. Recheck it before spending time or gil traveling.
        UpdateProgress();
        if (!TargetStillUseful(target))
            return true;
        EnsureInventorySpace();

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

            UpdateProgress();
            if (!TargetStillUseful(target))
                return true;
        }

        SetState(FarmState.WaitingForZone, "Waiting for navigation data");
        if (!await navigation.WaitUntilReadyAsync(token))
        {
            session.AddWarning($"vnavmesh did not become ready in {target.TerritoryName}; skipping this source for the current session.");
            excludedSources.Add(new MobSourceKey(target.BNpcNameId, target.TerritoryId));
            return false;
        }

        var sawAnyMobAtSource = false;
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

                    UpdateProgress();
                    if (!TargetStillUseful(target))
                        return true;
                    EnsureInventorySpace();

                    // Clear any currently visible targets before moving to the next marker.
                    while (FindNearbyTarget(target) is { } localTarget)
                    {
                        sawMobThisCycle = true;
                        sawAnyMobAtSource = true;
                        if (!await KillAndRecordAsync(target, localTarget, token))
                            return false;

                        UpdateProgress();
                        if (!TargetStillUseful(target))
                            return true;
                        EnsureInventorySpace();
                    }

                    var snapped = navigation.SnapToFloor(spawnPoint);
                    if (snapped is null)
                    {
                        if (RegisterNavigationFailure(target,
                                $"Spawn coordinate {FormatPosition(spawnPoint)} is not on reachable vnavmesh terrain."))
                            return false;
                        continue;
                    }

                    var reachedSpawn = false;
                    while (!reachedSpawn && TargetStillUseful(target))
                    {
                        token.ThrowIfCancellationRequested();
                        await WaitIfPausedAsync(token);

                        // Scan again immediately before issuing movement.
                        if (FindNearbyTarget(target) is { } beforeMoveTarget)
                        {
                            sawMobThisCycle = true;
                            sawAnyMobAtSource = true;
                            if (!await KillAndRecordAsync(target, beforeMoveTarget, token))
                                return false;
                            UpdateProgress();
                            continue;
                        }

                        await MountForTravelIfNeededAsync(snapped.Value, token);
                        SetState(FarmState.Navigating,
                            $"Moving through {target.MobName} spawn cluster {cluster.AreaIndex}/{target.Clusters.Count}; scanning en route");
                        var fly = configuration.UseFlight && mount.IsMounted && mount.CanFly;
                        var moveResult = await navigation.MoveToAsync(
                            snapped.Value,
                            8f,
                            fly,
                            token,
                            () => !TargetStillUseful(target) || FindNearbyTarget(target) is not null);

                        if (!TargetStillUseful(target))
                            return true;

                        switch (moveResult)
                        {
                            case NavigationMoveResult.Arrived:
                                reachedSpawn = true;
                                ClearNavigationFailures(target);
                                break;

                            case NavigationMoveResult.Interrupted:
                                // vnavmesh was stopped because a matching mob became visible.
                                // Fetch a fresh object wrapper after stopping, kill it, then resume
                                // toward the same spawn point if more drops are still required.
                                if (FindNearbyTarget(target) is { } interceptedTarget)
                                {
                                    sawMobThisCycle = true;
                                    sawAnyMobAtSource = true;
                                    if (!await KillAndRecordAsync(target, interceptedTarget, token))
                                        return false;
                                    UpdateProgress();
                                }
                                break;

                            case NavigationMoveResult.Failed:
                                if (RegisterNavigationFailure(target,
                                        $"Navigation failed or stalled near {FormatPosition(snapped.Value)}."))
                                    return false;
                                reachedSpawn = true; // abandon this point for this pass
                                break;
                        }
                    }

                    if (!TargetStillUseful(target))
                        return true;

                    SetState(FarmState.SearchingForMob, $"Searching for {target.MobName}");
                    var battleNpc = targets.FindTarget(target.BNpcNameId, snapped.Value, Math.Max(75f, configuration.MobScanRadius))
                        ?? FindNearbyTarget(target);
                    if (battleNpc is null)
                        continue;

                    sawMobThisCycle = true;
                    sawAnyMobAtSource = true;
                    if (!await KillAndRecordAsync(target, battleNpc, token))
                        return false;
                    UpdateProgress();
                    if (!TargetStillUseful(target))
                        return true;
                }

                if (!sawMobThisCycle)
                    session.EmptySpawnCycles++;

                UpdateProgress();
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

        if (!sawAnyMobAtSource)
        {
            var key = new MobSourceKey(target.BNpcNameId, target.TerritoryId);
            var spawnPointCount = CountSpawnPoints(target.Clusters);
            var unresolvedItems = target.RelevantDropItemIds
                .Where(CalculateRequiredQuantities().ContainsKey)
                .ToList();

            if (unresolvedItems.Count > 0)
            {
                SetState(FarmState.Validating, $"Looking up fresh spawn data for {target.MobName}");
                await database.EnsureSourcesResolvedAsync(unresolvedItems, token, includeKnownSources: true);
                database.RefreshTravelDestinations(unresolvedItems);

                var refreshed = unresolvedItems
                    .SelectMany(database.GetSourcesForItem)
                    .FirstOrDefault(x => x.BNpcNameId == target.BNpcNameId && x.TerritoryId == target.TerritoryId);
                if (refreshed is not null && CountSpawnPoints(refreshed.Clusters) > spawnPointCount)
                {
                    session.AddWarning($"{target.MobName}: no live monsters were found at the static spawn points, so LootHunter added fallback spawn data and will re-plan.");
                    return true;
                }
            }

            excludedSources.Add(key);
            session.AddWarning(
                $"{target.MobName}: no matching live monsters were found after {Math.Max(1, configuration.MaxEmptyClusterCycles)} complete spawn passes. " +
                "This source was disabled for the current farm session instead of retrying indefinitely.");
            return false;
        }

        session.AddWarning($"{target.MobName}: all known spawn clusters were exhausted; route will be re-planned.");
        if (HasAlternateSource(target))
            excludedSources.Add(new MobSourceKey(target.BNpcNameId, target.TerritoryId));
        else
            await DelayWithPauseAsync(TimeSpan.FromSeconds(Math.Max(1, configuration.RespawnWaitSeconds)), token);
        return true;
    }

    private async Task MountForTravelIfNeededAsync(Vector3 destination, CancellationToken token)
    {
        var player = objectTable.LocalPlayer;
        if (!configuration.AutoMount || mount.IsMounted || player is null)
            return;

        var distance = Vector3.Distance(player.Position, destination);
        if (distance < configuration.AutoMountMinimumDistance)
            return;

        SetState(FarmState.Mounting, $"Mounting for {distance:F0} yalms of travel");
        await mount.MountAsync(token);
    }

    private async Task<bool> KillAndRecordAsync(FarmTarget farmTarget, IBattleNpc battleNpc, CancellationToken token)
    {
        if (!await navigation.StopAsync(token))
        {
            RegisterNavigationFailure(farmTarget, $"Could not stop the active route for visible {farmTarget.MobName}.");
            return false;
        }

        // The inventory can change while traveling or while another kill is resolving.
        // Never engage another mob once all of its requested drops are already satisfied.
        UpdateProgress();
        if (!TargetStillUseful(farmTarget))
            return true;
        EnsureInventorySpace();

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

        var combatDistance = Math.Clamp(configuration.CombatApproachDistance, 1.5f, 15f);

        // Visible mobs bypass the spawn-point travel path, so they need their own
        // mount decision. This is especially important immediately after a kill.
        await MountForTravelIfNeededAsync(battleNpc.Position, token);

        // Use obstacle-aware navigation for the mounted portion, but leave enough
        // room for a deliberate final approach on foot after landing/dismounting.
        if (mount.IsMounted)
        {
            var fly = configuration.UseFlight && mount.CanFly;
            var handoffDistance = Math.Max(MountedCombatHandoffDistance, combatDistance + 2f);
            var mountedPlayer = objectTable.LocalPlayer;
            var distance = mountedPlayer is null
                ? float.MaxValue
                : DistanceToTarget(mountedPlayer.Position, battleNpc.Position, horizontalOnly: fly);

            if (distance > handoffDistance + CombatArrivalTolerance)
            {
                SetState(FarmState.Navigating, $"Moving into dismount range of {farmTarget.MobName}");
                var mountedApproach = await navigation.MoveToAsync(
                    battleNpc.Position,
                    stopDistance: handoffDistance,
                    fly: fly,
                    cancellationToken: token,
                    arrivalTolerance: CombatArrivalTolerance,
                    horizontalArrival: fly);
                if (mountedApproach != NavigationMoveResult.Arrived || !await navigation.StopAsync(token))
                {
                    RegisterNavigationFailure(farmTarget, $"Could not get into dismount range of visible {farmTarget.MobName}.");
                    return false;
                }
            }

            SetState(FarmState.Mounting, "Dismounting for combat");
            if (!await mount.DismountAsync(token))
            {
                session.AddWarning($"Could not dismount before fighting {farmTarget.MobName}; skipping this source for the current session.");
                excludedSources.Add(new MobSourceKey(farmTarget.BNpcNameId, farmTarget.TerritoryId));
                return false;
            }
        }

        var player = objectTable.LocalPlayer;
        if (player is null)
            return false;

        // Follow the live target position for the short final approach. This uses
        // vnavmesh's direct path follower, not a navmesh calculation to a stale point.
        targets.SetTarget(battleNpc);
        if (DistanceToTarget(player.Position, battleNpc.Position, horizontalOnly: true) > combatDistance + CombatArrivalTolerance)
        {
            SetState(FarmState.Navigating, $"Approaching {farmTarget.MobName} on foot");
            var approach = await navigation.MoveToMovingTargetAsync(
                () => battleNpc.CurrentHp > 0 ? battleNpc.Position : null,
                combatDistance,
                cancellationToken: token,
                arrivalTolerance: CombatArrivalTolerance);
            if (approach != NavigationMoveResult.Arrived)
            {
                RegisterNavigationFailure(farmTarget, $"Could not approach visible {farmTarget.MobName} on foot.");
                return false;
            }
        }

        if (!await navigation.StopAsync(token))
        {
            RegisterNavigationFailure(farmTarget, $"Could not stop after approaching visible {farmTarget.MobName}.");
            return false;
        }

        UpdateProgress();
        if (!TargetStillUseful(farmTarget))
            return true;
        EnsureInventorySpace();

        var before = farmTarget.RelevantDropItemIds
            .Where(goals.ContainsKey)
            .ToDictionary(x => x, inventory.GetItemCount);
        var inventoryVersionBeforeKill = inventory.ChangeVersion;

        targets.SetTarget(battleNpc);
        if (combat.BeginEncounter() is { Length: > 0 } avoidanceError)
        {
            session.AddWarning(avoidanceError);
            excludedSources.Add(new MobSourceKey(farmTarget.BNpcNameId, farmTarget.TerritoryId));
            return false;
        }

        try
        {
            IBattleNpc? combatTarget = battleNpc;
            var firstTarget = true;
            while (combatTarget is not null)
            {
                var targetName = GetBattleNpcName(combatTarget, farmTarget.MobName);
                if (!firstTarget)
                {
                    var additionalSafety = levelSafety.CheckObserved(combatTarget);
                    if (!additionalSafety.IsSafe)
                        session.AddWarning($"{additionalSafety.Message} It is already attacking the player, so LootHunter will finish the encounter.");
                    EnsureInventorySpace();
                }

                session.CurrentMobName = targetName;
                targets.SetTarget(combatTarget);
                SetState(FarmState.EngagingMob, firstTarget
                    ? $"Engaging {targetName}"
                    : $"Engaging additional attacker: {targetName}");

                var result = await KillWithPositioningAsync(farmTarget, combatTarget, combatDistance, token);
                if (!result.Success)
                {
                    session.AddWarning(result.Error ?? $"Combat failed against {targetName}.");
                    excludedSources.Add(new MobSourceKey(farmTarget.BNpcNameId, farmTarget.TerritoryId));
                    return false;
                }

                session.Kills++;
                firstTarget = false;
                combatTarget = await WaitForHostileTargetAsync(token);
            }
        }
        finally
        {
            combat.EndEncounter();
        }

        session.CurrentMobName = farmTarget.MobName;
        SetState(FarmState.WaitingForLoot, "Waiting for inventory to receive loot");
        await WaitForLootSettlementAsync(before, inventoryVersionBeforeKill, token);
        UpdateProgress();
        return true;
    }

    private async Task<CombatResult> KillWithPositioningAsync(
        FarmTarget farmTarget,
        IBattleNpc battleNpc,
        float combatDistance,
        CancellationToken token)
    {
        using var fightCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var combatTask = combat.KillAsync(battleNpc, fightCancellation.Token);
        string? positioningError = null;
        DateTime? outOfRangeSince = null;

        try
        {
            while (!combatTask.IsCompleted && battleNpc.CurrentHp > 0)
            {
                token.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(token);

                var player = objectTable.LocalPlayer;
                if (player is null)
                {
                    positioningError = "The local player became unavailable during combat.";
                    break;
                }

                var targetName = GetBattleNpcName(battleNpc, farmTarget.MobName);
                if (combat.IsControllingMovement)
                {
                    outOfRangeSince = null;
                    SetState(FarmState.EngagingMob, combat.IsAvoidingAreaAttack
                        ? $"Avoiding an area attack while fighting {targetName}"
                        : $"Engaging {targetName}");
                    await Task.WhenAny(combatTask, Task.Delay(250, token));
                    continue;
                }

                if (DistanceToTarget(player.Position, battleNpc.Position, horizontalOnly: true) > combatDistance + CombatReapproachTolerance)
                {
                    outOfRangeSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - outOfRangeSince >= CombatPursuitGrace)
                    {
                        SetState(FarmState.Navigating, $"Closing distance to {farmTarget.MobName}");
                        var moveResult = await navigation.MoveToMovingTargetAsync(
                            () => battleNpc.CurrentHp > 0 ? battleNpc.Position : null,
                            combatDistance,
                            cancellationToken: fightCancellation.Token,
                            interruptRequested: () => pauseRequested || battleNpc.CurrentHp == 0 || combatTask.IsCompleted,
                            arrivalTolerance: CombatArrivalTolerance);

                        if (combatTask.IsCompleted || battleNpc.CurrentHp == 0)
                            break;

                        if (moveResult == NavigationMoveResult.Interrupted && pauseRequested)
                        {
                            await WaitIfPausedAsync(token);
                            SetState(FarmState.EngagingMob, $"Engaging {farmTarget.MobName}");
                            outOfRangeSince = null;
                            continue;
                        }

                        if (moveResult != NavigationMoveResult.Arrived)
                        {
                            positioningError = $"Could not stay within combat range of {farmTarget.MobName}.";
                            break;
                        }

                        // Direct pursuit can temporarily disturb the hard target. Reassert it
                        // after movement and let the combat provider continue handling actions.
                        targets.SetTarget(battleNpc);
                        SetState(FarmState.EngagingMob, $"Engaging {farmTarget.MobName}");
                        outOfRangeSince = null;
                    }
                }
                else
                    outOfRangeSince = null;

                await Task.WhenAny(combatTask, Task.Delay(250, token));
            }

            if (positioningError is null)
                return await combatTask;

            fightCancellation.Cancel();
            return new CombatResult(false, positioningError);
        }
        finally
        {
            fightCancellation.Cancel();
            navigation.Stop();

            if (!combatTask.IsCompletedSuccessfully)
            {
                try
                {
                    await combatTask;
                }
                catch (OperationCanceledException) when (fightCancellation.IsCancellationRequested)
                {
                    // Expected when movement fails or the farm session is stopped.
                }
                catch (Exception ex)
                {
                    // Preserve the primary movement/cancellation result while still observing
                    // an asynchronously faulted combat provider task.
                    log.Warning(ex, "Combat provider faulted while combat positioning was stopping.");
                }
            }
        }
    }

    private async Task<IBattleNpc?> WaitForHostileTargetAsync(CancellationToken token)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.AddSeconds(1.5);
        var scanRadius = Math.Clamp(configuration.HostileScanRadius, 10f, 80f);

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(token);

            var hostile = targets.FindHostileTarget(scanRadius);
            if (hostile is not null)
                return hostile;

            var player = objectTable.LocalPlayer;
            if (player is null)
                return null;
            if (DateTime.UtcNow - startedAt >= TimeSpan.FromMilliseconds(350)
                && !player.StatusFlags.HasFlag(StatusFlags.InCombat))
                return null;

            await Task.Delay(150, token);
        }

        return targets.FindHostileTarget(scanRadius);
    }

    private static string GetBattleNpcName(IBattleNpc target, string fallback)
    {
        var name = target.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private async Task WaitForLootSettlementAsync(
        IReadOnlyDictionary<uint, uint> before,
        long inventoryVersionBeforeKill,
        CancellationToken token)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(configuration.LootWaitTimeoutMilliseconds, 1000, 15000));
        var stableWindow = TimeSpan.FromMilliseconds(Math.Clamp(configuration.LootSettleMilliseconds, 250, 3000));
        var deadline = DateTime.UtcNow + timeout;
        var lastCounts = before.ToDictionary(x => x.Key, x => inventory.GetItemCount(x.Key));
        DateTime? lastRelevantChangeAt = null;
        var observedVersion = inventoryVersionBeforeKill;

        if (HasRelevantInventoryIncrease(before, lastCounts))
        {
            lastRelevantChangeAt = DateTime.UtcNow;
            UpdateProgress();
        }

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(token);

            var now = DateTime.UtcNow;
            if (lastRelevantChangeAt is { } changedAt && now - changedAt >= stableWindow)
                return;

            var remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
                break;

            var wait = lastRelevantChangeAt is { } lastChange
                ? stableWindow - (now - lastChange)
                : TimeSpan.FromMilliseconds(500);
            if (wait <= TimeSpan.Zero)
                wait = TimeSpan.FromMilliseconds(50);
            if (wait > remaining)
                wait = remaining;

            await inventory.WaitForChangeAsync(observedVersion, wait, token);
            observedVersion = inventory.ChangeVersion;

            var current = before.Keys.ToDictionary(x => x, inventory.GetItemCount);
            var relevantChanged = current.Any(x => x.Value != lastCounts.GetValueOrDefault(x.Key));
            if (relevantChanged)
            {
                lastCounts = current;
                lastRelevantChangeAt = DateTime.UtcNow;
                UpdateProgress();
            }
        }

        // No relevant inventory event means this kill simply produced none of the
        // requested drops. A late event will still be caught by the next mandatory
        // inventory check before movement/engagement, so it cannot be double-counted.
        UpdateProgress();
    }

    private static bool HasRelevantInventoryIncrease(
        IReadOnlyDictionary<uint, uint> before,
        IReadOnlyDictionary<uint, uint> after)
        => before.Any(x => after.GetValueOrDefault(x.Key) > x.Value);

    private IBattleNpc? FindNearbyTarget(FarmTarget target)
        => targets.FindTarget(
            target.BNpcNameId,
            objectTable.LocalPlayer?.Position,
            Math.Clamp(configuration.MobScanRadius, 20f, 200f));

    private void EnsureInventorySpace()
    {
        if (inventory.GetFreeNormalInventorySlots() <= 0)
            throw new InvalidOperationException("Your normal inventory is full. LootHunter stopped before traveling or engaging another monster.");
    }

    private bool RegisterNavigationFailure(FarmTarget target, string reason)
    {
        var key = new MobSourceKey(target.BNpcNameId, target.TerritoryId);
        var failures = navigationFailures.GetValueOrDefault(key) + 1;
        navigationFailures[key] = failures;
        var maximum = Math.Clamp(configuration.MaxNavigationFailuresPerSource, 1, 10);

        session.AddWarning($"{target.MobName}: {reason} Navigation failure {failures}/{maximum}.");
        if (failures < maximum)
            return false;

        excludedSources.Add(key);
        session.StatusMessage = $"Skipping {target.MobName}: navigation failed {failures} times for this source.";
        session.AddWarning($"{target.MobName}: source disabled for this farm session after repeated unreachable/stalled navigation.");
        navigation.Stop();
        return true;
    }

    private void ClearNavigationFailures(FarmTarget target)
        => navigationFailures.Remove(new MobSourceKey(target.BNpcNameId, target.TerritoryId));

    private static string FormatPosition(Vector3 position)
        => $"({position.X:F1}, {position.Y:F1}, {position.Z:F1})";

    private static float DistanceToTarget(Vector3 current, Vector3 target, bool horizontalOnly)
        => horizontalOnly
            ? Vector2.Distance(new Vector2(current.X, current.Z), new Vector2(target.X, target.Z))
            : Vector3.Distance(current, target);

    private static int CountSpawnPoints(IReadOnlyList<SpawnCluster> clusters)
        => clusters.Sum(x => x.SpawnPoints.Count);

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
        if (activeList is not null)
            SyncGoals(activeList);

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
        if (activeList is not null)
            SyncGoals(activeList);

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

        // Session drops are derived from actual inventory deltas instead of being
        // attributed to a kill. This remains correct even if the loot event arrives
        // late, between kills, or while LootHunter is moving.
        ulong obtained = 0;
        foreach (var (itemId, startCount) in sessionStartCounts)
        {
            var current = inventory.GetItemCount(itemId);
            if (current > startCount)
                obtained += current - startCount;
        }
        session.DropsObtained = (int)Math.Min((ulong)int.MaxValue, obtained);
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

}
