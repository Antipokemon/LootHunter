using System.Numerics;
using Dalamud.Plugin.Services;
using LootHunter.Models;
using LootHunter.Services;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace LootHunter.Data;

public sealed class MobDropDatabase : IMobDropDatabase
{
    private const float ClusterDistance = 70f;
    private const float ClusterDistanceSquared = ClusterDistance * ClusterDistance;
    private const double MapFactor = 0.019999999552965164d;

    private readonly IDataManager dataManager;
    private readonly IAetheryteList aetherytes;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, List<MobSource>> byItem = [];
    private readonly Dictionary<uint, string> itemNames = [];

    public bool IsReady { get; private set; }
    public string? LoadError { get; private set; }

    public MobDropDatabase(IDataManager dataManager, IAetheryteList aetherytes, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.aetherytes = aetherytes;
        this.log = log;
        Load();
    }

    public IReadOnlyList<MobSource> GetSourcesForItem(uint itemId)
        => byItem.TryGetValue(itemId, out var sources) ? sources : [];

    public string GetItemName(uint itemId)
        => itemNames.TryGetValue(itemId, out var name) ? name : $"Item {itemId}";

    public IReadOnlyList<ItemSearchResult> SearchDropItems(string query, int limit = 30)
    {
        if (!IsReady || limit <= 0)
            return [];

        var text = query.Trim();
        IEnumerable<KeyValuePair<uint, string>> candidates = itemNames;
        if (!string.IsNullOrWhiteSpace(text))
            candidates = candidates.Where(x => x.Value.Contains(text, StringComparison.OrdinalIgnoreCase));

        return candidates
            .OrderBy(x => x.Value.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => new ItemSearchResult(x.Key, x.Value, byItem.GetValueOrDefault(x.Key)?.Count ?? 0))
            .ToList();
    }

    public void RefreshTravelDestinations()
    {
        var seen = new HashSet<MobSourceKey>();
        foreach (var source in byItem.Values.SelectMany(x => x))
        {
            var key = new MobSourceKey(source.BNpcNameId, source.TerritoryId);
            if (!seen.Add(key))
                continue;
            source.NearestAetheryte = SelectAetheryte(source.TerritoryId, source.Clusters);
        }
    }

