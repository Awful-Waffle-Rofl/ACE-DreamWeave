using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T2 game-changer (the slot Streak-to-Arc vacated on retirement): when the player lands the
    /// killing blow on a hostile creature that is still carrying one of the player's own nether
    /// damage-over-time enchantments, that DoT BLOOMS - it is re-applied to the nearest 1/2/3 other hostile
    /// creatures (by rank) within a radius, carrying its remaining duration and its already-computed
    /// per-tick damage. Void's identity is the DoT, so the reward for finishing a corrupted target is that
    /// the corruption spreads.
    ///
    /// No affinity rider by design: this is the class's T2 GC and its power knob is the jump count.
    ///
    /// CASCADE GUARD. A bloomed DoT is stamped with <see cref="BloomMarker"/> in the enchantment row's
    /// otherwise inert DegradeLimit field, and a kill on a target whose nether DoT carries that stamp does
    /// not bloom again. This mirrors SpellProjectile.IsClassAbilitySpawned (a flag set on the spawned object
    /// that the dispatch checks before spawning more), moved onto the only carrier an enchantment has - a
    /// field of its own registry row. DegradeLimit was chosen because it is read nowhere on the server
    /// (only echoed into the client enchantment structure, and blooms only ever land on monsters), and it
    /// already carries a sentinel precedent in this codebase: EnchantmentManager.StartCooldown writes
    /// DegradeLimit = -666 to tag cooldown rows.
    /// </summary>
    public class NetherBloomAbility : ICreatureDeathAbility, IAbilityReadout
    {
        /// <summary>
        /// Sentinel written into a bloomed enchantment's DegradeLimit so it can never bloom again. Any value
        /// far outside the real DegradeLimit range works; this one is deliberately distinct from the -666
        /// cooldown sentinel.
        /// </summary>
        public const float BloomMarker = -6660.0f;

        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.NetherBloom,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 2,
            Name = "netherbloom",
            DisplayName = "Nether Bloom",
            Description = "When you land the killing blow on a creature carrying one of your void " +
                          "damage-over-time spells, that spell spreads to the 1/2/3 nearest other enemies " +
                          "by rank, keeping its remaining duration. A spread spell cannot spread again.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        public void OnCreatureKilled(Player killer, int rank, Creature victim)
        {
            var jumps = JumpCount(rank, PropertyManager.GetDouble("class_ability_netherbloom_jumps_per_rank").Item);
            if (jumps <= 0)
                return;

            // Nether Bloom equipment mod (MACHINERY, not standalone - this handler only fires at all when
            // the base ability is owned, so there is no rank-0 gate to worry about here). The resolved gear
            // value IS the roll probability - not a percent bonus - so it composes as one extra Bernoulli
            // trial per bloom event rather than folding additively into JumpCount.
            var gearMod = killer.GetEquippedModValue(EquipmentModId.NetherBloom);
            jumps = ApplyGearBloomChance(jumps, gearMod, ThreadSafeRandom.Next(0.0f, 1.0f));

            var source = FindBloomableNetherDot(killer, victim);
            if (source == null)
                return;

            var remaining = RetainedDuration(source.Duration + source.StartTime,
                PropertyManager.GetDouble("class_ability_netherbloom_duration_retained").Item);

            if (remaining <= 0.0)
                return;

            var spell = new Spell(source.SpellId);
            if (spell.NotFound)
                return;

            var radius = (float)PropertyManager.GetDouble("class_ability_netherbloom_radius").Item;
            if (radius <= 0.0f)
                return;

            var spread = 0;

            foreach (var target in FindNearestTargets(killer, victim, radius, jumps))
            {
                if (ApplyBloom(killer, target, spell, source, remaining))
                    spread++;
            }

            if (spread > 0)
            {
                killer.Session?.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{spell.Name} blooms from {victim.Name} to {spread} nearby {(spread == 1 ? "enemy" : "enemies")}!", ChatMessageType.Magic));
            }
        }

        /// <summary>
        /// How many other creatures the DoT jumps to at a given rank: rank * jumpsPerRank, floored, never
        /// negative (1/2/3 at the default rate of 1.0). Pure.
        /// </summary>
        public static int JumpCount(int rank, double jumpsPerRank)
        {
            if (rank <= 0 || jumpsPerRank <= 0.0)
                return 0;

            return (int)Math.Floor(rank * jumpsPerRank);
        }

        /// <summary>
        /// The Nether Bloom equipment mod's extra-jump roll: <paramref name="gearModFraction"/> is a
        /// PROBABILITY (not a percent damage bonus) of one additional jump, compared against a caller-supplied
        /// uniform roll in [0, 1). Pure for testability - the live call site rolls via ThreadSafeRandom.
        /// gearModFraction &lt;= 0 (unequipped, or the mod's linked ability - this same one - is unlearned so
        /// GetEquippedModValue never resolves it) always returns <paramref name="jumps"/> unchanged.
        /// </summary>
        public static int ApplyGearBloomChance(int jumps, double gearModFraction, double roll)
        {
            if (gearModFraction > 0.0 && roll < gearModFraction)
                return jumps + 1;

            return jumps;
        }

        /// <summary>
        /// The duration a bloomed copy carries: the source's remaining duration times the retained fraction,
        /// floored at 0 (an already-expired source blooms nothing). Pure.
        /// </summary>
        public static double RetainedDuration(double remainingSeconds, double retainedFraction)
        {
            if (remainingSeconds <= 0.0 || retainedFraction <= 0.0)
                return 0.0;

            return remainingSeconds * retainedFraction;
        }

        /// <summary>
        /// The strongest nether DoT on the victim that was cast by this killer and is not itself a bloom;
        /// null if there is none. "Strongest" so a stacked victim spreads the DoT that mattered.
        /// </summary>
        private static PropertiesEnchantmentRegistry FindBloomableNetherDot(Player killer, Creature victim)
        {
            if (victim?.Biota == null)
                return null;

            var enchantments = victim.Biota.PropertiesEnchantmentRegistry.GetEnchantmentsTopLayer(victim.BiotaDatabaseLock, SpellSet.SetSpells);
            if (enchantments == null)
                return null;

            PropertiesEnchantmentRegistry best = null;

            foreach (var enchantment in enchantments)
            {
                if (enchantment.StatModKey != (uint)PropertyInt.NetherOverTime)
                    continue;

                if (enchantment.CasterObjectId != killer.Guid.Full)
                    continue;

                // cascade guard - a bloomed DoT never blooms again
                if (IsBloomed(enchantment))
                    continue;

                if (best == null || enchantment.StatModValue > best.StatModValue)
                    best = enchantment;
            }

            return best;
        }

        public static bool IsBloomed(PropertiesEnchantmentRegistry enchantment) =>
            enchantment != null && Math.Abs(enchantment.DegradeLimit - BloomMarker) < 0.001f;

        /// <summary>
        /// The nearest <paramref name="jumps"/> valid bloom targets around the dead victim - hostile,
        /// living, non-player, non-pet creatures the killer may legally damage, within
        /// <paramref name="radius"/> of where the victim fell. Same visible-objects source and monster
        /// filters Spell AOE uses.
        /// </summary>
        private static List<Creature> FindNearestTargets(Player killer, Creature victim, float radius, int jumps)
        {
            var results = new List<Creature>();

            if (killer.PhysicsObj == null || victim.Location == null)
                return results;

            var candidates = new List<(Creature creature, double distance)>();

            foreach (var obj in killer.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null))
            {
                if (obj.WeenieObj.WorldObject is not Creature creature)
                    continue;

                if (creature == victim || creature == killer)
                    continue;

                if (creature.IsDead || creature.Teleporting || creature.Location == null)
                    continue;

                // PvP exclusion, and never the player's own summons
                if (creature is Player || creature is Pet)
                    continue;

                if (!killer.CanDamage(creature) || killer.CheckPKStatusVsTarget(creature, null) != null)
                    continue;

                // radius measured from where the victim fell, not from the killer
                var distance = victim.Location.DistanceTo(creature.Location);
                if (distance > radius)
                    continue;

                candidates.Add((creature, distance));
            }

            foreach (var candidate in candidates.OrderBy(c => c.distance).Take(jumps))
                results.Add(candidate.creature);

            return results;
        }

        /// <summary>
        /// Re-applies the source DoT to one secondary target, carrying the source's per-tick damage and the
        /// retained remaining duration, and stamps the new row so it cannot bloom again. Returns TRUE if a
        /// bloom landed.
        /// </summary>
        private static bool ApplyBloom(Player killer, Creature target, Spell spell, PropertiesEnchantmentRegistry source, double duration)
        {
            var result = target.EnchantmentManager.Add(spell, killer, null);

            var entry = result?.Enchantment;
            if (entry == null)
                return false;

            // carry the source's already-calculated per-tick damage and its remaining life, then stamp it.
            // NOTE: when Add() refreshed an enchantment the target already had (same caster, same category),
            // this stamps that pre-existing row too - deliberately conservative, since the alternative is a
            // bloom that can be laundered back into a bloomable DoT by re-casting on the secondary target.
            entry.StatModValue = source.StatModValue;
            entry.StartTime = 0;
            entry.Duration = duration;
            entry.DegradeLimit = BloomMarker;

            target.ChangesDetected = true;

            return true;
        }

        /// <summary>
        /// The ability's own effect is a JUMP COUNT (an integer, 1/2/3 by rank) and the Nether Bloom
        /// equipment mod is a separate extra-jump PROBABILITY (see ApplyGearBloomChance) - two different
        /// units that cannot be summed into one meaningful scalar the way Skill/Affinity/Gear normally
        /// compose. Rather than misleadingly force the jump count into a "%" or "pp" slot (or vice versa),
        /// this reports no value; the ability name and rank alone (jumps = rank, per the description) are
        /// the honest summary.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank) => new ClassAbilityReadout { HasValue = false };
    }
}
