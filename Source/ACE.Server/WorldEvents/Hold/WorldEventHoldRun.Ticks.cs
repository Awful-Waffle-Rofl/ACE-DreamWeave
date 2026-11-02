using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// What a Hold run says through its run (World Events Hold H6), supplied by HoldObjective. Announce and AnnounceLocal are
    /// called on the world thread only.
    /// </summary>
    public interface IWorldEventHoldVoice
    {
        /// <summary>The run's anchor name, for {anchor} and the milestone line.</summary>
        string AnchorName { get; }

        /// <summary>The run's composed monster family display name, for {family}/{Family}.</summary>
        string FamilyDisplayName { get; }

        /// <summary>A World Event line during the run: world-wide, or to the players near the anchor when the run announces locally.</summary>
        void Announce(string line);

        /// <summary>A line to the players near the anchor only (the theme's wave chatter, a pull call-out).</summary>
        void AnnounceLocal(string line);
    }

    /// <summary>
    /// World Events Hold H6: the run's once-a-second ticks and its knights' voice. Called by HoldObjective.Tick on the world
    /// thread, after the spawner's views were published (PublishHoldTo). Every creature change - a heal, a health rescale, a
    /// pose step, an effect, a wake, the finale boss's target - is ENQUEUED on that creature's own landblock; nothing here
    /// writes a creature from the world thread. Every tick is wrapped: one failing never stops the others or the run.
    /// </summary>
    public sealed partial class WorldEventHoldRun
    {
        private volatile IWorldEventHoldVoice voice;

        private WorldObject[] shrineObjects = Array.Empty<WorldObject>();

        private Func<uint, bool> isParticipant;

        // ---- world thread only ----

        private readonly Random lineRng = new Random();
        private readonly object lineRngSync = new object();

        private int healedForClears;
        private double lastRegenAt;
        private double lastBossEffectAt;
        private double lastCorruptedEffectAt;

        private readonly HashSet<uint> greeted = new HashSet<uint>();
        private readonly Dictionary<uint, HashSet<int>> milestonesFired = new Dictionary<uint, HashSet<int>>();
        private readonly Dictionary<uint, int> knightHealthTarget = new Dictionary<uint, int>();
        private readonly HashSet<uint> bossArrivals = new HashSet<uint>();
        private readonly HashSet<uint> enraged = new HashSet<uint>();
        private readonly HashSet<uint> wardArrivals = new HashSet<uint>();
        private readonly Dictionary<uint, double> shrinePulsedAt = new Dictionary<uint, double>();

        private WorldEventHoldFinaleController finale;

        /// <summary>Lines said off the world thread (a knight's last words) whose world copy waits for the next tick.</summary>
        private readonly ConcurrentQueue<string> pendingWorldLines = new ConcurrentQueue<string>();

        /// <summary>World thread, once, before the run goes Active: who speaks for this run.</summary>
        public void AttachVoice(IWorldEventHoldVoice v) => voice = v;

        /// <summary>
        /// World thread, once: the presence-ledger test behind <see cref="IsParticipant"/>. Without one every character counts
        /// (the view's default), so a run built in a test needs no ledger.
        /// </summary>
        public void SetParticipantTest(Func<uint, bool> test) => Volatile.Write(ref isParticipant, test);

        /// <inheritdoc/>
        /// <remarks>Any thread. A ledger that throws reads as "not a participant" (a shrine refuses rather than heals for free).</remarks>
        public bool IsParticipant(uint guid)
        {
            var test = Volatile.Read(ref isParticipant);

            if (test == null)
                return true;

            try
            {
                return guid != 0 && test(guid);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} participant test threw for 0x{guid:X8}; refusing", ex);
                return false;
            }
        }

        // ---- lines -----------------------------------------------------------------------------------------------------

        /// <summary>
        /// One line from <paramref name="pool"/> with this run's tokens filled ({family}, the nouns, {anchor}, the alive and
        /// total counts, and <paramref name="pct"/>), or null when the pool is empty. Any thread.
        /// </summary>
        public string ComposeLine(IReadOnlyList<string> pool, int? pct = null)
        {
            var protect = goal?.Protect;

            if (pool == null || pool.Count == 0)
                return null;

            string template;

            lock (lineRngSync)
                template = WorldEventHoldRules.PickLine(pool, lineRng);

            if (template == null)
                return null;

            var v = voice;

            return WorldEventHoldRules.FillLine(template, v?.FamilyDisplayName, protect?.EffectiveNoun ?? ProtectDef.DefaultNoun,
                protect?.EffectiveNounPlural ?? ProtectDef.DefaultNounPlural, v?.AnchorName, LiveKnightGuids.Count, PlacedKnightGuids.Count, pct);
        }

        /// <summary>A random standing knight, or null. Any thread.</summary>
        public Creature RandomStandingKnight()
        {
            var standing = Volatile.Read(ref knights).Where(k => k != null && !k.IsDead && !k.IsDestroyed).ToList();

            if (standing.Count == 0)
                return null;

            lock (lineRngSync)
                return standing[lineRng.Next(standing.Count)];
        }

        /// <summary><paramref name="speaker"/> says a line from <paramref name="pool"/>, queued on its landblock. Any thread.</summary>
        public void SayQueued(Creature speaker, IReadOnlyList<string> pool, int? pct = null) => SayQueued(speaker, ComposeLine(pool, pct));

        /// <summary><paramref name="speaker"/> says <paramref name="line"/> in local chat, queued on its landblock. Any thread.</summary>
        public void SayQueued(Creature speaker, string line)
        {
            var landblock = speaker?.CurrentLandblock;

            if (landblock == null || string.IsNullOrEmpty(line))
                return;

            var s = speaker;

            landblock.EnqueueAction(new ActionEventDelegate(() => Say(s, line)));
        }

        /// <summary>The speaker says a line from <paramref name="pool"/> at once. Its own landblock thread only.</summary>
        private void SayNow(Creature speaker, IReadOnlyList<string> pool) => Say(speaker, ComposeLine(pool));

        /// <summary>The local-chat speech itself, like an npc's Say emote. The speaker's landblock thread. Never throws.</summary>
        private void Say(Creature speaker, string line)
        {
            if (speaker == null || speaker.IsDestroyed || string.IsNullOrEmpty(line))
                return;

            try
            {
                log.Info($"[WORLDEVENT] run={RunId} knight_say guid=0x{speaker.Guid.Full:X8} \"{line}\"");

                speaker.EnqueueBroadcast(new GameMessageHearSpeech(line, speaker.Name, speaker.Guid.Full, ChatMessageType.Emote),
                    WorldObject.LocalBroadcastRange);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} knight speech threw", ex);
            }
        }

        /// <summary>
        /// World thread, at a paying success (HoldObjective.OnFinish): one standing knight says a protect.lines.success line,
        /// queued on its landblock (the same queue the cleanup destroys it through, so the line is heard first). Returns the
        /// world copy for the caller to send, or null when no knight stands or the goal has no success lines.
        /// </summary>
        public string SpeakSuccessLine()
        {
            var lines = goal?.Protect?.Lines?.Success;

            if (lines == null || lines.Count == 0)
                return null;

            var speaker = RandomStandingKnight();

            if (speaker == null)
                return null;

            var line = ComposeLine(lines);

            if (string.IsNullOrEmpty(line))
                return null;

            SayQueued(speaker, line);

            return WorldEventHoldRules.KnightSpeechLine(speaker.Name, line);
        }

        /// <summary>World thread: a World Event line through the voice (nothing without one).</summary>
        private void Announce(string line)
        {
            if (!string.IsNullOrEmpty(line))
                voice?.Announce(line);
        }

        // ---- deaths (the dying creature's landblock thread) ---------------------------------------------------------

        /// <summary>
        /// A creature of this run died (HoldObjective.OnCreatureDied, on the dying creature's landblock thread). A knight says
        /// its last words at once, and their world copy goes out on the next world tick. A corrupted monster's death heals every
        /// standing knight (corrupted_kill_heal) and one of them says so; a monster that was attacking a knight, killed by a
        /// player or a pet, restores a little of that knight (rescue_heal). Every heal is queued on the knight's own landblock.
        /// </summary>
        public void OnCreatureDied(Creature creature, bool killerIsPlayer, bool killerHasPetOwner)
        {
            var tag = creature?.WorldEventHold;

            if (tag == null || tag.RunId != RunId)
                return;

            var protect = goal?.Protect;
            var t = tuning;

            if (tag.Kind == HoldUnitKind.Knight)
            {
                var last = ComposeLine(protect?.Lines?.Destroyed);

                if (last != null)
                {
                    Say(creature, last);
                    pendingWorldLines.Enqueue(WorldEventHoldRules.KnightSpeechLine(creature.Name, last));
                }

                return;
            }

            if (tag.Corrupted && t.CorruptedKillHeal > 0)
            {
                var standing = Volatile.Read(ref knights).Where(k => k != null && !k.IsDead && !k.IsDestroyed).ToList();

                foreach (var k in standing)
                    WorldEventHoldHeals.Queue(k, t.CorruptedKillHeal, HoldHealSource.CorruptedKill);

                log.Info($"[WORLDEVENT] run={RunId} corrupted {creature.Name} slain; {standing.Count} knight(s) heal {t.CorruptedKillHeal:P0}");

                if (standing.Count > 0)
                    SayQueued(RandomStandingKnight(), protect?.Lines?.CorruptedKill);
            }

            if (t.RescueHeal > 0 && creature.AttackTarget is Creature victim && victim.WorldEventHold?.Kind == HoldUnitKind.Knight
                && victim.WorldEventHold.RunId == RunId && !victim.IsDead && WorldEventHoldRules.IsRescueKill(killerIsPlayer, killerHasPetOwner))
            {
                WorldEventHoldHeals.Queue(victim, t.RescueHeal, HoldHealSource.Rescue);
            }
        }

        // ---- the once-a-second ticks (world thread) ---------------------------------------------------------------------

        /// <summary>
        /// World thread, once a second while Active: every run tick in order. <paramref name="clearedWaves"/> is how many waves
        /// the spawner has seen cleared so far.
        /// </summary>
        public void TickRuntime(double now, int clearedWaves)
        {
            Guard("world lines", () =>
            {
                while (pendingWorldLines.TryDequeue(out var line))
                    Announce(line);
            });

            Guard("between-waves heal", () => TickBetweenWaveHeals(clearedWaves));
            Guard("clear regeneration", () => TickClearRegen(now));
            Guard("shrine pulse", () => TickShrinePulse(now));
            Guard("knight health", TickKnightHealth);
            Guard("milestones", TickMilestones);
            Guard("wake", TickWake);
            Guard("defend pose", () => TickDefendPose(now));
            Guard("markers", () => TickMarkers(now));
            Guard("finale", () => TickFinale(now));
        }

        private void Guard(string what, Action tick)
        {
            try
            {
                tick();
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} hold {what} tick threw", ex);
            }
        }

        private List<Creature> StandingKnights() =>
            Volatile.Read(ref knights).Where(k => k != null && !k.IsDead && !k.IsDestroyed).ToList();

        private List<Creature> LiveMonsters() =>
            Volatile.Read(ref waveMonsters).Where(m => m != null && !m.IsDead && !m.IsDestroyed).ToList();

        /// <summary>
        /// heal_between_waves above 0: every time a wave is fully cleared, each standing knight regains that share (queued on its
        /// landblock, through the one heal path), and one of them says a protect.lines.heal line. Several clears between two
        /// ticks heal once per wave.
        /// </summary>
        private void TickBetweenWaveHeals(int cleared)
        {
            var share = tuning.HealBetweenWaves;

            if (cleared <= healedForClears)
                return;

            var newClears = cleared - healedForClears;
            healedForClears = cleared;

            if (!(share > 0))
                return;

            var standing = StandingKnights();

            if (standing.Count == 0)
                return;

            foreach (var k in standing)
                WorldEventHoldHeals.Queue(k, Math.Min(1.0, share * newClears), HoldHealSource.BetweenWaves);

            log.Info($"[WORLDEVENT] run={RunId} hold heal {share:P0} x{newClears} for {standing.Count} knight(s) (waves cleared={cleared})");

            SayQueued(RandomStandingKnight(), goal?.Protect?.Lines?.Heal);
        }

        /// <summary>regen_per_second: a knight with no living run monster within regen_clear_radius regenerates, in steps of regen_interval_seconds.</summary>
        private void TickClearRegen(double now)
        {
            var t = tuning;

            if (!(t.RegenPerSecond > 0))
                return;

            var interval = Math.Max(1, t.RegenIntervalSeconds);

            if (now - lastRegenAt < interval - 0.01)
                return;

            lastRegenAt = now;

            var monsters = LiveMonsters().Select(m => m.Location).Where(l => l != null).ToList();

            foreach (var k in StandingKnights())
            {
                var at = k.Location;

                if (at == null || k.Health.Current >= k.Health.MaxValue)
                    continue;

                var nearest = monsters.Count == 0 ? (double?)null : monsters.Min(m => (double)at.DistanceTo(m));

                if (!WorldEventHoldRules.IsClear(nearest, t.RegenClearRadius))
                    continue;

                WorldEventHoldHeals.Queue(k, Math.Min(0.1, t.RegenPerSecond) * interval, HoldHealSource.Regen);
            }
        }

        /// <summary>A ready shrine plays its ready effect when it becomes ready, and every protect.shrine.readyPulseSeconds while ready.</summary>
        private void TickShrinePulse(double now)
        {
            var shrine = goal?.Protect?.Shrine;

            if (shrine == null || !tuning.ShrineEnabled || !isActive)
                return;

            if (!WorldEventHoldRules.TryParseEffect(shrine.ReadyEffect, out var ready) || ready == PlayScript.Invalid)
                return;

            foreach (var item in Volatile.Read(ref shrineObjects))
            {
                if (item == null || item.IsDestroyed)
                    continue;

                double readyAt;

                lock (sync)
                    shrineReady.TryGetValue(item.Guid.Full, out readyAt);

                shrinePulsedAt.TryGetValue(item.Guid.Full, out var pulsedAt);

                if (!WorldEventHoldRules.ShrineReadyPulseDue(now, readyAt, pulsedAt, shrine.ReadyPulseSeconds))
                    continue;

                shrinePulsedAt[item.Guid.Full] = now;
                PlayEffectQueued(item, ready);
            }
        }

        /// <summary>
        /// Live knight health (knight_max_health and _by_wcid): a standing knight whose setting moved since it was placed (or
        /// last rescaled) is rescaled on its own landblock, keeping its health percentage. A knight first seen records the value
        /// it was placed with, so with nothing changed this never touches a knight.
        /// </summary>
        private void TickKnightHealth()
        {
            var protect = goal?.Protect;

            if (protect == null)
                return;

            var t = tuning;

            foreach (var k in StandingKnights())
            {
                var guid = k.Guid.Full;
                var desired = WorldEventSpawner.KnightHealth(k.WeenieClassId, t, protect, t.Participants);

                if (!knightHealthTarget.TryGetValue(guid, out var placed))
                {
                    knightHealthTarget[guid] = desired;
                    continue;
                }

                if (desired <= 0 || desired == placed || k.CurrentLandblock == null)
                    continue;

                knightHealthTarget[guid] = desired;

                var knight = k;

                k.CurrentLandblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        if (knight.IsDead || knight.IsDestroyed)
                            return;

                        var before = knight.Health.MaxValue;
                        var current = WorldEventHoldRules.RescaledHealth(knight.Health.Current, before, (uint)desired);

                        WorldEventSpawner.ApplyHealth(knight, desired);
                        knight.Health.Current = Math.Min(knight.Health.MaxValue, current);

                        log.Info($"[WORLDEVENT] run={RunId} knight {knight.Name} max health {before} -> {knight.Health.MaxValue} (setting), now {knight.Health.Current}");
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={RunId} knight health rescale threw", ex);
                    }
                }));
            }
        }

        /// <summary>
        /// Each knight's spawn line (once, the first tick it stands) and its health milestones (milestone_percents, once per tier
        /// per knight): the knight's protect.lines.health line, and with health_milestones the world line.
        /// </summary>
        private void TickMilestones()
        {
            var protect = goal?.Protect;

            if (protect == null)
                return;

            var rt = tuning.Runtime;

            foreach (var k in StandingKnights())
            {
                var guid = k.Guid.Full;

                if (greeted.Add(guid))
                    SayQueued(k, protect.Lines?.Spawn);

                if (!milestonesFired.TryGetValue(guid, out var fired))
                    milestonesFired[guid] = fired = new HashSet<int>();

                foreach (var pct in WorldEventHoldRules.DueMilestones(k.Health.Current, k.Health.MaxValue, fired, rt.MilestonePercents))
                {
                    if (rt.HealthMilestones)
                    {
                        log.Info($"[WORLDEVENT] run={RunId} announce knight_health guid=0x{guid:X8} {pct}");
                        Announce(WorldEventHoldRules.KnightHealthMilestoneLine(protect.EffectiveNoun, voice?.AnchorName, pct));
                    }

                    SayQueued(k, protect.Lines?.Health, pct);
                }
            }
        }

        /// <summary>
        /// Every wave monster still asleep is asked to wake, on its own landblock (Creature.TryHoldWake), so a wave never stands
        /// at its portal until a player walks into its awareness range. Asked again every tick while it sleeps.
        /// </summary>
        private void TickWake()
        {
            if (!isActive)
                return;

            foreach (var m in LiveMonsters())
            {
                if (m.MonsterState != Creature.State.Idle || m.CurrentLandblock == null)
                    continue;

                var monster = m;

                m.CurrentLandblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        if (monster.TryHoldWake())
                            log.Debug($"[WORLDEVENT] run={RunId} hold wake guid=0x{monster.Guid.Full:X8} target={(monster.AttackTarget as Creature)?.Name ?? "none"}");
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={RunId} hold wake threw", ex);
                    }
                }));
            }
        }

        /// <summary>
        /// defend_pose: each standing knight is handed the nearest of this run's living monsters in reach (edge to edge) and plays
        /// its cosmetic defense on its own landblock (Creature.TickHoldDefendPose). Only the run's own monsters are offered.
        /// </summary>
        private void TickDefendPose(double now)
        {
            var t = tuning;

            if (!t.DefendPoseEnabled || !isActive)
                return;

            var monsters = LiveMonsters().Where(m => m.Location != null && !(m is Player)).ToArray();

            foreach (var k in StandingKnights())
            {
                if (k.CurrentLandblock == null || k.Location == null)
                    continue;

                var knight = k;
                int seed;

                lock (lineRngSync)
                    seed = lineRng.Next();

                k.CurrentLandblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        if (knight.IsDead || knight.Location == null)
                            return;

                        var selfRadius = knight.PhysicsObj?.GetRadius() ?? 0f;
                        var gaps = monsters.Where(m => !m.IsDead && m.Location != null)
                            .Select(m => (m.Guid.Full, (double)(knight.Location.DistanceTo(m.Location) - selfRadius - (m.PhysicsObj?.GetRadius() ?? 0f))));

                        var foeGuid = WorldEventHoldRules.PickDefendFoe(gaps, t.DefendReach, knight.HoldState.DefendFoe);
                        var foe = foeGuid == 0 ? null : monsters.FirstOrDefault(m => m.Guid.Full == foeGuid);

                        var action = knight.TickHoldDefendPose(foe, ACE.Common.Time.GetUnixTime(), new Random(seed));

                        if (action == HoldPoseAction.DrawWeapon)
                            log.Debug($"[WORLDEVENT] run={RunId} defend pose: {knight.Name} draws its weapon");
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={RunId} defend pose threw", ex);
                    }
                }));
            }
        }

        /// <summary>
        /// The cosmetic markers: the finale boss's arrival effect once and its pulse every protect.bossEffectSeconds, the
        /// corrupted effect every protect.corrupted.effectSeconds, and a warded monster's restore effect when it arrives and each
        /// time its ward comes back (decided on the monster's own landblock, where its ward state lives).
        /// </summary>
        private void TickMarkers(double now)
        {
            var protect = goal?.Protect;

            if (protect == null)
                return;

            WorldEventHoldRules.TryParseEffect(protect.BossEffect, out var bossPulse);
            WorldEventHoldRules.TryParseEffect(protect.BossArrivalEffect, out var bossArrival);

            var bossDue = bossPulse != PlayScript.Invalid && now - lastBossEffectAt >= Math.Max(1.0, protect.BossEffectSeconds);

            if (bossDue)
                lastBossEffectAt = now;

            var corrupted = protect.EffectiveCorrupted;
            WorldEventHoldRules.TryParseEffect(corrupted.Effect, out var corruptedEffect);
            WorldEventHoldRules.TryParseEffect(corrupted.ExtraEffect, out var corruptedExtra);

            var corruptedDue = corruptedEffect != PlayScript.Invalid && now - lastCorruptedEffectAt >= Math.Max(1.0, corrupted.EffectSeconds);

            if (corruptedDue)
                lastCorruptedEffectAt = now;

            var restore = PlayScript.Invalid;

            if (protect.Ward != null)
                WorldEventHoldRules.TryParseEffect(protect.Ward.RestoreEffect, out restore);

            foreach (var m in LiveMonsters())
            {
                var tag = m.WorldEventHold;

                if (tag == null || m.CurrentLandblock == null)
                    continue;

                if (tag.Kind == HoldUnitKind.FinaleBoss)
                {
                    var first = bossArrivals.Add(m.Guid.Full);
                    var script = first && bossArrival != PlayScript.Invalid ? bossArrival : bossDue ? bossPulse : PlayScript.Invalid;

                    if (first)
                        log.Info($"[WORLDEVENT] run={RunId} hold finale boss {m.Name} 0x{m.Guid.Full:X8} has arrived");

                    PlayEffectQueued(m, script);
                    continue;
                }

                if (tag.Corrupted && corruptedDue)
                {
                    PlayEffectQueued(m, corruptedEffect);
                    PlayEffectQueued(m, corruptedExtra);
                }

                if (tag.Warded && restore != PlayScript.Invalid)
                {
                    var arrived = wardArrivals.Add(m.Guid.Full);
                    var monster = m;

                    m.CurrentLandblock.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        try
                        {
                            if (monster.IsDead || monster.IsDestroyed)
                                return;

                            var s = monster.HoldState;
                            var restored = false;

                            lock (s)
                            {
                                if (s.WardRestorePending && monster.IsHoldWardUp(ACE.Common.Time.GetUnixTime()))
                                {
                                    s.WardRestorePending = false;
                                    restored = true;
                                }
                            }

                            if (arrived || restored)
                                monster.EnqueueBroadcast(new GameMessageScript(monster.Guid, restore, 1.0f));
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[WORLDEVENT] run={RunId} ward marker threw", ex);
                        }
                    }));
                }
            }
        }

        /// <summary>Plays <paramref name="script"/> on <paramref name="target"/> through its own landblock queue (nothing for Invalid).</summary>
        private static void PlayEffectQueued(WorldObject target, PlayScript script)
        {
            if (target?.CurrentLandblock == null || script == PlayScript.Invalid)
                return;

            var t = target;

            target.CurrentLandblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                if (!t.IsDestroyed)
                    t.EnqueueBroadcast(new GameMessageScript(t.Guid, script, 1.0f));
            }));
        }

        /// <summary>World thread: the portals flare as a wave comes through (protect.portals.pulseEffect on every part placed).</summary>
        public void PulsePortals(IEnumerable<WorldObject> sceneObjects)
        {
            var portals = goal?.Protect?.Portals;

            if (portals?.Parts == null || !WorldEventHoldRules.TryParseEffect(portals.PulseEffect, out var pulse) || pulse == PlayScript.Invalid)
                return;

            var parts = new HashSet<uint>(portals.Parts.Where(p => p != null).Select(p => p.Wcid));

            foreach (var part in sceneObjects ?? Enumerable.Empty<WorldObject>())
            {
                if (part != null && parts.Contains(part.WeenieClassId))
                    PlayEffectQueued(part, pulse);
            }
        }

        /// <summary>
        /// The finale boss: once a second the controller (WorldEventHoldFinaleController) decides from the standing knights and
        /// the boss's recent player damage (Creature.HoldBossDamageSnapshot), and its decision is applied ON THE BOSS'S LANDBLOCK
        /// through <see cref="ApplyFinaleDecision"/> (Creature.SetHoldPull). The target and pull call-outs go out here; the
        /// enrage line once per boss.
        /// </summary>
        private void TickFinale(double now)
        {
            var boss = LiveMonsters().FirstOrDefault(m => m.WorldEventHold?.Kind == HoldUnitKind.FinaleBoss);

            finale ??= new WorldEventHoldFinaleController { Apply = ApplyFinaleDecision };

            var t = tuning;

            if (boss == null)
            {
                finale.Tick(now, null, Array.Empty<Creature>(), (0, 0u), false, t);
                return;
            }

            if (!enraged.Contains(boss.Guid.Full) && FinaleEnrageMult(boss.Guid.Full, now) > 1.0)
            {
                enraged.Add(boss.Guid.Full);
                log.Info($"[WORLDEVENT] run={RunId} {boss.Name} enrages");
                Announce(WorldEventHoldRules.EnrageLine(boss.Name));
            }

            var recent = WorldEventHoldFinaleBoss.RecentDamage(boss.HoldBossDamageSnapshot(now), now, t.FinalePullWindowSeconds);

            bool held;
            var s = boss.HoldState;

            lock (s)
                held = s.PullTarget is Player && now < s.PullUntil;

            var decision = finale.Tick(now, boss, StandingKnights(), recent, held, t);

            if (!decision.Changed)
                return;

            var protect = goal?.Protect;

            switch (decision.Kind)
            {
                case HoldFinaleDecisionKind.Target:
                {
                    var knight = StandingKnights().FirstOrDefault(k => k.Guid.Full == decision.TargetGuid);
                    var line = ComposeLine((protect?.BossTargetAnnounce ?? new List<string>())
                        .Select(l => WorldEventHoldFinaleBoss.TargetLine(l, boss.Name, knight?.Name)).ToList());

                    if (!string.IsNullOrEmpty(line))
                        Announce(WorldEventAnnouncer.Global(line));
                    break;
                }

                case HoldFinaleDecisionKind.Pull:
                {
                    var player = PlayerManager.GetOnlinePlayer(decision.PlayerGuid);
                    var line = ComposeLine((protect?.BossPulledAnnounce ?? new List<string>())
                        .Select(l => WorldEventHoldFinaleBoss.PulledLine(l, boss.Name, player?.Name)).ToList());

                    if (!string.IsNullOrEmpty(line))
                        voice?.AnnounceLocal(line);
                    break;
                }
            }
        }

        /// <summary>
        /// The finale controller's sink, ON THE BOSS'S LANDBLOCK (WorldEventHoldFinaleController.Tick enqueues it there): a knight
        /// target or a player pull holds the boss on that creature (Creature.SetHoldPull) and re-targets it at once; on the
        /// players the hold is released and the boss's own seeking takes over.
        /// </summary>
        private void ApplyFinaleDecision(Creature boss, HoldFinaleDecision d)
        {
            var now = ACE.Common.Time.GetUnixTime();
            var t = tuning;

            switch (d.Kind)
            {
                case HoldFinaleDecisionKind.Target:
                {
                    var knight = Volatile.Read(ref knights).FirstOrDefault(k => k != null && k.Guid.Full == d.TargetGuid);

                    if (knight == null || knight.IsDead)
                        return;

                    boss.SetHoldPull(knight, now + Math.Max(1, t.FinaleSwapMaxSeconds));
                    break;
                }

                case HoldFinaleDecisionKind.Pull:
                {
                    var player = PlayerManager.GetOnlinePlayer(d.PlayerGuid);

                    if (player == null || player.IsDead)
                        return;

                    boss.SetHoldPull(player, d.Until);
                    break;
                }

                case HoldFinaleDecisionKind.OnPlayers:
                    boss.SetHoldPull(null, 0);
                    break;

                default:
                    return;
            }

            if (boss.MonsterState != Creature.State.Idle)
                boss.TrySelectHoldTarget();

            log.Info($"[WORLDEVENT] run={RunId} hold finale boss {boss.Name}: {d}");
        }
    }
}
