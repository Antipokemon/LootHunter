using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace LootHunter.Services;

public sealed class InventoryService : IInventoryService
{
    private readonly IGameInventory gameInventory;
    private readonly object signalLock = new();
    private TaskCompletionSource<bool> changeSignal = NewSignal();
    private long changeVersion;
    private bool disposed;

    public InventoryService(IGameInventory gameInventory)
    {
        this.gameInventory = gameInventory;
        gameInventory.InventoryChangedRaw += OnInventoryChanged;
    }

    public long ChangeVersion => Interlocked.Read(ref changeVersion);

    public unsafe uint GetItemCount(uint itemId)
    {
        if (itemId == 0)
            return 0;

        var manager = InventoryManager.Instance();
        return manager == null ? 0u : (uint)Math.Max(0, manager->GetInventoryItemCount(itemId));
    }

    public unsafe int GetFreeNormalInventorySlots()
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

    public async Task<bool> WaitForChangeAsync(long afterVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (ChangeVersion > afterVersion)
            return true;

        Task signal;
        lock (signalLock)
        {
            if (ChangeVersion > afterVersion)
                return true;
            signal = changeSignal.Task;
        }

        try
        {
            await signal.WaitAsync(timeout, cancellationToken);
            return ChangeVersion > afterVersion;
        }
        catch (TimeoutException)
        {
            return ChangeVersion > afterVersion;
        }
    }

    private void OnInventoryChanged(IReadOnlyCollection<Dalamud.Game.Inventory.InventoryEventArgTypes.InventoryEventArgs> _)
    {
        Interlocked.Increment(ref changeVersion);

        TaskCompletionSource<bool> completed;
        lock (signalLock)
        {
            completed = changeSignal;
            changeSignal = NewSignal();
        }
        completed.TrySetResult(true);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        gameInventory.InventoryChangedRaw -= OnInventoryChanged;
        lock (signalLock)
            changeSignal.TrySetCanceled();
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
