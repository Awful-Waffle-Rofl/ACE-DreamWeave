using System;
using System.Text;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// Every player-facing PvP arena string, in one place so the coordinator (C3) and the commands (D) add
    /// theirs here too rather than scattering literals. The text is authored by the orchestrator and used
    /// verbatim; every chat line carries the "[Arena] " prefix. Placeholders are written as {name}, exactly as
    /// authored, and filled with <see cref="Fill"/>.
    /// </summary>
    public static class PvpArenaText
    {
        // ---------------- mode labels ----------------

        public const string ModeLabel1v1 = "1v1";
        public const string ModeLabel2v2 = "2v2";
        public const string ModeLabelFfa = "Tugak Brawl";

        /// <summary>The player-facing label for a mode key ("1v1", "2v2", "ffa"); an unknown key is returned as is.</summary>
        public static string ModeLabel(string modeKey)
        {
            switch (modeKey)
            {
                case ArenaMapCatalog.OneVOneKey: return ModeLabel1v1;
                case ArenaMapCatalog.TwoVTwoKey: return ModeLabel2v2;
                case ArenaMapCatalog.FfaKey: return ModeLabelFfa;
                case BattlegroundModes.RoomKey: return BattlegroundText.ModeLabelBattleground;
                case BattlegroundModes.KothModeKey: return BattlegroundText.ModeLabelKoth;
                case BattlegroundModes.AttackDefendModeKey: return BattlegroundText.ModeLabelAttackDefend;
                default: return modeKey ?? "";
            }
        }

        /// <summary>
        /// Replaces each {key} in <paramref name="template"/> with its value. A placeholder with no matching pair is
        /// left as written, so a missing argument is visible in game rather than silently blank.
        /// </summary>
        public static string Fill(string template, params (string Key, object Value)[] values)
        {
            if (template == null)
                return "";

            if (values == null || values.Length == 0)
                return template;

            var sb = new StringBuilder(template);

            foreach (var (key, value) in values)
                sb.Replace("{" + key + "}", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");

            return sb.ToString();
        }

        // ---------------- queue ----------------

        public const string Joined = "[Arena] You joined the {mode} queue. Players waiting: {count}. Type /arena leave to leave the queue.";
        public const string JoinedDuo = "[Arena] You and {partner} joined the 2v2 queue as a pair. Players waiting: {count}.";
        public const string Left = "[Arena] You left the {mode} queue.";
        public const string StatusQueued = "[Arena] You are in the {mode} queue ({waited} waited). Players waiting: {count}.";
        public const string StatusIdle = "[Arena] You are not queued. Join with /arena join 1v1, /arena join 2v2, /arena join 2v2 duo or /arena join tugak.";
        public const string StatusInMatch = "[Arena] You are in a {mode} match on {map}. {remaining} remaining.";
        public const string FfaLobbyProgress = "[Arena] Tugak Brawl lobby: {count} of {needed} players.";

        // ---------------- refusals ----------------

        public const string Disabled = "[Arena] The arena is closed right now.";
        public const string ModeDisabled = "[Arena] The {mode} arena is closed right now.";
        public const string AlreadyQueued = "[Arena] You are already in the {mode} queue. Type /arena leave first.";
        public const string AlreadyInMatch = "[Arena] You are already in a match.";
        public const string InInstance = "[Arena] You cannot join the arena from inside an instance.";

        public const string Denylisted = "[Arena] You cannot join the arena right now.";

        public const string NotOnPkFacet = "[Arena] You must be on your PK facet to join the arena. Use /facet pk, then try again.";

        /// <summary>Both the respite and the active PK timer refusals.</summary>
        public const string RecentPvp = "[Arena] You have been in combat with another player too recently. Try again shortly.";

        public const string TooLow = "[Arena] You must be at least level {level} to enter the arena.";
        public const string Olthoi = "[Arena] Olthoi cannot enter the arena.";
        public const string Mule = "[Arena] This character cannot enter the arena.";

        /// <summary>Both the dead and the teleporting refusals.</summary>
        public const string CannotJoinNow = "[Arena] You cannot join the arena right now.";

        public const string DeclineLockout = "[Arena] You declined a match recently. You can queue again in {seconds} seconds.";
        public const string DuoNeedsPair = "[Arena] To queue as a pair you must be in a fellowship of exactly two players, and both of you must be eligible.";
        public const string DuoPartnerIneligible = "[Arena] {partner} cannot enter the arena right now.";

        /// <summary>PetDevice.ActOnUse refuses a summon while the player is in a match.</summary>
        public const string SummonRefused = "[Arena] You cannot summon a pet during an arena match.";

        /// <summary>Every class ability point spend gate (S1-S5) and unlearning (S6) refuse while the player is in a match.</summary>
        public const string AbilitySpendRefused = "[Arena] You cannot change your class abilities during an arena match.";

        /// <summary>Player.CheckFacetGates refuses a facet switch while the player is in a match.</summary>
        public const string FacetSwitchRefused = "[Arena] You cannot change facets during an arena match.";

        /// <summary>Player.CheckFacetGates refuses a switch away from the PK facet while queued for the arena.</summary>
        public const string QueuedFacetSwitchRefused = "[Arena] You cannot change facets while queued for or entering an arena match. Use /arena leave first.";

        // ---------------- PvP Template Facets (Docs/Pvp/TEMPLATES.md): every match is templated ----------------

        public const string TemplatesDisabled = "[Arena] Arena templates are switched off, so the arena is closed right now.";
        public const string TemplatesUnavailable = "[Arena] The arena templates are still loading. Try again in a moment.";
        public const string NoTemplateChosen = "[Arena] Choose an arena template first. Type /arena templates to see them, then /arena join <mode> <template>.";
        public const string TemplateNotOffered = "[Arena] The {template} template is not offered for {mode}. Type /arena templates to see what is.";

        /// <summary>The accept-time and dispatch-time re-check: an admin withdrew the template after the player queued on it.</summary>
        public const string TemplateWithdrawn = "[Arena] The {template} template is no longer offered for {mode}, so you were taken out of this match with no penalty. Choose another with /arena templates.";

        public const string TemplateAccountRefused = "[Arena] Characters on the template account cannot enter the arena.";
        public const string TemplateLockedRefused = "[Arena] Your character is still wearing an arena template and is locked until staff restore it. Please contact staff.";

        /// <summary>An AllOrNothing match lost a player whose template could not be put on: to those not yet placed.</summary>
        public const string OtherCouldNotEnter = "[Arena] A player could not enter the match. You are back at the front of the queue.";

        /// <summary>The same, to those already placed (they are returned at once).</summary>
        public const string OtherCouldNotEnterPlaced = "[Arena] A player could not enter the match, so it was called off. You are back at the front of the queue.";

        /// <summary>To the player whose template apply failed at entry. The apply has already said why.</summary>
        public const string TemplateBackstopEjected = "[Arena] You are wearing gear that is not part of your arena template and it could not be taken off, so you have been removed from the match. Please tell staff.";

        public const string EntryFailedSelf = "[Arena] You could not enter the match and were removed from it with no penalty.";

        /// <summary>An FFA lobby lost a player at entry and still has enough to play.</summary>
        public const string SeatRemovedCarriesOn = "[Arena] {names} could not enter the match. The match goes on without them.";

        /// <summary>The arena announcement when the match goes Live: each fighter with their template.</summary>
        public const string Lineup = "[Arena] {mode}: {lineup}.";

        public const string TemplatesHeader = "[Arena] Arena templates:";
        public const string TemplatesRow = "  {key} - {name} ({modes})";
        public const string TemplatesNone = "[Arena] No arena templates are offered right now.";
        public const string TemplatesFooter = "[Arena] Choose one with /arena template <key>, or join directly with /arena join <mode> <key>.{current}";
        public const string TemplatesCurrent = " Your template: {template}.";
        public const string TemplateSet = "[Arena] Your arena template is now {template}. Join with /arena join <mode>.";
        public const string TemplateUnknown = "[Arena] There is no offered arena template called {template}. Type /arena templates to see them.";
        public const string JoinedWithTemplate = "[Arena] You joined the {mode} queue on the {template} template. Players waiting: {count}. Type /arena leave to leave the queue.";

        /// <summary>Sent once to each queued unit while pvp_arena_max_concurrent_matches holds the queue.</summary>
        public const string MatchesFull = "[Arena] All arenas are in use. You stay in the queue and will be matched when one frees up.";

        // ---------------- accept ----------------

        /// <summary>The Yes/No popup text. No "[Arena] " prefix: it is a dialog, not a chat line.</summary>
        public const string AcceptPopup = "A {mode} arena match is ready on {map}. Enter now?";

        public const string AcceptPopupFallback = "[Arena] Your {mode} match is ready. Type /arena accept within {seconds} seconds to enter, or /arena decline.";
        public const string YouDeclined = "[Arena] You declined the match.";
        public const string OtherDeclined = "[Arena] A player declined. You are back at the front of the queue.";
        public const string TooFewAccepted = "[Arena] Not enough players accepted. You are back at the front of the queue.";
        public const string AcceptTimeoutSelf = "[Arena] You did not accept in time and were removed from the queue.";

        // ---------------- match flow ----------------

        /// <summary>Sent by Player.EnterPvpMatch once the masks are on.</summary>
        public const string Entering = "[Arena] Entering the arena. Class abilities, equipment mods, weapon mods and speed bonuses are off for the match.";

        public const string Countdown = "[Arena] The match begins in {seconds}...";
        public const string Start = "[Arena] Fight!";
        public const string Elimination = "[Arena] {victim} was defeated by {killer}.";
        public const string EliminationNoKiller = "[Arena] {victim} has been eliminated.";
        public const string Forfeit = "[Arena] {name} forfeited.";
        public const string LeftTheArena = "[Arena] You left the arena and forfeited the match.";

        /// <summary>The leave-while-in-a-match confirmation popup (PR D). No "[Arena] " prefix: it is a dialog.</summary>
        public const string LeaveConfirmPopup = "Leave the match? You will forfeit and it counts as a loss.";

        public const string TimeWarning = "[Arena] One minute remaining.";
        public const string TimeUpDraw = "[Arena] Time is up. The match is a draw.";

        // ---------------- overtime (Docs/Pvp/DESIGN.md "Overtime") ----------------

        /// <summary>Sent to the healer when a Health kit or Health potion is refused in overtime (healing factor 0). The item is kept.</summary>
        public const string OvertimeHealRefused = "[Arena] Healing is disabled during overtime. Your item was not used.";

        /// <summary>/arena status during overtime.</summary>
        public const string StatusInMatchOvertime = "[Arena] You are in a {mode} match on {map}. Overtime: {remaining} remaining.";

        /// <summary>
        /// "3 minutes", "1 minute", "90 seconds", "1 second": whole minutes when the duration is a whole number of
        /// minutes, otherwise seconds. Pure.
        /// </summary>
        public static string DurationPhrase(int seconds)
        {
            if (seconds >= 60 && seconds % 60 == 0)
            {
                var minutes = seconds / 60;
                return minutes == 1 ? "1 minute" : $"{minutes} minutes";
            }

            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        /// <summary>
        /// The overtime announcement, sent to everyone still in the match when overtime starts. Built from the
        /// values snapshotted onto the match: the duration, the healing factor (already sanitized: 0 = no healing,
        /// under 1 = reduced, otherwise not mentioned), the damage ramp (mentioned only when it is on) and the mode
        /// (a 1v1/2v2 ends in a draw, FFA survivors share first). With the shipped defaults (180 s, healing 0, ramp
        /// off) a 1v1 reads "[Arena] Overtime! No healing for the final 3 minutes. If no one wins, the match is a
        /// draw." Plain ASCII. Pure.
        /// </summary>
        public static string OvertimeAnnouncement(int seconds, double healingFactor, double rampPerMinute, bool survivorsShareFirst)
        {
            var duration = DurationPhrase(seconds);
            var sb = new StringBuilder("[Arena] Overtime! ");

            if (healingFactor == 0.0)
                sb.Append($"No healing for the final {duration}.");
            else if (healingFactor < 1.0)
                sb.Append($"Healing is reduced for the final {duration}.");
            else
                sb.Append($"The final {duration} begins.");

            if (rampPerMinute > 0 && !double.IsInfinity(rampPerMinute))
                sb.Append($" Damage rises by {(rampPerMinute * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}% each minute.");

            sb.Append(survivorsShareFirst
                ? " If time runs out, the survivors share first place."
                : " If no one wins, the match is a draw.");

            return sb.ToString();
        }

        /// <summary>A 1v1/2v2 in which every remaining team went out in the same tick: an unrated draw.</summary>
        public const string DoubleKnockoutDraw = "[Arena] Both sides fell at once. The match is a draw.";

        /// <summary>Sent to a participant who died in the match, as they are taken out of it.</summary>
        public const string YouWereEliminated = "[Arena] You have been eliminated. Returning you to where you were.";

        /// <summary>An admin canceled a match that had already reached Countdown or Live.</summary>
        public const string AdminCanceled = "[Arena] An administrator ended the match. It will not be rated.";
        public const string TimeUpFfa = "[Arena] Time is up. The survivors share first place.";
        public const string Win = "[Arena] Victory!";
        public const string Loss = "[Arena] Defeat.";
        public const string FfaPlacement = "[Arena] You placed {placement} of {total}.";
        public const string RatingChange = "[Arena] {ladder} rating: {before} -> {after} ({delta}).";
        public const string Unrated = "[Arena] This match was not rated.";
        public const string Returning = "[Arena] Returning you to where you were.";
        public const string Canceled = "[Arena] The match was canceled.";

        /// <summary>Player.CheckPKStatusVsTarget refused harm to a teammate (friendly fire off). Sent to the attacker.</summary>
        public const string FriendlyFireAttacker = "[Arena] You cannot harm your teammate.";

        // ---------------- Mark of the Hopeslayer payout (Docs/Pvp/DESIGN.md "Rewards"; the currency was named Blood before 2026-10-06) ----------------

        /// <summary>The currency's display name for <paramref name="amount"/>: "Mark of the Hopeslayer" for exactly 1, otherwise "Marks of the Hopeslayer". Fills the {marks} token of the payout lines.</summary>
        public static string MarkNoun(int amount) => amount == 1 ? "Mark of the Hopeslayer" : "Marks of the Hopeslayer";

        /// <summary>Marks paid for a match and placed in the pack. {marks} is <see cref="MarkNoun"/> of the amount.</summary>
        public const string BloodReceived = "[Arena] You receive {amount} {marks}.";

        /// <summary>The daily cap was already reached: this match paid nothing. One line per match.</summary>
        public const string BloodDailyLimit = "[Arena] You have reached today's Mark of the Hopeslayer limit. This match paid no Marks of the Hopeslayer.";

        /// <summary>Marks earned but not delivered (pack full or too heavy): kept and delivered at the next login.</summary>
        public const string BloodOwed = "[Arena] Your pack could not hold {amount} {marks}. It will be delivered the next time you log in.";

        /// <summary>Battleground counterpart of <see cref="BloodReceived"/>: the arena line with "battleground" in place of "arena".</summary>
        public const string BgMarksReceived = "[Battleground] You receive {amount} {marks}.";

        /// <summary>Battleground counterpart of <see cref="BloodDailyLimit"/> (the battleground daily cap).</summary>
        public const string BgMarksDailyLimit = "[Battleground] You have reached today's Mark of the Hopeslayer limit. This match paid no Marks of the Hopeslayer.";

        /// <summary>Battleground counterpart of <see cref="BloodOwed"/>.</summary>
        public const string BgMarksOwed = "[Battleground] Your pack could not hold {amount} {marks}. It will be delivered the next time you log in.";

        /// <summary>Owed Marks delivered at login. Mode-neutral: the owed balance is shared by arena and battleground matches.</summary>
        public const string BloodOwedDelivered = "[Arena] You receive {amount} {marks} owed from earlier matches.";

        /// <summary>Owed Marks that still did not fit after the login delivery (mode-neutral, shared balance).</summary>
        public const string BloodOwedRemaining = "[Arena] Your pack still could not hold {amount} {marks}. It will be delivered the next time you log in.";

        /// <summary>Sent at login when the PvpMatchReturnPkStatus marker was found and the PK status restored from it.</summary>
        public const string RestoredAtLogin = "[Arena] Your arena match ended while you were away. Your player-killer status has been restored.";

        // ---------------- commands (PR D) ----------------

        /// <summary>/arena leave (not queued, not in a match) and /arena leave confirm with no match to forfeit.</summary>
        public const string NotQueuedOrInMatch = "[Arena] You are not in a queue or a match.";

        /// <summary>/arena leave confirm on a match already resolved for this player (eliminated or already forfeited).</summary>
        public const string AlreadyOutOfMatch = "[Arena] You are already out of this match.";

        /// <summary>/arena accept or /arena decline with no match waiting on an answer.</summary>
        public const string NoPendingMatch = "[Arena] You have no match waiting for an answer.";

        /// <summary>/arena accept or /arena decline for a match already answered.</summary>
        public const string AlreadyAnswered = "[Arena] You have already answered.";

        /// <summary>/arena leave in a match while another Yes/No popup (the accept prompt) is already open.</summary>
        public const string LeaveConfirmFallback = "[Arena] Type /arena leave confirm to forfeit the match. It counts as a loss.";

        /// <summary>/arena status while a match invite is pending this player's answer.</summary>
        public const string StatusAwaitingAccept = "[Arena] Your {mode} match is ready on {map}. Type /arena accept or /arena decline.";

        /// <summary>/arena status while placed in a match that has not reached Live yet (Countdown/Staging).</summary>
        public const string StatusInMatchNotLive = "[Arena] You are in a {mode} match on {map}. It has not started yet.";

        /// <summary>/arena rating and /arena top while PvpRatingStoreState is not Available.</summary>
        public const string RatingsUnavailable = "[Arena] Arena ratings are unavailable right now.";

        // ---------------- Arena Crier (Docs/Pvp/DESIGN.md "Arena Crier") ----------------

        /// <summary>The Crier's own mode label, distinct from <see cref="ModeLabel"/> only in form (the same words for every mode today).</summary>
        public static string CrierModeLabel(string modeKey)
        {
            switch (modeKey)
            {
                case ArenaMapCatalog.OneVOneKey: return "1v1";
                case ArenaMapCatalog.TwoVTwoKey: return "2v2";
                case ArenaMapCatalog.FfaKey: return ModeLabelFfa;
                case BattlegroundModes.RoomKey: return BattlegroundText.ModeLabelBattleground;
                case BattlegroundModes.KothModeKey: return BattlegroundText.ModeLabelKoth;
                case BattlegroundModes.AttackDefendModeKey: return BattlegroundText.ModeLabelAttackDefend;
                default: return modeKey ?? "";
            }
        }

        /// <summary>The periodic /lfg queue announcement. Filled with mode, count and needed; the join arg is the mode key itself.</summary>
        public const string CrierPeriodic = "{mode} arena queue: {count} queued, {needed} more needed. Type /arena join {arg} to play.";

        /// <summary>The last-call /lfg announcement, fired pvp_arena_crier_last_call_delay_seconds after a queue grows to exactly 1 needed.</summary>
        public const string CrierLastCall = "{mode} arena: 1 more player needed to start a match! Type /arena join {arg} now.";

        /// <summary>The periodic Crier template for a queue: the battleground queue has its own wording.</summary>
        public static string CrierPeriodicFor(string modeKey) => modeKey == BattlegroundModes.RoomKey ? BattlegroundText.CrierPeriodic : CrierPeriodic;

        /// <summary>The last-call Crier template for a queue: the battleground queue has its own wording.</summary>
        public static string CrierLastCallFor(string modeKey) => modeKey == BattlegroundModes.RoomKey ? BattlegroundText.CrierLastCall : CrierLastCall;
    }
}
