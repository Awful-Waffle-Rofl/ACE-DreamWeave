using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// PvP arena player commands (Docs/Pvp/DESIGN.md "Commands"): /arena join and the rest of the /arena family.
    /// Thin wrappers over the <see cref="PvpMatchManager"/> facade (PR C) - every mutating call happens on the
    /// world thread, which chat commands already run on. Result-to-text mapping is split into internal pure
    /// functions so it is unit testable without a live Session/Player.
    ///
    /// Observed on stage 2026-09-26: the AC client rejected both "/join arena 1v1" and "@join arena 1v1" with its
    /// own "That is not a valid command." and the server received neither - the client intercepts any command
    /// whose leading word is "join" regardless of prefix, so a top-level "join" command is permanently
    /// unreachable from a player. "/arena join ..." is the only entry point.
    /// </summary>
    public static class PvpArenaCommands
    {
        // ---------------- /arena join ----------------

        internal const string JoinUsage = "[Arena] Usage: /arena join <1v1|2v2|tugak> [duo] [template] or /arena join bg [group] [template]";

        /// <summary>
        /// The handler behind "/arena join ...". PvP Template Facets (Docs/Pvp/TEMPLATES.md "Commands"): a template key
        /// on the line becomes the remembered preference (when it is offered for that mode) and is joined on; with no
        /// key the remembered one is used; with neither, the template list is shown instead of queuing.
        /// </summary>
        private static void HandleJoinArena(Player player, string[] args)
        {
            var parsed = ParseJoinArena(args);

            if (parsed.Outcome == JoinArenaParseOutcome.BadUsage)
            {
                Msg(player, JoinUsage);
                return;
            }

            // Every mode is templated by default (owner ruling 2026-10-03); for bg the key is checked against the modes the room can form.
            if (parsed.TemplateKey != null && PvpMatchManager.IsTemplateOffered(parsed.TemplateKey, parsed.ModeKey))
                player.PvpTemplatePreference = parsed.TemplateKey;

            var result = PvpMatchManager.Join(player, parsed.ModeKey, parsed.Duo, parsed.TemplateKey, parsed.Group);

            if (result.Refusal == PvpJoinRefusal.NoTemplateChosen)
            {
                Msg(player, PvpArenaText.NoTemplateChosen);
                Msg(player, TemplatesListText(PvpMatchManager.ListTemplates(offeredOnly: true), player.PvpTemplatePreference, PvpMatchManager.TemplateLabel));
                return;
            }

            Msg(player, JoinResultText(result));
        }

        internal enum JoinArenaParseOutcome
        {
            Ok,
            BadUsage
        }

        internal readonly record struct JoinArenaParseResult(JoinArenaParseOutcome Outcome, string ModeKey, bool Duo, string TemplateKey = null, bool Group = false)
        {
            internal static readonly JoinArenaParseResult BadUsage = new JoinArenaParseResult(JoinArenaParseOutcome.BadUsage, null, false);
        }

        /// <summary>
        /// Parses the tokens after "/arena join": "&lt;1v1|2v2|tugak&gt; [duo] [template]" (the two optional tokens in
        /// either order) or "bg [group]". Pure. "duo" (any case) is the pair flag and "group" the whole-fellowship flag;
        /// any other token is the template key, lowercased, and must pass PvpTemplateSnapshotService.ValidateKey, which
        /// also reserves "duo" and "group", so no template can be keyed with a flag word. A template key works on every
        /// mode, the battleground room included. Whether duo or
        /// group is supported for the mode is a coordinator refusal (DuoNotSupportedForMode, GroupNotSupportedForMode),
        /// not a parse error, so "tugak duo" parses OK and is refused downstream with its own message. Duo and group
        /// together, a repeated flag, or a second key are usage errors.
        /// </summary>
        internal static JoinArenaParseResult ParseJoinArena(string[] args)
        {
            if (args == null || args.Length < 1 || args.Length > 3)
                return JoinArenaParseResult.BadUsage;

            var mode = ArenaMapCatalog.CanonicalModeWord(args[0]);

            if (!IsJoinableMode(mode))
                return JoinArenaParseResult.BadUsage;

            var duo = false;
            string key = null;

            var group = false;

            foreach (var token in args.Skip(1))
            {
                if (string.Equals(token, "duo", StringComparison.OrdinalIgnoreCase))
                {
                    if (duo || group)
                        return JoinArenaParseResult.BadUsage;

                    duo = true;
                    continue;
                }

                if (string.Equals(token, GroupToken, StringComparison.OrdinalIgnoreCase))
                {
                    if (duo || group)
                        return JoinArenaParseResult.BadUsage;

                    group = true;
                    continue;
                }

                var candidate = token.Trim().ToLowerInvariant();

                if (key != null || PvpTemplateSnapshotService.ValidateKey(candidate) != null)
                    return JoinArenaParseResult.BadUsage;

                key = candidate;
            }

            return new JoinArenaParseResult(JoinArenaParseOutcome.Ok, mode, duo, key, group);
        }

        /// <summary>The second token that queues the caller's whole fellowship (battleground room only).</summary>
        internal const string GroupToken = "group";

        /// <summary>The mode tokens /arena join and /top take: the three arena modes and the battleground room.</summary>
        private static bool IsJoinableMode(string mode) =>
            mode == ArenaMapCatalog.OneVOneKey || mode == ArenaMapCatalog.TwoVTwoKey || mode == ArenaMapCatalog.FfaKey
            || mode == BattlegroundModes.RoomKey;

        /// <summary>
        /// TRUE when <paramref name="word"/> (any case; "tugak" or its alias "ffa" included) names a ladder /top can show: the three
        /// arena modes or the battleground room. The one check behind /top's ladder arm, so the alias handling is unit-testable.
        /// </summary>
        internal static bool IsLadderWord(string word) => IsJoinableMode(ArenaMapCatalog.CanonicalModeWord(word));

        /// <summary>The chat line for a <see cref="PvpJoinResult"/>. Pure. Refusals reuse the coordinator's own mapper.</summary>
        internal static string JoinResultText(PvpJoinResult result)
        {
            if (!result.Joined)
                return PvpMatchCoordinator.RefusalText(result) ?? PvpArenaText.CannotJoinNow;

            if (result.PartnerName != null)
                return PvpArenaText.Fill(PvpArenaText.JoinedDuo, ("partner", result.PartnerName), ("count", result.WaitingCount));

            if (result.TemplateLabel != null)
                return PvpArenaText.Fill(PvpArenaText.JoinedWithTemplate, ("mode", PvpArenaText.ModeLabel(result.ModeKey)), ("template", result.TemplateLabel), ("count", result.WaitingCount));

            return PvpArenaText.Fill(PvpArenaText.Joined, ("mode", PvpArenaText.ModeLabel(result.ModeKey)), ("count", result.WaitingCount));
        }

        // ---------------- /arena templates and /arena template <key> (PvP Template Facets) ----------------

        private static void HandleArenaTemplates(Player player)
        {
            if (!PvpMatchManager.TemplatesLoaded)
            {
                Msg(player, PvpArenaText.TemplatesUnavailable);
                return;
            }

            Msg(player, TemplatesListText(PvpMatchManager.ListTemplates(offeredOnly: true), player.PvpTemplatePreference, PvpMatchManager.TemplateLabel));
        }

        /// <summary>
        /// The /arena templates block: every offered template with the modes it is offered in, then how to choose one,
        /// naming the player's remembered template when they have one. Pure.
        /// </summary>
        internal static string TemplatesListText(IReadOnlyList<PvpTemplateOffer> offered, string preferenceKey, Func<string, string> labelFor)
        {
            if (offered == null || offered.Count == 0)
                return PvpArenaText.TemplatesNone;

            var sb = new StringBuilder();
            sb.AppendLine(PvpArenaText.TemplatesHeader);

            foreach (var t in offered)
                sb.AppendLine(PvpArenaText.Fill(PvpArenaText.TemplatesRow, ("key", t.Key), ("name", t.DisplayName), ("modes", string.Join(", ", t.Modes.Select(PvpArenaText.ModeLabel)))));

            var current = string.IsNullOrEmpty(preferenceKey) ? "" : PvpArenaText.Fill(PvpArenaText.TemplatesCurrent, ("template", labelFor?.Invoke(preferenceKey) ?? preferenceKey));

            sb.Append(PvpArenaText.Fill(PvpArenaText.TemplatesFooter, ("current", current)));

            return sb.ToString();
        }

        /// <summary>
        /// "/arena template &lt;key&gt;": remembers the template (PropertyString 9023, on the character, so it survives a
        /// relog). Only a key offered in at least one mode is accepted; a duo partner uses their own.
        /// </summary>
        private static void HandleArenaTemplate(Player player, string[] args)
        {
            if (args.Length != 1)
            {
                Msg(player, ArenaUsage);
                return;
            }

            var key = PvpMatchCoordinator.NormalizeTemplateKey(args[0]);

            if (key == null || PvpTemplateSnapshotService.ValidateKey(key) != null || !PvpMatchManager.IsTemplateOfferedAnywhere(key))
            {
                Msg(player, PvpArenaText.Fill(PvpArenaText.TemplateUnknown, ("template", args[0])));
                return;
            }

            player.PvpTemplatePreference = key;

            Msg(player, PvpArenaText.Fill(PvpArenaText.TemplateSet, ("template", PvpMatchManager.TemplateLabel(key))));
        }

        // ---------------- /arena ----------------

        internal const string ArenaUsage =
            "[Arena] Usage: /arena <join <1v1|2v2|tugak> [duo] [template]|join bg [group] [template]|templates|template <key>|leave [confirm]|status|accept|decline|rating [name]|top <1v1|2v2|tugak|bg> [count]>";

        [CommandHandler("arena", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "PvP arena: templates, join, leave, status, accept/decline, ratings and leaderboards.",
            "join <1v1|2v2|tugak> [duo] [template] - queue for a PvP arena match on a template (duo: queue as your 2-player fellowship; a partner uses their own template)\n" +
            "join bg [group] [template] - queue for a battleground on a template (group: queue as your whole fellowship; each member uses their own template)\n" +
            "templates               - the arena templates on offer, and the modes each is offered in\n" +
            "template <key>          - remember the template you fight on (kept across relogs)\n" +
            "leave [confirm]         - leave the queue, or forfeit a match in progress (confirm skips the popup)\n" +
            "status                  - your current queue/match status\n" +
            "accept | decline        - answer a pending match invite\n" +
            "rating [name]           - your rating on every ladder, or a named character's\n" +
            "top <1v1|2v2|tugak|bg> [n] - the top of a ladder (default 10, max 25)")]
        public static void HandleArena(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            if (parameters == null || parameters.Length == 0)
            {
                Msg(player, ArenaUsage);
                return;
            }

            var sub = parameters[0].ToLowerInvariant();
            var rest = parameters.Skip(1).ToArray();

            switch (sub)
            {
                case "join":
                    HandleJoinArena(player, rest);
                    break;

                case "templates":
                    HandleArenaTemplates(player);
                    break;

                case "template":
                    HandleArenaTemplate(player, rest);
                    break;

                case "leave":
                    HandleArenaLeave(player, rest);
                    break;

                case "status":
                    HandleArenaStatus(player);
                    break;

                case "accept":
                    HandleArenaAccept(player);
                    break;

                case "decline":
                    HandleArenaDecline(player);
                    break;

                case "rating":
                    HandleArenaRating(player, rest);
                    break;

                case "top":
                    HandleArenaTop(player, rest);
                    break;

                default:
                    Msg(player, ArenaUsage);
                    break;
            }
        }

        // ---- leave ----

        private static void HandleArenaLeave(Player player, string[] args)
        {
            var isConfirmForm = args.Length == 1 && string.Equals(args[0], "confirm", StringComparison.OrdinalIgnoreCase);

            if (args.Length > 1 || (args.Length == 1 && !isConfirmForm))
            {
                Msg(player, ArenaUsage);
                return;
            }

            if (isConfirmForm)
            {
                var forfeit = PvpMatchManager.Forfeit(player);
                var text = ForfeitResultText(forfeit);

                if (text != null)
                    Msg(player, text);

                return;
            }

            var result = PvpMatchManager.Leave(player);

            switch (result.Outcome)
            {
                case PvpLeaveOutcome.NotQueued:
                    Msg(player, PvpArenaText.NotQueuedOrInMatch);
                    break;

                case PvpLeaveOutcome.LeftQueue:
                    Msg(player, PvpArenaText.Fill(PvpArenaText.Left, ("mode", PvpArenaText.ModeLabel(result.ModeKey))));
                    break;

                case PvpLeaveOutcome.DeclinedMatch:
                    // The coordinator's DeclineBeforePlacement already sent PvpArenaText.YouDeclined.
                    break;

                case PvpLeaveOutcome.InMatchNeedsConfirm:
                    if (!player.ConfirmationManager.EnqueueSend(new Confirmation_PvpArenaLeave(player.Guid, result.MatchId ?? Guid.Empty), PvpArenaText.LeaveConfirmPopup))
                        Msg(player, PvpArenaText.LeaveConfirmFallback);
                    break;
            }
        }

        /// <summary>
        /// The chat line for a <see cref="PvpForfeitResult"/> from "/arena leave confirm". Pure. Null means the
        /// coordinator already sent everything needed (Forfeited: PvpArenaText.LeftTheArena via MarkOut;
        /// Declined: PvpArenaText.YouDeclined via DeclineBeforePlacement).
        /// </summary>
        internal static string ForfeitResultText(PvpForfeitResult result)
        {
            switch (result.Outcome)
            {
                case PvpForfeitOutcome.NotInMatch:
                    return PvpArenaText.NotQueuedOrInMatch;

                case PvpForfeitOutcome.AlreadyOut:
                    return PvpArenaText.AlreadyOutOfMatch;

                case PvpForfeitOutcome.Forfeited:
                case PvpForfeitOutcome.Declined:
                default:
                    return null;
            }
        }

        // ---- status ----

        private static void HandleArenaStatus(Player player)
        {
            var result = PvpMatchManager.Status(player);

            Msg(player, StatusResultText(result));
        }

        /// <summary>The chat line for a <see cref="PvpStatusResult"/>. Pure.</summary>
        internal static string StatusResultText(PvpStatusResult result)
        {
            switch (result.Kind)
            {
                case PvpStatusKind.Idle:
                    return PvpArenaText.StatusIdle;

                case PvpStatusKind.Queued:
                    return PvpArenaText.Fill(PvpArenaText.StatusQueued,
                        ("mode", PvpArenaText.ModeLabel(result.ModeKey)),
                        ("waited", FormatDuration(result.Waited)),
                        ("count", result.WaitingCount));

                case PvpStatusKind.AwaitingAccept:
                    return PvpArenaText.Fill(PvpArenaText.StatusAwaitingAccept,
                        ("mode", PvpArenaText.ModeLabel(result.ModeKey)),
                        ("map", MapDisplay(result)));

                case PvpStatusKind.InMatch:
                    if (result.Remaining.HasValue)
                    {
                        var line = PvpArenaText.Fill(result.Overtime ? PvpArenaText.StatusInMatchOvertime : PvpArenaText.StatusInMatch,
                            ("mode", PvpArenaText.ModeLabel(result.ModeKey)),
                            ("map", MapDisplay(result)),
                            ("remaining", FormatDuration(result.Remaining.Value)));

                        // The mode's own suffix (King of the Hill's score, Attack/Defend's crystal count); a result that carries
                        // none falls back to the King of the Hill score suffix built from the scores.
                        if (result.StatusLine != null)
                            line += result.StatusLine;
                        else if (result.Scores != null && result.ScoreTarget > 0)
                            line += BattlegroundText.StatusScore(result.Scores.GetValueOrDefault(0), result.Scores.GetValueOrDefault(1), result.ScoreTarget);

                        return line;
                    }

                    return PvpArenaText.Fill(PvpArenaText.StatusInMatchNotLive,
                        ("mode", PvpArenaText.ModeLabel(result.ModeKey)),
                        ("map", MapDisplay(result)));

                default:
                    return PvpArenaText.StatusIdle;
            }
        }

        /// <summary>
        /// The map name for a status line: the display name, falling back to the bare map key, falling back to
        /// "the arena" if the coordinator result carries neither. AwaitingAccept/InMatch are only reached once a
        /// match has a map (PvpMatchCoordinator assigns one before AwaitingAccept, for the accept popup's own
        /// "{map}" line), so the last fallback is not expected to fire in practice - it exists so a status line
        /// never goes out with a dangling "{map}" if that ever changes. Pure.
        /// </summary>
        internal static string MapDisplay(PvpStatusResult result) => result.MapName ?? result.MapKey ?? "the arena";

        /// <summary>
        /// "5 minutes", "1 minute", "30 seconds", "1 second" - matching ThreadDungeonRun_EmptyGrace.FormatRemaining's
        /// rounding convention (seconds round up, minutes round down) so a queue-waited or match-remaining line never
        /// overstates the time. Pure.
        /// </summary>
        internal static string FormatDuration(TimeSpan span)
        {
            var ticks = Math.Max(0L, span.Ticks);
            var seconds = (ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;

            if (seconds >= 60)
            {
                var minutes = seconds / 60;
                return minutes == 1 ? "1 minute" : $"{minutes} minutes";
            }

            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        // ---- accept / decline ----

        private static void HandleArenaAccept(Player player)
        {
            var result = PvpMatchManager.Accept(player);
            var text = AnswerResultText(result, accepted: true);

            if (text != null)
                Msg(player, text);
        }

        private static void HandleArenaDecline(Player player)
        {
            var result = PvpMatchManager.Decline(player);
            var text = AnswerResultText(result, accepted: false);

            if (text != null)
                Msg(player, text);
        }

        /// <summary>
        /// The chat line for a <see cref="PvpAnswerResult"/>. Pure. Null means the coordinator already sent
        /// everything needed: a decline (chat or popup) sends PvpArenaText.YouDeclined via DeclineBeforePlacement;
        /// an accept has nothing further to say until the match proceeds (Countdown/Fight!/entering).
        /// </summary>
        internal static string AnswerResultText(PvpAnswerResult result, bool accepted)
        {
            switch (result.Outcome)
            {
                case PvpAnswerOutcome.NoPendingMatch:
                    return PvpArenaText.NoPendingMatch;

                case PvpAnswerOutcome.AlreadyAnswered:
                    return PvpArenaText.AlreadyAnswered;

                case PvpAnswerOutcome.Accepted:
                case PvpAnswerOutcome.Declined:
                default:
                    return null;
            }
        }

        // ---- rating ----

        private static void HandleArenaRating(Player player, string[] args)
        {
            if (PvpMatchManager.RatingsState != PvpRatingStoreState.Available)
            {
                Msg(player, PvpArenaText.RatingsUnavailable);
                return;
            }

            uint characterId;
            string name;

            if (args.Length == 0)
            {
                characterId = player.Guid.Full;
                name = player.Name;
            }
            else
            {
                var targetName = string.Join(" ", args);
                var target = PlayerManager.FindByName(targetName);

                if (target == null)
                {
                    Msg(player, PvpArenaText.Fill(NotFound, ("name", targetName)));
                    return;
                }

                characterId = target.Guid.Full;
                name = target.Name;
            }

            var oneVOne = PvpMatchManager.GetRating(characterId, "arena_1v1");
            var twoVTwo = PvpMatchManager.GetRating(characterId, "arena_2v2");
            var ffa = PvpMatchManager.GetRating(characterId, "arena_ffa");
            var battleground = PvpMatchManager.GetRating(characterId, BattlegroundModes.LadderKey);

            Msg(player, RatingLineText(name, oneVOne, twoVTwo, ffa, battleground));
        }

        /// <summary>The "not found" line for a name that does not resolve to any character (online or offline).</summary>
        internal const string NotFound = "[Arena] No arena record for {name}.";

        /// <summary>
        /// The rating line for one character across all three ladders. Pure.
        ///
        /// The template ("{name}: 1v1 {r1} ({w1}-{l1}), 2v2 {r2} ({w2}-{l2}), Tugak Brawl {r3} ({w3}-{l3}).")
        /// substitutes "unranked" for a WHOLE "{r} ({w}-{l})" segment on a ladder with no games, which
        /// PvpArenaText.Fill's flat key/value replacement cannot express (it would leave a stray "(0-0)" behind
        /// a substituted "unranked"). Built directly instead; every literal word, the prefix and the punctuation
        /// match the authored template exactly for both the ranked and unranked cases.
        /// </summary>
        internal static string RatingLineText(string name, PvpRatingView oneVOne, PvpRatingView twoVTwo, PvpRatingView ffa)
        {
            return $"[Arena] {name}: 1v1 {LadderSegment(oneVOne)}, 2v2 {LadderSegment(twoVTwo)}, Tugak Brawl {LadderSegment(ffa)}.";
        }

        /// <summary>
        /// <see cref="RatingLineText(string, PvpRatingView, PvpRatingView, PvpRatingView)"/> plus the battleground
        /// ladder, as a final ", Battleground {r} ({w}-{l})" segment (or "unranked"). Pure.
        /// </summary>
        internal static string RatingLineText(string name, PvpRatingView oneVOne, PvpRatingView twoVTwo, PvpRatingView ffa, PvpRatingView battleground)
        {
            return $"[Arena] {name}: 1v1 {LadderSegment(oneVOne)}, 2v2 {LadderSegment(twoVTwo)}, Tugak Brawl {LadderSegment(ffa)}, {PvpArenaText.ModeLabel(BattlegroundModes.RoomKey)} {LadderSegment(battleground)}.";
        }

        private static string LadderSegment(PvpRatingView view)
        {
            if (view == null || view.Games <= 0)
                return "unranked";

            return $"{view.Rating} ({view.Wins}-{view.Losses})";
        }

        // ---- top ----

        internal const int TopDefaultCount = 10;
        internal const int TopMaxCount = 25;

        internal enum TopParseOutcome
        {
            Ok,
            BadUsage
        }

        internal readonly record struct TopParseResult(TopParseOutcome Outcome, string ModeKey, int Count)
        {
            internal static readonly TopParseResult BadUsage = new TopParseResult(TopParseOutcome.BadUsage, null, 0);
        }

        /// <summary>
        /// Parses "&lt;1v1|2v2|tugak|bg&gt; [count]". Pure. A supplied count below 1 is a usage error; above
        /// <see cref="TopMaxCount"/> it is clamped down rather than refused, matching the "default 10, max 25"
        /// wording (a request for more is satisfied at the cap, not rejected).
        /// </summary>
        internal static TopParseResult ParseTop(string[] args)
        {
            if (args == null || args.Length < 1 || args.Length > 2)
                return TopParseResult.BadUsage;

            var mode = ArenaMapCatalog.CanonicalModeWord(args[0]);

            if (!IsJoinableMode(mode))
                return TopParseResult.BadUsage;

            var count = TopDefaultCount;

            if (args.Length == 2)
            {
                if (!int.TryParse(args[1], out count) || count < 1)
                    return TopParseResult.BadUsage;
            }

            if (count > TopMaxCount)
                count = TopMaxCount;

            return new TopParseResult(TopParseOutcome.Ok, mode, count);
        }

        /// <summary>
        /// The ladder key for a mode key ("1v1" -&gt; "arena_1v1", matching PvpModes' hardcoded LadderKey values;
        /// "bg" -&gt; the one shared "battleground" ladder).
        /// </summary>
        internal static string LadderForMode(string modeKey) =>
            modeKey == BattlegroundModes.RoomKey ? BattlegroundModes.LadderKey : "arena_" + modeKey;

        /// <summary>The bad-usage line when the ladder is reached through /top rather than /arena top.</summary>
        internal const string TopCommandUsage = "Usage: /top <1v1|2v2|tugak|bg> [count] (default 10, max 25)";

        internal static void HandleArenaTop(Player player, string[] args, string badUsageText = ArenaUsage)
        {
            var parsed = ParseTop(args);

            if (parsed.Outcome == TopParseOutcome.BadUsage)
            {
                Msg(player, badUsageText);
                return;
            }

            if (PvpMatchManager.RatingsState != PvpRatingStoreState.Available)
            {
                Msg(player, PvpArenaText.RatingsUnavailable);
                return;
            }

            var ladder = LadderForMode(parsed.ModeKey);
            var top = PvpMatchManager.GetTop(ladder, parsed.Count);
            var modeLabel = PvpArenaText.ModeLabel(parsed.ModeKey);

            if (top == null || top.Count == 0)
            {
                Msg(player, PvpArenaText.Fill(TopEmpty, ("mode", modeLabel)));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(PvpArenaText.Fill(TopHeader, ("count", parsed.Count), ("mode", modeLabel)));

            for (var i = 0; i < top.Count; i++)
            {
                var v = top[i];
                sb.AppendLine(FillTopRow(i + 1, v.CharacterName, v.Rating, v.Wins, v.Losses, PvpMatchManager.LatestTemplateLabel(v.CharacterId, ladder)));
            }

            Msg(player, sb.ToString().TrimEnd());
        }

        internal const string TopHeader = "[Arena] Top {count} - {mode}:";
        internal const string TopRow = "{rank}. {name} {rating} ({wins}-{losses})";
        internal const string TopEmpty = "[Arena] No one is ranked in {mode} yet.";

        /// <summary>A /top row naming the template the player fought their latest match on (PvP Template Facets).</summary>
        internal const string TopRowWithTemplate = "{rank}. {name} ({template}) {rating} ({wins}-{losses})";

        /// <summary>One /top row. With <paramref name="templateLabel"/> (their latest match's template) it is shown beside the name.</summary>
        internal static string FillTopRow(int rank, string name, int rating, int wins, int losses, string templateLabel = null) =>
            PvpArenaText.Fill(templateLabel != null ? TopRowWithTemplate : TopRow, ("rank", rank), ("name", name), ("template", templateLabel), ("rating", rating), ("wins", wins), ("losses", losses));

        // ---------------- shared ----------------

        private static void Msg(Player player, string text)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
