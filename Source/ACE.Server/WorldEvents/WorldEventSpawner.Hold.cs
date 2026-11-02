using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// World Events Hold H5: the spawner's Hold side - the knights and their scene (<see cref="SpawnProtected"/>), the tagged
    /// waves (<see cref="SpawnHoldWave"/>), and the views H6 publishes to the run (<see cref="PublishHoldTo"/>).
    ///
    /// The one rule this file exists to keep: a Hold creature's <see cref="WorldEventHoldTag"/> is built here, during
    /// construction on the world thread, and set by TryPlace exactly once, BEFORE EnterWorld. Nothing here writes a creature
    /// after it entered the world; the finale controller (<see cref="WorldEventHoldFinaleController"/>) enqueues its writes on
    /// the boss's landblock.
    ///
    /// THREADING: SpawnProtected, SpawnHoldWave and PublishHoldTo are world thread. The knight lists are guarded by
    /// <see cref="sync"/> like every other list here; the read-side views copy under it.
    /// </summary>
    public sealed partial class WorldEventSpawner
    {
        /// <summary>The PlayerKillerStatus every knight is sent with (the retail shopkeepers' value).</summary>
        public const PlayerKillerStatus ProtectedPlayerKillerStatus = PlayerKillerStatus.RubberGlue;

        /// <summary>Live knights. Guarded by <see cref="sync"/>.</summary>
        private readonly List<Creature> liveKnights = new List<Creature>();

        /// <summary>Every knight ever adopted, in placement order. Guarded by <see cref="sync"/>.</summary>
        private readonly List<uint> knightGuids = new List<uint>();

        /// <summary>Finale bosses adopted, with their adopt time, not yet handed to the run. Guarded by <see cref="sync"/>.</summary>
        private readonly Dictionary<uint, double> pendingFinaleBosses = new Dictionary<uint, double>();

        /// <summary>Wave indices that resolved (placed or failed). Guarded by <see cref="sync"/>.</summary>
        private readonly HashSet<int> resolvedWaveIndices = new HashSet<int>();

        /// <summary>The Hold spawn ring radius (metres) the indoor re-pick samples on; 0 = none. Guarded by <see cref="sync"/>.</summary>
        private float holdRingRadius;

        // ---- the views H6 reads ---------------------------------------------------------------------------------------

        /// <summary>Every knight ever placed (adopted), in placement order. A copy.</summary>
        public IReadOnlyList<uint> KnightGuids
        {
            get { lock (sync) return knightGuids.ToArray(); }
        }

        /// <summary>The knights still standing, as an immutable array (safe to hand to any thread).</summary>
        public ImmutableArray<Creature> LiveKnights
        {
            get { lock (sync) return liveKnights.Where(IsStanding).ToImmutableArray(); }
        }

        /// <summary>The live Hold wave monsters (wave creatures carrying a Hold tag, the finale boss included), as an immutable array.</summary>
        public ImmutableArray<Creature> LiveWaveMonsters
        {
            get { lock (sync) return liveWave.Where(c => IsStanding(c) && c.WorldEventHold != null).ToImmutableArray(); }
        }

        /// <summary>How many distinct wave indices have resolved (placed or failed); a finale sent in two batches counts once.</summary>
        public int ResolvedWaveCount
        {
            get { lock (sync) return resolvedWaveIndices.Count; }
        }

        private static bool IsStanding(Creature c) => c != null && !c.IsDestroyed && !c.IsDead;

        /// <summary>
        /// True when the guid is a friendly this run placed for the players to defend: any knight ever adopted
        /// (WorldEventSpawnKind.Protected). Read by WorldEvent's throughput note, so a knight's death is never counted as
        /// health the group cleared (review finding F5).
        /// </summary>
        public bool IsFriendlyGuid(uint guid)
        {
            lock (sync) return knightGuids.Contains(guid);
        }

        // ---- test seams (pure bookkeeping; each takes sync like its production caller) ----------------------------------

        internal void AdoptHoldKnightForTest(Creature creature)
        {
            lock (sync) AdoptHoldKnight(creature);
        }

        internal void RemoveHoldKnightForTest(Creature creature)
        {
            lock (sync) RemoveHoldKnight(creature);
        }

        internal void ClearHoldKnightsForTest()
        {
            lock (sync) ClearHoldKnights();
        }

        /// <summary>The wave half of Adopt's bookkeeping that the Hold views read: liveWave plus the finale-boss note.</summary>
        internal void AdoptHoldWaveForTest(Creature creature)
        {
            lock (sync)
            {
                liveWave.Add(creature);
                NoteHoldFinaleBoss(creature);
            }
        }

        internal void NoteWaveResolvedForTest(int waveIndex) => NoteWaveResolved(waveIndex);

        /// <summary>
        /// World thread: hands the run its knights and wave monsters (WorldEventHoldRun.PublishKnights / PublishWaveMonsters)
        /// and notes each finale boss adopted since the last call (NoteFinaleBossSpawned, at its adopt time). H6 calls this
        /// on its cadence; nothing in H5 calls it on a timer.
        /// </summary>
        public void PublishHoldTo(WorldEventHoldRun run)
        {
            if (run == null)
                return;

            ImmutableArray<Creature> knights;
            ImmutableArray<Creature> monsters;
            List<KeyValuePair<uint, double>> bosses;

            lock (sync)
            {
                knights = liveKnights.Where(IsStanding).ToImmutableArray();
                monsters = liveWave.Where(c => IsStanding(c) && c.WorldEventHold != null).ToImmutableArray();
                bosses = pendingFinaleBosses.ToList();
                pendingFinaleBosses.Clear();
            }

            run.PublishKnights(knights);
            run.PublishWaveMonsters(monsters);

            foreach (var boss in bosses)
                run.NoteFinaleBossSpawned(boss.Key, boss.Value);

            // H6: the shrines that have landed (adopted as scene objects) and still stand, so the run's TryUseShrine knows them.
            List<WorldObject> shrines;

            lock (sync)
                shrines = holdShrines.Where(s => s != null && !s.IsDestroyed && decorGuids.Contains(s.Guid.Full)).ToList();

            // Re-registered whenever the SET changes (one shrine destroyed while another lands keeps the count but not the set).
            var guids = new HashSet<uint>(shrines.Select(s => s.Guid.Full));

            if (registeredShrineGuids == null || !registeredShrineGuids.SetEquals(guids))
            {
                registeredShrineGuids = guids;
                run.RegisterShrines(shrines);
            }
        }

        /// <summary>Hold restoration shrines constructed by this run's scene (landed or not). Guarded by <see cref="sync"/>.</summary>
        private readonly List<WorldObject> holdShrines = new List<WorldObject>();

        /// <summary>The shrine guids PublishHoldTo last registered with the run, null before the first (world thread only).</summary>
        private HashSet<uint> registeredShrineGuids;

        /// <summary>H6: true once at least one knight has been adopted (the knights' batch landed). Any thread.</summary>
        public bool HoldKnightsLanded
        {
            get { lock (sync) return knightGuids.Count > 0; }
        }

        /// <summary>H6: waves (wave index 0 and up) at least one monster of which was placed. Any thread.</summary>
        public int LandedWaveCount
        {
            get { lock (sync) return waveRecords.Count(kv => kv.Key >= 0); }
        }

        /// <summary>H6: waves (wave index 0 and up) every placed monster of which has died. Any thread.</summary>
        public int ClearedWaveCount
        {
            get { lock (sync) return waveRecords.Count(kv => kv.Key >= 0 && kv.Value.Cleared); }
        }

        /// <summary>H6: the scene objects (decor) still standing, a copy - the portals' parts flare from it. Any thread.</summary>
        public IReadOnlyList<WorldObject> LiveSceneObjects
        {
            get { lock (sync) return liveDecor.Where(d => d != null && !d.IsDestroyed).ToList(); }
        }

        /// <summary>Caller holds <see cref="sync"/> (Adopt).</summary>
        private void AdoptHoldKnight(Creature creature)
        {
            liveKnights.Add(creature);

            if (!knightGuids.Contains(creature.Guid.Full))
                knightGuids.Add(creature.Guid.Full);
        }

        /// <summary>Caller holds <see cref="sync"/> (OnDied).</summary>
        private void RemoveHoldKnight(Creature creature) => liveKnights.Remove(creature);

        /// <summary>Caller holds <see cref="sync"/> (DestroyAll).</summary>
        private void ClearHoldKnights() => liveKnights.Clear();

        /// <summary>Caller holds <see cref="sync"/> (Adopt): a finale boss's adopt time, for PublishHoldTo.</summary>
        private void NoteHoldFinaleBoss(Creature creature)
        {
            if (creature?.WorldEventHold?.Kind == HoldUnitKind.FinaleBoss)
                pendingFinaleBosses[creature.Guid.Full] = clock();
        }

        private void NoteWaveResolved(int waveIndex)
        {
            if (waveIndex < 0)
                return;

            lock (sync)
                resolvedWaveIndices.Add(waveIndex);
        }

        /// <summary>
        /// A point at a random bearing on the Hold spawn ring around <paramref name="centre"/> (the indoor re-pick), from this
        /// spawner's own seeded rng under <see cref="sync"/>. Null without a centre or a ring radius.
        /// </summary>
        private Position NextHoldRingSample(Position centre, Position basePosition)
        {
            float radius;
            double bearing;

            lock (sync)
            {
                radius = holdRingRadius;
                bearing = rng.NextDouble() * Math.PI * 2.0;
            }

            if (centre == null || !(radius > 0f))
                return null;

            return WorldEventGeometry.AtBearing(centre, radius, bearing);
        }

        // ---- the knights and their scene -------------------------------------------------------------------------------

        /// <summary>
        /// Places the goal's knights around <paramref name="centre"/> and their scene. Count and ring radius come from
        /// protect.countByParticipants / count / radius for the run's audience (knight_count overrides the count); one weenie
        /// per knight in turn (protect.wcids), each facing outward. Every knight carries a Kind Knight tag (set in TryPlace
        /// before EnterWorld), its health from the tuning (knight_max_health_by_wcid, then knight_max_health, then
        /// protect.health), is not attackable, and can show the shrine's heal effect. The scene - protect.decor, the per-knight
        /// targetDecor, a shrine beside the first shrine_count knights, and the portals (at the start-spot search's spots when
        /// <paramref name="areaPlan"/> has them, else the configured ring) - is placed Stuck after the knights.
        /// World thread.
        /// </summary>
        public void SpawnProtected(WorldEvent evt, ProtectDef protect, Position centre, WorldEventHoldRun run, WorldEventAreaPlan areaPlan = null)
        {
            if (evt == null || protect == null || protect.WcidFor(0) == 0 || centre == null)
            {
                log.Warn($"[WORLDEVENT] run={evt?.RunId} hold knights skipped: {(protect == null ? "no protect block" : centre == null ? "no anchor" : "no knight wcid")}");
                return;
            }

            var tuning = run?.Tuning ?? WorldEventHoldTuning.Defaults;
            var participants = Math.Max(0, evt.ResolvedAudienceCount);
            var (count, radius) = protect.ResolveLayout(participants);

            if (tuning.Spawner.KnightCount is int forced)
                count = Math.Clamp(forced, 1, ProtectDef.MaxProtectedTargets);

            var ring = WorldEventHoldRules.RingOffsets(count, radius);
            var tag = KnightTag(evt.RunId, run, protect.ShowHealthBar);

            var entries = new List<SpawnEntry>();
            var anchors = new List<Position>();

            for (var i = 0; i < ring.Count; i++)
            {
                entries.Add(new SpawnEntry(protect.WcidFor(i), overflowChampion: false, WorldEventWaveEntryKind.Trash, false, 0, NoSlotLevel, tag));
                anchors.Add(NpcPosition(centre, ring[i].Dx, ring[i].Dy, 0f, ring[i].YawDegrees));
            }

            var scene = HoldScene(protect, ring, tuning, areaPlan);

            log.Info($"[WORLDEVENT] run={evt.RunId} hold knights wcids={string.Join(",", Enumerable.Range(0, ring.Count).Select(protect.WcidFor))} " +
                     $"count={ring.Count} radius={radius:F1} scene={scene.Count} participants={participants}");

            // healthMult 1.0: a knight's health is absolute (PrepareKnight), never the wave multiplier.
            Spawn(evt, entries, anchors, WorldEventSpawnKind.Protected, NoWaveIndex, default, 1.0, null,
                (i, creature) =>
                {
                    PrepareKnight(evt, creature, protect, tuning, participants);
                    return null;
                });

            // Queued on the same landblock queue AFTER the knights, so a prop can never take a knight's spot first.
            if (scene.Count > 0)
                SpawnDecor(evt, scene, centre, holdScene: true);
        }

        /// <summary>A knight's tag (pure): Kind Knight, no tuning role, the run's id and view.</summary>
        public static WorldEventHoldTag KnightTag(uint runId, IWorldEventHoldRunView run, bool showHealthBar)
        {
            return new WorldEventHoldTag
            {
                RunId = runId,
                Run = run,
                Kind = HoldUnitKind.Knight,
                WaveRole = HoldRole.Seeker,
                Role = HoldDamageRole.None,
                ShowHealthBar = showHealthBar
            };
        }

        /// <summary>
        /// A knight's max health (pure): knight_max_health_by_wcid for its wcid, else knight_max_health when above 0, else
        /// protect.health for the audience. At least 1.
        /// </summary>
        public static int KnightHealth(uint wcid, WorldEventHoldTuning tuning, ProtectDef protect, int participants)
        {
            if (tuning?.Spawner?.KnightHealthByWcid != null && tuning.Spawner.KnightHealthByWcid.TryGetValue(wcid, out var byWcid) && byWcid > 0)
                return byWcid;

            if (tuning != null && tuning.KnightMaxHealth > 0)
                return (int)Math.Min(tuning.KnightMaxHealth, int.MaxValue);

            return Math.Max(1, protect?.EffectiveMaxHealth(participants) ?? 1);
        }

        /// <summary>A knight's pre-spawn writes (construction, world thread): health, the friendly appearance, the shrine effect table.</summary>
        private void PrepareKnight(WorldEvent evt, Creature creature, ProtectDef protect, WorldEventHoldTuning tuning, int participants)
        {
            var hp = KnightHealth(creature.WeenieClassId, tuning, protect, participants);

            if (ApplyHealth(creature, hp))
                log.Warn($"[WORLDEVENT] run={evt.RunId} knight wcid={creature.WeenieClassId} health target={hp} could not be reached; MaxValue={creature.Health.MaxValue}");

            ApplyProtectedAppearance(creature);

            if (protect.Shrine != null)
                WorldEventEffectTables.EnsureCanPlay(creature, protect.Shrine.UsedEffect, WorldEventEffectTables.HumanTable);

            log.Info($"[WORLDEVENT] run={evt.RunId} knight wcid={creature.WeenieClassId} {creature.Name} health={creature.Health.MaxValue}");
        }

        /// <summary>
        /// A knight is sent as a friendly NPC: not attackable, the shopkeepers' PK status. Its own run's monsters still reach it
        /// through the Hold target route (H4), which never reads Attackable. Pure property writes before EnterWorld.
        /// </summary>
        public static void ApplyProtectedAppearance(Creature creature)
        {
            if (creature == null)
                return;

            creature.Attackable = false;
            creature.PlayerKillerStatus = ProtectedPlayerKillerStatus;

            // Monster.SetMonsterState caches IsMonster from Attackable at construction; refresh it.
            creature.SetMonsterState();
        }

        /// <summary>
        /// The knights' scene (pure): protect.decor, protect.targetDecor once per knight, a shrine beside each of the first
        /// shrine_count knights (all when not set), and the portals' parts at the start-spot search's spots
        /// (<paramref name="areaPlan"/>) or else on the configured ring (portal_count / portal_radius override protect.portals).
        /// </summary>
        public static List<DecorDef> HoldScene(ProtectDef protect, IReadOnlyList<WorldEventHoldRules.RingOffset> ring,
            WorldEventHoldTuning tuning, WorldEventAreaPlan areaPlan)
        {
            var scene = new List<DecorDef>();

            if (protect == null)
                return scene;

            tuning ??= WorldEventHoldTuning.Defaults;
            ring ??= new List<WorldEventHoldRules.RingOffset>();

            if (protect.Decor != null)
                scene.AddRange(protect.Decor.Where(d => d != null));

            scene.AddRange(WorldEventHoldRules.PlaceTargetDecor(ring, protect.TargetDecor));

            if (protect.Shrine != null && protect.Shrine.Wcid != 0)
            {
                var shrineRing = tuning.Spawner.ShrineCount is int shrines ? ring.Take(Math.Max(0, shrines)).ToList() : ring.ToList();

                scene.AddRange(WorldEventHoldRules.PlaceTargetDecor(shrineRing, new List<DecorDef> { WorldEventHoldRules.ShrineDecor(protect.Shrine) }));
            }

            var portals = protect.Portals;

            if (portals != null && portals.Parts != null && portals.Parts.Count > 0)
            {
                var portalRing = areaPlan != null && areaPlan.Ok && areaPlan.Portals != null && areaPlan.Portals.Count > 0
                    ? WorldEventSpawnArea.SpotOffsets(areaPlan.Portals)
                    : WorldEventHoldRules.PortalOffsets(tuning.Spawner.PortalCount ?? portals.Count, tuning.Spawner.PortalRadius ?? portals.Radius, portals.RotationDegrees);

                scene.AddRange(WorldEventHoldRules.PlaceTargetDecor(portalRing, WorldEventHoldRules.ScalePortalParts(portals.Parts, portals.Scale)));
            }

            return scene;
        }

        /// <summary>What the spawner does with one Hold scene object (see <see cref="DecorVerdict"/>).</summary>
        public enum DecorUse
        {
            /// <summary>Placed, never usable.</summary>
            Inert,

            /// <summary>Placed and usable: the Hold shrine, whose use the run handles.</summary>
            HoldShrine,

            /// <summary>Not placed: its weenie type carries its own use behaviour.</summary>
            Refuse
        }

        /// <summary>
        /// The scene rule (pure): a shrine must be a plain Generic (its only use is the Hold heal); any other prop must be a
        /// Generic or a light source (a campfire) and is made unusable. Every other weenie type (a PK altar, a portal, a
        /// lifestone, a switch, a door, a chest, a creature...) keeps its own use behaviour and is refused.
        /// </summary>
        public static DecorUse DecorVerdict(WeenieType type, bool isShrine)
        {
            if (isShrine)
                return type == WeenieType.Generic ? DecorUse.HoldShrine : DecorUse.Refuse;

            return type == WeenieType.Generic || type == WeenieType.LightSource ? DecorUse.Inert : DecorUse.Refuse;
        }

        /// <summary>
        /// A Hold restoration shrine, before it enters the world: named, usable from shrine_use_radius (else
        /// protect.shrine.useRadius) metres, within 1 to 10 (Usable.Remote), its own appraisal words, and able to show its
        /// ready effect. What the use does is the run's (H6).
        /// </summary>
        public static void ConfigureHoldShrine(WorldObject wo, ShrineDef shrine, double? useRadius = null)
        {
            if (wo == null || shrine == null)
                return;

            if (!string.IsNullOrWhiteSpace(shrine.Name))
                wo.Name = shrine.Name;

            wo.ItemUseable = Usable.Remote;
            wo.UseRadius = (float)Math.Clamp(useRadius ?? shrine.UseRadius, 1, 10);

            wo.RemoveProperty(PropertyString.ShortDesc);
            wo.RemoveProperty(PropertyString.ActivationTalk);
            wo.RemoveProperty(PropertyString.ActivationFailure);
            wo.RemoveProperty(PropertyString.UseMessage);
            wo.RemoveProperty(PropertyString.UsePkServerError);
            wo.SetProperty(PropertyString.LongDesc, "Asheron's blessing rests in this shrine. Use it to restore the defender who stands beside it.");
            wo.SetProperty(PropertyString.Use, "Double-click to restore the defender beside this shrine.");

            WorldEventEffectTables.EnsureCanPlay(wo, shrine.ReadyEffect, WorldEventEffectTables.CreatureTable);
        }

        /// <summary>SpawnDecor's Hold scene check for one constructed object. False = refused and destroyed.</summary>
        private bool PrepareHoldSceneObject(WorldEvent evt, WorldObject wo, DecorDef entry)
        {
            var verdict = DecorVerdict(wo.WeenieType, entry.HoldShrine);

            if (verdict == DecorUse.Refuse)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} hold scene wcid {entry.Wcid} ({wo.Name}) is a {wo.WeenieType}, which keeps its own " +
                          $"use behaviour; refused{(entry.HoldShrine ? " as a shrine (it must be a Generic)" : "")}");
                wo.Destroy();
                return false;
            }

            if (verdict == DecorUse.Inert)
                wo.ItemUseable = Usable.No;
            else
            {
                var runTuning = (evt.Objective as Objectives.HoldObjective)?.Run?.Tuning;
                ConfigureHoldShrine(wo, evt.Composition?.Goal?.Protect?.Shrine, runTuning?.Spawner.ShrineUseRadius);

                // H6: PublishHoldTo registers it with the run (WorldEventHoldRun.RegisterShrines) once it has landed.
                lock (sync)
                    holdShrines.Add(wo);
            }

            return true;
        }

        // ---- the waves -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Places one batch of a Hold wave planned by <paramref name="plan"/> (WorldEventHoldWavePlanner.BuildPlan). The batch is
        /// the pick's trash (ranged slots and level-offset slots as the plan redrew them; a runners wave cut to its keep count)
        /// then its champions; a finale's boss batch is the champion alone. Each constructed creature gets its tag
        /// (WorldEventHoldWavePlanner.BuildTag, after its ranged check) and its role's pre-spawn writes; TryPlace sets the tag
        /// before EnterWorld. <paramref name="ringRadius"/> is the spawn ring the indoor re-pick samples on. World thread.
        /// </summary>
        public void SpawnHoldWave(WorldEvent evt, WavePick pick, IReadOnlyList<Position> anchors, int waveIndex, LevelBand band,
            double healthMult, HoldWavePlan plan, WorldEventHoldRun run, float ringRadius = 0f)
        {
            if (evt == null || plan == null)
                return;

            var tuning = run?.Tuning ?? WorldEventHoldTuning.Defaults;
            var entries = HoldWaveEntries(pick, plan);

            if (entries.Count == 0)
                return;

            lock (sync)
                holdRingRadius = ringRadius > 0f ? ringRadius : (float)tuning.Spawner.SpawnRadius;

            var runId = evt.RunId;
            var count = entries.Count;
            var tags = new List<WorldEventHoldTag>();

            // world_events_boss_scale_multiplier, read once per batch (not per creature) and only for a finale boss batch:
            // the finale boss's size falls back to it when no height can be read (WorldEventHoldFinaleBoss.DecideScale).
            var bossMultiplier = plan.FinaleBossOnly
                ? ResolveBossScaleMultiplier(PropertyManager.GetDouble("world_events_boss_scale_multiplier").Item)
                : 1f;

            Spawn(evt, entries, anchors, WorldEventSpawnKind.Wave, waveIndex, band, healthMult, null,
                (i, creature) =>
                {
                    WorldEventHoldWavePlanner.EntryAttack? attack = null;

                    if (IsRangedSlot(plan, i))
                        attack = new WorldEventHoldWavePlanner.EntryAttack(WorldEventRangedMembers.CreatureHasRealRangedAttack(creature),
                            WorldEventRangedMembers.CreatureIsCaster(creature));

                    var tag = WorldEventHoldWavePlanner.BuildTag(plan, count, i, runId, run, tuning, attack);

                    if (attack != null && !attack.Value.Ranged)
                        log.Info($"[WORLDEVENT] run={runId} wcid {creature.WeenieClassId} {creature.Name} has no real ranged attack; seeks instead of ranged");

                    ApplyHoldMonster(evt, creature, tag, plan, tuning, bossMultiplier);
                    tags.Add(tag);

                    if (tag.Corrupted && plan.AssaultCalls != null && plan.CorruptedTargets != null && plan.CorruptedTargets.ContainsKey(i))
                    {
                        var k = plan.CorruptedTargets.Keys.OrderBy(x => x).ToList().IndexOf(i);

                        if (k >= 0 && k < plan.AssaultCalls.Count)
                            plan.AssaultCalls[k] = (creature.Name, plan.AssaultCalls[k].Target);
                    }

                    return tag;
                });

            log.Info($"[WORLDEVENT] run={runId} {WorldEventHoldRules.WaveRoleSummary(plan.WaveNumber, tags)} plan={plan}");
        }

        /// <summary>
        /// The spawn entries of one Hold wave batch (pure): see <see cref="SpawnHoldWave"/>. A finale boss batch is the champion
        /// alone (entry 0); a level-offset slot keeps its moved level (no uplift back to the band, slot level moved with it).
        /// </summary>
        private static List<SpawnEntry> HoldWaveEntries(WavePick pick, HoldWavePlan plan)
        {
            var entries = new List<SpawnEntry>();

            if (plan.FinaleBossOnly)
            {
                if (plan.ChampionWcid != 0)
                    entries.Add(new SpawnEntry(plan.ChampionWcid, overflowChampion: false, WorldEventWaveEntryKind.Champion,
                        plan.ChampionSynthetic, plan.ChampionUplift, plan.ChampionSlotLevel));

                plan.ChampionEntryIndex = entries.Count > 0 ? 0 : -1;
                return entries;
            }

            if (pick == null)
                return entries;

            var trash = plan.ReplacedTrash != null && plan.ReplacedTrash.Count == pick.Trash.Count ? plan.ReplacedTrash : pick.Trash;

            for (var i = 0; i < trash.Count; i++)
            {
                var isElite = i == pick.EliteIndex;
                var offset = plan.LevelOffsetSlots != null && plan.LevelOffsetSlots.TryGetValue(i, out var off) ? off : 0;

                entries.Add(new SpawnEntry(trash[i], overflowChampion: false,
                    isElite ? WorldEventWaveEntryKind.Elite : WorldEventWaveEntryKind.Trash,
                    isElite && pick.EliteSynthetic, offset != 0 ? 0 : pick.TrashUpliftAt(i),
                    WorldEventHoldRules.MovedSlotLevel(SlotLevelFor(pick, pick.TrashSlotLevelAt(i)), offset, NoSlotLevel)));
            }

            if (!plan.FinaleEscort)
            {
                for (var i = 0; i < pick.Champions.Count; i++)
                {
                    var synthetic = i < pick.ChampionsSynthetic.Count && pick.ChampionsSynthetic[i];

                    entries.Add(new SpawnEntry(pick.Champions[i], overflowChampion: false, WorldEventWaveEntryKind.Champion,
                        synthetic, pick.ChampionUpliftAt(i), SlotLevelFor(pick, pick.ChampionSlotLevelAt(i))));
                }
            }

            if (plan.KeepCount is int keep && keep < entries.Count)
                entries = entries.GetRange(0, Math.Max(1, keep));

            return entries;
        }

        private static bool IsRangedSlot(HoldWavePlan plan, int i)
        {
            if (plan.Type == HoldWaveType.Ranged)
                return true;

            return plan.Type == HoldWaveType.Mixed && plan.Roles != null && i < plan.Roles.Length && plan.Roles[i] == HoldRole.Ranged;
        }

        /// <summary>
        /// A Hold wave monster's pre-spawn writes (construction, world thread): its role's health (world_events_hold_&lt;role&gt;_health_mult,
        /// which carries the runner and corrupted multipliers), a corrupted monster's title, size and effect tables, a warded
        /// monster's ward effects, and the finale boss's name, size and health. Never throws.
        /// </summary>
        private void ApplyHoldMonster(WorldEvent evt, Creature creature, WorldEventHoldTag tag, HoldWavePlan plan, WorldEventHoldTuning tuning,
            float bossMultiplier = 1f)
        {
            try
            {
                var protect = evt.Composition?.Goal?.Protect;

                if (tag.Kind == HoldUnitKind.FinaleBoss)
                {
                    ApplyFinaleBoss(evt, creature, plan.ChampionName, tuning, protect, bossMultiplier);
                    return;
                }

                var mult = tuning.Role(tag.Role).HealthMult;

                if (mult is double m && double.IsFinite(m) && m > 0 && Math.Abs(m - 1.0) > 1e-9)
                    ApplyHealth(creature, Math.Max(1, (int)Math.Round(creature.Health.MaxValue * Math.Clamp(m, 0.05, 10))));

                if (tag.Corrupted)
                    ApplyCorrupted(evt, creature, protect?.EffectiveCorrupted ?? new CorruptedDef());

                if (tag.Warded && protect?.Ward != null)
                {
                    WorldEventEffectTables.EnsureCanPlay(creature, protect.Ward.RestoreEffect, WorldEventEffectTables.CreatureTable);
                    WorldEventEffectTables.EnsureCanPlay(creature, protect.Ward.BreakEffect, WorldEventEffectTables.CreatureTable);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} hold role writes for wcid {creature.WeenieClassId} threw", ex);
            }
        }

        /// <summary>
        /// A corrupted monster, before EnterWorld: titled (protect.corrupted.prefix), its size multiplied (1 to 2), and able to
        /// show its aura effects. Its health comes from the corrupted role's health_mult (ApplyHoldMonster); its tint rides on the
        /// tag (TintPalette). Logged.
        /// </summary>
        private void ApplyCorrupted(WorldEvent evt, Creature creature, CorruptedDef c)
        {
            creature.Name = WorldEventHoldRules.Titled(c.Prefix, creature.Name);

            var scale = (float)Math.Clamp(double.IsFinite(c.Scale) ? c.Scale : 1.0, 1.0, 2.0);

            if (scale != 1.0f)
                creature.ApplyRuntimeScaleMultiplier(scale);

            WorldEventEffectTables.EnsureCanPlay(creature, c.Effect, WorldEventEffectTables.CreatureTable);
            WorldEventEffectTables.EnsureCanPlay(creature, c.ExtraEffect, WorldEventEffectTables.CreatureTable);

            log.Info($"[WORLDEVENT] run={evt.RunId} corrupted wcid={creature.WeenieClassId} {creature.Name} scale=x{scale:F2} health={creature.Health.MaxValue}");
        }

        /// <summary>
        /// The finale boss, before EnterWorld: its finale name, its size (WorldEventHoldFinaleBoss.DecideScale: finale_scale
        /// within 1 to 2.5; 0 or less matches the named bosses' height, else the World Events boss multiplier), and its health
        /// (finale_health as a flat max, else times finale_health_mult within 1 to 10), and able to show the boss effects.
        /// <paramref name="bossMultiplier"/> is world_events_boss_scale_multiplier, read once per batch by the caller. Logged.
        /// </summary>
        private void ApplyFinaleBoss(WorldEvent evt, Creature creature, string bossName, WorldEventHoldTuning tuning, ProtectDef protect,
            float bossMultiplier)
        {
            var spawner = tuning.Spawner;

            var measure = !(spawner.FinaleScale > 0);
            var named = measure ? WorldEventHoldFinaleBoss.NamedBossHeight(bossMultiplier) : null;
            var own = measure ? WorldEventHoldFinaleBoss.SetupHeight(creature.SetupTableId) : null;

            var scale = WorldEventHoldFinaleBoss.DecideScale(spawner.FinaleScale, named, own, creature.ObjScale ?? 1.0f, bossMultiplier,
                spawner.FinaleHeightScaleMin, spawner.FinaleHeightScaleMax, out var how);

            log.Info($"[WORLDEVENT] run={evt.RunId} hold finale boss size x{scale:F2}: {how}");

            if (!string.IsNullOrWhiteSpace(bossName))
                creature.Name = bossName;

            if (scale != 1.0f)
                creature.ApplyRuntimeScaleMultiplier(scale);

            if (spawner.FinaleFlatHealth is int flat)
                ApplyHealth(creature, flat);
            else
            {
                var mult = Math.Clamp(double.IsFinite(spawner.FinaleHealthMult) ? spawner.FinaleHealthMult : 1.0, 1.0, 10.0);

                if (mult > 1.0)
                    ApplyHealth(creature, (int)Math.Min(int.MaxValue, Math.Round(creature.Health.MaxValue * mult)));
            }

            if (protect != null)
            {
                WorldEventEffectTables.EnsureCanPlay(creature, protect.BossEffect, WorldEventEffectTables.CreatureTable);
                WorldEventEffectTables.EnsureCanPlay(creature, protect.BossArrivalEffect, WorldEventEffectTables.CreatureTable);
            }

            log.Info($"[WORLDEVENT] run={evt.RunId} hold finale boss wcid={creature.WeenieClassId} {creature.Name} scale=x{scale:F2} health={creature.Health.MaxValue}");
        }

        /// <summary>
        /// The finale's adds (finale_add_count of them, ordinary members of the boss's band, never the boss's own monster):
        /// placed as Wave kind under wave index -1 so they never count as a wave, each tagged as a plain seeker of the run.
        /// The caller (H6) times them every finale_add_interval_seconds. World thread.
        /// </summary>
        public void SpawnHoldFinaleAdds(WorldEvent evt, IReadOnlyList<FamilyMember> members, LevelBand? band, uint bossWcid,
            IReadOnlyList<Position> anchors, double healthMult, WorldEventHoldRun run, Random rng = null)
        {
            var tuning = run?.Tuning ?? WorldEventHoldTuning.Defaults;
            var adds = WorldEventHoldRules.PickAdds(members, band, bossWcid, tuning.Spawner.FinaleAddCount, rng ?? new Random());

            if (evt == null || adds.Count == 0)
                return;

            var plan = new HoldWavePlan { Type = HoldWaveType.Normal, SeekerIndices = new HashSet<int>(Enumerable.Range(0, adds.Count)) };
            var entries = adds.Select(w => new SpawnEntry(w, overflowChampion: false, WorldEventWaveEntryKind.Trash, false, 0, NoSlotLevel)).ToList();
            var runId = evt.RunId;

            Spawn(evt, entries, anchors, WorldEventSpawnKind.Wave, NoWaveIndex, band ?? default, healthMult, null,
                (i, creature) =>
                {
                    var tag = WorldEventHoldWavePlanner.BuildTag(plan, entries.Count, i, runId, run, tuning, null);
                    ApplyHoldMonster(evt, creature, tag, plan, tuning);
                    return tag;
                });
        }
    }
}
