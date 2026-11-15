using System;

using ACE.Server.Frays;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>The Frays on any world object: transient marks, the campfire back-reference, and per-viewer creates.</summary>
    partial class WorldObject
    {
        /// <summary>Runtime only: a Frays campfire or fire monster. Never saved (WorldObject_Database).</summary>
        public bool FraysTransient;

        /// <summary>Runtime only: on a Hollow Campfire object, the fire it lights.</summary>
        public FraysFire FraysFire;

        /// <summary>Runtime only: on a fire monster's corpse, the one player who may loot it (0 = normal rules).</summary>
        public uint FraysLootOwner;

        /// <summary>Runtime only: when set, only this player is ever sent the object (a zone's wisps, for its owner).</summary>
        public uint FraysOnlyFor;

        /// <summary>
        /// Runtime only: every create message describes the object as ethereal, and its real (solid) state follows right behind
        /// (Player.TrackObject). A Frays house door sent solid in a create overlaps its building's shell, so the player's game
        /// shoves it off its doorway; created ethereal, then made solid, it stays put, even
        /// after a relog. The server's own PhysicsObj.State is never changed.
        /// </summary>
        public bool FraysCreateEthereal;

        /// <summary>
        /// The player a create message is being serialized for, on this thread, or null. Set only by
        /// <see cref="CreateObjectFor"/>; read by a per-viewer description (a Frays monster's Attackable bit).
        /// </summary>
        [ThreadStatic]
        protected static Player SerializingFor;

        /// <summary>
        /// True when this object's create message differs per viewer, so it must never be serialized once and shared
        /// (NotifyPlayers) and each viewer gets <see cref="CreateObjectFor"/>.
        /// </summary>
        public virtual bool HasPerViewerDescription => false;

        /// <summary>The Attackable bit the client is sent, before the World Events Hold rule. A fire's monster overrides it (per viewer).</summary>
        public virtual bool FraysAttackableBit => Attackable;

        /// <summary>A create message for <paramref name="viewer"/> alone.</summary>
        public static GameMessageCreateObject CreateObjectFor(Player viewer, WorldObject wo, bool adminvision, bool adminnodraw)
        {
            var previous = SerializingFor;
            SerializingFor = viewer;

            try
            {
                return new GameMessageCreateObject(wo, adminvision, adminnodraw);
            }
            finally
            {
                SerializingFor = previous;
            }
        }
    }

    partial class Player
    {
        /// <summary>Runtime only: the Frays' camp modifiers while this player camps at a fire (null otherwise).</summary>
        public FraysCampMods FraysMods;

        /// <summary>
        /// The Frays: a dungeon's fellowship is run by its campfires and has no real leader, so nobody may disband,
        /// lock, rename, open, recruit into, dismiss from or hand on the lead of it. True (with a line to the player)
        /// when this player's fellowship is one and the action is refused.
        /// </summary>
        public bool FraysFellowshipBlocked(string action)
        {
            if (Fellowship == null || !FraysManager.IsDungeonFellowship(Fellowship))
                return false;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(
                $"You cannot {action} the Fray's fellowship. It belongs to the campfires: light a fire to join it, leave it to give up your fire.",
                ACE.Entity.Enum.ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>
        /// Adds <paramref name="amount"/> of a stackable (the Frays' Embers) to this player's pack: into stacks they
        /// already carry first, then new stacks. Returns how many were placed (less when the pack is full).
        /// </summary>
        public int FraysAddStackable(uint wcid, int amount, bool burdenCheck = true)
        {
            if (amount <= 0)
                return 0;

            var placed = 0;

            foreach (var stack in GetInventoryItemsOfWCID(wcid))
            {
                if (placed >= amount)
                    break;

                var room = (stack.MaxStackSize ?? 1) - (stack.StackSize ?? 1);

                if (room <= 0)
                    continue;

                var add = System.Math.Min(room, amount - placed);

                if (FindObject(stack.Guid.Full, SearchLocations.MyInventory, out var container, out var root, out _) == null)
                    continue;

                if (!AdjustStack(stack, add, container, root))
                    continue;

                Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));
                placed += add;
            }

            while (placed < amount)
            {
                var item = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(wcid);

                if (item == null)
                    break;

                var size = System.Math.Min(amount - placed, item.MaxStackSize ?? 1);
                item.SetStackSize(size);
                size = item.StackSize ?? 1;

                if (!TryCreateInInventoryWithNetworking(item, out _, burdenCheck))
                {
                    item.Destroy();
                    break;
                }

                placed += size;
            }

            return placed;
        }
    }
}
