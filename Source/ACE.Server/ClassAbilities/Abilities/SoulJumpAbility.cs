using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T3: when a blow would kill the summoner and a combat pet is alive, the PET dies instead
    /// and the summoner is restored to 10% of maximum health, once every 6/4/2 minutes by rank. WITH NO PET
    /// ALIVE THERE IS NO SAVE, and that is the entry's whole cost structure - it is not a free death save,
    /// it is one that spends a pet and then leaves the summoner at 10% health with nothing out.
    ///
    /// A DEATH SAVE BELONGS ON IPreWriteDamageAbility AT THE DeathSave BAND. It cannot work on
    /// IIncomingDamageAbility, which fires after the health write has already clamped at zero; see
    /// ClassAbilityHooks' IPreWriteDamageAbility doc comment, which spells out that a save falls out of
    /// reducing the damage below the defender's current Health rather than from a "survive this" flag.
    ///
    /// AND IT MUST RUN LAST, WHICH IS WHY THE BAND EXISTS. Every mitigation ahead of it may remove the very
    /// death it is reacting to: a 100 point blow on a 60 health Spellsword with a 50 point ward standing is
    /// not lethal once the ward has eaten its half, so a save that ran first would spend a pet and a
    /// 6 minute cooldown on a hit that was never going to kill anyone. The bands are ordered
    /// AbsorbPool -> Divert -> DeathSave for exactly that reason, and the read-only Observe band sits ahead
    /// of all three rather than between them so this one stays literally last in the bucket.
    ///
    /// PvE ONLY: the save requires the dispatch's filtered Attacker, so a killing blow from another player,
    /// from the summoner themselves, or from an unattributed DoT tick spends no pet and fires no cooldown.
    ///
    /// AFFINITY DIRECTION, SETTLED BY THE DESIGN AND NOT BY THIS SLICE: JUMP MULTIPLIES THE RESTORED
    /// HEALTH, and the cooldown carries no affinity term whatever. Jump multiplies the health you come
    /// back with at the SHARED rate (class_ability_affinity_rate_per_trained / _per_spec), read via the
    /// single-argument GetClassAbilityAffinityMultiplier(Skill) overload. The design's original wording
    /// called for a lower, non-standard pair (0.05 / 0.07 against a then-shared 0.12/0.17), carried by
    /// Soul Jump's own now-retired class_ability_affinity_souljump_rate_per_trained / _per_spec keys.
    /// The 2026-10-02 owner ruling retired those keys rather than merely restating the shared values in
    /// them, specifically so a future change to the shared pair reaches Soul Jump with no per-ability
    /// follow-up; see Database/Updates/Shard/2026-10-02-01-Retire-SoulJump-VoidDamage-Affinity-Rate-Keys.sql.
    ///
    /// That direction is also the only one that needs no special pleading. The affinity primitive returns a
    /// factor at or above 1.0, so it can only be applied to a quantity where BIGGER IS BETTER; the restore
    /// fraction is such a quantity and the cooldown is not. Applying the factor to the cooldown would
    /// LENGTHEN it and turn the skill into a penalty, and reaching for a reciprocal to fix that would be
    /// inventing a second affinity shape for one ability. Do not "improve" this back onto the cooldown:
    /// the cooldown is 6/4/2 minutes by rank, full stop.
    /// </summary>
    public class SoulJumpAbility : IClassAbility, IPreWriteDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.SoulJump,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 3,
            Name = "soul_jump",
            DisplayName = "Soul Jump",
            Description = "When a blow would kill you and one of your combat pets is alive, the pet dies in " +
                          "your place and you are restored to 10% of your maximum health. Once every 6/4/2 " +
                          "minutes by rank. With no pet alive, there is no save. Higher Jump multiplies " +
                          "the health you come back with.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },   // premium reprice 2026-09-13: 1/rank -> 2/rank
            Implemented = true,
            AffinitySkill = Skill.Jump,
        };

        /// <summary>
        /// The shortest cooldown any tunable or affinity combination may produce, in seconds. A hardcoded
        /// floor rather than a tunable on purpose: it is a runaway guard, not a balance dial (the dials are
        /// the base and step), and an unregistered tunable key does not fail cleanly - PropertyManager falls
        /// through to a live shard-config read and throws a MySqlException that reads like unrelated
        /// infrastructure trouble.
        /// </summary>
        public const double MinimumCooldownSeconds = 1.0;

        /// <summary>
        /// DeathSave band, and it must stay there: the save decides on the FINAL post-mitigation figure, so
        /// any mitigation running after it would invalidate the decision it just made, and any mitigation
        /// running before it may legitimately remove the death entirely. See the type doc for the worked
        /// example.
        /// </summary>
        public int MitigationOrder => DamageMitigationOrder.DeathSave;

        /// <summary>
        /// FALSE. Nothing here outlives a rank: the cooldown is recomputed from the live rank on every hit
        /// and the ability holds no pool that could be stranded, so the rank filter on the per-player hook
        /// cache is exactly right. A summoner who unlearns mid-cooldown simply has no save on the next hit.
        /// </summary>
        public bool RunsWithoutLearnedRank => false;

        /// <summary>
        /// RANK-GATED - reached only while a rank is held, because <see cref="RunsWithoutLearnedRank"/> is
        /// false and the hook cache filters on rank. Rank sets the cooldown and nothing else; the affinity
        /// scales the restore instead, so the two magnitudes move independently.
        ///
        /// The context's Damage is the damage AS THROWN, never a figure already clamped to the defender's
        /// Health (that clamp is exactly what made Mana Barrier unkillable before 2026-09-08), so comparing
        /// it against Health.Current is a genuine "would this kill me" test rather than a tautology.
        /// </summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            if (defender == null || rank <= 0 || context.Attacker == null || context.Damage == 0)
                return;

            // 6/4/2 minutes by rank, with NO affinity term - see the ruling in the type doc.
            var cooldownSeconds = SoulJumpMath.CooldownSeconds(rank,
                PropertyManager.GetDouble("class_ability_souljump_cooldown_seconds_base").Item,
                PropertyManager.GetDouble("class_ability_souljump_cooldown_seconds_step").Item,
                MinimumCooldownSeconds);

            // Jump MULTIPLIES the health the summoner comes back with, at the SHARED affinity rate
            // (class_ability_affinity_rate_per_trained / _per_spec) via the single-argument overload -
            // Soul Jump's own non-standard rate pair was retired by the 2026-10-02 owner ruling. At zero
            // effective Jump the factor is exactly 1.0 and the restore is bit-identical to the bare
            // tunable.
            var restoreFraction = SoulJumpMath.RestoreFraction(
                PropertyManager.GetDouble("class_ability_souljump_restore_fraction").Item,
                defender.GetClassAbilityAffinityMultiplier(Skill.Jump));

            context.Damage = defender.TrySoulJumpSave(context.Damage, context.Attacker, cooldownSeconds, restoreFraction);
        }

        /// <summary>
        /// Reports the RESTORED HEALTH as a percentage of maximum, which is the one magnitude the affinity
        /// moves. Mirrors <see cref="SoulJumpMath.RestoreFraction"/> exactly (x100 for display) - the two
        /// must stay in step.
        ///
        /// <paramref name="rank"/> is deliberately absent from the arithmetic: rank buys COOLDOWN on this
        /// ability, not restore size, so a rank 1 and a rank 3 summoner come back with the same share of
        /// their health. The cooldown is in the ability's description rather than on this line, because a
        /// readout carries one scalar and the restore is the one the affinity acts on.
        ///
        /// Affinity is a MULTIPLIER on the flat base fraction, reported as the AMOUNT it adds so the three
        /// terms share one unit and sum exactly to Effective. Nothing clamps the FRACTION (the only ceiling
        /// is that the restored amount cannot exceed maximum health, which bounds the points and not this
        /// percentage), so CapNote is always null; the ability carries no equipment mod, so Gear is 0.
        ///
        /// SECONDARY: the COOLDOWN (SoulJumpMath.CooldownSeconds, 6/4/2 minutes by rank) is the second
        /// player-meaningful, rank-scaled magnitude here - rank buys cooldown, Jump buys restore size, and
        /// the two move independently (see the type doc's affinity-direction ruling). No affinity or gear
        /// term exists for the cooldown at all, so Affinity and Gear are always 0 on this segment.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseFraction = PropertyManager.GetDouble("class_ability_souljump_restore_fraction").Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Jump);

            var skill = baseFraction * 100.0;
            var affinity = (SoulJumpMath.RestoreFraction(baseFraction, multiplier) - baseFraction) * 100.0;

            var cooldownSeconds = SoulJumpMath.CooldownSeconds(rank,
                PropertyManager.GetDouble("class_ability_souljump_cooldown_seconds_base").Item,
                PropertyManager.GetDouble("class_ability_souljump_cooldown_seconds_step").Item,
                MinimumCooldownSeconds);

            var cooldownSecondary = new ClassAbilityReadout
            {
                HasValue = true,
                Skill = cooldownSeconds,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = cooldownSeconds,
                Unit = "s",
                Label = "cooldown",
                Per = null,
                CapNote = null,
            };

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = skill + affinity,
                Unit = "%",
                Label = "health back",
                Per = null,
                CapNote = null,
                Secondary = new List<ClassAbilityReadout> { cooldownSecondary },
            };
        }
    }
}
