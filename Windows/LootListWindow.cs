using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LootHunter.Models;

namespace LootHunter.Windows;

public sealed class LootListWindow : Window
{
    private readonly Plugin plugin;
    private Guid? selectedListId;

    public LootListWindow(Plugin plugin) : base("LootHunter List##CompactList")
    {
        this.plugin = plugin;
        selectedListId = plugin.LootLists.Lists.FirstOrDefault()?.Id;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300f, 180f),
            MaximumSize = new Vector2(620f, float.MaxValue),
        };
    }

    public void SelectList(Guid listId) => selectedListId = listId;

    public override void OnOpen()
    {
        plugin.Configuration.ShowCompactListWindow = true;
        plugin.Configuration.Save();
    }

    public override void OnClose()
    {
        plugin.Configuration.ShowCompactListWindow = false;
        plugin.Configuration.Save();
    }

    public override void Draw()
    {
        var list = GetSelectedList();
        if (list is null)
        {
            ImGui.TextDisabled("No loot list is available.");
            return;
        }

        DrawListSelector(list);
        DrawEnabledHeader(list);
        ImGui.Spacing();
        DrawItems(list);
    }

    private void DrawListSelector(LootList selected)
    {
        if (plugin.LootLists.Lists.Count <= 1)
            return;

        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo("##CompactListSelector", selected.Name))
            return;

        foreach (var list in plugin.LootLists.Lists)
        {
            var isSelected = list.Id == selected.Id;
            if (ImGui.Selectable($"{list.Name}##Compact{list.Id}", isSelected))
                selectedListId = list.Id;
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        ImGui.Spacing();
    }

    private void DrawEnabledHeader(LootList list)
    {
        var color = list.Enabled
            ? new Vector4(0.18f, 0.46f, 0.25f, 1f)
            : new Vector4(0.42f, 0.19f, 0.19f, 1f);
        var hoveredColor = list.Enabled
            ? new Vector4(0.23f, 0.57f, 0.31f, 1f)
            : new Vector4(0.53f, 0.23f, 0.23f, 1f);

        ImGui.PushStyleColor(ImGuiCol.Header, color);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, hoveredColor);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, hoveredColor);
        if (ImGui.Selectable(
                $"List {(list.Enabled ? "enabled" : "disabled")}##CompactEnabled",
                false,
                ImGuiSelectableFlags.None,
                new Vector2(0f, 30f)))
        {
            SetListEnabled(list, !list.Enabled);
        }
        ImGui.PopStyleColor(3);
    }

    private void DrawItems(LootList list)
    {
        if (list.Items.Count == 0)
        {
            ImGui.TextDisabled("This list is empty.");
            return;
        }

        uint? removeItemId = null;
        if (!ImGui.BeginTable("CompactLootItems", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 34f);
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableHeadersRow();

        foreach (var entry in list.Items)
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)entry.ItemId);

            ImGui.TableSetColumnIndex(0);
            var enabled = entry.Enabled;
            if (ImGui.Checkbox("##CompactItemEnabled", ref enabled))
            {
                entry.Enabled = enabled;
                plugin.LootLists.Save();
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(plugin.MobDatabase.GetItemName(entry.ItemId));
            if (ImGui.IsItemHovered()
                && ImGui.GetIO().KeyCtrl
                && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                removeItemId = entry.ItemId;
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(GetQuantityText(list, entry));

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (removeItemId is { } itemId)
            plugin.LootLists.RemoveItem(list, itemId);
    }

    private string GetQuantityText(LootList list, LootListEntry entry)
    {
        var progress = plugin.FarmController.ActiveListId == list.Id
            ? plugin.FarmController.Session.Progress.FirstOrDefault(x => x.ItemId == entry.ItemId)
            : null;
        if (progress is not null)
            return $"{progress.Current}/{progress.Goal}";

        var current = plugin.Inventory.GetItemCount(entry.ItemId);
        return list.QuantityMode == QuantityMode.TargetInventory
            ? $"{current}/{entry.Quantity}"
            : $"{current} (+{entry.Quantity})";
    }

    private void SetListEnabled(LootList list, bool enabled)
    {
        if (!enabled && plugin.FarmController.ActiveListId == list.Id)
            plugin.FarmController.Stop();
        plugin.LootLists.SetEnabled(list, enabled);
    }

    private LootList? GetSelectedList()
    {
        var list = plugin.LootLists.Lists.FirstOrDefault(x => x.Id == selectedListId);
        if (list is not null)
            return list;

        list = plugin.LootLists.Lists.FirstOrDefault();
        selectedListId = list?.Id;
        return list;
    }
}
