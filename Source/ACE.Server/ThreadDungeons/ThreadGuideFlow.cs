using System;
using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The player-shaped surface the Thread-Guide flows act on. Production wraps a live Player
    /// (ThreadGuideStation.PlayerGuideHolder); the tests wrap a fake, because ACE.Server.Tests cannot build a
    /// live Player. Every state change the flows make goes through this, so a test sees all of it.
    /// </summary>
    public interface IGuideHolder
    {
        uint Guid { get; }

        /// <summary>PropertyInt.ThreadGuideLevel: the highest rung won, 0 when none.</summary>
        int GuideLevel { get; set; }

        /// <summary>PropertyInt.ThreadGuideGrantSerial: the current grant's serial, 0 before the first grant.</summary>
        int GrantSerial { get; set; }

        /// <summary>True when the holder's inventory (pack and side packs) holds a guide item that IsCurrent.</summary>
        bool HasOutstandingGuideItem();

        /// <summary>The pack pre-flight for one guide fragment. False when it would not fit.</summary>
        bool HasRoomForFragment();

        /// <summary>
        /// Builds the guide fragment for <paramref name="tag"/> (ThreadGuideItems.CreateFragment, the ONE builder)
        /// and puts it in the pack. False, with nothing left behind, when either step fails.
        /// </summary>
        bool TryGiveFragment(GuideTag tag);

        /// <summary>GrantLevelProportionalXp(percent, 0, cap). Only ever called with cap greater than 0.</summary>
        void GrantXp(double percent, long cap);

        /// <summary>Persists the holder now (Player.SaveBiotaToDatabase), so a written rung or serial survives a crash.</summary>
        void Save();

        /// <summary>
        /// Destroys every guide item in the inventory (pack and side packs) that IsGuide but not IsCurrent and
        /// returns how many went. Current items are never touched. See <see cref="ThreadGuideFlow.FindStaleGuideItems"/>.
        /// </summary>
        int RemoveStaleGuideItems();
    }

    /// <summary>What <see cref="ThreadGuideFlow.Issue"/> did.</summary>
    public enum GuideIssueOutcome
    {
        /// <summary>The fragment is in the pack and the serial is stamped.</summary>
        Given,

        /// <summary>The pre-flight refused: nothing was built, the serial is unchanged.</summary>
        NoRoom,

        /// <summary>The build or the give failed: nothing was given, the serial is unchanged.</summary>
        GiveFailed,

        /// <summary>The guide is switched off: nothing was attempted (fail seam only).</summary>
        Disabled,
    }

    /// <summary>The Press's answer for one fragment. See <see cref="ThreadGuideFlow.DecidePress"/>.</summary>
    public sealed class GuidePressDecision
    {
        /// <summary>The line to say and refuse with, or null to proceed.</summary>
        public string Refusal { get; }

        /// <summary>The Trade Note fee to charge. 0 for every guide fragment.</summary>
        public int Fee { get; }

        /// <summary>PressLimits.GuaranteedTypes: every type the rung demands (empty for none).</summary>
        public IReadOnlyList<string> GuaranteedTypes { get; }

        /// <summary>The type whose T-EXPLAIN goes out after a successful press, or null.</summary>
        public string ExplainType { get; }

        /// <summary>True for a guide fragment: the gem must carry the tag and be bound like the fragment.</summary>
        public bool IsGuide { get; }

        public GuidePressDecision(string refusal, int fee, IReadOnlyList<string> guaranteedTypes, string explainType, bool isGuide)
        {
            Refusal = refusal;
            Fee = fee;
            GuaranteedTypes = guaranteedTypes ?? Array.Empty<string>();
            ExplainType = explainType;
            IsGuide = isGuide;
        }
    }

    /// <summary>The gem handler's answer for one use. See <see cref="ThreadGuideFlow.DecideGemUse"/>.</summary>
    public sealed class GuideGemUseDecision
    {
        /// <summary>The line to say and refuse with, or null to proceed.</summary>
        public string Refusal { get; }

        /// <summary>The level floor DungeonGemRules.Decide applies to an unbound gem.</summary>
        public int MinPlayerLevel { get; }

        /// <summary>True for a guide gem: StartSoloRun, never the group offer.</summary>
        public bool ForceSolo { get; }

        /// <summary>True when the player is fellowed and must be told T-SOLO.</summary>
        public bool TellSolo { get; }

        public GuideGemUseDecision(string refusal, int minPlayerLevel, bool forceSolo, bool tellSolo)
        {
            Refusal = refusal;
            MinPlayerLevel = minPlayerLevel;
            ForceSolo = forceSolo;
            TellSolo = tellSolo;
        }
    }

    /// <summary>
    /// The Thread-Guide's game-side decisions and flows, in one place and free of engine reads: every input is
    /// handed in (tunables, the holder, the run state), so each one is unit tested directly. The impure shells
    /// that read PropertyManager and resolve players are FragmentPressStation.Press,
    /// ThreadDungeonGemHandler.TryHandleUse, ThreadGuideStation and the ThreadDungeonManager seams.
    ///
    /// THE INVARIANTS this class is the enforcement point for:
    ///   - guide detection is ThreadGuideRules.IsGuide and the stale check is ThreadGuideRules.IsCurrent, called
    ///     the same way by the Press (<see cref="DecidePress"/>), gem use (<see cref="DecideGemUse"/>) and the
    ///     NPC's outstanding scan (<see cref="HasOutstandingGuideItem"/>);
    ///   - the special type and the tier come from ThreadGuideLadder only;
    ///   - XP is paid only by <see cref="ApplyClear"/>, and only when the cap is greater than 0;
    ///   - progress (the rung, the serial) is written before any message goes out.
    /// </summary>
    public static class ThreadGuideFlow
    {
        /// <summary>Entries on a freshly issued guide fragment: the shipped Raw Fragment weenies' 3 of 3.</summary>
        public const int FragmentEntries = 3;

        // ---------------- Press ----------------

        /// <summary>
        /// The Press gate. A non-guide fragment comes back unchanged: <paramref name="configuredFee"/>, no
        /// guarantee, no explanation. A guide fragment is refused T-STALE when it is not current, refused
        /// T-NEEDS-&lt;type&gt; for the FIRST of its rung's required types (ThreadGuideLadder.RequiredTypes, in
        /// order) with no dose loaded - so rung 150 says T-NEEDS-Taper before T-NEEDS-Talisman - and otherwise
        /// pressed free with every required type guaranteed. Called BEFORE the room check and the fee.
        /// </summary>
        public static GuidePressDecision DecidePress(DungeonGemSpec spec, uint holderGuid, int currentSerial,
            Func<string, int> dosesOfType, int configuredFee)
        {
            if (!ThreadGuideRules.IsGuide(spec))
                return new GuidePressDecision(null, configuredFee, null, null, false);

            if (!ThreadGuideRules.IsCurrent(spec, holderGuid, currentSerial))
                return new GuidePressDecision(ThreadGuideText.Stale, 0, null, null, true);

            var required = ThreadGuideLadder.RequiredTypes(spec.Guide.Rung);

            foreach (var type in required)
                if (dosesOfType == null || dosesOfType(type) <= 0)
                    return new GuidePressDecision(ThreadGuideText.ComposeNeeds(type), 0, null, null, true);

            return new GuidePressDecision(null, 0, required, ThreadGuideLadder.RequiredType(spec.Guide.Rung), true);
        }

        // ---------------- gem use ----------------

        /// <summary>
        /// The gem-use gate. A non-guide gem keeps <paramref name="configuredMinLevel"/> and the normal
        /// solo/group choice. A guide gem is refused T-STALE when not current; otherwise its level floor is
        /// ThreadGuideRules.MinPlayerLevel and it always opens solo, with T-SOLO for a fellowed player.
        /// </summary>
        public static GuideGemUseDecision DecideGemUse(DungeonGemSpec spec, uint holderGuid, int currentSerial,
            int configuredMinLevel, bool fellowed)
        {
            if (!ThreadGuideRules.IsGuide(spec))
                return new GuideGemUseDecision(null, configuredMinLevel, false, false);

            if (!ThreadGuideRules.IsCurrent(spec, holderGuid, currentSerial))
                return new GuideGemUseDecision(ThreadGuideText.Stale, ThreadGuideRules.MinPlayerLevel, true, false);

            return new GuideGemUseDecision(null, ThreadGuideRules.MinPlayerLevel, true, fellowed);
        }

        // ---------------- NPC ----------------

        /// <summary>
        /// The one guide fragment spec: dg=any, lvl=rung, the ladder's tier, fam=any, seed 0, no modifiers, and
        /// the guide tag. Throws ArgumentException for a level that is not a rung.
        /// </summary>
        public static DungeonGemSpec BuildFragmentSpec(GuideTag tag)
        {
            if (tag == null) throw new ArgumentNullException(nameof(tag));

            var rung = ThreadGuideLadder.Get(tag.Rung) ?? throw new ArgumentException($"{tag.Rung} is not a Thread-Guide rung", nameof(tag));
            var tier = rung.Tier ?? throw new InvalidOperationException($"Thread-Guide rung {tag.Rung} has no tier");

            return new DungeonGemSpec(DungeonGemSpec.Any, tag.Rung, tier, DungeonGemSpec.Any, 0, null, 0, 0, guide: tag);
        }

        /// <summary>
        /// Issues one guide fragment for <paramref name="rung"/>: pre-flight, then build and give with serial
        /// (current + 1), then STAMP that serial - only on a successful give, so a failed give leaves the old
        /// serial (and any item it still validates) untouched. Shared by the NPC and the fail regrant.
        /// </summary>
        public static GuideIssueOutcome Issue(IGuideHolder holder, int rung)
        {
            if (!holder.HasRoomForFragment())
                return GuideIssueOutcome.NoRoom;

            var newSerial = Math.Max(0, holder.GrantSerial) + 1;

            if (!holder.TryGiveFragment(new GuideTag(rung, newSerial, holder.Guid)))
                return GuideIssueOutcome.GiveFailed;

            holder.GrantSerial = newSerial;
            holder.Save();
            return GuideIssueOutcome.Given;
        }

        /// <summary>
        /// One NPC visit: first take back every faded (stale) guide item, saying so once; then DecideGrant, and
        /// either the refusal tell or Issue followed by the grant tell and the steps popup. The serial is stamped
        /// and saved inside Issue, before either message. <paramref name="enabled"/> is
        /// <see cref="GuideAvailable"/>: Threads, the Press and the guide all switched on.
        /// </summary>
        public static GuideGrantDecision HandleNpcUse(IGuideHolder holder, bool enabled, int playerLevel, bool hasLiveGuideRun,
            Action<string> tell, Action<string> popup)
        {
            if (holder.RemoveStaleGuideItems() > 0)
                tell?.Invoke(ThreadGuideText.TakesBackFaded);

            var decision = ThreadGuideRules.DecideGrant(enabled, playerLevel, holder.GuideLevel, holder.HasOutstandingGuideItem(), hasLiveGuideRun);

            if (!decision.Granted)
            {
                tell?.Invoke(ThreadGuideText.ComposeRefusal(decision.Reason));
                return decision;
            }

            switch (Issue(holder, decision.Rung))
            {
                case GuideIssueOutcome.Given:
                    tell?.Invoke(ThreadGuideText.ComposeGrant(decision.Rung));
                    popup?.Invoke(ThreadGuideText.ComposePopup(decision.Rung));
                    break;

                case GuideIssueOutcome.NoRoom:
                    tell?.Invoke(ThreadGuideText.RefusePackFull);
                    break;

                default:
                    tell?.Invoke(ThreadGuideText.RefuseDisabled);
                    break;
            }

            return decision;
        }

        /// <summary>
        /// The NPC's "outstanding item" test: a guide-tagged fragment or gem anywhere in
        /// <paramref name="topLevel"/> (the pack) or inside any container in it (side packs) that IsCurrent for
        /// the holder. A stale copy does not count, so it cannot wedge the NPC. An unreadable spec is ignored.
        /// </summary>
        public static bool HasOutstandingGuideItem(IEnumerable<WorldObject> topLevel, uint holderGuid, int currentSerial)
        {
            if (topLevel == null)
                return false;

            foreach (var item in topLevel)
            {
                if (item == null)
                    continue;

                if (IsCurrentGuideItem(item, holderGuid, currentSerial))
                    return true;

                if (item is Container container && HasOutstandingGuideItem(container.Inventory.Values, holderGuid, currentSerial))
                    return true;
            }

            return false;
        }

        private static bool IsCurrentGuideItem(WorldObject item, uint holderGuid, int currentSerial)
            => TryReadSpec(item, out var spec) && ThreadGuideRules.IsCurrent(spec, holderGuid, currentSerial);

        private static bool TryReadSpec(WorldObject item, out DungeonGemSpec spec)
        {
            spec = null;
            var text = item.GetProperty(PropertyString.DungeonGemSpec);
            return !string.IsNullOrEmpty(text) && DungeonGemSpec.TryParse(text, out spec, out _);
        }

        /// <summary>
        /// Every guide item in <paramref name="topLevel"/> or any container in it that IsGuide but NOT IsCurrent
        /// for the holder: superseded copies, which are Attuned (cannot be dropped) and refused at the Press, so
        /// the NPC takes them back. A current guide item, a normal item and an unreadable spec are never listed.
        /// </summary>
        public static List<WorldObject> FindStaleGuideItems(IEnumerable<WorldObject> topLevel, uint holderGuid, int currentSerial)
        {
            var stale = new List<WorldObject>();
            CollectStale(topLevel, holderGuid, currentSerial, stale);
            return stale;
        }

        private static void CollectStale(IEnumerable<WorldObject> items, uint holderGuid, int currentSerial, List<WorldObject> stale)
        {
            if (items == null)
                return;

            foreach (var item in items)
            {
                if (item == null)
                    continue;

                if (TryReadSpec(item, out var spec) && ThreadGuideRules.IsGuide(spec) && !ThreadGuideRules.IsCurrent(spec, holderGuid, currentSerial))
                    stale.Add(item);

                if (item is Container container)
                    CollectStale(container.Inventory.Values, holderGuid, currentSerial, stale);
            }
        }

        /// <summary>
        /// A live guide run for the NPC's DecideGrant: a run owned by <paramref name="ownerGuid"/> whose spec
        /// IsGuide and which is still Starting or Active. A Cleared run does NOT count: its rung has been won
        /// (or, for a nothing-spawned run, deliberately not won), its gem crumbled at the clear, and it only
        /// stands until the reap - counting it would hold the next fragment for minutes after every clear.
        /// </summary>
        internal static bool HasLiveGuideRun(IEnumerable<ThreadDungeonRun> runs, uint ownerGuid)
        {
            if (runs == null)
                return false;

            foreach (var run in runs)
            {
                if (run == null || run.OwnerGuid != ownerGuid || !ThreadGuideRules.IsGuide(run.Spec))
                    continue;

                var state = run.State;

                if (state == ThreadDungeonRunState.Starting || state == ThreadDungeonRunState.Active)
                    return true;
            }

            return false;
        }

        // ---------------- clear ----------------

        /// <summary>
        /// The clear seam's body, for the run owner. Advances ONLY when the run's rung is exactly
        /// ThreadGuideLadder.NextRung(GuideLevel), so a replayed old rung (or a skipped-ahead one) pays nothing.
        /// On an advance: GuideLevel = rung FIRST (so a throw below cannot leave the rung unlatched and
        /// replayable), then the XP when both the cap and the percent are greater than 0 (a cap of 0 means
        /// UNCAPPED to GrantLevelProportionalXp), then T-CLEAR. True when the ladder advanced.
        /// </summary>
        public static bool ApplyClear(IGuideHolder holder, DungeonGemSpec spec, double percent, Func<int, long> capFor,
            Action<string> say, Action<Exception> onPayThrow = null)
        {
            if (holder == null || !ThreadGuideRules.IsGuide(spec))
                return false;

            var rung = spec.Guide.Rung;

            if (ThreadGuideLadder.NextRung(holder.GuideLevel) != rung)
                return false;

            holder.GuideLevel = rung;

            try
            {
                var cap = capFor == null ? 0 : capFor(rung);

                if (cap > 0 && percent > 0 && !double.IsNaN(percent) && !double.IsInfinity(percent))
                    holder.GrantXp(percent, cap);
            }
            catch (Exception ex)
            {
                onPayThrow?.Invoke(ex);
            }

            // Saved AFTER the XP and BEFORE the tell: a crash before this point loses the rung and the XP together,
            // so the clear is simply replayable; a crash after it has both persisted.
            holder.Save();

            say?.Invoke(ThreadGuideText.ComposeClear(rung));
            return true;
        }

        // ---------------- clears counter ----------------

        /// <summary>
        /// Who a clear counts toward ThreadClearsLifetime for: the survey's recipients
        /// (ThreadDungeonManager.SurveyRecipients) under the survey's nothing-spawned gate. Every clear, guide or
        /// not; the survey's gem-level floor is NOT applied.
        /// </summary>
        internal static IReadOnlyList<uint> ClearCountRecipients(IReadOnlyList<RosterMemberSnapshot> roster, Func<uint, bool> isOnline, int spawned)
        {
            if (spawned <= 0)
                return Array.Empty<uint>();

            return ThreadDungeonManager.SurveyRecipients(roster, isOnline);
        }

        // ---------------- fail regrant ----------------

        /// <summary>What the clear seam does for a run. See <see cref="DecideClearSeam"/>.</summary>
        public enum ClearSeamAction
        {
            /// <summary>Not a guide run, or the owner is offline: nothing (an offline owner gets no credit).</summary>
            Skip,

            /// <summary>The run placed nothing: no credit, and the owner is told ThreadGuideText.EmptyThread.</summary>
            Empty,

            /// <summary>Run <see cref="ApplyClear"/> on the owner's action queue.</summary>
            Apply,
        }

        /// <summary>The clear seam's branch, in the order it checks: guide run, owner online, something spawned.</summary>
        public static ClearSeamAction DecideClearSeam(DungeonGemSpec spec, bool ownerOnline, int spawned)
        {
            if (!ThreadGuideRules.IsGuide(spec) || !ownerOnline)
                return ClearSeamAction.Skip;

            return spawned <= 0 ? ClearSeamAction.Empty : ClearSeamAction.Apply;
        }

        /// <summary>True when an ended run should regrant: it never reached Cleared, and it was a guide run.</summary>
        public static bool ShouldRegrantOnEnd(ThreadDungeonRunState priorState, DungeonGemSpec spec)
            => priorState != ThreadDungeonRunState.Cleared && ThreadGuideRules.IsGuide(spec);

        /// <summary>
        /// The fail seam's body, for the online owner. Does nothing when a current guide item or a live guide run
        /// is still outstanding (null). When the guide is switched off (<paramref name="enabled"/> is
        /// <see cref="GuideAvailable"/>) it issues nothing and says ThreadGuideText.FailDisabled, with no NPC
        /// pointer since the NPC would refuse too (outcome Disabled). Otherwise it
        /// issues a same-rung fragment through <see cref="Issue"/> and says T-FAIL-REGRANT, or T-FAIL-VISIT when
        /// the pack is full or the give failed.
        /// </summary>
        public static GuideIssueOutcome? ApplyFailRegrant(IGuideHolder holder, int rung, bool hasLiveGuideRun, bool enabled, Action<string> say)
        {
            if (holder == null || hasLiveGuideRun || holder.HasOutstandingGuideItem())
                return null;

            if (!enabled)
            {
                say?.Invoke(ThreadGuideText.FailDisabled);
                return GuideIssueOutcome.Disabled;
            }

            var outcome = Issue(holder, rung);

            say?.Invoke(outcome == GuideIssueOutcome.Given ? ThreadGuideText.ComposeFailRegrant(rung) : ThreadGuideText.FailVisit);

            return outcome;
        }

        // ---------------- notice ----------------

        /// <summary>
        /// Whether the guide can be used at all: Threads on (dynamic_dungeons_enabled), the Fragment Press on
        /// (FragmentPressStation.EnabledProperty, dynamic_dungeons_press_enabled) and the guide on
        /// (dynamic_dungeons_guide_enabled). The NPC grants, the fail seam regrants and the notice fires only then.
        /// </summary>
        public static bool GuideAvailable(bool threadsEnabled, bool pressEnabled, bool guideEnabled)
            => threadsEnabled && pressEnabled && guideEnabled;

        /// <summary>
        /// The level-50 notice gate, evaluated in this order so the costly check runs last: the guide is available,
        /// the latch is not set, the character is at or above ThreadGuideRules.MinPlayerLevel, the character is NOT
        /// a veteran (no Thread clear, no guide rung won, and no survey ever filed - ThreadClearsLifetime is new, so
        /// an existing player's only history is the survey ring), and only then <paramref name="npcExists"/>, the
        /// world-database lookup for the Thread-Guide weenie. <paramref name="npcExists"/> is never invoked when an
        /// earlier gate is closed.
        /// </summary>
        public static bool ShouldShowNotice(bool available, Func<bool> npcExists, bool noticeAlreadyShown, long level, int clearsLifetime,
            int guideLevel, bool hasSurveyHistory)
            => available && !noticeAlreadyShown && level >= ThreadGuideRules.MinPlayerLevel
               && clearsLifetime <= 0 && guideLevel <= 0 && !hasSurveyHistory
               && npcExists != null && npcExists();

        /// <summary>
        /// The notice, the facet notice's shape: when <see cref="ShouldShowNotice"/> says so, <paramref name="latch"/>
        /// (set and save PropertyBool 9073) runs BEFORE <paramref name="send"/>; when it does not, NEITHER runs, so
        /// a notice gated by content not being live yet is not spent. True when it fired.
        /// </summary>
        public static bool SendNoticeIfDue(bool available, Func<bool> npcExists, bool noticeAlreadyShown, long level, int clearsLifetime,
            int guideLevel, bool hasSurveyHistory, Action latch, Action send)
        {
            if (!ShouldShowNotice(available, npcExists, noticeAlreadyShown, level, clearsLifetime, guideLevel, hasSurveyHistory))
                return false;

            latch?.Invoke();
            send?.Invoke();
            return true;
        }

        /// <summary>
        /// The login reminder for a player left without a fragment (an offline fail, or a pack that was full):
        /// the guide is available, the ladder is not complete, a fragment has been issued before (serial &gt; 0),
        /// and nothing guide-related is outstanding.
        /// </summary>
        public static bool ShouldSendLoginReminder(bool available, int guideLevel, int grantSerial, bool hasOutstandingGuideItem, bool hasLiveGuideRun)
            => available && guideLevel < ThreadGuideLadder.LastRung && grantSerial > 0 && !hasOutstandingGuideItem && !hasLiveGuideRun;

        /// <summary>
        /// A cached "does this exist" answer for a world-database lookup that is costly when it MISSES: a positive
        /// answer is kept forever (content is not unapplied from a running server), a negative one for
        /// <see cref="NegativeTtl"/>, so a missing weenie costs one lookup per five minutes rather than one per
        /// login and level-up. Thread-safe enough by construction: the worst a race does is one extra lookup.
        /// </summary>
        public sealed class PresenceCache
        {
            public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(5);

            private volatile bool positive;
            private long negativeUntilTicks;

            public bool Check(Func<bool> lookup, DateTime nowUtc)
            {
                if (positive)
                    return true;

                if (nowUtc.Ticks < System.Threading.Interlocked.Read(ref negativeUntilTicks))
                    return false;

                if (lookup != null && lookup())
                {
                    positive = true;
                    return true;
                }

                System.Threading.Interlocked.Exchange(ref negativeUntilTicks, (nowUtc + NegativeTtl).Ticks);
                return false;
            }
        }

        /// <summary>
        /// The reward percent tunable as a usable value: NaN or Infinity reads as the shipped 1.0. A value of 0
        /// or less is kept, and ApplyClear pays nothing for it.
        /// </summary>
        public static double SanitizeRewardPercent(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? 1.0 : value;
    }
}
