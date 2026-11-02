using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>One standing knight as the wave planner sees it: its guid, name and offset from the anchor (metres east/north).</summary>
    public readonly struct HoldKnightSlot
    {
        public readonly uint Guid;
        public readonly string Name;
        public readonly float Dx;
        public readonly float Dy;

        public HoldKnightSlot(uint guid, string name, float dx, float dy)
        {
            Guid = guid;
            Name = name;
            Dx = dx;
            Dy = dy;
        }
    }

    /// <summary>What one wave of a Hold run is planned from (all of it read on the world thread before the call).</summary>
    public sealed class HoldWaveRequest
    {
        /// <summary>The 1-based wave number.</summary>
        public int WaveNumber { get; init; }

        /// <summary>How many counted waves the run has (the last one is the finale when the goal defines one).</summary>
        public int TargetWaves { get; init; }

        /// <summary>The roster pick for this wave (trash, then champions, in spawn order).</summary>
        public WavePick Pick { get; init; } = WavePick.Empty;

        /// <summary>The knights still standing, in placement order.</summary>
        public IReadOnlyList<HoldKnightSlot> Knights { get; init; } = Array.Empty<HoldKnightSlot>();

        /// <summary>An admin-forced wave type, or null.</summary>
        public HoldWaveType? Forced { get; init; }

        /// <summary>The family's ranged members in the wave's band (WorldEventRangedMembers.Pool); empty for none.</summary>
        public IReadOnlyList<uint> RangedPool { get; init; } = Array.Empty<uint>();

        /// <summary>wcid to "is a caster" (WorldEventRangedMembers.IsCasterWcid); null = nobody is.</summary>
        public Func<uint, bool> IsCaster { get; init; }

        /// <summary>The roster's members, for the role level offsets and the finale adds; empty for none.</summary>
        public IReadOnlyList<FamilyMember> Members { get; init; } = Array.Empty<FamilyMember>();

        /// <summary>Finale: the champion the caller picked (RosterSelector.PickChampion, else PickFinaleBossFallback).</summary>
        public ChampionPick Champion { get; init; } = ChampionPick.None;

        /// <summary>Finale: the champion's own name (the weenie name), before the finale title.</summary>
        public string ChampionName { get; init; }
    }

    /// <summary>
    /// World Events Hold H5: decides what each wave of one Hold run does (<see cref="HoldWavePlan"/>) and turns a plan into one
    /// <see cref="WorldEventHoldTag"/> per spawn entry (<see cref="BuildTags"/>). Pure apart from the run's own random source:
    /// no clock, no world, no property reads, so every decision is testable.
    ///
    /// THREADING: one planner per run, used only from the world thread (the run cadence that will call it lands in H6). Its
    /// state (the previous wave type, the specials so far, the seeker carry) is not shared.
    /// </summary>
    public sealed class WorldEventHoldWavePlanner
    {
        private readonly GoalDef goal;
        private readonly WorldEventHoldTuning tuning;
        private readonly Random rng;

        private double seekerCarry;

        public WorldEventHoldWavePlanner(GoalDef goal, WorldEventHoldTuning tuning, Random rng = null)
        {
            this.goal = goal;
            this.tuning = tuning ?? WorldEventHoldTuning.Defaults;
            this.rng = rng ?? new Random();
        }

        /// <summary>The type of the last planned wave.</summary>
        public HoldWaveType Previous { get; private set; } = HoldWaveType.Normal;

        /// <summary>How many one-type special waves (not the finale, not a mixed wave) were planned so far.</summary>
        public int SpecialsSoFar { get; private set; }

        public WorldEventHoldTuning Tuning => tuning;

        /// <summary>Plans one wave. Never null; an empty pick gives an empty Normal plan.</summary>
        public HoldWavePlan BuildPlan(HoldWaveRequest request)
        {
            request ??= new HoldWaveRequest();

            var protect = goal?.Protect;
            var pick = request.Pick ?? WavePick.Empty;
            var size = pick.Total;
            var spawner = tuning.Spawner;

            var type = WorldEventHoldRules.PickWaveType(request.WaveNumber, request.TargetWaves, Previous,
                protect?.SpecialWaveChance ?? 0, protect?.SpecialWaveFromWave ?? 2, protect?.SpecialWaves, rng, request.Forced,
                SpecialsSoFar, protect?.MinSpecialWaves ?? 0);

            if (spawner.HasWaveMix && type != HoldWaveType.Finale)
                type = HoldWaveType.Mixed;

            var plan = new HoldWavePlan { Type = type, WaveNumber = request.WaveNumber };
            var keep = KeepIndices(pick);
            var knights = request.Knights ?? Array.Empty<HoldKnightSlot>();
            var def = protect?.SpecialWave(type);

            switch (type)
            {
                case HoldWaveType.Mixed:
                    PlanMixed(plan, request, pick, size, keep, knights);
                    break;

                case HoldWaveType.Breach:
                    PickBreachTarget(plan, knights);
                    plan.SeekerIndices = new HashSet<int>(Enumerable.Range(0, size));
                    break;

                case HoldWaveType.Runners:
                    // The tuning already resolves setting over goal (runner_* rows fall back to this def's values), so the
                    // def supplies only what has no setting: the share kept.
                    plan.KeepCount = WorldEventHoldRules.RunnersKeep(size, def?.CountMult ?? 0.6);
                    plan.HealthMult = Math.Clamp(tuning.RunnerHealthMult, 0.05, 1);
                    plan.RunRateMult = (float)Math.Clamp(tuning.RunnerSpeedMult, 1, 3);
                    plan.AttackSpeedMult = (float)Math.Clamp(tuning.RunnerAttackSpeedMult, 1, 3);
                    plan.SeekerIndices = new HashSet<int>(Enumerable.Range(0, size));
                    break;

                case HoldWaveType.Ranged:
                    if (request.RangedPool != null && request.RangedPool.Count > 0)
                        plan.ReplacedTrash = WorldEventHoldRules.ReplaceWithRanged(pick.Trash, request.RangedPool, rng);
                    // ranged_keep_distance, which falls back to this def's holdDistance.
                    plan.HoldDistance = (float)Math.Clamp(tuning.RangedKeepDistance, 5, 40);
                    plan.SeekerIndices = Seekers(size);
                    break;

                case HoldWaveType.Hunters:
                    plan.Hunters = Math.Max(0, def?.Hunters ?? 2);
                    plan.SeekerIndices = Seekers(size);
                    break;

                case HoldWaveType.Finale:
                    plan.ChampionWcid = request.Champion.Wcid;
                    plan.ChampionSynthetic = request.Champion.Synthetic;
                    plan.ChampionUplift = request.Champion.UpliftLevel;
                    plan.ChampionSlotLevel = request.Champion.SlotLevel;
                    plan.ChampionName = WorldEventHoldRules.FinaleBossName(protect?.FinaleBossPrefix, request.ChampionName);
                    plan.FinaleBossOnly = plan.ChampionWcid != 0;
                    plan.ChampionEntryIndex = plan.ChampionWcid != 0 ? 0 : -1;
                    plan.SeekerIndices = Seekers(size);
                    break;

                default:
                    plan.SeekerIndices = Seekers(size);
                    break;
            }

            if (!plan.FinaleBossOnly)
                ApplyLevelOffsets(plan, request, pick, keep);

            if (type != HoldWaveType.Normal && type != HoldWaveType.Finale && type != HoldWaveType.Mixed)
                SpecialsSoFar++;

            Previous = type;

            return plan;
        }

        /// <summary>The finale's rest-of-wave plan, after the boss (<see cref="HoldWavePlan.EscortFor"/>), with this run's seekers.</summary>
        public HoldWavePlan BuildEscortPlan(HoldWavePlan bossPlan, WavePick pick)
        {
            var escort = HoldWavePlan.EscortFor(bossPlan, pick, rng);
            escort.SeekerIndices = Seekers(escort.ReplacedTrash?.Count ?? 0);
            return escort;
        }

        private HashSet<int> Seekers(int size)
        {
            var count = WorldEventHoldRules.SeekerCount(size, tuning.Spawner.SeekShare, ref seekerCarry);
            return WorldEventHoldRules.PickIndices(size, count, rng);
        }

        /// <summary>The slots the mix never re-rolls: the elite and every champion (champions follow the trash in a batch).</summary>
        public static HashSet<int> KeepIndices(WavePick pick)
        {
            var keep = new HashSet<int>();

            if (pick == null)
                return keep;

            if (pick.EliteIndex >= 0 && pick.EliteIndex < pick.Trash.Count)
                keep.Add(pick.EliteIndex);

            for (var c = 0; c < pick.Champions.Count; c++)
                keep.Add(pick.Trash.Count + c);

            return keep;
        }

        private void PickBreachTarget(HoldWavePlan plan, IReadOnlyList<HoldKnightSlot> knights)
        {
            if (knights.Count == 0)
                return;

            var target = knights[rng.Next(knights.Count)];

            plan.BreachTargetGuid = target.Guid;
            plan.BreachTargetName = target.Name;
            plan.BreachSide = WorldEventHoldRules.CompassSide(target.Dx, target.Dy);
        }

        private void PlanMixed(HoldWavePlan plan, HoldWaveRequest request, WavePick pick, int size, HashSet<int> keep, IReadOnlyList<HoldKnightSlot> knights)
        {
            var spawner = tuning.Spawner;
            var assault = spawner.AssaultMode && WorldEventHoldRules.IsAssaultWave(request.WaveNumber, spawner.AssaultEveryWaves, spawner.AssaultFromWave, false);

            HoldRole[] roles;

            if (assault && knights.Count > 0)
            {
                roles = WorldEventHoldRules.AssignAssaultWave(size, spawner.AssaultCount, spawner.Shares, spawner.Minimums, spawner.Maximums, keep, rng);
            }
            else
            {
                var mix = WorldEventHoldRules.AssaultMix(spawner.Shares, spawner.Minimums, spawner.Maximums, spawner.AssaultMode);
                roles = WorldEventHoldRules.AssignWaveMix(size, mix.Shares, mix.Minimums, mix.Maximums, keep, rng);
                assault = false;
            }

            if (request.RangedPool == null || request.RangedPool.Count == 0)
                WorldEventHoldRules.ReassignRole(roles, HoldRole.Ranged, HoldRole.Seeker);

            if (knights.Count == 0)
            {
                WorldEventHoldRules.ReassignRole(roles, HoldRole.Breach, HoldRole.Seeker);
                WorldEventHoldRules.ReassignRole(roles, HoldRole.Corrupted, HoldRole.Seeker);
            }

            plan.Roles = roles;
            plan.Assault = assault;
            plan.Warded = tuning.WardEnabled;

            plan.RunRateMult = (float)Math.Clamp(tuning.RunnerSpeedMult, 1, 3);
            plan.AttackSpeedMult = (float)Math.Clamp(tuning.RunnerAttackSpeedMult, 1, 3);
            plan.HealthMult = Math.Clamp(tuning.RunnerHealthMult, 0.05, 1);
            plan.HoldDistance = (float)Math.Clamp(tuning.RangedKeepDistance, 0, 40);

            if (roles.Contains(HoldRole.Breach))
                PickBreachTarget(plan, knights);

            plan.ReplacedTrash = WorldEventHoldRules.ReplaceRangedSlots(pick.Trash, roles.Take(pick.Trash.Count).ToList(), request.RangedPool, rng,
                request.IsCaster, spawner.CasterShareOfRanged);

            if (assault)
            {
                var corrupted = Enumerable.Range(0, roles.Length).Where(i => roles[i] == HoldRole.Corrupted).ToList();
                var targets = WorldEventHoldRules.AssaultTargets(knights.Count, corrupted.Count, rng);

                plan.CorruptedTargets = new Dictionary<int, uint>();
                plan.AssaultCalls = new List<(string Monster, string Target)>();

                for (var k = 0; k < corrupted.Count && k < targets.Count; k++)
                {
                    var knight = knights[targets[k]];
                    plan.CorruptedTargets[corrupted[k]] = knight.Guid;
                    plan.AssaultCalls.Add((null, knight.Name));
                }
            }
        }

        /// <summary>
        /// Role level offsets (world_events_hold_&lt;role&gt;_level_offset): each non-kept trash slot whose role has an offset is
        /// redrawn from the members inside the wave's band moved by that offset - ranged slots from the ranged pool, every other
        /// slot from the ordinary (role 0) members. A slot whose pool is empty keeps its monster.
        /// </summary>
        private void ApplyLevelOffsets(HoldWavePlan plan, HoldWaveRequest request, WavePick pick, HashSet<int> keep)
        {
            var trash = plan.ReplacedTrash ?? pick.Trash;
            var members = request.Members ?? Array.Empty<FamilyMember>();

            if (trash == null || trash.Count == 0 || members.Count == 0)
                return;

            var ranged = new HashSet<uint>(request.RangedPool ?? Array.Empty<uint>());

            HoldRole RoleAt(int i)
            {
                if (plan.Roles != null && i < plan.Roles.Length)
                    return plan.Roles[i];

                switch (plan.Type)
                {
                    case HoldWaveType.Breach: return HoldRole.Breach;
                    case HoldWaveType.Runners: return HoldRole.Runner;
                    case HoldWaveType.Ranged: return HoldRole.Ranged;
                    default: return HoldRole.Seeker;
                }
            }

            int OffsetAt(int i)
            {
                var role = RoleAt(i);
                var caster = role == HoldRole.Ranged && request.IsCaster != null && request.IsCaster(trash[i]);

                return tuning.Role(WorldEventHoldRules.DamageRoleFor(role, caster)).LevelOffset;
            }

            IReadOnlyList<uint> PoolAt(int i)
            {
                var band = WorldEventHoldRules.ShiftBand(pick.Band, OffsetAt(i));
                var wantRanged = RoleAt(i) == HoldRole.Ranged && ranged.Count > 0;

                return members.Where(m => m != null && m.Wcid != 0 && band.Contains(m.Level)
                        && (wantRanged ? ranged.Contains(m.Wcid) : m.Role == 0 && !ranged.Contains(m.Wcid)))
                    .Select(m => m.Wcid).Distinct().ToList();
            }

            var redrawn = WorldEventHoldRules.RedrawForLevelOffsets(trash, keep, OffsetAt, PoolAt, rng, out var moved);

            if (moved.Count == 0)
                return;

            plan.ReplacedTrash = redrawn;
            plan.LevelOffsetSlots = moved;
        }

        /// <summary>
        /// The ranged check the spawner makes on a constructed wave monster: whether it really attacks from range and whether
        /// that attack is spells. Null = not checked (the plan's own role stands).
        /// </summary>
        public readonly struct EntryAttack
        {
            public readonly bool Ranged;
            public readonly bool Caster;

            public EntryAttack(bool ranged, bool caster)
            {
                Ranged = ranged;
                Caster = caster;
            }
        }

        /// <summary>
        /// One tag per spawn entry of a batch planned by <paramref name="plan"/> (the pure step; the spawner sets each tag on its
        /// creature before EnterWorld). Every tag carries <paramref name="runId"/> and <paramref name="run"/>. A ranged entry
        /// whose constructed creature has no real ranged attack (<paramref name="attackAt"/>) becomes a seeker instead.
        /// </summary>
        public static WorldEventHoldTag[] BuildTags(HoldWavePlan plan, int entryCount, uint runId, IWorldEventHoldRunView run,
            WorldEventHoldTuning tuning, Func<int, EntryAttack?> attackAt = null)
        {
            var tags = new WorldEventHoldTag[Math.Max(0, entryCount)];

            for (var i = 0; i < tags.Length; i++)
                tags[i] = BuildTag(plan, entryCount, i, runId, run, tuning, attackAt?.Invoke(i));

            return tags;
        }

        /// <summary>
        /// The tag of entry <paramref name="i"/> of a <paramref name="entryCount"/> batch (see <see cref="BuildTags"/>). The
        /// spawner calls this once per constructed creature, after its ranged check. Never null.
        /// </summary>
        public static WorldEventHoldTag BuildTag(HoldWavePlan plan, int entryCount, int i, uint runId, IWorldEventHoldRunView run,
            WorldEventHoldTuning tuning, EntryAttack? attack)
        {
            plan ??= new HoldWavePlan();
            tuning ??= WorldEventHoldTuning.Defaults;

            var hunters = plan.Type == HoldWaveType.Hunters
                ? WorldEventHoldRules.PickHunterIndices(entryCount, plan.Hunters, plan.SeekerIndices, plan.ChampionEntryIndex)
                : new HashSet<int>();

            if (plan.Type == HoldWaveType.Finale && !plan.FinaleEscort && i == plan.ChampionEntryIndex)
            {
                return new WorldEventHoldTag
                {
                    RunId = runId,
                    Run = run,
                    Kind = HoldUnitKind.FinaleBoss,
                    WaveRole = HoldRole.Seeker,
                    Role = HoldDamageRole.None,
                    Seeker = !string.Equals(tuning.FinaleTarget, ProtectDef.FinaleTargetPlayers, StringComparison.Ordinal)
                };
            }

            var role = WaveRoleAt(plan, i, hunters);
            var seeker = plan.Type == HoldWaveType.Mixed
                ? role != HoldRole.Hunter
                : role != HoldRole.Hunter && (plan.Type == HoldWaveType.Breach || plan.Type == HoldWaveType.Runners
                    || (plan.SeekerIndices != null && plan.SeekerIndices.Contains(i)));

            // A ranged slot whose monster cannot really shoot or cast is a plain seeker.
            if (role == HoldRole.Ranged && attack != null && !attack.Value.Ranged)
            {
                role = HoldRole.Seeker;
                seeker = plan.Type == HoldWaveType.Mixed || (plan.SeekerIndices != null && plan.SeekerIndices.Contains(i));
            }

            var caster = role == HoldRole.Ranged && attack != null && attack.Value.Caster;
            var corrupted = role == HoldRole.Corrupted;

            uint forced = 0, avoid = 0;

            if (role == HoldRole.Breach)
                forced = plan.BreachTargetGuid;
            else if (corrupted && plan.CorruptedTargets != null && plan.CorruptedTargets.TryGetValue(i, out var target))
                forced = target;
            else if (plan.Type == HoldWaveType.Mixed && seeker && plan.BreachTargetGuid != 0)
                avoid = plan.BreachTargetGuid;

            var runner = role == HoldRole.Runner;
            var damageRole = WorldEventHoldRules.DamageRoleFor(role, caster);
            var roleSpeed = tuning.Role(damageRole).AttackSpeedMult;

            return new WorldEventHoldTag
            {
                RunId = runId,
                Run = run,
                Kind = HoldUnitKind.Monster,
                WaveRole = role,
                Role = damageRole,
                Seeker = seeker,
                Hunter = role == HoldRole.Hunter,
                Corrupted = corrupted,
                Warded = plan.Warded && role == HoldRole.Ranged,
                ForcedTargetGuid = forced,
                AvoidTargetGuid = avoid,
                RunRateMult = runner ? plan.RunRateMult : 1f,
                // A runner wave's own attack speed, else the role's (world_events_hold_<role>_attack_speed_mult), else 1.
                AttackSpeedMult = runner ? plan.AttackSpeedMult
                    : roleSpeed is double s && double.IsFinite(s) && s > 0 ? (float)Math.Clamp(s, 0.25, 3) : 1f,
                HoldDistance = role == HoldRole.Ranged ? plan.HoldDistance : 0f,
                TintPalette = corrupted ? tuning.CorruptedTintPalette : 0
            };
        }

        private static HoldRole WaveRoleAt(HoldWavePlan plan, int i, HashSet<int> hunters)
        {
            switch (plan.Type)
            {
                case HoldWaveType.Mixed:
                    return plan.Roles != null && i < plan.Roles.Length ? plan.Roles[i] : HoldRole.Seeker;
                case HoldWaveType.Breach:
                    return plan.BreachTargetGuid != 0 ? HoldRole.Breach : HoldRole.Seeker;
                case HoldWaveType.Runners:
                    return HoldRole.Runner;
                case HoldWaveType.Ranged:
                    return HoldRole.Ranged;
                case HoldWaveType.Hunters:
                    return hunters.Contains(i) ? HoldRole.Hunter : HoldRole.Seeker;
                default:
                    return HoldRole.Seeker;
            }
        }
    }
}
