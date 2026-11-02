namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Scattering: shortens how long the Vanguard's Taunt class ability holds this monster on the taunter.
    /// It NEVER makes the monster immune - a taunt always lands and always holds for at least
    /// monster_effect_taunt_hold_floor of its normal duration, however many scatter records the monster
    /// carries.
    ///
    /// pct= is the fraction of the hold REMOVED, strictly between 0 and 1: scatter pct=0.4 leaves 60% of the
    /// hold. Several records compose by product of (1 - pct), and the total is clamped to [floor, 1] at the
    /// dispatch site (Creature.GetMonsterEffectTauntHoldMultiplier), not here.
    ///
    /// READERS OF THE HOLD. The multiplier is applied once, in Creature.ApplyTaunt, to the duration written
    /// into Creature.TauntExpiry. TauntExpiry is also read by OpportunistAbility (the "target is taunting
    /// onto a fellow" check), so scatter shortens that window too - there is no separate Opportunist hook and
    /// none is needed.
    ///
    /// Stateless, and it touches nothing else on the monster.
    /// </summary>
    public sealed class ScatterEffect : IMonsterEffect, IMonsterTauntHold
    {
        public string Kind => "scatter";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var pct = spec.GetDouble("pct", 0.0);

            // written so NaN fails too
            if (!(pct > 0.0 && pct < 1.0))
            {
                error = "scatter requires pct= strictly between 0 and 1 (the fraction of the taunt hold removed)";
                return false;
            }

            error = null;
            return true;
        }

        public double GetTauntHoldMultiplier(ACE.Server.WorldObjects.Creature holder, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var pct = spec.GetDouble("pct", 0.0);

            if (!(pct > 0.0 && pct < 1.0))
                return 1.0;

            return 1.0 - pct;
        }
    }
}
