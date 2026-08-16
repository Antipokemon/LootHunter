using System.Numerics;
using Dalamud.Plugin.Services;
using LootHunter.Models;
using LootHunter.Services;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace LootHunter.Data;

public sealed class MobDropDatabase : IMobDropDatabase, IDisposable
{
    private const float ClusterDistance = 70f;
    private const float ClusterDistanceSquared = ClusterDistance * ClusterDistance;
    private const double MapFactor = 0.019999999552965164d;

    private readonly IDataManager dataManager;
    private readonly IAetheryteList aetherytes;
    private readonly IPluginLog log;
    private readonly MonsterLootResolver monsterLootResolver;
    private readonly Dictionary<uint, List<MobSource>> byItem = [];
    private readonly Dictionary<uint, string> itemNames = [];
    private readonly Dictionary<uint, string> allItemNames = [];
    private readonly Dictionary<uint, HashSet<uint>> dropNpcIdsByItem = [];
    private readonly Dictionary<uint, HashSet<uint>> dropItemsByNpc = [];
    private readonly Dictionary<string, uint> territoryIdsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, Task> sourceResolutionTasks = [];
    private readonly Dictionary<uint, string> sourceResolutionErrors = [];
    private readonly object resolutionLock = new();
    private IFramework? framework;

    public bool IsReady { get; private set; }
    public bool IsLoading { get; private set; }
    public string? LoadError { get; private set; }

    public MobDropDatabase(IDataManager dataManager, IAetheryteList aetherytes, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.aetherytes = aetherytes;
        this.log = log;
        monsterLootResolver = new MonsterLootResolver(log);
    }

