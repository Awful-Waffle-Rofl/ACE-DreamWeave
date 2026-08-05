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
    /// Spellsword T3 game-changer: a landed LIGHT WEAPON hit has a flat 10% chance to cast a Ring war spell
    /// matching the damage type the swing actually dealt - nine projectiles at spread angle 360, radiating
    /// outward, so a single target takes roughly one and a surrounding pack takes one each.
    ///
    /// SINGLE RANK, AND NO LEVEL LADDER. This is the one war proc that does not scale with anything: the
    /// user's ruling is that Spellstorm fires only the tier-I ring, because it is a SHAPE change rather
    /// than a ladder (SPELLSWORD-DESIGN.md section 3, "Single rank - it is a shape change, not a ladder").
    /// The tier-I ring block is stat-identical across all seven damage types (42/42, avg 63, 9 projectiles),
    /// so the entry means exactly the same thing whichever element the Spellsword is swinging - no
    /// normalization, no fallback, no per-type balance work. Tier II (6189-6196) sits unused in reserve if
    /// the entry ever needs a rank ladder.
    ///
    /// Consequently there is no affinity rider either: with neither a chance ladder nor a level ladder to
    /// ride, an affinity would just be a flat percentage on a single-rank entry. The design's section 3
    /// table lists no affinity for this entry, and section 7 lists no per-skill tunable for it, unlike
    /// Spellblade (Item Enchantment), Runeblade (Magic Item Tinkering) and Sundermark (Life Magic).
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
            Description = "Your landed light weapon hits have a 10% chance to unleash a ring of nine war " +
                          "projectiles matching the damage type of the swing, radiating outward in every " +
                          "direction - roughly one for each enemy around you.",
            MaxRank = 1,
            CostPerRank = new[] { 5 },
            Implemented = true,
        };

        /// <summary>
        /// The proc chance: a flat base plus the current Spellsurge stack bonus plus the Spellstorm
        /// equipment mod. Pure for testability. Returns 0 for rank &lt;= 0. No rank term (the entry has one
        /// rank) and no affinity rider - see the class doc comment for why.
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
                PropertyManager.GetDouble("class_ability_spellstorm_chance").Item,
                attacker.GetSpellsurgeProcChanceBonus(),
                attacker.GetEquippedModValue(EquipmentModId.Spellstorm));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Exotic damage types (Health / Stamina / Mana / Nether / Base) are not in the table and must
            // proc nothing, silently.
            if (!SpellswordSpellTables.RingByType.TryGetValue(damageType, out var spellId))
                return;

            var spell = new Spell(spellId);

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
                attacker.CreateSpellProjectiles(spell, target, damageEvent.Weapon, false, fromProc: true));

            // A landed war proc feeds Spellsurge (design section 5e).
            attacker.AddSpellsurgeStack();
        }

        /// <summary>
        /// Mirrors the flat base chance fed into Chance() above, plus the SPELLSTORM mod. No affinity rider
        /// by design - see the class doc comment - so no cap can bite and CapNote is always null.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_spellstorm_chance").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase;

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
                Unit = "pp",
                Label = "proc",
                Per = null,
                CapNote = null,
            };
        }
    }
}
