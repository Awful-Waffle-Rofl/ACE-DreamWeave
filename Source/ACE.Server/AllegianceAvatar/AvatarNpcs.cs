using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>
/// The Avatar NPCs in the world: at most one SUMMONED NPC per Avatar (/avatar summon, gone after summonSeconds or on the
/// next summon) and at most one SEATED NPC per Avatar (standing at its Seat whenever that landblock is loaded). Neither is
/// ever persisted (WorldObject_Database excludes AvatarVendor), and every NPC created here carries the Avatar's id
/// (<see cref="AvatarVendor.AvatarId"/>) before it enters the world.
///
/// The contract with the lifecycle (Task 8):
/// - DespawnSummoned removes the SUMMONED NPC only, never the seated one.
/// - RevalidateSeat re-checks the Seat after a binding change: the seated NPC stays (it reads the binding by id, so a
///   new Keeper needs no respawn) and comes back if it is missing from a loaded landblock, or the Avatar is unseated
///   when the Seat no longer qualifies. It unseats an Avatar that is no longer Bound (absorbed or retired).
/// - Redress replaces each live NPC after a look change (ruling R7): every NPC is dressed by Build before it enters the
///   world, and an ObjDesc event cannot carry scale, Setup or wielded items to existing viewers.
///
/// THREADS. The NPC maps are guarded by one private lock, and nothing calls the world or the manager while holding it.
/// The lifecycle calls (DespawnSummoned, RevalidateSeat, Unseat), the 10-minute re-check and OnHouseLost's work run on
/// the world thread (OnHouseLost marshals there). Seated NPCs are created on their landblock's own action queue
/// (SpawnSeated). A summon creates and places its NPC on the caller's thread, the way the mule vendor's summon does.
/// Redress runs on the world thread (ruling R10): it replaces the summoned NPC inline and queues the seated one on its
/// landblock's action queue, the thread that creates seated NPCs.
/// </summary>
public static class AvatarNpcs
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>What the Seat popup and TrySeat read about the house the Keeper stands at.</summary>
    internal readonly record struct SeatSite(uint HouseGuid, HouseType Type, uint? Owner, bool OnProperty, Position Location);

    internal delegate bool SummonerStateFn(Player summoner, out string refusal);

    /// <summary>Test seam: receives every DespawnSummoned call.</summary>
    internal static Action<uint> DespawnSummonedSink;

    /// <summary>Test seam: receives every RevalidateSeat call.</summary>
    internal static Action<AvatarState> RevalidateSeatSink;

    /// <summary>Test seam: receives every Unseat call.</summary>
    internal static Action<AvatarState> UnseatSink;

    /// <summary>Test seam: builds one Avatar NPC (not yet bound to an Avatar).</summary>
    internal static Func<WorldObject> NpcBuilder = DefaultNpcBuilder;

    /// <summary>Test seam: places a summoned NPC in front of its summoner. Production is the mule placement ladder.</summary>
    internal static Func<AvatarVendor, Player, bool> SummonPlacer = DefaultSummonPlacer;

    /// <summary>Test seam: the summoner-state rule (dead, busy, airborne). Production is MuleSummonHandler's.</summary>
    internal static SummonerStateFn SummonerState = DefaultSummonerState;

    /// <summary>Test seam: true when a position is on the account_vault_denylist emergency lever.</summary>
    internal static Func<Position, bool> IsDenylisted = DefaultIsDenylisted;

    /// <summary>Test seam: arranges for a summoned NPC to go after that many seconds.</summary>
    internal static Action<AvatarVendor, double> ScheduleExpiry = DefaultScheduleExpiry;

    /// <summary>Test seam: the house guid a character owns (IPlayer.HouseInstance), or null.</summary>
    internal static Func<uint, uint?> HouseInstanceOf = DefaultHouseInstanceOf;

    /// <summary>Test seam: the house the Keeper stands at, without loading anything; null when there is none at hand.</summary>
    internal static Func<Player, SeatSite?> SeatSiteOf = DefaultSeatSiteOf;

    /// <summary>Test seam: the loaded landblock a position is in, or null. Never loads one.</summary>
    internal static Func<Position, Landblock> LoadedLandblockOf = DefaultLoadedLandblockOf;

    /// <summary>Test seam: runs work on the world thread.</summary>
    internal static Action<Action> WorldEnqueue = DefaultWorldEnqueue;

    /// <summary>Test seam: puts a replacement summoned NPC into the world at that exact position (Redress, world thread).</summary>
    internal static Func<AvatarVendor, Position, bool> SummonReplacer = DefaultSummonReplacer;

    /// <summary>
    /// Test seam: when <see cref="SummonReplacer"/> refuses the exact spot, places the replacement on the summon placement
    /// ladder around it (MuleSummonHandler.TryPlace with the summon's own rung gate). Production faces it back toward that spot.
    /// </summary>
    internal static Func<AvatarVendor, Position, bool> SummonFallbackPlacer = DefaultSummonFallbackPlacer;

    /// <summary>
    /// Test seam: queues work on the action queue of the loaded landblock holding that position, handing it the landblock's
    /// add. False when that landblock is not loaded (nothing is queued). Never loads one.
    /// </summary>
    internal static Func<Position, Action<Func<AvatarVendor, bool>>, bool> SeatLandblockEnqueue = DefaultSeatLandblockEnqueue;

    /// <summary>Test seam: false only when the caller is provably not on the world thread (Redress's precondition check).</summary>
    internal static Func<bool> OnWorldThread = DefaultOnWorldThread;

    /// <summary>Test seam: sends a Yes/No popup; false when the player is already answering another.</summary>
    internal static Func<Player, Confirmation, string, bool> SendConfirmation = DefaultSendConfirmation;

    /// <summary>Test seam: a chat line to one player.</summary>
    internal static Action<Player, string> Tell = DefaultTell;

    /// <summary>Test seam: an online player by guid, or null.</summary>
    internal static Func<uint, Player> OnlinePlayer = DefaultOnlinePlayer;

    /// <summary>Test seam: true while the Avatar's fighter is in the field in its private event (summon refused).</summary>
    internal static Func<uint, bool> EventFighterUp = DefaultEventFighterUp;

    private static bool DefaultEventFighterUp(uint avatarId) => Gauntlet.AvatarEventFighterHost.Live.IsUp(avatarId);

    internal static void ResetSeams()
    {
        DespawnSummonedSink = null;
        RevalidateSeatSink = null;
        UnseatSink = null;
        NpcBuilder = DefaultNpcBuilder;
        SummonPlacer = DefaultSummonPlacer;
        SummonerState = DefaultSummonerState;
        IsDenylisted = DefaultIsDenylisted;
        ScheduleExpiry = DefaultScheduleExpiry;
        HouseInstanceOf = DefaultHouseInstanceOf;
        SeatSiteOf = DefaultSeatSiteOf;
        LoadedLandblockOf = DefaultLoadedLandblockOf;
        WorldEnqueue = DefaultWorldEnqueue;
        EventFighterUp = DefaultEventFighterUp;
        SummonReplacer = DefaultSummonReplacer;
        SummonFallbackPlacer = DefaultSummonFallbackPlacer;
        SeatLandblockEnqueue = DefaultSeatLandblockEnqueue;
        OnWorldThread = DefaultOnWorldThread;
        SendConfirmation = DefaultSendConfirmation;
        Tell = DefaultTell;
        OnlinePlayer = DefaultOnlinePlayer;
    }

    private static readonly object gate = new object();
    /// <summary>The summoned NPC and when it goes (unix seconds), so a Redress replacement keeps the original expiry.</summary>
    private static readonly Dictionary<uint, (AvatarVendor Npc, double ExpiresAtUnix)> summoned = new Dictionary<uint, (AvatarVendor, double)>();
    private static readonly Dictionary<uint, (AvatarVendor Npc, AvatarSeat Seat)> seated = new Dictionary<uint, (AvatarVendor, AvatarSeat)>();

    /// <summary>Test helper: forgets every tracked NPC without destroying it.</summary>
    internal static void ClearTracking()
    {
        lock (gate)
        {
            summoned.Clear();
            seated.Clear();
        }
    }

    /// <summary>The live summoned NPC of that Avatar, or null.</summary>
    internal static AvatarVendor SummonedNpc(uint avatarId)
    {
        lock (gate)
            return summoned.TryGetValue(avatarId, out var entry) && !entry.Npc.IsDestroyed ? entry.Npc : null;
    }

    /// <summary>The live seated NPC of that Avatar, or null.</summary>
    internal static AvatarVendor SeatedNpc(uint avatarId)
    {
        lock (gate)
            return seated.TryGetValue(avatarId, out var entry) && !entry.Npc.IsDestroyed ? entry.Npc : null;
    }

    // ---- summon ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Summons the Avatar in front of <paramref name="summoner"/> for summonSeconds. Needs the Favor-spending rights or
    /// the Keeper's; refused while dead, busy or airborne (the mule rule), indoors, or on a denylisted landblock. One
    /// summoned NPC per Avatar: a new summon destroys the old one. A seated Avatar can still be summoned.
    /// </summary>
    public static bool TrySummon(Player summoner, AvatarState a, out string refusal)
    {
        if (summoner == null || a == null || a.State != AvatarBindState.Bound)
        {
            refusal = AvatarSeatRules.NoAvatarMessage;
            return false;
        }

        if (!AvatarRights.CanSpend(summoner, a) && !AvatarRights.IsKeeper(summoner, a))
        {
            refusal = AvatarSeatRules.SummonNotAllowedMessage;
            return false;
        }

        // the Avatar is fighting in its own private event (design part B): no vendor until the event ends
        if (EventFighterUp(a.AvatarId))
        {
            refusal = Gauntlet.AvatarEventFighterHost.SummonRefusal;
            return false;
        }

        if (!SummonerState(summoner, out refusal))
            return false;

        refusal = AvatarSeatRules.SummonAreaRefusal(summoner.Location, IsDenylisted);
        if (refusal != null)
            return false;

        var npc = Build(a.AvatarId);
        if (npc == null)
        {
            refusal = AvatarSeatRules.SummonFailedMessage;
            return false;
        }

        bool placed;
        try
        {
            placed = SummonPlacer(npc, summoner);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Summon of Avatar {a.AvatarId} by {summoner.Name} threw while placing.", ex);
            placed = false;
        }

        if (!placed)
        {
            npc.Destroy();
            refusal = AvatarSeatRules.SummonFailedMessage;
            return false;
        }

        var seconds = Math.Max(1L, AvatarManager.CurrentTuning().SummonSeconds);
        var expiresAt = AvatarManager.NowUnix() + seconds;

        AvatarVendor previous = null;
        lock (gate)
        {
            if (summoned.TryGetValue(a.AvatarId, out var old))
                previous = old.Npc;
            summoned[a.AvatarId] = (npc, expiresAt);
        }

        if (previous != null && previous != npc)
            previous.Destroy();

        AvatarLevelUp.SyncNpc(npc, a.AvatarId);

        ScheduleExpiry(npc, seconds);

        refusal = null;
        return true;
    }

    /// <summary>Removes the Avatar's summoned NPC, if one is out. Never touches the seated NPC.</summary>
    public static void DespawnSummoned(uint avatarId)
    {
        DespawnSummonedSink?.Invoke(avatarId);

        (AvatarVendor Npc, double ExpiresAtUnix) entry;
        lock (gate)
        {
            if (!summoned.Remove(avatarId, out entry))
                return;
        }

        entry.Npc.Destroy();
    }

    /// <summary>The summonSeconds timer: the NPC goes, and forgets its slot only if it still holds it.</summary>
    internal static void ExpireSummoned(AvatarVendor npc)
    {
        if (npc == null)
            return;

        lock (gate)
        {
            if (summoned.TryGetValue(npc.AvatarId, out var current) && current.Npc == npc)
                summoned.Remove(npc.AvatarId);
        }

        npc.Destroy();
    }

    // ---- seat -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// /avatar seat: checks the Keeper's rights and the mansion, then asks "Seat the Avatar here? ..." with the Version
    /// read now. Yes arrives on the Keeper's action queue and hands the re-check and the seat write to the world thread,
    /// where TrySeat re-checks the Keeper, the mansion, OnProperty, the cooldown and that Version.
    /// </summary>
    public static void RequestSeat(Player keeper, AvatarState a)
    {
        if (keeper == null)
            return;

        if (!CheckSeat(keeper, a, out _, out var refusal))
        {
            Tell(keeper, refusal);
            return;
        }

        var avatarId = a.AvatarId;
        var version = a.Version;
        var keeperGuid = keeper.Guid.Full;

        var confirmation = new Confirmation_Choice(keeper.Guid, () => WorldEnqueue(() =>
        {
            var k = OnlinePlayer(keeperGuid);
            if (k == null)
                return;

            if (!AvatarManager.CurrentTuning().Enabled)
            {
                Tell(k, AvatarSeatRules.SwitchedOffMessage);
                return;
            }

            if (TrySeat(k, AvatarManager.Get(avatarId), version, out var reason))
                Tell(k, AvatarSeatRules.SeatedMessage);
            else
                Tell(k, $"The Avatar was not seated: {Lower(reason)}");
        }), null, DateTime.UtcNow);

        if (!SendConfirmation(keeper, confirmation, AvatarSeatRules.SeatPrompt(AvatarManager.CurrentTuning())))
            Tell(keeper, "You are already answering another question.");
    }

    /// <summary>Seats the Avatar where the Keeper stands, after every check. Called after the popup.</summary>
    public static bool TrySeat(Player keeper, AvatarState a, out string refusal) => TrySeat(keeper, a, null, out refusal);

    /// <summary>
    /// <see cref="TrySeat(Player, AvatarState, out string)"/> gated on the Version the popup was shown with. On success the
    /// Seat and SeatedAtUtc are stored (Version bumps, the row is dirty), the old seated NPC goes, and the new one is
    /// spawned at once when its landblock is loaded.
    /// </summary>
    internal static bool TrySeat(Player keeper, AvatarState a, int? expectedVersion, out string refusal)
    {
        if (!CheckSeat(keeper, a, out var site, out refusal))
            return false;

        if (expectedVersion.HasValue && a.Version != expectedVersion.Value)
        {
            refusal = AvatarSeatRules.SeatChangedMessage;
            return false;
        }

        var seat = new AvatarSeat(site.HouseGuid, new Position(site.Location));
        var now = ACE.Common.Time.GetDateTimeFromTimestamp(AvatarManager.NowUnix());

        if (!AvatarManager.TrySetSeat(a.AvatarId, seat, now, expectedVersion))
        {
            refusal = AvatarSeatRules.SeatChangedMessage;
            return false;
        }

        DespawnSeated(a.AvatarId);
        EnsureSeatedSpawn(a.AvatarId, seat);

        refusal = null;
        return true;
    }

    /// <summary>Every seat check: Bound, the Keeper, a mansion at hand, on its grounds, owned by the monarch or the Keeper, and the cooldown.</summary>
    private static bool CheckSeat(Player keeper, AvatarState a, out SeatSite site, out string refusal)
    {
        site = default;

        if (keeper == null || a == null || a.State != AvatarBindState.Bound || !a.MonarchGuid.HasValue)
        {
            refusal = AvatarSeatRules.NoAvatarMessage;
            return false;
        }

        if (!AvatarRights.IsKeeper(keeper, a))
        {
            refusal = AvatarSeatRules.SeatNotKeeperMessage;
            return false;
        }

        var found = SeatSiteOf(keeper);
        if (found == null || found.Value.Type != HouseType.Mansion || found.Value.Location == null)
        {
            refusal = AvatarSeatRules.SeatNoMansionMessage;
            return false;
        }

        site = found.Value;

        if (!site.OnProperty)
        {
            refusal = AvatarSeatRules.SeatNotOnPropertyMessage;
            return false;
        }

        if (!AvatarSeatRules.Qualifies(site.Type, site.Owner, a.MonarchGuid.Value, a.KeeperGuid))
        {
            refusal = AvatarSeatRules.SeatNotQualifiedMessage;
            return false;
        }

        var t = AvatarManager.CurrentTuning();
        var now = ACE.Common.Time.GetDateTimeFromTimestamp(AvatarManager.NowUnix());
        if (!AvatarSeatRules.CanReseat(a.SeatedAtUtc, now, t))
        {
            refusal = AvatarSeatRules.ReseatCooldownMessage(AvatarSeatRules.DaysUntilReseat(a.SeatedAtUtc, now, t));
            return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>Landblock.Init (on the landblock's action queue): one seated NPC per Bound Avatar seated on this landblock.</summary>
    public static void SpawnSeated(Landblock lb)
    {
        if (lb == null)
            return;

        SpawnSeated(lb.Id.Landblock, lb.Instance, npc => lb.AddWorldObject(npc));
    }

    /// <summary>
    /// The spawn with the landblock's add injected. Skips an Avatar whose NPC for the same Seat is already standing;
    /// replaces one left over from an older Seat. Returns how many NPCs were added.
    /// </summary>
    internal static int SpawnSeated(ushort landblock, uint instance, Func<AvatarVendor, bool> add)
    {
        var added = 0;

        foreach (var (avatarId, seat) in AvatarManager.SeatsOn(landblock, instance))
        {
            lock (gate)
            {
                if (seated.TryGetValue(avatarId, out var current) && !current.Npc.IsDestroyed && ReferenceEquals(current.Seat, seat))
                    continue;
            }

            var npc = Build(avatarId);
            if (npc == null)
                continue;

            npc.Location = new Position(seat.Position);
            npc.Home = new Position(seat.Position);
            npc.IsSeated = true;

            bool ok;
            try
            {
                ok = add(npc);
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] Seated spawn of Avatar {avatarId} at 0x{seat.Position.Cell:X8} threw.", ex);
                ok = false;
            }

            if (!ok)
            {
                npc.Destroy();
                continue;
            }

            (AvatarVendor Npc, AvatarSeat Seat) previous;
            bool hadPrevious;
            lock (gate)
            {
                hadPrevious = seated.TryGetValue(avatarId, out previous);
                seated[avatarId] = (npc, seat);
            }

            if (hadPrevious && previous.Npc != npc)
                previous.Npc.Destroy();

            AvatarLevelUp.SyncNpc(npc, avatarId);

            added++;
        }

        return added;
    }

    /// <summary>Re-checks the Seat after a state change: keep (or bring back) the seated NPC, or unseat.</summary>
    public static void RevalidateSeat(AvatarState state)
    {
        RevalidateSeatSink?.Invoke(state);
        Revalidate(state);
    }

    /// <summary>AvatarManager.Tick's 10-minute re-check: every Avatar holding a Seat is revalidated.</summary>
    internal static void RecheckSeats()
    {
        foreach (var a in AvatarManager.SeatedAvatars())
        {
            try
            {
                Revalidate(a);
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] Seat re-check of Avatar {a.AvatarId} failed.", ex);
            }
        }
    }

    private static void Revalidate(AvatarState a)
    {
        var seat = a?.Seat;
        if (seat == null)
            return;

        if (a.State != AvatarBindState.Bound || !a.MonarchGuid.HasValue)
        {
            Unseat(a, "the Avatar is no longer bound");
            return;
        }

        // Before the first Keeper settle after a load, KeeperGuid is not known yet: never unseat on a guess.
        if (!a.KeeperResolved)
            return;

        var monarch = a.MonarchGuid.Value;
        var owner = AvatarSeatRules.OwnerAmong(seat.HouseGuid, monarch, a.KeeperGuid, HouseInstanceOf);

        // The Seat was stored only after Qualifies passed on the real house, and a house's type never changes.
        if (!AvatarSeatRules.Qualifies(HouseType.Mansion, owner, monarch, a.KeeperGuid))
        {
            Unseat(a, "the mansion is no longer the monarch's or the Keeper's");
            return;
        }

        EnsureSeatedSpawn(a.AvatarId, seat);
    }

    /// <summary>When the Seat's landblock is loaded and no NPC stands for this Seat, queues the spawn onto that landblock.</summary>
    private static void EnsureSeatedSpawn(uint avatarId, AvatarSeat seat)
    {
        lock (gate)
        {
            if (seated.TryGetValue(avatarId, out var current) && !current.Npc.IsDestroyed && ReferenceEquals(current.Seat, seat))
                return;
        }

        var lb = LoadedLandblockOf(seat.Position);
        if (lb == null)
            return;

        lb.EnqueueAction(new ActionEventDelegate(() =>
        {
            try
            {
                SpawnSeated(lb);
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] Seated spawn on landblock 0x{lb.Id.Landblock:X4} failed.", ex);
            }
        }));
    }

    /// <summary>Returns the Avatar to wandering: the seated NPC goes and the Seat is cleared. Level, Favor and ledger are untouched.</summary>
    public static void Unseat(AvatarState state) => Unseat(state, null);

    /// <summary>
    /// Returns the Avatar to wandering: the Seat is cleared (Version bumps, the row is dirty), the seated NPC goes, and the
    /// allegiance is told "The Avatar has left &lt;house&gt; and wanders again.". Level, Favor and the ledger are untouched.
    /// </summary>
    public static void Unseat(AvatarState state, string reason)
    {
        UnseatSink?.Invoke(state);

        if (state == null)
            return;

        var old = AvatarManager.TryClearSeat(state.AvatarId);
        DespawnSeated(state.AvatarId);

        if (old == null)
            return;

        log.Info($"[AVATAR] Avatar {state.AvatarId} unseated from house 0x{old.HouseGuid:X8}{(reason == null ? "" : $": {reason}")}.");

        if (state.MonarchGuid is uint monarch)
            AvatarAnnouncer.ToAllegiance(monarch, AvatarSeatRules.UnseatLine(PlaceName(old)));
    }

    /// <summary>
    /// A house was lost (rent eviction or /house abandon): every Avatar seated there is unseated. Safe to call from any
    /// thread and never throws; the work runs on the world thread.
    /// </summary>
    public static void OnHouseLost(uint houseGuid)
    {
        try
        {
            if (!AvatarManager.Enabled)
                return;

            WorldEnqueue(() =>
            {
                try
                {
                    foreach (var a in AvatarManager.SeatedAvatars().Where(s => s.Seat?.HouseGuid == houseGuid))
                        Unseat(a, "the mansion was lost");
                }
                catch (Exception ex)
                {
                    log.Error($"[AVATAR] Unseat after the loss of house 0x{houseGuid:X8} failed.", ex);
                }
            });
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] OnHouseLost(0x{houseGuid:X8}) failed.", ex);
        }
    }

    private static void DespawnSeated(uint avatarId)
    {
        (AvatarVendor Npc, AvatarSeat Seat) entry;
        lock (gate)
        {
            if (!seated.Remove(avatarId, out entry))
                return;
        }

        entry.Npc.Destroy();
    }

    // ---- redress (ruling R7) --------------------------------------------------------------------------------------

    /// <summary>What a Redress did, for the Keeper's chat line.</summary>
    /// <param name="Replaced">NPCs replaced: the summoned one (replaced inline) plus the seated one (queued on its landblock, so its add has not run yet).</param>
    /// <param name="SummonEnded">True when the summoned replacement could be placed neither at nor around the old spot, so the summon is gone.</param>
    internal readonly record struct RedressResult(int Replaced, bool SummonEnded);

    /// <summary>
    /// After a look change: REPLACES each live NPC of the Avatar with a fresh one built (and so dressed) through Build, at
    /// the old NPC's position and heading (rulings R7, R10).
    ///
    /// PRECONDITION: call on the WORLD THREAD (Task 5's Yes handler marshals there, as RequestSeat does). The summoned
    /// replacement runs INLINE, so the result can say whether the summon ended; it keeps the original expiry and takes the
    /// old NPC's slot. Called off the world thread (detected through <see cref="OnWorldThread"/>), it logs an error and
    /// still runs; it never throws. The seated replacement is QUEUED on its landblock's action queue and takes the seated
    /// slot there (Home stays the Seat).
    ///
    /// A summoned replacement refused at the exact spot tries the summon placement ladder around it, and the summon ends
    /// only if that fails too; a seated replacement that will not add is retried once through SpawnSeated at the Seat. A
    /// Build failure keeps the old NPC. A slot that changed before the work runs (a new summon, a re-seat, an expiry) is
    /// left alone, and an NPC already gone is left to the normal spawn paths.
    /// </summary>
    internal static RedressResult Redress(uint avatarId)
    {
        var replaced = 0;
        var summonEnded = false;

        try
        {
            if (!OnWorldThread())
                log.Error($"[AVATAR] Redress of Avatar {avatarId} called off the world thread; it must run on the world thread. Running it here anyway.{Environment.NewLine}{Environment.StackTrace}");

            AvatarVendor summonedNpc = null;
            AvatarVendor seatedNpc = null;
            AvatarSeat seat = null;
            lock (gate)
            {
                if (summoned.TryGetValue(avatarId, out var s) && !s.Npc.IsDestroyed)
                    summonedNpc = s.Npc;
                if (seated.TryGetValue(avatarId, out var e) && !e.Npc.IsDestroyed)
                    (seatedNpc, seat) = e;
            }

            if (summonedNpc != null)
            {
                var outcome = SummonOutcome.Kept;
                Guarded(avatarId, "summoned", () => outcome = ReplaceSummoned(avatarId, summonedNpc));
                if (outcome == SummonOutcome.Replaced)
                    replaced++;
                summonEnded = outcome == SummonOutcome.Ended;
            }

            if (seatedNpc != null)
            {
                if (SeatLandblockEnqueue(seat.Position, add => Guarded(avatarId, "seated", () => ReplaceSeated(avatarId, seatedNpc, seat, add))))
                    replaced++;
                else
                    log.Warn($"[AVATAR] Redress of Avatar {avatarId}: the Seat's landblock 0x{seat.Position.LandblockShort:X4} is not loaded; the seated NPC was not replaced.");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Redress of Avatar {avatarId} failed.", ex);
        }

        return new RedressResult(replaced, summonEnded);
    }

    private enum SummonOutcome { Kept, Replaced, Ended }

    private static void Guarded(uint avatarId, string which, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Redress of Avatar {avatarId}'s {which} NPC threw.", ex);
        }
    }

    /// <summary>
    /// World thread. Swaps the summoned NPC for a freshly dressed one in the same spot, keeping its expiry. Kept when the
    /// old NPC stays (or the slot moved on to another NPC), Ended when no replacement could be placed.
    /// </summary>
    private static SummonOutcome ReplaceSummoned(uint avatarId, AvatarVendor old)
    {
        double expiresAt;
        lock (gate)
        {
            if (!summoned.TryGetValue(avatarId, out var current) || current.Npc != old || old.IsDestroyed)
                return SummonOutcome.Kept;
            expiresAt = current.ExpiresAtUnix;
        }

        if (old.Location == null)
            return SummonOutcome.Kept;

        var at = new Position(old.Location);
        var npc = Build(avatarId);
        if (npc == null)
            return SummonOutcome.Kept; // the old NPC stays, in its old look

        // The old NPC goes first, so the new one never has to fit beside it.
        old.Destroy();

        var placed = TryPlaceReplacement(avatarId, "at", SummonReplacer, npc, at)
            || TryPlaceReplacement(avatarId, "around", SummonFallbackPlacer, npc, at);

        if (!placed)
        {
            npc.Destroy();
            lock (gate)
            {
                if (summoned.TryGetValue(avatarId, out var current) && current.Npc == old)
                    summoned.Remove(avatarId);
            }
            log.Warn($"[AVATAR] Redress of Avatar {avatarId}: the summoned replacement could not be placed at or around 0x{at.Cell:X8}; the summon ends.");
            return SummonOutcome.Ended;
        }

        bool won;
        lock (gate)
        {
            won = summoned.TryGetValue(avatarId, out var current) && current.Npc == old;
            if (won)
                summoned[avatarId] = (npc, expiresAt);
        }

        if (!won)
        {
            // Another thread changed the slot meanwhile (a new summon or the expiry): that NPC, or its absence, stands.
            npc.Destroy();
            return SummonOutcome.Kept;
        }

        // As TrySummon: a level-up dispatched between Build and the swap could not see this NPC (or hit the old one).
        AvatarLevelUp.SyncNpc(npc, avatarId);

        ScheduleExpiry(npc, Math.Max(1.0, expiresAt - AvatarManager.NowUnix()));
        return SummonOutcome.Replaced;
    }

    private static bool TryPlaceReplacement(uint avatarId, string where, Func<AvatarVendor, Position, bool> placer, AvatarVendor npc, Position at)
    {
        try
        {
            return placer(npc, new Position(at));
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Redress of Avatar {avatarId}: placing the summoned replacement {where} 0x{at.Cell:X8} threw.", ex);
            return false;
        }
    }

    /// <summary>The Seat's landblock action queue. Swaps the seated NPC for a freshly dressed one where the old one stood.</summary>
    private static void ReplaceSeated(uint avatarId, AvatarVendor old, AvatarSeat seat, Func<AvatarVendor, bool> add)
    {
        lock (gate)
        {
            if (!seated.TryGetValue(avatarId, out var current) || current.Npc != old || old.IsDestroyed)
                return;
        }

        var npc = Build(avatarId);
        if (npc == null)
            return; // the old NPC stays, in its old look

        npc.Location = new Position(old.Location ?? seat.Position);
        npc.Home = new Position(seat.Position);
        npc.IsSeated = true;

        old.Destroy();

        bool ok;
        try
        {
            ok = add(npc);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Redress of Avatar {avatarId}: adding the seated replacement at 0x{npc.Location.Cell:X8} threw.", ex);
            ok = false;
        }

        if (!ok)
        {
            npc.Destroy();

            // Already on the Seat's landblock queue: one more try through the ordinary seated spawn, at the Seat itself.
            // SpawnSeated replaces the slot (it still names the destroyed NPC). If that fails too, the next Seat re-check
            // (EnsureSeatedSpawn) brings one back.
            var again = SpawnSeated((ushort)seat.Position.LandblockShort, seat.Position.Instance, add);
            if (SeatedNpc(avatarId) == null)
                log.Warn($"[AVATAR] Redress of Avatar {avatarId}: the seated replacement could not be added, nor respawned at the Seat ({again} added); the Seat re-check will respawn it.");
            return;
        }

        bool won;
        lock (gate)
        {
            won = seated.TryGetValue(avatarId, out var current) && current.Npc == old;
            if (won)
                seated[avatarId] = (npc, seat);
        }

        if (!won)
        {
            npc.Destroy();
            return;
        }

        // As SpawnSeated (which also covers the retry above): catch a level-up dispatched before the swap.
        AvatarLevelUp.SyncNpc(npc, avatarId);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds an NPC, binds it to the Avatar and dresses it in the Avatar's cached look (the default look when never
    /// dressed), all BEFORE it can enter the world. Null (and nothing leaked) when the build or the dress fails.
    /// </summary>
    private static AvatarVendor Build(uint avatarId)
    {
        WorldObject wo;
        try
        {
            wo = NpcBuilder();
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Building the Avatar NPC for Avatar {avatarId} threw.", ex);
            return null;
        }

        if (wo is not AvatarVendor npc)
        {
            log.Error($"[AVATAR] wcid {AvatarVendor.AvatarWcid} did not build an AvatarVendor (got {wo?.GetType().Name ?? "null"}); is PropertyBool.AllegianceAvatarVendor missing from the weenie?");
            wo?.Destroy();
            return null;
        }

        npc.AvatarId = avatarId;

        // Hand-placed with no generator and no Lifespan, so without this the landblock heartbeat rots it after 300 s
        // (WorldObject.IsDecayable). The seated NPC then vanished until the 10-minute seat re-check respawned it.
        npc.TimeToRot = -1;

        try
        {
            // Memory only (AvatarState.Look): no spawn path reads the database.
            AvatarLookApplier.Apply(npc, AvatarManager.Get(avatarId)?.Look);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Dressing the Avatar NPC for Avatar {avatarId} threw.", ex);
            npc.Destroy();
            return null;
        }

        // After the dress: Apply rewrites body properties from the stored look, and the level must win over anything it holds.
        SetSpawnLevel(npc, avatarId);
        return npc;
    }

    /// <summary>
    /// Gives the NPC the Avatar's real current level instead of the weenie's fixed 7 (clamped to at least 1, see
    /// <see cref="AvatarLevelUp.LiveLevel"/>). Left at the weenie value when the Avatar is missing.
    /// </summary>
    private static void SetSpawnLevel(AvatarVendor npc, uint avatarId)
    {
        var level = AvatarLevelUp.LiveLevel(avatarId);
        if (level.HasValue)
            npc.Level = level.Value;
    }

    /// <summary>"the mansion at 12.3N, 45.6E" for a Seat on the mansion's own landblock, else "its mansion".</summary>
    internal static string PlaceName(AvatarSeat seat)
    {
        var p = seat?.Position;
        if (p == null)
            return "its mansion";

        var houseLandblock = (seat.HouseGuid >> 12) & 0xFFFF;
        if (houseLandblock != p.LandblockShort)
            return "its mansion";

        var outdoor = new Position(p) { LandblockId = new LandblockId(p.GetOutdoorCell()) };
        var coords = outdoor.GetMapCoordStr();
        return coords == null ? "its mansion" : $"the mansion at {coords}";
    }

    private static string Lower(string s) => string.IsNullOrEmpty(s) ? "it could not be done." : char.ToLowerInvariant(s[0]) + s.Substring(1) + (s.EndsWith(".") ? "" : ".");

    // ---- production defaults --------------------------------------------------------------------------------------

    private static WorldObject DefaultNpcBuilder() => WorldObjectFactory.CreateNewWorldObject(AvatarVendor.AvatarWcid);

    private static bool DefaultSummonPlacer(AvatarVendor npc, Player summoner) =>
        MuleSummonHandler.TryPlaceVendor(npc, summoner, location => AvatarSeatRules.IsPlacementRungPermitted(location, IsDenylisted));

    private static bool DefaultSummonerState(Player summoner, out string refusal) =>
        MuleSummonHandler.TrySummonerState(summoner, out refusal, AvatarSeatRules.SummonDeadMessage);

    private static bool DefaultIsDenylisted(Position location) => MuleSummonHandler.GetDenylistLandblocks().Contains(location);

    private static void DefaultScheduleExpiry(AvatarVendor npc, double seconds)
    {
        var chain = new ActionChain();
        chain.AddDelaySeconds(seconds);
        chain.AddAction(npc, () => ExpireSummoned(npc));
        chain.EnqueueChain();
    }

    private static uint? DefaultHouseInstanceOf(uint guid) => PlayerManager.FindByGuid(guid)?.HouseInstance;

    private static SeatSite? DefaultSeatSiteOf(Player keeper)
    {
        var houseGuid = keeper.GetHouseInstance();
        var house = keeper.House;

        if (house == null || (houseGuid.HasValue && house.Guid.Full != houseGuid.Value))
        {
            // Only a house already in a loaded landblock: the Keeper is standing on it, so nothing is loaded from the database.
            house = null;
            if (houseGuid.HasValue)
            {
                var landblock = (houseGuid.Value >> 12) & 0xFFFF;
                var loaded = LandblockManager.GetLoadedLandblock(new LandblockId(landblock << 16 | 0xFFFF), 0);
                house = loaded?.GetObject(new ObjectGuid(houseGuid.Value)) as House;
            }
        }

        if (house == null || keeper.Location == null)
            return null;

        return new SeatSite(house.Guid.Full, house.HouseType, house.HouseOwner, house.OnProperty(keeper), new Position(keeper.Location));
    }

    private static Landblock DefaultLoadedLandblockOf(Position p) => p == null ? null : LandblockManager.GetLoadedLandblock(p.LandblockId, p.Instance);

    private static void DefaultWorldEnqueue(Action work) => WorldManager.EnqueueAction(new ActionEventDelegate(work));

    private static bool DefaultOnWorldThread() => WorldManager.IsOnWorldThreadOrUnknown;

    private static bool DefaultSummonReplacer(AvatarVendor npc, Position at)
    {
        npc.Location = new Position(at);
        npc.Location.LandblockId = new LandblockId(npc.Location.GetCell());
        // A vendor's Home must be live before EnterWorld (MuleSummonHandler.TryPlace, R10: ApproachVendor reads Home.Pos).
        npc.Home = new Position(npc.Location);
        return npc.EnterWorld();
    }

    private static bool DefaultSummonFallbackPlacer(AvatarVendor npc, Position around)
    {
        // One body width and a margin off the refused spot; the ladder then closes in to 0.6x, 0.3x and the spot itself.
        var spawnDist = 2f * MuleSummonHandler.GetVendorRadius(npc) + 0.5f;

        return MuleSummonHandler.TryPlace(npc, around, spawnDist, wo =>
        {
            wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
            if (!AvatarSeatRules.IsPlacementRungPermitted(wo.Location, IsDenylisted))
                return false;
            return wo.EnterWorld();
        });
    }

    private static bool DefaultSeatLandblockEnqueue(Position p, Action<Func<AvatarVendor, bool>> work)
    {
        var lb = LoadedLandblockOf(p);
        if (lb == null)
            return false;

        lb.EnqueueAction(new ActionEventDelegate(() => work(npc => lb.AddWorldObject(npc))));
        return true;
    }

    private static bool DefaultSendConfirmation(Player p, Confirmation c, string text) => p.ConfirmationManager.EnqueueSend(c, text);

    private static void DefaultTell(Player p, string text) => p.SendMessage(text);

    private static Player DefaultOnlinePlayer(uint guid) => PlayerManager.GetOnlinePlayer(guid);
}
