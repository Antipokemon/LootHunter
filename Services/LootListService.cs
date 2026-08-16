using LootHunter.Models;

namespace LootHunter.Services;

public sealed class LootListService
{
    private readonly Configuration configuration;

    public LootListService(Configuration configuration)
    {
        this.configuration = configuration;
        if (configuration.LootLists.Count == 0)
        {
            configuration.LootLists.Add(new LootList { Name = "Monster Drops" });
            configuration.Save();
        }
    }

    public IReadOnlyList<LootList> Lists => configuration.LootLists;

    public LootList Create(string? name = null)
    {
        var list = new LootList { Name = string.IsNullOrWhiteSpace(name) ? "New Loot List" : name.Trim() };
        configuration.LootLists.Add(list);
        configuration.Save();
        return list;
    }

    public void Delete(Guid id)
    {
        configuration.LootLists.RemoveAll(x => x.Id == id);
        if (configuration.LootLists.Count == 0)
            configuration.LootLists.Add(new LootList { Name = "Monster Drops" });
        configuration.Save();
    }

    public void AddItem(LootList list, uint itemId, uint quantity = 1)
    {
        if (itemId == 0)
            return;

        var existing = list.Items.FirstOrDefault(x => x.ItemId == itemId);
        if (existing is not null)
        {
            existing.Enabled = true;
            existing.Quantity = Math.Max(1, quantity);
        }
        else
        {
            list.Items.Add(new LootListEntry { ItemId = itemId, Quantity = Math.Max(1, quantity) });
        }
        configuration.Save();
    }

    public void RemoveItem(LootList list, uint itemId)
    {
        list.Items.RemoveAll(x => x.ItemId == itemId);
        configuration.Save();
    }

    public void SetEnabled(LootList list, bool enabled)
    {
        list.Enabled = enabled;
        configuration.Save();
    }

    public void Save() => configuration.Save();
}
