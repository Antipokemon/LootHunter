using LootHunter.Models;

namespace LootHunter.Services;

public sealed class RoutePlanner(IMobDropDatabase database) : IRoutePlanner
{
    public FarmPlan BuildPlan(LootList list, IReadOnlyDictionary<uint, uint> requiredQuantities, uint currentTerritoryId, IReadOnlySet<MobSourceKey> excludedSources)
    {
        var entries = list.Items
            .Where(x => x.Enabled && requiredQuantities.GetValueOrDefault(x.ItemId) > 0)
            .ToDictionary(x => x.ItemId, x => x);
        var remaining = entries.Keys.ToHashSet();
        var targets = new List<FarmTarget>();
        var territoryCursor = currentTerritoryId;

        while (remaining.Count > 0)
        {
            var candidates = new List<(uint RequestedItemId, MobSource Source, double Score)>();
            foreach (var itemId in remaining)
            {
                var entry = entries[itemId];
                foreach (var source in database.GetSourcesForItem(itemId))
                {
                    if (excludedSources.Contains(new MobSourceKey(source.BNpcNameId, source.TerritoryId)))
                        continue;
                    if (entry.PreferredBNpcNameId is not null && source.BNpcNameId != entry.PreferredBNpcNameId)
                        continue;
                    if (entry.PreferredTerritoryId is not null && source.TerritoryId != entry.PreferredTerritoryId)
                        continue;

                    var sharedNeeded = source.DropItemIds.Count(remaining.Contains);
                    var spawns = source.Clusters.Sum(x => x.SpawnPoints.Count);
                    var score = sharedNeeded * 1000d
                        + Math.Min(spawns, 100) * 3d
                        + (source.TerritoryId == territoryCursor ? 500d : 0d)
                        + (source.TerritoryId == currentTerritoryId ? 250d : 0d)
                        + (source.NearestAetheryte is not null ? 75d : -500d);
                    candidates.Add((itemId, source, score));
                }
            }

            var chosen = candidates
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Source.TerritoryName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (chosen.Source is null)
                break;

            var relevant = chosen.Source.DropItemIds.Where(remaining.Contains).ToHashSet();
            if (relevant.Count == 0)
                relevant.Add(chosen.RequestedItemId);

            targets.Add(new FarmTarget
            {
                ItemId = chosen.RequestedItemId,
                BNpcNameId = chosen.Source.BNpcNameId,
                MobName = chosen.Source.MobName,
                TerritoryId = chosen.Source.TerritoryId,
                TerritoryName = chosen.Source.TerritoryName,
                MobLevel = chosen.Source.MobLevel,
                NearestAetheryte = chosen.Source.NearestAetheryte,
                RequiredQuantity = requiredQuantities.GetValueOrDefault(chosen.RequestedItemId),
                Clusters = chosen.Source.Clusters,
                RelevantDropItemIds = relevant,
            });

            foreach (var itemId in relevant)
                remaining.Remove(itemId);
            territoryCursor = chosen.Source.TerritoryId;
        }

        return new FarmPlan(targets);
    }
}
