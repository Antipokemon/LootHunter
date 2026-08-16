using System.Numerics;
using Dalamud.Bindings.ImGui;
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
            MinimumSize = new Vector2(480, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var changed = false;

        ImGui.TextUnformatted("Travel");
        var autoMount = config.AutoMount;
        if (ImGui.Checkbox("Auto-mount for long routes", ref autoMount))
        {
            config.AutoMount = autoMount;
            changed = true;
        }

        var mountDistance = config.AutoMountMinimumDistance;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputFloat("Mount minimum distance (yalms)", ref mountDistance, 5f, 20f))
        {
            config.AutoMountMinimumDistance = Math.Max(0f, mountDistance);
            changed = true;
        }

        var useFlight = config.UseFlight;
        if (ImGui.Checkbox("Allow vnavmesh flight routes", ref useFlight))
        {
            config.UseFlight = useFlight;
            changed = true;
        }
        ImGui.TextDisabled("Flight is attempted only after mounting; disable this if you prefer ground routes.");

        ImGui.Separator();
        ImGui.TextUnformatted("Combat and safety");

        var combatDistance = config.CombatApproachDistance;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputFloat("Combat approach distance", ref combatDistance, 0.5f, 1f))
        {
            config.CombatApproachDistance = Math.Clamp(combatDistance, 1.5f, 15f);
            changed = true;
        }

        var levelDifference = config.MinimumLevelDifference;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputInt("Required player level advantage", ref levelDifference))
        {
            config.MinimumLevelDifference = Math.Clamp(levelDifference, -20, 50);
            changed = true;
        }
        ImGui.TextDisabled("0 means your effective level must be at least the monster's level; positive values require an advantage.");

        var skipUnsafe = config.SkipUnsafeTargets;
        if (ImGui.Checkbox("Skip monsters that fail the level safety check", ref skipUnsafe))
        {
            config.SkipUnsafeTargets = skipUnsafe;
            changed = true;
        }

        var preset = config.BossModPresetName;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.InputText("BossModReborn autorotation preset", ref preset, 120))
        {
            config.BossModPresetName = preset.Trim();
            changed = true;
        }
        ImGui.TextDisabled("Leave blank to let LootHunter create and manage its own BossModReborn overworld combat preset.");

        ImGui.Separator();
        ImGui.TextUnformatted("Spawn and recovery behavior");

        var cycles = config.MaxEmptyClusterCycles;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputInt("Maximum empty cluster passes", ref cycles))
        {
            config.MaxEmptyClusterCycles = Math.Clamp(cycles, 1, 20);
            changed = true;
        }

        var respawn = config.RespawnWaitSeconds;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputInt("Respawn wait (seconds)", ref respawn))
        {
            config.RespawnWaitSeconds = Math.Clamp(respawn, 1, 120);
            changed = true;
        }

        var settle = config.LootSettleMilliseconds;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputInt("Loot settle delay (ms)", ref settle, 100, 500))
        {
            config.LootSettleMilliseconds = Math.Clamp(settle, 250, 10000);
            changed = true;
        }

        if (ImGui.CollapsingHeader("Timeouts"))
        {
            var teleportTimeout = config.TeleportTimeoutSeconds;
            if (ImGui.InputInt("Teleport timeout (seconds)", ref teleportTimeout))
            {
                config.TeleportTimeoutSeconds = Math.Clamp(teleportTimeout, 15, 180);
                changed = true;
            }

            var navTimeout = config.NavigationTimeoutSeconds;
            if (ImGui.InputInt("Navigation timeout (seconds)", ref navTimeout))
            {
                config.NavigationTimeoutSeconds = Math.Clamp(navTimeout, 15, 300);
                changed = true;
            }

            var combatTimeout = config.CombatTimeoutSeconds;
            if (ImGui.InputInt("Combat timeout (seconds)", ref combatTimeout))
            {
                config.CombatTimeoutSeconds = Math.Clamp(combatTimeout, 15, 600);
                changed = true;
            }
        }

        if (changed)
            config.Save();
    }
}
