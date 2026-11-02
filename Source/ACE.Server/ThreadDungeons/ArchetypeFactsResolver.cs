using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The outside lookups <see cref="ArchetypeFacts.FromWeenie(ACE.Entity.Models.Weenie, ArchetypeFactsResolver)"/>
    /// needs: create-list item weenies, the spell table (damaging and harmful school per spell, and a projectile's mean
    /// base damage) and the wielded-treasure launcher probability. One object rather than a parameter per lookup, so the
    /// runtime, archetype-census and uplift-matchup build the facts identically (<see cref="Live"/>), and a test can hand
    /// in stubs. Every member may be null; a null lookup reads as "no data" for the fact it feeds.
    /// </summary>
    public sealed class ArchetypeFactsResolver
    {
        public Func<uint, ACE.Entity.Models.Weenie> WeenieOf { get; }

        /// <summary>Spell id to the school skill it is a harmful DAMAGING spell of (ArchetypeFacts.DamagingSpellSkill), or null.</summary>
        public Func<int, Skill?> DamagingSpellSkillOf { get; }

        /// <summary>Spell id to the school skill it is HARMFUL in (ArchetypeFacts.HarmfulSpellSkill), or null. Null delegate: harmful = damaging.</summary>
        public Func<int, Skill?> HarmfulSpellSkillOf { get; }

        public Func<uint, double> WieldedMissileProbabilityOf { get; }

        /// <summary>Spell id to its mean base damage when it is a single-projectile war or void projectile, else 0.</summary>
        public Func<int, double> ProjectileMeanOf { get; }

        /// <summary>
        /// The AoE/drain strip the facts are read AFTER (orchestrator ruling 2026-10-23: classify on the post-strip book).
        /// Null or disabled: the authored book.
        /// </summary>
        public SpellStripConfig Strip { get; }

        /// <summary>Spell id to its NumProjectiles, for the strip's projectile rule. Null: every spell reads 0 (only banned ids strip).</summary>
        public Func<int, int> ProjectileCountOf { get; }

        public ArchetypeFactsResolver(Func<uint, ACE.Entity.Models.Weenie> weenieOf, Func<int, Skill?> damagingSpellSkillOf,
            Func<int, Skill?> harmfulSpellSkillOf, Func<uint, double> wieldedMissileProbabilityOf, Func<int, double> projectileMeanOf,
            SpellStripConfig strip = null, Func<int, int> projectileCountOf = null)
        {
            WeenieOf = weenieOf;
            DamagingSpellSkillOf = damagingSpellSkillOf;
            HarmfulSpellSkillOf = harmfulSpellSkillOf;
            WieldedMissileProbabilityOf = wieldedMissileProbabilityOf;
            ProjectileMeanOf = projectileMeanOf;
            Strip = strip;
            ProjectileCountOf = projectileCountOf;
        }

        /// <summary>
        /// The spell ids of <paramref name="weenie"/>'s authored book the strip would remove (pure over this resolver):
        /// <see cref="SpellStripConfig.WouldRemove"/> with the weenie's own "has a damaging body part" guard input.
        /// </summary>
        public ISet<int> StrippedSpellsOf(ACE.Entity.Models.Weenie weenie)
        {
            if (Strip == null || !Strip.Enabled || weenie?.PropertiesSpellBook == null || weenie.PropertiesSpellBook.Count == 0)
                return new HashSet<int>();

            var damagingBodyPart = weenie.PropertiesBodyPart != null && weenie.PropertiesBodyPart.Values.Any(p => p != null && p.DVal > 0);

            return Strip.WouldRemove(weenie.PropertiesSpellBook.Keys, ProjectileCountOf, damagingBodyPart);
        }

        /// <summary>
        /// The mean base damage of a war or void projectile spell (pure): (min + max) / 2 for a single-projectile
        /// Projectile-family spell of the War or Void school, else 0. Life projectiles take a different damage formula
        /// and multi-projectile spells are what the Threads AoE strip removes, so neither counts (the same limits
        /// CreatureAttackerSpecs.RequireWarOrVoidProjectile sets for the combat simulator).
        /// </summary>
        public static double ProjectileMean(MagicSchool school, SpellType metaType, int numProjectiles, int minDamage, int maxDamage)
        {
            if (school != MagicSchool.WarMagic && school != MagicSchool.VoidMagic)
                return 0.0;

            if (numProjectiles != 1 || metaType == SpellType.LifeProjectile || metaType == SpellType.EnchantmentProjectile)
                return 0.0;

            if (maxDamage <= 0)
                return 0.0;

            return (Math.Max(0, minDamage) + maxDamage) / 2.0;
        }

        private static ArchetypeFactsResolver live;
        private static readonly object liveLock = new object();

        /// <summary>
        /// The live resolver: weenies from DatabaseManager.World's cache, spells from the portal dat's spell table plus
        /// the world spell table (ACE.Server.Entity.Spell), and wielded-treasure tables from the world cache reduced by
        /// <see cref="WieldedTreasureMath"/>. Needs DatManager initialized and the world database reachable. One shared
        /// instance; its memos are concurrent, and a content reload goes through ThreadPlanCache.Invalidate, which drops
        /// every profile built from it (the memos here hold only treasure-table and spell reductions, which a reload of
        /// weenies does not move; <see cref="ResetLive"/> drops them too).
        /// </summary>
        public static ArchetypeFactsResolver Live => LiveFor(SpellStripConfig.Default);

        /// <summary>
        /// The live resolver reading the facts after <paramref name="strip"/> (the runtime passes the live dials, an
        /// offline tool its own). One shared instance per strip setting: a different setting rebuilds it.
        /// </summary>
        public static ArchetypeFactsResolver LiveFor(SpellStripConfig strip)
        {
            strip ??= SpellStripConfig.None;

            lock (liveLock)
            {
                if (live == null || live.Strip?.Key != strip.Key)
                    live = BuildLive(strip);

                return live;
            }
        }

        /// <summary>Drops the live resolver's memos (called with ThreadPlanCache.Invalidate).</summary>
        public static void ResetLive()
        {
            lock (liveLock)
            {
                live = null;
                liveMeans = new ConcurrentDictionary<int, double>();
            }
        }

        private static ConcurrentDictionary<int, double> liveMeans = new ConcurrentDictionary<int, double>();

        /// <summary>
        /// A spell's <see cref="ProjectileMean"/> off the dat and world spell tables (ACE.Server.Entity.Spell), memoised.
        /// Independent of any strip setting, so the spawner's spell-output floor reads it without a resolver.
        /// </summary>
        public static double LiveProjectileMeanOf(int id)
            => liveMeans.GetOrAdd(id, sid =>
            {
                if (sid <= 0)
                    return 0.0;

                var spell = new Spell(sid);

                return spell.NotFound ? 0.0 : ProjectileMean(spell.School, spell.MetaSpellType, spell.NumProjectiles, spell.MinDamage, spell.MaxDamage);
            });

        /// <summary>A spell's NumProjectiles off the world spell cache, the spawner's strip's own lookup.</summary>
        internal static int WorldProjectileCountOf(int spellId)
            => spellId <= 0 ? 0 : DatabaseManager.World.GetCachedSpell((uint)spellId)?.NumProjectiles ?? 0;

        private static ArchetypeFactsResolver BuildLive(SpellStripConfig strip)
        {
            var spellTable = DatManager.PortalDat.SpellTable.Spells;

            Skill? DamagingSkillOf(int id)
                => spellTable.TryGetValue((uint)id, out var sb)
                    ? ArchetypeFacts.DamagingSpellSkill(sb.School, sb.MetaSpellType, ((SpellFlags)sb.Bitfield).HasFlag(SpellFlags.Beneficial))
                    : null;

            Skill? HarmfulSkillOf(int id)
                => spellTable.TryGetValue((uint)id, out var sb)
                    ? ArchetypeFacts.HarmfulSpellSkill(sb.School, ((SpellFlags)sb.Bitfield).HasFlag(SpellFlags.Beneficial))
                    : null;


            ACE.Entity.Models.Weenie WeenieOf(uint wcid) => DatabaseManager.World.GetCachedWeenie(wcid);

            var launcherOf = new ConcurrentDictionary<uint, bool>();

            bool IsLauncher(uint wcid)
                => launcherOf.GetOrAdd(wcid, w =>
                {
                    var weenie = WeenieOf(w);
                    return weenie != null && ArchetypeFacts.IsLauncherType(weenie.WeenieType);
                });

            var treasureMemo = new ConcurrentDictionary<uint, double>();

            double MissileProbabilityOf(uint treasureType)
                => treasureMemo.GetOrAdd(treasureType, t =>
                {
                    var rows = DatabaseManager.World.GetCachedWieldedTreasure(t) ?? new List<ACE.Database.Models.World.TreasureWielded>();
                    var reduced = rows.Select(r => new WieldedRow(r.WeenieClassId, r.Probability, r.SetStart, r.HasSubSet, r.ContinuesPreviousSet)).ToList();

                    return WieldedTreasureMath.ProbabilityOfAny(reduced, IsLauncher);
                });

            var projectiles = new ConcurrentDictionary<int, int>();

            return new ArchetypeFactsResolver(WeenieOf, DamagingSkillOf, HarmfulSkillOf, MissileProbabilityOf, LiveProjectileMeanOf,
                strip, id => projectiles.GetOrAdd(id, WorldProjectileCountOf));
        }
    }
}
