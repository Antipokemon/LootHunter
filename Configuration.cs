using Dalamud.Configuration;
using LootHunter.Models;

namespace LootHunter;

public enum CompletionTeleportDestination
{
    ResidentialDistrict,
    FreeCompanyEstate,
    Apartment,
    Inn,
    Custom,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool AutoMount { get; set; } = true;
    public float AutoMountMinimumDistance { get; set; } = 60f;
    public bool UseFlight { get; set; } = false;
    public float CombatApproachDistance { get; set; } = 3.5f;
    public float MobScanRadius { get; set; } = 90f;
    public float HostileScanRadius { get; set; } = 35f;
    public bool AvoidAreaAttacks { get; set; } = true;
    public bool TeleportOnCompletion { get; set; } = true;
    public CompletionTeleportDestination CompletionTeleportDestination { get; set; } = CompletionTeleportDestination.ResidentialDistrict;
    public string CompletionTeleportCustomCommand { get; set; } = string.Empty;

    public int MinimumLevelDifference { get; set; } = 0;
    public bool SkipUnsafeTargets { get; set; } = true;

    public int MaxEmptyClusterCycles { get; set; } = 3;
    public int RespawnWaitSeconds { get; set; } = 10;
    public int LootSettleMilliseconds { get; set; } = 650;
    public int LootWaitTimeoutMilliseconds { get; set; } = 5000;
    public int TeleportTimeoutSeconds { get; set; } = 60;
    public int NavigationTimeoutSeconds { get; set; } = 90;
    public int NavigationStallSeconds { get; set; } = 12;
    public int MaxNavigationFailuresPerSource { get; set; } = 3;
    public int CombatTimeoutSeconds { get; set; } = 120;

    // Used only by the BossModReborn fallback provider.
    public string BossModPresetName { get; set; } = string.Empty;

    public bool ShowCompactListWindow { get; set; }

    public List<LootList> LootLists { get; set; } = [];

    public string GetCompletionTeleportCommand()
        => CompletionTeleportDestination switch
        {
            CompletionTeleportDestination.ResidentialDistrict => "auto",
            CompletionTeleportDestination.FreeCompanyEstate => "fc",
            CompletionTeleportDestination.Apartment => "apartment",
            CompletionTeleportDestination.Inn => "inn",
            CompletionTeleportDestination.Custom => CompletionTeleportCustomCommand.Trim(),
            _ => "auto",
        };

    public string GetCompletionTeleportLabel()
        => CompletionTeleportDestination switch
        {
            CompletionTeleportDestination.ResidentialDistrict => "residential district",
            CompletionTeleportDestination.FreeCompanyEstate => "Free Company estate",
            CompletionTeleportDestination.Apartment => "apartment",
            CompletionTeleportDestination.Inn => "inn room",
            CompletionTeleportDestination.Custom => string.IsNullOrWhiteSpace(CompletionTeleportCustomCommand)
                ? "custom destination"
                : CompletionTeleportCustomCommand.Trim(),
            _ => "residential district",
        };

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
