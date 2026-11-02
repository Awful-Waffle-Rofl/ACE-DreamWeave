using ACE.Server.Entity;
using ACE.Server.Network.Sequence;
using ACE.Server.WorldObjects;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessageCreateObject : GameMessage
    {
        /// <summary>The created object's guid. Bookkeeping only (ClientObjectBook), never serialized again.</summary>
        public uint ObjectId { get; }

        /// <summary>The wielder's guid when the object was wielded at construction, else 0.</summary>
        public uint WielderId { get; }

        /// <summary>
        /// True when the object stood in the world (its own Location, or its wielder's when wielded) at
        /// construction. A contained item has none, and the client object ledger never files it.
        /// </summary>
        public bool HasPlacement { get; }

        public ushort PlacementLandblock { get; }

        public uint PlacementInstance { get; }

        /// <summary>
        /// The ObjectInstance sequence at construction: exactly what a GameMessageDeleteObject built from the
        /// same object at the same moment would carry, so a delete rebuilt from the ledger matches it byte for byte.
        /// </summary>
        public byte[] InstanceSequence { get; }

        /// <summary>The created object's wcid. Diagnostics only (the outbound ring's w=), never serialized again.</summary>
        public uint WeenieClassId { get; }

        /// <summary>The created object's setup (SetupTableId). Diagnostics only (the outbound ring's a=), never serialized again.</summary>
        public uint SetupId { get; }

        public GameMessageCreateObject(WorldObject worldObject, bool adminvision = false, bool adminnodraw = false)
            : base(GameMessageOpcode.ObjectCreate, GameMessageGroup.SmartboxQueue)
        {
            worldObject.SerializeCreateObject(Writer, adminvision, adminnodraw);

            // Captured AFTER serializing, so reading them can never change what was written. None is per-recipient,
            // so one message shared across a broadcast fan-out stays valid for every session.
            ObjectId = worldObject.Guid.Full;
            WielderId = worldObject.Wielder?.Guid.Full ?? 0;
            HasPlacement = ClientObjectPlacement.TryGet(worldObject, out var landblock, out var instance);
            PlacementLandblock = landblock;
            PlacementInstance = instance;
            InstanceSequence = worldObject.Sequences.GetCurrentSequence(SequenceType.ObjectInstance);
            WeenieClassId = worldObject.WeenieClassId;
            SetupId = worldObject.SetupTableId;
        }
    }
}