    private void Load()
    {
        IsReady = false;
        LoadError = null;
        byItem.Clear();
        itemNames.Clear();

        try
        {
            // We only need the raw supplemental IDs/positions here. Passing Dalamud's
            // ClientLanguage would be incorrect because CsvLoader expects Lumina.Data.Language;
            // the optional population arguments are unnecessary for LootHunter.
            var drops = CsvLoader.LoadResource<MobDrop>(
                CsvLoader.MobDropResourceName,
                true,
                out _,
                out _) ?? [];
            var spawns = CsvLoader.LoadResource<MobSpawnPosition>(
                CsvLoader.MobSpawnResourceName,
                true,
                out _,
                out _) ?? [];

            if (drops.Count == 0)
                throw new InvalidOperationException("LuminaSupplemental returned no monster-drop records.");

            var npcNames = dataManager.GetExcelSheet<BNpcName>();
            var territories = dataManager.GetExcelSheet<TerritoryType>();
            var maps = dataManager.GetExcelSheet<Map>();
            var items = dataManager.GetExcelSheet<Item>();
            if (npcNames is null || territories is null || maps is null || items is null)
                throw new InvalidOperationException("One or more required game-data sheets are unavailable.");

            var dropItemsByNpc = drops
                .Where(x => x.ItemId != 0 && x.BNpcNameId != 0)
                .GroupBy(x => x.BNpcNameId)
                .ToDictionary(
                    x => x.Key,
                    x => x.Select(y => y.ItemId).ToHashSet());

            var spawnsByNpc = spawns
                .Where(x => x.BNpcNameId != 0 && x.TerritoryTypeId != 0)
                .GroupBy(x => x.BNpcNameId)
                .ToDictionary(x => x.Key, x => x.ToList());

            var sourceCache = new Dictionary<(uint BNpcNameId, uint TerritoryId), MobSource>();

            foreach (var drop in drops.Where(x => x.ItemId != 0 && x.BNpcNameId != 0))
            {
                if (!spawnsByNpc.TryGetValue(drop.BNpcNameId, out var mobSpawns) || mobSpawns.Count == 0)
                    continue;
                if (!npcNames.TryGetRow(drop.BNpcNameId, out var npcNameRow))
                    continue;

                var mobName = npcNameRow.Singular.ExtractText();
                if (string.IsNullOrWhiteSpace(mobName))
                    continue;

                foreach (var territoryGroup in mobSpawns.GroupBy(x => x.TerritoryTypeId))
                {
                    var territoryId = territoryGroup.Key;
                    if (!territories.TryGetRow(territoryId, out var territory))
                        continue;
                    if (territory.ContentFinderCondition.RowId != 0 || territory.QuestBattle.RowId != 0)
                        continue;

                    var key = (drop.BNpcNameId, territoryId);
                    if (!sourceCache.TryGetValue(key, out var source))
                    {
                        Map? map = null;
                        if (territory.Map.RowId != 0 && maps.TryGetRow(territory.Map.RowId, out var foundMap))
                            map = foundMap;

                        var points = territoryGroup
                            .Select(x => NormalizeToWorld(x.Position, map))
                            .Where(x => x.HasValue)
                            .Select(x => x!.Value)
                            .Distinct()
                            .ToList();
                        if (points.Count == 0)
                            continue;

                        var territoryName = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Territory {territoryId}";
                        var clusters = BuildClusters(points);
                        var aetheryte = SelectAetheryte(territoryId, clusters);

                        source = new MobSource
                        {
                            BNpcNameId = drop.BNpcNameId,
                            MobName = mobName,
                            TerritoryId = territoryId,
                            TerritoryName = territoryName,
                            MobLevel = null,
                            NearestAetheryte = aetheryte,
                            Clusters = clusters,
                            DropItemIds = dropItemsByNpc.GetValueOrDefault(drop.BNpcNameId) ?? new HashSet<uint>(),
                        };
                        sourceCache[key] = source;
                    }

                    if (!byItem.TryGetValue(drop.ItemId, out var sources))
                        byItem[drop.ItemId] = sources = [];
                    if (!sources.Any(x => x.BNpcNameId == source.BNpcNameId && x.TerritoryId == source.TerritoryId))
                        sources.Add(source);
                }
            }

            foreach (var itemId in byItem.Keys.ToList())
            {
                if (items.TryGetRow(itemId, out var item))
                {
                    var name = item.Name.ExtractText();
                    itemNames[itemId] = string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name;
                }
                else
                {
                    itemNames[itemId] = $"Item {itemId}";
                }

                byItem[itemId] = byItem[itemId]
                    .OrderByDescending(x => x.Clusters.Sum(c => c.SpawnPoints.Count))
                    .ThenBy(x => x.TerritoryName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            IsReady = byItem.Count > 0;
            if (!IsReady)
                throw new InvalidOperationException("Monster-drop data loaded, but no usable open-world monster spawns were resolved.");

            log.Information("LootHunter loaded {ItemCount} drop items, {SourceCount} monster-zone sources, and {SpawnCount} spawn points from LuminaSupplemental.",
                byItem.Count,
                sourceCache.Count,
                sourceCache.Values.Sum(x => x.Clusters.Sum(c => c.SpawnPoints.Count)));
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            IsReady = false;
            log.Error(ex, "LootHunter failed to build the monster-drop database.");
        }
    }

    private AetheryteDestination? SelectAetheryte(uint territoryId, IReadOnlyList<SpawnCluster> clusters)
    {
        var candidates = aetherytes
            .Where(x => x.TerritoryId == territoryId)
            .Where(x => !x.IsApartment && !x.IsSharedHouse)
            .Select(x => new { Entry = x, Position = GetAetherytePosition(x) })
            .OrderBy(x => x.Position is null ? 1 : 0)
            .ThenBy(x => x.Position is null || clusters.Count == 0
                ? float.MaxValue
                : clusters.Min(cluster => DistanceSquaredXZ(x.Position.Value, cluster.Center)))
            .ThenBy(x => x.Entry.GilCost)
            .ThenBy(x => x.Entry.AetheryteId)
            .ToList();

        var candidate = candidates.FirstOrDefault();
        if (candidate is null)
            return null;

        var entry = candidate.Entry;
        var name = entry.AetheryteData.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText();
        return new AetheryteDestination(
            entry.AetheryteId,
            entry.SubIndex,
            string.IsNullOrWhiteSpace(name) ? $"Aetheryte {entry.AetheryteId}" : name,
            candidate.Position);
    }

    private static Vector3? GetAetherytePosition(Dalamud.Game.ClientState.Aetherytes.IAetheryteEntry entry)
    {
        if (entry.AetheryteData.ValueNullable is not { } row)
            return null;

        foreach (var levelRef in row.Level)
        {
            if (levelRef.ValueNullable is { } level)
                return new Vector3(level.X, level.Y, level.Z);
        }

        return null;
    }

    private static IReadOnlyList<SpawnCluster> BuildClusters(IReadOnlyList<Vector3> points)
    {
        var remaining = points.ToList();
        var clusters = new List<List<Vector3>>();

        while (remaining.Count > 0)
        {
            var cluster = new List<Vector3> { remaining[0] };
            remaining.RemoveAt(0);

            var changed = true;
            while (changed)
            {
                changed = false;
                var center = Average(cluster);
                for (var i = remaining.Count - 1; i >= 0; i--)
                {
                    if (DistanceSquaredXZ(center, remaining[i]) > ClusterDistanceSquared)
                        continue;
                    cluster.Add(remaining[i]);
                    remaining.RemoveAt(i);
                    changed = true;
                }
            }

            clusters.Add(cluster);
        }

        return clusters
            .OrderByDescending(x => x.Count)
            .Select((cluster, index) => new SpawnCluster
            {
                AreaIndex = index + 1,
                SpawnPoints = cluster,
                Center = Average(cluster),
            })
            .ToList();
    }

    private static Vector3 Average(IReadOnlyList<Vector3> points)
    {
        if (points.Count == 0)
            return Vector3.Zero;
        var total = Vector3.Zero;
        foreach (var point in points)
            total += point;
        return total / points.Count;
    }

    private static float DistanceSquaredXZ(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return dx * dx + dz * dz;
    }

    private static Vector3? NormalizeToWorld(Vector3 position, Map? map)
    {
        if (MathF.Abs(position.Z) < 0.001f && LooksLikeMapCoordinate(position.X) && LooksLikeMapCoordinate(position.Y))
        {
            if (map is null || map.Value.SizeFactor == 0)
                return null;
            var worldX = MapToWorld(position.X, map.Value.SizeFactor, map.Value.OffsetX);
            var worldZ = MapToWorld(position.Y, map.Value.SizeFactor, map.Value.OffsetY);
            return new Vector3(worldX, 0f, worldZ);
        }

        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return null;
        return position;
    }

    private static bool LooksLikeMapCoordinate(float value) => value is > 0f and < 50f;

    private static float MapToWorld(float mapCoordinate, uint sizeFactor, int offset)
        => (float)((mapCoordinate - 1.0d - (2048.0d / sizeFactor) - (MapFactor * offset)) / MapFactor);
}
