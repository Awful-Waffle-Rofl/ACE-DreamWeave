using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T3: a landed PROC has a 5/10/15% chance (by rank, multiplied by Item Enchantment) to
    /// immediately fire the same spell again at a SECOND nearby enemy. SPELLSWORD-DESIGN.md sec 5f, and the generation guard is design question Q13.
    ///
    /// THE GUARD IS THE POINT OF THIS ABILITY, so read this before changing anything here.
    ///
    /// Q10 ruled that proc-spawned projectiles are NOT flagged IsClassAbilitySpawned, so that Archmage's
    /// Echo Cast and Elemental Rend fire off a Spellsword proc ("I want this class to be a firework").
    /// Cascade then triggers on a landed proc - so an unflagged cascade child is itself a landed proc, and
    /// could cascade again, and again. At 25% per link that converges (expected 1.33 extra hits, terminates
    /// with probability 1), so it is not a hang, but it is unbounded in the tail and no class ability in the
    /// codebase chains today: both existing precedents (SpellAoeAbility, EchoCastAbility) refuse outright on
    /// IsClassAbilitySpawned (and Echo Cast also on IsEchoCopy, its own recasts - which since 2026-09-21 are
    /// full casts that Spell AOE radiates off, once).
    ///
    /// Q13, ACCEPTED: an explicit GENERATION COUNTER on SpellProjectile rather than reuse of the boolean.
    /// SpellProjectile.ClassAbilityGeneration starts at 0, a cascade child is 1, and this handler refuses at
    /// generation &gt;= class_ability_cascade_max_generation (default 1) - exactly one extra hop. Cascade
    /// children stay UNFLAGGED on IsClassAbilitySpawned, which is the whole reason the counter was chosen
    /// over the boolean: a cascade child then behaves exactly like the proc that spawned it (Echo Cast and
    /// Elemental Rend fire off both), instead of being a visibly different kind of projectile. The two
    /// fields are independent - see the doc comments on SpellProjectile.
    ///
    /// TELLING A PROC FROM AN ORDINARY CAST: SpellProjectile.IsClassAbilityProc. The dispatch site fires
    /// ISpellHitAbility for EVERY landed war-spell hit, including a plain hand-cast Flame Bolt, so a signal
    /// is required or Cascade would chain off ordinary war casts and become a second, broader ability.
    /// Three candidates existed and the choice is worth recording:
    ///
    ///  - SpellProjectile.FromProc - already set on every proc cast, needs no new state, but it is ALSO set
    ///    on ordinary item cast-on-strike procs, on cloak procs and on Echo Cast recasts. It answers "did
    ///    something proc this" rather than "is this one of MY class's procs", which is a different question;
    ///  - the generation counter - cannot serve here, because generation 0 is exactly what a primary proc
    ///    and an ordinary cast have in common;
    ///  - a dedicated flag, chosen. IsClassAbilityProc is stamped in WorldObject_Magic.LaunchSpellProjectiles
    ///    from a player-side "this cast is a class-ability proc" latch, so it is exact and costs one bool.
    ///
    /// The latch is armed by Player.CastClassAbilityProc, which the three Spellsword war proc handlers wrap
    /// their cast in. IF THAT WRAPPER IS NOT USED, CASCADE SIMPLY NEVER FIRES - it fails closed, not open.
    ///
    /// RANKS. It was cut to a single rank by ruling (user, 2026-08-04) because ranks 2 and 3 then bought
    /// nothing. The 2026-10-24 Spellsword pass gave rank something to buy - CHANCE, 5/10/15% from the
    /// class_ability_cascade_chance_r1/_r2/_r3 tunables - and three ranks at 1/1/1. The same pass added an
    /// ITEM ENCHANTMENT affinity that MULTIPLIES the rank's chance, the added amount clamped by
    /// class_ability_affinity_chance_cap exactly as Sundermark clamps its Life Magic rider, with the gear term
    /// outside the clamp. Neither change touches the generation guard: rank, affinity and gear all move the
    /// CHANCE of one hop, never the number of hops.
    /// </summary>
    public class CascadeAbility : ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Cascade,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 3,
            Name = "cascade",
            DisplayName = "Cascade",
            Description = "A landed proc has a 5/10/15% chance (by rank) to immediately fire again at a second " +
                          "nearby enemy. The second casting cannot cascade further. Higher Item Enchantment " +
                          "increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // Spellsword pass 2026-10-24: one rank at 3 -> three at 1/1/1 (ClassAbilityRepricing wave 4, PromoteToPaid)
            Implemented = true,
            AffinitySkill = Skill.ItemEnchantment,
        };

        /// <summary>
        /// The rank's base chain chance: the r1/r2/r3 tunables (0.05 / 0.10 / 0.15). Ranks above 3 clamp to
        /// the rank-3 value; 0 for rank &lt;= 0. Pure for testability.
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
            PropertyManager.GetDouble("class_ability_cascade_chance_r1").Item,
            PropertyManager.GetDouble("class_ability_cascade_chance_r2").Item,
            PropertyManager.GetDouble("class_ability_cascade_chance_r3").Item);

        /// <summary>
        /// Radius in metres, measured from the PRIMARY target, within which the second enemy is looked for -
        /// used only when class_ability_cascade_radius is not registered. 8 metres matches Crimson Harvest's
        /// registered default, which is the other "spread to a creature near the one I just struck" selection
        /// in the catalog; Spell AOE's 5 metres is a whole-pack radiate, which is a different shape.
        /// </summary>
        private const double DefaultRadiusMeters = 8.0;

        /// <summary>
        /// The chance a landed proc cascades: the rank's base <paramref name="chance"/> (from
        /// <see cref="BaseChance"/>) MULTIPLIED by the Item Enchantment affinity, plus the Cascade equipment
        /// mod. Pure for testability. Returns 0 for rank &lt;= 0.
        ///
        /// <paramref name="affinityMultiplier"/> is what <c>Player.GetClassAbilityAffinityMultiplier</c>
        /// returns - a FACTOR &gt;= 1.0. What is capped is the AMOUNT the multiply ADDS
        /// (chance * multiplier - chance), bounded by <paramref name="affinityCap"/>
        /// (class_ability_affinity_chance_cap; 0 means uncapped) - the same shape as SundermarkAbility.Chance.
        ///
        /// <paramref name="gearModChance"/> is the CASCADE equipment mod (EquipmentModId.Cascade), a
        /// MACHINERY mod added to the chain CHANCE on the ability's own axis, OUTSIDE the affinity clamp. It
        /// deliberately does NOT touch class_ability_cascade_max_generation: that integer is the entire safety
        /// argument against unbounded chaining, and DESIGN.md 2.3 records it as unmoddable on purpose.
        ///
        /// The two affinity parameters are APPENDED after the gear term (the gear parameter shipped first),
        /// and default to the neutral 1.0 / uncapped, so an existing (rank, chance, gear) call is
        /// bit-identical to what it was.
        /// </summary>
        public static float CascadeChance(int rank, double chance, double gearModChance = 0.0, double affinityMultiplier = 1.0, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            var added = chance * affinityMultiplier - chance;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            added = Math.Max(0.0, added);

            return (float)Math.Max(0.0, chance + added + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// TRUE when this projectile may cascade: it is a class-ability proc, and it has not already used up
        /// its allowed hops. Pure, so the guard that is the entire point of Q13 is unit-testable without a
        /// live projectile in flight.
        /// </summary>
        public static bool CanCascade(bool isClassAbilityProc, int generation, long maxGeneration)
        {
            if (!isClassAbilityProc)
                return false;

            return generation < maxGeneration;
        }

        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            // Only off one of this class's own procs, and only within the generation budget. Both halves are
            // in CanCascade so the rule reads in one place; see the class doc comment for why the dedicated
            // flag was chosen over FromProc.
            if (!CanCascade(projectile.IsClassAbilityProc, projectile.ClassAbilityGeneration,
                    PropertyManager.GetLong("class_ability_cascade_max_generation").Item))
                return;

            if (projectile.Spell == null || caster.PhysicsObj == null)
                return;

            // The struck target may have just died from this hit; its WorldObject lingers (removal is
            // deferred) but guard against a torn-down Location before measuring distances from it. Same
            // guard SpellAoeAbility and TryCrimsonHarvest carry, for the same reason.
            if (primaryTarget.Location == null)
                return;

            // Cascade equipment mod (MACHINERY): +pp on this ability's own chain roll. Ownership is proven
            // by the dispatch itself - ApplySpellHitClassAbilities only walks LEARNED ISpellHitAbility
            // handlers - so the plain read is correct here.
            var chance = CascadeChance(rank, LiveBaseChance(rank),
                caster.GetEquippedModValue(EquipmentModId.Cascade),
                caster.GetClassAbilityAffinityMultiplier(Skill.ItemEnchantment),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            var secondTarget = SelectSecondTarget(caster, primaryTarget);

            if (secondTarget == null)
                return;

            // Re-cast the same spell at the SECOND target - this is EchoCastAbility's recast, aimed
            // elsewhere. The launcher is carried through so the cascade keeps the wielded sword's rend,
            // slayer and crit frequency, which is what makes it the same proc rather than a weaker copy
            // (Q11, sec 5a). fromProc suppresses item procs on the child, exactly as the primary proc does.
            var children = caster.CreateSpellProjectiles(projectile.Spell, secondTarget, projectile.ProjectileLauncher,
                projectile.IsWeaponSpell, fromProc: true, suppressPetAssist: true);

            foreach (var child in children)
            {
                // Still a proc for every other purpose - it is the same spell from the same source, and
                // Q10's firework ruling applies to it too. IsClassAbilitySpawned is deliberately NOT set:
                // the generation counter below is the only thing bounding the chain, which is precisely the
                // trade Q13 accepted so a cascade child behaves like the proc that spawned it.
                child.IsClassAbilityProc = true;
                child.ClassAbilityGeneration = projectile.ClassAbilityGeneration + 1;

                // The child is the parent proc's cast carried to a second target, so it takes the parent's
                // captured per-cast stamps rather than whatever the live player fields hold by now (which
                // CreateSpellProjectiles copied, and which may belong to a later cast). Without this,
                // LifeProjectileDamage defaults to 0 and a life-projectile spell (Martyr's Hecatomb) lands
                // for 0 damage on every cascade hit.
                SpellProjectile.CopyPerCastStamps(child, projectile);

                // Volatile Wall counts the wall's OWN projectiles only (owner ruling 2026-10-24): a cascade
                // re-fire neither ramps nor advances any counter, including the fresh one CreateSpellProjectiles
                // would have made for a wall spell.
                child.VolatileWallCounter = null;
            }
        }

        /// <summary>
        /// Picks the eligible enemy NEAREST to the primary target, within the cascade radius. Nearest-first
        /// rather than random so the cascade reads as the spell jumping to the next creature in the pack
        /// rather than teleporting across the room, and so the behaviour is reproducible in testing.
        ///
        /// The candidate scan is the shared one - PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere - with the
        /// same hostile-creature filters SpellAoeAbility, TauntAbility, Creature.GetMultiShotTargets and
        /// WorldObject_Magic.TryCrimsonHarvest all use. There is no higher-level "nearby enemies" helper in
        /// the codebase; that call IS the helper, and duplicating its filter list is the existing convention.
        /// Excludes the primary target (the whole point is a SECOND enemy), the caster, players, the caster's
        /// own combat pets, and anything already dead or teleporting.
        /// </summary>
        private static Creature SelectSecondTarget(Player caster, Creature primaryTarget)
        {
            var radius = PropertyManager.GetDouble("class_ability_cascade_radius", DefaultRadiusMeters).Item;

            if (radius <= 0.0)
                return null;

            Creature nearest = null;
            var nearestDistance = double.MaxValue;

            var visible = caster.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null);

            foreach (var obj in visible)
            {
                if (obj.WeenieObj.WorldObject is not Creature creature)
                    continue;

                if (creature == primaryTarget || creature == caster)
                    continue;

                if (creature.IsDead || creature.Teleporting || creature.Location == null)
                    continue;

                // PvE exclusion: the cascade never jumps to a player or to the caster's own pet
                if (creature is Player || creature is CombatPet)
                    continue;

                if (Player.IsObjectiveProbeRefused(caster, creature))
                    continue;

                if (!caster.CanDamage(creature) || caster.CheckPKStatusVsTarget(creature, null) != null)
                    continue;

                // radius measured from the struck target, not the caster - the cascade is the spell
                // spreading from the creature it just hit
                var distance = primaryTarget.Location.DistanceTo(creature.Location);

                if (distance > radius || distance >= nearestDistance)
                    continue;

                nearest = creature;
                nearestDistance = distance;
            }

            return nearest;
        }

        /// <summary>
        /// Mirrors CascadeChance() above: Skill is the rank's base chance, Affinity the CAPPED amount the
        /// Item Enchantment multiplier adds (chance * multiplier - chance, then clamped by
        /// class_ability_affinity_chance_cap), Gear the CASCADE mod added outside the clamp - so the three
        /// terms sum to Effective. CapNote reports "affinity cap" exactly when the clamp is biting.
        ///
        /// The null-conditionals are for the readout unit tests, which call this with a null Player
        /// because Player's static initializer cannot run under the test host; the affinity fallback is the
        /// NEUTRAL 1.0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skillChance = LiveBaseChance(rank);
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var affinityMultiplier = player?.GetClassAbilityAffinityMultiplier(Skill.ItemEnchantment) ?? 1.0;
            var rawAdded = skillChance * affinityMultiplier - skillChance;

            var clampedAdded = affinityCap > 0.0 ? Math.Min(rawAdded, affinityCap) : rawAdded;
            clampedAdded = Math.Max(0.0, clampedAdded);
            var capBit = affinityCap > 0.0 && rawAdded > affinityCap;

            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Cascade) ?? 0.0);

            var skill = skillChance * 100.0;
            var affinity = clampedAdded * 100.0;
            var gear = gearChance * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = skill + affinity + gear,
                Unit = "%",
                Label = "chain",
                Per = null,
                CapNote = capBit ? "affinity cap" : null,
            };
        }
    }
}
