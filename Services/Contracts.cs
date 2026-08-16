using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using LootHunter.Models;

namespace LootHunter.Services;

public interface IMobDropDatabase
{
    bool IsReady { get; }
    string? LoadError { get; }
    IReadOnlyList<MobSource> GetSourcesForItem(uint itemId);
    IReadOnlyList<ItemSearchResult> SearchDropItems(string query, int limit = 30);
    string GetItemName(uint itemId);
    void RefreshTravelDestinations();
}

public interface IInventoryService
{
    uint GetItemCount(uint itemId);
    int GetFreeNormalInventorySlots();
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
    void Abort();
}

public interface INavigationService
{
    bool IsAvailable { get; }
    bool IsReady { get; }
    bool IsRunning { get; }
    Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken);
    Task<bool> MoveToAsync(Vector3 destination, float stopDistance, bool fly, CancellationToken cancellationToken);
    Vector3? SnapToFloor(Vector3 destination);
    void Stop();
}

public interface IMountService
{
    bool IsMounted { get; }
    bool CanFly { get; }
    Task<bool> MountAsync(CancellationToken cancellationToken);
    Task<bool> DismountAsync(CancellationToken cancellationToken);
}

public interface ITargetService
{
    IBattleNpc? FindTarget(uint bNpcNameId, Vector3? around = null, float maxDistance = float.MaxValue);
    void SetTarget(IBattleNpc target);
}

public interface ICombatProvider
{
    string Name { get; }
    bool IsAvailable { get; }
    string? AvailabilityError { get; }
    Task<CombatResult> KillAsync(IBattleNpc target, CancellationToken cancellationToken);
}

public sealed record CombatResult(bool Success, string? Error = null);
