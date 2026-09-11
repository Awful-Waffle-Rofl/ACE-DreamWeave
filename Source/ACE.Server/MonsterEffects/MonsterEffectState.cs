namespace ACE.Server.MonsterEffects
{
    /// <summary>
    /// One monster's mutable state for ONE of its effects. Handlers are stateless singletons shared by every
    /// monster in the world, so this struct is the only place a running effect may keep anything.
    ///
    /// A STRUCT held in a per-creature array, passed by ref to every hook, so a monster with three effects
    /// costs one small array and no per-effect allocations. Never persisted: an effect is rebuilt from the
    /// weenie when the creature is, exactly like Weakened Blood in Creature_ClassAbilityDebuffs.cs.
    ///
    /// The fields are a deliberately small fixed vocabulary rather than a per-effect state type, because a
    /// per-effect type would put an allocation and a cast in every hook call. Each effect uses the slots it
    /// needs and ignores the rest; what a slot means is documented by the effect that uses it, and no two
    /// effects share a slot because each effect owns its own array element.
    ///
    /// Mutated on the landblock thread that ticks the monster, the same assumption every class-ability
    /// effect reaching across to another creature already makes.
    /// </summary>
    public struct MonsterEffectState
    {
        /// <summary>Held stacks - ramps, and anything else that counts up to a cap.</summary>
        public int Stacks;

        /// <summary>Unix time of the last event this effect cared about; drives windows and cooldowns.</summary>
        public double LastTime;

        /// <summary>Absorbable damage remaining on a ward or shield.</summary>
        public uint WardAmount;

        /// <summary>Unix time at which <see cref="WardAmount"/> lapses.</summary>
        public double WardExpire;

        /// <summary>Fractional remainder carried between ticks, so integer damage does not round to nothing.</summary>
        public double Carry;

        /// <summary>Set once the effect has announced itself, so a per-tick effect emits one line, not many.</summary>
        public bool Announced;
    }
}
