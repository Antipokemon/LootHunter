using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace LootHunter.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin) : base("LootHunter Settings##Config")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620f, 460f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var changed = false;
        if (ImGui.BeginTabBar("LootHunterSettingsTabs"))
        {
            if (ImGui.BeginTabItem("Travel"))
            {
                changed |= DrawTravelSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Combat"))
            {
                changed |= DrawCombatSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Recovery"))
            {
                changed |= DrawRecoverySettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Plugins"))
            {
                DrawPluginSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Interface"))
            {
                DrawInterfaceSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (changed)
            plugin.Configuration.Save();
    }

    private bool DrawTravelSettings()
    {
        var config = plugin.Configuration;
        var changed = false;

        var autoMount = config.AutoMount;
        if (ImGui.Checkbox("Auto-mount for long routes", ref autoMount))
        {
            config.AutoMount = autoMount;
            changed = true;
        }

        var mountDistance = config.AutoMountMinimumDistance;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputFloat("Mount minimum distance (yalms)", ref mountDistance, 5f, 20f))
        {
            config.AutoMountMinimumDistance = Math.Max(0f, mountDistance);
            changed = true;
        }

        var useFlight = config.UseFlight;
        if (ImGui.Checkbox("Allow flight routes", ref useFlight))
        {
            config.UseFlight = useFlight;
            changed = true;
        }

        ImGui.Spacing();
        DrawSectionLabel("Completion");

        var teleportOnCompletion = config.TeleportOnCompletion;
        if (ImGui.Checkbox("Teleport when the list is complete", ref teleportOnCompletion))
        {
            config.TeleportOnCompletion = teleportOnCompletion;
            changed = true;
        }

        ImGui.BeginDisabled(!teleportOnCompletion);
        var destination = config.CompletionTeleportDestination;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo("Destination", GetCompletionDestinationName(destination)))
        {
            foreach (var option in Enum.GetValues<CompletionTeleportDestination>())
            {
                if (ImGui.Selectable(GetCompletionDestinationName(option), destination == option))
                {
                    config.CompletionTeleportDestination = option;
                    destination = option;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }

        if (destination == CompletionTeleportDestination.Custom)
        {
            var command = config.CompletionTeleportCustomCommand;
            ImGui.SetNextItemWidth(320f);
            if (ImGui.InputText("Lifestream destination", ref command, 120))
            {
                config.CompletionTeleportCustomCommand = command;
                changed = true;
            }
        }
        ImGui.EndDisabled();

        return changed;
    }

    private bool DrawCombatSettings()
    {
        var config = plugin.Configuration;
        var changed = false;

        var avoidAreaAttacks = config.AvoidAreaAttacks;
        if (ImGui.Checkbox("Avoid area attacks with BossMod Reborn", ref avoidAreaAttacks))
        {
            config.AvoidAreaAttacks = avoidAreaAttacks;
            changed = true;
        }

        var combatDistance = config.CombatApproachDistance;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputFloat("Combat approach distance", ref combatDistance, 0.5f, 1f))
        {
            config.CombatApproachDistance = Math.Clamp(combatDistance, 1.5f, 15f);
            changed = true;
        }

        var scanRadius = config.MobScanRadius;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputFloat("Farm target scan radius", ref scanRadius, 5f, 20f))
        {
            config.MobScanRadius = Math.Clamp(scanRadius, 20f, 200f);
            changed = true;
        }

        var hostileRadius = config.HostileScanRadius;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputFloat("Additional attacker scan radius", ref hostileRadius, 5f, 10f))
        {
            config.HostileScanRadius = Math.Clamp(hostileRadius, 10f, 80f);
            changed = true;
        }

        ImGui.Spacing();
        DrawSectionLabel("Safety");

        var levelDifference = config.MinimumLevelDifference;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Required player level advantage", ref levelDifference))
        {
            config.MinimumLevelDifference = Math.Clamp(levelDifference, -20, 50);
            changed = true;
        }

        var skipUnsafe = config.SkipUnsafeTargets;
        if (ImGui.Checkbox("Skip unsafe targets", ref skipUnsafe))
        {
            config.SkipUnsafeTargets = skipUnsafe;
            changed = true;
        }

        ImGui.Spacing();
        DrawSectionLabel("BossMod fallback");

        var preset = config.BossModPresetName;
        ImGui.SetNextItemWidth(320f);
        if (ImGui.InputText("Autorotation preset", ref preset, 120))
        {
            config.BossModPresetName = preset.Trim();
            changed = true;
        }

        return changed;
    }

    private bool DrawRecoverySettings()
    {
        var config = plugin.Configuration;
        var changed = false;

        var cycles = config.MaxEmptyClusterCycles;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Maximum empty cluster passes", ref cycles))
        {
            config.MaxEmptyClusterCycles = Math.Clamp(cycles, 1, 20);
            changed = true;
        }

        var respawn = config.RespawnWaitSeconds;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Respawn wait (seconds)", ref respawn))
        {
            config.RespawnWaitSeconds = Math.Clamp(respawn, 1, 120);
            changed = true;
        }

        var settle = config.LootSettleMilliseconds;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Loot stable window (ms)", ref settle, 100, 250))
        {
            config.LootSettleMilliseconds = Math.Clamp(settle, 250, 3000);
            changed = true;
        }

        var lootTimeout = config.LootWaitTimeoutMilliseconds;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Loot wait timeout (ms)", ref lootTimeout, 250, 1000))
        {
            config.LootWaitTimeoutMilliseconds = Math.Clamp(lootTimeout, 1000, 15000);
            changed = true;
        }

        var navFailures = config.MaxNavigationFailuresPerSource;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Navigation failures per source", ref navFailures))
        {
            config.MaxNavigationFailuresPerSource = Math.Clamp(navFailures, 1, 10);
            changed = true;
        }

        ImGui.Spacing();
        DrawSectionLabel("Timeouts");

        var teleportTimeout = config.TeleportTimeoutSeconds;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Teleport (seconds)", ref teleportTimeout))
        {
            config.TeleportTimeoutSeconds = Math.Clamp(teleportTimeout, 15, 180);
            changed = true;
        }

        var navTimeout = config.NavigationTimeoutSeconds;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Navigation (seconds)", ref navTimeout))
        {
            config.NavigationTimeoutSeconds = Math.Clamp(navTimeout, 15, 300);
            changed = true;
        }

        var navStall = config.NavigationStallSeconds;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Navigation stall (seconds)", ref navStall))
        {
            config.NavigationStallSeconds = Math.Clamp(navStall, 5, 60);
            changed = true;
        }

        var combatTimeout = config.CombatTimeoutSeconds;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputInt("Combat (seconds)", ref combatTimeout))
        {
            config.CombatTimeoutSeconds = Math.Clamp(combatTimeout, 15, 600);
            changed = true;
        }

        return changed;
    }

    private void DrawPluginSettings()
    {
        if (ImGui.BeginTable("SettingsPluginRequirements", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("Details");
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 82f);
            ImGui.TableHeadersRow();

            foreach (var requirement in plugin.FarmController.PluginRequirements)
            {
                ImGui.TableNextRow();
                ImGui.PushID(requirement.Name);

                ImGui.TableSetColumnIndex(0);
                ImGui.TextColored(
                    requirement.Available ? new Vector4(0.35f, 0.85f, 0.45f, 1f) : new Vector4(0.95f, 0.35f, 0.35f, 1f),
                    requirement.Available ? "Ready" : requirement.Mandatory ? "Missing" : "Optional");

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(requirement.Name);
                if (requirement.Mandatory)
                    ImGui.TextDisabled("Required");

                ImGui.TableSetColumnIndex(2);
                ImGui.TextWrapped(requirement.Detail);

                ImGui.TableSetColumnIndex(3);
                if (ImGui.SmallButton(requirement.Available ? "Manage" : "Install"))
                    Plugin.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.AllPlugins, requirement.InstallerSearch);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("Plugin repositories"))
            Plugin.PluginInterface.OpenDalamudSettingsTo(SettingsOpenKind.Experimental, "Custom Plugin Repositories");
    }

    private void DrawInterfaceSettings()
    {
        var showCompactList = plugin.IsLootListUiOpen;
        if (ImGui.Checkbox("Show compact loot-list window", ref showCompactList))
            plugin.SetLootListUiOpen(showCompactList);
    }

    private static void DrawSectionLabel(string label)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(label);
    }

    private static string GetCompletionDestinationName(CompletionTeleportDestination destination)
        => destination switch
        {
            CompletionTeleportDestination.ResidentialDistrict => "Residential district (automatic)",
            CompletionTeleportDestination.FreeCompanyEstate => "Free Company estate",
            CompletionTeleportDestination.Apartment => "Apartment",
            CompletionTeleportDestination.Inn => "Inn room",
            CompletionTeleportDestination.Custom => "Custom Lifestream location",
            _ => destination.ToString(),
        };
}
