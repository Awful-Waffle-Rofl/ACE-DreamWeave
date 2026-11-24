namespace ACE.Server.Entity
{
    /// <summary>
    /// What kind of attack a recorded hit was, stamped on <see cref="DamageHistoryEntry.AttackKind"/> by the damage sinks
    /// that record the unclamped hit (DamageHistory.Add with a thrown figure). Recording only: nothing in the damage path
    /// reads it. The lowercase wire spelling (player_death_event.attack_kind) is <see cref="DamageAttackKindNames.Wire"/>.
    /// </summary>
    public enum DamageAttackKind : byte
    {
        /// <summary>A melee strike (a DamageEvent whose CombatType is Melee).</summary>
        Melee,

        /// <summary>A physical missile (a DamageEvent whose CombatType is Missile).</summary>
        Missile,

        /// <summary>Magic: a spell projectile, a Harm-style life boost, or a Drain Health transfer.</summary>
        Magic,

        /// <summary>The hazard library and the ML digsite boss mechanics (isHazard), plus a Hotspot (lava, traps).</summary>
        Hazard,

        /// <summary>A MonsterEffects proc: DoT and flat-damage effects, and a Reflect effect's damage back.</summary>
        Effect,

        /// <summary>Anything else that reaches a recording sink (a developer command, an unclassified caller).</summary>
        Other,
    }

    public static class DamageAttackKindNames
    {
        /// <summary>The kind for a DamageEvent's CombatType (Player.TakeDamage's DamageEvent overload).</summary>
        public static DamageAttackKind Of(ACE.Server.WorldObjects.CombatType combatType)
        {
            switch (combatType)
            {
                case ACE.Server.WorldObjects.CombatType.Melee: return DamageAttackKind.Melee;
                case ACE.Server.WorldObjects.CombatType.Missile: return DamageAttackKind.Missile;
                case ACE.Server.WorldObjects.CombatType.Magic: return DamageAttackKind.Magic;
                default: return DamageAttackKind.Other;
            }
        }

        /// <summary>
        /// The kind Player.TakeDamage records when its caller names none: hazard for a hazard hit, effect for a
        /// MonsterEffects hit (the callers that suppress the defender notification), other for everything else.
        /// </summary>
        public static DamageAttackKind Default(bool isHazard, bool sendDefenderNotification)
            => isHazard ? DamageAttackKind.Hazard : !sendDefenderNotification ? DamageAttackKind.Effect : DamageAttackKind.Other;

        /// <summary>
        /// The crit flag to record for a hit of <paramref name="kind"/>: the caller's value for melee, missile and magic,
        /// which roll crits; null (not applicable) for hazard, effect and other, whose callers never pass a real crit.
        /// </summary>
        public static bool? CritFor(DamageAttackKind kind, bool crit)
            => kind == DamageAttackKind.Melee || kind == DamageAttackKind.Missile || kind == DamageAttackKind.Magic ? crit : (bool?)null;

        /// <summary>The lowercase wire spelling (player_death_event.attack_kind); every spelling must fit VARCHAR(8).</summary>
        public static string Wire(DamageAttackKind kind)
        {
            switch (kind)
            {
                case DamageAttackKind.Melee: return "melee";
                case DamageAttackKind.Missile: return "missile";
                case DamageAttackKind.Magic: return "magic";
                case DamageAttackKind.Hazard: return "hazard";
                case DamageAttackKind.Effect: return "effect";
                default: return "other";
            }
        }
    }
}
