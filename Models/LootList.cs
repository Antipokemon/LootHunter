namespace LootHunter.Models;

public enum QuantityMode
{
    TargetInventory,
    GatherAdditional,
}

public sealed class LootList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Loot List";
    public bool Enabled { get; set; } = true;
    public QuantityMode QuantityMode { get; set; } = QuantityMode.TargetInventory;
    public List<LootListEntry> Items { get; set; } = [];
}

public sealed class LootListEntry
{
    public uint ItemId { get; set; }
    public uint Quantity { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public uint? PreferredBNpcNameId { get; set; }
    public uint? PreferredTerritoryId { get; set; }
}
