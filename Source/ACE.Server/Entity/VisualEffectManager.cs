using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Replays an object's persistent visual effect at a client whenever that client builds the object.
    ///
    /// The effect is a raw PhysicsScript DataID sent via opcode 0xF754 (PlayScriptId), stored on the
    /// object as PropertyDataId.VisualEffectScript. Three properties of the client make this work, all
    /// established live on 2026-08-07:
    ///
    ///  - Effects are ADDITIVE. ParticleManager.CreateParticleEmitter treats emitter handle 0 as
    ///    "allocate a fresh id" rather than "slot 0", and 9965 of the ~10743 CreateParticle hooks in
    ///    the dat use handle 0. So a sent script layers over whatever the object's Setup already bakes
    ///    in instead of replacing it. This is why the design keeps each weapon's own elemental effect
    ///    and adds a marker on top.
    ///  - The emitter survives anything short of an object rebuild - a portal does not clear it.
    ///  - A rebuild DOES clear it, and nothing re-sends it. That is the entire reason this class
    ///    exists: it re-emits behind every GameMessageCreateObject.
    ///
    /// A handle-0 emitter cannot be stopped by id (ParticleManager.StopParticleEmitter and
    /// DestroyParticleEmitter both return false for 0), so clearing the property stops future replays
    /// but does not extinguish an effect already running on a client. That only ends on the next
    /// rebuild, which is expected and harmless.
    /// </summary>
    public static class VisualEffectManager
    {
        /// <summary>
        /// Scratch overrides keyed by object GUID, for /vfxstick. These are NOT persisted and do not
        /// survive a server restart; the property is the real storage. Kept because testing a script
        /// on a live object without editing a weenie is the fastest iteration loop there is.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, List<uint>> Scratch = new ConcurrentDictionary<uint, List<uint>>();

        public static void Attach(uint objectGuid, uint scriptId)
        {
            var list = Scratch.GetOrAdd(objectGuid, _ => new List<uint>());
            lock (list)
            {
                if (!list.Contains(scriptId))
                    list.Add(scriptId);
            }
        }

        public static bool Clear(uint objectGuid) => Scratch.TryRemove(objectGuid, out _);

        public static IEnumerable<(uint guid, List<uint> scripts)> AllScratch()
        {
            foreach (var kv in Scratch)
                yield return (kv.Key, kv.Value.ToList());
        }

        /// <summary>
        /// Emits every script attached to <paramref name="wo"/> down this session, AT MOST ONCE for as
        /// long as that client keeps the object built. Must be called immediately after the object's
        /// GameMessageCreateObject - EnqueueSend preserves order, so the client has built the object
        /// before the script arrives.
        ///
        /// The once-only guard is load-bearing, not tidiness. These emitters are endless and handle 0
        /// allocates a NEW one per send rather than replacing, so sending twice leaves two running
        /// forever and the effect visibly doubles. Several paths can legitimately fire for the same
        /// object in one session - created into a pack, then equipped, then unequipped and re-equipped -
        /// and only the first should reach the client. Forget() reopens it when the client is told to
        /// destroy the object, which is the only thing that actually clears a handle-0 emitter.
        /// </summary>
        /// <param name="rebuilt">
        /// The caller re-sent this object's CreateObject WITHOUT a preceding delete (see /fi). The
        /// client discards the emitters rebuilding the object either way, so here the re-emit restores
        /// what the rebuild destroyed instead of doubling a live one, and the guard must be bypassed.
        /// </param>
        public static void SendTo(Session session, WorldObject wo, bool rebuilt = false)
        {
            if (session == null || wo == null)
                return;

            var player = session.Player;
            if (player != null)
            {
                lock (player.VisualEffectsSent)
                {
                    if (!player.VisualEffectsSent.Add(wo.Guid.Full) && !rebuilt)
                        return;
                }
            }

            var property = wo.VisualEffectScript;
            if (property.HasValue && property.Value != 0)
                session.Network.EnqueueSend(new GameMessagePlayScriptId(wo.Guid, property.Value));

            if (Scratch.IsEmpty)
                return;

            if (!Scratch.TryGetValue(wo.Guid.Full, out var scratch))
                return;

            foreach (var scriptId in scratch.ToList())
            {
                // A scratch entry duplicating the stored property would send the same script twice,
                // and because handle 0 allocates a fresh emitter each time that renders as a visibly
                // doubled effect rather than a no-op.
                if (property.HasValue && property.Value == scriptId)
                    continue;

                session.Network.EnqueueSend(new GameMessagePlayScriptId(wo.Guid, scriptId));
            }
        }

        /// <summary>
        /// Call when a client is told to DESTROY an object (GameMessageDeleteObject). That is the only
        /// thing that clears a handle-0 emitter, so it is also the only point at which re-sending
        /// becomes correct rather than duplicating.
        /// </summary>
        public static void Forget(Session session, WorldObject wo)
        {
            var player = session?.Player;
            if (player == null || wo == null)
                return;

            lock (player.VisualEffectsSent)
                player.VisualEffectsSent.Remove(wo.Guid.Full);
        }
    }
}
