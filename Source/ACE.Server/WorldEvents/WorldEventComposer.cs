using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Resolves a <see cref="WorldEventRequest"/> against an axis store and a family catalog into a
    /// <see cref="WorldEventComposition"/> (TECH-DESIGN 2.3).
    ///
    /// Pure apart from one read - no logging, no manager state, no side effects - so the whole refusal matrix is unit
    /// testable (D6) and so "/worldevent simulate" and "--dry" can run it without touching a live event. The one read is
    /// world_events_hold_enabled for a pinned or rolled Hold goal (World Events Hold H6), behind the <see cref="HoldEnabledReader"/> seam.
    /// WorldEventManager.TryCompose is a thin wrapper that adds the one warning this cannot emit.
    /// </summary>
    public static class WorldEventComposer
    {
        public const string DefaultBossId = "none";
        public const string DefaultRewardId = "standard";

        /// <summary>
        /// The pseudo boss id that asks the composer to pick a Named boss for the caller: the one bound
        /// to the composed family when exactly one is bound, a random Named boss when the family has no
        /// binding (or more than one boss shares the binding), and a refusal only when the store has no
        /// Named bosses at all. Reserved in <see cref="WorldEventAxisStore"/> so bosses.json can never
        /// define a boss with this id.
        /// </summary>
        public const string AutoBossId = "auto";

        /// <summary>The synthetic anchor id used by the "--here" path.</summary>
        public const string HereAnchorId = "here";

        /// <summary>
        /// The synthetic anchor id used by a web Admin panel start, which carries an explicit position and
        /// label rather than a player's location (Docs/AdminPanel/WORLD-EVENTS-START.md).
        /// </summary>
        public const string WebAnchorId = "web";

        /// <summary>
        /// How many families one run may compose (two-family composition, 2026-08-29). A RANDOM run always
        /// draws exactly two; an explicit "--family" may name one or two and is never constrained further.
        /// </summary>
        public const int MaxComposedFamilies = 2;

        /// <summary>
        /// How many of the shuffled candidate family pairs a random composition crosses with the
        /// (source, goal) list before giving up. See <see cref="TryComposeRandom"/> for why this is small.
        /// </summary>
        public const int MaxPairsTried = 8;

        // ---- World Events Hold: the composer gate (H6) ------------------------------------------------------------------

        /// <summary>
        /// THE ONE-LINE FLIP (owner ruled 2026-11-16: random rolls may pick Hold): whether a RANDOM roll may land on a Hold
        /// goal at all. False: a Hold goal starts only when pinned with --goal, and a random roll never picks one whatever
        /// world_events_hold_enabled says. True: a random roll may pick a Hold goal while world_events_hold_enabled is on.
        /// Every decision reads it through <see cref="RandomRollMayPick"/>.
        /// </summary>
        public const bool RandomRollIncludesHoldGoals = true;

        /// <summary>
        /// Whether a random roll may cross a source with <paramref name="goal"/> (pure): every goal that is not Hold; a Hold
        /// goal only when <see cref="RandomRollIncludesHoldGoals"/> and the switch is on.
        /// </summary>
        public static bool RandomRollMayPick(GoalDef goal, bool holdEnabled)
        {
            if (goal == null || goal.TypeKind != GoalType.Hold)
                return true;

            return RandomRollIncludesHoldGoals && holdEnabled;
        }

        /// <summary>The refusal a pinned Hold goal gets while world_events_hold_enabled is off. ASCII.</summary>
        public static string HoldOffError(string goalId) =>
            $"Hold goals are off ({Objectives.WorldEventObjectiveFactory.HoldEnabledProperty} is false); '{goalId}' cannot start";

        /// <summary>
        /// world_events_hold_enabled for the composer's gate. The composer's one read that is not of its inputs; a seam so tests
        /// stay pure (process-wide: a test that sets it restores it in a finally). The live reader treats an unreadable
        /// property as OFF.
        /// </summary>
        internal static Func<GoalDef, bool> HoldEnabledReader = ReadHoldEnabled;

        private static bool ReadHoldEnabled(GoalDef goal)
        {
            try
            {
                return WorldEventHoldSettingsResolver.Flag(goal, HoldKeys.Enabled);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether <paramref name="goal"/> may start at all as far as the Hold switch goes (pure apart from the reader seam):
        /// every goal that is not Hold; a Hold goal only while world_events_hold_enabled is on. The web Admin catalog uses it
        /// to leave a Hold goal out while it would only be refused.
        /// </summary>
        internal static bool GoalSwitchedOn(GoalDef goal) => goal == null || goal.TypeKind != GoalType.Hold || HoldEnabled(goal);

        private static bool HoldEnabled(GoalDef goal)
        {
            try
            {
                return (HoldEnabledReader ?? ReadHoldEnabled)(goal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TryCompose(WorldEventAxisStore store, WorldEventCatalog catalog,
            WorldEventRequest request, out WorldEventComposition composition, out string error, Random rng = null)
        {
            composition = null;
            error = null;

            if (store == null)
            {
                error = "no axes loaded";
                return false;
            }

            if (request == null)
            {
                error = "no request";
                return false;
            }

            if (request.Random)
                return TryComposeRandom(store, catalog, request, out composition, out error, rng);

            if (string.IsNullOrWhiteSpace(request.SourceId))
            {
                error = "missing --source";
                return false;
            }

            if (!TryNormalizeFamilyIds(request, out var familyIds, out error))
                return false;

            if (string.IsNullOrWhiteSpace(request.GoalId))
            {
                error = "missing --goal";
                return false;
            }

            var sourceId = Normalize(request.SourceId);
            var goalId = Normalize(request.GoalId);

            if (!store.Sources.TryGetValue(sourceId, out var source))
            {
                error = Unknown("source", sourceId, store.Sources.Keys);
                return false;
            }

            if (!SourceAllowed(request, sourceId))
            {
                error = $"source '{sourceId}' is not allowed for this request (allowed: {Join(request.AllowedSourceIds)})";
                return false;
            }

            if (!store.Goals.TryGetValue(goalId, out var goal))
            {
                error = Unknown("goal", goalId, store.Goals.Keys);
                return false;
            }

            // World Events Hold: a Hold goal starts only while world_events_hold_enabled is on, and the refusal names it.
            if (goal.TypeKind == GoalType.Hold && !HoldEnabled(goal))
            {
                error = HoldOffError(goalId);
                return false;
            }

            var bossId = string.IsNullOrWhiteSpace(request.BossId) ? DefaultBossId : Normalize(request.BossId);
            var bossWasAuto = bossId == AutoBossId;

            if (bossWasAuto)
            {
                if (!TryResolveAutoBoss(store, familyIds, rng, out bossId, out error))
                    return false;
            }

            if (!store.Bosses.TryGetValue(bossId, out var boss))
            {
                error = Unknown("boss", bossId, store.Bosses.Keys);
                return false;
            }

            string rewardId;

            if (!string.IsNullOrWhiteSpace(request.RewardId))
                rewardId = Normalize(request.RewardId);
            else if (store.Rewards.ContainsKey(DefaultRewardId))
                rewardId = DefaultRewardId;
            else
            {
                error = "missing --reward";
                return false;
            }

            if (!store.Rewards.TryGetValue(rewardId, out var reward))
            {
                error = Unknown("reward", rewardId, store.Rewards.Keys);
                return false;
            }

            var compatible = source.CompatibleGoals ?? new List<string>();

            if (!compatible.Contains(goalId))
            {
                error = $"goal '{goalId}' is not compatible with source '{sourceId}' (compatible: {Join(compatible)})";
                return false;
            }

            // 2026-08-16: kill_boss with no boss goal is the sibling of the placement-retry fix - a run
            // that can never place a champion is structurally unwinnable from the moment it starts, so this
            // refuses it at compose time rather than letting it reach WorldEventObjectiveFactory's own
            // refusal ("goal kill_boss needs a --boss that is not 'none'") after the run has already
            // announced. boss is already resolved above (auto included), so this catches every path that
            // can produce Kind == None, not just an explicit "--boss none".
            if (goal.TypeKind == GoalType.KillBoss && boss.Kind == BossKind.None)
            {
                error = "goal 'kill_boss' needs --boss (family_champion, auto, or a named boss id)";
                return false;
            }

            var families = new List<FamilyDef>();

            foreach (var id in familyIds)
            {
                if (!TryResolveFamily(store, catalog, id, out var family, out error))
                    return false;

                families.Add(family);
            }

            // Owner ruling 2026-10-03: a named boss's bosses.json familyId is an auto-pick preference only
            // (TryResolveAutoBoss), never a compose-time gate. An explicit named boss composes with any family.

            if (!TryResolveAnchor(store, request, out var anchor, out var anchorPosition, out error))
                return false;

            // A NAMED anchor (--anchor <id>) carries the city's maxRadius; a position anchor (--here, the web page) does not.
            if (!string.IsNullOrWhiteSpace(request.AnchorId))
            {
                error = WorldEventAnchorRules.Refusal(anchor, source);

                if (error != null)
                    return false;
            }

            composition = new WorldEventComposition(source, families, boss, goal, reward, anchor, anchorPosition, store);
            return true;
        }

        /// <summary>
        /// The composed family ids, normalised, in slot order (two-family composition, 2026-08-29).
        /// Refuses an empty list ("missing --family", the message a single-family request has always
        /// produced), a blank element, a duplicate, and more than <see cref="MaxComposedFamilies"/>.
        ///
        /// Order is preserved rather than sorted: slot A is the one the pairing floor rule bound, and
        /// "family=a+b" in the compose log line has to name them in the order they were drawn.
        /// </summary>
        private static bool TryNormalizeFamilyIds(WorldEventRequest request, out List<string> familyIds, out string error)
        {
            familyIds = new List<string>();
            error = null;

            var requested = request.FamilyIds ?? new List<string>();

            if (requested.Count == 0)
            {
                error = "missing --family";
                return false;
            }

            if (requested.Count > MaxComposedFamilies)
            {
                error = $"a run composes at most {MaxComposedFamilies} families, got {requested.Count}";
                return false;
            }

            foreach (var raw in requested)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    error = "missing --family";
                    return false;
                }

                var id = Normalize(raw);

                if (familyIds.Contains(id, StringComparer.Ordinal))
                {
                    error = $"family '{id}' is listed twice - a run's two families must be different";
                    return false;
                }

                familyIds.Add(id);
            }

            return true;
        }

        /// <summary>
        /// "/worldevent start random" (TECH-DESIGN 5.3): fills in only the axes <paramref name="request"/>
        /// left blank and hands the result to the ordinary <see cref="TryCompose"/> path, so this never
        /// restates the compatibility matrix - it just searches it. Any axis the caller pinned (--source,
        /// --family, --goal, --boss) is honoured verbatim on every attempt.
        ///
        /// Candidate (source, goal) pairs come from crossing every source with its own CompatibleGoals.
        ///
        /// The FAMILY half is drawn as a PAIR (two-family composition, 2026-08-29):
        /// <see cref="WorldEventFamilyPairing.CandidatePairs"/> returns every ordered pair of distinct
        /// catalog families that satisfies the pairing rule - family A must have a member at or below
        /// world_events_family_floor_level, and at least one of A/B must have a caster - degrading through
        /// <see cref="WorldEventFamilyPairing.PairTier"/> when nothing in the catalog can satisfy that.
        /// The (source, goal) list is ordered by weighted sampling (<see cref="WeightedPairOrder"/>) and the family
        /// pairs are shuffled once (Fisher-Yates) with the caller's rng before search, so a seeded rng
        /// reproduces the same run, and search crosses the first <see cref="MaxPairsTried"/> family pairs
        /// with the shuffled (source, goal) list until one composes, within the attempt budget
        /// <see cref="ResolveMaxAttempts"/> sets (big enough that every pair being tried gets a full cross).
        ///
        /// A PINNED "--family" is honoured verbatim and is never constrained: "--family a" composes that
        /// one family alone with no rolled partner, and "--family a,b" composes exactly those two. An admin
        /// naming a family is making a deliberate choice, and the floor/caster rule exists to protect the
        /// UNATTENDED roll, not to overrule them.
        ///
        /// When the candidate lists are empty outright (an unknown/incompatible pinned axis, or no
        /// families at all), this delegates straight to <see cref="TryCompose"/> with the best-effort ids
        /// so the caller gets the same specific refusal a manual "/worldevent start" would have produced,
        /// rather than a generic "no combination" message.
        ///
        /// This is the FIRST element of <see cref="RandomCompositions"/>; WorldEventManager.TryStart walks further
        /// along the same sequence when a later stage (the start-spot rule, the objective factory) refuses it.
        /// </summary>
        private static bool TryComposeRandom(WorldEventAxisStore store, WorldEventCatalog catalog,
            WorldEventRequest request, out WorldEventComposition composition, out string error, Random rng)
        {
            var first = RandomCompositions(store, catalog, request, rng).First();

            composition = first.Composition;
            error = first.Error;

            return composition != null;
        }

        /// <summary>
        /// One step of <see cref="RandomCompositions"/>: a composed candidate (<see cref="Composition"/> set,
        /// <see cref="Error"/> null), or the search's end (<see cref="Composition"/> null, <see cref="Error"/>
        /// the refusal <see cref="TryCompose"/> would have returned).
        /// </summary>
        internal readonly struct RandomComposeStep
        {
            public RandomComposeStep(WorldEventComposition composition, string error)
            {
                Composition = composition;
                Error = error;
            }

            public WorldEventComposition Composition { get; }

            public string Error { get; }
        }

        /// <summary>
        /// The random search of <see cref="TryComposeRandom"/> as a LAZY sequence, in the weighted draw order: every
        /// candidate that composes, then exactly one terminal step with a null composition and the refusal. Nothing is
        /// searched past what the caller pulls, so taking the first element costs exactly what TryComposeRandom always
        /// did. Every yielded composition is independent (its own anchor Position, see <see cref="TryResolveAnchor"/>),
        /// so a caller may let a later stage mutate or discard one and move on to the next. <paramref name="request"/>
        /// is never written.
        /// </summary>
        internal static IEnumerable<RandomComposeStep> RandomCompositions(WorldEventAxisStore store, WorldEventCatalog catalog,
            WorldEventRequest request, Random rng)
        {
            if (store == null)
            {
                yield return new RandomComposeStep(null, "no axes loaded");
                yield break;
            }

            if (request == null)
            {
                yield return new RandomComposeStep(null, "no request");
                yield break;
            }

            WorldEventComposition composition;
            string error;

            rng = rng ?? new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

            var requestedSource = string.IsNullOrWhiteSpace(request.SourceId) ? null : Normalize(request.SourceId);
            var requestedGoal = string.IsNullOrWhiteSpace(request.GoalId) ? null : Normalize(request.GoalId);

            var pinnedFamilies = (request.FamilyIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(Normalize)
                .ToList();

            var pairs = RandomCandidatePairs(store, request, requestedSource, requestedGoal);

            // Weighted draw (TECH-DESIGN 5.3): replaces the old uniform shuffle of the (source, goal) list. The list
            // comes back in draw order with every weight-0 pair removed, so "first pair that composes wins" holds.
            if (pairs.Count > 0)
            {
                pairs = WeightedPairOrder(store, pairs, requestedSource != null, requestedGoal != null, rng);

                if (pairs.Count == 0)
                {
                    yield return new RandomComposeStep(null, ZeroWeightRefusal(store, requestedSource, requestedGoal));
                    yield break;
                }
            }

            var floorLevel = WorldEventFamilyPairing.FloorLevel();

            var tier = WorldEventFamilyPairing.PairTier.Single;

            List<List<string>> familyCandidates;

            if (pinnedFamilies.Count > 0)
            {
                // Pinned verbatim: exactly what the admin asked for, one candidate, no constraint check and
                // no rolled partner. A duplicate or a third id is still refused, by TryCompose.
                familyCandidates = new List<List<string>> { pinnedFamilies };
            }
            else
            {
                familyCandidates = WorldEventFamilyPairing.CandidatePairs(catalog, floorLevel, out tier)
                    .Select(p => p.b == null ? new List<string> { p.a } : new List<string> { p.a, p.b })
                    .ToList();
            }

            if (pairs.Count == 0 || familyCandidates.Count == 0)
            {
                if (familyCandidates.Count == 0 && pinnedFamilies.Count == 0)
                {
                    yield return new RandomComposeStep(null, "random composition: no family in the catalog has any members");
                    yield break;
                }

                if (requestedSource == null && !store.Sources.Keys.Any(id => SourceAllowed(request, id)))
                {
                    yield return new RandomComposeStep(null,
                        $"random composition: no loaded source is allowed for this request (allowed: {Join(request.AllowedSourceIds)})");
                    yield break;
                }

                var probeSourceId = requestedSource ?? store.Sources.Keys
                    .Where(id => SourceAllowed(request, id))
                    .OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault();
                var probeGoalId = requestedGoal ?? store.Goals.Keys.OrderBy(g => g, StringComparer.Ordinal).FirstOrDefault();
                var probeFamilyIds = familyCandidates.FirstOrDefault() ?? pinnedFamilies;

                var probe = ClonePinned(request, probeSourceId, probeFamilyIds, probeGoalId, request.BossId);

                TryCompose(store, catalog, probe, out _, out error);

                if (string.IsNullOrEmpty(error))
                    error = "no compatible source/family/goal combination was found";

                yield return new RandomComposeStep(null, error);
                yield break;
            }

            Shuffle(familyCandidates, rng);

            // Only the first few drawn family pairs are crossed with the (source, goal) list. The pair is
            // the thing being drawn - trying every one of them in turn would walk the whole ordered
            // enumeration on a catalog where something else is what refuses, which is neither random nor
            // cheap. Anything beyond this is a search for a pair that composes at all, not a draw.
            //
            // MaxPairsTried is the ONLY thing that decides how many pairs are tried; the attempt budget
            // below is sized from it so it can never become the real limit by accident (see
            // ResolveMaxAttempts).
            var tried = familyCandidates.Take(MaxPairsTried).ToList();

            string lastError = null;
            var attempts = 0;

            var maxAttempts = ResolveMaxAttempts(tried.Count, pairs.Count);

            foreach (var combo in tried.SelectMany(f => pairs.Select(p => (p.sourceId, p.goalId, familyIds: f))))
            {
                if (attempts >= maxAttempts)
                    break;

                attempts++;

                string bossId;

                if (!string.IsNullOrWhiteSpace(request.BossId))
                    bossId = request.BossId;
                else if (store.Goals.TryGetValue(combo.goalId, out var goalDef) && goalDef.TypeKind == GoalType.KillBoss)
                    bossId = AutoBossId;
                else if (goalDef != null && goalDef.TypeKind == GoalType.Hold)
                    // A Hold run brings its own finale boss; a rolled family champion would also arm the base champion
                    // tick, whose placement grace can end the run as SuccessBossAbsent. Same as a pinned Hold start.
                    bossId = DefaultBossId;
                else
                    bossId = rng.Next(2) == 0 ? DefaultBossId : "family_champion";

                var attemptRequest = ClonePinned(request, combo.sourceId, combo.familyIds, combo.goalId, bossId);

                if (TryCompose(store, catalog, attemptRequest, out composition, out error, rng))
                {
                    // Only a ROLLED pair has a tier worth reporting; a pinned --family bypassed the rule
                    // entirely, so it leaves these null and WorldEventManager logs no pairing line.
                    if (pinnedFamilies.Count == 0)
                    {
                        composition.PairingTier = tier;
                        composition.PairingFloorLevel = floorLevel;
                    }

                    yield return new RandomComposeStep(composition, null);
                    continue;
                }

                lastError = error;
            }

            yield return new RandomComposeStep(null, lastError != null
                ? $"{lastError}; no compatible source/family/goal combination was found"
                : "no compatible source/family/goal combination was found");
        }

        /// <summary>
        /// The (source, goal) pairs a random roll may draw from: every source crossed with its own CompatibleGoals,
        /// minus sources the request does not allow and (for an unpinned goal) Hold goals the roll may not pick.
        /// Weights are NOT applied here - <see cref="WeightedPairOrder"/> does that. Ordered by source then goal id.
        /// </summary>
        internal static List<(string sourceId, string goalId)> RandomCandidatePairs(WorldEventAxisStore store,
            WorldEventRequest request, string requestedSource, string requestedGoal)
        {
            var pairs = new List<(string sourceId, string goalId)>();


            foreach (var source in store.Sources.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                if (requestedSource != null && source.Id != requestedSource)
                    continue;

                // A restricted request (the web Admin panel) only ever searches the sources it allows, so a
                // roll can never land on one it would have refused had it been pinned.
                if (!SourceAllowed(request, source.Id))
                    continue;

                // A named city anchor (--anchor) only hosts the sources whose clearance fits its maxRadius, the same rule the
                // scheduler narrows by. Done per candidate rather than through AllowedSourceIds, which would also lift the
                // in-game roll's InGameRandom opt-out below.
                if (!string.IsNullOrWhiteSpace(request.AnchorId)
                    && store.Anchors.TryGetValue(Normalize(request.AnchorId), out var namedAnchor)
                    && !WorldEventAnchorRules.Hosts(namedAnchor, source))
                    continue;

                // An unrestricted in-game roll skips a source that opts out (envoy_muster: its only goal is ambush's too).
                if (requestedSource == null && request.AllowedSourceIds == null && !source.InGameRandom)
                    continue;

                var compatibleGoals = (source.CompatibleGoals ?? new List<string>())
                    .Where(store.Goals.ContainsKey)
                    .OrderBy(g => g, StringComparer.Ordinal);

                foreach (var goalId in compatibleGoals)
                {
                    if (requestedGoal != null && goalId != requestedGoal)
                        continue;

                    // World Events Hold (H6): the roll lands on a Hold goal only when RandomRollMayPick allows it; a goal pinned
                    // with --goal is the caller's choice and is crossed here, then gated by TryCompose's switch check.
                    if (requestedGoal == null && store.Goals.TryGetValue(goalId, out var rolledGoal)
                        && rolledGoal.TypeKind == GoalType.Hold && !RandomRollMayPick(rolledGoal, RandomRollIncludesHoldGoals && HoldEnabled(rolledGoal)))
                        continue;

                    pairs.Add((source.Id, goalId));
                }
            }

            return pairs;
        }

        /// <summary>
        /// The refusal for a roll whose every candidate pair has weight 0. Names the axis that is actually unpinned,
        /// because that is the one the caller can pin to get a run.
        /// </summary>
        private static string ZeroWeightRefusal(WorldEventAxisStore store, string requestedSource, string requestedGoal)
        {
            if (requestedSource != null && requestedGoal == null)
            {
                var holdHints = string.Empty;

                if (store.Sources.TryGetValue(requestedSource, out var source))
                {
                    foreach (var goalId in (source.CompatibleGoals ?? new List<string>()).OrderBy(g => g, StringComparer.Ordinal))
                    {
                        if (store.Goals.TryGetValue(goalId, out var goal) && goal.TypeKind == GoalType.Hold)
                            holdHints += $"; {goalId} also needs world_events_hold_enabled";
                    }
                }

                return $"random composition: every candidate goal for source '{requestedSource}' has random weight 0 " +
                       $"(pin --goal to start one explicitly{holdHints})";
            }

            if (requestedGoal != null && requestedSource == null)
                return $"random composition: every candidate source for goal '{requestedGoal}' has random weight 0 " +
                       "(pin --source to start one explicitly)";

            return "random composition: every candidate source/goal has random weight 0 " +
                   "(pin --source or --goal to start one explicitly)";
        }

        /// <summary>
        /// The attempt budget for one random search: enough for every family pair actually being tried to
        /// get a FULL cross with the (source, goal) list, floored at <see cref="MinRandomAttempts"/> and
        /// ceilinged at <see cref="MaxRandomAttempts"/>.
        ///
        /// This used to be a flat 200, which silently coupled the search to how much content happened to
        /// exist. Today's axes produce 21 (source, goal) combinations, so 200 covers 9 pairs and the cap is
        /// invisible; at 26 combinations it covers 7, and family pairs 8 and beyond would never be attempted
        /// at all - a starvation that would arrive with a content addition, nowhere near this file, and show
        /// up only as a family pair that mysteriously never composes. The budget now grows with the content
        /// instead, so <see cref="MaxPairsTried"/> stays the only thing that decides how many pairs are
        /// tried.
        ///
        /// The ceiling is what keeps this a budget rather than an unbounded walk: a pathological axis store
        /// cannot turn one refused composition into an arbitrarily long search.
        /// </summary>
        internal static int ResolveMaxAttempts(int pairsTried, int sourceGoalCount)
        {
            var full = Math.Max(0, pairsTried) * (long)Math.Max(0, sourceGoalCount);

            if (full < MinRandomAttempts)
                return MinRandomAttempts;

            return full > MaxRandomAttempts ? MaxRandomAttempts : (int)full;
        }

        /// <summary>The floor on the random search's attempt budget - the historic flat cap.</summary>
        internal const int MinRandomAttempts = 200;

        /// <summary>The ceiling on the random search's attempt budget. See <see cref="ResolveMaxAttempts"/>.</summary>
        internal const int MaxRandomAttempts = 2000;

        /// <summary>A copy of <paramref name="request"/> with the four composed axes pinned and Random cleared.</summary>
        private static WorldEventRequest ClonePinned(WorldEventRequest request, string sourceId,
            IEnumerable<string> familyIds, string goalId, string bossId)
        {
            return new WorldEventRequest
            {
                SourceId = sourceId,
                FamilyIds = familyIds == null ? new List<string>() : familyIds.ToList(),
                GoalId = goalId,
                BossId = bossId,
                RewardId = request.RewardId,
                AnchorId = request.AnchorId,
                AnchorPosition = request.AnchorPosition,
                AnchorLabel = request.AnchorLabel,
                PositionAnchorId = request.PositionAnchorId,
                AllowedSourceIds = request.AllowedSourceIds,
                Invoker = request.Invoker,
                Random = false,
                DryRun = request.DryRun,
                AnnounceLeadSeconds = request.AnnounceLeadSeconds,
                MaxDurationSeconds = request.MaxDurationSeconds,
                MinDurationSeconds = request.MinDurationSeconds,
                AbandonAfterSeconds = request.AbandonAfterSeconds,
                WipeGraceSeconds = request.WipeGraceSeconds,
                TeaserLeadSeconds = request.TeaserLeadSeconds
            };
        }

        /// <summary>
        /// Orders the candidate (source, goal) pairs by repeated weighted sampling WITHOUT replacement. Each
        /// step draws a source bucket by weight, then a source inside it, then a goal of that source, each
        /// share taken over only the pairs still eligible, so P(pair) = bucket x source x goal share.
        ///
        /// Bucket = the source's randomGroup (weight from <see cref="WorldEventAxisStore.RandomGroups"/>, a
        /// source inside it counts by its randomWeight) or, ungrouped, the source itself (its randomWeight is
        /// the bucket weight). A pinned axis ignores its own weight (it counts as 1) so a weight-0 source or
        /// goal can still be started explicitly. Pairs with any applicable weight at 0 are dropped.
        /// Candidates must arrive in a stable order; the result is reproducible for a seeded rng.
        /// </summary>
        internal static List<(string sourceId, string goalId)> WeightedPairOrder(WorldEventAxisStore store,
            IReadOnlyList<(string sourceId, string goalId)> candidates, bool sourcePinned, bool goalPinned, Random rng)
        {
            var pool = new List<(string sourceId, string goalId, string bucket, double bucketW, double sourceW, double goalW)>();

            foreach (var (sourceId, goalId) in candidates)
            {
                if (!store.Sources.TryGetValue(sourceId, out var source) || !store.Goals.TryGetValue(goalId, out var goal))
                    continue;

                string bucket;
                double bucketW;
                double sourceW;

                if (!string.IsNullOrEmpty(source.RandomGroup) && store.RandomGroups.TryGetValue(source.RandomGroup, out var groupW))
                {
                    bucket = "g:" + source.RandomGroup;
                    bucketW = groupW;
                    sourceW = source.RandomWeight;
                }
                else
                {
                    bucket = "s:" + sourceId;
                    bucketW = source.RandomWeight;
                    sourceW = 1.0;
                }

                if (sourcePinned)
                {
                    bucketW = 1.0;
                    sourceW = 1.0;
                }

                var goalW = goalPinned ? 1.0 : goal.RandomWeight;

                if (bucketW > 0 && sourceW > 0 && goalW > 0)
                    pool.Add((sourceId, goalId, bucket, bucketW, sourceW, goalW));
            }

            var ordered = new List<(string sourceId, string goalId)>(pool.Count);

            // A level at a time: pick a key among the distinct keys of the remaining pool by weight.
            int PickIndex(List<double> weights)
            {
                var total = 0.0;

                foreach (var w in weights)
                    total += w;

                var roll = rng.NextDouble() * total;

                for (var i = 0; i < weights.Count; i++)
                {
                    roll -= weights[i];

                    if (roll < 0)
                        return i;
                }

                return weights.Count - 1;
            }

            while (pool.Count > 0)
            {
                var buckets = pool.Select(e => e.bucket).Distinct().ToList();
                var bucket = buckets[PickIndex(buckets.Select(b => pool.First(e => e.bucket == b).bucketW).ToList())];

                var inBucket = pool.Where(e => e.bucket == bucket).ToList();
                var sources = inBucket.Select(e => e.sourceId).Distinct().ToList();
                var sourceId = sources[PickIndex(sources.Select(id => inBucket.First(e => e.sourceId == id).sourceW).ToList())];

                var goals = inBucket.Where(e => e.sourceId == sourceId).ToList();
                var picked = goals[PickIndex(goals.Select(e => e.goalW).ToList())];

                ordered.Add((picked.sourceId, picked.goalId));
                pool.Remove(picked);
            }

            return ordered;
        }

        /// <summary>Fisher-Yates, in place, using the caller's rng so a seed makes the pick reproducible.</summary>
        private static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// Resolves <see cref="AutoBossId"/> (BOSS-STANDARD.md section 5) to a concrete Named boss id.
        /// Runs before the families are validated, so it is keyed on the raw normalised id strings -
        /// an invalid family still falls through to the "unknown family" refusal later in TryCompose,
        /// this only decides which Named boss id to look up in the meantime.
        ///
        /// With two composed families the bound pool is the UNION of the bosses bound to EITHER of them,
        /// picked from uniformly (two-family composition, 2026-08-29) - not "family A's bosses, then
        /// family B's". A run composed of a Drudge half and a Virindi half is as much one as the other, so
        /// weighting the draw toward slot A would make the second family cosmetic.
        ///
        /// Enumeration is ordered by boss id (ordinal) before any random pick, so a seeded rng is
        /// reproducible regardless of the store's internal dictionary order.
        /// </summary>
        private static bool TryResolveAutoBoss(WorldEventAxisStore store, IReadOnlyList<string> familyIds, Random rng,
            out string bossId, out string error)
        {
            bossId = null;
            error = null;

            var named = store.Bosses.Values
                .Where(b => b.Kind == BossKind.Named)
                .OrderBy(b => b.Id, StringComparer.Ordinal)
                .ToList();

            if (named.Count == 0)
            {
                error = "boss 'auto' needs at least one named boss in bosses.json";
                return false;
            }

            var ids = familyIds ?? new List<string>();

            var bound = named.Where(b => !string.IsNullOrEmpty(b.FamilyId) && ids.Contains(b.FamilyId, StringComparer.Ordinal)).ToList();

            var pool = bound.Count > 0 ? bound : named;

            rng = rng ?? new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

            bossId = pool[rng.Next(pool.Count)].Id;
            return true;
        }

        /// <summary>
        /// A family is valid when the weenie-scan catalog has at least one member for it. The
        /// families.json entry is metadata only (C2), so a family that exists in the catalog but not in the
        /// JSON gets a synthesized definition rather than a refusal - that is what makes a throwaway
        /// flagged clone usable without a content edit. The caller is responsible for warning about it.
        /// </summary>
        private static bool TryResolveFamily(WorldEventAxisStore store, WorldEventCatalog catalog,
            string familyId, out FamilyDef family, out string error)
        {
            family = null;
            error = null;

            var inStore = store.Families.TryGetValue(familyId, out var storeFamily);

            IReadOnlyList<FamilyMember> members = null;
            var inCatalog = catalog != null && catalog.Families.TryGetValue(familyId, out members);

            if (!inStore && !inCatalog)
            {
                var known = store.Families.Keys.AsEnumerable();

                if (catalog != null)
                    known = known.Concat(catalog.Families.Keys);

                error = Unknown("family", familyId, known.Distinct());
                return false;
            }

            if (members == null || members.Count == 0)
            {
                error = $"family '{familyId}' has no catalog members - flag creature weenies with WorldEventCreature/WorldEventFamily/WorldEventRole";
                return false;
            }

            family = new FamilyDef
            {
                Id = inStore ? storeFamily.Id : familyId,
                DisplayName = inStore ? storeFamily.DisplayName : familyId,
                HueKey = inStore ? storeFamily.HueKey : null,
                BiomeTags = inStore && storeFamily.BiomeTags != null
                    ? new List<string>(storeFamily.BiomeTags)
                    : new List<string>(),
                Members = new List<FamilyMember>(members)
            };

            return true;
        }

        private static bool TryResolveAnchor(WorldEventAxisStore store, WorldEventRequest request,
            out AnchorDef anchor, out Position position, out string error)
        {
            anchor = null;
            position = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(request.AnchorId))
            {
                var anchorId = Normalize(request.AnchorId);

                if (!store.Anchors.TryGetValue(anchorId, out anchor))
                {
                    error = Unknown("anchor", anchorId, store.Anchors.Keys);
                    return false;
                }

                position = new Position(anchor.CellId, anchor.OriginX, anchor.OriginY, anchor.OriginZ,
                    anchor.AnglesX, anchor.AnglesY, anchor.AnglesZ, anchor.AnglesW, 0);

                return true;
            }

            if (request.AnchorPosition == null)
            {
                error = "missing anchor - pass --here or --anchor <name>";
                return false;
            }

            // A COPY, never the request's own object: a start-spot rule moves the composed anchor in place
            // (WorldEventHoldStartSpotRule.AnchorAt), and a random start that refuses one candidate composes the next
            // from this same request (WorldEventManager.TryStart). Sharing the reference would start every later
            // candidate from the spot an earlier one was moved to, and would rewrite the caller's request.
            // relativePos: true copies the raw cell and offsets verbatim; the non-relative constructor would re-derive the
            // cell from the offsets (SetPosition) when the low word is 0, so the copy would not be exact.
            var asked = request.AnchorPosition;
            position = new Position(asked.Cell, asked.PositionX, asked.PositionY, asked.PositionZ,
                asked.RotationX, asked.RotationY, asked.RotationZ, asked.RotationW, asked.Instance, relativePos: true);

            var positionAnchorId = string.IsNullOrWhiteSpace(request.PositionAnchorId) ? HereAnchorId : request.PositionAnchorId;

            anchor = new AnchorDef
            {
                Id = positionAnchorId,
                DisplayName = string.IsNullOrWhiteSpace(request.AnchorLabel) ? positionAnchorId : request.AnchorLabel,
                CellId = position.Cell,
                OriginX = position.PositionX,
                OriginY = position.PositionY,
                OriginZ = position.PositionZ,
                AnglesW = position.RotationW,
                AnglesX = position.RotationX,
                AnglesY = position.RotationY,
                AnglesZ = position.RotationZ,
                BiomeTags = new List<string>(),
                CooldownSeconds = 0
            };

            return true;
        }

        /// <summary>True when the request carries no source restriction, or its restriction names <paramref name="sourceId"/>.</summary>
        private static bool SourceAllowed(WorldEventRequest request, string sourceId)
        {
            return request.AllowedSourceIds == null || request.AllowedSourceIds.Contains(sourceId, StringComparer.Ordinal);
        }

        private static string Normalize(string id)
        {
            return id == null ? null : id.Trim().ToLowerInvariant();
        }

        private static string Unknown(string axis, string id, IEnumerable<string> known)
        {
            return $"unknown {axis} '{id}' (known: {Join(known)})";
        }

        private static string Join(IEnumerable<string> ids)
        {
            if (ids == null)
                return "none";

            var list = ids.Where(i => !string.IsNullOrEmpty(i)).OrderBy(i => i, StringComparer.Ordinal).ToList();

            return list.Count == 0 ? "none" : string.Join(", ", list);
        }
    }
}
