using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Database.Models.World;
using ACE.Server.Factories;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The loot-only rolls a creature death makes, with no corpse involved. Both loot models call these:
    /// GenerateTreasure (the corpse model) and ThreadCacheFiller.MaterializeEntry (the Threads pooled model,
    /// Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 3), so the two cannot drift. Static and free of
    /// PropertyManager reads so the unit tests can drive them.
    /// </summary>
    partial class Creature
    {
        /// <summary>The DeathTreasure roll GenerateTreasure makes. Null profile -> empty list.</summary>
        internal static List<WorldObject> RollDeathTreasureItems(TreasureDeath deathTreasure)
        {
            if (deathTreasure == null)
                return new List<WorldObject>();

            return LootGenerationFactory.CreateRandomLootObjects(deathTreasure);
        }

        /// <summary>
        /// The Threads salvage-affinity roll: one draw per affinity, in list order, and an item created only
        /// when the draw does not exceed that affinity's chance. <paramref name="roll"/> and
        /// <paramref name="create"/> are test seams; production passes neither and gets ThreadSafeRandom and
        /// DungeonSalvageAffinity.TryCreate, exactly the calls GenerateTreasure made inline.
        /// </summary>
        internal static List<WorldObject> RollSalvageAffinityItems(IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> affinities, int tier,
            Func<double> roll = null, Func<uint, int, int, WorldObject> create = null)
        {
            var items = new List<WorldObject>();

            if (affinities == null)
                return items;

            // An explicit null check, not ??=: a lambda has no natural type for ??= to target (CS0019).
            if (roll == null)
                roll = () => ThreadSafeRandom.Next(0.0f, 1.0f);
            create ??= ACE.Server.ThreadDungeons.DungeonSalvageAffinity.TryCreate;

            foreach (var affinity in affinities)
            {
                if (roll() > affinity.Chance)
                    continue;

                var wo = create(affinity.BaseWcid, affinity.MaterialId, tier);

                if (wo == null)
                    continue;

                items.Add(wo);
            }

            return items;
        }

        /// <summary>
        /// The level the rare-eligibility rule reads for a dying creature: the authored gate level when a spawner
        /// raised Level and recorded the original (<see cref="LootGateLevel"/>), else the live Level.
        /// </summary>
        internal static int? LootGateLevelFor(int? gateLevel, int? level) => gateLevel ?? level;

        /// <summary>
        /// CreateCorpse's CanGenerateRare rule, unchanged: a player (non-Olthoi) killer makes a level-100+
        /// creature, or a creature above the killer's level, rare-eligible; a player killer that meets neither
        /// leaves the creature's current value alone; any other killer, or none, makes it ineligible.
        /// <paramref name="killerPlayerLevel"/> is read only in the branch CreateCorpse read it in.
        /// </summary>
        internal static bool ResolveCanGenerateRare(bool current, int? creatureLevel, bool killerPresent, bool killerIsPlayer, bool killerIsOlthoiPlayer, Func<int?> killerPlayerLevel)
        {
            if (!(killerPresent && killerIsPlayer && !killerIsOlthoiPlayer))
                return false;

            if (creatureLevel >= 100)
                return true;

            var killerLevel = killerPlayerLevel?.Invoke();

            if (killerLevel != null && creatureLevel > killerLevel)
                return true;

            return current;
        }

        /// <summary>
        /// True when this death belongs to the Threads pooled-loot model: the same two-key guard as the run kill
        /// hook in Die (the in-memory back-reference AND the persisted run stamp, so a creature from a dead run
        /// is inert), plus the run's write-once switch.
        /// </summary>
        internal static bool IsPooledLootDeath(ACE.Server.ThreadDungeons.ThreadDungeonRun run, int? runIdStamp)
            => run != null && runIdStamp != null && run.PooledLoot;

        /// <summary>
        /// True when this death belongs to an ML digsite encounter, which leaves no corpse at all.
        ///
        /// The SIBLING of <see cref="IsPooledLootDeath"/>, and deliberately shaped the same way: the same
        /// two-key guard (the in-memory back-reference AND the persisted stamp, so a creature from a finished
        /// encounter is inert), OR-ed into the SAME local in Die and consumed at the SAME single corpse call
        /// site. One predicate per system, one call site for both.
        ///
        /// Suppressing the corpse is the point, not a side effect. PropertyBool.NoCorpse would NOT do this
        /// job: CreateCorpse's NoCorpse branch still calls GenerateTreasure and drops the rolled items on the
        /// ground, so an encounter's creatures would carpet the dig site in loot. Skipping the CreateCorpse
        /// call entirely is what actually suppresses it - and it also skips the rare roll, the ML treasure
        /// map drop and the Relaria trophy, all of which live INSIDE CreateCorpse. That is intended: an
        /// encounter's reward is its chest, and a wave of twelve must not also be twelve chances at a
        /// treasure map.
        ///
        /// NAMED "Corpseless", not after the engine property, and that is load-bearing rather than taste:
        /// PooledLootWiringTests reads Creature.Die's body and asserts the string does not appear in it at
        /// all, which is what keeps both systems off a mechanism that still generates treasure. A rename
        /// back would reintroduce the substring at the call site and fail that guard.
        /// </summary>
        internal static bool IsDigsiteCorpselessDeath(ACE.Server.MlDigsite.MlDigsiteEncounter encounter, int? encounterIdStamp)
            => encounter != null && encounterIdStamp != null;

        /// <summary>
        /// True when this death must grant NOTHING to anyone: a puzzle-gate ambush creature (see
        /// <see cref="IsPuzzleAmbush"/>), which a player can summon for free with a wrong lever pull. Read once
        /// by OnDeath (which then skips every on-kill reward: killer hooks, kill tasks, death spawn, XP and
        /// luminance) and once by Die (corpse and treasure, death emote, speed-boss and objective-lock reports,
        /// the CreatureKills counter). One predicate, two consumers, so the two halves cannot disagree.
        /// </summary>
        internal static bool IsRewardlessDeath(bool isPuzzleAmbush) => isPuzzleAmbush;
    }
}
