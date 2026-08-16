using FFXIVClientStructs.FFXIV.Client.Game;

namespace LootHunter.Services;

public sealed unsafe class InventoryService : IInventoryService
{
    public uint GetItemCount(uint itemId)
    {
        if (itemId == 0)
            return 0;

        var manager = InventoryManager.Instance();
        return manager == null ? 0u : (uint)Math.Max(0, manager->GetInventoryItemCount(itemId));
    }

    public int GetFreeNormalInventorySlots()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return 0;

        var free = 0;
        for (var type = InventoryType.Inventory1; type <= InventoryType.Inventory4; type++)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->ItemId == 0)
                    free++;
            }
        }

        return free;
    }
}
