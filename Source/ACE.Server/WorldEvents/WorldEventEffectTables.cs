using System;
using System.Collections.Concurrent;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// An effect (PlayScript) only shows on an object whose effect table (PhysicsEffectTable, DID 22) lists it. Many
    /// objects have no table at all (the envoy knights, the Altar of Asheron) or a short one (Penumbral Horrors have an
    /// empty table; grievvers and rats lack EnchantUpPurple), so an effect the Hold plays on them silently does
    /// nothing. <see cref="EnsureCanPlay"/> gives such an object, before it enters the world, a stock table that lists
    /// the effect. Objects whose own table already lists it are left alone.
    /// </summary>
    public static class WorldEventEffectTables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventEffectTables));

        /// <summary>The stock creature table (96 effects): monsters, decor.</summary>
        public const uint CreatureTable = 0x3400002A;

        /// <summary>The player (human) table (139 effects): human-shaped targets such as the knights.</summary>
        public const uint HumanTable = 0x34000004;

        private static readonly ConcurrentDictionary<(uint, PlayScript), bool> cache = new ConcurrentDictionary<(uint, PlayScript), bool>();

        /// <summary>Test seam: whether a table lists an effect (null when the dats are not loaded).</summary>
        internal static Func<uint, PlayScript, bool?> TableHasLookup = ReadTableHas;

        /// <summary>
        /// Before EnterWorld: when <paramref name="wo"/>'s table cannot play <paramref name="effect"/> (a PlayScript
        /// name; "none" and unknown names do nothing) and <paramref name="fallback"/> can, <paramref name="wo"/> takes
        /// the fallback table. Returns true when it changed the table. Never throws.
        /// </summary>
        public static bool EnsureCanPlay(WorldObject wo, string effect, uint fallback)
        {
            if (wo == null || !WorldEventHoldRules.TryParseBossEffect(effect, out var script) || script == PlayScript.Invalid)
                return false;

            try
            {
                var own = wo.PhysicsTableId;

                if (!NeedsFallback(own, fallback, script, TableHas))
                    return false;

                wo.PhysicsTableId = fallback;

                log.Debug($"[WORLDEVENT] {wo.Name} (wcid {wo.WeenieClassId}) takes effect table 0x{fallback:X8} to show {script} (own 0x{own:X8})");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] effect table check for {wo.Name} ({wo.WeenieClassId}) failed", ex);
                return false;
            }
        }

        /// <summary>
        /// The rule (D6 - pure over <paramref name="has"/>): change the table only when the object's own table does
        /// not list the effect (or it has none) and the fallback does. Unknown answers (dats not loaded) change nothing.
        /// </summary>
        public static bool NeedsFallback(uint ownTable, uint fallback, PlayScript script, Func<uint, PlayScript, bool?> has)
        {
            if (fallback == 0 || ownTable == fallback || has == null)
                return false;

            if (ownTable != 0 && has(ownTable, script) != false)
                return false;

            return has(fallback, script) == true;
        }

        private static bool? TableHas(uint table, PlayScript script)
        {
            if (table == 0)
                return false;

            if (cache.TryGetValue((table, script), out var known))
                return known;

            var answer = TableHasLookup(table, script);

            if (answer != null)
                cache[(table, script)] = answer.Value;

            return answer;
        }

        private static bool? ReadTableHas(uint table, PlayScript script)
        {
            if (DatManager.PortalDat == null)
                return null;

            var t = DatManager.PortalDat.ReadFromDat<PhysicsScriptTable>(table);

            return t?.ScriptTable != null && t.ScriptTable.ContainsKey((uint)script);
        }

        /// <summary>Forgets cached answers (tests swap the lookup).</summary>
        internal static void ClearCache() => cache.Clear();
    }
}
