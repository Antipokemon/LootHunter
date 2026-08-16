using Dalamud.Configuration;
using LootHunter.Models;

namespace LootHunter;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool AutoMount { get; set; } = true;
    public float AutoMountMinimumDistance { get; set; } = 60f;
    public bool UseFlight { get; set; } = false;
    public float CombatApproachDistance { get; set; } = 3.5f;

    public int MinimumLevelDifference { get; set; } = 0;
    public bool SkipUnsafeTargets { get; set; } = true;

    public int MaxEmptyClusterCycles { get; set; } = 3;
    public int RespawnWaitSeconds { get; set; } = 10;
    public int LootSettleMilliseconds { get; set; } = 1500;
    public int TeleportTimeoutSeconds { get; set; } = 60;
    public int NavigationTimeoutSeconds { get; set; } = 90;
    public int CombatTimeoutSeconds { get; set; } = 120;

    // Blank means preserve/use the currently active BossMod autorotation preset.
    public string BossModPresetName { get; set; } = string.Empty;

    public List<LootList> LootLists { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
