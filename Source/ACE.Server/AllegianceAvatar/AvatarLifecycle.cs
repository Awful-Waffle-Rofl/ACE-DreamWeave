using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>
/// What a swear popup was decided on: both Avatars and their Versions, and the target allegiance. Re-checked when the
/// patron confirms; any difference cancels the swear.
/// </summary>
public sealed record AvatarSnapshot(uint? SwearerAvatarId, int SwearerVersion, uint? TargetAvatarId, int TargetVersion, uint TargetMonarchGuid);

/// <summary>A classified swear: the effect, the snapshot to re-check, the swearing monarch's popup text and the patron's line.</summary>
public sealed record AvatarSwearPlan(SwearAvatarEffect Effect, AvatarSnapshot Snapshot, string SwearerText, string PatronLine);

/// <summary>A classified break or boot. AvatarId is null when the allegiance it leaves has no Bound Avatar.</summary>
public sealed record AvatarBreakGate(BreakAvatarEffect Effect, uint? AvatarId, int Version, uint OldMonarchGuid, uint LeavingTopGuid, string Text);

/// <summary>A monarch delete, planned on the pre-split tree. KeeperGuid null means nobody is left: retire.</summary>
public sealed record AvatarDeletePlan(uint AvatarId, int Version, uint DeletedGuid, string DeletedName, uint? KeeperGuid);

/// <summary>What the recompute's binding repair finds for a Bound Avatar's monarch guid.</summary>
public enum AvatarMonarchStatus
{
    /// <summary>Still leads (or is solo): the binding is right.</summary>
    Monarch,

    /// <summary>Has sworn under someone: the Avatar belongs to that player's current allegiance.</summary>
    Vassal,

    /// <summary>The character record is gone or deleted: the monarch-delete plan applies.</summary>
    Gone,
}

