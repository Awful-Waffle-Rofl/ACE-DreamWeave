using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T3 game-changer: two-handed OR dual-wield melee swings become a full 360-degree arc and
    /// strike one additional target beyond the weapon's normal cleave cap - even a weapon with no innate
    /// cleave gains that one extra target. Each Whirlwind swing costs +100% stamina; if stamina is
    /// insufficient the swing falls back to a normal swing. Deliberately unscaled (SKILL-TABLES-PREVIEW).
    /// Bespoke: the stamina surcharge + affordability decision live in Player.ApplyWhirlwindStamina (gated
    /// on the 2H / dual-wield stance, setting Player.WhirlwindSwingActive), and the 360-degree/+1-target
    /// cleave selection reads that flag in Creature.GetCleaveTarget. Carries IPassiveStatAbility because it
    /// hooks no combat
    /// event of its own.
    /// </summary>
    public class WhirlwindAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Whirlwind,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 3,
            Name = "whirlwind",
            DisplayName = "Whirlwind",
            Description = "Your two-handed or dual-wield melee swings hit in a full 360-degree arc and strike one " +
                          "extra target - even a weapon with no innate cleave. Each Whirlwind swing costs +100% " +
                          "stamina; with too little stamina it falls back to a normal swing.",
            MaxRank = 1,
            CostPerRank = new[] { 3 },
            Implemented = true,
        };

        /// <summary>
        /// Pure shape change (360deg arc, +1 target) with no scalable magnitude - no single number is
        /// worth printing.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank) => new ClassAbilityReadout { HasValue = false };
    }
}
