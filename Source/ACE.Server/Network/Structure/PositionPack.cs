using System;
using System.IO;
using System.Numerics;
using ACE.Entity.Enum;
using ACE.Server.Network.Sequence;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;

namespace ACE.Server.Network.Structure
{
    /// <summary>
    /// A position with sequences
    /// </summary>
    /// <summary>
    /// Who a Player's PositionPack is being built for. Decides which ObjectTeleport sequence it carries; see the
    /// PositionPack constructor for why the two audiences must be told different values.
    /// </summary>
    public enum PositionAudience
    {
        /// <summary>One pack for everyone. Safe for the moving player, may be dropped by observers - avoid for Players.</summary>
        Shared,
        /// <summary>The moving player's own client: the frozen SelfTeleportSequence.</summary>
        Self,
        /// <summary>Everyone but the moving player: the live sequence.</summary>
        Observers,
        /// <summary>Everyone but the moving player, applied at once: the live sequence advanced by one.</summary>
        ObserversTeleport,
    }

    public class PositionPack
    {
        public WorldObject WorldObject;

        public PositionFlags Flags;
        public Origin Origin;       // the location of the object in the world
        public Quaternion Rotation;

        public Vector3 Velocity;
        public Placement? PlacementID;

        // really just a bunch of ushorts for these particular sequences,
        // but for type safety, there appear to be some other sequences that could be either uints or ulongs,
        // so GetCurrentSequence/GetNextSequence returns byte arrays here...

        public byte[] InstanceSequence;
        public byte[] PositionSequence;
        public byte[] TeleportSequence;
        public byte[] ForcePositionSequence;

        public PositionPack() { }

        /// <param name="adminMove">Advance ObjectTeleport for EVERY recipient, the moving player included - a real
        /// teleport, which the moving player's own client acts on as one.</param>
        /// <param name="audience">Who this pack is going to. Only matters for a Player, and only for the teleport
        /// sequence it carries - see PositionAudience. Every Player position broadcast that reaches both the moving
        /// player and observers must be built TWICE (Self + Observers) rather than once (Shared), because the two
        /// audiences must be told different teleport sequences once an observer-only teleport has ever been sent:
        ///
        ///   - the moving player's own client must NEVER see a teleport sequence newer than the one it last
        ///     accepted: on the client's own object that is a full portal transition (character stopped, portal
        ///     view; tested live 2026-08-17). So self-bound packs carry Player.SelfTeleportSequence, frozen.
        ///   - an observer's client DROPS any UpdatePosition whose teleport sequence is OLDER than the one it
        ///     stored (CPhysicsObj::MoveOrTeleport, ported at PhysicsObj.MoveOrTeleport: timestamp older than
        ///     UpdateTimes[Teleport] -> return false). So observer-bound packs carry the live sequence, never the
        ///     frozen self value - or the observer stops applying this player's positions until the next bump.
        ///
        /// Why an observer wants a teleport-sequenced position at all: the same MoveOrTeleport applies a remote
        /// object's UpdatePosition in one of two ways. A NEWER teleport sequence -> SetPosition now, position and
        /// heading, interpolation queue cleared. Otherwise a grounded position -> queued as an InterpolationManager
        /// node, walked toward oldest-first at no more than 2x run speed and ONLY while the client's copy is itself
        /// on the ground; an airborne one is dropped. A jump-running player's copy is airborne almost continuously,
        /// so its nodes pile up (20 deep), it walks backwards to stale ones on every landing, and it drains the
        /// history for seconds after the run stops (measured 2026-08-17: 21 m behind after 8 s, 61 m of history
        /// walked, 3.4 s tail). Sending the grounded positions of a recent jumper with a fresh teleport sequence
        /// (ObserversTeleport) puts the copy exactly on each landing point instead (0.0 m error at every touchdown,
        /// landing slide preserved).</param>
        public PositionPack(WorldObject wo, bool adminMove = false, PositionAudience audience = PositionAudience.Shared)
        {
            WorldObject = wo;

            Origin = new Origin(wo.Location.Cell, wo.Location.Pos);
            Rotation = wo.Location.Rotation;
            Velocity = wo.PhysicsObj != null ? wo.PhysicsObj.Velocity : Vector3.Zero;  // average or instantaneous?
            PlacementID = wo.Placement;

            // note that this constructor increments the wo position sequence
            InstanceSequence = wo.Sequences.GetCurrentSequence(SequenceType.ObjectInstance);
            PositionSequence = wo.Sequences.GetNextSequence(SequenceType.ObjectPosition);

            var player = wo as Player;

            if (adminMove)
            {
                TeleportSequence = wo.Sequences.GetNextSequence(SequenceType.ObjectTeleport);

                // an admin move reaches the moving player too, and its client acts on it as a teleport
                if (player != null)
                    player.SelfTeleportSequence = TeleportSequence;
            }
            else if (player == null)
                TeleportSequence = wo.Sequences.GetCurrentSequence(SequenceType.ObjectTeleport);
            else
            {
                switch (audience)
                {
                    case PositionAudience.ObserversTeleport:
                        // freeze the moving player's own view at the pre-bump value the first time, then advance
                        // for observers only. A later real teleport advances the live sequence again and resets
                        // SelfTeleportSequence to it (GameMessagePlayerTeleport / adminMove), so both views move
                        // past every observer bump together.
                        if (player.SelfTeleportSequence == null)
                            player.SelfTeleportSequence = wo.Sequences.GetCurrentSequence(SequenceType.ObjectTeleport);

                        TeleportSequence = wo.Sequences.GetNextSequence(SequenceType.ObjectTeleport);
                        break;

                    case PositionAudience.Observers:
                        // the live value: equal to what observers last stored, so applied as a normal position
                        TeleportSequence = wo.Sequences.GetCurrentSequence(SequenceType.ObjectTeleport);
                        break;

                    case PositionAudience.Self:
                    case PositionAudience.Shared:
                    default:
                        // the value this player's own client last accepted. Shared falls on this side because a
                        // shared pack that reached self with a newer value would portal the player; the price is
                        // that observers may drop it, so no Player broadcast should be built Shared - see
                        // WorldObject_Networking.SendUpdatePosition, which splits.
                        TeleportSequence = player.SelfTeleportSequence ?? wo.Sequences.GetCurrentSequence(SequenceType.ObjectTeleport);
                        break;
                }
            }

            ForcePositionSequence = wo.Sequences.GetCurrentSequence(SequenceType.ObjectForcePosition);

            Flags = BuildFlags();
        }