/// <summary>
/// The Avatar lifecycle: swear (adopt or absorb), break (inheritance or inactive), monarch delete (inherit or
/// retire), the Keeper and the heir. Classification is AvatarLifecycleRules; the binding writes are AvatarManager's
/// lifecycle primitives, each under the manager lock with a Version bump and a dirty mark for the next flush. After a
/// write, and outside the lock, every change despawns the SUMMONED NPC, revalidates the Seat and is announced to the
/// allegiance. THREADS. On the world thread: the Tick, DoHandlePlayerDelete, HandleAllegianceOnLogin, the break, boot
/// and officer handlers, and the plain Confirmation_SwearAllegiance confirm. On the player's action queue (a
/// landblock-group thread): Confirmation_Choice callbacks and the unconfirmed swear (the MoveTo chain); the Choice Yes
/// paths hand their re-check, the allegiance change and the Complete* call to the world thread with
/// WorldManager.EnqueueAction. The popups themselves live in Player_Allegiance.
/// </summary>
public static class AvatarLifecycle
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const double SecondsPerDay = 86400.0;

    /// <summary>Test seam: a character's name, or null when unknown.</summary>
    internal static Func<uint, string> NameOf = DefaultNameOf;

    /// <summary>Test seam: the monarch a character ends up under, from the live tree (itself when it leads or is solo).</summary>
    internal static Func<uint, uint> MonarchOf = DefaultMonarchOf;

    internal static void ResetSeams()
    {
        NameOf = DefaultNameOf;
        MonarchOf = DefaultMonarchOf;
    }

    private static string DefaultNameOf(uint guid) => PlayerManager.FindByGuid(guid)?.Name;

    private static uint DefaultMonarchOf(uint guid)
    {
        var player = PlayerManager.FindByGuid(guid);
        return player == null ? guid : AllegianceManager.GetMonarch(player)?.Guid.Full ?? guid;
    }

    private static string Name(uint guid, string fallback)
    {
        try
        {
            return NameOf?.Invoke(guid) ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static int Percent(AvatarTuning t) => (int)Math.Round(Math.Clamp(t.AbsorbFraction, 0.0, 1.0) * 100.0);

    // ---- swear -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Classifies a swear and builds its popup texts. Null when the manager is disabled. Effect None means the plain
    /// swear, untouched.
    /// </summary>
    public static AvatarSwearPlan PlanSwear(uint swearerGuid, bool swearerIsMonarch, string swearerName, uint targetMonarchGuid)
    {
        if (!AvatarManager.Enabled)
            return null;

        var swearer = AvatarManager.ForMonarch(swearerGuid);
        var target = AvatarManager.ForMonarch(targetMonarchGuid);
        var effect = AvatarLifecycleRules.ClassifySwear(swearerIsMonarch, swearer, target);

        if (effect == SwearAvatarEffect.None)
            return new AvatarSwearPlan(SwearAvatarEffect.None, null, null, null);

        var t = AvatarManager.CurrentTuning();
        var level = swearer.Level(t);
        var targetName = Name(targetMonarchGuid, "the other monarch");
        var snapshot = new AvatarSnapshot(swearer.AvatarId, swearer.Version,
            effect == SwearAvatarEffect.Absorb ? target.AvatarId : null,
            effect == SwearAvatarEffect.Absorb ? target.Version : 0,
            targetMonarchGuid);

        if (effect == SwearAvatarEffect.Absorb)
        {
            return new AvatarSwearPlan(effect, snapshot,
                $"Your Avatar (level {level}) will be absorbed into {targetName}'s at {Percent(t)}%. This can't be undone.",
                $"{swearerName}'s Avatar (level {level}) will be absorbed into {targetName}'s at {Percent(t)}%. This can't be undone.");
        }

        return new AvatarSwearPlan(effect, snapshot,
            $"Your Avatar will become {targetName}'s allegiance's Avatar.",
            $"{swearerName}'s Avatar will become {targetName}'s allegiance's Avatar.");
    }

    /// <summary>True when both Avatars, their Versions and the target allegiance's Avatar are as the snapshot recorded.</summary>
    public static bool StillMatches(AvatarSnapshot s) => StillMatches(s, out _);

    public static bool StillMatches(AvatarSnapshot s, out string reason)
    {
        reason = null;
        if (s == null)
            return true;

        if (s.SwearerAvatarId.HasValue)
        {
            var swearer = AvatarManager.Get(s.SwearerAvatarId.Value);
            if (swearer == null || swearer.State != AvatarBindState.Bound || swearer.Version != s.SwearerVersion)
            {
                reason = "the swearing monarch's Avatar has changed";
                return false;
            }
        }

        var target = AvatarManager.ForMonarch(s.TargetMonarchGuid);
        if (target?.AvatarId != s.TargetAvatarId || (target != null && target.Version != s.TargetVersion))
        {
            reason = "the Avatar of the allegiance being joined has changed";
            return false;
        }

        if (target == null && AvatarManager.FoundingPending(s.TargetMonarchGuid))
        {
            reason = "the allegiance being joined is founding its Avatar, so try again in a moment";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The gate at the patron's confirm, before any property changes. With a snapshot: the target allegiance and both
    /// Avatars must be unchanged. Without one (the plain swear popup): no Avatar may have become involved since, or
    /// the swear would skip the swearing monarch's own popup.
    /// </summary>
    public static bool CheckConfirmedSwear(AvatarSnapshot snapshot, uint swearerGuid, bool swearerIsMonarch, string swearerName, uint currentTargetMonarchGuid, out string reason)
    {
        reason = null;
        if (!AvatarManager.Enabled)
            return true;

        if (snapshot == null)
        {
            var plan = PlanSwear(swearerGuid, swearerIsMonarch, swearerName, currentTargetMonarchGuid);
            if (plan != null && plan.Effect != SwearAvatarEffect.None)
            {
                reason = "an Avatar is now involved, so the swear must be offered again";
                return false;
            }
            return true;
        }

        if (snapshot.TargetMonarchGuid != currentTargetMonarchGuid)
        {
            reason = "the allegiance being joined has changed";
            return false;
        }

        return StillMatches(snapshot, out reason);
    }

    /// <summary>
    /// After the swear has happened (AllegianceManager.OnSwearAllegiance): moves the swearing monarch's Avatar, then
    /// re-resolves the Keeper. The snapshot was re-checked before the swear, and the swear cannot be taken back, so the
    /// bind does NOT re-gate on Version: a binding change landing between that check and here (an heir named, a Keeper
    /// flip) must not leave the Avatar bound to a guid that is no longer a monarch.
    /// </summary>
    public static void CompleteSwear(uint newMonarchGuid, AvatarSnapshot snapshot)
    {
        if (!AvatarManager.Enabled)
            return;

        if (snapshot?.SwearerAvatarId is uint avatarId)
            BindAfterIrreversibleStep(avatarId, newMonarchGuid, "Swear");

        RecomputeKeeper(newMonarchGuid);
    }

    /// <summary>
    /// The bind after a swear or an inheritance break. The checks that remain are that it is still the same Avatar and
    /// still Bound (Bind and the manager primitives keep both); there is no Version gate, so this IS the
    /// expectation-free bind. A refusal is logged; the only remaining cause is a founding Insert in flight for that
    /// monarch (TryRebind refuses to race the new row), which CheckConfirmedSwear already cancels for a swear.
    /// </summary>
    private static bool BindAfterIrreversibleStep(uint avatarId, uint newMonarchGuid, string what, string leavingLine = null)
    {
        var avatar = AvatarManager.Get(avatarId);
        if (avatar == null || avatar.State != AvatarBindState.Bound)
        {
            log.Warn($"[AVATAR] {what} completed but Avatar {avatarId} is no longer Bound; nothing to move.");
            return false;
        }

        if (Bind(avatar, newMonarchGuid, null, null, leavingLine: leavingLine))
            return true;

        log.Warn($"[AVATAR] {what} completed but Avatar {avatarId} could not be bound to 0x{newMonarchGuid:X8}.");
        return false;
    }

    // ---- binding ------------------------------------------------------------------------------------------------

    /// <summary>Binds <paramref name="a"/> to <paramref name="newMonarchGuid"/>, absorbing it when that allegiance already has a Bound Avatar.</summary>
    public static bool Bind(AvatarState a, uint newMonarchGuid) => Bind(a, newMonarchGuid, null, null);

    internal static bool Bind(AvatarState a, uint newMonarchGuid, int? expectedVersion, int? expectedTargetVersion, string arrivingLine = null, string leavingLine = null)
    {
        if (a == null || !AvatarManager.Enabled)
            return false;

        var target = AvatarManager.ForMonarch(newMonarchGuid);
        if (target != null && target.AvatarId != a.AvatarId)
            return Absorb(a, target, expectedVersion, expectedTargetVersion);

        var oldMonarch = a.MonarchGuid;
        if (oldMonarch == newMonarchGuid)
            return true;

        var level = a.Level(AvatarManager.CurrentTuning());
        if (!AvatarManager.TryRebind(a.AvatarId, newMonarchGuid, expectedVersion))
            return false;

        AfterChange(a);

        if (leavingLine != null && oldMonarch.HasValue)
            AvatarAnnouncer.ToAllegiance(oldMonarch.Value, leavingLine);
        AvatarAnnouncer.ToAllegiance(newMonarchGuid, arrivingLine ?? $"The Avatar (level {level}) now belongs to {Name(newMonarchGuid, "your monarch")}'s allegiance.");
        return true;
    }

    /// <summary>Absorbs <paramref name="source"/> into <paramref name="target"/>: absorb_fraction of its Avatar XP, its Favor lost.</summary>
    public static bool Absorb(AvatarState source, AvatarState target) => Absorb(source, target, null, null);

    internal static bool Absorb(AvatarState source, AvatarState target, int? expectedSourceVersion, int? expectedTargetVersion)
    {
        if (source == null || target == null || !AvatarManager.Enabled)
            return false;

        var sourceMonarch = source.MonarchGuid;
        var level = source.Level(AvatarManager.CurrentTuning());

        var added = AvatarManager.TryAbsorb(source.AvatarId, target.AvatarId, expectedSourceVersion, expectedTargetVersion);
        if (added == null)
            return false;

        AfterChange(source);
        AfterChange(target);

        var sourceName = sourceMonarch.HasValue ? Name(sourceMonarch.Value, "Another monarch") : "Another monarch";
        if (target.MonarchGuid is uint targetMonarch)
            AvatarAnnouncer.ToAllegiance(targetMonarch, $"{sourceName}'s Avatar (level {level}) has been absorbed into this allegiance's Avatar, adding {added.Value} Avatar XP.");
        return true;
    }

    /// <summary>Retires a Bound Avatar whose monarch is gone with nobody to inherit it.</summary>
    public static bool Retire(AvatarState a) => Retire(a, null);

    internal static bool Retire(AvatarState a, int? expectedVersion)
    {
        if (a == null || !AvatarManager.Enabled)
            return false;

        var monarch = a.MonarchGuid;
        var level = a.Level(AvatarManager.CurrentTuning());
        if (!AvatarManager.TryRetire(a.AvatarId, expectedVersion))
            return false;

        AfterChange(a);
        if (monarch.HasValue)
            AvatarAnnouncer.ToAllegiance(monarch.Value, $"The Avatar (level {level}) has been retired: nobody is left to inherit it.");
        return true;
    }

    /// <summary>Names the heir the Keeper rules try first while the monarch is idle, then re-resolves the Keeper.</summary>
    public static bool SetHeir(AvatarState a, uint heirGuid)
    {
        if (a == null || !AvatarManager.Enabled || !a.MonarchGuid.HasValue)
            return false;

        var monarch = a.MonarchGuid.Value;
        var unchanged = a.HeirGuid == heirGuid;
        if (!AvatarManager.TrySetHeir(a.AvatarId, heirGuid, null))
            return false;
        if (unchanged)
            return true;

        // Naming an heir moves no binding a summon depends on, so the summoned NPC stays; only the Seat is revalidated.
        // A Keeper change that the heir causes still despawns it, through RecomputeKeeper below.
        try
        {
            AvatarNpcs.RevalidateSeat(a);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Seat revalidation after naming an heir for Avatar {a.AvatarId} failed.", ex);
        }
        AvatarAnnouncer.ToAllegiance(monarch, $"{Name(heirGuid, "A member")} is now heir to the Avatar.");
        RecomputeKeeper(monarch);
        return true;
    }

    // ---- the Keeper ---------------------------------------------------------------------------------------------

    /// <summary>Re-resolves the Keeper of the Avatar bound to <paramref name="monarchGuid"/> from its live roster.</summary>
    public static void RecomputeKeeper(uint monarchGuid)
    {
        if (!AvatarManager.Enabled)
            return;

        RecomputeKeeper(monarchGuid, AvatarManager.RosterFor(monarchGuid));
    }

    /// <summary>The Tick's per-roster step and the hooks' common path. A change despawns the summoned NPC, revalidates the Seat and is announced.</summary>
    internal static void RecomputeKeeper(uint monarchGuid, AvatarRoster roster)
    {
        var change = AvatarManager.ApplyKeeper(monarchGuid, roster);
        if (change == null)
            return;

        var c = change.Value;
        AfterChange(AvatarManager.Get(c.AvatarId));

        if (!c.Announce)
            return;

        var monarchName = Name(c.MonarchGuid, "the monarch");
        AvatarAnnouncer.ToAllegiance(c.MonarchGuid, c.NowActing
            ? $"{Name(c.NewKeeper, "A member")} keeps the Avatar while {monarchName} is away."
            : $"{CapitalFirst(monarchName)} keeps the Avatar again.");
    }

    /// <summary>
    /// A login: refresh the account in the live index FIRST (pre-flight 28), then re-resolve every Avatar whose
    /// monarch is on this account (a monarch logging in on an alt gets the Keeper's rights back at once) and the
    /// Avatar of the allegiance the character is in.
    /// </summary>
    public static void OnLogin(uint? accountId, uint allegianceMonarchGuid)
    {
        if (!AvatarManager.Enabled)
            return;

        if (accountId.HasValue)
        {
            AvatarManager.TouchAccount(accountId.Value);
            foreach (var monarch in AvatarManager.MonarchsOfAccount(accountId.Value))
            {
                if (monarch != allegianceMonarchGuid)
                    RecomputeKeeper(monarch);
            }
        }

        RecomputeKeeper(allegianceMonarchGuid);
    }

    // ---- break and boot -----------------------------------------------------------------------------------------

    /// <summary>
    /// Classifies a break or boot. <paramref name="actorLeaves"/> is true only when the actor breaks from their own
    /// patron; <paramref name="leavingGuids"/> is every character in the branch that leaves. The Keeper is refreshed
    /// first, so a monarch who has just come back is never treated as idle.
    /// </summary>
    public static AvatarBreakGate PlanBreak(uint actorGuid, uint oldMonarchGuid, bool actorLeaves, uint leavingTopGuid, IReadOnlyCollection<uint> leavingGuids)
    {
        var none = new AvatarBreakGate(BreakAvatarEffect.None, null, 0, oldMonarchGuid, leavingTopGuid, null);
        if (!AvatarManager.Enabled)
            return none;

        RecomputeKeeper(oldMonarchGuid);

        var s = AvatarManager.ForMonarch(oldMonarchGuid);
        if (s == null)
            return none;

        var t = AvatarManager.CurrentTuning();
        var roster = AvatarManager.RosterFor(oldMonarchGuid);
        var leaving = new HashSet<uint>(leavingGuids ?? Array.Empty<uint>());
        var remaining = roster == null ? new List<KeeperCandidate>() : roster.Members.Where(m => !leaving.Contains(m.Guid)).ToList();
        var activeAfter = AvatarManager.ActiveAccountsAmong(remaining);
        var activeBefore = roster == null ? 0 : AvatarManager.ActiveAccountsAmong(roster.Members);

        var monarchIdle = s.KeeperIsActing;
        var actorIsKeeper = s.KeeperIsActing && s.KeeperGuid == actorGuid;
        var actorIsMonarch = actorGuid == oldMonarchGuid;

        var effect = AvatarLifecycleRules.ClassifyBreak(actorIsKeeper && actorLeaves, monarchIdle, actorIsMonarch, activeAfter, t);

        // Only a break that TAKES the allegiance below the threshold asks: one already below it changes nothing.
        if (effect == BreakAvatarEffect.GoesInactive && activeBefore < t.MinActiveAccounts)
            effect = BreakAvatarEffect.InfoStays;

        var monarchName = Name(oldMonarchGuid, "your monarch");
        string text;
        switch (effect)
        {
            case BreakAvatarEffect.Inheritance:
                text = $"{AwayLine(monarchName, roster, t)} Breaking will take the Avatar (level {s.Level(t)}) with you to your new allegiance, permanently. Continue?";
                break;
            case BreakAvatarEffect.GoesInactive:
                text = $"This leaves the allegiance with fewer than {t.MinActiveAccounts} active accounts. The Avatar will go inactive and its Favor will decay.";
                break;
            default:
                text = actorIsMonarch ? "The Avatar stays with your allegiance." : $"The Avatar stays with {monarchName}'s allegiance.";
                break;
        }

        return new AvatarBreakGate(effect, s.AvatarId, s.Version, oldMonarchGuid, leavingTopGuid, text);
    }

    private static string AwayLine(string monarchName, AvatarRoster roster, AvatarTuning t)
    {
        var seen = roster == null ? null : AvatarManager.AccountLastSeen(roster.MonarchAccountId);
        if (seen.HasValue)
        {
            var days = (long)Math.Floor(Math.Max(0.0, AvatarManager.NowUnix() - seen.Value) / SecondsPerDay);
            return $"{CapitalFirst(monarchName)} has been away {days} days.";
        }
        return $"{CapitalFirst(monarchName)} has been away more than {t.KeeperIdleDays} days.";
    }

    /// <summary>The Yes re-check: the same effect on the same Avatar at the same Version as when the popup was shown.</summary>
    public static bool BreakStillMatches(AvatarBreakGate original, AvatarBreakGate now, out string reason)
    {
        reason = null;
        if (original.Effect == now.Effect && original.AvatarId == now.AvatarId && original.Version == now.Version)
            return true;

        reason = original.Effect == BreakAvatarEffect.Inheritance && now.Effect != BreakAvatarEffect.Inheritance
            ? "you no longer hold the Avatar's Keeper rights"
            : "the Avatar has changed since you were asked";
        return false;
    }

    /// <summary>
    /// After the break has happened: Inheritance binds the Avatar to the actor (now the monarch of their own branch),
    /// GoesInactive tells the allegiance, and both sides re-resolve their Keeper.
    /// </summary>
    public static void CompleteBreak(AvatarBreakGate gate, uint actorGuid)
    {
        if (gate == null || !AvatarManager.Enabled)
            return;

        if (gate.Effect == BreakAvatarEffect.Inheritance && gate.AvatarId is uint avatarId)
        {
            // The gate was re-checked (BreakStillMatches) before the break; the break is done, so no Version gate here.
            var level = AvatarManager.Get(avatarId)?.Level(AvatarManager.CurrentTuning()) ?? 0;
            var actorName = Name(actorGuid, "The Keeper");
            BindAfterIrreversibleStep(avatarId, actorGuid, "Inheritance break",
                $"{actorName} has broken away and taken the Avatar (level {level}) to a new allegiance.");
        }
        else if (gate.Effect == BreakAvatarEffect.GoesInactive)
        {
            var t = AvatarManager.CurrentTuning();
            AvatarAnnouncer.ToAllegiance(gate.OldMonarchGuid, $"The allegiance now has fewer than {t.MinActiveAccounts} active accounts. The Avatar will go inactive and its Favor will decay.");
        }

        RecomputeKeeper(gate.OldMonarchGuid);
        if (gate.LeavingTopGuid != gate.OldMonarchGuid)
            RecomputeKeeper(gate.LeavingTopGuid);
        if (actorGuid != gate.OldMonarchGuid && actorGuid != gate.LeavingTopGuid)
            RecomputeKeeper(actorGuid);
    }

    // ---- monarch delete -----------------------------------------------------------------------------------------

    /// <summary>
    /// Before the delete splits the tree: picks who inherits, the way the Keeper rules would with the monarch idle.
    /// Null when the deleted character holds no Bound Avatar. With nobody left in the allegiance the plan retires.
    /// If no remaining member is active, the same ordering (heir, officer, rank) is applied ignoring activity, so a
    /// sleeping allegiance still inherits (builder's call: the spec retires only when no vassals are left).
    /// </summary>
    public static AvatarDeletePlan PlanMonarchDelete(uint guid)
    {
        if (!AvatarManager.Enabled)
            return null;

        var s = AvatarManager.ForMonarch(guid);
        if (s == null)
            return null;

        var name = Name(guid, "The monarch");
        var roster = AvatarManager.RosterFor(guid);
        var heirs = roster?.Members.Where(m => m.Guid != guid).ToList() ?? new List<KeeperCandidate>();
        if (heirs.Count == 0)
            return new AvatarDeletePlan(s.AvatarId, s.Version, guid, name, null);

        var preSplit = roster with { MonarchDeleted = true, Members = heirs };
        var resolved = AvatarManager.ResolveKeeper(preSplit, s.HeirGuid);
        if (resolved.IsActing)
            return new AvatarDeletePlan(s.AvatarId, s.Version, guid, name, resolved.KeeperGuid);

        var now = AvatarManager.NowUnix();
        var everyoneActive = AccountActivityIndex.Build(heirs.Select(h => (h.AccountId, now)));
        var fallback = AvatarKeeperResolver.Resolve(preSplit.MonarchGuid, preSplit.MonarchAccountId, true, s.HeirGuid, heirs, everyoneActive, now, AvatarManager.CurrentTuning());
        return new AvatarDeletePlan(s.AvatarId, s.Version, guid, name, fallback.IsActing ? fallback.KeeperGuid : heirs.OrderBy(h => h.Depth).ThenBy(h => h.Guid).First().Guid);
    }

    /// <summary>After the split: binds the Avatar to the monarch of the Keeper's new allegiance (absorbing if it has one), or retires it.</summary>
    public static void ApplyMonarchDelete(AvatarDeletePlan plan)
    {
        if (plan == null || !AvatarManager.Enabled)
            return;

        var avatar = AvatarManager.Get(plan.AvatarId);
        if (avatar == null)
            return;

        if (plan.KeeperGuid == null)
        {
            Retire(avatar, plan.Version);
            return;
        }

        var newMonarch = MonarchOf(plan.KeeperGuid.Value);
        var level = avatar.Level(AvatarManager.CurrentTuning());
        if (!Bind(avatar, newMonarch, plan.Version, null,
            arrivingLine: $"{plan.DeletedName} is gone, and the Avatar (level {level}) now belongs to {Name(newMonarch, "your monarch")}'s allegiance."))
        {
            log.Warn($"[AVATAR] Monarch 0x{plan.DeletedGuid:X8} deleted but Avatar {plan.AvatarId} could not be bound to 0x{newMonarch:X8}.");
            return;
        }

        RecomputeKeeper(newMonarch);
    }

    // ---- repair -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Production status of a monarch guid for the recompute's repair. A missing record, or an online character that is
    /// deleted, is Gone (an offline delete leaves PlayerManager through ProcessDeletedPlayer). A member's Monarch property
    /// holds the monarch's guid, the monarch's own included, so only a Monarch naming someone else makes a vassal.
    /// </summary>
    internal static AvatarMonarchStatus LiveMonarchStatus(uint guid)
    {
        var player = PlayerManager.FindByGuid(guid);
        if (player == null || (player is Player online && online.IsDeleted))
            return AvatarMonarchStatus.Gone;

        return player.MonarchId.HasValue && player.MonarchId.Value != guid ? AvatarMonarchStatus.Vassal : AvatarMonarchStatus.Monarch;
    }

    /// <summary>
    /// The recompute's repair of a binding a missed hook left wrong. A Vassal monarch's Avatar is bound to that player's
    /// current monarch (Bind absorbs when that allegiance has an Avatar, and adopts it otherwise); a Gone monarch's Avatar
    /// takes the monarch-delete plan and apply. No popup: the player already acted, and this only repairs. Skipped while a
    /// founding is in flight for either guid. True when something moved.
    /// </summary>
    internal static bool Reconcile(uint avatarId, uint monarchGuid, AvatarMonarchStatus status)
    {
        if (!AvatarManager.Enabled || status == AvatarMonarchStatus.Monarch)
            return false;

        var a = AvatarManager.Get(avatarId);
        if (a == null || a.State != AvatarBindState.Bound || a.MonarchGuid != monarchGuid || AvatarManager.FoundingPending(monarchGuid))
            return false;

        if (status == AvatarMonarchStatus.Gone)
        {
            var plan = PlanMonarchDelete(monarchGuid);
            if (plan == null)
                return false;

            log.Info($"[AVATAR] reconciled Avatar {avatarId}: monarch 0x{monarchGuid:X8} is gone, so the monarch delete runs ({(plan.KeeperGuid is uint k ? $"inherited by 0x{k:X8}" : "retired")}).");
            ApplyMonarchDelete(plan);
            return true;
        }

        var newMonarch = MonarchOf(monarchGuid);
        if (newMonarch == monarchGuid || AvatarManager.FoundingPending(newMonarch))
            return false;

        var absorbs = AvatarManager.ForMonarch(newMonarch) is AvatarState target && target.AvatarId != avatarId;
        if (!Bind(a, newMonarch))
        {
            log.Warn($"[AVATAR] reconcile of Avatar {avatarId}: 0x{monarchGuid:X8} is no longer a monarch, but the Avatar could not be bound to 0x{newMonarch:X8}.");
            return false;
        }

        log.Info($"[AVATAR] reconciled Avatar {avatarId}: 0x{monarchGuid:X8} is no longer a monarch, so it was {(absorbs ? "absorbed into" : "adopted by")} 0x{newMonarch:X8}'s allegiance.");
        RecomputeKeeper(newMonarch);
        return true;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>Every state change: the summoned NPC goes, the seated one is revalidated (respawned or unseated, never just removed).</summary>
    private static void AfterChange(AvatarState s)
    {
        if (s == null)
            return;

        try
        {
            AvatarNpcs.DespawnSummoned(s.AvatarId);
            AvatarNpcs.RevalidateSeat(s);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] NPC update after a state change of Avatar {s.AvatarId} failed.", ex);
        }
    }

    private static string CapitalFirst(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
