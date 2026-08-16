using LootHunter.Models;

namespace LootHunter.Automation;

public sealed class FarmSession
{
    private readonly List<string> warnings = [];
    private readonly List<ItemProgress> progress = [];

    public FarmState State { get; internal set; } = FarmState.Idle;
    public string StatusMessage { get; internal set; } = "Idle";
    public string? CurrentMobName { get; internal set; }
    public string? CurrentTerritoryName { get; internal set; }
    public uint? CurrentItemId { get; internal set; }
    public int? CurrentClusterIndex { get; internal set; }
    public int? CurrentClusterCount { get; internal set; }
    public int Kills { get; internal set; }
    public int DropsObtained { get; internal set; }
    public int EmptySpawnCycles { get; internal set; }
    public DateTime? StartedAt { get; internal set; }
    public Exception? LastError { get; internal set; }
    public IReadOnlyList<string> Warnings => warnings;
    public IReadOnlyList<ItemProgress> Progress => progress;

    public bool IsRunning => State is not FarmState.Idle and not FarmState.Completed and not FarmState.Error;
    public bool IsPaused => State == FarmState.Paused;

    internal void Reset()
    {
        State = FarmState.Idle;
        StatusMessage = "Idle";
        CurrentMobName = null;
        CurrentTerritoryName = null;
        CurrentItemId = null;
        CurrentClusterIndex = null;
        CurrentClusterCount = null;
        Kills = 0;
        DropsObtained = 0;
        EmptySpawnCycles = 0;
        StartedAt = DateTime.UtcNow;
        LastError = null;
        warnings.Clear();
        progress.Clear();
    }

    internal void AddWarning(string warning)
    {
        if (!warnings.Contains(warning, StringComparer.Ordinal))
            warnings.Add(warning);
    }

    internal void SetProgress(IEnumerable<ItemProgress> values)
    {
        progress.Clear();
        progress.AddRange(values);
    }
}
