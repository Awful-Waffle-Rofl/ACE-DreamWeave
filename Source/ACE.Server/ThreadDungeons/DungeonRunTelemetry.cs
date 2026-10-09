using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers.Analytics;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One aggregated placement outcome on a run's ledger: N attempts that shared a
    /// (reason, wcid, role) triple. Immutable - <see cref="ThreadDungeonRun.SnapshotPlacements"/>
    /// materialises these from its live counters so the caller can never see a half-updated entry and the
    /// analytics queue never holds a reference to anything the run still writes to.
    /// </summary>
    public sealed class DungeonPlacementRecord
    {
        /// <summary>"plan" or "place" - see <see cref="DungeonRunTelemetry.Phases"/>.</summary>
        public string Phase { get; }

        public string Reason { get; }
        public uint Wcid { get; }
        public DungeonRole Role { get; }
        public int Attempts { get; }

        /// <summary>
        /// Did a plan entry that contributed attempts here nonetheless reach the world? Always false on a
        /// terminal reason - see <see cref="ThreadDungeonRun.MarkEntryPlaced"/> for why only a recovery
        /// reason can ever flip it.
        /// </summary>
        public bool EntryPlaced { get; }

        public bool IsFailure => DungeonRunTelemetry.IsFailure(Reason);

        public DungeonPlacementRecord(string phase, string reason, uint wcid, DungeonRole role, int attempts, bool entryPlaced)
        {
            Phase = phase;
            Reason = reason;
            Wcid = wcid;
            Role = role;
            Attempts = attempts;
            EntryPlaced = entryPlaced;
        }
    }

    /// <summary>
    /// The plan-time facts one run's population actually drew, captured once from
    /// <see cref="DungeonSpawnPlan"/> and carried by <see cref="ThreadDungeonRun.MarkPlan"/> so EndRun can
    /// project them onto dungeon_run_detail long after the plan and the run are both gone. Immutable, and
    /// pure with respect to the world - every value is copied off the plan at MarkPlan time, so the record
    /// holds no reference to anything the spawner still writes to.
    ///
    /// A run that never got as far as building a plan (population threw before <see cref="ThreadDungeonRun.MarkPlan"/>
    /// ever ran) has no record at all - <see cref="ThreadDungeonRun.PlanRecord"/> stays null - and
    /// dungeon_run_detail.plan_built is 0 with every plan-derived column NULL for that row.
    /// </summary>
    public sealed class DungeonRunPlanRecord
    {
        public string FamilyId { get; }
        public int TrashPlanned { get; }
        public int ElitePlanned { get; }
        public int EntriesPlanned { get; }
        public int UpliftedPlanned { get; }
        public int NaturalBandLow { get; }
        public int EffectiveBandLow { get; }
        public int BossLevel { get; }
        public bool BossNormalize { get; }
        public double BossHealthBase { get; }
        public uint PoolMaxBase { get; }
        public double HealthMultiplier { get; }
        public double BossHealthMultiplier { get; }
        public double HealthCurveTarget { get; }
        public double HealthNormalizeRatio { get; }
        public uint TrashHealthFloor { get; }
        public int BandStandardSamples { get; }
        public int DamageRating { get; }
        public int CritRating { get; }
        public int CritDamageRating { get; }
        public int DamageResistRating { get; }
        public int BossDamageRating { get; }
        public int BossDamageResistRating { get; }
        public double RunSpeedMult { get; }
        public double IgnoreShield { get; }

        /// <summary>
        /// The plan's real 0-1 hollow intensity (DungeonSpawnPlan.HollowIntensity), passed through directly.
        /// </summary>
        public double HollowIntensity { get; }

        public bool StripCombatTraits { get; }
        public double XpMultiplier { get; }
        public double BossXpMultiplier { get; }
        public double LumMultiplier { get; }
        public double XpScale { get; }
        public double LumScale { get; }
        public int LootTier { get; }
        public double LootQuantityMult { get; }

        public DungeonRunPlanRecord(string familyId, int trashPlanned, int elitePlanned, int entriesPlanned,
            int upliftedPlanned, int naturalBandLow, int effectiveBandLow, int bossLevel, bool bossNormalize,
            double bossHealthBase, uint poolMaxBase, double healthMultiplier, double bossHealthMultiplier,
            double healthCurveTarget, double healthNormalizeRatio, uint trashHealthFloor, int bandStandardSamples,
            int damageRating, int critRating, int critDamageRating, int damageResistRating, int bossDamageRating,
            int bossDamageResistRating, double runSpeedMult, double ignoreShield, double hollowIntensity,
            bool stripCombatTraits, double xpMultiplier, double bossXpMultiplier, double lumMultiplier,
            double xpScale, double lumScale, int lootTier, double lootQuantityMult)
        {
            FamilyId = familyId;
            TrashPlanned = trashPlanned;
            ElitePlanned = elitePlanned;
            EntriesPlanned = entriesPlanned;
            UpliftedPlanned = upliftedPlanned;
            NaturalBandLow = naturalBandLow;
            EffectiveBandLow = effectiveBandLow;
            BossLevel = bossLevel;
            BossNormalize = bossNormalize;
            BossHealthBase = bossHealthBase;
            PoolMaxBase = poolMaxBase;
            HealthMultiplier = healthMultiplier;
            BossHealthMultiplier = bossHealthMultiplier;
            HealthCurveTarget = healthCurveTarget;
            HealthNormalizeRatio = healthNormalizeRatio;
            TrashHealthFloor = trashHealthFloor;
            BandStandardSamples = bandStandardSamples;
            DamageRating = damageRating;
            CritRating = critRating;
            CritDamageRating = critDamageRating;
            DamageResistRating = damageResistRating;
            BossDamageRating = bossDamageRating;
            BossDamageResistRating = bossDamageResistRating;
            RunSpeedMult = runSpeedMult;
            IgnoreShield = ignoreShield;
            HollowIntensity = hollowIntensity;
            StripCombatTraits = stripCombatTraits;
            XpMultiplier = xpMultiplier;
            BossXpMultiplier = bossXpMultiplier;
            LumMultiplier = lumMultiplier;
            XpScale = xpScale;
            LumScale = lumScale;
            LootTier = lootTier;
            LootQuantityMult = lootQuantityMult;
        }
    }

    /// <summary>
    /// The vocabulary and the pure projections behind the dungeon_run analytics tables: the closed sets of
    /// reason codes and end states, the terminal-state mapping, and the run-to-row projection.
    ///
    /// Everything here is pure and side-effect free on purpose. The mapping in particular has to live in
    /// exactly ONE place: EndRun is not the only producer of an end reason (the /dd admin command supplies
    /// its own), and a mapping duplicated at the call sites would drift the first time a reason was added.
    /// Being pure is also what makes it unit-testable - the test harness cannot construct a live Player, and
    /// a PropertyManager read throws under it, so anything that needs a world cannot be covered.
    /// </summary>
    public static class DungeonRunTelemetry
    {
        /// <summary>
        /// WHERE a placement outcome came from. Two values, and they are part of the ledger's aggregation
        /// key rather than a decoration.
        ///
        /// The distinction is derivable from role plus reason today and will not stay that way: boss
        /// killability checking moves to plan time on an in-flight branch, at which point not_killable and
        /// not_creature become emittable from BOTH the builder and the spawner with the same reason string
        /// meaning different things - at plan time the content roster is bad, at place time the world
        /// refused a creature. Those have different owners and different fixes, so they must not merge.
        /// </summary>
        public static class Phases
        {
            /// <summary>Decided by the pure builder, before any object exists.</summary>
            public const string Plan = "plan";

            /// <summary>Decided by the spawner, while putting a creature into the world.</summary>
            public const string Place = "place";
        }

        /// <summary>
        /// The closed set of nine placement reason codes. See ace_analytics.sql's dungeon_run_placement
        /// comment for what each one means and which of them have no writer yet.
        /// </summary>
        public static class Reasons
        {
            /// <summary>WorldObjectFactory.CreateNewWorldObject threw.</summary>
            public const string CreateThrew = "create_threw";

            /// <summary>The wcid did not resolve to a Creature.</summary>
            public const string NotCreature = "not_creature";

            /// <summary>Not attackable, or flagged PlayerKillerStatus.NPC, so it could never satisfy the clear rule.</summary>
            public const string NotKillable = "not_killable";

            /// <summary>The spawn point is not on the run's own landblock.</summary>
            public const string OffLandblock = "off_landblock";

            /// <summary>EnterWorld returned false.</summary>
            public const string EnterWorldRefused = "enter_world_refused";

            /// <summary>EnterWorld threw.</summary>
            public const string EnterWorldThrew = "enter_world_threw";

            /// <summary>
            /// PLAN time, not placement time: a boss was wanted and none could be fielded, by the curated
            /// bosses.json window or by the run's own family. Carries wcid 0, because the failure is that
            /// there was no wcid to try.
            /// </summary>
            public const string NoCandidate = "no_candidate";

            /// <summary>
            /// RESERVED for the in-flight anchor-fallback branch, and the ONLY code that is a RECOVERY rather
            /// than a failure: the entry was retried at another anchor. No writer today.
            /// </summary>
            public const string AnchorFallbackUsed = "anchor_fallback_used";

            /// <summary>RESERVED for the in-flight anchor-fallback branch: every anchor was tried. No writer today.</summary>
            public const string FallbackExhausted = "fallback_exhausted";
        }

        /// <summary>Every legal reason code, in the order the DDL comment lists them. Exposed for tests and validation.</summary>
        public static readonly string[] AllReasons =
        {
            Reasons.CreateThrew,
            Reasons.NotCreature,
            Reasons.NotKillable,
            Reasons.OffLandblock,
            Reasons.EnterWorldRefused,
            Reasons.EnterWorldThrew,
            Reasons.NoCandidate,
            Reasons.AnchorFallbackUsed,
            Reasons.FallbackExhausted,
        };

        /// <summary>
        /// The closed set of seven end states written to dungeon_run.end_state.
        ///
        /// The last three are the abort family, and they are three states rather than one because the three
        /// causes have three different OWNERS: unwinnable is a content or data fault, underpopulated is a
        /// curation fault (typically a drifted spawn file), and timeout is a server or landblock fault.
        /// Collapsing them would destroy exactly the distinction the data is collected to draw, and the
        /// collapse could not be undone afterwards.
        /// </summary>
        public static class EndStates
        {
            public const string Cleared = "cleared";
            public const string Expired = "expired";
            public const string Abandoned = "abandoned";
            public const string Aborted = "aborted";

            /// <summary>
            /// RESERVED: a boss was planned and never placed, so the run is mathematically unwinnable.
            /// Content or data fault.
            /// </summary>
            public const string AbortedUnwinnable = "aborted_unwinnable";

            /// <summary>
            /// RESERVED: the trash that placed fell below the minimum population ratio. Winnable but
            /// threadbare - a curation fault, typically a spawn file that has drifted.
            /// </summary>
            public const string AbortedUnderpopulated = "aborted_underpopulated";

            /// <summary>
            /// RESERVED: population never completed inside the start timeout and the pending run was ended.
            /// Neither unwinnable nor thin, just stuck - a server or landblock fault.
            /// </summary>
            public const string AbortedTimeout = "aborted_timeout";
        }

        /// <summary>Every legal end state. Exposed for tests and validation.</summary>
        public static readonly string[] AllEndStates =
        {
            EndStates.Cleared,
            EndStates.Expired,
            EndStates.Abandoned,
            EndStates.Aborted,
            EndStates.AbortedUnwinnable,
            EndStates.AbortedUnderpopulated,
            EndStates.AbortedTimeout,
        };

        /// <summary>
        /// The end reasons ThreadDungeonManager itself passes to EndRun, named here so the mapping below
        /// and the producers cannot drift apart.
        ///
        /// <see cref="AdminEndPrefix"/> is a PREFIX match rather than a constant because the /dd command
        /// builds its reason as "ended by &lt;name&gt;" and that file is owned by another branch; matching the
        /// prefix is what lets this mapping recognise a deliberate abort without touching it.
        /// </summary>
        public static class EndReasons
        {
            public const string Expired = "expired";
            public const string LandblockUnloaded = "landblock unloaded";
            public const string LoadFailed = "landblock failed to load";
            public const string ClearedAndEmpty = "cleared and empty";
            public const string Superseded = "superseded by a new run";
            public const string AdminEndPrefix = "ended by ";

            /// <summary>
            /// The player gave the bound gem to the Fragment Press and confirmed the close
            /// (FragmentPressStation.HandleGive). Maps to <see cref="EndStates.Aborted"/>, the same state an
            /// admin /dd end produces, because both are a deliberate abort rather than a copy going away.
            ///
            /// The wording deliberately does NOT start with <see cref="AdminEndPrefix"/>: it must reach
            /// Aborted through its own branch so end_reason stays the field that separates "a player closed
            /// it" from "staff closed it". Equally it must not fall through to the default, which is
            /// Abandoned and would file a deliberate choice as the copy simply going away.
            ///
            /// No eighth end state was added for it. The abort family is split by FAULT OWNER (content,
            /// curation, server) and a player's own decision is not a fault, so an eighth member would break
            /// that reading of the column.
            /// </summary>
            public const string ClosedByOwner = "closed by owner";

            /// <summary>
            /// A ROSTER MEMBER gave their Thread Key to the Fragment Press and confirmed the close, which ends the
            /// run for the whole fellowship (owner ruling, 2026-09-17, reversing R7/R32: a member's key used to be
            /// destroyed on its own and leave the run open). Maps to <see cref="EndStates.Aborted"/>, exactly as
            /// <see cref="ClosedByOwner"/> does, and for the same reason: it is a deliberate abort rather than a
            /// copy going away.
            ///
            /// A separate REASON rather than a reuse of ClosedByOwner, because the two answer a question the
            /// column would otherwise lose: whether the person who ended a group run was the one who paid for it.
            /// Still no new end STATE, for the reason ClosedByOwner gives - the abort family is split by fault
            /// owner, and a player's own decision is not a fault.
            /// </summary>
            public const string ClosedByMember = "closed by member";

            /// <summary>
            /// The Thread puzzle fail policy removed the run's last character inside (a solo run's owner, or the
            /// last member inside a group run) after their connection reached the wrong-pull threshold
            /// (ThreadPuzzleFailPolicy). Maps to <see cref="EndStates.Aborted"/> through its own explicit branch,
            /// not the Abandoned default: the run was ended on purpose, as a penalty, not left to go away.
            /// </summary>
            public const string PuzzleLockout = "puzzle lockout";

            /// <summary>
            /// RESERVED, no producer today: a boss was planned and never placed, so the run cannot be won.
            /// Maps to <see cref="EndStates.AbortedUnwinnable"/>.
            /// </summary>
            public const string Unwinnable = "unwinnable";

            /// <summary>
            /// RESERVED, no producer today: the trash that placed fell below the minimum population ratio.
            /// Maps to <see cref="EndStates.AbortedUnderpopulated"/>.
            /// </summary>
            public const string Underpopulated = "underpopulated";

            /// <summary>
            /// RESERVED, no producer today: population never completed inside the start timeout, so the
            /// pending run was abandoned before the player was let in. Maps to
            /// <see cref="EndStates.AbortedTimeout"/>.
            ///
            /// Deliberately NOT the same thing as <see cref="LoadFailed"/>, and the two must not be fused.
            /// LoadFailed is the reap of a run whose LANDBLOCK never finished CreateWorldObjects, which
            /// happens to a registered run the player may already be sitting in front of, and it maps to
            /// 'abandoned'. This one is the retry loop giving up on POPULATION before any of that, and the
            /// player is never teleported in at all.
            /// </summary>
            public const string PopulateTimeout = "populate timeout";
        }

        /// <summary>
        /// True for every reason code except <see cref="Reasons.AnchorFallbackUsed"/>, which records a
        /// recovery rather than a loss. A panel counting placement failures filters on this; summing
        /// attempts across the whole table without it mixes a recovery in with the failures it recovered
        /// from.
        ///
        /// Terminality coincides with failure today and the two are deliberately NOT separate predicates:
        /// all eight failure codes end the plan entry they refused, and the one recovery code is the only
        /// one that leaves an entry still able to place. If a future code is a failure that is nonetheless
        /// retryable, split them then - inventing the distinction now would leave an untested branch.
        /// </summary>
        public static bool IsFailure(string reason)
            => !string.Equals(reason, Reasons.AnchorFallbackUsed, StringComparison.Ordinal);

        /// <summary>
        /// The ONE mapping from a run's terminal state plus its end reason onto the closed end_state set.
        ///
        /// Cleared wins over everything: a run the player finished is 'cleared' however it was later
        /// disposed of, including an admin /dd end or a TTL expiry that arrived afterwards. That is why
        /// EndRun reads the state the run held BEFORE MarkEnded overwrote it, and why MarkEnded hands that
        /// value back atomically rather than the caller reading State first (a landblock thread can land the
        /// clearing kill between the two).
        ///
        /// 'abandoned' is the default for an unrecognised reason, and that choice is deliberate: it is the
        /// bucket that claims the least. A reason nobody taught this function about is far more likely to be
        /// the copy going away than a deliberate abort, and reading it as 'aborted' would put a
        /// player-driven meaning on a mechanical event.
        /// </summary>
        public static string EndStateFor(ThreadDungeonRunState priorState, string endReason)
        {
            if (priorState == ThreadDungeonRunState.Cleared)
                return EndStates.Cleared;

            var reason = endReason ?? string.Empty;

            if (string.Equals(reason, EndReasons.Expired, StringComparison.Ordinal))
                return EndStates.Expired;

            // The abort family. Three states rather than one because the three causes have three different
            // owners - content, curation, server - and a dashboard that could not separate them would be
            // measuring nothing actionable. All three are reserved; the retry branch is what writes them.
            if (string.Equals(reason, EndReasons.Unwinnable, StringComparison.Ordinal))
                return EndStates.AbortedUnwinnable;

            if (string.Equals(reason, EndReasons.Underpopulated, StringComparison.Ordinal))
                return EndStates.AbortedUnderpopulated;

            if (string.Equals(reason, EndReasons.PopulateTimeout, StringComparison.Ordinal))
                return EndStates.AbortedTimeout;

            if (string.Equals(reason, EndReasons.LandblockUnloaded, StringComparison.Ordinal)
                || string.Equals(reason, EndReasons.LoadFailed, StringComparison.Ordinal))
                return EndStates.Abandoned;

            // ABOVE the admin prefix check, and an exact match rather than a prefix. A player closing a run at the
            // Fragment Press is an abort in the same sense a /dd end is, so it shares the state; end_reason is what
            // keeps the three apart on the row. Owner and member share the state deliberately: who pressed the
            // button is a question for end_reason, not for the state column.
            if (string.Equals(reason, EndReasons.ClosedByOwner, StringComparison.Ordinal)
                || string.Equals(reason, EndReasons.ClosedByMember, StringComparison.Ordinal))
                return EndStates.Aborted;

            // The puzzle fail policy's removal: a deliberate end (a penalty), so Aborted, never the Abandoned default.
            if (string.Equals(reason, EndReasons.PuzzleLockout, StringComparison.Ordinal))
                return EndStates.Aborted;

            if (reason.StartsWith(EndReasons.AdminEndPrefix, StringComparison.Ordinal))
                return EndStates.Aborted;

            // ClearedAndEmpty and Superseded are deliberately absent: both are only ever produced for a run
            // that IS Cleared, so the first branch has already answered them. Reaching here with one of them
            // would mean the prior state was not Cleared, which is a bug rather than a state worth naming,
            // and the honest answer for it is the least-claiming bucket below.
            return EndStates.Abandoned;
        }

        /// <summary>
        /// Trims a value to its column width. Every string column in dungeon_run is fixed-width and MySQL in
        /// strict mode ERRORS on an overlong value rather than truncating it, which would roll back the whole
        /// batch and lose every run in it. Clipping at the producer is the same choice RecordChat makes for
        /// chat_event.message: truncate, never reject.
        /// </summary>
        internal static string Clip(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Length <= max ? value : value.Substring(0, max);
        }

        /// <summary>
        /// Projects a freshly built <see cref="DungeonSpawnPlan"/> onto its immutable plan-time record. Pure
        /// and side-effect free, on the same contract as <see cref="BuildRow"/>: every value is copied off
        /// the plan, so the record can outlive it and be handed to a run from any thread.
        ///
        /// trash_planned / elite_planned are counted here rather than carried on the plan itself, on the same
        /// footing as entries_planned and uplifted_planned below (the log line at ThreadDungeonSpawner.cs
        /// computes uplifted_planned the identical way) - a filter over Entries by an existing field, not new
        /// builder logic.
        /// </summary>
        internal static DungeonRunPlanRecord BuildPlanRecord(DungeonSpawnPlan plan)
        {
            if (plan == null)
                return null;

            return new DungeonRunPlanRecord(
                plan.FamilyId == null ? null : Clip(plan.FamilyId, 32),
                plan.Entries.Count(e => e.Role == DungeonRole.Trash),
                plan.Entries.Count(e => e.Role == DungeonRole.Elite),
                plan.Entries.Count,
                plan.Entries.Count(e => e.UpliftLevel > 0),
                plan.NaturalBandLow,
                plan.EffectiveBandLow,
                plan.BossLevel,
                plan.BossNormalize,
                plan.BossHealthBase,
                plan.PoolMaxBase,
                plan.HealthMultiplier,
                plan.BossHealthMultiplier,
                plan.HealthCurveTarget,
                plan.HealthNormalizeRatio,
                plan.TrashHealthFloor,
                plan.BandStandard?.SampleCount ?? 0,
                plan.DamageRating,
                plan.CritRating,
                plan.CritDamageRating,
                plan.DamageResistRating,
                plan.BossDamageRating,
                plan.BossDamageResistRating,
                plan.RunSpeedMult,
                plan.IgnoreShield,
                plan.HollowIntensity,
                plan.StripCombatTraits,
                plan.XpMultiplier,
                plan.BossXpMultiplier,
                plan.LumMultiplier,
                plan.XpScale,
                plan.LumScale,
                plan.Profile?.Tier ?? 0,
                plan.LootQuantityMult);
        }

        /// <summary>
        /// Projects a finished run onto its analytics row, children included. Pure with respect to the world:
        /// every value comes from the run itself or from a scalar the caller resolved, and the result holds
        /// no reference to the run, so the writer thread can serialise it long after the copy is gone.
        ///
        /// Modifier ids are de-duplicated because dungeon_run_modifier is keyed on (run_fk, modifier_id): a
        /// duplicate would throw and take the whole transaction with it. DungeonGemSpec.TryParse already
        /// refuses a repeated id, but its constructor does not, so the guard sits here rather than resting on
        /// an invariant enforced on only one of the two paths in.
        /// </summary>
        internal static AnalyticsDatabase.DungeonRunRow BuildRow(ThreadDungeonRun run, ThreadDungeonRunState priorState,
            string endReason, DateTime endedUtc, int charLevel)
        {
            var spec = run.Spec;

            var modifiers = new List<AnalyticsDatabase.DungeonRunModifierRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var m in spec?.Modifiers ?? Enumerable.Empty<(string Id, double Magnitude)>())
            {
                var id = Clip(m.Id, 32);

                if (id.Length == 0 || !seen.Add(id))
                    continue;

                modifiers.Add(new AnalyticsDatabase.DungeonRunModifierRow { ModifierId = id, Magnitude = m.Magnitude });
            }

            var participants = run.SnapshotRoster()
                .Select(m => new AnalyticsDatabase.DungeonRunParticipantRow
                {
                    CharacterId = m.Guid,
                    Name = Clip(m.Name, 64),
                    IsOwner = m.IsOwner,
                    LevelStart = m.LevelAtStart,
                    LevelEnd = m.IsOwner && m.LevelEnd == 0 ? charLevel : m.LevelEnd,
                    Entered = m.Entered,
                    SecondsInside = m.SecondsInside,
                    XpGained = m.XpGained,
                    LumGained = m.LumGained,
                    PilesReceived = m.PilesReceived,
                    PilesForfeited = m.PilesForfeited,
                    SurveyFiled = m.SurveyFiled,
                    KeyGranted = m.KeyGranted,
                })
                .ToList();

            var group = run.IsGroup
                ? new AnalyticsDatabase.DungeonRunGroupRow
                {
                    RosterSize = run.Group.RosterSize,
                    Effort = run.Group.Effort,
                    CountMult = run.GroupCountActual,
                    CountMultTarget = run.Group.CountTarget,
                    HealthMult = run.GroupHealthMult,
                    DamageRatingBonus = run.Group.DamageRatingBonus,
                    RewardBonus = run.Group.RewardBonus,
                    ShareTotal = run.Group.ShareTotal,
                }
                : null;

            var planRecord = run.PlanRecord;
            var clearedUtc = run.ClearedUtc;

            var detail = new AnalyticsDatabase.DungeonRunDetailRow
            {
                Source = "live",
                PlanBuilt = planRecord != null,
                ClearedSecs = clearedUtc.HasValue ? (double?)(clearedUtc.Value - run.StartedUtc).TotalSeconds : null,
                FamilyId = planRecord?.FamilyId,
                TrashPlanned = planRecord?.TrashPlanned,
                ElitePlanned = planRecord?.ElitePlanned,
                EntriesPlanned = planRecord?.EntriesPlanned,
                UpliftedPlanned = planRecord?.UpliftedPlanned,
                NaturalBandLow = planRecord?.NaturalBandLow,
                EffectiveBandLow = planRecord?.EffectiveBandLow,
                BossLevel = planRecord?.BossLevel,
                BossNormalize = planRecord?.BossNormalize,
                BossHealthBase = planRecord?.BossHealthBase,
                PoolMaxBase = planRecord?.PoolMaxBase,
                HealthMultiplier = planRecord?.HealthMultiplier,
                BossHealthMultiplier = planRecord?.BossHealthMultiplier,
                HealthCurveTarget = planRecord?.HealthCurveTarget,
                HealthNormalizeRatio = planRecord?.HealthNormalizeRatio,
                TrashHealthFloor = planRecord?.TrashHealthFloor,
                BandStandardSamples = planRecord?.BandStandardSamples,
                DamageRating = planRecord?.DamageRating,
                CritRating = planRecord?.CritRating,
                CritDamageRating = planRecord?.CritDamageRating,
                DamageResistRating = planRecord?.DamageResistRating,
                BossDamageRating = planRecord?.BossDamageRating,
                BossDamageResistRating = planRecord?.BossDamageResistRating,
                RunSpeedMult = planRecord?.RunSpeedMult,
                IgnoreShield = planRecord?.IgnoreShield,
                HollowIntensity = planRecord?.HollowIntensity,
                StripCombatTraits = planRecord?.StripCombatTraits,
                XpMultiplier = planRecord?.XpMultiplier,
                BossXpMultiplier = planRecord?.BossXpMultiplier,
                LumMultiplier = planRecord?.LumMultiplier,
                XpScale = planRecord?.XpScale,
                LumScale = planRecord?.LumScale,
                LootTier = planRecord?.LootTier,
                LootQuantityMult = planRecord?.LootQuantityMult,
            };

            var placements = run.SnapshotPlacements()
                .Select(p => new AnalyticsDatabase.DungeonRunPlacementRow
                {
                    Phase = Clip(p.Phase, 8),
                    Reason = Clip(p.Reason, 32),
                    IsFailure = p.IsFailure,
                    EntryPlaced = p.EntryPlaced,
                    // RESERVED, no writer. See ace_analytics.sql.
                    Credited = false,
                    Wcid = p.Wcid,
                    Role = Clip(p.Role.ToString(), 16),
                    Attempts = p.Attempts,
                })
                .ToList();

            return new AnalyticsDatabase.DungeonRunRow
            {
                RunId = run.RunId,
                StartGroup = Clip(run.StartGroup, 32),
                StartedUtc = run.StartedUtc,
                EndedUtc = endedUtc,
                DurationSecs = (endedUtc - run.StartedUtc).TotalSeconds,
                EndState = EndStateFor(priorState, endReason),
                EndReason = Clip(endReason, 64),
                Entered = run.PlayerEverObserved,
                CharacterId = run.OwnerGuid,
                Name = Clip(run.OwnerName, 64),
                CharLevelStart = run.OwnerLevelAtStart,
                CharLevel = charLevel,
                DungeonId = Clip(run.Dungeon?.Id, 16),
                GemLevel = spec?.Level ?? 0,
                Tier = spec?.Tier ?? 0,
                Family = Clip(spec?.Family, 32),
                // RETIRED with the instability mechanic (owner ruling, 2026-09-07). The ace_analytics column
                // is deliberately left in place - its schema freezes on first deploy and has no ALTER path -
                // so rows already written keep their historical values and every new row records 0.
                Instability = 0,
                Presses = spec?.Presses ?? 0,
                Seed = spec?.Seed ?? 0,
                // RESERVED, no writer. See ace_analytics.sql.
                StartAttempts = 0,
                PopulateReached = run.PopulateReached,
                PopulateMs = run.PopulateMs,
                Planned = run.Planned,
                Spawned = run.Spawned,
                Killed = run.Killed,
                ClearTarget = run.ClearTarget,
                BossWcidIntended = run.BossWcidIntended,
                BossWcidPlaced = run.BossWcid,
                BossKilled = run.BossKilled,
                BossHealthRatio = run.BossHealthRatio,
                BossHealthClamped = run.BossHealthClamped,
                SurveyFiled = run.SurveyFiled,
                // RESERVED, no writer. See ace_analytics.sql.
                CreditedKills = 0,
                XpGained = run.XpEarned,
                LumGained = run.LumEarned,
                Modifiers = modifiers,
                Placements = placements,
                Participants = participants,
                Group = group,
                Detail = detail,
            };
        }
    }
}
