using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Foolproof salvage bag pool and the two chance rolls that hand one out: the Thread Cache boss bonus
    /// (one roll per player) and the Pinnacle Cache (one roll per cache). The 14 wcids are the 13 retail
    /// Colosseum-vendor foolproofs (36619-36628, 36634-36636; 36629-36633 are augmentations and are NOT in the pool)
    /// plus the fork's Foolproof White Quartz (1002700). Content/dungeons/dynamic/encounters.json's
    /// 14 foolproof_<wcid> prize entries and the Event Crate pool in Content/events/axes/rewards.json carry the same set;
    /// FoolproofSalvageTests pins all three against <see cref="Wcids"/>.
    ///
    /// Both chances are server tunables that ship at the ruled value and clamp to [0, 1]. A read that cannot reach
    /// the config store (PropertyManager reads throw in unit tests with no database) returns the shipped default.
    /// </summary>
    public static class FoolproofSalvage
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string ThreadCacheChanceKey = "thread_cache_foolproof_chance";
        public const string PinnacleCacheChanceKey = "pinnacle_cache_foolproof_chance";

        public const double DefaultThreadCacheChance = 0.02;
        public const double DefaultPinnacleCacheChance = 0.5;

        public static readonly IReadOnlyList<uint> Wcids = new uint[]
        {
            36619, 36620, 36621, 36622, 36623, 36624, 36625, 36626, 36627, 36628, 36634, 36635, 36636,
            1002700,
        };

        /// <summary>Test seam: how a pool wcid becomes an object. Production is the world object factory.</summary>
        internal static Func<uint, WorldObject> Factory = WorldObjectFactory.CreateNewWorldObject;

        public static double ThreadCacheChance => Clamp01(ReadDouble(ThreadCacheChanceKey, DefaultThreadCacheChance));

        public static double PinnacleCacheChance => Clamp01(ReadDouble(PinnacleCacheChanceKey, DefaultPinnacleCacheChance));

        internal static double Clamp01(double v) => double.IsNaN(v) ? 0.0 : Math.Min(1.0, Math.Max(0.0, v));

        /// <summary>One uniformly random pool wcid.</summary>
        public static uint PickRandom() => Wcids[ThreadSafeRandom.Next(0, Wcids.Count - 1)];

        /// <summary>
        /// One independent roll at <paramref name="chance"/>: on a hit, one random pool bag, else null. Chance 1 always
        /// hits and chance 0 never does, without consuming a random draw.
        /// </summary>
        public static WorldObject TryRoll(double chance)
        {
            chance = Clamp01(chance);

            if (chance <= 0.0)
                return null;

            if (chance < 1.0 && ThreadSafeRandom.Next(0.0f, 1.0f) >= chance)
                return null;

            var wcid = PickRandom();
            var bag = Factory(wcid);

            if (bag == null)
                log.Warn($"[FOOLPROOF] could not create foolproof salvage wcid {wcid}; the roll pays nothing");

            return bag;
        }

        /// <summary>The Thread Cache roll: one per call, so one per player. Appends the bag to <paramref name="items"/> on a hit.</summary>
        public static void AddThreadCacheRoll(List<WorldObject> items)
        {
            var bag = TryRoll(ThreadCacheChance);

            if (bag != null)
                items.Add(bag);
        }

        private static double ReadDouble(string key, double fallback)
        {
            try { return PropertyManager.GetDouble(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }
    }
}
