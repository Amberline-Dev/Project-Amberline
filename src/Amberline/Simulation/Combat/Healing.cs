using Amberline.Core;

namespace Amberline.Simulation.Combat;

/// <summary>Applies an already-modified heal and credits only the restored health.</summary>
public static class Healing
{
    /// <summary>
    /// Restores health. <paramref name="announce"/> marks a script-driven heal (spell, buff, item or
    /// summoner effect): the stock heal routine (reached
    /// only from <c>BBIncHealth</c> and the legacy Lua <c>IncHealth</c>) sends UnitApplyHeal (0x84) after
    /// every heal that raised health, carrying the requested amount rather than the clipped one. Pass
    /// <c>false</c> for engine sustain that is not a script call (natural regeneration, fountain, life steal).
    /// </summary>
    public static double Apply(Entity recipient, double amount, Entity? source = null, bool announce = true)
    {
        if (!double.IsFinite(amount)) throw new ArgumentOutOfRangeException(nameof(amount));
        if (!recipient.Alive || amount <= 0 || recipient.Health >= recipient.MaxHealth) return 0;
        var before = recipient.Health;
        recipient.Health = Math.Min(recipient.MaxHealth, before + amount);
        var restored = recipient.Health - before;
        var healer = source ?? recipient;
        if (healer.Kind == EntityKind.Hero && healer.UnitType == UnitType.Hero)
            healer.MatchStatistics.RecordHealing(recipient.NetId, restored);
        if (announce && restored > 0)
            recipient.PendingHealAnnouncements.Add(new HealAnnouncement((float)recipient.MaxHealth, (float)amount));
        return restored;
    }
}
