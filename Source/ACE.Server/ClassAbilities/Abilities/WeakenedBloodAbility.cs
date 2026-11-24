using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T2 game-changer: a damaging Life Magic spell the Blood Mage lands has a 25/35/50% chance
    /// (by rank), rolled ONCE PER TARGET PER CAST, to apply/refresh Weakened Blood on that target for 20s.
    /// While it holds, every Health-damage spell except Drain that strikes the target - from ANY caster, not
    /// only the one who applied it - resolves against a life resistance mod of 2.00/2.50/3.10 by rank: the
    /// top three rungs of the retail vulnerability ladder (Vulnerability V, VI, and the Incantation). The
    /// target also attacks and casts 10/20/30% slower (rank x Creature Enchantment affinity, never below the
    /// class_ability_weakenedblood_slow_floor fraction of its own speed). It is a target-side debuff, not
    /// caster state, precisely so a second life caster in the fellowship reads it. BLOOD-MAGE-DESIGN.md
    /// sec 3 / 4a; Blood Mage buff pass 2026-10-17 (proc, projectile sources, benefit widening, slow).
    ///
    /// WHERE THE MECHANIC LIVES. The mark is TARGET-SIDE transient state (Creature_ClassAbilityDebuffs.cs)
    /// over <see cref="WeakenedBloodMath"/>, rolled and applied by Player.TryApplyWeakenedBlood from three
    /// kinds of landed-spell site:
    ///
    ///  - Harm, in WorldObject_Magic.HandleCastSpell_Boost;
    ///  - every Drain strike, in HandleCastSpell_Transfer, including each of Crimson Harvest's secondaries;
    ///  - every landed Health-damage Life Magic PROJECTILE (Martyr's Hecatomb, Curse of Raven Fury,
    ///    Bloodstone Bolt), through this handler's <see cref="OnSpellHit"/>. The spell-hit dispatch
    ///    (SpellProjectile.OnCollideObject -> Player.ApplySpellHitClassAbilities) runs after the hit's damage
    ///    is applied and fires for every projectile that lands, so Spell AOE children, Cascade children and
    ///    Echo Cast wisp casts all reach the roll - but a creature that already rolled during this cast
    ///    (MagicState.CastNum) draws no further roll, however many of the cast's projectiles strike it
    ///    (WeakenedBloodCastRollLedger). Three Raven Fury projectiles on one large monster are one roll,
    ///    not three.
    ///
    /// DoT ticks never roll: they reach none of those sites.
    ///
    /// IT ALSO LANDS THE RETAIL FESTER ENCHANTMENT (user, live test 2026-08-03: "the 'Fester' spell is what
    /// should be applied, and also multiply outgoing damage. The fester animation should be visible when
    /// this happens"). Fester Other V / VI / the Incantation by rank - the same rungs of the retail ladder
    /// the 2.00/2.50/3.10 vulnerability values themselves come from (see
    /// <see cref="WeakenedBloodMath.FesterSpell"/>). The enchantment is what the player SEES: it plays its
    /// own portal.dat TargetEffect on the creature, broadcast so nearby players see it too. It is NOT where
    /// the vulnerability is stored, and must not become so - the registry's top-layer selection is by
    /// PowerLevel, never by StatModValue, so a Fester from any other source would otherwise displace a
    /// blood mage's mark.
    ///
    /// It is fed into <c>Creature.GetLifeVulnerabilityMod</c> rather than multiplied at the damage sites.
    /// That seam takes MAX against weapon cleave/rend, so "one life-vulnerability axis" stays a mechanical
    /// guarantee instead of a spreadsheet convention - a second multiplier at a damage site would silently
    /// re-open the stacking that method exists to prevent.
    ///
    /// DRAIN APPLIES THE MARK BUT DOES NOT BENEFIT FROM IT (user ruling, 2026-08-02: "Lets exclude drains
    /// from the blood rend/vuln"). Applying and benefiting are separate questions and only the second one is
    /// excluded, so a blood mage's Drain still sets up every Harm and Hecatomb that follows, including a
    /// fellow's. The beneficiary list is <see cref="WeakenedBloodMath.DamageBenefits(SpellType, DamageType)"/>;
    /// the exclusion is enforced in HandleCastSpell_Transfer, which reads
    /// Creature.GetHealthDrainResistanceOnly rather than entering the axis at all.
    ///
    /// The applying strike does not amplify itself - the mark is applied after that strike's damage has
    /// resolved.
    /// </summary>
    public class WeakenedBloodAbility : ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.WeakenedBlood,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 2,
            Name = "weakened_blood",
            DisplayName = "Weakened Blood",
            // Live text. Proc chance, resistance and slow per rank: WeakenedBloodAbility.cs ~129-141 and ~159-161; duration:
            // Player_WeakenedBlood.cs ~90. The resistance ladder (2.00/2.50/3.10) is the f2 formatter.
            DescriptionTemplate = "Your damaging Life Magic (Harm, Drain, and projectiles such as Martyr's Hecatomb, Curse of Raven " +
                          "Fury and Bloodstone Bolt) can make each target it hits fester with Weakened Blood for " +
                          "{v:class_ability_weakenedblood_duration_seconds|num} seconds. While it lasts, every Health-damage spell " +
                          "except Drain that hits the target, from any caster, meets a lower life resistance, and the target " +
                          "attacks and casts slower. The slow can reach {fn:max_slow@max|pct}% of the target's speed. It shares one " +
                          "slot with weapon resistance cleaving, and only the stronger applies.",
            RankTailTemplate = "{ranks:class_ability_weakenedblood_proc_chance_r|pct}% chance, life resistance " +
                          "{ranks:class_ability_weakenedblood_resist_r|f2}, {fn:slow|pct}% slow",
            // The slow is 1 - WeakenedBloodMath.SpeedFactor(values, rank, 1.0): the speed factor Player.ComputeWeakenedBloodSpeedFactor applies
            // (it passes the Creature Enchantment affinity where the text uses the neutral 1.0), so the clamp to slow_floor is exact and a
            // retune past it shows what the mechanic grants. max_slow is the same method at a slow of 1.0, which leaves exactly the floor.
            TextFormulas = new Dictionary<string, TextFormula>
            {
                ["slow"] = new TextFormula(
                    (values, rank) => 1.0 - WeakenedBloodMath.SpeedFactor(values, rank, 1.0),
                    "class_ability_weakenedblood_slow_r1",
                    "class_ability_weakenedblood_slow_r2",
                    "class_ability_weakenedblood_slow_r3",
                    "class_ability_weakenedblood_slow_floor"),
                ["max_slow"] = new TextFormula(
                    (values, rank) => WeakenedBloodMath.MaxSlow(values),
                    "class_ability_weakenedblood_slow_floor"),
            },
            TunableSourceFiles = new[] { "WorldObjects/Player_WeakenedBlood.cs", "ClassAbilities/WeakenedBloodMath.cs" },
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.CreatureEnchantment,
            Ceilings = new[]
            {
                new ClassAbilityCeiling { Id = "slowfloor", Bounds = CeilingBounds.Total, Source = CeilingSource.Constant, ConstantValueFor = (v, rank) => WeakenedBloodMath.MaxSlow(v), ConstantRef = "WeakenedBloodMath.MaxSlow", Label = "max attack and cast slow", Format = "pct" },
            },
        };

        /// <summary>
        /// A landed Health-damage Life Magic projectile rolls for the mark on the creature it struck. The
        /// dispatch already guarantees a damaging landed hit on a non-player Creature by a player caster
        /// (Player.ApplySpellHitClassAbilities refuses a Player target); the Player test is repeated here so
        /// the gate is whole in one place. Fires on Spell AOE / Cascade children and Echo Cast wisp casts by
        /// design - it spawns no projectile, so it carries no cascade risk.
        /// </summary>
        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            if (caster == null || primaryTarget == null || primaryTarget is Player || projectile?.Spell == null)
                return;

            var spell = projectile.Spell;

            if (!WeakenedBloodMath.AppliesFromSpellProjectile(spell.School, spell.MetaSpellType, spell.DamageType))
                return;

            caster.TryApplyWeakenedBlood(primaryTarget);
        }

        /// <summary>
        /// PRIMARY: WeakenedBloodMath.ResistanceMod as a BARE MULTIPLIER (2.00/2.50/3.10 by rank), not a
        /// percentage. Skill is the rank's table value; no affinity rides it; Gear is the HEMORRHAGE mod,
        /// reported in that same bare-multiplier unit because that is the unit it is added in (+0.05 takes a
        /// rank-3 mark to 3.15) - so Skill + Gear reads as the real mark. Effective calls the REAL helper with
        /// the gear term, so the 1.0 floor and the rank-0 early return are honoured by the same code combat
        /// runs.
        ///
        /// SECONDARY 1: the per-target PROC chance (WeakenedBloodMath.ProcChance), in percent. Rank only.
        ///
        /// SECONDARY 2: the attack/cast SLOW, in percent. Skill is the rank's slow; Affinity is the amount
        /// the Creature Enchantment multiplier adds (rankSlow * multiplier - rankSlow); Effective is the slow
        /// combat actually applies, 1 - WeakenedBloodMath.SpeedFactor, so a binding floor shows as CapNote
        /// "speed floor". Mirrors Player.GetWeakenedBloodSpeedFactor.
        ///
        /// Null-Player-safe for the readout unit tests (Player's static initializer cannot run under the test
        /// host): a null player reads no gear and an affinity multiplier of 1.0. Every live caller
        /// (/abilities list) passes session.Player.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var gearMod = player?.GetEquippedModValue(EquipmentModId.Hemorrhage) ?? 0.0;

            var r1 = PropertyManager.GetDouble("class_ability_weakenedblood_resist_r1").Item;
            var r2 = PropertyManager.GetDouble("class_ability_weakenedblood_resist_r2").Item;
            var r3 = PropertyManager.GetDouble("class_ability_weakenedblood_resist_r3").Item;

            var mod = WeakenedBloodMath.ResistanceMod(rank, r1, r2, r3);
            var effective = WeakenedBloodMath.ResistanceMod(rank, r1, r2, r3, gearMod);

            var secondaries = new List<ClassAbilityReadout>();
            var slowReading = 0.0;

            var chance = WeakenedBloodMath.ProcChance(rank,
                PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r1").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r2").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r3").Item);

            if (rank > 0)
            {
                secondaries.Add(new ClassAbilityReadout
                {
                    HasValue = true,
                    Skill = chance * 100.0,
                    Affinity = 0.0,
                    Gear = 0.0,
                    Effective = chance * 100.0,
                    Unit = "%",
                    Label = "proc",
                    Per = null,
                    CapNote = null,
                });

                var rankSlow = WeakenedBloodMath.RankSlow(ClassAbilityText.Live, rank);

                var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.CreatureEnchantment) ?? 1.0;

                var slow = rankSlow * multiplier;

                var factor = WeakenedBloodMath.SpeedFactor(ClassAbilityText.Live, rank, multiplier);

                var effectiveSlow = (1.0 - factor) * 100.0;
                var totalSlow = slow * 100.0;
                slowReading = totalSlow;

                if (rankSlow > 0.0)
                {
                    secondaries.Add(new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = rankSlow * 100.0,
                        Affinity = totalSlow - rankSlow * 100.0,
                        Gear = 0.0,
                        Effective = effectiveSlow,
                        Unit = "%",
                        Label = "atk slow",
                        Per = null,
                        CapNote = effectiveSlow < totalSlow - 1e-9 ? "speed floor" : null,
                    });
                }
            }

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = mod,
                Affinity = 0.0,
                Gear = effective - mod,
                Effective = effective,
                Unit = "x",
                Label = "life vuln",
                Per = null,
                CapNote = null,
                Secondary = secondaries.Count > 0 ? secondaries : null,
                // the raw slow (rank slow x affinity) is what reaches the speed floor, so it is the pre-clamp reading
                Ceilings = new[] { ClassAbilityCeilings.Read(Definition, "slowfloor", slowReading, rank, ClassAbilityText.Live) },
            };
        }
    }
}