        /// <summary>
        /// Returns the PositionFlags based on the current state
        /// </summary>
        public PositionFlags BuildFlags()
        {
            var flags = PositionFlags.None;

            if (Velocity != Vector3.Zero)
                flags |= PositionFlags.HasVelocity;

            if (PlacementID != null)
                flags |= PositionFlags.HasPlacementID;

            if (WorldObject.PhysicsObj != null && (WorldObject.PhysicsObj.TransientState & TransientStateFlags.OnWalkable) != 0)
                flags |= PositionFlags.IsGrounded;

            if (Rotation.W == 0.0f)
                flags |= PositionFlags.OrientationHasNoW;
            if (Rotation.X == 0.0f)
                flags |= PositionFlags.OrientationHasNoX;
            if (Rotation.Y == 0.0f)
                flags |= PositionFlags.OrientationHasNoY;
            if (Rotation.Z == 0.0f)
                flags |= PositionFlags.OrientationHasNoZ;

            return flags;
        }
    }

    public static class PositionPackExtensions
    {
        public static void Write(this BinaryWriter writer, PositionPack position)
        {
            writer.Write((uint)position.Flags);
            writer.Write(position.Origin);

            // choose valid sections by masking against flags
            if ((position.Flags & PositionFlags.OrientationHasNoW) == 0)
                writer.Write(position.Rotation.W);
            if ((position.Flags & PositionFlags.OrientationHasNoX) == 0)
                writer.Write(position.Rotation.X);
            if ((position.Flags & PositionFlags.OrientationHasNoY) == 0)
                writer.Write(position.Rotation.Y);
            if ((position.Flags & PositionFlags.OrientationHasNoZ) == 0)
                writer.Write(position.Rotation.Z);

            if ((position.Flags & PositionFlags.HasVelocity) != 0)
                writer.Write(position.Velocity);

            if ((position.Flags & PositionFlags.HasPlacementID) != 0)
                writer.Write((uint)position.PlacementID);

            writer.Write(position.InstanceSequence);
            writer.Write(position.PositionSequence);
            writer.Write(position.TeleportSequence);
            writer.Write(position.ForcePositionSequence);
        }
    }
}
