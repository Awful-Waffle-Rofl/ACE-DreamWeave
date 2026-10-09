using System;
using System.Collections.Concurrent;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// One fresh, never-owned instance of each weenie, kept for as long as that weenie's world-database
    /// row is unchanged, so the vault's comparisons can diff against a template without building and
    /// tearing one down per call.
    ///
    /// THE GUID IS THE WHOLE TRICK, and it is copied rather than invented: Hook.cs:187-193 already
    /// caches per-wcid reference objects built with
    /// <see cref="WorldObjectFactory.CreateWorldObject(ACE.Entity.Models.Weenie, ObjectGuid)"/> passing
    /// <see cref="ObjectGuid.Invalid"/>. An object built that way never takes a dynamic guid out of the
    /// pool, so it never has to be destroyed, so caching it leaks nothing. Building references with
    /// <c>CreateNewWorldObject</c> instead would burn one dynamic guid per cached wcid and would have to
    /// be released.
    ///
    /// THESE OBJECTS ARE SHARED AND MUST BE TREATED AS IMMUTABLE. Every consumer here only READS the
    /// reference's biota. A caller that mutated one would poison every later comparison for that wcid,
    /// and the consumers of <see cref="VaultCollapse.TryDescribeClass"/> destroy biotas, so a poisoned
    /// reference is an item-loss bug rather than a cosmetic one. If you need a mutable copy, build your
    /// own with <see cref="WorldObjectFactory.CreateNewWorldObject(uint)"/>.
    ///
    /// The cache is keyed by wcid and is therefore stale the moment a weenie row changes underneath it.
    /// <see cref="Invalidate()"/> is wired beside every existing weenie-cache clear in
    /// DeveloperContentCommands, in the same shape as the ThreadPlanCache hook that already sits there.
    /// </summary>
    public static class VaultReferenceCache
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ConcurrentDictionary<uint, WorldObject> references = new ConcurrentDictionary<uint, WorldObject>();

        /// <summary>
        /// The shared reference object for this wcid, or NULL when one could not be built - an unknown
        /// wcid, a weenie the factory declines, or a world-database read that failed. Null is never
        /// cached, so a transient failure heals on the next call.
        ///
        /// Total by construction. Every caller treats null as "cannot prove anything about this item",
        /// which is always the conservative answer.
        /// </summary>
        public static WorldObject Get(uint wcid)
        {
            if (references.TryGetValue(wcid, out var cached))
                return cached;

            try
            {
                // Reaches WorldDatabaseWithEntityCache.GetWeenie for anything not already cached, which
                // runs an unguarded EF query. Inside the try for the same reason IsPristine's own
                // construction is: a world database that drops mid-session must not throw out of a
                // deposit handler.
                var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

                if (weenie == null)
                    return null;

                var reference = WorldObjectFactory.CreateWorldObject(weenie, ObjectGuid.Invalid);

                if (reference == null)
                    return null;

                // BEFORE publishing it, never after: another thread that read a half-prepared reference
                // out of the dictionary would diff against the authored icon and get a different answer
                // from the same inputs.
                //
                // A reference that could not be RENDERED is handed back but never published. It is
                // still usable - comparisons against it simply report the icon difference and refuse,
                // which is the safe direction - but caching it would pin one transient dat failure as
                // "this wcid is unclassifiable" for the whole process lifetime, with nothing to clear
                // it but a manual cache clear. Skipping the publish keeps the failure per-call, and it
                // does not weaken the ordering above: the only thing that changes is whether the store
                // happens at all, never when it happens relative to the normalization.
                if (!Normalize(reference))
                    return reference;

                references[wcid] = reference;

                return reference;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] VaultReferenceCache could not build a reference for wcid {wcid}; callers will treat the item as unclassifiable.", ex);
                return null;
            }
        }

        /// <summary>
        /// Brings the reference into the state a real item is ALREADY in after its first render, for
        /// exactly the reason VaultCollapse.NormalizeDatDerivedState documents: CalculateObjDesc writes
        /// the dat's sub-palette icon over the authored one, a stored item has been through that and a
        /// fresh one has not, and without this nothing carrying a ClothingBase ever compares equal.
        ///
        /// Its own try/catch, and a failure is not fatal: the reference simply keeps the authored icon
        /// and every comparison against it reports an icon difference, which is the refusing direction.
        ///
        /// Returns FALSE when it could not render, which is <see cref="Get"/>'s signal not to cache the
        /// reference. See that call site for why.
        /// </summary>
        private static bool Normalize(WorldObject reference)
        {
            try
            {
                reference.CalculateObjDesc();
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"[VAULT] VaultReferenceCache could not normalize the dat-derived state of its reference for wcid {reference.WeenieClassId}; items of that wcid will simply not be judged equivalent, and the reference is not cached so the next call retries.", ex);
                return false;
            }
        }

        /// <summary>Drops every cached reference. Called beside every weenie-cache clear.</summary>
        public static void Invalidate()
        {
            references.Clear();
        }

        /// <summary>Drops one wcid's cached reference. Called beside every single-weenie cache clear.</summary>
        public static void Invalidate(uint wcid)
        {
            references.TryRemove(wcid, out _);
        }

        /// <summary>Test seam. How many references are currently held.</summary>
        internal static int Count => references.Count;
    }
}
