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
            MinimumSize = new Vector2(520, 500),
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

        var teleportOnCompletion = config.TeleportOnCompletion;
        if (ImGui.Checkbox("Teleport when the loot list is complete", ref teleportOnCompletion))
        {
            config.TeleportOnCompletion = teleportOnCompletion;
            changed = true;
        }

        ImGui.BeginDisabled(!teleportOnCompletion);
        var completionDestination = config.CompletionTeleportDestination;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("Completion destination", GetCompletionDestinationName(completionDestination)))
        {
            foreach (var destination in Enum.GetValues<CompletionTeleportDestination>())
            {
                if (ImGui.Selectable(GetCompletionDestinationName(destination), completionDestination == destination))
                {
                    config.CompletionTeleportDestination = destination;
                    completionDestination = destination;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }

        if (completionDestination == CompletionTeleportDestination.Custom)
        {
            var command = config.CompletionTeleportCustomCommand;
            ImGui.SetNextItemWidth(300f);
            if (ImGui.InputText("Lifestream destination", ref command, 120))
            {
                config.CompletionTeleportCustomCommand = command;
                changed = true;
            }
            ImGui.TextDisabled("Enter Lifestream command arguments, such as Limsa Lominsa or /li Limsa Lominsa.");
        }
        else
        {
            ImGui.TextDisabled("The default uses Lifestream's configured automatic property priority.");
        }
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextUnformatted("Combat and safety");

        var combatDistance = config.CombatApproachDistance;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputFloat("Combat approach distance", ref combatDistance, 0.5f, 1f))
        {
            config.CombatApproachDistance = Math.Clamp(combatDistance, 1.5f, 15f);
            changed = true;
        }
        ImGui.TextDisabled("LootHunter directly follows the live target to this distance; autorotation handles combat actions.");

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

        var scanRadius = config.MobScanRadius;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputFloat("Mob scan radius (yalms)", ref scanRadius, 5f, 20f))
        {
            config.MobScanRadius = Math.Clamp(scanRadius, 20f, 200f);
            changed = true;
        }
        ImGui.TextDisabled("LootHunter scans for the current farm mob while traveling and interrupts navigation to fight it.");

        var preset = config.BossModPresetName;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.InputText("BossModReborn autorotation preset", ref preset, 120))
        {
            config.BossModPresetName = preset.Trim();
            changed = true;
        }
        ImGui.TextDisabled("Used only if WrathCombo IPC is unavailable and LootHunter falls back to BossModReborn.");

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
        if (ImGui.InputInt("Loot stable window (ms)", ref settle, 100, 250))
        {
            config.LootSettleMilliseconds = Math.Clamp(settle, 250, 3000);
            changed = true;
        }

        var lootTimeout = config.LootWaitTimeoutMilliseconds;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputInt("Loot wait timeout (ms)", ref lootTimeout, 250, 1000))
        {
            config.LootWaitTimeoutMilliseconds = Math.Clamp(lootTimeout, 1000, 15000);
            changed = true;
        }
        ImGui.TextDisabled("After a kill, LootHunter waits for the actual inventory event and then for counts to stabilize.");

        var navFailures = config.MaxNavigationFailuresPerSource;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputInt("Max navigation failures per source", ref navFailures))
        {
            config.MaxNavigationFailuresPerSource = Math.Clamp(navFailures, 1, 10);
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

            var navStall = config.NavigationStallSeconds;
            if (ImGui.InputInt("Navigation stall timeout (seconds)", ref navStall))
            {
                config.NavigationStallSeconds = Math.Clamp(navStall, 5, 60);
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

    private static string GetCompletionDestinationName(CompletionTeleportDestination destination)
        => destination switch
        {
            CompletionTeleportDestination.ResidentialDistrict => "Residential district (automatic, default)",
            CompletionTeleportDestination.FreeCompanyEstate => "Free Company estate",
            CompletionTeleportDestination.Apartment => "Apartment",
            CompletionTeleportDestination.Inn => "Inn room",
            CompletionTeleportDestination.Custom => "Custom Lifestream location",
            _ => destination.ToString(),
        };
}
