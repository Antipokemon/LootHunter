using System.Numerics;

namespace LootHunter.Models;

public sealed record ItemSearchResult(uint ItemId, string Name, int SourceCount);

public readonly record struct MobSourceKey(uint BNpcNameId, uint TerritoryId);

public sealed record AetheryteDestination(uint AetheryteId, byte SubIndex, string Name, Vector3? Position);

public sealed record MobSource
{
    public required uint BNpcNameId { get; init; }
    public required string MobName { get; init; }
    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public int? MobLevel { get; init; }
    public AetheryteDestination? NearestAetheryte { get; set; }
    public IReadOnlyList<SpawnCluster> Clusters { get; init; } = [];
    public IReadOnlySet<uint> DropItemIds { get; init; } = new HashSet<uint>();
}

public sealed record SpawnCluster
{
    public required int AreaIndex { get; init; }
    public required IReadOnlyList<Vector3> SpawnPoints { get; init; }
    public Vector3 Center { get; init; }
}

public sealed record FarmTarget
{
    public required uint ItemId { get; init; }
    public required uint BNpcNameId { get; init; }
    public required string MobName { get; init; }
    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public int? MobLevel { get; init; }
    public AetheryteDestination? NearestAetheryte { get; init; }
    public uint RequiredQuantity { get; init; }
    public IReadOnlyList<SpawnCluster> Clusters { get; init; } = [];
    public IReadOnlySet<uint> RelevantDropItemIds { get; init; } = new HashSet<uint>();
}

public sealed record FarmPlan(IReadOnlyList<FarmTarget> Targets);

public sealed record ItemProgress(uint ItemId, string ItemName, uint Current, uint Goal, uint Remaining);
