using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// What kind of spawn a batch is. Only affects bookkeeping (which guid set the placed object lands in),
    /// never the placement recipe - except for <see cref="Decor"/>, which is not a Creature at all and takes
    /// its own placement path (no terrain snap, no jitter retry); see <see cref="WorldEventSpawner.SpawnDecor"/>.
    ///
    /// <see cref="Npc"/> (WP-15) sits between the two: it IS a Creature and is snapped to terrain like one,
    /// but it counts for nothing - not MaxAlive, not LiveTotal, not an objective, not the ledger - and it is
    /// adopted WITHOUT the P_WorldEvent back-reference, so its death cannot reach the run at all. See
    /// <see cref="WorldEventSpawner.SpawnNpcs"/>.
    /// </summary>
    /// <summary>
    /// InertObjective (2026-08-19) is the same objective creature as Source, placed as SCENERY because the
    /// run's goal is not DestroySource - an element portal's pillars still frame the fight under a
    /// kill_count or kill_boss goal, but nothing about them is a target. It is a Creature and is snapped to
    /// terrain like one, and it then takes the same bookkeeping branch as Decor: no sourceGuids entry, no
    /// P_WorldEvent back-reference, and no throughput credit. TryPlace additionally makes it unattackable,
    /// un-labelled and invisible on the radar before it enters the world.
    /// </summary>
    /// <summary>
    /// Add (2026-09-05) is the odd one out: nothing in this class ever PLACES one. It is an object a
    /// generator belonging to the run put into the world on its own - a roster weenie with generator
    /// profiles is itself a generator - caught at birth in WorldObject.OnGeneration and routed here by
    /// <see cref="WorldEventSpawner.AdoptGeneratedAdd"/> purely so cleanup destroys it.
    ///
    /// It is HOLD-FOR-CLEANUP ONLY and must stay that way: it takes the not-live side of
    /// <see cref="WorldEventSpawner.CountsAsLive"/>, so it never reaches liveWave, MaxAlive, sourceGuids,
    /// the pace controller, the throughput ledger or the kill objective, and it is adopted WITHOUT the
    /// P_WorldEvent back-reference (Creature_Death.cs requires that reference AND the WorldEventId stamp
    /// before it calls OnEventCreatureDied). An add must not change pacing or objectives - the run was
    /// composed without it, and a monster that spawns its own escort would otherwise inflate the very
    /// numbers the difficulty controller is steering by.
    ///
    /// It is also the one kind that is NOT stamped with PropertyInt.WorldEventId, deliberately. That stamp
    /// is written before EnterWorld everywhere else and is a persistence exclusion; an add is already
    /// excluded from persistence for a stronger reason (WorldObject_Database refuses to save anything with
    /// Generator != null), and stamping it after the fact would make a generated ITEM a player legitimately
    /// picked up permanently unsaveable.
    /// </summary>
    public enum WorldEventSpawnKind
    {
        Wave,
        Source,
        Boss,
        Decor,
        Npc,
        InertObjective,
        Add
    }

    /// <summary>
    /// What a wave-kind <see cref="WorldEventSpawner.SpawnEntry"/> is, within the Wave spawn kind (WP-24).
    /// Only affects the synthetic-promotion rename/health-buff decision in
    /// <see cref="WorldEventSpawner.Spawn"/> - it never changes placement, MaxAlive accounting or anything
    /// <see cref="WorldEventSpawnKind"/> already governs.
    /// </summary>
    public enum WorldEventWaveEntryKind
    {
        Trash,
        Elite,
        Champion
    }

    /// <summary>
    /// Places and destroys everything one run puts into the world (TECH-DESIGN 2.4).
    ///
    /// The recipe is structurally identical to Player_WaveChallenge.SpawnWave, which is the only other
    /// hand-spawner in this fork: create -> Creature guard -> position -> landblock guard -> stamp ->
    /// TimeToRot -> EnterWorld -> adopt. Two of those steps are load-bearing and easy to drop:
    ///   * TimeToRot = -1, because a hand-spawned creature has no Generator back-reference and is therefore
    ///     decayable; the landblock heartbeat would Destroy() it at DefaultTimeToRot with a bare Destroy()
    ///     that never runs Die(), so the objective would never advance;
    ///   * PropertyInt.WorldEventId, which is both the persistence exclusion (WorldObject_Database) and the
    ///     marker the orphan sweep looks for.
    ///
    /// Threading: object construction runs on the caller's thread (the world thread), but every placement,
    /// Destroy and position write is queued onto the owning landblock's action queue - a cross-thread
    /// AddWorldObject is logged as an error by the landblock itself. OnDied arrives from a landblock tick
    /// thread, so every mutable collection here is guarded.
    /// </summary>
    /// <summary>
    /// One finished wave's timing, handed to the pace controller (TECH-DESIGN 2.15).
    /// </summary>
    public readonly struct WorldEventWaveClear
    {
        public readonly int WaveIndex;
        public readonly double ClearSeconds;

        public WorldEventWaveClear(int waveIndex, double clearSeconds)
        {
            WaveIndex = waveIndex;
            ClearSeconds = clearSeconds;
        }

        public override string ToString() => $"wave={WaveIndex} clear={ClearSeconds:F1}s";
    }

    public sealed class WorldEventSpawner
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Metres of jitter applied when an anchor is reused, and on every placement retry.</summary>
        public const float RetryJitterMetres = 2.0f;

        /// <summary>
        /// The boss's own retry jitter (2026-08-16, D6 - pure), widened by attempt so a champion stuck at a
        /// transiently blocked anchor fans further out on each retry instead of resampling the same handful
        /// of metres every time. Boss kind only - <see cref="RetryJitterMetres"/> is unchanged for every
        /// other kind. attempt 0 -> 2, 1 -> 4, 2 -> 6, 3 -> 8.
        /// </summary>
        public static float BossRetryJitterMetres(int attempt)
        {
            return RetryJitterMetres * (attempt + 1);
        }

        /// <summary>
        /// Whether <see cref="Adopt"/> should accept an objective-boss placement (2026-08-16, D6 - pure):
        /// true only when no boss has been adopted yet. Defense in depth against two enqueued attempts
        /// resolving concurrently - <see cref="BossPlacementPending"/> is what should normally stop
        /// WorldEvent.TickChampion from ever issuing a second attempt while the first is still in flight,
        /// but Adopt is the last line that can actually stop a duplicate boss from entering the world if
        /// that ever fails to hold.
        /// </summary>
        public static bool AcceptsBossAdopt(uint existingBossGuid)
        {
            return existingBossGuid == 0;
        }

        /// <summary>
        /// The anchor as given, then up to three alternative jitters, before a creature is given up on
        /// (TECH-DESIGN R16 - a bad anchor must cost one creature, not silently place nothing).
        /// </summary>
        public const int PlacementAttempts = 4;

        /// <summary>
        /// Attempts allowed for a WAVE on a "disc" theme (WP-18 item 2). Higher than
        /// <see cref="PlacementAttempts"/> because each retry is a fresh sample from the whole disc rather
        /// than a 2 m nudge of the same rejected point, so the extra attempts genuinely explore new ground
        /// instead of re-testing the same obstruction.
        /// </summary>
        public const int PlacementAttemptsDisc = 8;

        /// <summary>How long after the queued destroys the survivor check runs, in seconds.</summary>
        public const double SurvivorCheckDelaySeconds = 1.0;

        /// <summary>
        /// WP-17 sky-drop: how long a dropping wave creature may stay in the air before the physics tick
        /// force-settles it onto the terrain. A 20 m fall takes roughly 2 s, so this is a generous bound
        /// whose only job is to guarantee that a creature which somehow never reports OnWalkable (a bad
        /// cell, a physics edge case) cannot hang in the sky for the rest of the run.
        ///
        /// Measured against PhysicsTimer.CurrentTime, which is the clock the tick compares it to.
        /// </summary>
        public const double SkyDropTimeoutSeconds = 12.0;

        /// <summary>Wave index used by the objective and champion spawns, which are not waves.</summary>
        private const int NoWaveIndex = -1;

        private readonly object sync = new object();

        private readonly Random rng = new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

        /// <summary>
        /// The run clock, injected so the wave-timing bookkeeping below is deterministic under test (D6).
        /// Null in the constructor means the live Unix clock, exactly as WorldEvent's own does.
        /// </summary>
        private readonly Func<double> clock;

        public WorldEventSpawner() : this(null)
        {
        }

        public WorldEventSpawner(Func<double> clock)
        {
            this.clock = clock ?? Time.GetUnixTime;
        }

        /// <summary>Everything this spawner put into the world, dead or alive.</summary>
        private readonly List<WorldObject> spawned = new List<WorldObject>();

        /// <summary>Live WAVE creatures. This, and only this, is charged against the theme's MaxAlive.</summary>
        private readonly List<Creature> liveWave = new List<Creature>();

        /// <summary>Live objective (Rift) and champion spawns, which are not wave pressure.</summary>
        private readonly List<Creature> liveObjective = new List<Creature>();

        private readonly HashSet<uint> sourceGuids = new HashSet<uint>();
        private readonly HashSet<uint> liveSourceGuids = new HashSet<uint>();

        /// <summary>
        /// Live decor objects (WP-14). Their own list, never <see cref="liveWave"/> or
        /// <see cref="liveObjective"/>: decor is scenery, so it is not wave pressure, not "still standing"
        /// for the status line, and not a kill anything can ever score.
        /// </summary>
        private readonly List<WorldObject> liveDecor = new List<WorldObject>();

        private readonly HashSet<uint> decorGuids = new HashSet<uint>();

        /// <summary>
        /// Live spawn-time NPCs (WP-15). Their own list for the same reason decor has one: an npc is a
        /// Creature, but it is not wave pressure, not "still standing" for the status line and not a kill
        /// anything can ever score.
        /// </summary>
        private readonly List<WorldObject> liveNpcs = new List<WorldObject>();

        private readonly HashSet<uint> npcGuids = new HashSet<uint>();

        /// <summary>
        /// Live generator-spawned adds (2026-09-05). Their own list for exactly the reason decor and npcs
        /// have theirs: an add is held for cleanup and counts for nothing else. See
        /// <see cref="WorldEventSpawnKind.Add"/>.
        /// </summary>
        private readonly List<WorldObject> liveAdds = new List<WorldObject>();

        private readonly HashSet<uint> addGuids = new HashSet<uint>();

        /// <summary>
        /// How many generator-spawned adds this run adopted, and how many it refused (because cleanup had
        /// already started) and destroyed instead. Reported once per run on the "adds" observability line;
        /// guarded by <see cref="sync"/> like everything else here.
        /// </summary>
        private int addsAdopted;

        private int addsStray;

        /// <summary>
        /// Guids of the OVERFLOW champions (TECH-DESIGN 2.15). They are wave-kind spawns in every other
        /// respect - they count toward LiveCount and toward the cleared-field trigger, and they never touch
        /// <see cref="bossGuid"/> - but they are additive ABOVE MaxAlive, so the only thing bounding them is
        /// the theme's overflowChampionMaxAlive measured against this set.
        /// </summary>
        private readonly HashSet<uint> overflowChampionGuids = new HashSet<uint>();

        /// <summary>Wave index each live wave creature belongs to, for the per-wave clear timing.</summary>
        private readonly Dictionary<uint, int> waveIndexByGuid = new Dictionary<uint, int>();

        /// <summary>Per-wave spawn time and alive count (TECH-DESIGN 2.15).</summary>
        private readonly Dictionary<int, WaveRecord> waveRecords = new Dictionary<int, WaveRecord>();

        /// <summary>Waves that have cleared since the last <see cref="TakeClearedWaves"/>, in order.</summary>
        private readonly Queue<WorldEventWaveClear> clearedWaves = new Queue<WorldEventWaveClear>();

        private sealed class WaveRecord
        {
            public double SpawnedAt;
            public int Alive;
            public bool Cleared;
        }

        /// <summary>The list the 5.2-line cleanup pass worked from, so SurvivorsSnapshot reports against it.</summary>
        private List<WorldObject> cleanupTargets;

        private bool creaturePassRan;
        private bool finalPassRan;
        private bool cleanupLineEmitted;

        private uint bossGuid;

        /// <summary>
        /// True while an ISSUED objective-boss placement attempt is enqueued on the anchor landblock's
        /// action queue but has not yet resolved (2026-08-16). <see cref="Spawn"/> only constructs the
        /// creature and calls <c>landblock.EnqueueAction</c> synchronously - actual placement (jitter
        /// retries, <see cref="Adopt"/>, which is what sets <see cref="bossGuid"/>) runs inside that queued
        /// delegate, drained later by the landblock tick, not by the caller of <see cref="SpawnBoss"/>. A
        /// caller that reads <see cref="BossGuid"/> immediately after <see cref="SpawnBoss"/> returns will
        /// ALWAYS see 0 for the attempt it just issued - that was the defect: WorldEvent.TickChampion used
        /// to read it synchronously and, seeing 0, concluded the attempt was refused and issued a brand new
        /// one on the very next tick, constructing and enqueuing a fresh creature every tick until the
        /// anchor cleared, at which point two or more could land. This flag lets the caller distinguish "no
        /// attempt is in flight, safe to retry" from "an attempt is in flight, wait for it".
        /// </summary>
        private bool bossPlacementPending;

        /// <summary>Count of objective-boss placement attempts that resolved WITHOUT adopting (2026-08-16).</summary>
        private int bossPlacementRefusals;

        /// <summary>
        /// The objective boss itself, plus the health values it was AUTHORED with - captured before
        /// ApplyHealthMultiplier touched them, which is what makes the ratchet idempotent: every later
        /// raise recomputes from the authored pair rather than compounding on the previous result. All
        /// three are written once, under <see cref="sync"/>, when the boss is adopted.
        /// </summary>
        private Creature boss;
        private uint bossAuthoredStartingValue;
        private uint bossAuthoredBaseMax;

        /// <summary>
        /// The boss's DamageRating exactly as its weenie authored it, captured at adopt before the damage
        /// controller has touched anything (TECH-DESIGN 2.16). A boss with no DamageRating property at all
        /// reads as 0, which is the rating that means "1.0x" - so an unauthored boss and a boss authored at
        /// 0 are the same starting point, and both are honest.
        /// </summary>
        private int bossAuthoredDamageRating;

        /// <summary>
        /// Run clock at the boss's adopt, at its death, and the boss's Health.MaxValue - the three terms the
        /// finish throughput line's bossTtkSeconds / bossHps are computed from (C16). The max is captured at
        /// adopt (post-multiplier) and overwritten at death, so a boss the ratchet raised mid-fight reports
        /// the number the players actually finished, not the one it landed with. All three stay 0 for a run
        /// whose boss never stood, and the death pair stays 0 for a boss that outlived the run.
        /// </summary>
        private double bossAdoptedAt;
        private double bossDiedAt;
        private uint bossMaxHealth;

        /// <summary>
        /// Live WAVE creatures - the number WorldEvent charges against the theme's MaxAlive.
        ///
        /// Objective spawns and the champion are deliberately NOT counted: MaxAlive governs trash pressure,
        /// so a rift theme's four Rifts must not eat four of its twenty wave slots. Use
        /// <see cref="LiveTotal"/> for "everything this run still has standing".
        /// </summary>
        public int LiveCount
        {
            get
            {
                lock (sync)
                {
                    Prune();
                    return liveWave.Count;
                }
            }
        }

        /// <summary>
        /// Every live creature this run placed - wave trash, objective spawns and the champion. Reported by
        /// "/worldevent status"; never used as a spawn budget.
        /// </summary>
        public int LiveTotal
        {
            get
            {
                lock (sync)
                {
                    Prune();
                    return liveWave.Count + liveObjective.Count;
                }
            }
        }

        /// <summary>
        /// Live decor objects this run has placed (WP-14). Reported by "/worldevent status" as decor=&lt;n&gt;
        /// and deliberately separate from LiveCount/LiveTotal - decor is scenery, not pressure.
        /// </summary>
        public int DecorCount
        {
            get
            {
                lock (sync)
                {
                    PruneDecor();
                    return liveDecor.Count;
                }
            }
        }

        /// <summary>
        /// True when the guid belongs to a decor object this run placed. Decor can never die (it is never a
        /// Creature, so Creature.Die's hook cannot reach it), so this is a guard for any future path that
        /// reports an arbitrary held guid as a kill.
        /// </summary>
        public bool IsDecorGuid(uint guid)
        {
            lock (sync) return decorGuids.Contains(guid);
        }

        /// <summary>
        /// Live spawn-time NPCs this run has placed (WP-15). Reported by "/worldevent status" as
        /// npcs=&lt;n&gt; and deliberately separate from LiveCount/LiveTotal - an npc is scenery that happens
        /// to be a Creature, not pressure.
        /// </summary>
        public int NpcCount
        {
            get
            {
                lock (sync)
                {
                    PruneNpcs();
                    return liveNpcs.Count;
                }
            }
        }

        /// <summary>
        /// True when the guid belongs to a spawn-time npc this run placed. An npc death cannot reach an
        /// objective today (it is adopted without P_WorldEvent, and Creature_Death.cs's hook requires that
        /// back-reference as well as the WorldEventId stamp), so this is the hook a future path that reports
        /// an arbitrary held guid as a kill would have to consult.
        /// </summary>
        public bool IsNpcGuid(uint guid)
        {
            lock (sync) return npcGuids.Contains(guid);
        }

        /// <summary>
        /// Live generator-spawned adds this run has adopted (2026-09-05). Reported by "/worldevent status"
        /// as adds=&lt;n&gt; and, like decor= and npcs=, deliberately outside LiveCount/LiveTotal - an add is
        /// not pressure the run composed, so it must not read as if it were.
        /// </summary>
        public int AddCount
        {
            get
            {
                lock (sync)
                {
                    PruneAdds();
                    return liveAdds.Count;
                }
            }
        }

        /// <summary>
        /// True when the guid belongs to a generator-spawned add this run adopted. An add death cannot
        /// reach an objective today (adopted without P_WorldEvent, and Creature_Death.cs requires that
        /// back-reference as well as the WorldEventId stamp - and an add carries neither), so this is the
        /// hook a future path that reports an arbitrary held guid as a kill would have to consult, exactly
        /// as <see cref="IsNpcGuid"/> is.
        /// </summary>
        public bool IsAddGuid(uint guid)
        {
            lock (sync) return addGuids.Contains(guid);
        }

        /// <summary>Counts for the once-per-run "adds" observability line. See <see cref="addsAdopted"/>.</summary>
        public (int Adopted, int Stray) AddCounters
        {
            get { lock (sync) return (addsAdopted, addsStray); }
        }

        /// <summary>Objective (Rift) spawns. WP-06's DestroySource objective reads this.</summary>
        public IReadOnlyCollection<uint> SourceGuids
        {
            get { lock (sync) return sourceGuids.ToList(); }
        }

        /// <summary>
        /// Spawn-time npc guids (WP-15). WP-16's KillCount exclusion reads this, unioned with
        /// <see cref="SourceGuids"/>, via <see cref="Objectives.WorldEventObjectiveFactory"/> - belt and
        /// braces alongside the withheld P_WorldEvent back-reference that is the primary guard (see
        /// <see cref="IsNpcGuid"/>'s remarks).
        /// </summary>
        public IReadOnlyCollection<uint> NpcGuids
        {
            get { lock (sync) return npcGuids.ToList(); }
        }

        /// <summary>
        /// True once every objective spawn this run made is down. False while there are none, so a theme
        /// with no objective wcid never reads as "already finished".
        /// </summary>
        public bool AllSourcesDead
        {
            get { lock (sync) return sourceGuids.Count > 0 && liveSourceGuids.Count == 0; }
        }

        /// <summary>0 until a champion is placed. WP-06's KillBoss objective reads this.</summary>
        public uint BossGuid
        {
            get { lock (sync) return bossGuid; }
        }

        /// <summary>
        /// True while an issued objective-boss placement attempt has been enqueued on the landblock but has
        /// not yet resolved (2026-08-16). See the backing field's remarks. WorldEvent.TickChampion reads
        /// this alongside <see cref="BossGuid"/> so it never treats an in-flight attempt as a refusal.
        /// </summary>
        public bool BossPlacementPending
        {
            get { lock (sync) return bossPlacementPending; }
        }

        /// <summary>Count of objective-boss placement attempts that resolved without adopting (2026-08-16).</summary>
        public int BossPlacementRefusals
        {
            get { lock (sync) return bossPlacementRefusals; }
        }

        /// <summary>
        /// The boss's Health.MaxValue - at death when it died, otherwise as it was spawned. 0 when no boss
        /// ever stood (C16 observability).
        /// </summary>
        public uint BossMaxHealth
        {
            get { lock (sync) return bossMaxHealth; }
        }

        /// <summary>
        /// A live read of the standing boss's current/max health (2026-08-29, boss health milestones), for a
        /// caller that only READS. False (with both outs 0) when there is no boss, or it is dead/destroyed -
        /// matching the guard both write paths (<see cref="RatchetBossHealth"/>,
        /// <see cref="ApplyBossDamageRating"/>) use.
        ///
        /// UNLIKE those two writers, this reads <c>creature.Health</c> directly rather than going through the
        /// boss's own landblock action queue. <c>Health.Current</c> is a plain field, but
        /// <c>Health.MaxValue</c> is NOT - it walks the creature's enchantment list on every call
        /// (<c>EnchantmentManager.GetVitalMod_Multiplier</c>/<c>GetVitalMod_Additives</c>
        /// `[obs WorldObjects/Entity/CreatureVital.cs:141,143,170,183]`), and that list is an ordinary
        /// (unsynchronized) collection another thread's queued spell-landed/enchantment action could be
        /// mutating - so "it's just a field read" is not what makes this safe. What actually makes it safe is
        /// a tick-ordering invariant this type does not itself enforce, so it is recorded here (2026-08-29
        /// review fix):
        /// <see cref="WorldManager.UpdateGameWorld"/> calls <c>LandblockManager.Tick()</c>
        /// `[obs Managers/WorldManager.cs:621]` and only calls <c>WorldEventManager.Tick()</c> - which is
        /// what eventually reaches this method, via <see cref="WorldEvent.TickBossHealthMilestones"/> -
        /// AFTER it returns `[obs Managers/WorldManager.cs:627]`, on the SAME calling thread.
        /// <c>LandblockManager.Tick()</c> does not return until its OWN <c>TickMultiThreadedWork()</c> call
        /// has returned `[obs Managers/LandblockManager.cs:311,320]` - that method's <c>Parallel.ForEach</c>
        /// is the one that matters here `[obs Managers/LandblockManager.cs:412,425,433]`, NOT the earlier,
        /// separate <c>Parallel.ForEach</c> inside <c>TickPhysics</c> (`Managers/LandblockManager.cs:363`),
        /// which only moves physics and never touches an action queue. TickMultiThreadedWork's per-group work
        /// includes draining the landblock's action queue - <c>Landblock.TickMultiThreadedWork</c> calls
        /// <c>actionQueue.RunActions()</c> synchronously `[obs Entity/Landblock.cs:877,897]`, which is where
        /// any <see cref="RatchetBossHealth"/>/<see cref="ApplyBossDamageRating"/> delegate queued earlier
        /// actually runs. <c>Parallel.ForEach</c> returning re-establishes happens-before on the calling
        /// thread for everything its worker threads did, so by the time this method runs there is no boss
        /// vital write still in flight to race against.
        ///
        /// This is fragile, not structural: moving the WorldEventManager.Tick() call ahead of
        /// LandblockManager.Tick(), or making either asynchronous, reintroduces an unguarded race on the
        /// boss Creature's live vitals. If that ordering ever needs to change, route this read through the
        /// boss's own landblock action queue the way the two writers already do, rather than assuming the
        /// invariant still holds.
        /// </summary>
        public bool TryGetBossHealth(out uint current, out uint max)
        {
            Creature creature;

            lock (sync)
                creature = boss;

            if (creature == null || creature.IsDestroyed || creature.IsDead)
            {
                current = 0;
                max = 0;
                return false;
            }

            max = creature.Health.MaxValue;
            current = creature.Health.Current;
            return true;
        }

        /// <summary>
        /// The boss's AUTHORED DamageRating (see the backing field). 0 when no boss has stood. This is what
        /// WorldEvent seeds its damage controller from, so the first adjustment is measured against the
        /// rating the content author actually gave the boss rather than against a guess.
        /// </summary>
        public int BossAuthoredDamageRating
        {
            get { lock (sync) return bossAuthoredDamageRating; }
        }

        /// <summary>
        /// Whether <see cref="ApplyBossDamageRating"/> would accept a write right now: a live boss with a
        /// CurrentLandblock. Read BEFORE the damage controller is asked for an advice, because
        /// WorldEventBossDamageController.TryAdvise consumes its sample window and its one step-cap
        /// exemption on a true return - so asking it for a decision that then cannot be written would throw
        /// away the very first correction, which is the one that is allowed to be large.
        ///
        /// There is still a race (the boss can enter an adjacency transfer between this read and the write)
        /// and that is fine: the write refuses, one window is lost, and the next one tries again. What this
        /// removes is the SYSTEMATIC case - a boss mid-transfer at the exact moment its window fills.
        /// </summary>
        public bool BossDamageWriteAvailable
        {
            get
            {
                Creature creature;

                lock (sync)
                    creature = boss;

                if (creature == null)
                    return false;

                return BossDamageWriteReady(creature.IsDestroyed || creature.IsDead, creature.CurrentLandblock != null);
            }
        }

        /// <summary>
        /// Seconds from the boss's adopt to its death, on this spawner's clock. 0 when the boss never stood
        /// or never died, which the finish line reports as a blank measurement rather than a fast kill
        /// (C16).
        /// </summary>
        public double BossTtkSeconds
        {
            get
            {
                lock (sync)
                    return bossAdoptedAt > 0 && bossDiedAt > bossAdoptedAt ? bossDiedAt - bossAdoptedAt : 0;
            }
        }

        /// <summary>
        /// <see cref="BossGuid"/> and <see cref="BossPlacementPending"/> together, under ONE
        /// <c>lock (sync)</c> (2026-08-16 review fix, following <see cref="SurvivorsSnapshot"/>'s
        /// convention). The two properties above are each individually locked, so a caller reading them as
        /// two separate calls can observe a torn snapshot - bossGuid read before <see cref="Adopt"/> sets it
        /// and pending read after the queued delegate's <c>finally</c> clears it - which reads as "nothing
        /// standing and nothing in flight" for an attempt that in fact just landed. WorldEvent.TickChampion
        /// must call this instead of the two properties whenever it needs both values for the same decision.
        /// </summary>
        public (uint BossGuid, bool Pending) SnapshotBossPlacement()
        {
            lock (sync)
                return (bossGuid, bossPlacementPending);
        }

        /// <summary>
        /// Live OVERFLOW champions (TECH-DESIGN 2.15) - the number the next pick subtracts from the theme's
        /// overflowChampionMaxAlive. Counted out of <see cref="liveWave"/>, so it retires a champion on
        /// exactly the same terms as any other wave creature.
        ///
        /// Never includes the OBJECTIVE champion (kind Boss), which is a different thing entirely and is
        /// counted by <see cref="LiveTotal"/> instead.
        /// </summary>
        public int OverflowChampionsAlive
        {
            get
            {
                lock (sync)
                {
                    Prune();

                    var count = 0;

                    foreach (var creature in liveWave)
                    {
                        if (creature != null && overflowChampionGuids.Contains(creature.Guid.Full))
                            count++;
                    }

                    return count;
                }
            }
        }

        /// <summary>
        /// Drains the waves that have finished since the last call (TECH-DESIGN 2.15). A wave is finished
        /// when every creature it placed has died - destroyed-at-cleanup does not count, because the run is
        /// over by then and no further pick will read this.
        ///
        /// Draining rather than accumulating is deliberate: the pace controller evaluates each observation
        /// exactly once, and a wave that cleared while no pick was due must still be seen by the next one.
        /// </summary>
        public IReadOnlyList<WorldEventWaveClear> TakeClearedWaves()
        {
            lock (sync)
            {
                if (clearedWaves.Count == 0)
                    return Array.Empty<WorldEventWaveClear>();

                var drained = new List<WorldEventWaveClear>(clearedWaves);

                clearedWaves.Clear();

                return drained;
            }
        }

        /// <summary>
        /// How long ago the OLDEST wave that still has something standing landed, in seconds, or 0 when
        /// the field is clear (TECH-DESIGN 2.15). This is the "nothing is dying" signal: a wave can be far
        /// past the pace window without ever clearing, and waiting for it to clear before easing off would
        /// mean never easing off at all on the run that most needs it.
        /// </summary>
        public double OldestAliveWaveAge(double now)
        {
            lock (sync)
            {
                var oldest = 0d;

                foreach (var record in waveRecords.Values)
                {
                    if (record.Alive <= 0)
                        continue;

                    var age = now - record.SpawnedAt;

                    if (age > oldest)
                        oldest = age;
                }

                return oldest;
            }
        }

        /// <summary>
        /// True once a <see cref="DestroyAll"/> pass has taken its snapshot. After that point no creature
        /// may be adopted: the cleanup pass has already decided what it is destroying, and a creature
        /// adopted afterwards carries TimeToRot = -1 and so would never leave the world at all.
        /// </summary>
        public bool IsClosed
        {
            get { lock (sync) return creaturePassRan || finalPassRan; }
        }

        /// <summary>
        /// One wave. <paramref name="waveIndex"/> and <paramref name="band"/> are carried through to the
        /// placement closure purely so the fixed 5.2 wave line can be emitted from THERE - once the wave has
        /// finished landing - rather than by the caller, which only knows what it asked for.
        /// </summary>
        public void SpawnWave(WorldEvent evt, IReadOnlyList<uint> wcids, IReadOnlyList<Position> anchors,
            int waveIndex, LevelBand band)
        {
            SpawnWave(evt, new WavePick(wcids, null), anchors, waveIndex, band, 1.0);
        }

        /// <summary>
        /// One wave, trash and overflow champions together (TECH-DESIGN 2.15). Both halves go down the
        /// SAME path and are adopted as kind Wave, so both count toward LiveCount and toward the
        /// cleared-field trigger; the only thing that distinguishes a champion afterwards is the per-guid
        /// tag <see cref="Adopt"/> files, which is what caps how many may be alive at once.
        ///
        /// <paramref name="healthMult"/> is the composed crowd x pace multiplier, applied to every creature
        /// in the wave at construction time (see <see cref="ApplyHealthMultiplier"/>). 1.0 is a no-op.
        /// </summary>
        public void SpawnWave(WorldEvent evt, WavePick pick, IReadOnlyList<Position> anchors,
            int waveIndex, LevelBand band, double healthMult)
        {
            if (pick == null)
                return;

            var entries = new List<SpawnEntry>();

            for (var i = 0; i < pick.Trash.Count; i++)
            {
                // WP-24: the every-third-wave elite slot is one specific index into Trash, marked by
                // WavePick.EliteIndex - every other index is plain trash.
                var isElite = i == pick.EliteIndex;

                entries.Add(new SpawnEntry(pick.Trash[i], overflowChampion: false,
                    isElite ? WorldEventWaveEntryKind.Elite : WorldEventWaveEntryKind.Trash,
                    isElite && pick.EliteSynthetic));
            }

            for (var i = 0; i < pick.Champions.Count; i++)
            {
                var synthetic = i < pick.ChampionsSynthetic.Count && pick.ChampionsSynthetic[i];

                entries.Add(new SpawnEntry(pick.Champions[i], overflowChampion: true,
                    WorldEventWaveEntryKind.Champion, synthetic));
            }

            Spawn(evt, entries, anchors, WorldEventSpawnKind.Wave, waveIndex, band, healthMult);
        }

        /// <summary>
        /// One objective spawn per anchor - this is how the "rift" theme places its Rifts.
        ///
        /// <paramref name="kind"/> is Source for a DestroySource run and InertObjective for every other goal
        /// (2026-08-19, WorldEvent.ObjectiveSpawnKind), exactly as <see cref="SpawnObjectives"/> takes it.
        /// The rift theme is compatible with destroy_source ONLY, so no shipped composition can reach this
        /// method with anything but the default - it is threaded through so that widening rift's
        /// compatibleGoals later cannot silently leave un-killable Rifts standing as live objectives.
        /// </summary>
        public void SpawnSources(WorldEvent evt, uint wcid, IReadOnlyList<Position> anchors,
            WorldEventSpawnKind kind = WorldEventSpawnKind.Source)
        {
            if (wcid == 0 || anchors == null || anchors.Count == 0)
                return;

            var entries = new List<SpawnEntry>();

            for (var i = 0; i < anchors.Count; i++)
                entries.Add(new SpawnEntry(wcid, overflowChampion: false));

            // Health scaling is a WAVE rule (TECH-DESIGN 2.15): a rift is the objective itself, and making
            // it tougher with turnout would move the goalposts rather than the pressure.
            Spawn(evt, entries, anchors, kind, NoWaveIndex, default, 1.0);
        }

        /// <summary>
        /// WP-21 fixed-offset objective spawns: one Source per <see cref="ObjectiveDef"/> entry, at a fixed
        /// offset from the geometry centre rather than a randomly-rotated/jittered anchor - what a theme
        /// with <c>objectives</c> in sources.json gets INSTEAD of <see cref="SpawnSources"/> (see
        /// SourceThemeDef.Objectives's remarks). Anchors are built with <see cref="NpcPosition"/> exactly
        /// as PlaceNpcs does; the creature placement path in <see cref="Spawn"/>/<see cref="TryPlace"/>
        /// re-snaps to terrain outdoors, which would otherwise silently drop each entry's <c>dz</c> (an
        /// outdoor AdjustMapCoords overwrites Z outright, and Spawn's own dz handling - SkyDropZ - is 0 for
        /// every kind but Wave). dzByAnchor threads each entry's dz through Spawn/PlaceOnLandblock/TryPlace
        /// so it is re-applied ON TOP of the snapped ground, the WP-15 npc rule exactly (see
        /// ObjectiveSnapZ's remarks) - indoors gets nothing extra to do, since NpcPosition already baked dz
        /// into the anchor's Z and nothing there overwrites it.
        /// </summary>
        public void SpawnObjectives(WorldEvent evt, IReadOnlyList<ObjectiveDef> objectives, Position centre,
            WorldEventSpawnKind kind = WorldEventSpawnKind.Source)
        {
            if (objectives == null || objectives.Count == 0 || centre == null)
                return;

            var entries = new List<SpawnEntry>();
            var anchors = new List<Position>();
            var dzByAnchor = new List<float>();

            foreach (var entry in objectives)
            {
                if (entry == null || entry.Wcid == 0)
                    continue;

                entries.Add(new SpawnEntry(entry.Wcid, overflowChampion: false));
                anchors.Add(NpcPosition(centre, entry.Dx, entry.Dy, entry.Dz, entry.Yaw));
                dzByAnchor.Add(entry.Dz);
            }

            if (entries.Count == 0)
                return;

            // healthMult 1.0 for the same reason SpawnSources passes it: the crowd x pace multiplier is a
            // WAVE rule, and a Source's health comes from the theme's objectiveHealth instead (see Spawn).
            //
            // kind is Source for a DestroySource run and InertObjective for every other goal (2026-08-19,
            // WorldEvent.ObjectiveSpawnKind) - the placement recipe is identical either way; what changes is
            // the bookkeeping branch in Adopt and the inert property writes in TryPlace.
            Spawn(evt, entries, anchors, kind, NoWaveIndex, default, 1.0, dzByAnchor);
        }

        public void SpawnBoss(WorldEvent evt, uint wcid, Position anchor)
        {
            SpawnBoss(evt, wcid, anchor, 1.0);
        }

        /// <summary>
        /// The objective champion, scaled by <paramref name="healthMult"/> as of ITS spawn moment
        /// (TECH-DESIGN 2.15) - not the multiplier in force when the run started, because the champion now
        /// arrives at the minimum-duration mark and the crowd may have moved a long way by then.
        /// </summary>
        public void SpawnBoss(WorldEvent evt, uint wcid, Position anchor, double healthMult)
        {
            SpawnBoss(evt, wcid, anchor, healthMult, synthetic: false);
        }

        /// <summary>
        /// WP-24 follow-up (owner ruling 2026-08-16): the family champion can now itself be a role-0
        /// promotion (<see cref="WorldEventRosterSelector.PickChampion"/>), when the elite/champion band had
        /// no real role-2/role-1 member. <paramref name="synthetic"/> true routes the entry through
        /// <see cref="WorldEventWaveEntryKind.Champion"/> so <see cref="Spawn"/>'s existing synthetic branch
        /// renames it and applies the further health multiplier, exactly as an overflow champion gets.
        /// </summary>
        public void SpawnBoss(WorldEvent evt, uint wcid, Position anchor, double healthMult, bool synthetic)
        {
            if (wcid == 0 || anchor == null)
                return;

            var entry = new SpawnEntry(wcid, overflowChampion: false, WorldEventWaveEntryKind.Champion, synthetic);

            Spawn(evt, new List<SpawnEntry> { entry },
                new List<Position> { anchor }, WorldEventSpawnKind.Boss, NoWaveIndex, default, healthMult);
        }

        /// <summary>
        /// One wcid to place, whether it is an overflow champion rather than trash, and (WP-24) which wave
        /// entry kind it is and whether it is a synthetic (role-0 promotion) elite or champion.
        ///
        /// The two-argument constructor is kept for the Source/Boss call sites, which never carry a
        /// synthetic marker: it defaults Kind from <paramref name="overflowChampion"/> and Synthetic false,
        /// so their behaviour is unchanged.
        /// </summary>
        private readonly struct SpawnEntry
        {
            public readonly uint Wcid;
            public readonly bool OverflowChampion;
            public readonly WorldEventWaveEntryKind Kind;
            public readonly bool Synthetic;

            public SpawnEntry(uint wcid, bool overflowChampion)
                : this(wcid, overflowChampion,
                       overflowChampion ? WorldEventWaveEntryKind.Champion : WorldEventWaveEntryKind.Trash, false)
            {
            }

            public SpawnEntry(uint wcid, bool overflowChampion, WorldEventWaveEntryKind kind, bool synthetic)
            {
                Wcid = wcid;
                OverflowChampion = overflowChampion;
                Kind = kind;
                Synthetic = synthetic;
            }
        }

        /// <summary>One constructed creature and the entry it came from.</summary>
        private sealed class SpawnCandidate
        {
            public SpawnCandidate(Creature creature, bool overflowChampion, WorldEventWaveEntryKind kind, bool synthetic)
            {
                Creature = creature;
                OverflowChampion = overflowChampion;
                Kind = kind;
                Synthetic = synthetic;
            }

            public Creature Creature { get; }

            public bool OverflowChampion { get; }

            public WorldEventWaveEntryKind Kind { get; }

            public bool Synthetic { get; }

            /// <summary>
            /// Health.StartingValue and Health.MaxValue as the weenie authored them, read BEFORE
            /// ApplyHealthMultiplier runs. Only the boss path uses them (see RatchetBossHealth); every
            /// other kind leaves them at 0.
            /// </summary>
            public uint AuthoredStartingValue { get; set; }

            public uint AuthoredBaseMax { get; set; }
        }

        /// <summary>
        /// WP-14 spawn-time decor: the theme's scenery layers, all placed at the geometry CENTRE (not one per
        /// geometry anchor - a stack of discs is one object in the sky, not a ring of them), each at
        /// <c>centre.Z + Dz</c>.
        ///
        /// Same enqueue-on-the-anchor-landblock -> EnterWorld -> Adopt recipe as the creature paths, with
        /// three deliberate differences:
        ///
        ///   * the "not a Creature" refusal does NOT apply - decor is scenery and is never expected to die,
        ///     so it cannot hold an objective open;
        ///   * no terrain snap. AdjustMapCoords would drop the origin to the ground and take the disc with
        ///     it, which is the entire thing this places in the sky;
        ///   * no jitter retry. Jitter exists to find a walkable spot near a blocked anchor; a point 20-70 m
        ///     up is either inside the held landblock or it is not, and nudging it 2 m sideways changes
        ///     nothing.
        ///
        /// The Dz values are a compound of the intended disc height and 0.455 x Scale (see DecorDef) and are
        /// used exactly as given.
        /// </summary>
        public void SpawnDecor(WorldEvent evt, IReadOnlyList<DecorDef> decor, Position centre)
        {
            if (evt == null || decor == null || decor.Count == 0)
                return;

            if (centre == null)
            {
                log.Warn($"[WORLDEVENT] run={evt.RunId} decor skipped: the composition has no anchor position");
                return;
            }

            var landblock = evt.AnchorLandblock;

            if (landblock == null)
            {
                log.Warn($"[WORLDEVENT] run={evt.RunId} decor skipped: no anchor landblock is held");
                return;
            }

            // Construction only - safe on the world thread, exactly as the creature paths do it.
            var candidates = new List<DecorCandidate>();

            foreach (var entry in decor)
            {
                if (entry == null || entry.Wcid == 0)
                    continue;

                WorldObject wo;

                try
                {
                    wo = WorldObjectFactory.CreateNewWorldObject(entry.Wcid);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} could not create decor wcid {entry.Wcid}", ex);
                    continue;
                }

                if (wo == null)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} decor wcid {entry.Wcid} does not exist; skipped");
                    continue;
                }

                candidates.Add(new DecorCandidate(wo, entry));
            }

            if (candidates.Count == 0)
                return;

            var runId = evt.RunId;
            var centreSnapshot = new Position(centre);

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                // Same race the creature paths guard: the run may have finished between the enqueue and here,
                // and nothing has been adopted yet, so the objects are thrown away rather than left standing
                // with TimeToRot = -1 and nothing that will ever destroy them.
                if (!evt.RunValid(runId) || IsClosed || evt.AnchorLandblock == null)
                {
                    foreach (var candidate in candidates)
                    {
                        if (!candidate.Object.IsDestroyed)
                            candidate.Object.Destroy();
                    }

                    return;
                }

                try
                {
                    PlaceDecor(evt, candidates, centreSnapshot);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={runId} decor placement threw", ex);
                }
            }));
        }

        /// <summary>
        /// WP-15 spawn-time NPCs: the theme's attendants, placed once around the geometry CENTRE at the
        /// per-entry (Dx, Dy) offset, each facing the heading its own Yaw names.
        ///
        /// Same enqueue-on-the-anchor-landblock -> EnterWorld -> Adopt recipe as every other path. Unlike
        /// decor, an npc IS a Creature, so:
        ///
        ///   * the "not a Creature" refusal DOES apply - a Generic wcid in the npcs list is refused with
        ///     the same log line the wave path uses;
        ///   * it is snapped to terrain, because it has to stand on the ground rather than hang in the air;
        ///
        /// and unlike every creature path, it is adopted with NO P_WorldEvent back-reference and into its
        /// own list, so it counts toward nothing and its death (which the shipped weenies cannot even have,
        /// being non-attackable) can never reach the run. There is no jitter retry: an attendant belongs at
        /// the exact offset the scene was reviewed at, and nudging it 2 m would break the composition it
        /// exists for.
        /// </summary>
        public void SpawnNpcs(WorldEvent evt, IReadOnlyList<NpcDef> npcs, Position centre)
        {
            if (evt == null || npcs == null || npcs.Count == 0)
                return;

            if (centre == null)
            {
                log.Warn($"[WORLDEVENT] run={evt.RunId} npcs skipped: the composition has no anchor position");
                return;
            }

            var landblock = evt.AnchorLandblock;

            if (landblock == null)
            {
                log.Warn($"[WORLDEVENT] run={evt.RunId} npcs skipped: no anchor landblock is held");
                return;
            }

            // Construction only - safe on the world thread, exactly as the other paths do it.
            var candidates = new List<NpcCandidate>();

            foreach (var entry in npcs)
            {
                if (entry == null || entry.Wcid == 0)
                    continue;

                WorldObject wo;

                try
                {
                    wo = WorldObjectFactory.CreateNewWorldObject(entry.Wcid);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} could not create npc wcid {entry.Wcid}", ex);
                    continue;
                }

                if (wo == null)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} npc wcid {entry.Wcid} does not exist; skipped");
                    continue;
                }

                if (!(wo is Creature creature))
                {
                    // RequiresCreature(Npc) is true, so the same refusal the wave path makes applies here.
                    log.Error($"[WORLDEVENT] run={evt.RunId} wcid {entry.Wcid} is not a Creature; it can never die, so it is not spawned");
                    wo.Destroy();
                    continue;
                }

                candidates.Add(new NpcCandidate(creature, entry));
            }

            if (candidates.Count == 0)
                return;

            var runId = evt.RunId;
            var centreSnapshot = new Position(centre);

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                // The same race every other path guards: the run may have finished between the enqueue and
                // here, and nothing has been adopted yet, so the objects are thrown away rather than left
                // standing with TimeToRot = -1 and nothing that will ever destroy them.
                if (!evt.RunValid(runId) || IsClosed || evt.AnchorLandblock == null)
                {
                    foreach (var candidate in candidates)
                    {
                        if (!candidate.Object.IsDestroyed)
                            candidate.Object.Destroy();
                    }

                    return;
                }

                try
                {
                    PlaceNpcs(evt, candidates, centreSnapshot);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={runId} npc placement threw", ex);
                }
            }));
        }

        /// <summary>
        /// Called from WorldEvent.OnCreatureDied, i.e. from Creature.Die on a landblock tick thread.
        /// </summary>
        public void OnDied(Creature creature)
        {
            if (creature == null)
                return;

            lock (sync)
            {
                // C16: the boss's own death, stamped on this spawner's clock so bossTtkSeconds is measured
                // on the SAME clock as the adopt. Only the first death is recorded - a boss cannot die twice
                // and a duplicate hook must not restate the time.
                if (bossGuid != 0 && creature.Guid.Full == bossGuid && bossDiedAt <= 0)
                {
                    bossDiedAt = clock();

                    try
                    {
                        bossMaxHealth = creature.Health.MaxValue;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] could not read the boss's final health", ex);
                    }
                }

                liveWave.Remove(creature);
                liveObjective.Remove(creature);

                Prune();

                liveSourceGuids.Remove(creature.Guid.Full);

                // TECH-DESIGN 2.15: this is the one place a wave creature is retired by DYING, which is
                // what "cleared" means. Prune() also drops corpses, but it cannot say when, and a wave the
                // players never finished must not be reported as a fast clear.
                NoteWaveDeath(creature.Guid.Full);
            }
        }

        /// <summary>Caller must hold <see cref="sync"/>. See <see cref="OnDied"/> and <see cref="Adopt"/>.</summary>
        private void NoteWaveSpawn(int waveIndex, uint guid, bool overflowChampion)
        {
            if (overflowChampion)
                overflowChampionGuids.Add(guid);

            if (waveIndex < 0)
                return;

            if (!waveRecords.TryGetValue(waveIndex, out var record))
            {
                // Stamped at the FIRST adopt of the wave, i.e. when it actually lands on the landblock -
                // not when the pick was made on the world thread, which can be a queue drain earlier.
                record = new WaveRecord { SpawnedAt = clock() };
                waveRecords[waveIndex] = record;
            }

            record.Alive++;
            waveIndexByGuid[guid] = waveIndex;
        }

        /// <summary>Caller must hold <see cref="sync"/>.</summary>
        private void NoteWaveDeath(uint guid)
        {
            if (!waveIndexByGuid.TryGetValue(guid, out var waveIndex))
                return;

            waveIndexByGuid.Remove(guid);

            if (!waveRecords.TryGetValue(waveIndex, out var record))
                return;

            record.Alive--;

            if (record.Alive > 0 || record.Cleared)
                return;

            record.Cleared = true;

            clearedWaves.Enqueue(new WorldEventWaveClear(waveIndex, clock() - record.SpawnedAt));
        }

        /// <summary>
        /// TECH-DESIGN 2.4 DestroyAll, split in two passes by decision (2.2 step 5 vs 2.7):
        ///
        ///   * <paramref name="includeCaches"/> FALSE - the creature pass, run from Finish the moment a run
        ///     resolves. Destroys every held object that is not a Weave Cache, so no event monster lingers
        ///     while players walk up to the caches, and leaves the caches themselves standing for the claim
        ///     window.
        ///   * <paramref name="includeCaches"/> TRUE - the final sweep, run from CompleteCleanup. Closes the
        ///     held list and destroys whatever is still standing. On the rewarding path that is the caches
        ///     DestroyCaches has just queued (plus anything that slipped through); on an aborted run, where
        ///     Rewarding never happened and no cache exists, it is the ONLY pass.
        ///
        /// Either pass iterates the event's FULL held list rather than only what this spawner placed,
        /// because WP-05's reward caches live in the same list.
        ///
        /// Each pass is once-only, and the fixed 5.2 cleanup line is emitted by whichever pass runs FIRST -
        /// so a monitoring query written against that format sees exactly one line per run either way.
        /// </summary>
        public void DestroyAll(WorldEvent evt, bool includeCaches)
        {
            if (evt == null)
                return;

            lock (sync)
            {
                if (includeCaches)
                {
                    if (finalPassRan)
                    {
                        log.Debug($"[WORLDEVENT] run={evt.RunId} final cleanup sweep called again; ignored");
                        return;
                    }

                    finalPassRan = true;
                }
                else
                {
                    if (creaturePassRan)
                    {
                        log.Debug($"[WORLDEVENT] run={evt.RunId} creature cleanup called again; ignored");
                        return;
                    }

                    creaturePassRan = true;
                }
            }

            if (includeCaches)
            {
                // Nothing may be adopted past this point: this pass has decided what it destroys, and a
                // late add would sit in the world with TimeToRot = -1 and nothing left to remove it.
                evt.CloseHeld();
            }

            var targets = new List<WorldObject>();

            foreach (var wo in evt.HeldSnapshot())
            {
                // Decor is never a cache, so DestroyedByPass is true for it in BOTH passes: the creature
                // pass at Finish is what clears the sky, and the final sweep is the safety net.
                //
                // WP-19: the reward-cache DRESSING is the one exception. It is not a cache (it must not
                // carry PropertyBool.WorldEventCache - see WorldEvent.IsCacheDecor), but it shares the
                // cache lifetime, so it takes the cache side of this split. In practice the dressing is
                // placed after this pass has already taken its snapshot; this makes the guarantee hold
                // regardless of how the landblock queue and the world thread interleave.
                // wo may be null here - the held list tolerates one, and the destroy loop below skips it -
                // so the guid read is guarded rather than the null being filtered out, which would change
                // the destroyed= count this pass reports.
                if (!DestroyedByPass(IsCache(wo) || (wo != null && evt.IsCacheDecor(wo.Guid.Full)), includeCaches))
                    continue;

                targets.Add(wo);
            }

            // WP-20 item 4: how many WAVE creatures this pass takes off the field while they are still
            // ALIVE. Counted against a snapshot of liveWave taken here rather than from LiveCount read at
            // the caller, so the number names the creatures this pass actually destroyed alive rather than a
            // count sampled at a different instant. With the WP-20 cadence term in place this should sit at
            // or near zero on a normal completion; a large number means waves are still outrunning the
            // objective.
            HashSet<uint> aliveWaveGuids;

            lock (sync)
            {
                Prune();
                aliveWaveGuids = new HashSet<uint>(liveWave.Where(c => c != null).Select(c => c.Guid.Full));
            }

            // 2026-09-05: adds only. A generator profile can spawn an ITEM, so a player may be carrying one
            // by the time this runs, and the object would then be destroyed out of their pack. Read once
            // here rather than per-object inside the loop, which would take sync N times.
            HashSet<uint> addGuidsSnapshot;

            lock (sync)
                addGuidsSnapshot = new HashSet<uint>(addGuids);

            var scheduled = 0;
            var aliveWaveDestroyed = 0;

            foreach (var wo in targets)
            {
                if (wo == null || wo.IsDestroyed)
                    continue;

                if (addGuidsSnapshot.Contains(wo.Guid.Full) && IsPlayerTaken(wo))
                    continue;

                if (wo is Creature creature)
                {
                    // Anything already dead or dying is left alone: Creature.Die queues a delayed chain that
                    // builds the corpse once the death animation finishes, and destroying the creature out
                    // from under that chain would have it corpse an object already removed from the
                    // landblock (the same guard Player_WaveChallenge and GeneratorProfile.DestroyAll take).
                    //
                    // THIS NULL IS LOAD-BEARING FOR THREAD SAFETY, not just for tidiness. It runs on the
                    // creature pass at Finish, i.e. the instant the run enters Rewarding, and it - together
                    // with the creaturePassRan latch above, which makes Adopt refuse every later
                    // back-reference - is the ONLY thing that stops a kill landed during the claim window
                    // from writing to WorldEventParticipation's unsynchronized dictionary while the claim
                    // path reads it from a landblock thread. OnEventCreatureDied has no Rewarding guard of
                    // its own (WorldEventManager.cs:533), so nothing else would. Two consequences: this
                    // assignment must stay AHEAD of the IsDead early-out just below (a dead-but-not-yet
                    // corpsed creature must be cut loose too), and nothing added to this loop may throw -
                    // a throw here leaves the remaining creatures holding a live back-reference with the
                    // pass already latched. See WorldEventParticipation's class comment.
                    creature.P_WorldEvent = null;

                    if (creature.IsDead)
                        continue;
                }

                var landblock = wo.CurrentLandblock;
                var target = wo;

                if (landblock != null)
                    landblock.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        if (!target.IsDestroyed)
                            target.Destroy();
                    }));
                else
                    target.Destroy();

                scheduled++;

                if (aliveWaveGuids.Contains(target.Guid.Full))
                    aliveWaveDestroyed++;
            }

            bool emitCleanupLine;

            lock (sync)
            {
                liveWave.Clear();
                liveObjective.Clear();

                // Cleared on both passes: the creature pass already queued every decor destroy (decor is not
                // a cache), so DecorCount must read 0 from that point rather than only after the final sweep.
                liveDecor.Clear();

                // Same reasoning for the WP-15 attendants: they are not caches either, so the creature pass
                // has already queued their destroys and npcs= must read 0 from that point.
                liveNpcs.Clear();

                // Same again for adds (2026-09-05): not caches, so the creature pass has queued their
                // destroys and adds= must read 0 from that point. addGuids is deliberately NOT cleared -
                // the DestroyAll loop above and WorldEvent's straggler sweep both still need to recognise
                // an add after this, and the set is bounded by what the run generated.
                liveAdds.Clear();

                emitCleanupLine = !cleanupLineEmitted;

                if (emitCleanupLine)
                {
                    cleanupLineEmitted = true;
                    cleanupTargets = targets;
                }
            }

            // Beside the fixed 5.2 cleanup line rather than part of it, and with its own leading keyword so
            // a monitoring query written against "cleanup destroyed=" cannot match it. Emitted by whichever
            // pass runs FIRST, so it appears exactly once per run on the rewarding path and on an aborted
            // run alike.
            if (emitCleanupLine)
                log.Info($"[WORLDEVENT] run={evt.RunId} finish aliveWaveDestroyed={aliveWaveDestroyed}");

            if (!emitCleanupLine)
            {
                // The 5.2 line already went out with the creature pass. Report the sweep only when it
                // actually found something, and never in a format the cleanup query would match.
                if (scheduled > 0)
                    log.Info($"[WORLDEVENT] run={evt.RunId} sweep destroyed={scheduled}");

                return;
            }

            if (scheduled == 0)
            {
                // Nothing was queued, so there is nothing a deferred check could observe. Report now rather
                // than burning a chain and emitting the fixed cleanup line a second later.
                LogCleanup(evt, 0, null);
                return;
            }

            try
            {
                var chain = new ActionChain();
                chain.AddDelaySeconds(SurvivorCheckDelaySeconds);
                chain.AddAction(WorldManager.ActionQueue, () => LogCleanup(evt, scheduled, SurvivorsSnapshot()));
                chain.EnqueueChain();
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} could not arm the cleanup survivor check", ex);
                LogCleanup(evt, scheduled, SurvivorsSnapshot());
            }
        }

        /// <summary>
        /// A Weave Cache, by the same marker property the claim handler and DestroyCaches dispatch on. Kept
        /// public so the split can be unit tested without a live WorldObject (D6).
        /// </summary>
        public static bool IsCache(WorldObject wo)
        {
            return wo != null && wo.GetProperty(PropertyBool.WorldEventCache) == true;
        }

        /// <summary>
        /// Guids from the last DestroyAll pass (or, before one has run, everything this spawner placed) that
        /// are still not destroyed. A creature that is dead but whose own die chain has not fired yet is NOT
        /// reported: it was deliberately skipped by DestroyAll and is already on its way out.
        /// </summary>
        public IReadOnlyList<uint> SurvivorsSnapshot()
        {
            lock (sync)
            {
                var source = cleanupTargets ?? spawned;

                var survivors = new List<uint>();

                foreach (var wo in source)
                {
                    if (wo == null || wo.IsDestroyed)
                        continue;

                    if (wo is Creature creature && creature.IsDead)
                        continue;

                    // 2026-09-05: a generated ITEM add a player picked up was deliberately skipped by
                    // DestroyAll (see the addGuids guard there). It is not a survivor - it is loot - and
                    // reporting it would put a false alarm in the fixed 5.2 cleanup line.
                    if (addGuids.Contains(wo.Guid.Full) && IsPlayerTaken(wo))
                        continue;

                    survivors.Add(wo.Guid.Full);
                }

                return survivors;
            }
        }

        // ---- placement ------------------------------------------------------------------------------

        /// <summary>
        /// The one creature placement path. Two independent health rules meet here and must not be confused:
        /// <paramref name="healthMult"/> is the crowd x pace multiplier (TECH-DESIGN 2.15) and is a WAVE
        /// rule - every non-wave caller passes 1.0, which is a no-op - while a Source spawn's absolute
        /// health comes from the theme's <c>objectiveHealth</c> and is applied later, in TryPlace
        /// (WP-21, see ApplyObjectiveHealth). They never both bite one creature: the Source callers
        /// (SpawnSources/SpawnObjectives) pass healthMult 1.0, and objectiveHealth is only read for
        /// kind == Source.
        ///
        /// <paramref name="dzByAnchor"/> is WP-21's per-anchor post-snap dz, indexed alongside
        /// <paramref name="anchors"/> (NOT alongside <paramref name="entries"/> - dz belongs to the anchor
        /// point, and candidates reuse anchors round-robin). Null for every caller but SpawnObjectives.
        /// </summary>
        private void Spawn(WorldEvent evt, IReadOnlyList<SpawnEntry> entries, IReadOnlyList<Position> anchors,
            WorldEventSpawnKind kind, int waveIndex, LevelBand band, double healthMult,
            IReadOnlyList<float> dzByAnchor = null)
        {
            if (evt == null || entries == null || entries.Count == 0)
                return;

            if (!RequiresCreature(kind))
            {
                // Decor is not a Creature and would be refused by the guard below. It has its own path.
                log.Error($"[WORLDEVENT] run={evt.RunId} {kind} must be placed by SpawnDecor, not the creature path");
                return;
            }

            var landblock = evt.AnchorLandblock;

            if (landblock == null)
            {
                log.Warn($"[WORLDEVENT] run={evt.RunId} {kind} spawn skipped: no anchor landblock is held");
                LogWaveIfWave(evt, kind, waveIndex, band, 0);
                NoteBossPlacementResolved(kind, placed: false);
                return;
            }

            if (anchors == null || anchors.Count == 0)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} {kind} spawn skipped: no spawn anchors were computed");
                LogWaveIfWave(evt, kind, waveIndex, band, 0);
                NoteBossPlacementResolved(kind, placed: false);
                return;
            }

            // Steps 1 and 2 of the recipe are pure object construction and may run on the world thread.
            var candidates = new List<SpawnCandidate>();

            foreach (var entry in entries)
            {
                var wcid = entry.Wcid;

                if (wcid == 0)
                    continue;

                WorldObject wo;

                try
                {
                    wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} could not create wcid {wcid}", ex);
                    continue;
                }

                if (wo == null)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} wcid {wcid} does not exist; skipped");
                    continue;
                }

                if (!(wo is Creature creature))
                {
                    // A non-Creature can never die, so it would hold a kill objective open for the rest of
                    // the run - exactly the failure Player_WaveChallenge guards against.
                    log.Error($"[WORLDEVENT] run={evt.RunId} wcid {wcid} is not a Creature; it can never die, so it is not spawned");
                    wo.Destroy();
                    continue;
                }

                // A roster-drawn monster fights the PLAYERS, not the rest of its own wave (2026-09-07).
                // Retail carries three reasons for one monster to attack another - a Tolerance that excludes
                // players, faction bits, and FoeType - and the species tables are mined from retail content,
                // so wave draws inherit all three; see SpawnedCreatureHostility for the measured counts and
                // for why the Source/InertObjective kinds this method also places are excluded.
                //
                // Here rather than in TryPlace, alongside the other pre-spawn property writes, and on the
                // world thread during construction: everything after this point up to EnterWorld
                // (ApplyBaseHealthOverride, ApplyHealthMultiplier, ApplySyntheticPromotion, ApplyObjectiveHealth,
                // ApplyBossTether) writes health, names and tether fields and reads none of Tolerance,
                // Faction*Bits, FoeType, IsMonster or IsFactionMob, so nothing downstream sees a stale value.
                if (SpawnedCreatureHostility.NormalizesHostility(kind))
                    SpawnedCreatureHostility.MakeHostileToPlayers(creature);

                var candidate = new SpawnCandidate(creature, entry.OverflowChampion, entry.Kind, entry.Synthetic);

                // bosses.json's optional baseHealth, applied first so the numbers captured below are the
                // boss's REAL base and the multiplier below is applied to that.
                if (kind == WorldEventSpawnKind.Boss)
                    ApplyBaseHealthOverride(evt, creature);

                // Read BEFORE the multiplier is applied, so the boss ratchet has the creature's own base
                // numbers to recompute from rather than an already-scaled pair.
                try
                {
                    candidate.AuthoredStartingValue = creature.Health.StartingValue;
                    candidate.AuthoredBaseMax = creature.Health.MaxValue;
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} could not read authored health for wcid {wcid}", ex);
                }

                // TECH-DESIGN 2.15: raised HERE, on the world thread, between construction and the queued
                // EnterWorld. Health.Current is re-seeded from the new maximum in the same step, so the
                // creature enters the world at full health rather than at its unscaled value.
                ApplyHealthMultiplier(evt, creature, healthMult);

                // WP-24: a synthetic elite/champion (a role-0 member promoted because the band had no real
                // one) is renamed and given a further health multiplier ON TOP of healthMult, here - before
                // EnterWorld, so the create packet the client first sees already carries the new name.
                if (entry.Synthetic)
                    ApplySyntheticPromotion(evt, creature, entry.Kind);

                candidates.Add(candidate);
            }

            if (candidates.Count == 0)
            {
                LogWaveIfWave(evt, kind, waveIndex, band, 0);
                NoteBossPlacementResolved(kind, placed: false);
                return;
            }

            var anchorSnapshot = new List<Position>(anchors);
            // WP-21: the fixed-offset objective per-anchor dz, snapshotted alongside the anchors themselves
            // (same indexing - see PlaceOnLandblock). Null for every caller but SpawnObjectives, which is
            // what makes this whole thread inert everywhere else.
            var dzSnapshot = dzByAnchor != null ? new List<float>(dzByAnchor) : null;
            var runId = evt.RunId;

            // 2026-08-16: an objective-boss attempt is now IN FLIGHT from this point until the queued
            // delegate below resolves it one way or another - see BossPlacementPending's remarks. Every
            // synchronous early-out above this line never enqueued anything, so it went through
            // NoteBossPlacementResolved(placed: false) instead and never set this.
            if (kind == WorldEventSpawnKind.Boss)
            {
                lock (sync)
                    bossPlacementPending = true;
            }

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                var placed = 0;

                try
                {
                    // The landblock queue drains on a landblock-group thread, which runs concurrently with
                    // the world thread that ends runs - so between the enqueue and here the run may have
                    // finished, cleaned up, or dropped its hold. Nothing was adopted yet, so the objects are
                    // thrown away rather than left in the world with TimeToRot = -1 and nothing that will
                    // ever destroy them.
                    if (!evt.RunValid(runId) || IsClosed || evt.AnchorLandblock == null)
                    {
                        foreach (var candidate in candidates)
                        {
                            if (!candidate.Creature.IsDestroyed)
                                candidate.Creature.Destroy();
                        }

                        LogWaveIfWave(evt, kind, waveIndex, band, 0);
                        return;
                    }

                    placed = PlaceOnLandblock(evt, candidates, anchorSnapshot, kind, waveIndex, band, dzSnapshot);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={runId} {kind} placement threw", ex);
                }
                finally
                {
                    // 2026-08-16: resolved - whichever of the three exits above was taken (adopted, thrown
                    // away/skipped, or an exception) - so WorldEvent.TickChampion never reads a stale
                    // "still pending" state past this point.
                    NoteBossPlacementResolved(kind, placed: placed > 0);
                }
            }));
        }

        /// <summary>
        /// 2026-08-16: records the resolution of an objective-boss placement attempt - clears
        /// <see cref="bossPlacementPending"/> and, when nothing was adopted, counts a refusal. A no-op for
        /// every other <paramref name="kind"/>. Called from every exit of <see cref="Spawn"/> that resolves
        /// (or never even issues) a Boss-kind attempt: the three synchronous early-outs above (never
        /// enqueued, so always <c>placed: false</c>) and the queued delegate's try/finally (whatever
        /// actually landed).
        /// </summary>
        private void NoteBossPlacementResolved(WorldEventSpawnKind kind, bool placed)
        {
            if (kind != WorldEventSpawnKind.Boss)
                return;

            lock (sync)
            {
                bossPlacementPending = false;

                if (!placed)
                    bossPlacementRefusals++;
            }
        }

        private int PlaceOnLandblock(WorldEvent evt, List<SpawnCandidate> candidates, List<Position> anchors,
            WorldEventSpawnKind kind, int waveIndex, LevelBand band, IReadOnlyList<float> dzByAnchor = null)
        {
            var placed = 0;
            var skipped = 0;

            // WP-17 sky-drop. Read from the run's own composition rather than threaded through SpawnWave, so
            // no other call site has to know the field exists. 0 for every kind but Wave and for every theme
            // that does not set spawnDz, which is what makes this whole path inert by default.
            var themeDz = evt.Composition?.Source?.SpawnDz ?? 0f;
            var skyDropDz = AppliesSkyDrop(kind, themeDz) ? themeDz : 0f;

            // WP-18 item 2. A "disc" theme samples anywhere inside its radius, so an individual sample can
            // land in a building, on a cliff or in water - and jittering THAT point by 2 m stays inside the
            // same obstruction, which is why the owner saw "2 of 5 spawns could not be placed". For a wave
            // on a disc, a retry therefore draws a whole new point from the disc instead. Every other
            // geometry keeps the jitter: a ring/edges/single anchor was chosen deliberately and a retry
            // should stay near it.
            var theme = evt.Composition?.Source;
            var geometry = theme?.GeometryKind ?? SourceGeometry.Single;
            var resample = ResamplesOnRetry(kind, geometry);
            var attempts = PlacementAttemptsFor(kind, geometry);

            Position lastRejected = null;

            try
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidateEntry = candidates[i];
                    var creature = candidateEntry.Creature;

                    var basePosition = anchors[i % anchors.Count];

                    // WP-21: the fixed-offset objective's post-terrain-snap dz, indexed the same way as
                    // basePosition - see SpawnObjectives/dzByAnchor's remarks. 0 for every kind but Source
                    // with no dzByAnchor threaded through, which is every caller except SpawnObjectives.
                    var postSnapDz = dzByAnchor != null ? dzByAnchor[i % anchors.Count] : 0f;

                    // More creatures than anchors: reuse them round-robin, but nudge each reuse so a stack of
                    // creatures does not land on one exact point.
                    var reused = i >= anchors.Count;

                    var success = false;

                    for (var attempt = 0; attempt < attempts && !success; attempt++)
                    {
                        Position candidate;

                        if (attempt == 0 && !reused)
                            candidate = new Position(basePosition);
                        else if (resample)
                            // Falls back to a jitter if the theme cannot produce a sample (no anchor
                            // position), so a resampling theme is never worse off than a jittering one.
                            candidate = NextDiscSample(theme, evt.Composition?.AnchorPosition) ?? NextJitter(basePosition);
                        else if (kind == WorldEventSpawnKind.Boss)
                            // 2026-08-16: the boss's own jitter widens by attempt, so a champion stuck at a
                            // transiently blocked anchor fans further out on each retry within this Spawn
                            // call - separate from WorldEvent's tick-level retry, which cycles anchors.
                            candidate = NextJitter(basePosition, BossRetryJitterMetres(attempt));
                        else
                            candidate = NextJitter(basePosition);

                        success = TryPlace(evt, creature, candidate, skyDropDz, kind, postSnapDz);

                        if (!success)
                        {
                            lastRejected = candidate;

                            log.Debug($"[WORLDEVENT] run={evt.RunId} {kind} attempt {attempt + 1}/{attempts} rejected at {candidate?.ToLOCString()}");
                        }
                    }

                    if (!success || !Adopt(evt, creature, kind, waveIndex, candidateEntry.OverflowChampion,
                            candidateEntry.AuthoredStartingValue, candidateEntry.AuthoredBaseMax))
                    {
                        if (!success)
                            skipped++;

                        if (!creature.IsDestroyed)
                            creature.Destroy();

                        continue;
                    }

                    placed++;

                    log.Info($"[WORLDEVENT] run={evt.RunId} spawn wcid={creature.WeenieClassId} guid=0x{creature.Guid.Full:X8} loc={creature.Location?.ToLOCString()}");

                    if (skyDropDz > 0f)
                        BeginSkyDrop(evt, creature, skyDropDz);

                    // The boss's INTRO line (BOSS-STANDARD.md section 2) is authored as an
                    // EmoteCategory.Generation set on the weenie. WorldObject.EnterWorld only fires that
                    // category when Generator != null, and a world-event spawn never has a generator, so
                    // the set would otherwise never run. Fired here, once, immediately after adoption -
                    // and only for the boss, because a wave of twenty would broadcast twenty lines.
                    if (kind == WorldEventSpawnKind.Boss)
                    {
                        try
                        {
                            creature.EmoteManager.OnGeneration();
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[WORLDEVENT] run={evt.RunId} boss intro emote threw", ex);
                        }
                    }
                }

                if (skipped > 0)
                    log.Warn($"[WORLDEVENT] run={evt.RunId} {kind}: {skipped} of {candidates.Count} spawns could not be placed after {attempts} attempts each; placed={placed} lastRejected={lastRejected?.ToLOCString() ?? "none"}");
            }
            finally
            {
                // In a finally so a throw partway through a wave still reports what actually landed, rather
                // than losing the wave line for that wave entirely.
                LogWaveIfWave(evt, kind, waveIndex, band, placed);
            }

            return placed;
        }

        private bool TryPlace(WorldEvent evt, Creature creature, Position candidate, float skyDropDz,
            WorldEventSpawnKind kind, float postSnapDz = 0f)
        {
            if (creature.IsDestroyed || candidate == null)
                return false;

            try
            {
                // Outdoor anchors carry the centre's Z, which is only right at the centre. Snapping to the
                // terrain here (and not in the geometry) is what keeps WorldEventGeometry pure.
                if (!candidate.Indoors)
                {
                    candidate.AdjustMapCoords();

                    // WP-17 sky-drop, the WP-15 npc Dz pattern exactly: AdjustMapCoords overwrites Z with the
                    // terrain height, so the drop height is re-applied ON TOP of the snapped ground rather
                    // than left as an offset from the anchor's Z. That is what makes spawnDz mean "metres
                    // above the ground under this creature", and it is why the snap is never skipped.
                    //
                    // Indoors gets nothing: there is no terrain to fall to and no sky to fall from, and the
                    // one theme that sets spawnDz is an open-sky scene.
                    candidate.PositionZ = SkyDropZ(candidate.PositionZ, skyDropDz);

                    // WP-21: the fixed-offset objective's dz, the WP-15 npc rule exactly (PlaceNpcs
                    // re-applies def.Dz on top of the snapped ground) - see ObjectiveSnapZ's remarks. 0 for
                    // every kind but Source spawned via SpawnObjectives.
                    candidate.PositionZ = ObjectiveSnapZ(candidate.PositionZ, postSnapDz);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} terrain snap failed at {candidate.ToLOCString()}", ex);
                return false;
            }

            if (!evt.IsHeldLandblock(candidate.InstancedLandblock))
            {
                // The same guard Spawn_Specific and the wave spawner take: a position outside the block the
                // event holds would drag a creature into a landblock nobody is keeping awake, and (with
                // adjacents not held) one the event cannot clean up reliably.
                log.Debug($"[WORLDEVENT] run={evt.RunId} candidate {candidate.ToLOCString()} is outside the held landblock(s); retrying");
                return false;
            }

            creature.Location = candidate;

            // Stamped BEFORE the object enters the world, so there is no window in which a landblock save
            // could see a live event object as an ordinary persistable dynamic (TECH-DESIGN 2.9).
            creature.SetProperty(PropertyInt.WorldEventId, (int)evt.RunId);

            // Mandatory - see the class remarks.
            creature.TimeToRot = -1;

            // WP-21 objective health scaling. Applied BEFORE EnterWorld, the same rule every other
            // pre-spawn property write in this method follows - there must be no window in which the
            // client, or a landblock save, sees the creature with its weenie-shipped health.
            if (kind == WorldEventSpawnKind.Source)
            {
                var objectiveHealth = evt.Composition?.Source?.ObjectiveHealth;

                if (objectiveHealth != null)
                    ApplyObjectiveHealth(evt, creature, objectiveHealth);
            }

            // 2026-08-19: the same objective weenie placed as SCENERY, because this run's goal is not
            // DestroySource. Written BEFORE EnterWorld for the same reason every other pre-spawn write in
            // this method is - the create packet the client first sees must already carry them, or the
            // pillar arrives as a live target and only stops being one on some later update.
            //
            // Attackable false is what the weenie's own PropertyBool 19 would say if it had been authored as
            // scenery; UiHidden suppresses the name label and the target cursor (a Creature still takes one
            // otherwise); RadarBehavior.ShowNever takes the blip off the radar. The objectiveHealth block
            // above stays Source-only on purpose: nothing here can be damaged, so scaling its health would
            // be bookkeeping with no observable effect.
            if (kind == WorldEventSpawnKind.InertObjective)
            {
                creature.Attackable = false;
                creature.UiHidden = true;
                creature.RadarBehavior = ACE.Entity.Enum.RadarBehavior.ShowNever;
            }

            ApplyBossTether(evt, creature, kind);

            try
            {
                if (!creature.EnterWorld())
                    return false;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} EnterWorld threw for wcid {creature.WeenieClassId} at {candidate.ToLOCString()}", ex);
                return false;
            }

            return true;
        }

        /// <summary>
        /// WP-21: sets <paramref name="creature"/>'s max health to <c>objectiveHealth.Resolve(participants)</c>
        /// and its current health to that new max, BEFORE the creature enters the world (see the TryPlace
        /// call site).
        ///
        /// Follows the precedent at Source/ACE.Server/WorldObjects/CombatPet.cs:62-63 (Empowered Summons'
        /// pet health scaling): adjust CreatureVital.StartingValue by the delta needed to reach the target,
        /// rather than writing MaxValue directly - MaxValue is a computed getter (CreatureVital.GetMaxValue),
        /// there is no setter. StartingValue is CreatureVital.InitLevel, the persisted base the getter folds
        /// Ranks/AttributeFormula/enchantments on top of; delta-ing it is what lets the same recipe apply on
        /// top of whatever the weenie already grants, exactly as CombatPet's bonusFraction case does.
        /// </summary>
        private void ApplyObjectiveHealth(WorldEvent evt, Creature creature, ScaledCount objectiveHealth)
        {
            var participants = Math.Max(0, evt.ResolvedAudienceCount);
            var hp = objectiveHealth.Resolve(participants);

            var clamped = ApplyHealth(creature, hp);

            if (clamped)
            {
                log.Warn($"[WORLDEVENT] run={evt.RunId} source wcid={creature.WeenieClassId} objectiveHealth " +
                         $"target={hp} could not be reached (weenie's native StartingValue too small to reach it " +
                         $"even at the floor); StartingValue clamped to 1, resulting MaxValue={creature.Health.MaxValue}");
            }

            log.Info($"[WORLDEVENT] run={evt.RunId} source wcid={creature.WeenieClassId} health={hp} (participants={participants})");
        }

        /// <summary>
        /// Boss confinement (2026-08-30). Stamps the tether properties on a named boss BEFORE it enters the
        /// world, the same rule every other pre-spawn write in <see cref="TryPlace"/> follows.
        ///
        /// The problem: one player kites the boss away from a twenty-player fight and the whole event stalls
        /// while the boss walks. HomeRadius alone does not fix it, because the leash only fires at 192 m by
        /// default and the return is passive.
        ///
        /// Three properties, all no-ops on any creature that does not carry them:
        ///   * TetherRadius - targeting is confined to players within this distance of Home, and stepping
        ///     outside it forces an immediate retarget (Creature.FindNextTarget / Creature.CheckMissHome).
        ///   * HomeRadius = twice the tether, so the ordinary leash still sits outside the tether and only
        ///     catches a boss with no in-tether player left to turn on.
        ///   * DisableSticky - the physics mover no longer latches onto the target it started toward.
        ///
        /// TargetingTactic is written ONLY when the weenie authored none. Every boss shipped today authors
        /// one, so in practice this branch does not fire; the tether confines whatever tactic the weenie
        /// chose rather than replacing it.
        ///
        /// Written through SetProperty rather than the Creature.TargetingTactic setter on purpose: that
        /// setter is <c>SetProperty(PropertyInt.TargetingTactic, (int)TargetingTactic)</c>, and under C#'s
        /// Color-Color rule the cast operand binds to the PROPERTY, not the type - so it re-writes the value
        /// it already had and discards <c>value</c>. Upstream bug, nothing else in the server calls it, and
        /// not this PR's to fix.
        /// </summary>
        private void ApplyBossTether(WorldEvent evt, Creature creature, WorldEventSpawnKind kind)
        {
            if (kind != WorldEventSpawnKind.Boss)
                return;

            var enabled = PropertyManager.GetBool("world_events_boss_tether_enabled").Item;
            var radius = PropertyManager.GetDouble("world_events_boss_tether_radius").Item;

            if (!AppliesBossTether(kind, enabled, radius))
                return;

            creature.TetherRadius = radius;
            creature.HomeRadius = BossHomeRadiusFor(radius);
            creature.DisableSticky = true;

            var authoredTactic = creature.GetProperty(PropertyInt.TargetingTactic);
            var stampedTactic = StampsBossTargetingTactic(authoredTactic);

            if (stampedTactic)
                creature.SetProperty(PropertyInt.TargetingTactic, (int)ACE.Entity.Enum.TargetingTactic.Nearest);

            log.Info($"[WORLDEVENT] run={evt.RunId} boss wcid={creature.WeenieClassId} tether={radius} " +
                     $"homeRadius={creature.HomeRadius} sticky=off targetingTactic=" +
                     (stampedTactic ? "Nearest (stamped)" : $"{authoredTactic} (authored, kept)"));
        }

        /// <summary>
        /// WP-22: the pure health-setting half of <see cref="ApplyObjectiveHealth"/>, extracted so
        /// WorldEventScenePreview (which has no live WorldEvent/RunId to log against) can apply the exact
        /// same math rather than a copy-pasted second implementation. Sets <paramref name="creature"/>'s max
        /// health to <paramref name="hp"/> and its current health to that new max, by adjusting
        /// CreatureVital.StartingValue by the delta needed to reach the target (see the class remarks on
        /// ApplyObjectiveHealth for why StartingValue and not MaxValue - MaxValue is a computed getter with
        /// no setter). Returns true when the weenie's native StartingValue was too small to reach hp even at
        /// the floor of 1, in which case StartingValue was clamped to 1 instead; the caller decides whether
        /// and how to log that.
        /// </summary>
        public static bool ApplyHealth(Creature creature, int hp)
        {
            var delta = hp - (int)creature.Health.MaxValue;
            var rawStarting = (int)creature.Health.StartingValue + delta;

            // A weenie whose native StartingValue is small enough that the delta needed to reach hp would
            // drive it negative is clamped to 1 (never 0 - a 0 StartingValue is a degenerate/dead vital).
            var clamped = rawStarting < 1;
            var newStarting = Math.Max(1, rawStarting);

            creature.Health.StartingValue = (uint)newStarting;
            creature.Health.Current = creature.Health.MaxValue;

            return clamped;
        }

        /// <summary>
        /// WP-17 sky-drop: arms one wave creature that has just been placed <paramref name="dz"/> metres up
        /// and adopted. Runs on the landblock's action queue, immediately after a successful Adopt.
        ///
        /// The flag Creature.ArmSkyDrop raises is what WorldObject_Tick.UpdateObjectPhysics reads to keep
        /// ticking the creature's physics while it falls (an asleep monster otherwise stops after its first
        /// frame and hangs in the air) and what Monster_Tick reads to hold its attacks; the deadline bounds
        /// that state so a creature that never reports OnWalkable is force-settled rather than left hanging.
        ///
        /// The creature is NOT woken here. The owner's "force-awake" requirement is honoured on LANDING
        /// instead (Creature.OnSkyDropLanded), which is the moment it means something - waking it mid-fall
        /// bought nothing and put an emote/motion, and therefore a lazy MovementManager creation that clears
        /// the physics Active flag, on the critical path of the fall. Creature.ArmSkyDrop's remarks carry
        /// the full argument and the citations.
        /// </summary>
        private void BeginSkyDrop(WorldEvent evt, Creature creature, float dz)
        {
            creature.ArmSkyDrop(ACE.Server.Physics.Common.PhysicsTimer.CurrentTime, SkyDropTimeoutSeconds);

            log.Info($"[WORLDEVENT] run={evt.RunId} skydrop wcid={creature.WeenieClassId} guid=0x{creature.Guid.Full:X8} dz={dz:F1}");
        }

        /// <summary>One constructed decor object and the entry it came from, carried into the landblock closure.</summary>
        private sealed class DecorCandidate
        {
            public DecorCandidate(WorldObject wo, DecorDef def)
            {
                Object = wo;
                Def = def;
            }

            public WorldObject Object { get; }

            public DecorDef Def { get; }
        }

        /// <summary>
        /// The decor half of the placement recipe. Runs on the anchor landblock's action queue.
        /// </summary>
        private void PlaceDecor(WorldEvent evt, List<DecorCandidate> candidates, Position centre)
        {
            foreach (var candidate in candidates)
            {
                var wo = candidate.Object;
                var def = candidate.Def;

                if (wo.IsDestroyed)
                    continue;

                var position = DecorPosition(centre, def.Dx, def.Dy, def.Dz, def.Inverted, def.Yaw, def.Pitch);

                if (def.Snap && !position.Indoors)
                {
                    try
                    {
                        // Ground scenery (DecorDef.Snap): mirrors PlaceNpcs exactly. AdjustMapCoords
                        // overwrites Z outright with the terrain height, so Dz is re-applied on top of the
                        // snapped ground rather than left as an offset from the anchor's Z. Rotation was
                        // already set by DecorPosition above and AdjustMapCoords never touches it, so no
                        // re-set is needed here.
                        position.AdjustMapCoords();
                        position.PositionZ += def.Dz;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={evt.RunId} decor terrain snap failed at {position.ToLOCString()}", ex);

                        wo.Destroy();
                        continue;
                    }
                }

                if (!evt.IsHeldLandblock(position.InstancedLandblock))
                {
                    log.Warn($"[WORLDEVENT] run={evt.RunId} decor wcid={def.Wcid} at {position.ToLOCString()} is outside the held landblock(s); skipped");

                    wo.Destroy();
                    continue;
                }

                wo.Location = position;

                // Stamped BEFORE the object enters the world, for the same reason the creature paths do it:
                // no window in which a landblock save could see a live event object as an ordinary
                // persistable dynamic (TECH-DESIGN 2.9).
                wo.SetProperty(PropertyInt.WorldEventId, (int)evt.RunId);

                wo.SetProperty(PropertyFloat.DefaultScale, def.Scale);
                wo.SetProperty(PropertyFloat.MotionSpeed, def.Speed);

                // AND directly, not instead: WorldObject.SetEphemeralValues already read MotionSpeed while
                // WorldObjectFactory.CreateNewWorldObject was building this object, so by the time the
                // property is written here the create packet's ForwardSpeed has long been seeded from the
                // weenie's value. Writing both keeps this correct whichever way that seeding moves later.
                // CurrentMotionState is null on an object with no MotionTable, which is a perfectly valid
                // (if motionless) decor weenie.
                if (wo.CurrentMotionState?.MotionState != null)
                    wo.CurrentMotionState.MotionState.ForwardSpeed = def.Speed;

                // Mandatory - see the class remarks. Decor has no Generator back-reference either, so the
                // landblock heartbeat would otherwise decay it out from under the run.
                wo.TimeToRot = -1;

                var entered = false;

                try
                {
                    entered = wo.EnterWorld();
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} decor wcid={def.Wcid} failed to enter the world at {position.ToLOCString()}", ex);
                }

                if (!entered)
                {
                    // No retry jitter: the origin is in the sky, so there is no blocked-spot case to nudge
                    // around (see SpawnDecor's remarks).
                    log.Error($"[WORLDEVENT] run={evt.RunId} decor wcid={def.Wcid} failed to enter the world at {position.ToLOCString()}");

                    if (!wo.IsDestroyed)
                        wo.Destroy();

                    continue;
                }

                if (!Adopt(evt, wo, WorldEventSpawnKind.Decor))
                {
                    if (!wo.IsDestroyed)
                        wo.Destroy();

                    continue;
                }

                log.Info($"[WORLDEVENT] run={evt.RunId} spawn wcid={wo.WeenieClassId} guid=0x{wo.Guid.Full:X8} loc={wo.Location?.ToLOCString()}");
            }
        }

        /// <summary>One constructed npc and the entry it came from, carried into the landblock closure.</summary>
        private sealed class NpcCandidate
        {
            public NpcCandidate(Creature creature, NpcDef def)
            {
                Object = creature;
                Def = def;
            }

            public Creature Object { get; }

            public NpcDef Def { get; }
        }

        /// <summary>
        /// The npc half of the placement recipe (WP-15). Runs on the anchor landblock's action queue.
        /// </summary>
        private void PlaceNpcs(WorldEvent evt, List<NpcCandidate> candidates, Position centre)
        {
            foreach (var candidate in candidates)
            {
                var creature = candidate.Object;
                var def = candidate.Def;

                if (creature.IsDestroyed)
                    continue;

                var position = NpcPosition(centre, def.Dx, def.Dy, def.Dz, def.Yaw);

                try
                {
                    // An npc is a creature and must stand on the ground, so unlike decor it IS snapped.
                    // AdjustMapCoords overwrites Z outright with the terrain height, so Dz is re-applied on
                    // top of the snapped ground rather than left as an offset from the anchor's Z - which is
                    // what makes Dz mean "metres above the ground under this npc" (see NpcDef).
                    if (!position.Indoors)
                    {
                        position.AdjustMapCoords();
                        position.PositionZ += def.Dz;
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} npc terrain snap failed at {position.ToLOCString()}", ex);

                    creature.Destroy();
                    continue;
                }

                if (!evt.IsHeldLandblock(position.InstancedLandblock))
                {
                    log.Warn($"[WORLDEVENT] run={evt.RunId} npc wcid={def.Wcid} at {position.ToLOCString()} is outside the held landblock(s); skipped");

                    creature.Destroy();
                    continue;
                }

                creature.Location = position;

                // Stamped BEFORE the object enters the world, for the same reason every other path does it:
                // no window in which a landblock save could see a live event object as an ordinary
                // persistable dynamic (TECH-DESIGN 2.9).
                creature.SetProperty(PropertyInt.WorldEventId, (int)evt.RunId);

                // Mandatory - see the class remarks. An npc has no Generator back-reference either, so the
                // landblock heartbeat would otherwise decay it out from under the run.
                creature.TimeToRot = -1;

                // Nothing else is written. Attackable, PlayerKillerStatus and RadarBehavior are whatever the
                // weenie ships (the Loz attendants ship non-attackable), and the engine must not depend on
                // that either way - a later scene may well want an npc that fights back.

                var entered = false;

                try
                {
                    entered = creature.EnterWorld();
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} npc wcid={def.Wcid} failed to enter the world at {position.ToLOCString()}", ex);
                }

                if (!entered)
                {
                    // No retry jitter: see SpawnNpcs's remarks - an attendant belongs where the scene put it.
                    log.Error($"[WORLDEVENT] run={evt.RunId} npc wcid={def.Wcid} failed to enter the world at {position.ToLOCString()}");

                    if (!creature.IsDestroyed)
                        creature.Destroy();

                    continue;
                }

                if (!Adopt(evt, creature, WorldEventSpawnKind.Npc))
                {
                    if (!creature.IsDestroyed)
                        creature.Destroy();

                    continue;
                }

                log.Info($"[WORLDEVENT] run={evt.RunId} spawn wcid={creature.WeenieClassId} guid=0x{creature.Guid.Full:X8} loc={creature.Location?.ToLOCString()}");
            }
        }

        /// <summary>
        /// Where one npc goes (D6 - pure, no engine object): the theme geometry centre offset by
        /// (<paramref name="dx"/> east, <paramref name="dy"/> north, <paramref name="dz"/> up), facing
        /// <paramref name="yawDegrees"/>.
        ///
        /// SetPosition is used rather than a raw PositionZ write - the opposite of
        /// <see cref="DecorPosition"/> - precisely BECAUSE X and Y move here: a 4.8 m offset can cross a
        /// land cell (and at the edge of a block, a landblock) boundary, and SetPosition is what re-addresses
        /// the position to the cell it actually landed in. The caller snaps Z to terrain afterwards; that
        /// needs a live landcell and so cannot happen in a pure helper.
        /// </summary>
        public static Position NpcPosition(Position centre, float dx, float dy, float dz, float yawDegrees)
        {
            if (centre == null)
                return null;

            var position = new Position(centre.LandblockId.Raw,
                centre.PositionX, centre.PositionY, centre.PositionZ,
                centre.RotationX, centre.RotationY, centre.RotationZ, centre.RotationW,
                centre.Instance);

            position.SetPosition(new Vector3(centre.PositionX + dx, centre.PositionY + dy, centre.PositionZ + dz));

            position.Rotation = NpcRotation(yawDegrees);

            return position;
        }

        /// <summary>
        /// The npc heading (D6 - pure): a rotation of <paramref name="yawDegrees"/> about +Z, i.e.
        /// (w=cos(yaw/2), x=0, y=0, z=sin(yaw/2)).
        ///
        /// Yaw 0 faces +Y (north) and positive yaw turns counter-clockwise seen from above, because a
        /// Position's facing is Vector3.Transform(Vector3.UnitY, Rotation) - Position.GetCurrentDir,
        /// Source/ACE.Entity/Position.cs:102-105. An npc at polar angle a that should face the centre
        /// therefore takes yaw a + 90; the three Weave Spiral values are pinned in
        /// WorldEventSpawnerNpcTests against the quaternions reviewed live.
        /// </summary>
        public static Quaternion NpcRotation(float yawDegrees)
        {
            return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(yawDegrees * Math.PI / 180.0));
        }

        /// <summary>
        /// Where one decor layer goes (D6 - pure, no engine object): the theme geometry centre with the
        /// origin raised by <paramref name="dz"/> metres. X, Y, the cell and the instance are the centre's,
        /// untouched - PositionZ is written directly rather than through Pos/SetPosition precisely so the
        /// landblock and land cell are not recomputed.
        /// </summary>
        public static Position DecorPosition(Position centre, float dz, bool inverted)
        {
            return DecorPosition(centre, dz, inverted, 0f);
        }

        /// <summary>
        /// <see cref="DecorPosition(Position, float, bool)"/> with a yaw about +Z (WP-15). The three-argument
        /// overload is kept so WP-14's callers and tests read unchanged; it is exactly this with yaw 0.
        /// </summary>
        public static Position DecorPosition(Position centre, float dz, bool inverted, float yawDegrees)
        {
            return DecorPosition(centre, 0f, 0f, dz, inverted, yawDegrees, 0f);
        }

        /// <summary>
        /// The full WP-21 form: the theme geometry centre offset by (<paramref name="dx"/> east,
        /// <paramref name="dy"/> north, <paramref name="dz"/> up) - exactly the NpcPosition offset recipe,
        /// via a raw <see cref="Position.SetPosition"/> write rather than the raw PositionZ write the
        /// dz-only overloads use, because dx/dy can cross a land cell (or landblock) boundary and need
        /// SetPosition to re-address the position to the cell it actually landed in. No terrain snap - see
        /// SpawnDecor's remarks; the caller decides that.
        ///
        /// The four-argument overloads are kept so WP-14/WP-15/WP-19 callers and tests read unchanged; they
        /// are exactly this with dx=dy=0, pitch=0.
        /// </summary>
        public static Position DecorPosition(Position centre, float dx, float dy, float dz, bool inverted,
            float yawDegrees, float pitchDegrees)
        {
            if (centre == null)
                return null;

            var position = new Position(centre.LandblockId.Raw,
                centre.PositionX, centre.PositionY, centre.PositionZ,
                centre.RotationX, centre.RotationY, centre.RotationZ, centre.RotationW,
                centre.Instance);

            position.SetPosition(new Vector3(centre.PositionX + dx, centre.PositionY + dy, centre.PositionZ + dz));

            position.Rotation = DecorRotation(inverted, yawDegrees, pitchDegrees);

            return position;
        }

        /// <summary>
        /// The decor rotation (D6 - pure): identity upright, or a 180 degree flip about X when inverted,
        /// which is what puts the disc BELOW the origin instead of above it (see DecorDef's remarks).
        /// System.Numerics.Quaternion's constructor takes (x, y, z, w), so the flip is (1, 0, 0, 0).
        /// </summary>
        public static Quaternion DecorRotation(bool inverted)
        {
            return DecorRotation(inverted, 0f);
        }

        /// <summary>
        /// The decor rotation with a yaw (WP-15, D6 - pure): turn <paramref name="yawDegrees"/> about +Z
        /// FIRST, then - only when <paramref name="inverted"/> - the 180 about the WORLD X axis.
        ///
        /// System.Numerics composes right-to-left (Quaternion.Concatenate(a, b) is documented as "a followed
        /// by b" and is implemented as b * a), so "yaw then flip" is Multiply(flip, yaw). That order is
        /// pinned by WorldEventSpawnerDecorTests.DecorRotation_Yaw90Inverted_IsFlipTimesYaw rather than by
        /// this paragraph.
        ///
        /// Yaw 0 reproduces WP-14 exactly: identity upright, (x=1, y=0, z=0, w=0) inverted.
        /// </summary>
        public static Quaternion DecorRotation(bool inverted, float yawDegrees)
        {
            return DecorRotation(inverted, yawDegrees, 0f);
        }

        /// <summary>
        /// The full WP-21 decor rotation (D6 - pure): turn <paramref name="pitchDegrees"/> about the
        /// object's LOCAL X axis FIRST (this is what stands a flat XY disc UPRIGHT), then
        /// <paramref name="yawDegrees"/> about world +Z, then - only when <paramref name="inverted"/> -
        /// the 180 about the WORLD X axis, exactly as before.
        ///
        /// System.Numerics composes right-to-left the same way the yaw/flip composition does (see the
        /// two-argument overload's remarks), so "pitch then yaw" is Multiply(yaw, pitch), and the full
        /// composition with an inverted flip is Multiply(flip, Multiply(yaw, pitch)). Pitch 0 reproduces the
        /// two-argument overload exactly - both are pinned by WorldEventSpawnerDecorTests.
        /// </summary>
        public static Quaternion DecorRotation(bool inverted, float yawDegrees, float pitchDegrees)
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(yawDegrees * Math.PI / 180.0));
            var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(pitchDegrees * Math.PI / 180.0));

            var rot = Quaternion.Multiply(yaw, pitch);

            if (!inverted)
                return rot;

            return Quaternion.Multiply(new Quaternion(1f, 0f, 0f, 0f), rot);
        }

        /// <summary>
        /// WP-25 decor color styles (D6 - pure): substitutes <paramref name="style"/>'s wcids into
        /// <paramref name="decor"/> by DISTINCT-wcid order of first appearance - the first distinct wcid seen
        /// in <paramref name="decor"/> is replaced everywhere by style[0], the second by style[1], and so on.
        /// Every other field (dx/dy/dz/scale/speed/inverted/yaw/pitch/snap) is copied verbatim onto NEW
        /// <see cref="DecorDef"/> instances - the input list's own entries are never mutated, since theme
        /// defs are shared store state and mutating them would permanently recolor the theme for every later
        /// run.
        ///
        /// Belt-and-braces only: WorldEventAxisStore.ValidateDecorStyles already guarantees style.Count
        /// matches the theme's distinct-wcid count for anything that reaches here, but if style is null/empty
        /// or its count does not match, the input list is returned unchanged rather than throwing.
        /// </summary>
        public static IReadOnlyList<DecorDef> ApplyDecorStyle(IReadOnlyList<DecorDef> decor, IReadOnlyList<uint> style)
        {
            if (decor == null)
                return decor;

            if (style == null || style.Count == 0)
                return decor;

            var wcidOrder = new List<uint>();

            foreach (var entry in decor)
            {
                if (entry == null)
                    continue;

                if (!wcidOrder.Contains(entry.Wcid))
                    wcidOrder.Add(entry.Wcid);
            }

            if (style.Count != wcidOrder.Count)
                return decor;

            var map = new Dictionary<uint, uint>();

            for (var i = 0; i < wcidOrder.Count; i++)
                map[wcidOrder[i]] = style[i];

            var result = new List<DecorDef>(decor.Count);

            foreach (var entry in decor)
            {
                if (entry == null)
                {
                    result.Add(null);
                    continue;
                }

                result.Add(new DecorDef
                {
                    Wcid = map.TryGetValue(entry.Wcid, out var styled) ? styled : entry.Wcid,
                    Dx = entry.Dx,
                    Dy = entry.Dy,
                    Dz = entry.Dz,
                    Scale = entry.Scale,
                    Speed = entry.Speed,
                    Inverted = entry.Inverted,
                    Yaw = entry.Yaw,
                    Pitch = entry.Pitch,
                    Snap = entry.Snap
                });
            }

            return result;
        }

        /// <summary>
        /// WP-25 decor color styles (D6 - pure): a uniform pick among <paramref name="styles"/>. Null or
        /// empty returns null (no style - the caller spawns Decor exactly as authored). A null
        /// <paramref name="rng"/> falls back to the ThreadSafeRandom-seeded production draw, the same
        /// convention as WorldEventGeometry.NewRandom/WorldEventRosterSelector.
        /// </summary>
        public static IReadOnlyList<uint> PickDecorStyle(IReadOnlyList<List<uint>> styles, Random rng)
        {
            if (styles == null || styles.Count == 0)
                return null;

            if (rng == null)
                rng = new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

            return styles[rng.Next(0, styles.Count)];
        }

        // ---- pure kind rules (D6) -------------------------------------------------------------------

        /// <summary>
        /// WP-17 sky-drop (D6 - pure): whether this spawn is dropped in from the sky at all. Wave kind AND a
        /// positive, finite spawnDz, and nothing else - a source (rift), the champion, decor and npcs are
        /// unchanged whatever a theme declares, and a theme that declares nothing (every shipped theme but
        /// sky_rift) takes the pre-WP-17 path byte for byte.
        /// </summary>
        public static bool AppliesSkyDrop(WorldEventSpawnKind kind, float spawnDz)
        {
            return kind == WorldEventSpawnKind.Wave && float.IsFinite(spawnDz) && spawnDz > 0f;
        }

        /// <summary>
        /// WP-17 sky-drop (D6 - pure): the Z a wave creature is placed at, given the Z the terrain snap just
        /// wrote and the theme's spawnDz. A zero, negative or non-finite dz returns the ground Z untouched,
        /// so the caller can apply this unconditionally.
        /// </summary>
        public static float SkyDropZ(float groundZ, float spawnDz)
        {
            if (!float.IsFinite(spawnDz) || spawnDz <= 0f)
                return groundZ;

            return groundZ + spawnDz;
        }

        /// <summary>
        /// WP-21 (D6 - pure): the post-terrain-snap Z for a fixed-offset objective spawn - the WP-15 npc dz
        /// rule (PlaceNpcs re-applies def.Dz on top of the snapped ground, WorldEventSpawner.cs's PlaceNpcs)
        /// applied to the Source path via TryPlace's postSnapDz. Unlike <see cref="SkyDropZ"/>, dz here is
        /// not gated to positive/finite - ObjectiveDef.Dz is validated finite at load (WorldEventAxisStore.
        /// ValidateObjectives) and a negative offset (into the ground, e.g. a half-buried pillar base) is a
        /// legitimate placement, so any finite value is applied as given.
        /// </summary>
        public static float ObjectiveSnapZ(float groundZ, float dz)
        {
            return groundZ + dz;
        }

        /// <summary>
        /// Whether a failed placement retries by RESAMPLING the theme geometry rather than jittering the
        /// point that just failed (WP-18 item 2, D6 - pure). Waves on a "disc" theme only.
        ///
        /// A disc anchor is an arbitrary draw from a 25 m circle, so a rejected one is evidence about that
        /// SPOT - nudging it 2 m keeps it inside the same building or slope. Every other geometry places on
        /// points the theme chose on purpose (a ring around the rift, the edges of the arena, a single
        /// centre), so a retry there should stay near the intended spot, which is what a jitter does.
        ///
        /// Non-wave kinds are excluded on purpose: a disc theme's objective spawns and champion use the
        /// STAGE-time anchors, and resampling those would move a rift away from the ring the run was
        /// composed around. Decor and npcs never reach this path at all - they have no retry.
        /// </summary>
        public static bool ResamplesOnRetry(WorldEventSpawnKind kind, SourceGeometry geometry)
        {
            return kind == WorldEventSpawnKind.Wave && geometry == SourceGeometry.Disc;
        }

        /// <summary>
        /// How many placement attempts one creature of this kind gets (WP-18 item 2, D6 - pure).
        /// <see cref="PlacementAttemptsDisc"/> for the resampling case, <see cref="PlacementAttempts"/>
        /// otherwise - more attempts are only worth paying for when each one explores somewhere new.
        /// </summary>
        public static int PlacementAttemptsFor(WorldEventSpawnKind kind, SourceGeometry geometry)
        {
            return ResamplesOnRetry(kind, geometry) ? PlacementAttemptsDisc : PlacementAttempts;
        }

        /// <summary>
        /// The InitLevel that makes a creature's MaxHealth come out at round(baseMax * mult)
        /// (TECH-DESIGN 2.15, D6 - pure).
        ///
        /// Why StartingValue is the right hook, and the only one: CreatureVital.GetMaxValue computes
        /// <c>total = StartingValue + Ranks + attr</c> (CreatureVital.cs:147) where <c>attr</c> is the
        /// portal.dat attribute formula for the vital and does not read StartingValue at all
        /// (AttributeFormula.GetFormula). For a non-Player it then applies only the enchantment multiplier
        /// (1.0 on a creature that has just been constructed) and the enchantment additives (0), so MaxValue
        /// moves one-for-one with StartingValue. The same idiom is already load-bearing in this fork at
        /// CombatPet.cs:62-63, where Empowered Summons scales a pet's health exactly this way.
        ///
        /// A multiplier at or below 1.0 returns the value untouched, so the caller can apply it blind.
        /// </summary>
        public static uint ScaledStartingValue(uint startingValue, uint baseMax, double mult)
        {
            if (!(mult > 1.0) || double.IsNaN(mult) || double.IsInfinity(mult) || baseMax == 0)
                return startingValue;

            var scaled = Math.Round(baseMax * mult, MidpointRounding.AwayFromZero);

            if (scaled > uint.MaxValue)
                scaled = uint.MaxValue;

            var delta = (uint)scaled - baseMax;

            // Cannot wrap in practice (delta is bounded by baseMax * (mult - 1) and both are small), but a
            // wrap here would hand the run a creature with 4 billion health, so it is checked rather than
            // reasoned about.
            if (startingValue > uint.MaxValue - delta)
                return uint.MaxValue;

            return startingValue + delta;
        }

        /// <summary>
        /// Raises one creature's maximum health by <paramref name="mult"/> and refills it, between
        /// construction and EnterWorld. A multiplier at or below 1.0 is a no-op.
        ///
        /// Nothing else is touched: damage, defences and level are left exactly as the weenie authored
        /// them, so a scaled wave is the same fight for longer rather than a different fight.
        /// </summary>
        private void ApplyHealthMultiplier(WorldEvent evt, Creature creature, double mult)
        {
            if (creature == null || !(mult > 1.0))
                return;

            try
            {
                var baseMax = creature.Health.MaxValue;

                var scaled = ScaledStartingValue(creature.Health.StartingValue, baseMax, mult);

                if (scaled == creature.Health.StartingValue)
                    return;

                creature.Health.StartingValue = scaled;
                creature.Health.Current = creature.Health.MaxValue;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt?.RunId} could not scale health for wcid {creature.WeenieClassId}", ex);
            }
        }

        /// <summary>
        /// WP-24 (owner ruling 2026-08-16): a synthetic elite/champion is a role-0 member promoted because
        /// the band it was drawn against had no real role-1/role-2 member. Two things mark it: the name gets
        /// "Champion "/"Elite " prefixed (written via the Name property setter, which is
        /// <c>SetProperty(PropertyString.Name, ...)</c> - see WorldObject_Properties.cs - so it is on the
        /// object BEFORE EnterWorld and the create packet carries it), and its health gets a further
        /// multiplier from the theme's syntheticChampionHealthMult/syntheticEliteHealthMult, applied ON TOP
        /// of the wave's healthMult by calling <see cref="ApplyHealthMultiplier"/> a second time - that
        /// method reads the creature's CURRENT MaxValue, so the two multipliers compose rather than one
        /// overwriting the other.
        ///
        /// Absent theme (should not happen once a run has composed, but guarded rather than trusted) uses
        /// the SourceThemeDef defaults, exactly as a missing JSON key would.
        /// </summary>
        private void ApplySyntheticPromotion(WorldEvent evt, Creature creature, WorldEventWaveEntryKind kind)
        {
            if (creature == null)
                return;

            var isChampion = kind == WorldEventWaveEntryKind.Champion;
            var prefix = isChampion ? "Champion " : "Elite ";

            var theme = evt?.Composition?.Source;

            var mult = isChampion
                ? theme?.EffectiveSyntheticChampionHealthMult ?? SourceThemeDef.DefaultSyntheticChampionHealthMult
                : theme?.EffectiveSyntheticEliteHealthMult ?? SourceThemeDef.DefaultSyntheticEliteHealthMult;

            try
            {
                var name = creature.Name;

                if (!string.IsNullOrEmpty(name) && !name.StartsWith(prefix, StringComparison.Ordinal))
                    creature.Name = prefix + name;

                ApplyHealthMultiplier(evt, creature, mult);

                log.Info($"[WORLDEVENT] run={evt?.RunId} synthetic {(isChampion ? "champion" : "elite")} " +
                         $"wcid={creature.WeenieClassId} name={creature.Name} health=x{mult:F2}");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt?.RunId} could not apply synthetic promotion to wcid {creature.WeenieClassId}", ex);
            }
        }

        /// <summary>
        /// Raises the ALREADY STANDING objective boss to <paramref name="newMult"/> (BOSS-STANDARD.md
        /// section 3). The named-boss health curve is re-evaluated at every audience re-sample, so a crowd
        /// that keeps growing after the boss has landed has to keep moving its health - otherwise the
        /// multiplier the boss happened to spawn at is the whole fight.
        ///
        /// Three properties this must have, and they are the whole reason it is not just another
        /// ApplyHealthMultiplier call:
        ///
        ///   * it RATCHETS. The new StartingValue is recomputed from the AUTHORED pair captured at Adopt,
        ///     so re-applying the same multiplier is a no-op, and a multiplier that came back DOWN never
        ///     shrinks the bar. Nothing here ever lowers StartingValue.
        ///   * the increase goes to Current as well as to Max, so a late arrival adds health rather than
        ///     handing the crowd a boss that is suddenly missing half its bar. The delta is measured from
        ///     two MaxValue reads rather than computed from the multiplier, because MaxValue also carries
        ///     the enchantment multiplier and additives, which StartingValue knows nothing about.
        ///   * it ONLY EVER runs on the boss's own landblock queue. There is deliberately no inline
        ///     fallback for a null CurrentLandblock: that field is null for the whole of an ADJACENCY
        ///     TRANSFER, not just after a destroy (Landblock.RemoveWorldObjectInternal nulls it, and
        ///     LandblockManager.RelocateObjectForPhysics removes from the old block before adding to the
        ///     new one), so applying inline would write a live boss's vitals off-queue exactly when it
        ///     crosses a landblock boundary. A skipped raise costs nothing - the multiplier is already
        ///     latched on the run and the next audience re-sample tries again.
        /// </summary>
        public void RatchetBossHealth(WorldEvent evt, double newMult)
        {
            Creature creature;
            uint authoredStartingValue;
            uint authoredBaseMax;

            lock (sync)
            {
                creature = boss;
                authoredStartingValue = bossAuthoredStartingValue;
                authoredBaseMax = bossAuthoredBaseMax;
            }

            var runId = evt?.RunId ?? 0;

            if (creature == null)
                return;

            var deadOrDestroyed = creature.IsDestroyed || creature.IsDead;
            var landblock = creature.CurrentLandblock;

            if (!BossRatchetReady(authoredBaseMax, newMult, deadOrDestroyed, landblock != null))
            {
                // The one refusal worth a line, because it is the one that is TEMPORARY: a boss in the
                // middle of an adjacency transfer is alive and will have a landblock again shortly.
                if (!deadOrDestroyed && landblock == null)
                    log.Debug($"[WORLDEVENT] run={runId} boss health ratchet skipped: no landblock (retried on next sample)");

                return;
            }

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (creature.IsDestroyed || creature.IsDead)
                        return;

                    var scaled = ScaledStartingValue(authoredStartingValue, authoredBaseMax, newMult);

                    // The ratchet: equal or lower is nothing to do, and lowering is never allowed.
                    if (scaled <= creature.Health.StartingValue)
                        return;

                    var before = creature.Health.MaxValue;

                    creature.Health.StartingValue = scaled;

                    var after = creature.Health.MaxValue;

                    if (after > before)
                        creature.UpdateVitalDelta(creature.Health, ClampVitalDelta(after - before));

                    log.Info($"[WORLDEVENT] run={runId} boss health=x{newMult:F2} max={after}");
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={runId} boss health ratchet threw", ex);
                }
            }));
        }

        /// <summary>
        /// Writes <paramref name="newRating"/> to the ALREADY STANDING boss's DamageRating (TECH-DESIGN
        /// 2.16), on the boss's own landblock action queue. Returns true when the write was queued, false
        /// when it was refused - the caller must not record the new rating as applied unless this returned
        /// true, or the controller's idea of the current rating drifts from the boss's.
        ///
        /// Same queue discipline as <see cref="RatchetBossHealth"/> and for the same reason: a null
        /// CurrentLandblock covers the whole of an adjacency transfer, not just a destroy, so an inline
        /// fallback would write a live boss off-queue exactly when it crosses a landblock boundary. A
        /// skipped write costs nothing - the next full sample window tries again from the same current
        /// rating.
        ///
        /// Unlike the health ratchet this is NOT one-directional. Damage scaling has to be able to come
        /// back DOWN: a boss that is killing the group in one hit is as broken as one that cannot hurt
        /// them, and unlike a health bar players have been chewing on, nothing is taken away from anyone by
        /// lowering it.
        ///
        /// The write is in-memory only. A world-event boss is a spawn-time creature that is destroyed with
        /// the run and never saved (see the class remarks on persistence exclusion), so this never reaches
        /// the shard.
        /// </summary>
        public bool ApplyBossDamageRating(WorldEvent evt, int newRating)
        {
            Creature creature;

            lock (sync)
                creature = boss;

            var runId = evt?.RunId ?? 0;

            if (creature == null)
                return false;

            var deadOrDestroyed = creature.IsDestroyed || creature.IsDead;
            var landblock = creature.CurrentLandblock;

            if (!BossDamageWriteReady(deadOrDestroyed, landblock != null))
            {
                if (!deadOrDestroyed && landblock == null)
                    log.Debug($"[WORLDEVENT] run={runId} boss damage rating write skipped: no landblock (retried on the next window)");

                return false;
            }

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (creature.IsDestroyed || creature.IsDead)
                        return;

                    creature.DamageRating = newRating;
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={runId} boss damage rating write threw", ex);
                }
            }));

            return true;
        }

        /// <summary>
        /// Whether a boss damage rating write may be queued at this instant (D6 - pure). Two refusals only:
        /// a dead or destroyed boss, and - the one that actually happens - a boss with no CurrentLandblock.
        /// There is no authored-value term here, unlike <see cref="BossRatchetReady"/>: rating 0 is a
        /// perfectly valid authored rating (it is 1.0x), so "authored is 0" cannot mean "not scalable".
        ///
        /// <paramref name="hasLandblock"/> false means SKIP, never "apply inline".
        /// </summary>
        public static bool BossDamageWriteReady(bool deadOrDestroyed, bool hasLandblock)
        {
            if (deadOrDestroyed)
                return false;

            return hasLandblock;
        }

        /// <summary>
        /// Whether a boss health raise may be queued at this instant (D6 - pure). Every term is a refusal:
        /// no authored base to recompute from, a multiplier that is not an increase or is not a number, a
        /// boss that is dead or destroyed, or - the case this was extracted for - a boss with no
        /// CurrentLandblock.
        ///
        /// <paramref name="hasLandblock"/> false means SKIP, never "apply inline". See
        /// <see cref="RatchetBossHealth"/>'s remarks for why an off-queue vital write is not an acceptable
        /// fallback here.
        /// </summary>
        public static bool BossRatchetReady(uint authoredBaseMax, double newMult, bool deadOrDestroyed,
            bool hasLandblock)
        {
            if (authoredBaseMax == 0)
                return false;

            if (!(newMult > 1.0) || double.IsNaN(newMult) || double.IsInfinity(newMult))
                return false;

            if (deadOrDestroyed)
                return false;

            return hasLandblock;
        }

        /// <summary>
        /// Creature.UpdateVitalDelta takes an INT (Creature_Vitals.cs), and the uint overload simply casts,
        /// so a delta above int.MaxValue would arrive as a negative number and REMOVE health from the boss
        /// instead of adding it (D6 - pure). Clamped rather than reasoned about: the bound is unreachable
        /// with the shipped cap of 8.0 on any sane authored health, but "unreachable" is a property of the
        /// content, and content is exactly what bosses.json lets somebody change.
        /// </summary>
        public static uint ClampVitalDelta(uint delta)
        {
            return delta > int.MaxValue ? (uint)int.MaxValue : delta;
        }

        /// <summary>
        /// bosses.json's OPTIONAL baseHealth override (BOSS-STANDARD.md section 3), applied between
        /// construction and <see cref="ApplyHealthMultiplier"/> so it is a BASE and the power multiplier
        /// still means what it says.
        ///
        /// The weenie stays the authority by default: 0 (the shipped state), an absent key, and every
        /// non-Named boss all leave the creature untouched. Health.Current is re-seeded from the new
        /// maximum exactly as ApplyHealthMultiplier does, and the caller captures the AUTHORED pair after
        /// this runs, so the ratchet recomputes against the overridden base rather than the weenie's.
        ///
        /// The number applied is <see cref="WorldEvent.BossRebaseHealth"/>, not bosses.json's baseHealth
        /// directly (C16): the event picks between them at the spawn decision - baseHealth on the legacy
        /// path, throughputFloorHealth on the throughput path - and the 0 = "no override" semantics are
        /// identical either way.
        /// </summary>
        private void ApplyBaseHealthOverride(WorldEvent evt, Creature creature)
        {
            var boss = evt?.Composition?.Boss;

            if (creature == null || boss == null || boss.Kind != BossKind.Named)
                return;

            var rebase = evt.BossRebaseHealth;

            if (rebase == 0)
                return;

            try
            {
                var rebased = RebasedStartingValue(creature.Health.StartingValue, creature.Health.MaxValue,
                    rebase);

                if (rebased == creature.Health.StartingValue)
                    return;

                creature.Health.StartingValue = rebased;
                creature.Health.Current = creature.Health.MaxValue;

                log.Info($"[WORLDEVENT] run={evt.RunId} boss '{boss.Id}' base health rebased to {rebase} " +
                         $"(max={creature.Health.MaxValue})");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt?.RunId} could not apply the baseHealth override for wcid {creature.WeenieClassId}", ex);
            }
        }

        /// <summary>
        /// The StartingValue that makes a constructed creature's PRE-multiplier Health.MaxValue equal
        /// <paramref name="baseHealth"/> (D6 - pure).
        ///
        /// CreatureVital.GetMaxValue is StartingValue + Ranks + attr and only StartingValue is ours to set,
        /// so the part that is not ours - (maxValue - startingValue) as the creature was constructed - is
        /// subtracted out and the remainder becomes the new StartingValue. A baseHealth at or below that
        /// fixed part floors at 0, which is as close to the requested total as the vital can get. 0 means
        /// "no override" and returns the value untouched.
        /// </summary>
        public static uint RebasedStartingValue(uint startingValue, uint maxValue, uint baseHealth)
        {
            if (baseHealth == 0)
                return startingValue;

            var fixedPart = maxValue > startingValue ? maxValue - startingValue : 0;

            return baseHealth > fixedPart ? baseHealth - fixedPart : 0;
        }

        /// <summary>
        /// Whether a spawn of this kind must be a Creature. Only decor may be something else - every other
        /// kind is fought, and a non-Creature among them could never die, so it would hold a kill objective
        /// open for the rest of the run. An InertObjective is not fought, but it IS the same objective
        /// weenie a Source would be, so it keeps the Creature requirement rather than special-casing it.
        ///
        /// Add is the other exception (2026-09-05), for a different reason: nothing places an add, so it
        /// never reaches the creature path this guards at all, and what a generator produced is not ours to
        /// require anything of - it can be an item just as easily as a monster.
        /// </summary>
        public static bool RequiresCreature(WorldEventSpawnKind kind)
        {
            return kind != WorldEventSpawnKind.Decor && kind != WorldEventSpawnKind.Add;
        }

        /// <summary>
        /// Whether this spawn gets the boss-confinement stamp: kind Boss, the live lever on, and a usable
        /// radius. A non-finite or non-positive radius reads the same as the lever being off, so a bad
        /// config row cannot stamp a zero tether - which would otherwise confine the boss to a point and
        /// leave it with nothing it may target. Pure; the radius comes from
        /// world_events_boss_tether_radius and the flag from world_events_boss_tether_enabled.
        /// </summary>
        public static bool AppliesBossTether(WorldEventSpawnKind kind, bool tetherEnabled, double tetherRadius)
        {
            return kind == WorldEventSpawnKind.Boss && tetherEnabled
                && double.IsFinite(tetherRadius) && tetherRadius > 0.0;
        }

        /// <summary>
        /// The HomeRadius stamped alongside a boss tether: twice the tether. The ordinary leash
        /// (Creature.CheckMissHome -> MoveToHome) therefore stays well outside the tether, so it only ever
        /// catches a boss the tether retarget could not pull back - a boss with no in-tether player left.
        /// </summary>
        public static double BossHomeRadiusFor(double tetherRadius)
        {
            return tetherRadius * 2.0;
        }

        /// <summary>
        /// Whether the boss stamp may write TargetingTactic. Only when the weenie authored none: a boss whose
        /// content deliberately sets a tactic keeps it, and the tether confines that tactic's candidates
        /// rather than replacing it. <paramref name="authoredTactic"/> is the raw PropertyInt.TargetingTactic
        /// read, null when the weenie carries no row.
        /// </summary>
        public static bool StampsBossTargetingTactic(int? authoredTactic)
        {
            return authoredTactic == null || authoredTactic.Value == 0;
        }

        /// <summary>
        /// Whether a spawn of this kind is charged against the theme's MaxAlive, i.e. counted by LiveCount.
        /// Wave trash only: objective spawns and the champion are not trash pressure, decor is scenery, and
        /// an npc is an attendant standing at the edge of the scene.
        /// </summary>
        public static bool CountsAsWavePressure(WorldEventSpawnKind kind)
        {
            return kind == WorldEventSpawnKind.Wave;
        }

        /// <summary>
        /// Whether a spawn of this kind is counted by LiveTotal ("everything this run still has standing" for
        /// the status line). Decor is not, and neither is an npc: neither is a kill, an objective or
        /// pressure, and both are reported separately as DecorCount / NpcCount.
        ///
        /// An InertObjective is not either (2026-08-19): it is the same creature a Source would be, but the
        /// run's goal is not DestroySource, so it is scenery for this run and must not land in sourceGuids -
        /// a guid there would make DestroySourceObjective (and the AllSourcesDead wave cut-off) wait forever
        /// on something nothing can kill.
        ///
        /// This is also the switch <see cref="Adopt"/> reads to decide that no P_WorldEvent back-reference
        /// is written, which is what keeps an npc death out of Creature.Die's world-event hook.
        /// </summary>
        public static bool CountsAsLive(WorldEventSpawnKind kind)
        {
            return kind != WorldEventSpawnKind.Decor && kind != WorldEventSpawnKind.Npc
                && kind != WorldEventSpawnKind.InertObjective && kind != WorldEventSpawnKind.Add;
        }

        /// <summary>
        /// The pure split rule behind <see cref="DestroyAll"/> (D6): the creature pass destroys everything
        /// that is not a Weave Cache, the final sweep destroys everything. Neither decor nor an npc is ever
        /// marked as a cache (<see cref="PlaceDecor"/> and <see cref="PlaceNpcs"/> set no PropertyBool at
        /// all), so <c>isCache</c> is false for both and BOTH passes take them - which is what makes the
        /// Finish sweep clear the sky AND take the attendants with it. No npc-specific rule is needed here.
        /// </summary>
        public static bool DestroyedByPass(bool isCache, bool includeCaches)
        {
            return includeCaches || !isCache;
        }

        /// <summary>
        /// Takes ownership of an object that is already in the world. Returns false when cleanup has
        /// already started, in which case the caller destroys it instead.
        ///
        /// The whole adoption - back-reference, held list and the spawner's own sets - happens under
        /// <see cref="sync"/> so it is atomic against DestroyAll's snapshot: either the creature is in the
        /// held list before the snapshot is taken, or the flag is already set and it is discarded. The lock
        /// ORDER is always sync then HeldObjects, and nothing takes them the other way round.
        /// </summary>
        private bool Adopt(WorldEvent evt, WorldObject wo, WorldEventSpawnKind kind)
        {
            return Adopt(evt, wo, kind, NoWaveIndex, overflowChampion: false);
        }

        private bool Adopt(WorldEvent evt, WorldObject wo, WorldEventSpawnKind kind, int waveIndex,
            bool overflowChampion, uint authoredStartingValue = 0, uint authoredBaseMax = 0)
        {
            lock (sync)
            {
                if (creaturePassRan || finalPassRan)
                    return false;

                // AddHeld is the single held-list entry point and has the final say: it refuses once the
                // run has closed the list, and a refusal here means the caller destroys the object.
                if (!evt.AddHeld(wo))
                    return false;

                spawned.Add(wo);

                if (!CountsAsLive(kind))
                {
                    // Decor, npcs and inert objectives: their own bookkeeping, and none of the creature
                    // bookkeeping. An InertObjective takes the decor side deliberately (2026-08-19) - it is
                    // scenery for this run, so it must reach decorGuids (which is what keeps it out of
                    // WorldEventThroughput.Counts and puts it in the DecorCount line) and must NOT reach
                    // sourceGuids. Both DestroyAll passes iterate the event's FULL held list, so it is still
                    // destroyed at Finish with everything else. No
                    // P_WorldEvent either. For decor that back-reference is simply unreachable (it is not a
                    // Creature); for an npc it is withheld DELIBERATELY - Creature_Death.cs requires both
                    // the back-reference and the WorldEventId stamp before it calls OnEventCreatureDied, so
                    // withholding it is what keeps an npc death out of the objective, the alive count and
                    // the MVP ledger. Returning here is also what stops the Creature branch below from
                    // setting it.
                    if (kind == WorldEventSpawnKind.Npc)
                    {
                        liveNpcs.Add(wo);
                        npcGuids.Add(wo.Guid.Full);
                    }
                    else if (kind == WorldEventSpawnKind.Add)
                    {
                        // 2026-09-05. Its own set rather than decorGuids: an add is not scenery this run
                        // placed, and IsDecorGuid is what WorldEventThroughput.Counts reads - conflating
                        // them would make the two indistinguishable to any later reader. See
                        // WorldEventSpawnKind.Add for why it counts for nothing either way.
                        liveAdds.Add(wo);
                        addGuids.Add(wo.Guid.Full);
                        addsAdopted++;
                    }
                    else
                    {
                        liveDecor.Add(wo);
                        decorGuids.Add(wo.Guid.Full);
                    }

                    return true;
                }

                if (!(wo is Creature creature))
                {
                    // Unreachable: RequiresCreature(kind) is enforced when the candidates are constructed.
                    // Held anyway rather than dropped, so cleanup still takes it out of the world.
                    log.Error($"[WORLDEVENT] run={evt.RunId} adopted a non-Creature as {kind}; it is held for cleanup but counts as nothing");
                    return true;
                }

                // 2026-08-16 defense in depth: two enqueued boss placement attempts should never both be
                // able to resolve to an adopt - BossPlacementPending is what should normally stop
                // WorldEvent.TickChampion from issuing a second attempt while the first is in flight, but
                // this is the last line that can actually prevent a duplicate boss from entering the world.
                // Already held (AddHeld/spawned above), so the caller still destroys it on a false return.
                if (kind == WorldEventSpawnKind.Boss && !AcceptsBossAdopt(bossGuid))
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} second boss adopt refused: bossGuid=0x{bossGuid:X8} already set, discarding guid=0x{wo.Guid.Full:X8}");
                    return false;
                }

                creature.P_WorldEvent = evt;

                if (CountsAsWavePressure(kind))
                {
                    liveWave.Add(creature);

                    // TECH-DESIGN 2.15. Both halves of the bookkeeping hang off the adopt rather than off
                    // the pick, so a creature whose placement failed is never counted as part of a wave the
                    // pace controller then waits forever to see cleared.
                    NoteWaveSpawn(waveIndex, creature.Guid.Full, overflowChampion);
                }
                else
                    liveObjective.Add(creature);

                if (kind == WorldEventSpawnKind.Source)
                {
                    sourceGuids.Add(creature.Guid.Full);
                    liveSourceGuids.Add(creature.Guid.Full);
                }
                else if (kind == WorldEventSpawnKind.Boss)
                {
                    bossGuid = creature.Guid.Full;

                    boss = creature;
                    bossAuthoredStartingValue = authoredStartingValue;
                    bossAuthoredBaseMax = authoredBaseMax;

                    // C16 observability. Post-multiplier by construction: Spawn applies the rebase and the
                    // multiplier before it enqueues, so this is the health the boss actually enters the
                    // world with.
                    bossAdoptedAt = clock();

                    try
                    {
                        bossMaxHealth = creature.Health.MaxValue;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={evt.RunId} could not read the boss's spawned health", ex);
                    }

                    // TECH-DESIGN 2.16. Read here, once, before anything can have written it, so the damage
                    // controller's first correction is relative to the authored value rather than to
                    // whatever the previous run happened to leave in a shared field.
                    try
                    {
                        bossAuthoredDamageRating = creature.DamageRating ?? 0;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={evt.RunId} could not read the boss's authored damage rating", ex);
                        bossAuthoredDamageRating = 0;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Takes ownership of an object a generator belonging to this run put into the world (2026-09-05).
        /// Called from WorldObject.OnGeneration by way of WorldEventManager.OnGeneratedIntoWorld, i.e. from
        /// inside EnterWorld on a landblock action queue - the object is already placed by the time this
        /// runs.
        ///
        /// The refusal path matters as much as the adopt: <see cref="Adopt"/> returns false once either
        /// cleanup pass has run, and an add left standing at that point is exactly the leak this whole
        /// change exists to close, so it is destroyed instead. The destroy is ENQUEUED on the object's own
        /// landblock rather than run here for the reason WorldEventScene.Clear documents at length: a
        /// direct Destroy() recycles the guid (GuidManager.RecycleDynamicGuid) and any still-pending action
        /// against that object would then either resurrect it or collide with whatever guid is allocated
        /// next. Same shape as DestroyAll's loop.
        ///
        /// Lock order is unchanged: Adopt takes sync then HeldObjects and returns before anything here
        /// touches landblock code, so nothing takes them the other way round and no landblock call is made
        /// while holding sync.
        /// </summary>
        public void AdoptGeneratedAdd(WorldEvent evt, WorldObject wo)
        {
            if (evt == null || wo == null)
                return;

            bool adopted;

            try
            {
                adopted = Adopt(evt, wo, WorldEventSpawnKind.Add);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} adopting a generated add threw for guid=0x{wo.Guid.Full:X8}", ex);
                return;
            }

            if (adopted)
                return;

            lock (sync)
                addsStray++;

            var landblock = wo.CurrentLandblock;

            try
            {
                if (landblock != null)
                    landblock.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        if (!wo.IsDestroyed)
                            wo.Destroy();
                    }));
                else if (!wo.IsDestroyed)
                    wo.Destroy();
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} could not destroy the late generated add guid=0x{wo.Guid.Full:X8}", ex);
            }
        }

        private Position NextJitter(Position basePosition, float metres = RetryJitterMetres)
        {
            lock (sync)
                return WorldEventGeometry.Jitter(basePosition, metres, rng);
        }

        /// <summary>
        /// One fresh uniform-by-area sample from the theme's disc (WP-18 item 2), drawn from this spawner's
        /// own seeded rng under <see cref="sync"/> exactly as <see cref="NextJitter"/> is. Null when the
        /// theme cannot produce one, which the caller treats as "fall back to a jitter".
        /// </summary>
        private Position NextDiscSample(SourceThemeDef theme, Position centre)
        {
            if (theme == null || centre == null)
                return null;

            lock (sync)
            {
                var sample = WorldEventGeometry.Disc(centre, theme.GeometryRadius, 1, rng);

                return sample.Count > 0 ? sample[0] : null;
            }
        }

        /// <summary>Caller must hold <see cref="sync"/>.</summary>
        private void Prune()
        {
            liveWave.RemoveAll(c => c == null || c.IsDestroyed || c.IsDead);
            liveObjective.RemoveAll(c => c == null || c.IsDestroyed || c.IsDead);
        }

        /// <summary>
        /// Caller must hold <see cref="sync"/>. No IsDead term: decor is not a Creature, so destroyed is the
        /// only way it leaves the world.
        /// </summary>
        private void PruneDecor()
        {
            liveDecor.RemoveAll(wo => wo == null || wo.IsDestroyed);
        }

        /// <summary>
        /// Caller must hold <see cref="sync"/>. Unlike <see cref="PruneDecor"/> this DOES carry an IsDead
        /// term: an npc is a Creature, so it can in principle die (the shipped attendants cannot, being
        /// non-attackable, but a later scene's npc might), and a corpse must stop being reported as a
        /// standing attendant. It never arrives here through <see cref="OnDied"/>, which an npc cannot
        /// reach - it has no P_WorldEvent - so this is the only thing that retires one.
        /// </summary>
        private void PruneNpcs()
        {
            liveNpcs.RemoveAll(wo => wo == null || wo.IsDestroyed || (wo is Creature creature && creature.IsDead));
        }

        /// <summary>
        /// Caller must hold <see cref="sync"/>. Same terms as <see cref="PruneNpcs"/> - an add is usually a
        /// Creature and can die like one, and a corpse must stop being reported as standing - plus the
        /// player-taken term: a generated ITEM a player picked up has left the world by a route that is
        /// neither destruction nor death, and it is no longer an add=&lt;n&gt; the run is holding.
        /// </summary>
        private void PruneAdds()
        {
            liveAdds.RemoveAll(wo => wo == null || wo.IsDestroyed
                || (wo is Creature creature && creature.IsDead) || IsPlayerTaken(wo));
        }

        /// <summary>
        /// True when an object is in a PLAYER's possession rather than standing in the world: carried or
        /// wielded.
        ///
        /// Only consulted for adds, and it is the one thing an add needs that no other kind does. A
        /// generator profile can spawn an ITEM, and a player may legitimately pick that item up while the
        /// run is still going; destroying it at Finish would take it out of their pack. Everything else the
        /// run holds is placed by this class, is a Creature or fixed scenery, and can never be picked up.
        ///
        /// A VENDOR SALE IS NOT POSSESSION, and that is why the container term reads OwnerId rather than
        /// ContainerId. Container.TryAddToInventory writes OwnerId, ContainerId and the live Container
        /// reference together, and TryRemoveFromInventory clears all three - but a vendor sale runs AFTER
        /// that removal and writes ContainerId back alone, as do both AddDefaultItem paths. Every other
        /// ContainerId writer in the server is likewise a vendor path. On a ContainerId test an add a
        /// player sold would be "taken" forever, and an event item would permanently enter the economy as
        /// purchasable stock. The explicit Vendor test is belt and braces for a future path that sets both.
        ///
        /// The decision itself lives in <see cref="WorldEventGeneratedAdds.IsPossessed"/> so it can be
        /// driven from a unit test without a live WorldObject (D6); this is the adapter that reads the
        /// three terms off one.
        /// </summary>
        public static bool IsPlayerTaken(WorldObject wo)
        {
            if (wo == null)
                return false;

            return WorldEventGeneratedAdds.IsPossessed(wo.OwnerId != null, wo.WielderId != null,
                wo.Container is Vendor);
        }

        /// <summary>
        /// TECH-DESIGN 5.2, fixed format - monitoring queries are written against it:
        /// [WORLDEVENT] run=&lt;id&gt; wave=&lt;n&gt; band=&lt;lo&gt;-&lt;hi&gt; spawned=&lt;k&gt; alive=&lt;a&gt;
        ///
        /// Emitted exactly once per attempted wave, from the placement path rather than from the caller.
        /// SpawnWave only ENQUEUES the placement, and EnterWorld can fail per creature (see the retry and
        /// skipped-count warning above), so a line logged at the call site would report the count REQUESTED
        /// and a PREDICTED alive - both of which overstate reality on a bad anchor. Here, "spawned" is what
        /// actually entered the world and "alive" is LiveCount read after those creatures were adopted.
        ///
        /// A no-op for objective and champion spawns, which are not waves and have no band.
        /// </summary>
        private void LogWaveIfWave(WorldEvent evt, WorldEventSpawnKind kind, int waveIndex, LevelBand band, int placed)
        {
            if (kind != WorldEventSpawnKind.Wave)
                return;

            log.Info($"[WORLDEVENT] run={evt.RunId} wave={waveIndex} band={band.Low}-{band.High} spawned={placed} alive={LiveCount}");
        }

        /// <summary>TECH-DESIGN 5.2, fixed format - monitoring queries are written against it.</summary>
        private static void LogCleanup(WorldEvent evt, int destroyed, IReadOnlyList<uint> survivors)
        {
            var text = survivors == null || survivors.Count == 0
                ? "none"
                : string.Join(",", survivors.Select(guid => $"0x{guid:X8}"));

            log.Info($"[WORLDEVENT] run={evt.RunId} cleanup destroyed={destroyed} survivors={text}");
        }
    }
}
