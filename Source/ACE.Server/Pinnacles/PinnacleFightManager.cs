using System;
using System.Collections.Concurrent;
using System.Diagnostics;

using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Knots;
using ACE.Server.Managers;
using ACE.Server.Realms;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Pinnacles
{
    /// <summary>One registered live fight: the core, its live world and the landblock whose thread owns them.</summary>
    public sealed class LiveFight
    {
        public LiveFight(PinnacleFight fight, LivePinnacleWorld world, Landblock landblock)
        {
            Fight = fight;
            World = world;
            Landblock = landblock;
        }

        public PinnacleFight Fight { get; }

        public LivePinnacleWorld World { get; }

        public Landblock Landblock { get; }

        /// <summary>The fight's copy is still the registered, loaded instance. Safe from any thread (a registry read).</summary>
        public bool IsCopyLive => ReferenceEquals(LandblockManager.GetEphemeralLandblock(Fight.Instance), Landblock);
    }

    /// <summary>
    /// The registry of live Pinnacle fights (one per owner) and the scripts that can run: the 1 s tick chains on each
    /// fight's landblock, the 30 s world-thread reaper for fights whose copy unloaded, and the three calls the game
    /// makes into it: <see cref="Initialize"/> (Program, after ThreadDungeonManager), <see cref="OnOwnerDied"/>
    /// (Player.Die, after the penalty latches) and <see cref="TryHandleHeadUse"/> (Creature.ActOnUse).
    ///
    /// SCRIPTS ARE REGISTERED AS FACTORIES. A boss script holds per-fight state (kills, round, series), so every new
    /// fight constructs its OWN script from the factory; one script object is never shared between fights.
    ///
    /// THE REALM HOLD. <see cref="AttachHold"/> sets EphemeralRealm.Hold to the fight when a copy is created and to
    /// null on release (<see cref="ReleaseHold"/>). The landblock heartbeat pulls the copy's unload override from it on
    /// every heartbeat, so a held fight keeps its copy loaded for the hold. A copy that hosts a Thread run is never given
    /// a hold (Hold and Run are never set together); AttachHold logs and skips that case.
    /// </summary>
    public static class PinnacleFightManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const double TickSeconds = 1.0;
        public const double ReaperSeconds = 30.0;

        private static readonly ConcurrentDictionary<string, Func<IPinnacleBossScript>> factories
            = new ConcurrentDictionary<string, Func<IPinnacleBossScript>>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<uint, LiveFight> byOwner = new ConcurrentDictionary<uint, LiveFight>();

        private static int reaperRunning;

        // ================================================================ scripts

        /// <summary>
        /// Registers the factory for a destination kind and the Knot-side encounter that routes to it. Replaces an
        /// earlier registration of the same kind. A blank kind or a null factory is ignored.
        /// </summary>
        public static void Register(string kind, Func<IPinnacleBossScript> create)
        {
            if (string.IsNullOrWhiteSpace(kind) || create == null)
                return;

            factories[kind] = create;
            PinnacleEncounters.Register(new PinnacleEncounter(kind));
        }

        /// <summary>A NEW script for <paramref name="kind"/>, or null when none is registered or the factory failed.</summary>
        internal static IPinnacleBossScript CreateScript(string kind)
        {
            if (kind == null || !factories.TryGetValue(kind, out var create))
                return null;

            try
            {
                return create();
            }
            catch (Exception ex)
            {
                log.Error($"[PINNACLE] the script factory for '{kind}' threw", ex);
                return null;
            }
        }

        /// <summary>Test seam: forgets every factory and fight.</summary>
        internal static void ClearForTests()
        {
            factories.Clear();
            byOwner.Clear();
        }

        // ================================================================ M2d entry points

        /// <summary>World startup: registers the shipped boss scripts and starts the 30 s reaper. Idempotent.</summary>
        public static void Initialize()
        {
            RegisterShippedScripts();

            if (System.Threading.Interlocked.CompareExchange(ref reaperRunning, 1, 0) == 0)
                QueueReaper();
        }

        /// <summary>
        /// The owner died. Only enqueues <c>fight.OwnerDied</c> on the fight's landblock; never throws, changes
        /// nothing about the death itself.
        /// </summary>
        public static void OnOwnerDied(Player player)
        {
            try
            {
                if (player == null || !byOwner.TryGetValue(player.Guid.Full, out var lf) || lf.Fight.IsEnded)
                    return;

                if (player.Location == null || player.Location.Instance != lf.Fight.Instance)
                    return;

                lf.Landblock.EnqueueAction(new ActionEventDelegate(() => lf.Fight.OwnerDied(Now())));
            }
            catch (Exception ex)
            {
                log.Error("[PINNACLE] OnOwnerDied threw", ex);
            }
        }

        /// <summary>
        /// Head use (Creature.ActOnUse, ahead of the Use emote). True when <paramref name="head"/> is the owner's
        /// fight head: the use is consumed and the fight begins (a no-op once begun). False for anything else.
        /// </summary>
        public static bool TryHandleHeadUse(Creature head, Player player)
        {
            try
            {
                if (head == null || player == null || !byOwner.TryGetValue(player.Guid.Full, out var lf) || lf.Fight.IsEnded)
                    return false;

                if (lf.Fight.Head.IsNone || !lf.World.IsObject(lf.Fight.Head, head))
                    return false;

                lf.Landblock.EnqueueAction(new ActionEventDelegate(() => lf.Fight.TryBegin(Now())));
                return true;
            }
            catch (Exception ex)
            {
                log.Error("[PINNACLE] TryHandleHeadUse threw", ex);
                return false;
            }
        }

        /// <summary>
        /// Prop use (Door.ActOnUse, first line). True when <paramref name="prop"/> is a prop the user's own live fight
        /// spawned: the use is consumed (the door's own open/close/lock logic never runs) and delivered to the fight on its
        /// landblock as ObjectUsed, which the core drops unless the fight is Running. False for anything else, which is
        /// every door in the game but those: one registry lookup by the user's guid, a miss for everyone without a fight.
        /// </summary>
        public static bool TryHandlePropUse(WorldObject prop, Player player)
        {
            try
            {
                if (prop == null || player == null || !byOwner.TryGetValue(player.Guid.Full, out var lf) || lf.Fight.IsEnded)
                    return false;

                if (!lf.World.TryGetHandle(prop, out var h))
                    return false;

                lf.Landblock.EnqueueAction(new ActionEventDelegate(() => lf.Fight.OnObjectUsed(h)));
                return true;
            }
            catch (Exception ex)
            {
                log.Error("[PINNACLE] TryHandlePropUse threw", ex);
                return false;
            }
        }
        // ================================================================ registry

        /// <summary>Registers every shipped boss script. A NEW script per fight (it holds per-fight state), built from the live settings at fight creation.</summary>
        internal static void RegisterShippedScripts()
        {
            Register("grael", () => new Grael.GraelScript(PinnacleTunables.Grael()));
            Register("matron", () => new Matron.MatronScript(PinnacleTunables.Matron()));
        }

        /// <summary>The registered, unended fight whose copy has this realm instance (the /pinnacle admin lookup), or false.</summary>
        internal static bool TryGetFightByInstance(uint instance, out LiveFight fight)
        {
            foreach (var lf in byOwner.Values)
            {
                if (lf.Fight.Instance == instance && !lf.Fight.IsEnded)
                {
                    fight = lf;
                    return true;
                }
            }

            fight = null;
            return false;
        }

        /// <summary>
        /// Player.Die's latch (owner ruling 2026-10-20: a Pinnacle death costs no vitae and drops no items): true when
        /// <paramref name="instance"/> is the copy of a registered, unended fight. Any thread (a registry snapshot read);
        /// never throws, a fault reads as false (the normal death penalties).
        /// </summary>
        public static bool IsInsidePinnacleCopy(uint instance)
        {
            try
            {
                return IsInsideCopy(instance, System.Linq.Enumerable.Select(byOwner.Values, lf => lf.Fight));
            }
            catch (Exception ex)
            {
                log.Error("[PINNACLE] IsInsidePinnacleCopy threw", ex);
                return false;
            }
        }

        // Owner ruling 2026-10-22: rare gems are disabled in Knot fights - stripped on entry, refused inside.
        internal const string RareGemsStrippedLine = "The Knot unravels the power of your rare gems.";
        internal const string RareGemsRefusedLine = "Rare gems have no power inside a Knot.";

        /// <summary>The pure refusal decision: a rare gem (Gem weenie carrying a RareId) used inside a fight copy.</summary>
        internal static bool RefusesRareGem(bool hasRareId, bool inFightCopy) => hasRareId && inFightCopy;

        /// <summary>
        /// The pure half of <see cref="IsInsidePinnacleCopy"/>: some unended fight in <paramref name="fights"/> has
        /// <paramref name="instance"/> as its copy. Instance 0 (never an ephemeral copy) is always false.
        /// </summary>
        internal static bool IsInsideCopy(uint instance, System.Collections.Generic.IEnumerable<PinnacleFight> fights)
        {
            if (instance == 0 || fights == null)
                return false;

            foreach (var f in fights)
                if (f != null && f.Instance == instance && !f.IsEnded)
                    return true;

            return false;
        }

        /// <summary>The owner's registered fight, ended or not.</summary>
        internal static bool TryGetFight(uint ownerGuid, out LiveFight fight) => byOwner.TryGetValue(ownerGuid, out fight);

        /// <summary>
        /// What a use of this Knot would do to the owner's live attempt on it, in a copy that is still loaded (owner rulings
        /// R1 and C1, 2026-10-19): Running or Held is Paid, Won with the cache owed is Owed, Won and collecting is Free,
        /// anything else (no fight, another Knot, NotStarted, ended, a dead copy) is None. A side-effect-free snapshot
        /// read; never throws.
        /// </summary>
        internal static KnotResume ResumableAttempt(uint ownerGuid, uint knotGuid)
        {
            try
            {
                if (!byOwner.TryGetValue(ownerGuid, out var lf) || lf.Fight.IsEnded || lf.Fight.KnotGuid != knotGuid)
                    return KnotResume.None;

                var kind = ResumeKind(lf.Fight.State, lf.Fight.AwaitingDelivery, lf.Fight.Collecting);

                return kind != KnotResume.None && lf.IsCopyLive ? kind : KnotResume.None;
            }
            catch (Exception)
            {
                return KnotResume.None;
            }
        }

        /// <summary>The pure half of <see cref="ResumableAttempt"/>: the resume kind of a fight in this state.</summary>
        internal static KnotResume ResumeKind(PinnacleFightState state, bool awaitingDelivery, bool collecting)
        {
            switch (state)
            {
                case PinnacleFightState.Running:
                case PinnacleFightState.Held:
                    return KnotResume.Paid;

                case PinnacleFightState.Won:
                    return collecting ? KnotResume.Free : awaitingDelivery ? KnotResume.Owed : KnotResume.None;

                default:
                    return KnotResume.None;
            }
        }

        /// <summary>
        /// The owner could not pay to return to their attempt on this Knot: it is forfeited on its own landblock
        /// (PinnacleFight.Forfeit, which only acts on a Running or Held fight). Never throws.
        /// </summary>
        internal static void Forfeit(uint ownerGuid, uint knotGuid, string reason)
        {
            try
            {
                if (!byOwner.TryGetValue(ownerGuid, out var lf) || lf.Fight.IsEnded || lf.Fight.KnotGuid != knotGuid)
                    return;

                lf.Landblock.EnqueueAction(new ActionEventDelegate(() => lf.Fight.Forfeit(reason)));
            }
            catch (Exception ex)
            {
                log.Error("[PINNACLE] Forfeit threw", ex);
            }
        }

        /// <summary>The login line for an owner relocated out of their own still-open pinnacle copy (WorldManager).</summary>
        public const string StillOpenLoginLine = "Your pinnacle trial is being held. Use your Knot to return before it closes.";

        /// <summary>
        /// <see cref="StillOpenLoginLine"/> when <paramref name="player"/> owns an unended fight whose copy is
        /// <paramref name="savedInstance"/> and is still loaded (they relogged out of a held, owed or collecting attempt);
        /// otherwise null. Never throws.
        /// </summary>
        public static string GetStillOpenLoginMessage(Player player, uint savedInstance)
        {
            try
            {
                if (player == null || !byOwner.TryGetValue(player.Guid.Full, out var lf))
                    return null;

                return StillOpenLoginMessage(!lf.Fight.IsEnded && lf.Fight.Instance == savedInstance && lf.IsCopyLive);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string StillOpenLoginMessage(bool ownFightOpenOnThatInstance) => ownFightOpenOnThatInstance ? StillOpenLoginLine : null;

        /// <summary>
        /// Registers the fight (replacing the owner's earlier one), attaches the hold and starts its tick chain.
        /// A replaced fight that has not ended and whose copy is gone could no longer be found by the reaper once it is
        /// out of the registry, so it is ticked once on the world thread (the reaper's own thread) and ends there: its
        /// loss is reported to its Knot exactly as the reaper would have.
        /// </summary>
        internal static void Add(LiveFight lf)
        {
            if (byOwner.TryGetValue(lf.Fight.OwnerGuid, out var old) && !ReferenceEquals(old, lf) && !old.Fight.IsEnded && !old.IsCopyLive)
                WorldManager.EnqueueAction(new ActionEventDelegate(() => old.Fight.Tick(Now())));

            byOwner[lf.Fight.OwnerGuid] = lf;
            AttachHold(lf.Landblock, lf.Fight);
            QueueTick(lf);
        }

        internal static void AttachHold(Landblock landblock, IEphemeralHold hold) => AssignHold(landblock?.InnerRealmInfo, hold);

        /// <summary>
        /// Sets (or, with null, clears) the realm's hold. A realm that hosts a Thread run never gets one: Hold and Run
        /// are never set together, so that case is logged and skipped. Clearing is always allowed.
        /// </summary>
        internal static bool AssignHold(EphemeralRealm realm, IEphemeralHold hold)
        {
            if (realm == null)
                return false;

            if (hold != null && realm.Run != null)
            {
                log.Error("[PINNACLE] refusing to attach a hold to an ephemeral realm that hosts a Thread run");
                return false;
            }

            realm.Hold = hold;
            return true;
        }

        /// <summary>Clears the copy's hold (the fight ended). Called by the live world's ReleaseInstance.</summary>
        internal static void ReleaseHold(Landblock landblock) => AttachHold(landblock, null);

        private static void Remove(LiveFight lf)
            => byOwner.TryRemove(new System.Collections.Generic.KeyValuePair<uint, LiveFight>(lf.Fight.OwnerGuid, lf));

        internal static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // ================================================================ chains

        private static void QueueTick(LiveFight lf)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(TickSeconds);
            chain.AddAction(lf.Landblock, () => TickFight(lf));
            chain.EnqueueChain();
        }

        private static void TickFight(LiveFight lf)
        {
            try
            {
                lf.Fight.Tick(Now());
            }
            catch (Exception ex)
            {
                log.Error("[PINNACLE] fight tick threw", ex);
            }

            if (lf.Fight.IsEnded)
                Remove(lf);
            else
                QueueTick(lf);
        }

        private static void QueueReaper()
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(ReaperSeconds);
            chain.AddAction(WorldManager.ActionQueue, ReaperTick);
            chain.EnqueueChain();
        }

        /// <summary>
        /// World thread. A fight whose copy has unloaded can no longer tick on its landblock (the landblock is gone),
        /// so it is ticked here once: the core sees the copy is not live and ends Lost without touching the copy.
        /// </summary>
        private static void ReaperTick()
        {
            try
            {
                foreach (var lf in byOwner.Values)
                {
                    if (!lf.Fight.IsEnded && !lf.IsCopyLive)
                        lf.Fight.Tick(Now());

                    if (lf.Fight.IsEnded)
                        Remove(lf);
                }
            }
            catch (Exception ex)
            {
                log.Error("[PINNACLE] reaper threw", ex);
            }
            finally
            {
                QueueReaper();
            }
        }
    }
}
