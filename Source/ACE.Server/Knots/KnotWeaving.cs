using System;
using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Knots
{
    /// <summary>
    /// What is left of the Fiber-to-Knot plumbing (Docs/Knots/KNOTS-DESIGN.md section 4): the level math shared by
    /// the Pattern fill, the Knotting Pouch container rules, and the Knot placement.
    ///
    /// A Pattern is the only way to make a Knot (owner ruling 2026-10-29): the weave prompts (the Pouch's and
    /// Thread-Guide Maelis's) are retired, see <see cref="PatternFill"/> and <see cref="KnotSeam"/>. The Knotting
    /// Pouch has no source in content and only still holds Fibers.
    /// </summary>
    public static class KnotWeaving
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // ---- pure ----

        /// <summary>Knot level = floor of the mean Fiber level. An empty set is level 0.</summary>
        public static int KnotLevelFor(IEnumerable<int> fiberLevels)
        {
            long sum = 0;
            var count = 0;

            foreach (var level in fiberLevels)
            {
                sum += level;
                count++;
            }

            return KnotLevelFromSum(sum, count);
        }

        /// <summary>Floor of <paramref name="sum"/> over <paramref name="count"/>; level 0 when nothing has been taken.</summary>
        public static int KnotLevelFromSum(long sum, int count)
        {
            if (count <= 0)
                return 0;

            return (int)Math.Floor(sum / (double)count);
        }

        /// <summary>
        /// An explicit player move of an item of <paramref name="itemKind"/> into a container of
        /// <paramref name="containerKind"/>: a Pouch takes Fibers only (so never another Pouch); any other
        /// container is not this rule's business.
        /// </summary>
        public static bool AcceptsExplicitMove(int containerKind, int itemKind)
            => !KnotItems.IsPouch(containerKind) || KnotItems.IsFiber(itemKind);

        /// <summary>A stack split into a container of <paramref name="containerKind"/>: never into a Pouch, whatever the item.</summary>
        public static bool AcceptsSplitInto(int containerKind) => !KnotItems.IsPouch(containerKind);

        /// <summary>A stack merge onto an item held in a container of <paramref name="containerKind"/>: never inside a Pouch.</summary>
        public static bool AcceptsMergeInto(int containerKind) => !KnotItems.IsPouch(containerKind);

        // ---- glue ----

        /// <summary>
        /// Creates an unbound Knot of <paramref name="knotLevel"/> and places it in the player's pack. False (and
        /// nothing placed, nothing left behind) when the weenie does not create or the pack has no room; the caller
        /// sends the refusal and must not consume anything. The Pattern completion (KnotSeam.TryCompletePattern)
        /// binds the Knot right after.
        /// </summary>
        internal static bool TryPlaceKnot(Player player, int knotLevel, out WorldObject knot)
        {
            knot = WorldObjectFactory.CreateNewWorldObject(KnotItems.KnotWcid);

            if (knot == null)
            {
                log.Error($"[KNOTS] {player.Name} (0x{player.Guid.Full:X8}): the Knot weenie {KnotItems.KnotWcid} could not be created; nothing woven");
                return false;
            }

            // Unbound: no PinnacleKey and no entries. Those are set when the Pattern binds it or on its first use.
            knot.SetProperty(PropertyInt.KnotLevel, knotLevel);
            knot.Name = KnotItems.KnotName(knotLevel);

            // Placement goes through the ordinary spill path, which never picks the Pouch (Container.TryAddToInventory).
            if (!player.TryCreateInInventoryWithNetworking(knot))
            {
                knot.Destroy();
                knot = null;
                return false;
            }

            return true;
        }
    }
}
