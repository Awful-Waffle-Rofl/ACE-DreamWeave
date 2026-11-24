using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The production <see cref="IGauntletFighterHooks"/> (plan Task 8), installed on GauntletRunHooks.Live by
/// LiveGauntletEnvironment.Build. One slot per run holds its fighter and the last stamp:
/// <list type="bullet">
/// <item>Spawn (Staging): build the fighter from wcid 1008504, dress it, and place it at the centre; it follows the highest-level living member.</item>
/// <item>Restamp (each wave start): read the Avatar's live level A (AvatarState.Level), compute the stamp from that wave's
/// plan (GauntletFighterStats) on the world thread, apply it on the arena landblock's thread. A level-up during a run takes
/// effect here, at the next restamp.</item>
/// <item>Respawn (after the respawn timer): a fresh fighter at the centre at full health with the last stamp.</item>
/// <item>Remove (Ending or close): take it out of the world, unbound first so the removal is never read as a death.</item>
/// </list>
/// A fighter's death reaches GauntletRun.OnFighterDied through its slot. Every hook is called on the world thread and does
/// its world work through <see cref="Enqueue"/>; a hook never throws into the run.
/// </summary>
internal sealed class LiveGauntletFighterHooks : IGauntletFighterHooks
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    // ---- seams (tests replace them; the defaults are the live world) ------------------------------------------------------

    /// <summary>Builds an unplaced fighter, or null when the weenie is missing.</summary>
    internal Func<GauntletAvatarFighter> Create { get; init; } = AvatarFighterFactory.CreateFromWeenie;

    /// <summary>Dresses an unplaced fighter in its Avatar's look and scale (AvatarFighterFactory.Dress), before it is placed.</summary>
    internal Action<GauntletAvatarFighter, uint> Dress { get; init; } = (f, avatarId) => AvatarFighterFactory.Dress(f, avatarId);

    /// <summary>Runs an action on the landblock holding the run's centre. False when that landblock is gone.</summary>
    internal Func<GauntletRun, Action, bool> Enqueue { get; init; } = EnqueueOnCentre;

    /// <summary>Puts a shaped fighter into the world at its Location (EnterWorld plus Init's world half). False when refused.</summary>
    internal Func<GauntletAvatarFighter, bool> Place { get; init; } = AvatarFighterFactory.PlaceAtLocation;

    /// <summary>The Avatar's current level A (0 when the Avatar is gone).</summary>
    internal Func<uint, int> AvatarLevel { get; init; } = LiveAvatarLevel;

    /// <summary>
    /// The members the fighter may follow: the run's members inside (its published snapshot), the ones held in the death
    /// pen included. A held member is a candidate who is DOWN (<see cref="IsHeld"/>, through the fighter's IsDown), so
    /// everyone dead or held is the all-down line, not an empty field. Read on the arena landblock's thread.
    /// </summary>
    internal Func<GauntletRun, IEnumerable<Player>> Participants { get; init; } = LiveParticipants;

    /// <summary>True while the character is held in a Gauntlet death pen.</summary>
    internal Func<uint, bool> IsHeld { get; init; } = guid => LiveGauntletDeathHooks.Instance.Holds.RunOf(guid) != null;

    /// <summary>Test seam: whether a member is dead.</summary>
    internal Func<Player, bool> IsDead { get; init; } = p => p.IsDead;

    /// <summary>Who hears the fighter: the run's members inside, the held ones included (they are still in the fight).</summary>
    internal Func<GauntletRun, IEnumerable<Player>> Audience { get; init; } = run => run.OnlineMembersInside();

    /// <summary>Takes a fighter out of the world.</summary>
    internal Action<GauntletAvatarFighter> Destroy { get; init; } = f => f.Destroy();

    private readonly ConcurrentDictionary<uint, Slot> slots = new ConcurrentDictionary<uint, Slot>();

    /// <summary>One run's fighter. The fighter field is written only on the arena landblock's thread.</summary>
    internal sealed class Slot : IGauntletFighterOwner
    {
        public readonly GauntletRun Run;
        public volatile GauntletAvatarFighter Fighter;
        public volatile GauntletFighterStamp LastStamp;
        public volatile bool Removed;

        /// <summary>A Spawn or Respawn placement that has not yet succeeded: <see cref="Ensure"/> retries it.</summary>
        public volatile bool PlacePending;

        /// <summary>True once any fighter of this run has been placed: a later placement is a return, not the first spawn.</summary>
        public volatile bool EverPlaced;

        /// <summary>Placements failed in a row since the last success. Its landblock thread.</summary>
        public int PlaceFailures;

        public Slot(GauntletRun run) { Run = run; }

        public bool IsLiveCopy
        {
            get
            {
                if (Removed)
                    return false;

                var s = Run.State;
                return s == GauntletState.Staging || s == GauntletState.Active || s == GauntletState.Intermission;
            }
        }

        public void OnFighterDied(GauntletAvatarFighter fighter)
        {
            if (Removed || !ReferenceEquals(Fighter, fighter))
                return;

            Fighter = null;
            Run.OnFighterDied();
        }
    }

    internal Slot SlotFor(GauntletRun run) => run == null ? null : slots.TryGetValue(run.RunKey, out var s) ? s : null;

    // ---- the hooks ------------------------------------------------------------------------------------------------------------

    public void Spawn(GauntletRun run)
    {
        if (run == null)
            return;

        var slot = slots.GetOrAdd(run.RunKey, _ => new Slot(run));

        // Before the first wave the fighter shows L(1) and keeps its weenie's health, with the cap on from the start.
        var t = run.Tuning ?? GauntletTuning.Defaults;
        var level = new GauntletRules(t).LevelOf(1);
        slot.LastStamp ??= new GauntletFighterStamp
        {
            Level = level,
            AvatarLevel = SafeAvatarLevel(run.AvatarId),
            MinDeathSeconds = t.FighterMinDeathSeconds,
            BurstSeconds = t.FighterBurstSeconds,
        };

        slot.PlacePending = true;
        SafeEnqueue(run, "spawn", () => PlaceNew(slot));
    }

    public void Restamp(GauntletRun run, GauntletWavePlan wave, bool clearBuffs)
    {
        var slot = SlotFor(run);

        if (slot == null || slot.Removed)
            return;

        GauntletFighterStamp stamp = null;

        try
        {
            stamp = GauntletFighterStats.Compute(wave, SafeAvatarLevel(run.AvatarId), run.Tuning ?? GauntletTuning.Defaults);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] {run} fighter stamp threw for wave {wave?.Wave}", ex);
        }

        if (stamp != null)
            slot.LastStamp = stamp;

        SafeEnqueue(run, "restamp", () =>
        {
            if (ReplaceIfLost(slot))
                return;   // the replacement was placed with the new stamp

            var f = slot.Fighter;

            if (f == null || f.IsDead || f.IsDestroyed || slot.Removed)
                return;

            f.ApplyStamp(stamp, clearBuffs: clearBuffs);

            if (stamp != null)
                log.Info($"[GAUNTLET] {run} fighter restamped for wave {wave?.Wave}: L{stamp.Level} A{stamp.AvatarLevel} health {f.Health.Current}/{f.Health.MaxValue}, hit {stamp.DamageTarget}, {stamp.Skills.Count} skill(s)");
        });
    }

    public void Respawn(GauntletRun run)
    {
        var slot = SlotFor(run);

        if (slot == null || slot.Removed)
            return;

        slot.PlacePending = true;

        SafeEnqueue(run, "respawn", () =>
        {
            var old = slot.Fighter;

            if (old != null && !old.IsDead && !old.IsDestroyed)
            {
                slot.PlacePending = false;
                return;
            }

            PlaceNew(slot);
        });
    }

    public void Ensure(GauntletRun run)
    {
        var slot = SlotFor(run);

        if (slot == null || slot.Removed)
            return;

        var f = slot.Fighter;

        // A spawn or respawn whose placement failed: try again (the landblock thread re-checks).
        if (slot.PlacePending && (f == null || f.IsDestroyed))
        {
            SafeEnqueue(run, "ensure placement", () =>
            {
                var now = slot.Fighter;

                if (!slot.PlacePending || (now != null && !now.IsDestroyed))
                    return;

                if (now != null)
                    now.Owner = null;

                PlaceNew(slot);
            });
            return;
        }

        // Read on the world thread only to skip the queue in the common case; the landblock thread decides.
        if (f == null || !f.IsDestroyed || f.IsDead)
            return;

        SafeEnqueue(run, "ensure", () => ReplaceIfLost(slot));
    }

    /// <summary>
    /// Landblock thread: when the slot's fighter is destroyed but did not die (it rotted or its landblock dropped it), places a
    /// fresh one with the last stamp. True when it did. A dead fighter is the respawn timer's business.
    /// </summary>
    internal bool ReplaceIfLost(Slot slot)
    {
        var f = slot.Fighter;

        if (slot.Removed || f == null || !f.IsDestroyed || f.IsDead)
            return false;

        log.Warn($"[GAUNTLET] {slot.Run} fighter 0x{f.Guid} left the world without dying; placing a new one");
        f.Owner = null;

        // The lost fighter stays in the slot until a new one is placed, so a failed placement is tried again at the next Ensure.
        return PlaceNew(slot) != null;
    }

    public void WaveCleared(GauntletRun run, int wave)
    {
        var f = SlotFor(run)?.Fighter;

        if (f != null && !f.IsDead && !f.IsDestroyed)
            AvatarFighterVoice.Say(f, FighterVoiceTrigger.WaveClear);
    }

    public void Remove(GauntletRun run)
    {
        if (run == null || !slots.TryRemove(run.RunKey, out var slot))
            return;

        slot.Removed = true;
        var f = slot.Fighter;

        if (f == null)
            return;

        // Unbound first: the destroy is never reported as a death, and the beneficial opt-in closes at once.
        f.Owner = null;

        // A copy that is already gone takes the fighter with it.
        if (!SafeEnqueue(run, "remove", () => SafeDestroy(f)))
            log.Info($"[GAUNTLET] {run} fighter 0x{f.Guid} left with its landblock");
    }

    // ---- placement -----------------------------------------------------------------------------------------------------------

    /// <summary>Builds, stamps and places a fighter for <paramref name="slot"/> (its landblock thread).</summary>
    internal GauntletAvatarFighter PlaceNew(Slot slot)
    {
        if (slot.Removed)
            return null;

        var run = slot.Run;
        var centre = run.CentrePosition;

        if (centre == null || run.Instance == 0)
            return null;

        GauntletAvatarFighter fighter;

        try
        {
            fighter = Create();
        }
        catch (Exception ex)
        {
            LogPlaceFailure(slot, $"[GAUNTLET] {run} could not build the fighter (wcid {GauntletAvatarFighter.FighterWcid})", ex);
            return null;
        }

        if (fighter == null)
        {
            LogPlaceFailure(slot, $"[GAUNTLET] {run} has no fighter: wcid {GauntletAvatarFighter.FighterWcid} is missing or is not the fighter weenie");
            return null;
        }

        DressSafely(fighter, run);

        fighter.Location = new Position(centre);

        // No leash in the Gauntlet (owner ruling 2026-11-23): it follows the highest-level living member inside the copy.
        fighter.Anchor = null;
        fighter.Participants = () => Participants(run);
        fighter.IsDown = p => IsHeld(p.Guid.Full) || IsDead(p);
        fighter.Audience = () => Audience(run);
        fighter.Owner = slot;
        fighter.ApplyStamp(slot.LastStamp, fullHealth: true);

        // A hand-placed object with a dynamic guid and no generator rots on the landblock heartbeat (IsDecayable), and a rot
        // is a bare Destroy with no death and so no respawn: never rot, as ThreadDungeonSpawner and Pet do.
        fighter.TimeToRot = -1;

        bool placed;

        try
        {
            placed = Place(fighter);
        }
        catch (Exception ex)
        {
            LogPlaceFailure(slot, $"[GAUNTLET] {run} fighter placement threw", ex);
            placed = false;
        }

        if (!placed)
        {
            fighter.Owner = null;
            LogPlaceFailure(slot, $"[GAUNTLET] {run} fighter was refused at {centre.ToLOCString()}");
            SafeDestroy(fighter);
            return null;
        }

        if (slot.PlaceFailures > 0)
            log.Info($"[GAUNTLET] {run} fighter placed after {slot.PlaceFailures} failed attempt(s)");

        slot.Fighter = fighter;
        slot.PlacePending = false;
        slot.PlaceFailures = 0;

        AvatarFighterProbe.OnPlaced(fighter, run.ToString());

        AvatarFighterVoice.Say(fighter, slot.EverPlaced ? FighterVoiceTrigger.Return : FighterVoiceTrigger.Spawn);
        slot.EverPlaced = true;

        return fighter;
    }

    /// <summary>The look and scale, before placement. A throw is logged and the fighter is placed in its weenie outfit.</summary>
    private void DressSafely(GauntletAvatarFighter fighter, GauntletRun run)
    {
        try
        {
            Dress(fighter, run.AvatarId);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] {run} fighter dress threw; placing it in its weenie outfit", ex);
        }
    }

    /// <summary>
    /// A failed placement is retried every few seconds while it keeps failing: the first failure of a streak is a Warn, the
    /// rest Debug, so a fighter that cannot be placed does not fill the log. <paramref name="ex"/>, when given, is logged with
    /// its stack trace at either level. Returns true when it logged at Warn.
    /// </summary>
    internal static bool LogPlaceFailure(Slot slot, string message, Exception ex = null)
    {
        var first = slot.PlaceFailures++ == 0;

        if (first)
            log.Warn(message, ex);
        else
            log.Debug(message, ex);

        return first;
    }

    // ---- live defaults --------------------------------------------------------------------------------------------------------

    private static bool EnqueueOnCentre(GauntletRun run, Action action)
    {
        if (run.Instance == 0)
            return false;

        var lb = LandblockManager.GetLoadedLandblock(new LandblockId(run.Site.Centre.Cell & 0xFFFF0000u), run.Instance);

        if (lb == null)
            return false;

        lb.EnqueueAction(new ActionEventDelegate(action));
        return true;
    }

    private static IEnumerable<Player> LiveParticipants(GauntletRun run) => run.OnlineMembersInside();

    private static int LiveAvatarLevel(uint avatarId) =>
        AvatarManager.Get(avatarId)?.Level(AvatarManager.CurrentTuning()) ?? 0;

    // ---- guards -----------------------------------------------------------------------------------------------------------------

    private int SafeAvatarLevel(uint avatarId)
    {
        try
        {
            return Math.Max(0, AvatarLevel(avatarId));
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] Avatar {avatarId} level read threw; the fighter uses A = 0", ex);
            return 0;
        }
    }

    private bool SafeEnqueue(GauntletRun run, string what, Action action)
    {
        try
        {
            return Enqueue(run, () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error($"[GAUNTLET] {run} fighter {what} threw", ex);
                }
            });
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] {run} fighter {what} could not be queued", ex);
            return false;
        }
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
            log.Error($"[GAUNTLET] fighter 0x{f.Guid} destroy threw", ex);
        }
    }
}
