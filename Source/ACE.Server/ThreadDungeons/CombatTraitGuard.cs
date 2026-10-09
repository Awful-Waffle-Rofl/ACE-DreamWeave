using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The three numeric dials the combat-trait guard reads, decoupled from any plan so World Events and
    /// Threads can each feed it from their own tunables. Values are used as given: the caller sanitizes.
    /// </summary>
    public readonly struct CombatGuardDials
    {
        public double CategoryResistFloor { get; }
        public double CategoryArmorModCeiling { get; }

        /// <summary>Negative disables the defense-skill ceiling (DungeonCombatNormalizer.DefenseSkillCap).</summary>
        public double DefenseSkillCapOffset { get; }

        public CombatGuardDials(double categoryResistFloor, double categoryArmorModCeiling, double defenseSkillCapOffset)
        {
            CategoryResistFloor = categoryResistFloor;
            CategoryArmorModCeiling = categoryArmorModCeiling;
            DefenseSkillCapOffset = defenseSkillCapOffset;
        }
    }

    /// <summary>
    /// The trait strip and category-immunity guard for one creature. Extracted from
    /// DungeonCreatureNormalizer.StripCombatTraits (which now builds the dials from its plan and forwards) so
    /// World Events reuse the identical writes. Every number written is decided by DungeonCombatNormalizer.
    /// See the original remarks, kept below, for what is covered and why.
    /// </summary>
    public static class CombatTraitGuard
    {
        /// <summary>
        /// The trait strip and category-immunity guard for one run creature (dynamic_dungeons_strip_combat_traits).
        /// Returns a short description of every change, for the spawner's log line; empty when nothing changed.
        /// Covers the creature's OWN properties, every item it has equipped (weapons, shields, ammo, armor),
        /// AND every item still sitting in its Inventory - several of these flags are read from the attacker's
        /// weapon as well as the attacker itself (see DungeonCombatNormalizer.BypassBools/BypassFloats), so a
        /// strip that only touched the creature leaves a wielded weapon's copy fully live. See
        /// StripItemCombatTraits for the per-item half and its per-instance-safety citation.
        ///
        /// Inventory is covered too because a Threads creature can equip a NEW item from Inventory mid-fight
        /// with no strip in between: Monster_Missile.SwitchToMeleeAttack (Monster_Missile.cs:222-256) destroys
        /// the ranged weapon/ammo and calls EquipInventoryItems(true) (Monster_Inventory.cs:323-345), which
        /// pulls the melee backup straight out of Inventory via SelectWieldedWeapons
        /// (Monster_Inventory.cs:201-260, sourced from Monster_Inventory.cs:169-188). Monster_Tick.cs:135 does
        /// the same thing when a missile creature runs out of ammo. Both only ever RE-EQUIP an item that
        /// already exists in Inventory - neither creates a fresh instance from the weenie - so stripping
        /// Inventory once here at spawn covers both paths; nothing needs to run again at the switch site.
        /// Safe from a loot standpoint: ThreadDungeonSpawner.StripCorpseTransfer clears DestinationType on
        /// both creature.Inventory.Values and creature.EquippedObjects.Values
        /// (ThreadDungeonSpawner.cs:1079-1080), so nothing sitting in Inventory can reach the corpse either
        /// way, before or after this change.
        ///
        /// Order inside matters only once: the defense-skill cap reads plan.BandStandard, which the builder
        /// computes whenever this guard is on and the plan has a non-boss entry. The BOSS is exempt from the cap -
        /// with normalization on its defense is SET to the band standard afterwards, and with normalization off
        /// the owner's boss is allowed to be tougher than its pack.
        /// </summary>
        public static List<string> Apply(Creature creature, CombatGuardDials dials, DungeonBandStandard standard, bool isBoss)
        {
            // Thin glue: every decision and write lives in ApplyCore, which runs over delegates so a test can
            // drive it with plain dictionaries. Only the Creature plumbing (and the per-item sweep, which needs
            // WorldObjects and is covered by DungeonCreatureNormalizerItemTraitStripTests) stays here.
            var target = new GuardTarget
            {
                GetBool = p => creature.GetProperty(p),
                RemoveBool = p => creature.RemoveProperty(p),
                GetFloat = p => creature.GetProperty(p),
                RemoveFloat = p => creature.RemoveProperty(p),
                SetFloat = (p, v) => creature.SetProperty(p, v),
                GetInt = p => creature.GetProperty(p),
                RemoveInt = p => creature.RemoveProperty(p),

                // Equipped items AND Inventory - same shape NormalizeBossWeapons already uses for the identical
                // "every item this creature might fight with" set. Distinct as a guard against an item present
                // in both collections. Recorded as "<PropertyName>@<item wcid>" so the log line names which item
                // carried the trait, since the same property can legitimately appear once for the creature and
                // again for one of its items.
                SweepItems = () => creature.EquippedObjects.Values.Concat(creature.Inventory.Values).Where(i => i != null).Distinct()
                    .SelectMany(item => DungeonCreatureNormalizer.StripItemCombatTraits(item).Select(change => $"{change}@{item.WeenieClassId}")),

                GetEthereal = () => creature.Ethereal,
                SetEthereal = v => creature.Ethereal = v,
                GetIgnoreCollisions = () => creature.IgnoreCollisions,
                SetIgnoreCollisions = v => creature.IgnoreCollisions = v,

                // add: false - never create a skill the creature was not authored with.
                BaseOf = skill => creature.GetCreatureSkill(skill, false)?.Base,
                SetCeiling = (skill, cap) => (creature.DefenseSkillCeilings ??= new Dictionary<Skill, uint>())[skill] = cap,
            };

            return ApplyCore(target, dials, standard, isBoss);
        }

        /// <summary>
        /// The creature's reads and writes, as delegates, so <see cref="ApplyCore"/> can be exercised without a
        /// live Creature (ACE.Server.Tests cannot construct one). Every member is required except
        /// <see cref="SweepItems"/>.
        /// </summary>
        internal sealed class GuardTarget
        {
            public Func<PropertyBool, bool?> GetBool;
            public Action<PropertyBool> RemoveBool;
            public Func<PropertyFloat, double?> GetFloat;
            public Action<PropertyFloat> RemoveFloat;
            public Action<PropertyFloat, double> SetFloat;
            public Func<PropertyInt, int?> GetInt;
            public Action<PropertyInt> RemoveInt;

            /// <summary>Optional: the already-tagged per-item changes (see Apply). Null means no items.</summary>
            public Func<IEnumerable<string>> SweepItems;

            public Func<bool?> GetEthereal;
            public Action<bool> SetEthereal;
            public Func<bool?> GetIgnoreCollisions;
            public Action<bool> SetIgnoreCollisions;

            /// <summary>The skill's current Base, or null when the creature does not carry the skill.</summary>
            public Func<Skill, uint?> BaseOf;

            /// <summary>Writes a runtime defense ceiling for the skill.</summary>
            public Action<Skill, uint> SetCeiling;
        }

        /// <summary>
        /// The whole guard over <paramref name="t"/>, in the original write order: bypass bools, floats, ints,
        /// the item sweep, pass-through physics, category resist floor, armor-mod ceiling, then (non-boss only)
        /// the defense ceilings. Returns a description of every change.
        /// </summary>
        internal static List<string> ApplyCore(GuardTarget t, CombatGuardDials dials, DungeonBandStandard standard, bool isBoss)
        {
            var changes = new List<string>();

            foreach (var p in DungeonCombatNormalizer.BypassBools)
            {
                if (t.GetBool(p) == true)
                {
                    t.RemoveBool(p);
                    changes.Add(p.ToString());
                }
            }

            foreach (var p in DungeonCombatNormalizer.BypassFloats)
            {
                if (t.GetFloat(p).HasValue)
                {
                    t.RemoveFloat(p);
                    changes.Add(p.ToString());
                }
            }

            foreach (var p in DungeonCombatNormalizer.BypassInts)
            {
                if (t.GetInt(p).HasValue)
                {
                    t.RemoveInt(p);
                    changes.Add(p.ToString());
                }
            }

            if (t.SweepItems != null)
                changes.AddRange(t.SweepItems());

            // Pass-through physics. An EXPLICIT false is required, not a removal: CalculatedPhysicsState only
            // defaults a physics bool from PropertyInt.PhysicsState when the bool is null, then rebuilds the state
            // bits from the bools (WorldObject_Networking.cs:556-564, 621-633), so false wins over the weenie's
            // authored PhysicsState while a removed property would be re-defaulted to true.
            var authoredState = (PhysicsState)(t.GetInt(PropertyInt.PhysicsState) ?? 0);

            var ethereal = t.GetEthereal();

            if (ethereal == true || (ethereal == null && authoredState.HasFlag(PhysicsState.Ethereal)))
            {
                t.SetEthereal(false);
                changes.Add("Ethereal");
            }

            var ignoreCollisions = t.GetIgnoreCollisions();

            if (ignoreCollisions == true || (ignoreCollisions == null && authoredState.HasFlag(PhysicsState.IgnoreCollisions)))
            {
                t.SetIgnoreCollisions(false);
                changes.Add("IgnoreCollisions");
            }

            foreach (var (property, value) in DungeonCombatNormalizer.CategoryResistWrites(p => t.GetFloat(p), dials.CategoryResistFloor))
            {
                t.SetFloat(property, value);
                changes.Add($"{property}->{value:0.###}");
            }

            foreach (var (property, value) in DungeonCombatNormalizer.ArmorModWrites(p => t.GetFloat(p), dials.CategoryArmorModCeiling))
            {
                t.SetFloat(property, value);
                changes.Add($"{property}->{value:0.###}");
            }

            if (!isBoss)
            {
                foreach (var skill in DungeonCombatNormalizer.DefenseSkills)
                {
                    var cap = DungeonCombatNormalizer.DefenseSkillCap(standard, skill, dials.DefenseSkillCapOffset);

                    if (cap == 0)
                        continue;

                    var authoredBase = t.BaseOf(skill);

                    if (authoredBase == null)
                        continue;

                    // A RUNTIME CEILING, never an InitLevel or attribute rewrite: lowering InitLevel alone
                    // cannot enforce a cap when the attribute-formula term alone exceeds it (both are
                    // non-negative and InitLevel floors at 0), and lowering attributes would also nerf the
                    // creature's attack skills and run speed. Read Base BEFORE the ceiling is set, so the
                    // comparison and the log line both see the creature's true authored value.
                    if (authoredBase.Value <= cap)
                        continue;

                    changes.Add($"{skill} {authoredBase.Value}->{cap} (ceiling)");
                    t.SetCeiling(skill, cap);
                }
            }

            return changes;
        }
    }
}
