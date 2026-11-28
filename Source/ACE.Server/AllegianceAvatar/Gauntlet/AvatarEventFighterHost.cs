using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The Avatar fighter in its own private World Event (design part B, owner-approved 2026-11-23). Installed as
/// WorldEventHooks.AvatarEventHost, so World Events reaches it only through that seam, and only for a run with an AvatarId.
/// <list type="bullet">
/// <item>Active: the summoned vendor despawns, and a fighter (AvatarFighterFactory: dressed, 2x, NPC-presented) is placed at
/// the event anchor, leashed to <see cref="LeashRadius"/>. It follows the highest-level living player near the anchor.</item>
/// <item>Each wave: restamped at min(Avatar level, the wave band's top) from the World Events band basis
/// (WorldEventHooks.AvatarFighterBasisAt) through the Gauntlet fighter's stats function.</item>
/// <item>A fall: it returns after <see cref="RespawnSeconds"/>, at the anchor, at full health.</item>
/// <item>Resolved, Cleanup or Done (a forced end included): it is removed, unbound first so the removal is never a death.</item>
/// </list>
/// While a fighter is up, /avatar summon is refused (<see cref="IsUp"/>). Every entry point is on the world thread; the
/// fighter's own writes go to the anchor landblock's thread through <see cref="OnAnchorThread"/>.
/// </summary>
internal sealed class AvatarEventFighterHost : IAvatarEventHost
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The event radius the fighter is leashed to, in metres (design part B).</summary>
    public const float LeashRadius = 60.0f;

    /// <summary>Seconds a fallen event fighter is held out before it returns (design part B).</summary>
    public const double RespawnSeconds = 60.0;

    public const string SummonRefusal = "The Avatar is in the field with your allegiance's event. It cannot be summoned until the event ends.";

    public static AvatarEventFighterHost Live { get; } = new AvatarEventFighterHost();

    // ---- seams (tests replace them; the defaults are the live world) ------------------------------------------------------

    /// <summary>Builds and dresses an unplaced fighter for the Avatar, or null.</summary>
    internal Func<uint, GauntletAvatarFighter> Build { get; init; } = BuildLive;

    /// <summary>Puts the fighter into the world at the position (its landblock thread). False when refused.</summary>
    internal Func<GauntletAvatarFighter, Position, bool> Place { get; init; } = AvatarFighterFactory.PlaceInWorld;

    /// <summary>Runs an action on the landblock holding the position (or at once when it is not loaded: EnterWorld loads it).</summary>
    internal Action<Position, Action> OnAnchorThread { get; init; } = EnqueueOnLandblock;

    /// <summary>Runs an action on the world thread after a delay in seconds.</summary>
    internal Action<double, Action> After { get; init; } = AfterLive;

    internal Action<GauntletAvatarFighter> Destroy { get; init; } = f => f.Destroy();

    internal Func<uint, int> AvatarLevel { get; init; } = id => AvatarManager.Get(id)?.Level(AvatarManager.CurrentTuning()) ?? 0;

    /// <summary>The stamp at a fighting level (already min(A, band top)).</summary>
    internal Func<int, GauntletFighterStamp> StampAt { get; init; } = StampAtLive;

    /// <summary>Every player near the event anchor, a snapshot (filtered by <see cref="People"/>).</summary>
    internal Func<WorldEvent, IEnumerable<Player>> Near { get; init; } = evt => WorldEventHooks.PlayersNearEvent(evt, LeashRadius);

    /// <summary>True when the character has a participation record in the event.</summary>
    internal Func<WorldEvent, uint, bool> Credited { get; init; } = WorldEventHooks.IsEventParticipant;

    /// <summary>True for an announced (server-wide) event; false for a quiet one, which is confined to the allegiance.</summary>
    internal Func<WorldEvent, bool> Announced { get; init; } = WorldEventHooks.IsAnnouncedEvent;

    /// <summary>True when the player is sworn to the Avatar's allegiance.</summary>
    internal Func<uint, Player, bool> Sworn { get; init; }

    /// <summary>True while the fighter is in the world (not destroyed, on a landblock).</summary>
    internal Func<GauntletAvatarFighter, bool> InWorld { get; init; } = f => !f.IsDestroyed && f.CurrentLandblock != null;

    /// <summary>The Avatar a slot fights for (the sworn test reads its monarch).</summary>
    internal Func<uint, AvatarState> AvatarOf { get; init; } = AvatarManager.Get;

    /// <summary>The clock (unix seconds) the pending deadline reads.</summary>
    internal Func<double> Now { get; init; } = ACE.Common.Time.GetUnixTime;

    internal Func<WorldEvent, Position> AnchorOf { get; init; } = evt => evt?.Composition?.AnchorPosition;

    internal Action<uint> DespawnVendor { get; init; } = AvatarNpcs.DespawnSummoned;

    // ---- slots --------------------------------------------------------------------------------------------------------

    private readonly ConcurrentDictionary<uint, Slot> slots = new ConcurrentDictionary<uint, Slot>();

    /// <summary>One run's fighter. The fighter field is written on the anchor landblock's thread.</summary>
    internal sealed class Slot : IGauntletFighterOwner
    {
        public readonly AvatarEventFighterHost Host;
        public readonly WorldEvent Event;
        public readonly uint AvatarId;
        public readonly Position Anchor;
        public volatile GauntletAvatarFighter Fighter;
        public volatile GauntletFighterStamp LastStamp;
        public volatile bool Removed;
        public volatile bool EverPlaced;
        public int BandTop;

        /// <summary>
        /// Until when a queued placement or a scheduled return is expected to land (unix seconds; 0 = none), so the reconcile
        /// waits. A deadline, not a flag: a placement that never runs (a dropped landblock queue) expires instead of
        /// blocking the reconcile forever. Under lock(this).
        /// </summary>
        public double PendingUntil;

        /// <summary>Placement failures in a row (the first warns, the rest are Debug). Reset by a success.</summary>
        public int PlaceFailures;

        public Slot(AvatarEventFighterHost host, WorldEvent evt, uint avatarId, Position anchor)
        {
            Host = host;
            Event = evt;
            AvatarId = avatarId;
            Anchor = anchor;
        }

        /// <summary>Members may heal and buff the fighter while its event is on.</summary>
        public bool IsLiveCopy => !Removed;

        public void OnFighterDied(GauntletAvatarFighter fighter)
        {
            lock (this)
            {
                if (Removed || !ReferenceEquals(Fighter, fighter))
                    return;

                Fighter = null;
                PendingUntil = Host.PendingDeadline();
            }

            Host.ScheduleReturn(this);
        }
    }

    internal Slot SlotFor(WorldEvent evt) => evt == null ? null : slots.TryGetValue(evt.RunId, out var s) ? s : null;

    /// <summary>
    /// True while a private event for this Avatar is live (Active through its end), placed or not: /avatar summon is refused
    /// for the whole event, so the vendor never comes back mid-event while the reconcile is still placing the fighter. Any thread.
    /// </summary>
    public bool IsUp(uint avatarId) => slots.Values.Any(s => s.AvatarId == avatarId && !s.Removed);

    /// <summary>Seconds past the respawn hold a pending placement may take before the reconcile stops waiting for it.</summary>
    public const double PendingSlackSeconds = 30.0;

    internal double PendingDeadline() => Now() + RespawnSeconds + PendingSlackSeconds;

    /// <summary>True while a placement or return is expected (the deadline has not passed). Caller holds lock(slot).</summary>
    private bool IsPending(Slot slot) => slot.PendingUntil > 0 && Now() < slot.PendingUntil;

    private bool IsSworn(uint avatarId, Player p) => Sworn != null ? Sworn(avatarId, p) : AvatarRights.IsMember(p, AvatarOf(avatarId));

    /// <summary>Seconds between reconcile passes while the event is live.</summary>
    public const double ReconcileSeconds = 5.0;

    /// <summary>
    /// The players the fighter fights beside (design ruling F4, 2026-11-23): near the anchor, in its instance, and sworn to
    /// the Avatar's allegiance, or, in an ANNOUNCED event only, credited in it. A QUIET event is confined to the allegiance
    /// (owner ruling): a credited non-member there (a run creature's landed hit is enough to credit one) is never a leader
    /// and never hears a line. A bystander is neither in any event. Every voice trigger and the leader choice read this.
    /// </summary>
    internal List<Player> People(Slot slot)
    {
        var evt = slot.Event;
        Func<uint, bool> credited = Announced(evt) ? g => Credited(evt, g) : null;

        return WorldEventHooks.SelectAvatarEventPlayers(Near(evt), credited, p => IsSworn(slot.AvatarId, p));
    }

    /// <summary>The fighter's listeners: <see cref="People"/> less hidden or cloaked admins.</summary>
    internal List<Player> Listeners(Slot slot) => People(slot).Where(p => !AvatarFighterVoice.IsHidden(p)).ToList();

    // ---- the host sink ----------------------------------------------------------------------------------------------

    public void OnStateChanged(WorldEvent evt, WorldEventState from, WorldEventState to)
    {
        if (evt?.AvatarId == null)
            return;

        if (to == WorldEventState.Active)
            Start(evt);
        else if (to == WorldEventState.Resolved || to == WorldEventState.Cleanup || to == WorldEventState.Done)
            End(evt);
    }

    public void OnWave(WorldEvent evt, int wave, int bandTop)
    {
        var slot = SlotFor(evt);

        if (slot == null || slot.Removed || bandTop <= 0)
            return;

        slot.BandTop = bandTop;

        var stamp = SafeStamp(slot);

        if (stamp == null)
            return;

        slot.LastStamp = stamp;

        SafeOnAnchor(slot, "restamp", () =>
        {
            var f = slot.Fighter;

            if (f == null || f.IsDead || f.IsDestroyed || slot.Removed)
                return;

            f.ApplyStamp(stamp);
            log.Info($"[AVATAR] event run {evt.RunId} fighter restamped for wave {wave}: L{stamp.Level} (band top {bandTop}, Avatar {slot.AvatarId})");
        });

        SafeOnAnchor(slot, "reconcile", () => Reconcile(slot));
    }

    /// <summary>
    /// Puts a fighter back when the slot has none in the world and nothing is on its way: a failed first placement, or a
    /// fighter destroyed without a death (an admin delete, a landblock reset). The anchor landblock's thread. True when it placed.
    /// </summary>
    internal bool Reconcile(Slot slot)
    {
        GauntletAvatarFighter stale;

        lock (slot)
        {
            if (slot.Removed || IsPending(slot))
                return false;

            stale = slot.Fighter;

            if (stale != null && InWorld(stale))
                return false;

            slot.Fighter = null;
            slot.PendingUntil = PendingDeadline();
        }

        if (stale != null)
        {
            log.Warn($"[AVATAR] event run {slot.Event?.RunId}: fighter 0x{stale.Guid} left the world without a death; replacing it");
            stale.Owner = null;
            SafeDestroy(stale);
        }

        return PlaceNew(slot) != null;
    }

    private void ScheduleReconcile(Slot slot)
    {
        try
        {
            After(ReconcileSeconds, () =>
            {
                if (slot.Removed)
                    return;

                SafeOnAnchor(slot, "reconcile", () => Reconcile(slot));
                ScheduleReconcile(slot);
            });
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] event run {slot.Event?.RunId}: scheduling the reconcile threw", ex);
        }
    }

    // ---- lifetime ---------------------------------------------------------------------------------------------------------

    private void Start(WorldEvent evt)
    {
        var avatarId = evt.AvatarId.Value;
        var anchor = AnchorOf(evt);

        if (anchor == null)
        {
            log.Warn($"[AVATAR] event run {evt.RunId}: no anchor, so no fighter for Avatar {avatarId}");
            return;
        }

        var slot = new Slot(this, evt, avatarId, new Position(anchor));

        if (!slots.TryAdd(evt.RunId, slot))
            return;   // Active is reached once; a repeat is ignored

        try
        {
            DespawnVendor(avatarId);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] event run {evt.RunId}: despawning Avatar {avatarId}'s summoned NPC threw", ex);
        }

        slot.LastStamp = SafeStamp(slot);

        lock (slot)
            slot.PendingUntil = PendingDeadline();

        SafeOnAnchor(slot, "spawn", () => PlaceNew(slot));
        ScheduleReconcile(slot);
    }

    private void End(WorldEvent evt)
    {
        if (evt == null || !slots.TryRemove(evt.RunId, out var slot))
            return;

        GauntletAvatarFighter f;

        lock (slot)
        {
            slot.Removed = true;
            f = slot.Fighter;
            slot.Fighter = null;
        }

        if (f == null)
            return;

        // unbound first: the removal is never read as a death, and the beneficial opt-in closes at once
        f.Owner = null;

        SafeOnAnchor(slot, "remove", () => SafeDestroy(f));
    }

    internal void ScheduleReturn(Slot slot)
    {
        try
        {
            After(RespawnSeconds, () =>
            {
                if (slot.Removed)
                    return;

                SafeOnAnchor(slot, "return", () => PlaceNew(slot));
            });
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] event run {slot.Event?.RunId}: scheduling the fighter's return threw; the reconcile will place it", ex);

            lock (slot)
                slot.PendingUntil = 0;
        }
    }

    /// <summary>Builds, stamps and places a fighter for <paramref name="slot"/> (the anchor landblock's thread).</summary>
    internal GauntletAvatarFighter PlaceNew(Slot slot)
    {
        try
        {
            return PlaceNewCore(slot);
        }
        finally
        {
            // the attempt is over, placed or not: a failure is the reconcile's to retry
            lock (slot)
                slot.PendingUntil = 0;
        }
    }

    private GauntletAvatarFighter PlaceNewCore(Slot slot)
    {
        lock (slot)
        {
            // ended, or a late placement after the reconcile already put one in: never two fighters
            if (slot.Removed || (slot.Fighter != null && InWorld(slot.Fighter)))
                return null;
        }

        GauntletAvatarFighter fighter;

        try
        {
            fighter = Build(slot.AvatarId);
        }
        catch (Exception ex)
        {
            LogPlaceFailure(slot, $"[AVATAR] event run {slot.Event?.RunId}: building the fighter threw", ex);
            return null;
        }

        if (fighter == null)
        {
            LogPlaceFailure(slot, $"[AVATAR] event run {slot.Event?.RunId}: no fighter (wcid {GauntletAvatarFighter.FighterWcid} missing)");
            return null;
        }

        var evt = slot.Event;

        fighter.Anchor = new Position(slot.Anchor);
        fighter.LeashRadius = LeashRadius;
        fighter.Participants = () => People(slot);
        fighter.Audience = () => Listeners(slot);
        fighter.Owner = slot;
        fighter.ApplyStamp(slot.LastStamp, fullHealth: true);

        bool placed;

        try
        {
            placed = Place(fighter, slot.Anchor);
        }
        catch (Exception ex)
        {
            LogPlaceFailure(slot, $"[AVATAR] event run {evt?.RunId}: placing the fighter threw", ex);
            placed = false;
        }

        if (!placed)
        {
            fighter.Owner = null;
            LogPlaceFailure(slot, $"[AVATAR] event run {evt?.RunId}: the fighter was refused at {slot.Anchor.ToLOCString()}");
            SafeDestroy(fighter);
            return null;
        }

        bool ended;

        lock (slot)
        {
            ended = slot.Removed;

            if (!ended)
            {
                slot.Fighter = fighter;
                slot.PlaceFailures = 0;
            }
        }

        // the event ended while this was queued: take it straight back out
        if (ended)
        {
            fighter.Owner = null;
            SafeDestroy(fighter);
            return null;
        }

        AvatarFighterProbe.OnPlaced(fighter, $"event run {evt?.RunId}");
        AvatarFighterVoice.Say(fighter, slot.EverPlaced ? FighterVoiceTrigger.Return : FighterVoiceTrigger.Spawn);
        slot.EverPlaced = true;

        return fighter;
    }

    // ---- strength ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The level the fighter fights at (pure): min(Avatar level, the band top). Before the first wave the band top is the
    /// highest level among the players near the anchor; with neither, the Avatar level. At least 1.
    /// </summary>
    internal static int FightingLevel(int avatarLevel, int bandTop, int topNearbyLevel)
    {
        var top = bandTop > 0 ? bandTop : topNearbyLevel;
        var level = top > 0 ? Math.Min(avatarLevel, top) : avatarLevel;

        return Math.Max(1, level);
    }

    private GauntletFighterStamp SafeStamp(Slot slot)
    {
        try
        {
            var nearTop = 0;

            if (slot.BandTop <= 0)
                nearTop = People(slot).Select(p => p.Level ?? 0).DefaultIfEmpty(0).Max();

            return StampAt(FightingLevel(Math.Max(0, AvatarLevel(slot.AvatarId)), slot.BandTop, nearTop));
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] event run {slot.Event?.RunId}: the fighter stamp threw; it keeps its last one", ex);
            return null;
        }
    }

    /// <summary>The Gauntlet fighter's stats function over the World Events band basis, at the fighting level for both L and A.</summary>
    private static GauntletFighterStamp StampAtLive(int level)
    {
        var (standard, medianHealth, perHitMedian) = WorldEventHooks.AvatarFighterBasisAt(level);

        return EventStamp(level, medianHealth, standard, perHitMedian, GauntletTuning.FromPropertyManager());
    }

    /// <summary>
    /// The private event's stamp (pure): the Gauntlet's stats function, WITHOUT the band armor. The band armor (owner ruling
    /// 2026-11-25) is scoped to the Gauntlet, so an event fighter keeps its authored (Warden's) armor.
    /// </summary>
    internal static GauntletFighterStamp EventStamp(int level, double medianHealth, ACE.Server.ThreadDungeons.DungeonBandStandard standard, double perHitMedian, GauntletTuning tuning)
    {
        var stamp = GauntletFighterStats.Compute(level, medianHealth, standard, perHitMedian, level, tuning);

        return stamp == null ? null : stamp with { ArmorTarget = 0 };
    }

    // ---- live defaults --------------------------------------------------------------------------------------------------------

    private static GauntletAvatarFighter BuildLive(uint avatarId)
    {
        var f = AvatarFighterFactory.CreateFromWeenie();

        if (f != null)
            AvatarFighterFactory.Dress(f, avatarId);

        return f;
    }

    private static void EnqueueOnLandblock(Position at, Action action)
    {
        var lb = LandblockManager.GetLoadedLandblock(new LandblockId(at.Cell & 0xFFFF0000u), at.Instance);

        if (lb == null)
        {
            action();
            return;
        }

        lb.EnqueueAction(new ActionEventDelegate(action));
    }

    private static void AfterLive(double seconds, Action action)
    {
        var chain = new ActionChain();
        chain.AddDelaySeconds(seconds);
        chain.AddAction(WorldManager.ActionQueue, action);
        chain.EnqueueChain();
    }

    // ---- guards -----------------------------------------------------------------------------------------------------------

    private void SafeOnAnchor(Slot slot, string what, Action action)
    {
        try
        {
            OnAnchorThread(slot.Anchor, () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error($"[AVATAR] event run {slot.Event?.RunId} fighter {what} threw", ex);
                }
            });
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] event run {slot.Event?.RunId} fighter {what} could not be queued", ex);

            // nothing will land: the reconcile may place (PlaceNew refuses a second fighter if one is in the world)
            lock (slot)
                slot.PendingUntil = 0;
        }
    }

    /// <summary>A placement failure: Warn on the first of a streak, Debug after (the reconcile retries every 5 s). True when it warned.</summary>
    internal static bool LogPlaceFailure(Slot slot, string message, Exception ex = null)
    {
        var first = Interlocked.Increment(ref slot.PlaceFailures) == 1;

        if (first)
            log.Warn(message, ex);
        else
            log.Debug(message, ex);

        return first;
    }

    private void SafeDestroy(GauntletAvatarFighter f)
    {
        try
        {
            if (!f.IsDestroyed)
                Destroy(f);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] event fighter 0x{f.Guid} destroy threw", ex);
        }
    }
}
