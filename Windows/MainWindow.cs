using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LootHunter.Automation;
using LootHunter.Models;

namespace LootHunter.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private Guid? selectedListId;
    private string itemSearch = string.Empty;
    private string newListName = string.Empty;

    public MainWindow(Plugin plugin) : base("LootHunter##Main")
    {
        this.plugin = plugin;
        selectedListId = plugin.LootLists.Lists.FirstOrDefault()?.Id;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        var list = GetSelectedList();
        if (list is null)
        {
            ImGui.TextDisabled("No loot list is selected.");
            return;
        }

        DrawListEditor(list);
        ImGui.Separator();
        DrawRunControls(list);
        ImGui.Separator();
        DrawSession();
    }

    private void DrawHeader()
    {
        var lists = plugin.LootLists.Lists;
        var selected = GetSelectedList();
        var preview = selected?.Name ?? "Select a loot list";

        ImGui.SetNextItemWidth(280f);
        if (ImGui.BeginCombo("Loot list", preview))
        {
            foreach (var list in lists)
            {
                var isSelected = list.Id == selectedListId;
                if (ImGui.Selectable($"{list.Name}##{list.Id}", isSelected))
                    selectedListId = list.Id;
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        ImGui.InputText("##NewListName", ref newListName, 80);
        ImGui.SameLine();
        if (ImGui.Button("New list"))
        {
            var created = plugin.LootLists.Create(newListName);
            selectedListId = created.Id;
            newListName = string.Empty;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(selected is null || plugin.FarmController.Session.IsRunning);
        if (ImGui.Button("Delete list") && selected is not null)
        {
            var deletedId = selected.Id;
            plugin.LootLists.Delete(deletedId);
            selectedListId = plugin.LootLists.Lists.FirstOrDefault()?.Id;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();
    }

    private void DrawListEditor(LootList list)
    {
        ImGui.TextUnformatted("List configuration");

        var name = list.Name;
        ImGui.SetNextItemWidth(320f);
        if (ImGui.InputText("Name", ref name, 100))
        {
            list.Name = string.IsNullOrWhiteSpace(name) ? "Loot List" : name;
            plugin.LootLists.Save();
        }

        var modeLabel = list.QuantityMode == QuantityMode.TargetInventory ? "Target inventory total" : "Gather additional";
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("Quantity mode", modeLabel))
        {
            if (ImGui.Selectable("Target inventory total", list.QuantityMode == QuantityMode.TargetInventory))
            {
                list.QuantityMode = QuantityMode.TargetInventory;
                plugin.LootLists.Save();
            }
            if (ImGui.Selectable("Gather additional", list.QuantityMode == QuantityMode.GatherAdditional))
            {
                list.QuantityMode = QuantityMode.GatherAdditional;
                plugin.LootLists.Save();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(list.QuantityMode == QuantityMode.TargetInventory
            ? "Stop when inventory reaches the requested total."
            : "Collect the requested amount in addition to what you have when the run starts.");

        ImGui.Spacing();
        DrawItemSearch(list);
        ImGui.Spacing();
        DrawItems(list);
    }

    private void DrawItemSearch(LootList list)
    {
        ImGui.TextUnformatted("Add monster drops");
        ImGui.SetNextItemWidth(320f);
        ImGui.InputTextWithHint("##ItemSearch", "Search item name...", ref itemSearch, 100);

        if (!plugin.MobDatabase.IsReady)
        {
            ImGui.TextWrapped($"Monster-drop database unavailable: {plugin.MobDatabase.LoadError ?? "not ready"}");
            return;
        }

        if (string.IsNullOrWhiteSpace(itemSearch))
        {
            ImGui.TextDisabled("Type an item name to search the monster-drop database.");
            return;
        }

        var results = plugin.MobDatabase.SearchDropItems(itemSearch, 12);
        if (results.Count == 0)
        {
            ImGui.TextDisabled("No monster-drop items matched that search.");
            return;
        }

        ImGui.BeginChild("ItemSearchResults", new Vector2(0, Math.Min(180, 28 * results.Count + 8)));
        foreach (var result in results)
        {
            var alreadyAdded = list.Items.Any(x => x.ItemId == result.ItemId);
            ImGui.BeginDisabled(alreadyAdded || plugin.FarmController.Session.IsRunning);
            if (ImGui.SmallButton(alreadyAdded ? $"Added##{result.ItemId}" : $"Add##{result.ItemId}"))
                plugin.LootLists.AddItem(list, result.ItemId, 1);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextUnformatted(result.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"({result.SourceCount} source{(result.SourceCount == 1 ? string.Empty : "s")})");
        }
        ImGui.EndChild();
    }

    private void DrawItems(LootList list)
    {
        ImGui.TextUnformatted("Requested items");
        if (list.Items.Count == 0)
        {
            ImGui.TextDisabled("This list is empty.");
            return;
        }

        uint? removeItemId = null;
        if (ImGui.BeginTable("LootItems", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 36f);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 105f);
            ImGui.TableSetupColumn("Sources", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 64f);
            ImGui.TableHeadersRow();

            foreach (var entry in list.Items)
            {
                ImGui.TableNextRow();
                ImGui.PushID((int)entry.ItemId);

                ImGui.TableSetColumnIndex(0);
                var enabled = entry.Enabled;
                ImGui.BeginDisabled(plugin.FarmController.Session.IsRunning);
                if (ImGui.Checkbox("##Enabled", ref enabled))
                {
                    entry.Enabled = enabled;
                    plugin.LootLists.Save();
                }
                ImGui.EndDisabled();

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(plugin.MobDatabase.GetItemName(entry.ItemId));
                ImGui.TextDisabled($"ID {entry.ItemId}");

                ImGui.TableSetColumnIndex(2);
                var quantity = entry.Quantity > int.MaxValue ? int.MaxValue : (int)entry.Quantity;
                ImGui.SetNextItemWidth(90f);
                ImGui.BeginDisabled(plugin.FarmController.Session.IsRunning);
                if (ImGui.InputInt("##Qty", ref quantity, 1, 10))
                {
                    entry.Quantity = (uint)Math.Max(1, quantity);
                    plugin.LootLists.Save();
                }
                ImGui.EndDisabled();

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(plugin.MobDatabase.GetSourcesForItem(entry.ItemId).Count.ToString());

                ImGui.TableSetColumnIndex(4);
                ImGui.BeginDisabled(plugin.FarmController.Session.IsRunning);
                if (ImGui.SmallButton("Remove"))
                    removeItemId = entry.ItemId;
                ImGui.EndDisabled();

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (removeItemId is { } itemId)
            plugin.LootLists.RemoveItem(list, itemId);
    }

    private void DrawRunControls(LootList list)
    {
        var session = plugin.FarmController.Session;
        ImGui.TextUnformatted("Automation");

        ImGui.BeginDisabled(session.IsRunning || list.Items.All(x => !x.Enabled));
        if (ImGui.Button("Start farming", new Vector2(120f, 0f)))
            _ = plugin.FarmController.StartAsync(list);
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (session.IsPaused)
        {
            if (ImGui.Button("Resume"))
                plugin.FarmController.Resume();
        }
        else
        {
            ImGui.BeginDisabled(!session.IsRunning);
            if (ImGui.Button("Pause"))
                plugin.FarmController.Pause();
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!session.IsRunning);
        if (ImGui.Button("Stop"))
            plugin.FarmController.Stop();
        ImGui.EndDisabled();
    }

    private void DrawSession()
    {
        var session = plugin.FarmController.Session;
        ImGui.TextUnformatted("Session");
        ImGui.Text($"State: {session.State}");
        ImGui.TextWrapped(session.StatusMessage);

        if (!string.IsNullOrWhiteSpace(session.CurrentMobName))
            ImGui.Text($"Current: {session.CurrentMobName} — {session.CurrentTerritoryName ?? "unknown zone"}");
        if (session.CurrentClusterIndex is not null && session.CurrentClusterCount is not null)
            ImGui.Text($"Spawn cluster: {session.CurrentClusterIndex}/{session.CurrentClusterCount}");

        ImGui.Text($"Kills: {session.Kills}   Drops obtained: {session.DropsObtained}   Empty cluster cycles: {session.EmptySpawnCycles}");

        if (session.Progress.Count > 0)
        {
            ImGui.Spacing();
            foreach (var progress in session.Progress)
            {
                var fraction = progress.Goal == 0 ? 1f : Math.Clamp(progress.Current / (float)progress.Goal, 0f, 1f);
                ImGui.TextUnformatted(progress.ItemName);
                ImGui.ProgressBar(fraction, new Vector2(-1f, 0f), $"{progress.Current}/{progress.Goal}");
            }
        }

        if (session.Warnings.Count > 0 && ImGui.CollapsingHeader($"Warnings ({session.Warnings.Count})"))
        {
            foreach (var warning in session.Warnings)
                ImGui.BulletText(warning);
        }

        if (session.LastError is not null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Error: {session.LastError.Message}");
        }
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