    public async Task InitializeAsync(IFramework framework)
    {
        if (IsReady || IsLoading)
            return;

        this.framework = framework;
        IsLoading = true;
        LoadError = null;

        try
        {
            await Task.Run(Load);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            IsReady = false;
            log.Error(ex, "LootHunter failed to initialize the monster-drop database.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public IReadOnlyList<MobSource> GetSourcesForItem(uint itemId)
        => byItem.TryGetValue(itemId, out var sources) ? sources : [];

    public string GetItemName(uint itemId)
        => itemNames.TryGetValue(itemId, out var name)
            ? name
            : allItemNames.TryGetValue(itemId, out var allName)
                ? allName
                : $"Item {itemId}";

    public bool IsResolving(uint itemId)
    {
        lock (resolutionLock)
            return sourceResolutionTasks.TryGetValue(itemId, out var task) && !task.IsCompleted;
    }

    public string? GetResolutionError(uint itemId)
    {
        lock (resolutionLock)
            return sourceResolutionErrors.GetValueOrDefault(itemId);
    }

    public IReadOnlyList<ItemSearchResult> SearchDropItems(string query, int limit = 30)
    {
        if (!IsReady || limit <= 0)
            return [];

        var text = query.Trim();
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Only expose items with a resolved open-world source. Unknown items must
        // pass an explicit fallback lookup before they become selectable.
        IEnumerable<KeyValuePair<uint, string>> candidates = itemNames
            .Where(item => byItem.ContainsKey(item.Key));

        if (terms.Length > 0)
        {
            candidates = candidates.Where(item =>
            {
                var idText = item.Key.ToString();
                return terms.All(term =>
                    item.Value.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    idText.Contains(term, StringComparison.OrdinalIgnoreCase));
            });
        }

        return candidates
            .OrderBy(item => GetSearchRank(item, text))
            .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(item => new ItemSearchResult(
                item.Key,
                item.Value,
                byItem.GetValueOrDefault(item.Key)?.Count ?? 0))
            .ToList();
    }

    public ItemSearchResult? FindExactItem(string query)
    {
        if (!IsReady)
            return null;

        var text = query.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        KeyValuePair<uint, string>? match = null;
        if (uint.TryParse(text, out var itemId) && allItemNames.TryGetValue(itemId, out var itemName))
        {
            match = new KeyValuePair<uint, string>(itemId, itemName);
        }
        else
        {
            foreach (var item in allItemNames)
            {
                if (!string.Equals(item.Value, text, StringComparison.OrdinalIgnoreCase))
                    continue;
                match = item;
                break;
            }
        }

        return match is { } itemMatch
            ? new ItemSearchResult(
                itemMatch.Key,
                itemMatch.Value,
                byItem.GetValueOrDefault(itemMatch.Key)?.Count ?? 0)
            : null;
    }

    private static int GetSearchRank(KeyValuePair<uint, string> item, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        if (string.Equals(item.Value, text, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (item.Value.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (item.Key.ToString().StartsWith(text, StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

    public async Task EnsureSourcesResolvedAsync(IEnumerable<uint> itemIds, CancellationToken cancellationToken, bool includeKnownSources = false)
    {
        if (!IsReady)
            return;

        var tasks = new List<Task>();
        foreach (var itemId in itemIds.Where(x => x != 0).Distinct())
        {
            if (!includeKnownSources && GetSourcesForItem(itemId).Count > 0)
                continue;

            Task resolutionTask;
            lock (resolutionLock)
            {
                if (!sourceResolutionTasks.TryGetValue(itemId, out resolutionTask!) ||
                    (includeKnownSources && resolutionTask.IsCompleted))
                {
                    resolutionTask = ResolveMissingItemAsync(itemId);
                    sourceResolutionTasks[itemId] = resolutionTask;
                }
            }
            tasks.Add(resolutionTask);
        }

        if (tasks.Count == 0)
            return;

        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }

    public void RefreshTravelDestinations(IEnumerable<uint>? itemIds = null)
    {
        var sources = itemIds is null
            ? byItem.Values.SelectMany(x => x)
            : itemIds
                .Where(x => x != 0)
                .Distinct()
                .SelectMany(itemId => byItem.GetValueOrDefault(itemId) ?? []);

        var seen = new HashSet<MobSourceKey>();
        foreach (var source in sources)
        {
            var key = new MobSourceKey(source.BNpcNameId, source.TerritoryId);
            if (!seen.Add(key))
                continue;
            source.NearestAetheryte = SelectAetheryte(source.TerritoryId, source.Clusters);
        }
    }

    private async Task ResolveMissingItemAsync(uint itemId)
    {
        try
        {
            var itemName = GetItemName(itemId);
            var records = await monsterLootResolver.ResolveAsync(itemId, itemName, CancellationToken.None).ConfigureAwait(false);
            if (records.Count == 0)
            {
                lock (resolutionLock)
                    sourceResolutionErrors[itemId] = "No monster-drop locations were returned by the MonsterLoot wiki fallback.";
                return;
            }

            if (framework is null)
                throw new InvalidOperationException("Dalamud framework service is unavailable for MonsterLoot source merging.");

            await framework.Run(() => MergeFallbackSources(itemId, records));

            if (GetSourcesForItem(itemId).Count == 0)
            {
                lock (resolutionLock)
                    sourceResolutionErrors[itemId] = "Monster-drop information was found, but LootHunter could not map it to a usable open-world monster source.";
            }
            else
            {
                lock (resolutionLock)
                    sourceResolutionErrors.Remove(itemId);
            }
        }
        catch (Exception ex)
        {
            lock (resolutionLock)
                sourceResolutionErrors[itemId] = ex.Message;
            log.Warning(ex, "MonsterLoot fallback source resolution failed for item {ItemId}.", itemId);
        }
    }

    private void Load()
    {
        IsReady = false;
        LoadError = null;
        byItem.Clear();
        itemNames.Clear();
        allItemNames.Clear();
        dropNpcIdsByItem.Clear();
        dropItemsByNpc.Clear();
        territoryIdsByName.Clear();
        lock (resolutionLock)
        {
            sourceResolutionTasks.Clear();
            sourceResolutionErrors.Clear();
        }

        try
        {
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

            foreach (var item in items)
            {
                if (item.RowId == 0)
                    continue;
                var name = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    allItemNames[item.RowId] = name;
            }

            foreach (var territory in territories)
            {
                if (territory.RowId == 0 || territory.ContentFinderCondition.RowId != 0 || territory.QuestBattle.RowId != 0)
                    continue;
                var name = territory.PlaceName.ValueNullable?.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                territoryIdsByName.TryAdd(name, territory.RowId);
            }

            foreach (var group in drops.Where(x => x.ItemId != 0 && x.BNpcNameId != 0).GroupBy(x => x.ItemId))
                dropNpcIdsByItem[group.Key] = group.Select(x => x.BNpcNameId).ToHashSet();

            foreach (var group in drops.Where(x => x.ItemId != 0 && x.BNpcNameId != 0).GroupBy(x => x.BNpcNameId))
                dropItemsByNpc[group.Key] = group.Select(x => x.ItemId).ToHashSet();

            // Build the picker index from every known drop link, regardless of whether a
            // spawn can currently be resolved. This prevents valid items from disappearing.
            foreach (var itemId in dropNpcIdsByItem.Keys)
                itemNames[itemId] = allItemNames.GetValueOrDefault(itemId) ?? $"Item {itemId}";

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
                        source = new MobSource
                        {
                            BNpcNameId = drop.BNpcNameId,
                            MobName = mobName,
                            TerritoryId = territoryId,
                            TerritoryName = territoryName,
                            MobLevel = null,
                            NearestAetheryte = null,
                            Clusters = clusters,
                            DropItemIds = dropItemsByNpc.GetValueOrDefault(drop.BNpcNameId) ?? new HashSet<uint>(),
                        };
                        sourceCache[key] = source;
                    }

                    AddSource(drop.ItemId, source);
                }
            }

            SortSources();

            IsReady = itemNames.Count > 0;
            if (!IsReady)
                throw new InvalidOperationException("Monster-drop data loaded, but no drop items were indexed.");

            log.Information(
                "LootHunter indexed {DropItemCount} known drop items ({ResolvedItemCount} with static spawn sources), {SourceCount} monster-zone sources, and {SpawnCount} spawn points from LuminaSupplemental.",
                itemNames.Count,
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

    private void MergeFallbackSources(uint itemId, IReadOnlyList<MonsterLootFallbackRecord> records)
    {
        var npcNames = dataManager.GetExcelSheet<BNpcName>();
        var territories = dataManager.GetExcelSheet<TerritoryType>();
        var maps = dataManager.GetExcelSheet<Map>();
        if (npcNames is null || territories is null || maps is null)
            return;

        var candidateNpcIds = dropNpcIdsByItem.GetValueOrDefault(itemId) ?? [];

        foreach (var record in records)
        {
            var bNpcNameId = ResolveBNpcNameId(record.MobName, candidateNpcIds, npcNames);
            if (bNpcNameId == 0)
            {
                log.Warning("MonsterLoot fallback could not map mob '{MobName}' for item {ItemId} to a BNpcName row.", record.MobName, itemId);
                continue;
            }

            if (!territoryIdsByName.TryGetValue(record.TerritoryName, out var territoryId) ||
                !territories.TryGetRow(territoryId, out var territory))
            {
                log.Warning("MonsterLoot fallback could not map territory '{Territory}' for item {ItemId}.", record.TerritoryName, itemId);
                continue;
            }

            Map? map = null;
            if (territory.Map.RowId != 0 && maps.TryGetRow(territory.Map.RowId, out var foundMap))
                map = foundMap;

            var point = NormalizeToWorld(new Vector3(record.MapX, record.MapY, 0f), map);
            if (point is null)
                continue;

            if (!dropNpcIdsByItem.TryGetValue(itemId, out var itemNpcIds))
                dropNpcIdsByItem[itemId] = itemNpcIds = [];
            itemNpcIds.Add(bNpcNameId);

            if (!dropItemsByNpc.TryGetValue(bNpcNameId, out var npcDropIds))
                dropItemsByNpc[bNpcNameId] = npcDropIds = [];
            npcDropIds.Add(itemId);

            var clusters = BuildClusters([point.Value]);
            var source = new MobSource
            {
                BNpcNameId = bNpcNameId,
                MobName = record.MobName,
                TerritoryId = territoryId,
                TerritoryName = record.TerritoryName,
                MobLevel = record.MobLevel,
                NearestAetheryte = SelectAetheryte(territoryId, clusters),
                Clusters = clusters,
                DropItemIds = new HashSet<uint>(npcDropIds),
            };

            MergeSource(itemId, source);
        }

        SortSources();
    }

    private static uint ResolveBNpcNameId(string mobName, IReadOnlySet<uint> candidateNpcIds, Lumina.Excel.ExcelSheet<BNpcName> npcNames)
    {
        foreach (var candidate in candidateNpcIds)
        {
            if (!npcNames.TryGetRow(candidate, out var row))
                continue;
            if (string.Equals(row.Singular.ExtractText(), mobName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        // If supplemental data omitted the drop link entirely, fall back to a name scan.
        foreach (var row in npcNames)
        {
            if (row.RowId != 0 && string.Equals(row.Singular.ExtractText(), mobName, StringComparison.OrdinalIgnoreCase))
                return row.RowId;
        }

        return 0;
    }

    private void AddSource(uint itemId, MobSource source)
        => UpsertSource(itemId, source, mergeExisting: false);

    private void MergeSource(uint itemId, MobSource source)
        => UpsertSource(itemId, source, mergeExisting: true);

    private void UpsertSource(uint itemId, MobSource source, bool mergeExisting)
    {
        if (!byItem.TryGetValue(itemId, out var sources))
            byItem[itemId] = sources = [];

        var existingIndex = sources.FindIndex(x => x.BNpcNameId == source.BNpcNameId && x.TerritoryId == source.TerritoryId);
        if (existingIndex < 0)
        {
            sources.Add(source);
        }
        else if (mergeExisting)
        {
            var merged = MergeSourceData(sources[existingIndex], source);
            ReplaceSourceReferences(merged);
            sources = byItem[itemId];
            existingIndex = sources.FindIndex(x => x.BNpcNameId == source.BNpcNameId && x.TerritoryId == source.TerritoryId);
            if (existingIndex < 0)
                sources.Add(merged);
            else
                sources[existingIndex] = merged;
        }

        if (!itemNames.ContainsKey(itemId))
            itemNames[itemId] = allItemNames.GetValueOrDefault(itemId) ?? $"Item {itemId}";
    }

    private MobSource MergeSourceData(MobSource existing, MobSource addition)
    {
        var points = existing.Clusters
            .SelectMany(x => x.SpawnPoints)
            .Concat(addition.Clusters.SelectMany(x => x.SpawnPoints))
            .Distinct()
            .ToList();
        var clusters = BuildClusters(points);
        var dropItemIds = existing.DropItemIds.Concat(addition.DropItemIds).ToHashSet();

        return existing with
        {
            MobName = string.IsNullOrWhiteSpace(existing.MobName) ? addition.MobName : existing.MobName,
            TerritoryName = string.IsNullOrWhiteSpace(existing.TerritoryName) ? addition.TerritoryName : existing.TerritoryName,
            MobLevel = existing.MobLevel ?? addition.MobLevel,
            NearestAetheryte = SelectAetheryte(existing.TerritoryId, clusters),
            Clusters = clusters,
            DropItemIds = dropItemIds,
        };
    }

    private void ReplaceSourceReferences(MobSource source)
    {
        foreach (var itemId in byItem.Keys.ToList())
        {
            var sources = byItem[itemId];
            var index = sources.FindIndex(x => x.BNpcNameId == source.BNpcNameId && x.TerritoryId == source.TerritoryId);
            if (index >= 0)
                sources[index] = source;
        }
    }

    private void SortSources()
    {
        foreach (var itemId in byItem.Keys.ToList())
        {
            byItem[itemId] = byItem[itemId]
                .OrderByDescending(x => x.Clusters.Sum(c => c.SpawnPoints.Count))
                .ThenBy(x => x.TerritoryName, StringComparer.OrdinalIgnoreCase)
                .ToList();
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

    public void Dispose() => monsterLootResolver.Dispose();
}
