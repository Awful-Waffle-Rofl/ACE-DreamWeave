using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T3 game-changer, the Echo Wisp rework (2026-11-22, Docs/ClassAbilities/ARCHMAGE-ECHO-CAST-WISP-DESIGN.md):
    /// each committed harmful cast of any school, and each Heal or Revitalize cast at the caster or a fellow (owner,
    /// 2026-11-23), rolls this ability's chance once and, on a success, leaves an Echo Wisp
    /// beside the caster (<see cref="EchoWisp"/>) that recasts the same spell for its life as the caster's own
    /// first-hand cast. The roll lives in Player.TryTriggerEchoWisp, called from the hand-cast commit
    /// (Player.DoCastSpell_Inner) and the three Spellsword proc handlers; this class owns the definition, the chance
    /// ladder and the readout.
    ///
    /// NOT A HOOK HANDLER. IPassiveStatAbility marks it as read directly off the player at its call sites (the
    /// Overchannel and Volatile Wall precedent), which exempts it from the "Implemented skills hook something" rule.
    /// Until the rework this was an ISpellHitAbility that recast a landed damaging projectile
    /// (OnSpellHit, StampEchoCopy, CanEcho and SpellProjectile.IsEchoCopy). That path is deleted, in the same change
    /// that first arms the wisp latch: while both existed, a legacy echo copy of a wisp bolt would have carried no
    /// wisp mark and escaped the wisp PvP guard (plan ruling on #2297).
    ///
    /// Magic Item Tinkering is MULTIPLICATIVE on this ability's OWN rank bonus (2026-09-12 overhaul), not an additive
    /// rider fed straight into EchoChance. EchoChance's fraction parameter stays additive in its own units, so
    /// <see cref="ChanceFor"/> and GetReadout feed it the AMOUNT the multiplier adds (rankBonus * affinity - rankBonus)
    /// rather than the multiplier itself - mathematically identical to rankBonus * affinity + gear.
    /// </summary>
    public class EchoCastAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.EchoCast,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 3,
            Name = "echocast",
            DisplayName = "Echo Cast",
            // Live text: the ladder is chanceBase + (rank - 1) * chanceStep, exactly EchoChance / ChanceFor /
            // GetReadout below (class_ability_echocast_chance_base / _step). The wisp numbers are the wisp tunables.
            // Heal and Revitalize joined the qualifying casts on 2026-11-23 (owner); the text is orchestrator-written.
            DescriptionTemplate = "Your harmful spells from any school, and your Heal and Revitalize spells, can leave an Echo Wisp beside you. For " +
                          "{v:class_ability_echocast_wisp_duration_seconds|num} seconds it recasts the same spell every " +
                          "{v:class_ability_echocast_wisp_interval_min_seconds|num} to {v:class_ability_echocast_wisp_interval_max_seconds|num} seconds " +
                          "as if you cast it, with your skill, bonuses and procs, at no cost. A debuff moves on to enemies that lack it; " +
                          "a damage spell stays on its target until it dies, then moves on; a heal moves on to the most hurt member of " +
                          "your fellowship once its target is full. Up to " +
                          "{v:class_ability_echocast_wisp_max_active|int} wisps at once. A wisp's cast never echoes.",
            RankTailTemplate = "{ladder:class_ability_echocast_chance_base,class_ability_echocast_chance_step|pct}% chance",
            MaxRank = 3,
            // Archmage pass 2026-10-24 (reprice wave 6): 2/rank -> 1/rank. It was 1/2/3 until the 2026-09-13
            // premium reprice moved it to flat 2/rank.
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.MagicItemTinkering,
            Ceilings = new[]
            {
                new ClassAbilityCeiling { Id = "wisps", Bounds = CeilingBounds.Stacks, Source = CeilingSource.Tunable, Key = "class_ability_echocast_wisp_max_active", Label = "max wisps at once", Format = "int", ZeroIsLimit = true },
                new ClassAbilityCeiling { Id = "gear", Bounds = CeilingBounds.Gear, Source = CeilingSource.GearStackCap, GearMod = EquipmentModId.EchoCast, Label = "max from gear", Format = "pct" },
            },
            // the wisp tunables the description quotes are read by Player.TryTriggerEchoWisp
            TunableSourceFiles = new[] { "WorldObjects/Player_EchoWisp.cs" },
        };

        /// <summary>
        /// The wisp chance at a given rank: base + step*(rank-1) + the Magic Item Tinkering rider + the
        /// equipment-mod term. Pure for testability (mirrors the other reactive-chance handlers). Returns 0
        /// for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the Echo Cast equipment mod, a MACHINERY mod: it amplifies
        /// the ability's own roll and stays behind the same rank check. Defaults to 0, which reproduces the
        /// pre-equipment-mod behavior exactly.
        /// </summary>
        public static float EchoChance(int rank, double chanceBase, double chanceStep, double tinkerFraction, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + Math.Max(0.0, tinkerFraction) + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// <paramref name="player"/>'s live wisp chance at <paramref name="rank"/>: <see cref="EchoChance"/> over the live
        /// tunables, the Magic Item Tinkering multiplier (no-arg overload = the shared 0.12/0.17 rate pair) and the Echo
        /// Cast equipment mod (MACHINERY, outside the affinity multiply). Read by Player.TryTriggerEchoWisp, which only
        /// calls it for a player who holds a rank.
        /// </summary>
        public static float ChanceFor(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_echocast_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_echocast_chance_step").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var affinity = player.GetClassAbilityAffinityMultiplier(Skill.MagicItemTinkering);
            var tinker = rankBonus * affinity - rankBonus;

            return EchoChance(rank, chanceBase, chanceStep, tinker, player.GetEquippedModValue(EquipmentModId.EchoCast));
        }

        /// <summary>
        /// Mirrors the terms fed into EchoChance() by ChanceFor above (x100 for display) - the two must stay in step.
        /// EchoChance takes no affinity-cap parameter, so this readout never clamps the Magic Item
        /// Tinkering rider and CapNote is always null.
        ///
        /// Affinity is a MULTIPLIER on the rank term, but it is reported as the AMOUNT that multiplier adds
        /// (rankBonus * affinity - rankBonus), so the three displayed terms stay in the same unit and still
        /// sum to the effective chance.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_echocast_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_echocast_chance_step").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.MagicItemTinkering);

            var gearChance = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.EchoCast));

            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;
            var gear = gearChance * 100.0;
            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "wisp",
                Per = null,
                CapNote = null,
                Ceilings = new[]
                {
                    ClassAbilityCeilings.Read(Definition, "wisps", player.ActiveEchoWispCount, rank, ClassAbilityText.Live),
                    ClassAbilityCeilings.Read(Definition, "gear", gear, rank, ClassAbilityText.Live),
                },
            };
        }
    }
}
