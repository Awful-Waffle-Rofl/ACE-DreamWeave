using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WeaponMods;
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
        /// The persistent scripts sent behind an object's CreateObject, in send order: the object's own
        /// VisualEffectScript (9000) first, then - only when that is a weapon quality tier aura, the instance still has the template's Setup and the
        /// template's script differs - the script its weenie TEMPLATE carries (<see cref="BaseScriptToLayer"/>). This is the layering rule: a weapon-mod quality tier
        /// aura takes the single 9000 slot of the instance (WeaponModManager.ApplyTierScript), which would
        /// otherwise erase the element look the weenie bakes there (Olthoi Voidclaw 0x33001154, the Harbinger
        /// elemental casters). Sent effects are additive on the client, so the two render together, the way a
        /// retail elemental weapon shows its per-element Setup script plus the tier aura. An object with no
        /// template script, or whose current 9000 equals the template's, yields at most one script, as before.
        /// </summary>
        internal static uint[] PersistentScripts(WorldObject wo)
        {
            // The common case (no 9000) allocates nothing: this runs behind every CreateObject.
            var property = wo.VisualEffectScript;
            if (!property.HasValue || property.Value == 0)
                return Array.Empty<uint>();

            var baseScript = BaseScriptToLayer(wo, property.Value);
            return baseScript.HasValue ? new[] { property.Value, baseScript.Value } : new[] { property.Value };
        }

        /// <summary>
        /// The template base script to layer under the instance's own 9000, or null. Null when the instance's 9000
        /// is not a weapon quality tier aura (<see cref="WeaponQualityTiers.IsTierScript"/>), when it has
        /// no non-zero 9000 (a cleared property is deliberate, so nothing is resurrected; this also keeps the
        /// template read off the CreateObject path of the great majority of objects, which carry no script),
        /// when the template has none, or when the two are equal. The read is WeaponModManager's
        /// BaseVisualEffectScript, a dictionary lookup in the already-loaded world weenie cache, so it adds no
        /// per-wcid cache of its own.
        /// </summary>
        internal static uint? BaseScriptToLayer(WorldObject wo, uint property)
        {
            // Only a weapon quality tier aura layers. Every other stamped 9000 (a tailored look, a hazard host,
            // a puzzle gate, a pinnacle prop) owns the slot deliberately and is sent alone, as before.
            if (!WeaponQualityTiers.IsTierScript(property))
                return null;

            // Tailoring changes the look (Setup), so the template's element effect no longer belongs to the
            // instance; only the template's own Setup keeps its base script.
            var baseScript = WeaponModManager.BaseVisualEffectScript(wo, requireTemplateSetup: true);
            if (!baseScript.HasValue || baseScript.Value == property)
                return null;

            return baseScript;
        }

        /// <summary>
        /// Everything SendTo emits for <paramref name="wo"/>: <see cref="PersistentScripts"/>, then each /vfxstick
        /// scratch script not already in that set. A scratch entry duplicating an emitted script would send it
        /// twice, and because handle 0 allocates a fresh emitter each time that renders as a visibly doubled
        /// effect rather than a no-op.
        /// </summary>
        internal static IReadOnlyList<uint> ScriptsToEmit(WorldObject wo)
        {
            var persistent = PersistentScripts(wo);

            if (Scratch.IsEmpty || !Scratch.TryGetValue(wo.Guid.Full, out var scratch))
                return persistent;

            var all = new List<uint>(persistent);
            foreach (var scriptId in scratch.ToList())
            {
                if (!all.Contains(scriptId))
                    all.Add(scriptId);
            }

            return all;
        }

        /// <summary>
        /// Emits every script attached to <paramref name="wo"/> (see <see cref="ScriptsToEmit"/>: the 9000 property plus the layered template base) down this session, AT MOST ONCE for as
        /// long as that client keeps the object built. Must be called immediately after the object's
        /// GameMessageCreateObject - EnqueueSend preserves order, so the client has built the object
        /// before the script arrives.
        ///
        /// The once-only guard is load-bearing, not tidiness. These emitters are endless and handle 0
        /// allocates a NEW one per send rather than replacing, so sending twice leaves two running
        /// forever and the effect visibly doubles. Several paths can legitimately fire for the same
        /// object in one session - created into a pack, then equipped, then unequipped and re-equipped -
        /// and only the first should reach the client. Forget() reopens it when the client is told to
        /// destroy the object or rebuilds it with a fresh create, which are the only things that actually
        /// clear a handle-0 emitter (see Forget).
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

            foreach (var scriptId in ScriptsToEmit(wo))
                session.Network.EnqueueSend(new GameMessagePlayScriptId(wo.Guid, scriptId));
        }

        /// <summary>
        /// Call when a client is told to DESTROY an object (GameMessageDeleteObject), or is sent a fresh
        /// GameMessageCreateObject for an object it is not drawing (a pack or container item), which rebuilds
        /// the object and drops its emitters the same way. Those are the only points at which re-sending
        /// becomes correct rather than duplicating. Player.TryCreateInInventoryWithNetworking, the pack
        /// pickup in Player.HandleActionPutItemInContainer, Container.SendInventory, ThreadCachePlacer.Deliver
        /// and PlayerDuoGemInventory.CommitAdd call it for exactly that reason. Forgetting a guid that was
        /// never recorded is a no-op (HashSet.Remove of an absent key).
        /// </summary>
        public static void Forget(Session session, WorldObject wo)
        {
            var player = session?.Player;
            if (player == null || wo == null)
                return;

            lock (player.VisualEffectsSent)
                player.VisualEffectsSent.Remove(wo.Guid.Full);
        }

        /// <summary>
        /// <see cref="Forget(Session, WorldObject)"/> for a delete sent by guid alone, when the server may no longer
        /// hold the object (the teleport_flush_scope 2 ledger).
        /// </summary>
        public static void Forget(Session session, uint objectGuid)
        {
            var player = session?.Player;
            if (player == null)
                return;

            lock (player.VisualEffectsSent)
                player.VisualEffectsSent.Remove(objectGuid);
        }
    }
}
