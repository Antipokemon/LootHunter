using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using LootHunter.Models;

namespace LootHunter.Services;

public sealed class LevelSafetyService(IPlayerState playerState, Configuration configuration) : ILevelSafetyService
{
    public bool IsCombatJob(out string reason)
    {
        if (!playerState.IsLoaded)
        {
            reason = "Player data is not loaded.";
            return false;
        }

        var classJobId = playerState.ClassJob.RowId;
        if (classJobId == 0 || classJobId is >= 8 and <= 18)
        {
            reason = "Equip a combat class/job before starting LootHunter.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public LevelSafetyResult Check(FarmTarget target)
        => CheckLevel(target.MobLevel, target.MobName);

    public LevelSafetyResult CheckObserved(IBattleNpc target)
        => CheckLevel(target.Level, target.Name.ToString());

    private LevelSafetyResult CheckLevel(int? mobLevel, string mobName)
    {
        var playerLevel = playerState.IsLoaded ? playerState.EffectiveLevel : (short)0;
        if (mobLevel is null or <= 0)
            return new(false, true, playerLevel, null, $"{mobName}: monster level is unknown; it will be checked again when the monster is visible.");

        var safe = playerLevel - mobLevel.Value >= configuration.MinimumLevelDifference;
        var message = safe
            ? $"{mobName}: level {mobLevel} is within the configured safety threshold for your level {playerLevel}."
            : $"{mobName}: level {mobLevel} exceeds the configured safety threshold for your level {playerLevel}.";

        return new(true, safe, playerLevel, mobLevel, message);
    }
}
