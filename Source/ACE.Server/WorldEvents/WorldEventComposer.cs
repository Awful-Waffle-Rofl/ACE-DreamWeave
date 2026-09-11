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
    /// Entirely pure - no logging, no manager state, no side effects - so the whole refusal matrix is unit
    /// testable (D6) and so "/worldevent simulate" and "--dry" can run it without touching a live event.
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
        /// How many families one run may compose (two-family composition, 2026-08-29). A RANDOM run always
        /// draws exactly two; an explicit "--family" may name one or two and is never constrained further.
        /// </summary>
        public const int MaxComposedFamilies = 2;

        /// <summary>
        /// How many of the shuffled candidate family pairs a random composition crosses with the
        /// (source, goal) list before giving up. See <see cref="TryComposeRandom"/> for why this is small.
        /// </summary>
        public const int MaxPairsTried = 8;

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

            // One token, ids joined with "+", used by every refusal message and log line that names the
            // composed families - the same shape WorldEventComposition.ToAxisSummary prints.
            var familyLabel = string.Join("+", familyIds);

            if (!store.Sources.TryGetValue(sourceId, out var source))
            {
                error = Unknown("source", sourceId, store.Sources.Keys);
                return false;
            }

            if (!store.Goals.TryGetValue(goalId, out var goal))
            {
                error = Unknown("goal", goalId, store.Goals.Keys);
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

            // A named boss MAY be bound to one family (BOSS-STANDARD.md section 5). The binding is
            // optional; an empty FamilyId means "any family". A binding that matches NONE of the composed
            // families is a refusal in the same shape as the goal/source compatibility check above - with
            // two composed families, matching EITHER is enough, since the boss stands over a crowd drawn
            // from the union of both. Both sides are already normalised ids - the request's families by
            // Normalize, the boss's by the store's IdPattern - so an ordinal compare is right.
            //
            // This check is skipped when the request asked for "auto": TryResolveAutoBoss already chose
            // among the composed families' own bound bosses when one existed, and falls back to a boss
            // bound to a DIFFERENT family (or none) only when neither family has a binding at all - that
            // fallback pick is deliberate (owner decision 2026-08-16, "a random Named boss is fine"), not
            // a mismatch to refuse.
            if (!bossWasAuto && boss.Kind == BossKind.Named && !string.IsNullOrEmpty(boss.FamilyId)
                && !familyIds.Contains(boss.FamilyId, StringComparer.Ordinal))
            {
                error = $"boss '{bossId}' is not compatible with family '{familyLabel}' (compatible: {boss.FamilyId})";
                return false;
            }

            if (!TryResolveAnchor(store, request, out var anchor, out var anchorPosition, out error))
                return false;

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
        /// Both lists are shuffled once (Fisher-Yates) with the caller's rng before search, so a seeded rng
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
        /// </summary>
        private static bool TryComposeRandom(WorldEventAxisStore store, WorldEventCatalog catalog,
            WorldEventRequest request, out WorldEventComposition composition, out string error, Random rng)
        {
            composition = null;
            error = null;

            rng = rng ?? new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

            var requestedSource = string.IsNullOrWhiteSpace(request.SourceId) ? null : Normalize(request.SourceId);
            var requestedGoal = string.IsNullOrWhiteSpace(request.GoalId) ? null : Normalize(request.GoalId);

            var pinnedFamilies = (request.FamilyIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(Normalize)
                .ToList();

            var pairs = new List<(string sourceId, string goalId)>();

            foreach (var source in store.Sources.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                if (requestedSource != null && source.Id != requestedSource)
                    continue;

                var compatibleGoals = (source.CompatibleGoals ?? new List<string>())
                    .Where(store.Goals.ContainsKey)
                    .OrderBy(g => g, StringComparer.Ordinal);

                foreach (var goalId in compatibleGoals)
                {
                    if (requestedGoal != null && goalId != requestedGoal)
                        continue;

                    pairs.Add((source.Id, goalId));
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
                    error = "random composition: no family in the catalog has any members";
                    return false;
                }

                var probeSourceId = requestedSource ?? store.Sources.Keys.OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault();
                var probeGoalId = requestedGoal ?? store.Goals.Keys.OrderBy(g => g, StringComparer.Ordinal).FirstOrDefault();
                var probeFamilyIds = familyCandidates.FirstOrDefault() ?? pinnedFamilies;

                var probe = ClonePinned(request, probeSourceId, probeFamilyIds, probeGoalId, request.BossId);

                TryCompose(store, catalog, probe, out _, out error);

                if (string.IsNullOrEmpty(error))
                    error = "no compatible source/family/goal combination was found";

                return false;
            }

            Shuffle(pairs, rng);
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

                    return true;
                }

                lastError = error;
            }

            composition = null;
            error = lastError != null
                ? $"{lastError}; no compatible source/family/goal combination was found"
                : "no compatible source/family/goal combination was found";

            return false;
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

            position = request.AnchorPosition;

            anchor = new AnchorDef
            {
                Id = HereAnchorId,
                DisplayName = string.IsNullOrWhiteSpace(request.AnchorLabel) ? HereAnchorId : request.AnchorLabel,
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
