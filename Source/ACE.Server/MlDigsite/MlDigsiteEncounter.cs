using System;
using System.Collections.Generic;

using ACE.Entity.Models;
using ACE.Server.MlTreasure;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// One live digsite encounter: everything that is true of a single dug-up fight, and nothing about how
    /// fights are driven (that is MlDigsiteManager) or how objects are placed (MlDigsiteSpawner).
    ///
    /// LOCKING. An encounter is touched from at least three threads: the world thread (MlDigsiteManager.Tick),
    /// whichever landblock tick thread killed a creature (Creature.Die), and whatever thread completed the
    /// dig that started it. EVERY mutable field below is guarded by <see cref="sync"/>, and every accessor
    /// takes it - there are no lock-free reads of mutable state, because a torn read of the wave bookkeeping
    /// is exactly how a wave gets spawned twice.
    ///
    /// THE END PIPELINE. Every end goes TryRequestEnd -> MlDigsiteManager.Tick (world thread) consumes it ->
    /// Finish -> <see cref="MarkEnded"/> -> Deliver -> <see cref="TryClaimReward"/>. A killing blow on a
    /// landblock thread, the reap, the meter, a wave clock and /digsite bail can only REQUEST an end
    /// (<see cref="TryRequestEnd"/>, first request wins). MarkEnded is still the single idempotent exit latch
    /// - it returns true to ONE caller ever - but that caller is always the world-thread tick, so the payout
    /// and cleanup never run on a landblock thread.
    /// </summary>
    public sealed class MlDigsiteEncounter
    {
        private readonly object sync = new object();

        public MlDigsiteEncounter(uint encounterId, uint diggerGuid, string diggerName, MlDigsiteType type,
            Position anchor, DateTime startedUtc, MlTreasureZone zone = MlTreasureZone.Unknown)
        {
            EncounterId = encounterId;
            DiggerGuid = diggerGuid;
            DiggerName = diggerName ?? "someone";
            Type = type;
            Zone = zone;

            // A private COPY. The caller's Position is the digging player's live location object, which
            // moves the moment they take a step; an encounter anchored to a reference would drift with them.
            Anchor = anchor == null ? null : new Position(anchor);

            StartedUtc = startedUtc;

            BossDamage = type == MlDigsiteType.BossRush ? new WorldEventBossDamageController() : null;

            lastPresenceUtc = startedUtc;
            waveStartedUtc = startedUtc;
            lastKillUtc = startedUtc;
        }

        // ---- identity, immutable -------------------------------------------------------------------------

        public uint EncounterId { get; }

        /// <summary>The digging player. The ONLY player who may open this encounter's chest.</summary>
        public uint DiggerGuid { get; }

        public string DiggerName { get; }

        public MlDigsiteType Type { get; }

        /// <summary>
        /// Zone isolation (owner-approved design): the Marae Lassel level zone this encounter's dig belongs
        /// to - the digging map's own zone when it had one, else the zone of the site it was dug at
        /// (MlDigsiteManager.TryStart's caller resolves this). Unknown applies no filter at all, which is
        /// what keeps a dig with no zone data (a legacy map, or a catalogue with no zone column) spawning
        /// exactly as it always did. Read by MlDigsiteSpawner's roster draw for every creature this
        /// encounter places.
        /// </summary>
        public MlTreasureZone Zone { get; }

        /// <summary>Where the dig finished. Never null in practice; the manager refuses to start without one.</summary>
        public Position Anchor { get; }

        public DateTime StartedUtc { get; }

        public override string ToString() => $"digsite={EncounterId} type={Type} digger={DiggerName}";

        // ---- run state -----------------------------------------------------------------------------------

        private MlDigsiteState state = MlDigsiteState.Active;
        private string endReason;

        public MlDigsiteState State
        {
            get { lock (sync) return state; }
        }

        /// <summary>
        /// The run token every deferred callback re-checks before acting. An action queued onto a landblock
        /// before the encounter ended can still run after it did, and this is what makes that action a no-op
        /// instead of a write to a finished encounter.
        /// </summary>
        public bool RunValid
        {
            get { lock (sync) return state == MlDigsiteState.Active; }
        }

        public string EndReason
        {
            get { lock (sync) return endReason; }
        }

        /// <summary>
        /// The single exit latch. Returns true to exactly one caller, ever, and reports the state the
        /// encounter was in before the transition.
        ///
        /// The prior state comes out of the SAME critical section as the transition on purpose, and the
        /// transition shares that section with <see cref="NoteCreatureDeath"/>: once this returns true, no
        /// death - and so no Kept Siraluun drop - can be recorded any more.
        /// </summary>
        public bool MarkEnded(string reason, out MlDigsiteState prior)
        {
            lock (sync)
            {
                prior = state;

                if (state == MlDigsiteState.Ended)
                    return false;

                state = MlDigsiteState.Ended;
                endReason = reason;

                return true;
            }
        }

        // ---- held objects --------------------------------------------------------------------------------

        private readonly List<WorldObject> heldObjects = new List<WorldObject>();
        private readonly HashSet<uint> heldGuids = new HashSet<uint>();
        private bool heldClosed;

        /// <summary>
        /// THE single entry point for anything this encounter puts into the world - every creature and the
        /// reward chest alike.
        ///
        /// A FALSE return means the encounter has already closed its held list (cleanup has decided what it
        /// is destroying), so the caller placed an object nothing will ever clean up. The caller MUST destroy
        /// it rather than leave it standing with TimeToRot = -1 and no owner. That contract is invariant 6
        /// and it is the whole reason this returns a bool instead of void.
        /// </summary>
        public bool AddHeld(WorldObject wo)
        {
            if (wo == null)
                return false;

            lock (sync)
            {
                if (heldClosed)
                    return false;

                heldObjects.Add(wo);
                heldGuids.Add(wo.Guid.Full);

                return true;
            }
        }

        /// <summary>
        /// Closes the held list: nothing may be adopted after this. Called once, from the manager's cleanup,
        /// after the snapshot it is destroying has been taken.
        /// </summary>
        public void CloseHeld()
        {
            lock (sync)
                heldClosed = true;
        }

        public bool HeldClosed
        {
            get { lock (sync) return heldClosed; }
        }

        /// <summary>A copy of the held list, safe to enumerate outside the lock.</summary>
        public List<WorldObject> HeldSnapshot()
        {
            lock (sync)
                return new List<WorldObject>(heldObjects);
        }

        /// <summary>
        /// Whether this encounter ever held the guid. An EVER-held set: nothing removes from it and
        /// <see cref="CloseHeld"/> does not clear it, so a creature that has already died still answers true.
        /// </summary>
        public bool IsHeld(uint guid)
        {
            lock (sync)
                return heldGuids.Contains(guid);
        }

        // ---- creature bookkeeping ------------------------------------------------------------------------

        private readonly Dictionary<uint, Creature> liveWave = new Dictionary<uint, Creature>();
        private uint objectiveGuid;
        private bool objectiveAlive;
        private Creature objectiveCreature;

        /// <summary>
        /// Registers a placed wave creature. Wave creatures are the field: in a Waves encounter they are the
        /// wave, and in a Corruption encounter they are the surrounding mobs the meter hardens.
        /// </summary>
        public void TrackWaveCreature(Creature creature)
        {
            if (creature == null)
                return;

            lock (sync)
                liveWave[creature.Guid.Full] = creature;
        }

        /// <summary>
        /// Registers the encounter's OBJECTIVE - the Corruption priority mob or the Boss Rush boss. Killing it
        /// is what wins, so it is tracked apart from the field rather than counted with it. An endless Waves
        /// encounter has no objective at all (its checkpoint mini-bosses are <see cref="TrackCheckpoint"/>).
        /// </summary>
        public void TrackObjective(Creature creature)
        {
            if (creature == null)
                return;

            lock (sync)
            {
                objectiveGuid = creature.Guid.Full;
                objectiveAlive = true;
                objectiveCreature = creature;

                // Round 16 fix: whatever placed this objective just satisfied any outstanding Corrupted-mob
                // respawn debt (MarkCorruptedSpawnPending/ShouldRetryCorruptedSpawn). Cleared unconditionally
                // here rather than only from the Corruption retry path, so a successful FIRST spawn at open
                // clears it too and the driver never retries a spawn that already landed.
                corruptedSpawnPending = false;
            }
        }

        public bool ObjectiveAlive
        {
            get { lock (sync) return objectiveAlive; }
        }

        /// <summary>The guid of the most recently tracked objective (0 when none ever was), alive or not.</summary>
        public uint ObjectiveGuid
        {
            get { lock (sync) return objectiveGuid; }
        }

        // ---- RoZ round 19 level-spread scaling: the Boss Rush boss's measured damage and health ratchet ----

        /// <summary>
        /// The measured boss-damage controller for a Boss Rush encounter's boss (the World Event controller,
        /// reused - WorldEventBossDamageController), sized by the digsite tick to the TOUGHEST present player.
        /// Null for every other encounter shape. The controller carries its own lock; hits arrive from
        /// landblock threads through WorldEventBossDamageHook's digsite branch.
        /// </summary>
        public WorldEventBossDamageController BossDamage { get; }

        private bool bossHealthBaseSet;
        private uint bossHealthBaseStartingValue;
        private uint bossHealthBaseMax;
        private double bossHealthMult = 1.0;

        /// <summary>
        /// Records the Boss Rush boss's pre-multiplier health pair and the power multiplier it spawned with, so
        /// the reap's re-check can ratchet the multiplier upward from the same base (WorldEvent.RatchetMult).
        /// </summary>
        public void NoteBossHealthBase(uint baseStartingValue, uint baseMax, double mult)
        {
            lock (sync)
            {
                bossHealthBaseSet = true;
                bossHealthBaseStartingValue = baseStartingValue;
                bossHealthBaseMax = baseMax;
                bossHealthMult = mult;
            }
        }

        public bool TryGetBossHealthBase(out uint baseStartingValue, out uint baseMax, out double mult)
        {
            lock (sync)
            {
                baseStartingValue = bossHealthBaseStartingValue;
                baseMax = bossHealthBaseMax;
                mult = bossHealthMult;

                return bossHealthBaseSet;
            }
        }

        /// <summary>Raises (never lowers) the recorded boss multiplier. Returns true when it moved.</summary>
        public bool RaiseBossHealthMult(double candidate)
        {
            lock (sync)
            {
                var next = WorldEvent.RatchetMult(bossHealthMult, candidate);

                if (!(next > bossHealthMult))
                    return false;

                bossHealthMult = next;

                return true;
            }
        }

        /// <summary>
        /// The live objective creature (priority mob / mini-boss / boss), or null once it is dead or none was
        /// ever tracked. Used by the corruption meter tick to re-broadcast the priority mob's visual tag
        /// (round 13 feedback item F) without needing a wave-list scan for a creature that was never added to
        /// <see cref="liveWave"/> in the first place.
        /// </summary>
        public Creature ObjectiveCreature
        {
            get { lock (sync) return objectiveAlive ? objectiveCreature : null; }
        }

        public int LiveWaveCount
        {
            get { lock (sync) return liveWave.Count; }
        }

        /// <summary>The field as it stands, safe to enumerate outside the lock. Used by the meter's damage pass.</summary>
        public List<Creature> LiveWaveSnapshot()
        {
            lock (sync)
                return new List<Creature>(liveWave.Values);
        }

        /// <summary>
        /// Records one death, and everything that death carries, in ONE critical section: which bookkeeping it
        /// clears, the damage credit in <paramref name="dead"/>'s history, and any Kept Siraluun
        /// <paramref name="drops"/> already resolved for it.
        ///
        /// THE FEATHER ORDERING, and why it is one lock rather than three calls. Deliver reads the recorded
        /// drops (<see cref="KeptSiraluunDropsSnapshot"/>) only after <see cref="MarkEnded"/> has returned true,
        /// and MarkEnded flips the state under this same lock. This method refuses outright once the state is
        /// Ended. So every death either takes the lock BEFORE MarkEnded - its drop is recorded and Deliver's
        /// later snapshot must see it - or AFTER, in which case the encounter was already over and nothing is
        /// recorded at all. There is no third window in which a drop can land after Deliver has read the list,
        /// which is the race the old separate NoteCreatureDeath + AddKeptSiraluunDrop pair had.
        ///
        /// Returns <see cref="MlDigsiteDeathKind.None"/> when the encounter has ended or the guid is not one it
        /// tracks, which makes a death report from a creature of a finished encounter inert.
        /// </summary>
        /// <param name="waveNowEmpty">true when this death emptied the live field</param>
        public MlDigsiteDeathKind NoteCreatureDeath(uint guid, DateTime nowUtc, Creature dead,
            IReadOnlyList<PropertiesCreateList> drops, out bool waveNowEmpty)
        {
            lock (sync)
            {
                waveNowEmpty = false;

                if (state == MlDigsiteState.Ended)
                    return MlDigsiteDeathKind.None;

                MlDigsiteDeathKind kind;

                if (objectiveAlive && guid == objectiveGuid)
                {
                    objectiveAlive = false;
                    objectiveKilled = true;

                    // Round 16: a Corruption encounter's objective is one Corrupted mob at a time, not a
                    // single priority mob for the whole run - each kill counts toward the required total
                    // (MlDigsiteRules.CorruptionKillsWon), and the manager re-tracks the NEXT one via
                    // TrackObjective once this death is reported, rather than the objective staying dead.
                    if (Type == MlDigsiteType.CorruptionMeter)
                        corruptedKills++;

                    kind = MlDigsiteDeathKind.Objective;
                }
                else if (liveWave.Remove(guid))
                {
                    waveNowEmpty = liveWave.Count == 0;

                    // The field emptied: the wave just placed is CLEARED, which is what the Waves tier pays on.
                    if (waveNowEmpty && wavesSpawned > highestWaveCleared)
                        highestWaveCleared = wavesSpawned;

                    kind = MlDigsiteDeathKind.Wave;
                }
                else if (checkpointAlive && guid == checkpointGuid)
                {
                    checkpointAlive = false;
                    checkpointKills++;
                    kind = MlDigsiteDeathKind.Checkpoint;
                }
                else if (liveAdds.Remove(guid))
                {
                    // A Boss Rush mechanic add. Tracked in its OWN dictionary rather than in liveWave on
                    // purpose: an add must never set waveNowEmpty (there is no wave to clear in a Boss Rush,
                    // and in a Waves encounter it would arm the breather early), must never count toward
                    // highestWaveCleared, and must never be the objective. The driver reacts to this kind -
                    // a volatile add detonates, an immune phase's add set shrinks - and nothing else does.
                    kind = MlDigsiteDeathKind.Add;
                }
                else
                {
                    return MlDigsiteDeathKind.None;
                }

                lastKillUtc = nowUtc;

                if (dead != null)
                    participation.CreditDamage(dead);

                if (drops != null)
                {
                    foreach (var row in drops)
                    {
                        if (row != null)
                            keptSiraluunDrops.Add(row);
                    }
                }

                return kind;
            }
        }

        private bool objectiveKilled;

        // ---- Boss Rush mechanic adds ---------------------------------------------------------------------

        private readonly Dictionary<uint, Creature> liveAdds = new Dictionary<uint, Creature>();

        /// <summary>
        /// Registers a creature placed as <see cref="MlDigsiteRole.Add"/> by the Boss Rush mechanic driver.
        /// Kept APART from <see cref="liveWave"/> for the reason spelled out in
        /// <see cref="NoteCreatureDeath"/>: an add is not a wave, not a checkpoint and not the objective, and
        /// its death must move none of that bookkeeping.
        /// </summary>
        public void TrackAdd(Creature creature)
        {
            if (creature == null)
                return;

            lock (sync)
                liveAdds[creature.Guid.Full] = creature;
        }

        /// <summary>How many mechanic adds are still alive. Read by the driver to cap how many it places at once.</summary>
        public int LiveAddCount
        {
            get { lock (sync) return liveAdds.Count; }
        }

        /// <summary>
        /// The live Boss Rush adds as they stand, safe to enumerate outside the lock. Used by the drum
        /// cadence's Ring/Wall resolve to gather additional casters alongside the boss - deliberately this
        /// list and not <see cref="LiveWaveSnapshot"/>: an add is not a wave (see <see cref="TrackAdd"/>).
        /// </summary>
        public List<Creature> LiveAddSnapshot()
        {
            lock (sync)
                return new List<Creature>(liveAdds.Values);
        }

        // ---- wave pacing ---------------------------------------------------------------------------------

        private int wavesSpawned;
        private DateTime waveStartedUtc;
        private DateTime lastKillUtc;
        private DateTime? nextWaveDueUtc;

        public int WavesSpawned
        {
            get { lock (sync) return wavesSpawned; }
        }

        private int highestWaveCleared;

        /// <summary>
        /// The highest wave whose field was emptied (NoteCreatureDeath), 0 until one is. The Waves tier is paid
        /// on this, never on <see cref="WavesSpawned"/>: wave 1 is placed synchronously when the dig opens, so a
        /// spawned count would pay a bail that followed the dig without a single kill.
        /// </summary>
        public int HighestWaveCleared
        {
            get { lock (sync) return highestWaveCleared; }
        }

        public DateTime WaveStartedUtc
        {
            get { lock (sync) return waveStartedUtc; }
        }

        public DateTime LastKillUtc
        {
            get { lock (sync) return lastKillUtc; }
        }

        /// <summary>When the next wave is due, or null when none is scheduled (a wave is in progress).</summary>
        public DateTime? NextWaveDueUtc
        {
            get { lock (sync) return nextWaveDueUtc; }
        }

        /// <summary>
        /// Records that a wave has just been placed: bumps the count, restarts both wave clocks and clears
        /// any pending schedule. One call, so the two clocks can never be restarted independently.
        /// </summary>
        public void NoteWaveSpawned(DateTime nowUtc)
        {
            lock (sync)
            {
                wavesSpawned++;
                waveStartedUtc = nowUtc;
                lastKillUtc = nowUtc;
                nextWaveDueUtc = null;
            }
        }

        /// <summary>Arms the inter-wave delay. Ignored once the encounter has ended.</summary>
        public void ScheduleNextWave(DateTime dueUtc)
        {
            lock (sync)
            {
                if (state != MlDigsiteState.Active)
                    return;

                nextWaveDueUtc = dueUtc;
            }
        }

        // ---- the checkpoint mini-boss ----------------------------------------------------------------------

        private uint checkpointGuid;
        private bool checkpointAlive;
        private Creature checkpointCreature;

        /// <summary>
        /// Registers a Waves encounter's checkpoint mini-boss. Tracked APART from the live wave on purpose: it
        /// must never hold a wave open (a wave clears when its trash is dead, whether or not the checkpoint
        /// still stands), and its death is not the encounter's objective.
        ///
        /// Also clears the boss-HP milestone latch, so each new checkpoint announces its own 75/50/25%.
        /// </summary>
        public void TrackCheckpoint(Creature creature)
        {
            if (creature == null)
                return;

            lock (sync)
            {
                checkpointGuid = creature.Guid.Full;
                checkpointAlive = true;
                checkpointCreature = creature;
                bossHpBandsLatched.Clear();
            }
        }

        /// <summary>True while the most recent checkpoint mini-boss is alive. A new one is skipped while it is.</summary>
        public bool CheckpointAlive
        {
            get { lock (sync) return checkpointAlive; }
        }

        /// <summary>The live checkpoint mini-boss, or null once it is dead or none was ever placed.</summary>
        public Creature CheckpointCreature
        {
            get { lock (sync) return checkpointAlive ? checkpointCreature : null; }
        }

        // ---- corruption: Corrupted mob kill count (round 16 redesign) -----------------------------------
        //
        // The meter (a climbing fill that failed the run at 100%) is retired. A Corruption encounter now
        // wins by killing a fixed number of Corrupted mobs, one at a time - see MlDigsiteManager.DriveCorruption
        // and MlDigsiteRules.CorruptionKillsWon/CorruptionProgressFraction.

        private int corruptedKills;
        private DateTime? nextFieldSpawnUtc;

        /// <summary>Corrupted mobs killed so far this encounter. Reaching ml_digsite_corruption_kills_required wins.</summary>
        public int CorruptedKills
        {
            get { lock (sync) return corruptedKills; }
        }

        /// <summary>
        /// Whether the periodic field-reinforcement tick is due, and arms the next one
        /// <paramref name="interval"/> out from now. Same one-shot-per-interval shape as
        /// <see cref="TryClaimStatusTick"/>: the FIRST call for a new encounter only arms the clock and
        /// returns false, so the opening field spawn (already placed at TryStart) is not immediately doubled.
        /// </summary>
        public bool TryClaimFieldSpawnTick(DateTime now, TimeSpan interval)
        {
            lock (sync)
            {
                if (nextFieldSpawnUtc == null)
                {
                    nextFieldSpawnUtc = now + interval;
                    return false;
                }

                if (now < nextFieldSpawnUtc.Value)
                    return false;

                nextFieldSpawnUtc = now + interval;
                return true;
            }
        }

        // ---- corruption: the Corrupted mob's power gain (round 17) ---------------------------------------
        //
        // While a Corrupted mob is alive, the rest of the field hardens on a fixed cadence. The count of
        // gains ("stacks") belongs to ONE Corrupted mob: it is keyed by that mob's guid, so the next one -
        // placed only after the last has died - always starts from zero, even if the death-hook reset below
        // were somehow missed. The field's DamageRating is recomputed from the stack count each time
        // (MlDigsiteRules.DamageRatingFor), never incremented, so a skipped write can never compound.

        private uint corruptionPowerGuid;
        private int corruptionPowerStacks;
        private DateTime? nextCorruptionPowerUtc;
        private readonly Dictionary<uint, int> baseDamageRatings = new Dictionary<uint, int>();

        /// <summary>How many power gains the CURRENT Corrupted mob has produced. 0 with none alive.</summary>
        public int CorruptionPowerStacks
        {
            get { lock (sync) return corruptionPowerStacks; }
        }

        /// <summary>
        /// One power-gain decision for this tick. Returns true - and the new stack count - only on the tick a
        /// gain is due. The FIRST call for a given Corrupted mob only arms the clock, so a freshly placed mob
        /// gives the players one full interval before the first gain. A different live Corrupted mob than the
        /// one the stacks belong to resets them to zero first. No live Corrupted mob, a non-positive interval,
        /// or an encounter that is not Active never gains.
        /// </summary>
        public bool TryClaimCorruptionPowerTick(uint corruptedGuid, bool corruptedAlive, DateTime now, TimeSpan interval, out int stacks)
        {
            lock (sync)
            {
                stacks = corruptionPowerStacks;

                if (state != MlDigsiteState.Active || !corruptedAlive || corruptedGuid == 0 || interval <= TimeSpan.Zero)
                    return false;

                if (corruptedGuid != corruptionPowerGuid)
                {
                    corruptionPowerGuid = corruptedGuid;
                    corruptionPowerStacks = 0;
                    nextCorruptionPowerUtc = now + interval;
                    stacks = 0;
                    return false;
                }

                if (nextCorruptionPowerUtc == null)
                {
                    nextCorruptionPowerUtc = now + interval;
                    return false;
                }

                if (now < nextCorruptionPowerUtc.Value)
                    return false;

                corruptionPowerStacks++;
                nextCorruptionPowerUtc = now + interval;
                stacks = corruptionPowerStacks;

                return true;
            }
        }

        /// <summary>
        /// Drops every stack - the Corrupted mob that owned them has died, or the encounter is ending. Returns
        /// the stack count that was dropped, so the caller knows whether the field needs writing back to its
        /// base rating at all. The next Corrupted mob re-arms its own clock on its first tick.
        /// </summary>
        public int ResetCorruptionPower()
        {
            lock (sync)
            {
                var dropped = corruptionPowerStacks;

                corruptionPowerStacks = 0;
                corruptionPowerGuid = 0;
                nextCorruptionPowerUtc = null;

                return dropped;
            }
        }

        /// <summary>
        /// The DamageRating a field creature carried BEFORE any power gain touched it - its spawn value, which
        /// already includes crowd and per-wave scaling. Recorded on the FIRST call for that guid, which is
        /// always made from the power write itself, before that write changes anything; every later write is
        /// computed from this base rather than from the creature's current (already raised) rating.
        /// </summary>
        public int BaseDamageRating(uint guid, int currentRating)
        {
            lock (sync)
            {
                if (!baseDamageRatings.TryGetValue(guid, out var baseRating))
                {
                    baseRating = currentRating;
                    baseDamageRatings[guid] = baseRating;
                }

                return baseRating;
            }
        }

        // ---- corruption: pending Corrupted-mob respawn (round 16 code-review fix) -----------------------
        //
        // MlDigsiteSpawner.TrySpawn can legitimately return null (roster miss, a scatter-point/terrain snap
        // failure, EnterWorld failing, or the held list already closed under a race with Finish). Before this
        // fix, both call sites that place a Corrupted mob - OpenEncounter's opening spawn and OnCorruptedDied
        // after a kill - only logged a warning on that null and moved on, leaving the encounter with NO live
        // objective and nothing that would ever place another one: ObjectiveAlive stays false forever, no
        // creature can ever report the death that wins the run, and the encounter idles until its TTL. This
        // flag is the fix: either call site that fails to place a Corrupted mob marks the debt here, and
        // MlDigsiteManager.DriveCorruption retries it EVERY 1s tick (see MlDigsiteRules.ShouldRetryCorruptedSpawn)
        // until a spawn lands (TrackObjective clears the flag) or the run ends (Tick stops driving an encounter
        // once EndRequested is true, so the retry stops with it).

        private bool corruptedSpawnPending;

        /// <summary>Whether a Corrupted mob spawn is owed and has not yet landed. See the remarks above.</summary>
        public bool CorruptedSpawnPending
        {
            get { lock (sync) return corruptedSpawnPending; }
        }

        /// <summary>
        /// Marks that a Corrupted mob spawn attempt failed and still needs to happen. Idempotent - safe to
        /// call every time TrySpawn returns null, never only once. Cleared by <see cref="TrackObjective"/>,
        /// never here, so there is exactly one place a genuine spawn success can clear the debt.
        /// </summary>
        public void MarkCorruptedSpawnPending()
        {
            lock (sync)
                corruptedSpawnPending = true;
        }

        // ---- tracker: boss HP bands + periodic status cadence (round 13 feedback item E, part 3) --------

        private readonly HashSet<int> bossHpBandsLatched = new HashSet<int>();
        private DateTime? nextStatusDueUtc;

        /// <summary>
        /// Which boss-HP milestone tiers are newly due on this sample, latched against THIS encounter's own
        /// set so a mini-boss's 75/50/25% are each announced once per encounter.
        /// </summary>
        public List<int> DueBossHealthMilestones(uint current, uint max)
        {
            lock (sync)
                return MlDigsiteRules.DueBossHealthMilestones(current, max, bossHpBandsLatched);
        }

        /// <summary>
        /// Whether the periodic status line is due, and arms the next one <paramref name="interval"/> out
        /// from now. The FIRST call for a new encounter always returns false and only arms the clock - a
        /// periodic status line makes no sense fired in the same instant as the opening announcement.
        /// </summary>
        public bool TryClaimStatusTick(DateTime now, TimeSpan interval)
        {
            lock (sync)
            {
                if (nextStatusDueUtc == null)
                {
                    nextStatusDueUtc = now + interval;
                    return false;
                }

                if (now < nextStatusDueUtc.Value)
                    return false;

                nextStatusDueUtc = now + interval;
                return true;
            }
        }

        // ---- the leash heal (RoZ round 13, "Open-area safeguard") ----------------------------------------

        private bool tetherHealLatched;

        /// <summary>
        /// The once-per-return-trip latch for the tether heal. Returns true on the FIRST sample that sees the
        /// boss walking home (Monster.State.Return) and false on every sample after it, until a sample sees it
        /// out of that state again - which re-arms it, so a boss pulled twice heals twice.
        ///
        /// The reset is why this takes the current state rather than being a plain one-shot claim: the digsite
        /// tick samples every second, and a latch that never cleared would heal only the first pull of a
        /// ten-minute fight.
        /// </summary>
        public bool TryClaimTetherHeal(bool isReturning)
        {
            lock (sync)
            {
                if (!isReturning)
                {
                    tetherHealLatched = false;
                    return false;
                }

                if (tetherHealLatched)
                    return false;

                tetherHealLatched = true;

                return true;
            }
        }

        // ---- the Boss Rush mechanic driver ----------------------------------------------------------------

        private MlDigsiteBossMechanicState bossMechanics;

        /// <summary>
        /// This encounter's mechanic driver state, or null for an encounter that carries no mechanics (any
        /// shape but Boss Rush, or a Boss Rush opened while ml_digsite_bossrush_mechanics_enabled was false).
        ///
        /// Assigned exactly once, from MlDigsiteManager.OpenEncounter's BossRush branch, before the boss can
        /// have been hit - so every later reader either sees the finished state or sees null, never a
        /// half-built one. The state object carries its own lock; this property only hands it out.
        /// </summary>
        public MlDigsiteBossMechanicState BossMechanics
        {
            get { lock (sync) return bossMechanics; }
        }

        /// <summary>
        /// Attaches the driver state. Returns false if one is already attached, so a second call cannot
        /// silently replace a running set's cadences and phase bookkeeping.
        /// </summary>
        public bool TryAttachBossMechanics(MlDigsiteBossMechanicState state)
        {
            if (state == null)
                return false;

            lock (sync)
            {
                if (bossMechanics != null)
                    return false;

                bossMechanics = state;

                return true;
            }
        }

        // ---- presence --------------------------------------------------------------------------------------

        private DateTime lastPresenceUtc;

        /// <summary>When anyone was last seen at the digsite. The abandon test measures from here.</summary>
        public DateTime LastPresenceUtc
        {
            get { lock (sync) return lastPresenceUtc; }
        }

        public void MarkPresence(DateTime nowUtc)
        {
            lock (sync)
                lastPresenceUtc = nowUtc;
        }

        // ---- reward ----------------------------------------------------------------------------------------

        private bool rewardClaimed;

        /// <summary>
        /// Latches the right to pay this encounter out. Returns true to one caller only. Belt and braces behind
        /// <see cref="MarkEnded"/>: Finish is reached only from the world-thread tick now, so two payouts would
        /// already need two Finish calls to pass MarkEnded - this makes the chests, the XP and the luminance
        /// exactly-once even if that ever stops being true.
        /// </summary>
        public bool TryClaimReward()
        {
            lock (sync)
            {
                if (rewardClaimed)
                    return false;

                rewardClaimed = true;

                return true;
            }
        }

        // ---- the Kept Siraluun's feather, banked for the chest --------------------------------------------

        /// <summary>
        /// The create_list rows dying Kept Siraluun resolved (MlDigsiteRules.ResolveCreateListDrops), so the
        /// owner's chest can create them at Deliver time even though this encounter's creatures leave no corpse
        /// for GenerateTreasure to ever read them from. Paid on EVERY outcome - a killed Kept Siraluun always
        /// pays its feather.
        ///
        /// WRITTEN ONLY BY <see cref="NoteCreatureDeath"/>, inside the same critical section that records the
        /// death and refuses once the encounter has Ended. There is deliberately no separate "add a drop"
        /// method: a second writer outside that section is exactly how a drop could land after Deliver had
        /// already read this list.
        /// </summary>
        private readonly List<PropertiesCreateList> keptSiraluunDrops = new List<PropertiesCreateList>();

        /// <summary>A copy of the recorded drops, safe to enumerate outside the lock.</summary>
        public List<PropertiesCreateList> KeptSiraluunDropsSnapshot()
        {
            lock (sync)
                return new List<PropertiesCreateList>(keptSiraluunDrops);
        }

        // ---- end requests --------------------------------------------------------------------------------
        //
        // THE END PIPELINE. Nothing but MlDigsiteManager.Tick ever finishes an encounter. Every other place
        // that decides an encounter is over - a death hook on a landblock thread, the reap, the meter, a wave
        // clock, /digsite bail - only REQUESTS the end here, and the next world-thread tick consumes the
        // request and runs Finish -> MarkEnded -> Deliver -> TryClaimReward. The first request wins, under this
        // encounter's lock, so a win and a bail landing on the same tick produce exactly one end, filed under
        // whichever reached the lock first.

        private bool endRequested;
        private string requestedReason;
        private MlDigsiteResult requestedResult;

        /// <summary>
        /// Asks for this encounter to end. Returns true to the FIRST request only; every later one, and any
        /// request once the encounter has actually ended, is ignored and returns false. Never finishes anything
        /// itself - see the section remarks.
        /// </summary>
        public bool TryRequestEnd(string reason, MlDigsiteResult result)
        {
            lock (sync)
            {
                if (state == MlDigsiteState.Ended || endRequested)
                    return false;

                endRequested = true;
                requestedReason = reason;
                requestedResult = result;

                return true;
            }
        }

        /// <summary>True once an end has been requested, whether or not the tick has consumed it yet.</summary>
        public bool EndRequested
        {
            get { lock (sync) return endRequested; }
        }

        /// <summary>
        /// The pending request, for the tick to act on. Reads without clearing: the one-shot latch is
        /// <see cref="MarkEnded"/>, which Finish takes, so reading the same request twice can still only end
        /// the encounter once.
        /// </summary>
        public bool TryGetEndRequest(out string reason, out MlDigsiteResult result)
        {
            lock (sync)
            {
                reason = requestedReason;
                result = requestedResult;

                return endRequested && state != MlDigsiteState.Ended;
            }
        }

        // ---- endless waves: checkpoints and progress -----------------------------------------------------

        private readonly HashSet<int> checkpointWavesClaimed = new HashSet<int>();
        private int checkpointKills;

        /// <summary>
        /// Latches wave <paramref name="waveNumber"/>'s checkpoint mini-boss. True the first time for each wave
        /// and never again, which replaces the old single mini-boss latch: an endless run has one checkpoint per
        /// qualifying wave, and a wave re-entering the spawn path must never place a second.
        /// </summary>
        public bool TryClaimCheckpoint(int waveNumber)
        {
            lock (sync)
                return checkpointWavesClaimed.Add(waveNumber);
        }

        /// <summary>Checkpoint mini-bosses killed. Each one adds ml_digsite_checkpoint_bonus to the tier.</summary>
        public int CheckpointKills
        {
            get { lock (sync) return checkpointKills; }
        }

        private bool forcedCheckpointClaimed;

        /// <summary>
        /// Latches the encounter's ONE time-forced checkpoint mini-boss (round 14 feedback, driven by
        /// ml_digsite_forced_miniboss_seconds). True to the first caller only and false ever after, which is
        /// what stops the elapsed-time test - re-evaluated on every 1 s tick once it is past its threshold -
        /// from placing a mini-boss per tick. Claimed only when the caller is actually about to spawn.
        /// </summary>
        public bool TryClaimForcedCheckpoint()
        {
            lock (sync)
            {
                if (forcedCheckpointClaimed)
                    return false;

                forcedCheckpointClaimed = true;

                return true;
            }
        }

        // ---- Boss Rush: lowest health seen ---------------------------------------------------------------

        private double bossMinHealthFraction = 1.0;

        /// <summary>
        /// Folds one sample of the boss's health (a fraction of max) into the running minimum. A non-finite
        /// sample is ignored rather than allowed to poison the minimum. Called by the tick every second.
        /// </summary>
        public void NoteBossHealthSample(double fraction)
        {
            if (!double.IsFinite(fraction))
                return;

            fraction = Math.Clamp(fraction, 0.0, 1.0);

            lock (sync)
            {
                if (fraction < bossMinHealthFraction)
                    bossMinHealthFraction = fraction;
            }
        }

        /// <summary>The lowest health fraction the Boss Rush boss was ever sampled at. 1.0 when never hit.</summary>
        public double BossMinHealthFraction
        {
            get { lock (sync) return bossMinHealthFraction; }
        }

        // ---- the Kept Siraluun: one roll per encounter ---------------------------------------------------

        private bool keptSiraluunRolled;

        /// <summary>
        /// Latches the encounter's single Kept Siraluun roll. True once, to the first eligible spawn (the Boss
        /// Rush boss, or a Waves encounter's FIRST checkpoint mini-boss), and false ever after.
        /// </summary>
        public bool TryClaimKeptSiraluunRoll()
        {
            lock (sync)
            {
                if (keptSiraluunRolled)
                    return false;

                keptSiraluunRolled = true;

                return true;
            }
        }

        // ---- participation: who fought, and who was here --------------------------------------------------
        //
        // The ledger is WorldEventParticipation, reused rather than copied: its CreditDamage walks a dead
        // creature's DamageHistory and credits a pet's damage to its owner (DamageHistoryInfo.
        // TryGetPetOwnerOrAttacker). Two landblock threads can each kill one of this encounter's creatures at
        // once, so every touch of it goes through `sync`. Since 2026-10-04 the ledger also locks internally;
        // that inner lock is a leaf (it never calls out while held), so the nesting `sync` -> ledger lock
        // cannot deadlock and changes nothing here. This encounter never calls the ledger's hit-time contact
        // writer, so its records are exactly the death-and-finish damage credits they always were.

        private readonly WorldEventParticipation participation = new WorldEventParticipation();
        private readonly Dictionary<uint, DateTime> lastPresentUtc = new Dictionary<uint, DateTime>();

        /// <summary>
        /// Credits every player (and pet owner) in <paramref name="creature"/>'s damage history. Called at each
        /// encounter creature's death and, at Finish, for every held creature still alive, so a group that
        /// wiped on a boss it never killed is still credited for the damage it did.
        /// </summary>
        public void CreditDamage(Creature creature)
        {
            if (creature == null)
                return;

            lock (sync)
                participation.CreditDamage(creature);
        }

        /// <summary>Test seam: credits damage by guid without a live DamageHistory (no Player can be built in the test tree).</summary>
        internal void CreditDamageForTest(uint characterGuid, float damage)
        {
            lock (sync)
                participation.CreditDamageForTest(characterGuid, damage);
        }

        /// <summary>
        /// Every credited player and their total damage, copied under the lock so the caller can walk it freely.
        /// </summary>
        public Dictionary<uint, float> DamageCreditSnapshot()
        {
            lock (sync)
            {
                var copy = new Dictionary<uint, float>();

                foreach (var rec in participation.Records)
                    copy[rec.CharacterGuid] = rec.Damage;

                return copy;
            }
        }

        /// <summary>
        /// Stamps every guid in <paramref name="playerGuids"/> as seen at the site at <paramref name="nowUtc"/>.
        /// The reap calls this with its live-audience scan; eligibility then asks how long ago each player was
        /// last seen, rather than whether they happen to be standing there at the end.
        /// </summary>
        public void MarkPlayersPresent(IEnumerable<uint> playerGuids, DateTime nowUtc)
        {
            if (playerGuids == null)
                return;

            lock (sync)
            {
                foreach (var guid in playerGuids)
                    lastPresentUtc[guid] = nowUtc;
            }
        }

        /// <summary>When <paramref name="playerGuid"/> was last seen at the site, or null if never.</summary>
        public DateTime? LastPresentUtc(uint playerGuid)
        {
            lock (sync)
                return lastPresentUtc.TryGetValue(playerGuid, out var at) ? at : (DateTime?)null;
        }

        // ---- the payout snapshot -------------------------------------------------------------------------

        /// <summary>
        /// Everything the payout depends on, read in ONE critical section so the fraction is computed from one
        /// consistent moment rather than from fields read one at a time while a death hook moves them.
        ///
        /// <see cref="MlDigsitePayoutSnapshot.Bailed"/> is read from the latched end request, so the payout of a
        /// bailed encounter prices the bail; <paramref name="assumeBailed"/> forces it for the bail prompt and
        /// the status line, which price a bail that has not been requested yet.
        /// </summary>
        public MlDigsitePayoutSnapshot PayoutSnapshot(MlDigsiteResult result, bool assumeBailed = false)
        {
            lock (sync)
                return new MlDigsitePayoutSnapshot(result, wavesSpawned, checkpointKills, bossMinHealthFraction,
                    Type == MlDigsiteType.BossRush && objectiveKilled, highestWaveCleared,
                    assumeBailed || (endRequested && requestedReason == MlDigsiteRules.EndReasons.Bailed),
                    corruptedKills);
        }
    }
}
