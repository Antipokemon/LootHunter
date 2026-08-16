using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using LootHunter.Models;

namespace LootHunter.Services;

public interface IMobDropDatabase
{
    bool IsReady { get; }
    bool IsLoading { get; }
    string? LoadError { get; }
    IReadOnlyList<MobSource> GetSourcesForItem(uint itemId);
    IReadOnlyList<ItemSearchResult> SearchDropItems(string query, int limit = 30);
    ItemSearchResult? FindExactItem(string query);
    string GetItemName(uint itemId);
    bool IsResolving(uint itemId);
    string? GetResolutionError(uint itemId);
    Task EnsureSourcesResolvedAsync(IEnumerable<uint> itemIds, CancellationToken cancellationToken, bool includeKnownSources = false);
    void RefreshTravelDestinations(IEnumerable<uint>? itemIds = null);
}

public interface IInventoryService : IDisposable
{
    long ChangeVersion { get; }
    uint GetItemCount(uint itemId);
    int GetFreeNormalInventorySlots();
    Task<bool> WaitForChangeAsync(long afterVersion, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IRoutePlanner
{
    FarmPlan BuildPlan(LootList list, IReadOnlyDictionary<uint, uint> requiredQuantities, uint currentTerritoryId, IReadOnlySet<MobSourceKey> excludedSources);
}

public interface ILevelSafetyService
{
    LevelSafetyResult Check(FarmTarget target);
    LevelSafetyResult CheckObserved(IBattleNpc target);
    bool IsCombatJob(out string reason);
}

public sealed record LevelSafetyResult(bool IsKnown, bool IsSafe, int PlayerLevel, int? MobLevel, string Message);

public interface ITravelService
{
    bool IsAvailable { get; }
    bool IsBusy { get; }
    Task<bool> TeleportNearAsync(FarmTarget target, CancellationToken cancellationToken);
    Task<bool> TeleportByLifestreamCommandAsync(string command, CancellationToken cancellationToken);
    void Abort();
}

public enum NavigationMoveResult
{
    Arrived,
    Interrupted,
    Failed,
}

public interface INavigationService
{
    bool IsAvailable { get; }
    bool IsReady { get; }
    bool IsRunning { get; }
    Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken);
    Task<NavigationMoveResult> MoveToAsync(
        Vector3 destination,
        float stopDistance,
        bool fly,
        CancellationToken cancellationToken,
        Func<bool>? interruptRequested = null,
        float arrivalTolerance = 1.5f,
        bool horizontalArrival = false);
    Task<NavigationMoveResult> MoveToMovingTargetAsync(
        Func<Vector3?> targetPosition,
        float stopDistance,
        CancellationToken cancellationToken,
        Func<bool>? interruptRequested = null,
        float arrivalTolerance = 0.25f);
    Vector3? SnapToFloor(Vector3 destination);
    Task<bool> StopAsync(CancellationToken cancellationToken);
    void Stop();
}

public interface IMountService
{
    bool IsMounted { get; }
    bool IsInFlight { get; }
    bool CanFly { get; }
    Task<bool> MountAsync(CancellationToken cancellationToken);
    Task<bool> DismountAsync(CancellationToken cancellationToken);
}

public interface ITargetService
{
    IBattleNpc? FindTarget(uint bNpcNameId, Vector3? around = null, float maxDistance = float.MaxValue);
    IBattleNpc? FindHostileTarget(float maxDistance);
    void SetTarget(IBattleNpc target);
}

public interface ICombatProvider
{
    string Name { get; }
    bool IsAvailable { get; }
    string? AvailabilityError { get; }
    bool IsAreaAvoidanceAvailable { get; }
    bool IsControllingMovement { get; }
    bool IsAvoidingAreaAttack { get; }
    string? PrepareForSession();
    string? BeginEncounter();
    void SetMovementPaused(bool paused);
    void EndEncounter();
    Task<CombatResult> KillAsync(IBattleNpc target, CancellationToken cancellationToken);
    void EndSession();
}

public sealed record CombatResult(bool Success, string? Error = null);

public sealed record PluginRequirementStatus(
    string Name,
    bool Mandatory,
    bool Available,
    string Detail,
    string InstallerSearch);
