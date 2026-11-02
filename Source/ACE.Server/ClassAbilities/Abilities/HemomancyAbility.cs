using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T1 splash: being healed arms a short damage buff. A single heal of at least 5% of maximum
    /// health - or leech heals (Drain, Transfusion, Bloodlust) adding up to 5% inside 2 seconds - grants
    /// 2/4/6/8/10% more damage by rank for 8 seconds. Re-arming refreshes the timer; it never stacks.
    /// (Reworked in the Blood Mage buff pass: the old DoT tick bonus, 1% tick heal and Drain cap bonus are
    /// gone.)
    ///
    /// ARMING happens at the heal sites, through Player.OnHealthRestored, always with the APPLIED delta
    /// (overheal arms nothing). The rules live in ClassAbilities/HemomancyMath.cs and the per-player state in
    /// Player_Hemomancy.cs.
    ///
    /// THE BUFF IS READ AT FOUR KINDS OF DAMAGE SITE, all through Player.GetHemomancyDamageMod and all behind
    /// existing not-a-Player-target gates (PvE only):
    ///  - weapon hits, via this handler's <see cref="ModifyOutgoingDamage"/> (the outgoing-damage dispatch
    ///    is monsters-only by construction);
    ///  - the magic sites (war/void projectiles, Harm, life projectiles), multiplied in
    ///    Player_ClassAbilityBuffs.cs;
    ///  - damage-over-time ticks of every school, in EnchantmentManager.ApplyDamageTick.
    /// Drain is deliberately excluded: a heal-armed bonus on the spell that heals would feed itself.
    ///
    /// DISPATCH ORDER: the default Modify band. The handler only multiplies DamageEvent.Damage, which
    /// commutes with every other Modify multiplier; it does not read the crit bonus Killer Instinct stashes
    /// in the earlier CritDamage band, and it casts nothing, so it has no business in SpellCastingProc.
    /// The one Modify-band reader of Damage is Bloodlust (the ability), whose lifesteal therefore sees or
    /// misses this bonus by registration order; that is the same standing every other Modify multiplier
    /// already has relative to it, and the Bloodlust gear mod runs after the whole loop and always sees it.
    ///
    /// AFFINITY: Healing, multiplying the rank bonus only (no gear term - this ability carries no equipment
    /// mod). The single affinity call lives in <see cref="ActiveDamageMultiplier"/> and the readout below.
    /// </summary>
    public class HemomancyAbility : IClassAbility, IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Hemomancy,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 1,
            Name = "hemomancy",
            DisplayName = "Hemomancy",
            Description = "Being healed for at least 5% of your maximum health at once - or leeching that much " +
                          "within 2 seconds through Drain, Transfusion or Bloodlust - makes your attacks and " +
                          "spells (except Drain) deal 2/4/6/8/10% more damage by rank for 8 seconds. Healing " +
                          "again refreshes the timer. Higher Healing multiplies the bonus.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Healing,
        };

        /// <summary>
        /// The weapon leg: melee, missile and multishot extra hits. Outgoing-damage dispatch never runs for a
        /// Player target, which is this leg's PvE gate. 1.0 (no change) unless the buff is live.
        /// </summary>
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || rank <= 0)
                return;

            var mod = attacker.GetHemomancyDamageMod();

            if (mod > 1.0f)
                damageEvent.Damage *= mod;
        }

        /// <summary>
        /// The live multiplier as if the buff were active, from the CURRENT rank and Healing affinity (1.0
        /// when unlearned). Player.GetHemomancyDamageMod calls this only after it has checked the buff is
        /// live, so a rank bought or lost mid-buff applies on the next strike.
        /// </summary>
        public static float ActiveDamageMultiplier(Player player)
        {
            if (player == null || !player.TryGetClassAbility(ClassAbilityId.Hemomancy, out var rank))
                return 1.0f;

            return HemomancyMath.DamageMultiplier(rank,
                PropertyManager.GetDouble("class_ability_hemomancy_damage_percent_per_rank").Item,
                player.GetClassAbilityAffinityMultiplier(Skill.Healing),
                active: true);
        }

        /// <summary>
        /// Mirrors <see cref="ActiveDamageMultiplier"/> (x100 for display), reported as if the buff were
        /// armed - a "how strong is the ability" readout, not a "is it up right now" one. Affinity is a
        /// MULTIPLIER on the rank term, reported as the AMOUNT it adds so the three terms sum to Effective.
        /// No gear term and no cap.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_hemomancy_damage_percent_per_rank").Item;
            var rankBonus = rank <= 0 ? 0.0 : rank * perRank;

            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.Healing) ?? 1.0;

            // same expression as HemomancyMath.DamageMultiplier, kept in double so the display does not pick up
            // float rounding noise
            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * (multiplier < 1.0 ? 1.0 : multiplier) - rankBonus) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = skill + affinity,
                Unit = "%",
                Label = "dmg after heal",
                Per = null,
                CapNote = null,
            };
        }
    }
}
