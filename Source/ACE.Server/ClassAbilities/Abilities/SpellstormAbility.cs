using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T3 game-changer: a landed LIGHT WEAPON hit has a 5/10/15% chance (by rank) to cast a Ring
    /// war spell matching the damage type the swing actually dealt - nine projectiles at spread angle 360,
    /// radiating outward, so a single target takes roughly one and a surrounding pack takes one each.
    ///
    /// A CHANCE LADDER, NO LEVEL LADDER. Spellstorm fires only the tier-I ring, because it is a SHAPE change
    /// rather than a level ladder (SPELLSWORD-DESIGN.md section 3). The tier-I ring block is stat-identical
    /// across all seven damage types (42/42, avg 63, 9 projectiles), so the entry means exactly the same
    /// thing whichever element the Spellsword is swinging - no normalization, no fallback, no per-type
    /// balance work. Tier II (6189-6196) sits unused in reserve. It shipped single-rank at a flat 14%; the
    /// 2026-10-24 Spellsword pass gave it three ranks at 1/1/1 whose rank buys CHANCE (5/10/15%, the
    /// class_ability_spellstorm_chance_r1/_r2/_r3 tunables).
    ///
    /// There is still no affinity rider. The design's section 3 table lists no affinity for this entry,
    /// and section 7 lists no per-skill tunable for it, unlike Spellblade (Item Enchantment), Runeblade
    /// (Magic Item Tinkering) and Sundermark (Life Magic).
    ///
    /// Caster/launcher split, damage-type resolution and the deliberately-unflagged projectiles are all
    /// identical to SpellbladeAbility; see that file's doc comment for the reasoning behind each.
    /// </summary>
    public class SpellstormAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Spellstorm,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 3,
            Name = "spellstorm",
            DisplayName = "Spellstorm",
            Description = "Your landed light weapon hits have a 5/10/15% chance (by rank) to unleash a ring of " +
                          "nine war projectiles matching the damage type of the swing, radiating outward in " +
                          "every direction - roughly one for each enemy around you. A Nether weapon unleashes a " +
                          "ring of void projectiles instead. Its resist chance and skill damage bonus use your " +
                          "Item Enchantment. A swing at full power unleashes the greater ring instead, and " +
                          "every proc hits harder the more power you put into the swing.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // Spellsword pass 2026-10-24: one rank at 3 -> three at 1/1/1 (ClassAbilityRepricing wave 4, PromoteToPaid)
            Implemented = true,
        };

        /// <summary>
        /// The base proc chance at a given rank: the r1/r2/r3 tunables (0.05 / 0.10 / 0.15). Ranks above 3
        /// clamp to the rank-3 value; 0 for rank &lt;= 0. Pure for testability.
        /// </summary>
        public static double BaseChance(int rank, double chanceR1, double chanceR2, double chanceR3)
        {
            if (rank <= 0)
                return 0.0;

            if (rank == 1)
                return Math.Max(0.0, chanceR1);

            if (rank == 2)
                return Math.Max(0.0, chanceR2);

            return Math.Max(0.0, chanceR3);
        }

        /// <summary>The live base chance at <paramref name="rank"/>, from the three per-rank tunables.</summary>
        private static double LiveBaseChance(int rank) => BaseChance(rank,
            PropertyManager.GetDouble("class_ability_spellstorm_chance_r1").Item,
            PropertyManager.GetDouble("class_ability_spellstorm_chance_r2").Item,
            PropertyManager.GetDouble("class_ability_spellstorm_chance_r3").Item);

        /// <summary>
        /// Casts a spell from inside the outgoing-damage dispatch, so it runs in the LAST band, after
        /// Spellweave's weapon half. See OutgoingDamageDispatchOrder.SpellCastingProc.
        /// </summary>
        public int DispatchOrder => OutgoingDamageDispatchOrder.SpellCastingProc;

        /// <summary>
        /// The proc chance: the rank's base (<see cref="BaseChance"/>, passed in as
        /// <paramref name="chanceBase"/>) plus the current Spellsurge stack bonus plus the Spellstorm
        /// equipment mod. Pure for testability. Returns 0 for rank &lt;= 0. No affinity rider - see the
        /// class doc comment for why.
        ///
        /// <paramref name="gearModChance"/> is the SPELLSTORM equipment mod (EquipmentModId.Spellstorm), a
        /// MACHINERY mod: it is simply a further summand here, because this entry carries no affinity rider
        /// and therefore has no cap for the gear term to have to sit outside of. Last and defaulted to 0,
        /// so an unmodded build is bit-identical.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double spellsurgeBonus = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            return (float)Math.Max(0.0, chanceBase + Math.Max(0.0, spellsurgeBonus) + Math.Max(0.0, gearModChance));
        }

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent == null)
                return;

            // Light weapons only - one of the three war procs. Gated on the weapon that landed this hit,
            // not the current attack skill (SpellswordSpellTables.IsLightWeaponHit - offhand dual-wield).
            if (!SpellswordSpellTables.IsLightWeaponHit(attacker, damageEvent.Weapon))
                return;

            // The DEALT damage type only. A combined Pierce|Slash weapon procs whichever type the swing
            // actually landed; DamageEvent.DamageType already carries that resolution.
            var damageType = damageEvent.DamageType;

            // Spellstorm equipment mod (MACHINERY): +pp on this ability's own proc roll. Ownership is proven
            // by the dispatch itself - only LEARNED IOutgoingDamageAbility handlers are in the hook cache.
            var chance = Chance(rank,
                LiveBaseChance(rank),
                attacker.GetSpellsurgeProcChanceBonus(),
                attacker.GetEquippedModValue(EquipmentModId.Spellstorm));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Exotic damage types (Health / Stamina / Mana / Base) are not in the table and must proc nothing,
            // silently. Nether is in it (Clouded Soul, owner ruling 2026-10-27). The Ring has no level ladder,
            // so no skill picks a level here; resist and skill damage bonus read Item Enchantment
            // (SpellProjectile.CasterSkillOverride).
            if (!SpellswordSpellTables.RingByType.TryGetValue(damageType, out var spellId))
                return;

            // Full-power upgrade (2026-11-01): a swing at a full bar throws Ring II instead of the tier-I ring.
            // A Riposte counter reads as power 0 and never upgrades.
            var swingPower = SpellswordSpellTables.SwingPowerLevel(damageEvent);
            var fullPower = SpellswordSpellTables.UseFullPowerShape(
                PropertyManager.GetBool("class_ability_spellsword_full_power_shape").Item, swingPower);

            var ringTable = fullPower ? SpellswordSpellTables.RingIIByType : SpellswordSpellTables.RingByType;

            if (fullPower && SpellswordSpellTables.RingIIByType.TryGetValue(damageType, out var ringII))
                spellId = ringII;

            var spell = new Spell(spellId);

            // Nether parity: 1.0 for every elemental type; for Nether, the Fire ring's average over Clouded
            // Soul's (Ring II: Cassius' Ring of Fire II over Clouded Soul II), times the tunable. See
            // SpellbladeAbility.
            var procDamageScale = SpellswordSpellTables.ProcDamageScaleFor(
                SpellswordSpellTables.RingParityReferenceFor(damageType, ringTable),
                spell,
                PropertyManager.GetDouble("class_ability_spellsword_nether_parity").Item);

            // Power-scaled proc damage: x(1 + a x PowerLevel), composed with the parity scale. Exactly 1.0 on
            // an empty bar.
            procDamageScale *= SpellswordSpellTables.PowerProcScale(swingPower,
                PropertyManager.GetDouble("class_ability_spellsword_power_proc_scale").Item);

            // The struck creature is passed as the spell's target, which is what the engine aims from; a
            // 360-spread ring then originates at the CASTER and radiates outward regardless (design section
            // 2c), so "centred on the target" and "centred on you" are the same call here - the spread angle
            // decides the shape, not this argument.
            //
            // Player as caster, wielded weapon as launcher, fromProc to suppress item procs. Projectiles are
            // deliberately left unflagged (design Q10) so Echo Cast and Elemental Rend can fire off them;
            // no recursion is possible because the trigger is a landed melee hit, never a spell hit.
            // Wrapped in CastClassAbilityProc so the launched projectiles carry
            // SpellProjectile.IsClassAbilityProc - the signal Cascade gates on. See SpellbladeAbility for
            // why fromProc alone is not that signal, and note Cascade fails CLOSED without this wrapper.
            attacker.CastClassAbilityProc(() =>
                attacker.CreateSpellProjectiles(spell, target, damageEvent.Weapon, false, fromProc: true, suppressPetAssist: true),
                procDamageScale,
                damageEvent.OriginalDamageType);   // Volatile Elements: the re-typed hit's original type, for the proc's rend max

            // A landed war proc feeds Spellsurge (design section 5e).
            attacker.AddSpellsurgeStack();
        }

        /// <summary>
        /// Mirrors the rank's base chance fed into Chance() above, plus the SPELLSTORM mod. No affinity rider
        /// by design - see the class doc comment - so no cap can bite and CapNote is always null.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skillChance = LiveBaseChance(rank);

            // Rank-gated like skillChance above - a machinery mod reports nothing without its ability.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Spellstorm) ?? 0.0);

            var skill = skillChance * 100.0;
            var gear = gearChance * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = skill + gear,
                Unit = "%",
                Label = "proc",
                Per = null,
                CapNote = null,
            };
        }
    }
}
