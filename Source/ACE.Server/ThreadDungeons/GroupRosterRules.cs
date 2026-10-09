using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.RewardClaims;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Why a fellow was left off a Group Thread roster (spec 4.1 step 3). Declaration order IS the rule order:
    /// a fellow failing several rules is reported under the first one only.
    ///
    /// These are the WHOLE rule set, and one member is deliberately absent. An InCombat reason existed until
    /// 2026-09-17 and was dropped on the owner's ruling: being in attack or cast mode is no blocker to being
    /// handed a Thread Key, so a fellow swinging at something is seated like anyone else. Note the asymmetry
    /// that leaves, because it is intended and not an oversight: they receive the key but cannot USE it until
    /// they leave combat mode, because ThreadDungeonGemHandler.RefusedByUseGates gates the act of using any
    /// gem or key on CombatMode, and that gate is untouched.
    ///
    /// Nothing persists these values. They are rendered into chat text and into the formation log line and go
    /// nowhere else - not into a spec, not into DungeonRunTelemetry, not into a database column - which is why
    /// a retired reason is deleted here rather than kept as a dead member.
    /// </summary>
    public enum RosterExclusionReason
    {
        /// <summary>
        /// FIRST on purpose, and the reason this enum has a first member at all: a fellow the owner cannot share
        /// XP with (another instance, indoors vs outdoors, a different indoor landblock, or simply too far) used
        /// to be filtered out BEFORE formation ever saw them, so they produced no exclusion and nobody was told
        /// anything. Stage, 2026-09-17: the Fragment Press stands in an indoor cell, so a fellow standing a few
        /// metres away on outdoor terrain vanished from the roster silently. Formation now considers every
        /// fellowship member and reports this one first, because "too far away" explains every other rule's
        /// reading being stale as well.
        /// </summary>
        OutOfRange,

        /// <summary>
        /// The fellow's connection key (ThreadPuzzleIpKey) is serving a Thread puzzle lockout
        /// (ThreadPuzzleFailPolicy). Second, straight after range: it is the one reason no amount of moving,
        /// levelling or freeing pack room can fix before it runs out, so it should not hide behind one that can.
        /// </summary>
        PuzzleLockout,

        BelowLevel,
        PkTimer,
        InInstance,
        OnLiveRoster,
        NoPackRoom,
    }

    /// <summary>
    /// One character considered for a roster, captured as plain values so formation is testable without a Player.
    /// <see cref="IpKey"/> is null when the session is exempt from the IP rule or has no address.
    /// </summary>
    public sealed class RosterCandidate
    {
        /// <param name="inRange">
        /// Trailing and defaulted to true so the OWNER's candidate (who is never range-checked against themselves)
        /// and every existing caller read unchanged. A fellow's value comes from
        /// <see cref="GroupRosterRules.CandidateFrom(Player, Player)"/>.
        /// </param>
        public RosterCandidate(uint guid, string name, uint accountId, int level, bool pkTimer,
            bool inEphemeral, bool onLiveRoster, bool hasPackRoom, string ipKey, bool inRange = true, TimeSpan? puzzleLockoutRemaining = null)
        {
            PuzzleLockoutRemaining = puzzleLockoutRemaining;
            Guid = guid;
            Name = name;
            AccountId = accountId;
            Level = level;
            PkTimer = pkTimer;
            InEphemeral = inEphemeral;
            OnLiveRoster = onLiveRoster;
            HasPackRoom = hasPackRoom;
            IpKey = ipKey;
            InRange = inRange;
        }

        public uint Guid { get; }
        public string Name { get; }
        public uint AccountId { get; }
        public int Level { get; }
        public bool PkTimer { get; }
        public bool InEphemeral { get; }
        public bool OnLiveRoster { get; }
        public bool HasPackRoom { get; }
        public string IpKey { get; }

        /// <summary>Would this fellow share the owner's kill XP where they stand? False is <see cref="RosterExclusionReason.OutOfRange"/>.</summary>
        public bool InRange { get; }

        /// <summary>Time left on the fellow's Thread puzzle lockout, or null. Set is <see cref="RosterExclusionReason.PuzzleLockout"/>.</summary>
        public TimeSpan? PuzzleLockoutRemaining { get; }

        public bool PuzzleLockedOut => PuzzleLockoutRemaining.HasValue;

        internal RosterSeat ToSeat() => new RosterSeat(Guid, Name, AccountId, Level);
    }

    /// <summary>The outcome of <see cref="GroupRosterRules.Form"/>. Immutable.</summary>
    public sealed class RosterFormResult
    {
        internal RosterFormResult(IReadOnlyList<RosterSeat> seats, IReadOnlyList<(string Name, RosterExclusionReason Reason)> exclusions,
            IReadOnlyList<string> ipConflictNames, IReadOnlyDictionary<string, TimeSpan> lockoutRemaining = null)
        {
            LockoutRemaining = lockoutRemaining ?? new Dictionary<string, TimeSpan>();
            Seats = seats;
            Exclusions = exclusions;
            IpConflictNames = ipConflictNames;
        }

        /// <summary>Owner first, then eligible fellows by ascending guid, capped. Just the owner on an IP conflict.</summary>
        public IReadOnlyList<RosterSeat> Seats { get; }

        /// <summary>One entry per ineligible fellow, in ascending guid order.</summary>
        public IReadOnlyList<(string Name, RosterExclusionReason Reason)> Exclusions { get; }

        /// <summary>
        /// Time left on each <see cref="RosterExclusionReason.PuzzleLockout"/> exclusion's lockout, by fellow name (names
        /// are unique on a shard; the exclusion carries only the name). Feeds the reason text's "for another {t}".
        /// </summary>
        public IReadOnlyDictionary<string, TimeSpan> LockoutRemaining { get; }

        /// <summary>Every character (owner included) sharing a non-exempt IP key with another, in roster order.</summary>
        public IReadOnlyList<string> IpConflictNames { get; }

        public bool IpConflict => IpConflictNames.Count > 0;

        /// <summary>At least one fellow holds a seat. Always false on an IP conflict; check <see cref="IpConflict"/> first.</summary>
        public bool IsGroup => Seats.Count > 1;
    }

    /// <summary>
    /// Group Threads roster formation (spec 4.1, rulings R9 and R12), pure, plus the player-facing lines it
    /// produces and the one live wrapper that turns a Player into a <see cref="RosterCandidate"/>.
    /// </summary>
    public static class GroupRosterRules
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The Name a member's key carries, distinct from the owner gem's "Thread Gem" (orchestrator ruling).</summary>
        public const string KeyItemName = "Thread Key";

        /// <summary>The PluralName a member's key carries.</summary>
        public const string KeyItemPluralName = "Thread Keys";

        /// <summary>
        /// Forms a roster from the owner and the fellows offered.
        ///
        /// A fellow is eligible when, in this order: in range of the owner; not serving a Thread puzzle lockout; level &gt;= <paramref name="minPlayerLevel"/>;
        /// no PK timer; not inside an ephemeral instance; not on a live roster; room in the pack for a key. The first
        /// failing rule is the exclusion. Null fellows, repeated guids and the owner's own guid are ignored. Combat
        /// mode is deliberately not a rule; see <see cref="RosterExclusionReason"/>.
        ///
        /// IP rule: over the owner plus the ELIGIBLE fellows only, any non-null IP key held by two or more characters
        /// fails the group start. Every character holding such a key is named, in roster order, and Seats is just the
        /// owner. Null keys (exempt or unknown) never collide.
        ///
        /// Seats are the owner, then eligible fellows by ascending guid, capped at <paramref name="maxSeats"/> (owner
        /// included; values below 1 are treated as 1, so the owner always holds a seat). The IP rule is applied
        /// before the cap. The owner is never checked for eligibility here: the gem use has already gated them, and
        /// TryStart gates them again.
        /// </summary>
        public static RosterFormResult Form(RosterCandidate owner, IEnumerable<RosterCandidate> fellows, int minPlayerLevel, int maxSeats)
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));

            var eligible = new List<RosterCandidate>();
            var exclusions = new List<(string Name, RosterExclusionReason Reason)>();
            var lockoutRemaining = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            var seen = new HashSet<uint> { owner.Guid };

            foreach (var fellow in (fellows ?? Enumerable.Empty<RosterCandidate>()).Where(f => f != null).OrderBy(f => f.Guid))
            {
                if (!seen.Add(fellow.Guid))
                    continue;

                if (TryExclude(fellow, minPlayerLevel, out var reason))
                {
                    exclusions.Add((fellow.Name, reason));

                    if (reason == RosterExclusionReason.PuzzleLockout && fellow.Name != null)
                        lockoutRemaining[fellow.Name] = fellow.PuzzleLockoutRemaining.Value;
                }
                else
                    eligible.Add(fellow);
            }

            var pool = new List<RosterCandidate>(eligible.Count + 1) { owner };
            pool.AddRange(eligible);

            var collidingKeys = new HashSet<string>(
                pool.Where(c => c.IpKey != null)
                    .GroupBy(c => c.IpKey, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key),
                StringComparer.Ordinal);

            var conflictNames = pool.Where(c => c.IpKey != null && collidingKeys.Contains(c.IpKey)).Select(c => c.Name).ToList();

            List<RosterSeat> seats;

            if (conflictNames.Count > 0)
                seats = new List<RosterSeat> { owner.ToSeat() };
            else
                seats = pool.Take(Math.Max(1, maxSeats)).Select(c => c.ToSeat()).ToList();

            return new RosterFormResult(seats.AsReadOnly(), exclusions.AsReadOnly(), conflictNames.AsReadOnly(), lockoutRemaining);
        }

        /// <summary>The eligibility rules, in order. True (with the reason) when the candidate is excluded.</summary>
        internal static bool TryExclude(RosterCandidate candidate, int minPlayerLevel, out RosterExclusionReason reason)
        {
            reason = default;

            if (!candidate.InRange) { reason = RosterExclusionReason.OutOfRange; return true; }
            if (candidate.PuzzleLockedOut) { reason = RosterExclusionReason.PuzzleLockout; return true; }
            if (candidate.Level < minPlayerLevel) { reason = RosterExclusionReason.BelowLevel; return true; }
            if (candidate.PkTimer) { reason = RosterExclusionReason.PkTimer; return true; }
            if (candidate.InEphemeral) { reason = RosterExclusionReason.InInstance; return true; }
            if (candidate.OnLiveRoster) { reason = RosterExclusionReason.OnLiveRoster; return true; }
            if (!candidate.HasPackRoom) { reason = RosterExclusionReason.NoPackRoom; return true; }

            return false;
        }

        /// <summary>
        /// The exclusions to report when the roster is re-formed on Yes (orchestrator ruling on Task 5): those in
        /// <paramref name="current"/> that were NOT already reported with the offer. Matched on the (name, reason)
        /// pair, so a fellow excluded again for the same reason is suppressed while a fellow excluded for a different
        /// reason counts as new. Keeps <paramref name="current"/>'s order. A null <paramref name="alreadyReported"/>
        /// returns every current exclusion; a null <paramref name="current"/> returns none.
        /// </summary>
        public static IReadOnlyList<(string Name, RosterExclusionReason Reason)> NewExclusions(
            IReadOnlyList<(string Name, RosterExclusionReason Reason)> alreadyReported,
            IReadOnlyList<(string Name, RosterExclusionReason Reason)> current)
        {
            if (current == null || current.Count == 0)
                return Array.Empty<(string Name, RosterExclusionReason Reason)>();

            if (alreadyReported == null || alreadyReported.Count == 0)
                return current;

            var seen = new HashSet<(string, RosterExclusionReason)>(alreadyReported.Select(e => (e.Name, e.Reason)));

            return current.Where(e => !seen.Contains((e.Name, e.Reason))).ToList().AsReadOnly();
        }

        /// <summary>
        /// The character budget for either confirmation prompt. The client's Yes/No panel is not an unbounded text
        /// field, and 500 is deliberately conservative rather than measured: nothing in the server knows the panel's
        /// real limit.
        ///
        /// Since the prompts list ONLY excluded fellows (owner ruling, 2026-09-17), a line is "  Name - reason": the
        /// longest reason, "already part of an open Thread", is 30 characters, so a line runs about 25 to 50. A
        /// retail-sized fellowship (at most eight exclusions under an owner) fits whole even with 16-character names
        /// and the longest reason on every line (479 characters; a test pins that it is not trimmed), so the cap only binds when a
        /// larger fellowship is mostly excluded - up to <see cref="Fellowship.AbsoluteMaxFellows"/> - 1 lines, of
        /// which roughly 8 to 11 fit depending on names and reasons. When it does, the trailing lines are replaced by
        /// "... and N more.", so the prompt still accounts for everyone, and every excluded fellow ALSO gets their
        /// own chat line to the owner and a direct message regardless of what the dialog had room for.
        /// </summary>
        public const int OfferTextMaxLength = 500;

        /// <summary>
        /// S1, the group-start confirmation dialog.
        ///
        /// Shape, per owner ruling 2026-09-17: "if we are only listing players who are excluded + reason we should
        /// fall under [500]. 1 line per excluded character. Name, concise reason." This SUPERSEDES the same day's
        /// earlier amendment, under which the dialog also listed every joining member by name; the joining side is
        /// now just the count in the header.
        ///
        /// Header "Open this Thread for your fellowship (N joining)?", where N is the formed roster INCLUDING the
        /// owner (<paramref name="seats"/>, which is read for its count only). Below it, and only when something
        /// was excluded, a "Cannot join:" block with one "Name - reason" line per excluded fellow in the order
        /// formation reported them. Nothing excluded means the prompt is the one-line question. The reason wording
        /// is <see cref="ReasonText"/>, the SAME source the per-fellow chat lines use, so the dialog and the chat
        /// log can never tell two different stories about one fellow.
        ///
        /// Pure, and capped at <see cref="OfferTextMaxLength"/>: see <see cref="FitOffer"/>.
        /// </summary>
        public static string OfferText(IReadOnlyList<RosterSeat> seats,
            IReadOnlyList<(string Name, RosterExclusionReason Reason)> exclusions, int minPlayerLevel,
            IReadOnlyDictionary<string, TimeSpan> lockoutRemaining = null)
        {
            var joining = seats?.Count(s => s != null) ?? 0;
            var header = $"Open this Thread for your fellowship ({joining.ToString(CultureInfo.InvariantCulture)} joining)?";
            var cannot = ExclusionLines(exclusions, minPlayerLevel, lockoutRemaining);

            if (cannot.Count == 0)
                return header;

            return FitOffer(cannot, shown => BuildOffer(header, cannot, shown));
        }

        /// <summary>
        /// S1b, the solo-fallback confirmation: no fellow survived formation, so the owner is asked whether to open
        /// the Thread alone rather than having one opened for them. Lists the same reasons in the same words as
        /// <see cref="OfferText"/>, and omits the reason block when there is nothing to explain (a fellowship of one).
        /// </summary>
        public static string SoloOfferText(IReadOnlyList<(string Name, RosterExclusionReason Reason)> exclusions, int minPlayerLevel,
            IReadOnlyDictionary<string, TimeSpan> lockoutRemaining = null)
        {
            var cannot = ExclusionLines(exclusions, minPlayerLevel, lockoutRemaining);

            if (cannot.Count == 0)
                return "No one in your fellowship can join right now. Open this Thread alone?";

            return FitOffer(cannot, shown => BuildSoloOffer(cannot, shown));
        }

        /// <summary>
        /// Shrinks a prompt to <see cref="OfferTextMaxLength"/> by dropping TRAILING exclusion lines, one at a time,
        /// until it fits. The exclusions are the whole variable content of both prompts, so they are the only thing
        /// trimmed; the fixed header and question in <paramref name="build"/> are never dropped. Every step is still
        /// a complete prompt, because <paramref name="build"/> accounts for the dropped lines as "... and N more.".
        /// </summary>
        private static string FitOffer(IReadOnlyList<string> cannot, Func<int, string> build)
        {
            var shown = cannot.Count;
            var text = build(shown);

            while (text.Length > OfferTextMaxLength && shown > 0)
                text = build(--shown);

            return text;
        }

        /// <summary>One "Name - reason" line per exclusion, in the order formation reported them.</summary>
        private static List<string> ExclusionLines(IReadOnlyList<(string Name, RosterExclusionReason Reason)> exclusions, int minPlayerLevel,
            IReadOnlyDictionary<string, TimeSpan> lockoutRemaining)
        {
            var lines = new List<string>();

            for (var i = 0; exclusions != null && i < exclusions.Count; i++)
                lines.Add($"{exclusions[i].Name} - {ReasonText(exclusions[i].Reason, minPlayerLevel, Remaining(lockoutRemaining, exclusions[i].Name))}");

            return lines;
        }

        private static string BuildOffer(string header, IReadOnlyList<string> cannot, int cannotShown)
        {
            var text = new StringBuilder(header);

            text.Append("\n\nCannot join:");
            AppendLines(text, cannot, cannotShown);

            return text.ToString();
        }

        private static string BuildSoloOffer(IReadOnlyList<string> cannot, int cannotShown)
        {
            var text = new StringBuilder("No one in your fellowship can join right now:");

            AppendLines(text, cannot, cannotShown);
            text.Append("\n\nOpen this Thread alone?");

            return text.ToString();
        }

        /// <summary>Appends the first <paramref name="shown"/> lines, then accounts for the rest as a count.</summary>
        private static void AppendLines(StringBuilder text, IReadOnlyList<string> lines, int shown)
        {
            for (var i = 0; i < shown && i < lines.Count; i++)
                text.Append("\n  ").Append(lines[i]);

            var hidden = lines.Count - Math.Max(0, Math.Min(shown, lines.Count));

            if (hidden > 0)
                text.Append("\n  ... and ").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append(" more.");
        }

        /// <summary>S3, one line per excluded fellow, sent to the owner.</summary>
        public static string ExclusionText(string name, RosterExclusionReason reason, int minPlayerLevel, TimeSpan? lockoutRemaining = null)
            => $"{name} cannot join: {ReasonText(reason, minPlayerLevel, lockoutRemaining)}.";

        /// <summary>
        /// S3b, the SAME exclusion told to the fellow who was left off, rather than only to the owner. Until this
        /// existed a fellow who was considered and dropped heard nothing at all, which is what made the 2026-09-17
        /// stage reports impossible for the testers themselves to diagnose.
        /// </summary>
        public static string ExcludedFellowText(string ownerName, RosterExclusionReason reason, int minPlayerLevel, TimeSpan? lockoutRemaining = null)
            => $"{ownerName} opened a Thread for the fellowship, but you could not be included: {ReasonText(reason, minPlayerLevel, lockoutRemaining)}.";

        /// <summary>The lockout time for <paramref name="name"/> from a formation's map, or null.</summary>
        public static TimeSpan? Remaining(IReadOnlyDictionary<string, TimeSpan> lockoutRemaining, string name)
            => lockoutRemaining != null && name != null && lockoutRemaining.TryGetValue(name, out var t) ? t : (TimeSpan?)null;

        /// <param name="lockoutRemaining">
        /// PuzzleLockout only: the time left, rendered with the gem refusal's own words ("barred from the Threads for
        /// another {t}", PuzzleGateText.FormatDuration). Without it the reason reads "barred from the Threads".
        /// </param>
        private static string ReasonText(RosterExclusionReason reason, int minPlayerLevel, TimeSpan? lockoutRemaining = null)
        {
            switch (reason)
            {
                case RosterExclusionReason.OutOfRange: return "too far away";
                case RosterExclusionReason.PuzzleLockout:
                    return lockoutRemaining.HasValue
                        ? $"barred from the Threads for another {ACE.Server.PuzzleGates.PuzzleGateText.FormatDuration(lockoutRemaining.Value)}"
                        : "barred from the Threads";
                case RosterExclusionReason.BelowLevel: return $"below level {minPlayerLevel.ToString(CultureInfo.InvariantCulture)}";
                case RosterExclusionReason.PkTimer: return "PK timer active";
                case RosterExclusionReason.InInstance: return "inside another instance";
                case RosterExclusionReason.OnLiveRoster: return "already part of an open Thread";
                case RosterExclusionReason.NoPackRoom: return "no room for a key";
                default: return "not eligible";
            }
        }

        /// <summary>S2, sent to the owner when the IP rule fails the group start. Names joined with ", ".</summary>
        public static string IpConflictText(IReadOnlyList<string> names)
            => $"This Thread cannot open for your fellowship: {string.Join(", ", names ?? Array.Empty<string>())} share a connection. Only one character per connection may join.";

        /// <summary>
        /// S5, the chat mirror a member gets with their key. Sent on <see cref="ChatMessageType.Advancement"/>
        /// rather than Broadcast (stage report, 2026-09-17: testers did not notice they had been given a key),
        /// and paired with <see cref="KeyGrantedPopupText"/> so the notice survives both a busy chat window and
        /// the popup being dismissed.
        /// </summary>
        public static string KeyGrantedText(string ownerName, string dungeonName)
            => $"{ownerName} opened {dungeonName} for your fellowship. A {KeyItemName} is in your pack; use it to enter.";

        /// <summary>
        /// S5's modal half, following the pattern at Player_ClassAbilities.SendFirstClassAbilityNotice: the
        /// popup makes the grant impossible to scroll past, and the chat line above is what persists afterwards.
        /// Names the item exactly as the pack shows it, because "use it to enter" is useless if the player
        /// cannot tell which item is meant.
        /// </summary>
        public static string KeyGrantedPopupText(string ownerName, string dungeonName)
            => $"{ownerName} has opened {dungeonName} for your fellowship.\n\n"
               + $"A {KeyItemName} is in your pack. USE the {KeyItemName} to enter the Thread; you are not taken there automatically.\n\n"
               + "The key dies with the run, and its first use costs you nothing.";

        /// <summary>S6, sent to the owner for each seat TryStart dropped at admission.</summary>
        public static string DroppedText(string name)
            => $"{name} could not join: already part of an open Thread.";

        /// <summary>S10, sent to the owner when a member's key could not be minted (ruling R9).</summary>
        public static string KeyFailedText(string name)
            => $"{name} did not receive a key.";

        /// <summary>
        /// S10's other half: the same failure told to the member it happened to. They are still on the roster, so
        /// they are still costing the run a share; without this they simply never hear that a Thread was opened.
        /// </summary>
        public static string KeyFailedMemberText(string ownerName, string dungeonName)
            => $"{ownerName} opened {dungeonName} for your fellowship, but your {KeyItemName} could not be created. Ask them to close the Thread and open it again.";

        /// <summary>
        /// The answer to No on either confirmation. The use is otherwise completely inert - nothing consumed,
        /// nothing bound - so without a line the player cannot tell a refusal from a swallowed click.
        /// </summary>
        public const string ThreadStaysClosedText = "The Thread stays closed.";

        /// <summary>
        /// Yes arrived, but the roster no longer forms a group (a fellow moved, the fellowship disbanded, group
        /// mode was switched off). The owner confirmed a GROUP start and nothing else, so nothing opens; the gem
        /// is untouched and can be used again.
        /// </summary>
        public const string FellowshipCannotJoinText = "Your fellowship can no longer join. The Thread stays closed.";

        /// <summary>
        /// The live wrapper. The PK timer and ephemeral checks mirror two of the gem handler's own refusals
        /// (ThreadDungeonGemHandler.RefusedByUseGates); its THIRD refusal, combat mode, is deliberately not
        /// mirrored since 2026-09-17 (see <see cref="RosterExclusionReason"/>). The IP key is normalised exactly as
        /// reward claims do it; the pack-room check is the Fragment Press's HasRoomFor shape for one Thread Gem.
        /// </summary>
        public static RosterCandidate CandidateFrom(Player player)
            => CandidateFrom(player, inRange: true);

        /// <summary>
        /// A FELLOW's candidate, range-checked against <paramref name="owner"/>. In range means
        /// Fellowship.GetDistanceScalar(owner position, fellow position) is positive: the same rule that decides
        /// whether the fellow would see a share of the owner's kill XP, so a fellow who can be dealt a Thread seat
        /// is exactly a fellow who could have earned from the owner's kills where they stand.
        ///
        /// This replaced <see cref="Fellowship.WithinRange"/> as formation's input filter. WithinRange returns only
        /// the fellows that pass, which is why an out-of-range fellow used to produce no exclusion and no message.
        /// </summary>
        public static RosterCandidate CandidateFrom(Player owner, Player fellow)
            => CandidateFrom(fellow, Fellowship.GetDistanceScalar(owner?.Location, fellow?.Location) > 0);

        private static RosterCandidate CandidateFrom(Player player, bool inRange)
        {
            var session = player.Session;
            var ipKey = RewardClaimRules.NormalizeIpKey(session?.EndPointC2S?.Address, IpLimitManager.IsExempt(session));

            return new RosterCandidate(
                player.Guid.Full,
                player.Name,
                player.Account?.AccountId ?? 0,
                player.Level ?? 0,
                player.PKTimerActive,
                player.Location?.IsEphemeralRealm ?? false,
                ThreadDungeonManager.IsOnLiveRoster(player.Guid.Full),
                HasRoomForKey(player),
                ipKey,
                inRange,
                ThreadPuzzleFailPolicy.LockoutRemaining(session, DateTime.UtcNow));
        }

        /// <summary>
        /// The formation line written to the log on every group-start attempt, at INFO under the [DYNDUNGEON]
        /// prefix. Pure so its shape is pinnable.
        ///
        /// This line is the whole reason the 2026-09-17 stage reports could not be diagnosed: exclusions were only
        /// ever chatted to the owner, so the server-side record of a run that opened with two seats instead of
        /// three said nothing about the third player. Seats and exclusions both, or the next report is just as
        /// opaque.
        /// </summary>
        public static string FormationLogText(string stage, string ownerName, RosterFormResult form)
        {
            var seats = form == null || form.Seats.Count == 0 ? "none" : string.Join(",", form.Seats.Select(s => s.Name));
            var excluded = form == null || form.Exclusions.Count == 0
                ? "none"
                : string.Join(",", form.Exclusions.Select(e => $"{e.Name}:{e.Reason}"));
            var ip = form != null && form.IpConflict ? $" ipConflict={string.Join(",", form.IpConflictNames)}" : string.Empty;

            return $"[DYNDUNGEON] formation {stage} owner={ownerName} seats={seats} excluded={excluded}{ip}";
        }

        /// <summary>
        /// Copied from FragmentPressStation.HasRoomFor (private there): can the player hold one Thread Gem?
        ///
        /// Final review F4: formation runs this against FELLOWS, whose inventories belong to their own action queues,
        /// not the owner's. A throw from that off-thread read counts as no room, so the fellow is excluded as
        /// NoPackRoom instead of the whole formation failing.
        /// </summary>
        private static bool HasRoomForKey(Player player)
        {
            try
            {
                var itemsToReceive = new ItemsToReceive(player);

                itemsToReceive.Add(DungeonGemFactory.DungeonGemWcid, 1);

                return !itemsToReceive.PlayerExceedsLimits;
            }
            catch (Exception ex)
            {
                log.Warn($"[DYNDUNGEON] pack-room check for {player?.Name} (0x{player?.Guid.Full:X8}) threw; counted as no room: {ex.Message}");
                return false;
            }
        }
    }
}
