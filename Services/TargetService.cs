using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace LootHunter.Services;

public sealed class TargetService(IObjectTable objectTable, ITargetManager targetManager) : ITargetService
{
    public IBattleNpc? FindTarget(uint bNpcNameId, Vector3? around = null, float maxDistance = float.MaxValue)
    {
        var origin = around ?? objectTable.LocalPlayer?.Position ?? Vector3.Zero;
        return objectTable
            .OfType<IBattleNpc>()
            .Where(x => x.NameId == bNpcNameId)
            .Where(x => x.BattleNpcKind == BattleNpcSubKind.Combatant)
            .Where(x => x.IsTargetable && x.CurrentHp > 0)
            .Select(x => new { Mob = x, Distance = Vector3.Distance(origin, x.Position) })
            .Where(x => x.Distance <= maxDistance)
            .OrderBy(x => x.Distance)
            .Select(x => x.Mob)
            .FirstOrDefault();
    }

    public IBattleNpc? FindHostileTarget(float maxDistance)
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
            return null;

        return objectTable
            .OfType<IBattleNpc>()
            .Where(x => x.BattleNpcKind == BattleNpcSubKind.Combatant)
            .Where(x => x.IsTargetable && x.CurrentHp > 0)
            .Where(x => x.StatusFlags.HasFlag(StatusFlags.Hostile | StatusFlags.InCombat))
            .Where(x => x.TargetObjectId == player.GameObjectId)
            .Select(x => new { Mob = x, Distance = Vector3.Distance(player.Position, x.Position) })
            .Where(x => x.Distance <= maxDistance)
            .OrderBy(x => x.Distance)
            .Select(x => x.Mob)
            .FirstOrDefault();
    }

    public void SetTarget(IBattleNpc target) => targetManager.Target = target;
}
