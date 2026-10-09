using System;
using System.Threading;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// The "offline bonus" system rewards time a character spends logged out with an equal amount of
        /// banked bonus time (capped by offline_bonus_max_seconds). While a character has banked bonus time,
        /// the combat XP and Luminance THAT CHARACTER RECEIVES is boosted by offline_bonus_multiplier. The
        /// intent is to let a player experiment with alternate characters while their primary catches back up
        /// at a relatively similar pace once they return to it.
        ///
        /// The bank is driven ENTIRELY by an automatic, activity-gated clock - there is no player-facing pause.
        /// The three states are:
        ///  - OFFLINE: accrues 1:1 (AccrueOfflineBonus, at login).
        ///  - ONLINE and ACTIVE (earning qualifying XP/Luminance, plus a trailing offline_bonus_idle_timeout_seconds
        ///    window after the last one): drains 1:1.
        ///  - ONLINE and IDLE (no qualifying grant for longer than the idle timeout): accrues again, at
        ///    offline_bonus_idle_accrual_rate per idle second, capped by offline_bonus_max_seconds like any
        ///    other accrual.
        /// "Qualifying" is intentionally narrow: only combat-sourced XP/Luminance that reaches
        /// ApplyOfflineExperienceBonus counts as activity. Skill use (XpType.Proficiency) and allegiance/vassal
        /// passup XP never reach that method (see the two call sites in Player_Xp.GrantXP / Player_Luminance.
        /// GrantLuminance), so grinding skills or receiving passup does NOT keep the bank draining - only combat
        /// does. Quest turn-ins are excluded the same way, since quest XP never reaches ApplyOfflineExperienceBonus
        /// either.
        ///
        /// The boost is applied per-receiver, after the fellowship split, to combat-sourced XP/Luminance only:
        ///  - a bonus holder's own kills are boosted
        ///  - a bonus holder's share of a FELLOW's kill is also boosted (the bonus follows whoever receives it)
        ///  - but the boost never inflates the shares distributed to OTHER fellows: a fellow always receives,
        ///    and the earner always contributes, at the un-boosted amount (each fellow's own bonus, if any,
        ///    is applied independently when their share lands on them)
        ///  - a bonus holder's item XP from their own kill is boosted too, at the kill's full pre-split amount
        ///
        /// The bonus deliberately does NOT apply to:
        ///  - quest XP / Luminance (including quest XP shared into a fellowship, which also arrives as
        ///    <see cref="XpType.Fellowship"/> - this is why combat shares are flagged explicitly rather than
        ///    inferred from the XpType alone)
        ///  - allegiance / vassal passup XP
        /// See Player_Xp.GrantXP / Player_Luminance.GrantLuminance (the combatShare overloads) and
        /// Fellowship.SplitXp / SplitLuminance for the wiring.
        /// </summary>

        /// <summary>
        /// Banked offline bonus time remaining for this character, in seconds.
        /// </summary>
        public double OfflineExperienceBonusRemaining
        {
            get => GetProperty(PropertyFloat.OfflineExperienceBonusRemaining) ?? 0;
            set
            {
                if (value <= 0)
                    RemoveProperty(PropertyFloat.OfflineExperienceBonusRemaining);
                else
                    SetProperty(PropertyFloat.OfflineExperienceBonusRemaining, value);
            }
        }

        /// <summary>
        /// Wall-clock unix time (seconds) that the online drain/accrual of <see cref="OfflineExperienceBonusRemaining"/>
        /// was last reconciled against. Set at login; in-memory only (drain/accrual only happens while online).
        /// </summary>
        private double offlineBonusLastCheck;

        /// <summary>
        /// Unix seconds of this character's most recent qualifying (combat-sourced) XP/Luminance grant, i.e. the
        /// last time <see cref="ApplyOfflineExperienceBonus"/> ran. Zero until the first such grant this session,
        /// so a freshly logged-in character is treated as idle (accruing) until its first kill. Written from the
        /// XP/Luminance grant path, which can run off the player's own thread during a fellowship split, and read
        /// from the player's heartbeat thread - use Interlocked accessors rather than a plain field read/write.
        /// </summary>
        private long offlineBonusLastEarnUnix;

        /// <summary>
        /// End of the current "active" window for online-bonus purposes: offline_bonus_idle_timeout_seconds after
        /// the last qualifying XP/Luminance grant, or 0 if no such grant has happened yet (treated as "already
        /// idle" by <see cref="OfflineBonus.SplitOnlineInterval"/>).
        /// </summary>
        private double OfflineBonusActiveWindowEnd()
        {
            var lastEarn = Interlocked.Read(ref offlineBonusLastEarnUnix);
            return lastEarn == 0 ? 0 : lastEarn + PropertyManager.GetLong("offline_bonus_idle_timeout_seconds", 300).Item;
        }

        /// <summary>
        /// Seconds this character was offline at its most recent login, as accrued by <see cref="AccrueOfflineBonus"/>.
        /// Consumed once by <see cref="SendOfflineBonusLoginMessage"/> to produce the "welcome back" message. Zero
        /// when the feature is disabled or nothing was banked (e.g. a brand-new character).
        /// </summary>
        private double offlineBonusLastAccruedSeconds;

        /// <summary>
        /// Called at login. Grants banked bonus time equal to the seconds this character spent offline
        /// since its last logoff, capped at offline_bonus_max_seconds, then anchors the online drain clock.
        /// </summary>
        public void AccrueOfflineBonus()
        {
            var now = Time.GetUnixTime();

            // anchor the drain clock regardless, so a disabled -> enabled toggle doesn't retroactively
            // drain a full session on the first online reconcile.
            offlineBonusLastCheck = now;
            offlineBonusLastAccruedSeconds = 0;

            if (!PropertyManager.GetBool("offline_bonus_enabled").Item)
                return;

            var maxSeconds = PropertyManager.GetLong("offline_bonus_max_seconds", 86400).Item;

            var logoff = LogoffTimestamp ?? 0;

            // seconds spent offline since last logoff (0 for a brand-new character with no prior logoff)
            var offlineSeconds = (logoff > 0 && now > logoff) ? now - logoff : 0;

            if (offlineSeconds <= 0)
                return;

            OfflineExperienceBonusRemaining = OfflineBonus.Accrue(OfflineExperienceBonusRemaining, offlineSeconds, maxSeconds);
            offlineBonusLastAccruedSeconds = offlineSeconds;
        }

        /// <summary>
        /// Sends the "welcome back" login message summarizing the time this character was away and the resulting
        /// banked bonus balance. No-op if the feature is disabled or nothing was accrued this login (a brand-new
        /// character, or a re-login with no offline gap). Call once, after the player is in-world.
        /// </summary>
        public void SendOfflineBonusLoginMessage()
        {
            if (!PropertyManager.GetBool("offline_bonus_enabled").Item || offlineBonusLastAccruedSeconds <= 0)
                return;

            var awayFor = FormatOfflineBonusDuration(offlineBonusLastAccruedSeconds);
            var banked = FormatOfflineBonusDuration(GetOfflineExperienceBonusRemaining());

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"Welcome back - you were away for {awayFor}. Your offline experience bonus bank has been increased to {banked}.",
                ChatMessageType.Broadcast));

            offlineBonusLastAccruedSeconds = 0;
        }

        /// <summary>
        /// TRUE if, as of the most recent <see cref="UpdateOfflineBonus"/> reconcile, the character was within
        /// its active window (i.e. the bank would be draining if it had anything in it). This tracks the window
        /// state ALONE, independent of the bank balance - see <see cref="offlineBonusWasBanked"/> for the balance
        /// side of the edge detection. Used only for the /offlinebonus transition chat pings; not authoritative
        /// between reconciles.
        /// </summary>
        private bool offlineBonusWasActive;

        /// <summary>
        /// TRUE if, as of the most recent <see cref="UpdateOfflineBonus"/> reconcile, the bank was nonzero.
        /// Paired with <see cref="offlineBonusWasActive"/> to detect the specific "ran out while still fighting"
        /// transition and fire the one-shot exhaustion notice, distinct from the active/idle transition pings.
        /// </summary>
        private bool offlineBonusWasBanked;

        /// <summary>
        /// Reconciles the banked bonus time against wall-clock time spent online since the last check. The
        /// interval since the last reconcile is split (via <see cref="OfflineBonus.SplitOnlineInterval"/>) into
        /// an active portion - before the trailing idle timeout following the character's most recent qualifying
        /// XP/Luminance grant - which drains the bank 1:1, and an idle portion, which accrues it at
        /// offline_bonus_idle_accrual_rate per second (capped by offline_bonus_max_seconds). Re-anchors the
        /// reconcile clock unconditionally, even on an early return, so that toggling offline_bonus_enabled off
        /// and back on can never retroactively drain or accrue a large stale block. Mutating; call from the
        /// player's own thread (Heartbeat / logout).
        /// </summary>
        public void UpdateOfflineBonus()
        {
            var now = Time.GetUnixTime();
            var start = offlineBonusLastCheck;

            // re-anchor first, before any early return below - see the doc comment above
            offlineBonusLastCheck = now;

            if (start <= 0 || now <= start)
                return;

            if (!PropertyManager.GetBool("offline_bonus_enabled").Item)
                return;

            var (activeSeconds, idleSeconds) = OfflineBonus.SplitOnlineInterval(start, now, OfflineBonusActiveWindowEnd());

            var maxSeconds = PropertyManager.GetLong("offline_bonus_max_seconds", 86400).Item;
            var idleRate = PropertyManager.GetDouble("offline_bonus_idle_accrual_rate", 1.0).Item;

            var current = OfflineExperienceBonusRemaining;
            var updated = OfflineBonus.Drain(current, activeSeconds);
            updated = OfflineBonus.Accrue(updated, idleSeconds * idleRate, maxSeconds);

            // only write back when the value actually changed: the property setter takes the biota writer
            // lock and dirties the biota on every call, and this runs from the ~5s heartbeat for every
            // player with a nonzero bank - writing unconditionally would dirty every such biota every tick
            if (updated != current)
                OfflineExperienceBonusRemaining = updated;

            // the active/idle window transition and the banked/exhausted balance transition are classified
            // together by the pure OfflineBonus.ClassifyTransition (see its doc comment for the rules) - this
            // method just supplies the before/after state, reads/writes the tracking bools, and sends the
            // resulting chat. No branching policy lives here.
            var isActive = now < OfflineBonusActiveWindowEnd();
            var isBanked = updated > 0;

            var transition = OfflineBonus.ClassifyTransition(offlineBonusWasActive, isActive, offlineBonusWasBanked, isBanked);

            switch (transition)
            {
                case OfflineBonus.OfflineBonusTransition.Activated:
                    Session?.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Your offline experience bonus is now active - {FormatOfflineBonusDuration(updated)} remaining.",
                        ChatMessageType.Broadcast));
                    break;

                case OfflineBonus.OfflineBonusTransition.Banking:
                    Session?.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Your offline experience bonus is now idle and banking again - {FormatOfflineBonusDuration(updated)} stored.",
                        ChatMessageType.Broadcast));
                    break;

                case OfflineBonus.OfflineBonusTransition.Exhausted:
                    Session?.Network.EnqueueSend(new GameMessageSystemChat(
                        "Your offline experience bonus is exhausted.",
                        ChatMessageType.Broadcast));
                    break;
            }

            offlineBonusWasActive = isActive;
            offlineBonusWasBanked = isBanked;
        }

        /// <summary>
        /// Returns the currently-available banked bonus time, in seconds, without mutating stored state. Applies
        /// the same active/idle split as <see cref="UpdateOfflineBonus"/>, so this can return a value HIGHER than
        /// the stored one - it projects idle accrual forward to the current instant rather than waiting for the
        /// next heartbeat reconcile. Safe to read from any thread (e.g. the combat XP path, which may run off
        /// the player's thread); never writes.
        /// </summary>
        public double GetOfflineExperienceBonusRemaining()
        {
            var remaining = OfflineExperienceBonusRemaining;

            if (offlineBonusLastCheck <= 0)
                return remaining;

            var now = Time.GetUnixTime();
            if (now <= offlineBonusLastCheck)
                return remaining;

            var (activeSeconds, idleSeconds) = OfflineBonus.SplitOnlineInterval(offlineBonusLastCheck, now, OfflineBonusActiveWindowEnd());

            var maxSeconds = PropertyManager.GetLong("offline_bonus_max_seconds", 86400).Item;
            var idleRate = PropertyManager.GetDouble("offline_bonus_idle_accrual_rate", 1.0).Item;

            var projected = OfflineBonus.Drain(remaining, activeSeconds);
            return OfflineBonus.Accrue(projected, idleSeconds * idleRate, maxSeconds);
        }

        /// <summary>
        /// TRUE if this character currently has offline bonus time banked and the feature is enabled.
        /// </summary>
        public bool IsOfflineExperienceBonusActive =>
            PropertyManager.GetBool("offline_bonus_enabled").Item && GetOfflineExperienceBonusRemaining() > 0;

        /// <summary>
        /// The character's currently-banked offline bonus time formatted for display (e.g. "12h 5m"), for the
        /// bank balance listing. Returns "none" when the bank is empty.
        /// </summary>
        public string OfflineExperienceBonusDisplay
        {
            get
            {
                var remaining = GetOfflineExperienceBonusRemaining();
                return remaining > 0 ? FormatOfflineBonusDuration(remaining) : "none";
            }
        }

        /// <summary>
        /// Applies the offline bonus multiplier to an amount of combat XP or Luminance landing on this player,
        /// if the feature is enabled and the character has banked bonus time. Returns the amount unchanged
        /// otherwise. The caller is responsible for only passing combat-sourced amounts that land on this
        /// player - their own kill, or their share of a fellow's kill - and never allegiance passup or quest
        /// amounts (see the callers in GrantXP / GrantLuminance).
        /// </summary>
        public long ApplyOfflineExperienceBonus(long amount)
        {
            if (amount <= 0 || !PropertyManager.GetBool("offline_bonus_enabled").Item)
                return amount;

            // read the bank BEFORE stamping the activity marker below. GetOfflineExperienceBonusRemaining
            // splits [offlineBonusLastCheck, now] against the active window, so if the stamp ran first it
            // would widen that window to include this instant and misclassify the idle seconds between the
            // last grant and THIS one as active (i.e. drain), when they were genuinely idle right up until
            // this kill landed.
            var boosted = PreviewOfflineExperienceBonus(amount);

            // stamp this as activity UNCONDITIONALLY from here, even if the bank above turned out to be empty -
            // a character actively fighting with an empty bank must not be treated as idle and start accruing.
            //
            // NOTE: this only corrects the value used for THIS grant's multiplier decision. It does NOT
            // eliminate the persisted skew: the next heartbeat still reconciles the whole
            // [offlineBonusLastCheck, now] span against this new, wider window, so up to one heartbeat
            // interval (~5s) of genuinely idle time before this grant is still counted as drain in the
            // stored bank. That residual is accepted by design - settling the bank right here would mean
            // mutating biota state from this call site, which can run off the player's own thread during a
            // fellowship split, and that trade is worse than a ~5s rounding error. Do not "fix" this further.
            Interlocked.Exchange(ref offlineBonusLastEarnUnix, (long)Time.GetUnixTime());

            return boosted;
        }

        /// <summary>
        /// What <see cref="ApplyOfflineExperienceBonus"/> would return for <paramref name="amount"/> right now,
        /// WITHOUT stamping the activity marker. For pricing a second grant off the same kill before the
        /// stamping grant runs - the fellowship split's full-amount item XP (see Player_Xp.GrantXP) - since a
        /// bank read taken after the stamp misclassifies the idle seconds before the kill as drain.
        /// </summary>
        public long PreviewOfflineExperienceBonus(long amount)
        {
            if (amount <= 0 || !PropertyManager.GetBool("offline_bonus_enabled").Item)
                return amount;

            var multiplier = PropertyManager.GetDouble("offline_bonus_multiplier").Item;

            return OfflineBonus.Apply(amount, GetOfflineExperienceBonusRemaining(), multiplier);
        }

        /// <summary>
        /// Sends a chat summary of the character's current offline bonus status.
        /// </summary>
        public void ShowOfflineExperienceBonusStatus()
        {
            if (!PropertyManager.GetBool("offline_bonus_enabled").Item)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("The offline experience bonus is not enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            var remaining = GetOfflineExperienceBonusRemaining();

            if (remaining <= 0)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat(
                    "You have no offline experience bonus banked. Log this character out to bank bonus time while you play others.",
                    ChatMessageType.Broadcast));
                return;
            }

            var percent = (int)Math.Round(PropertyManager.GetDouble("offline_bonus_multiplier").Item * 100);
            var idleTimeout = FormatOfflineBonusDuration(PropertyManager.GetLong("offline_bonus_idle_timeout_seconds", 300).Item);

            var now = Time.GetUnixTime();
            var spending = now < OfflineBonusActiveWindowEnd();

            var state = spending
                ? "currently being SPENT (draining while you fight)"
                : $"currently BANKING (idle for longer than the {idleTimeout} timeout, so it's growing again instead of draining)";

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"Offline experience bonus: +{percent}% to your combat experience and Luminance for the next {FormatOfflineBonusDuration(remaining)}, {state}.",
                ChatMessageType.Broadcast));
        }

        private static string FormatOfflineBonusDuration(double seconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, seconds));

            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours}h {span.Minutes}m";
            if (span.TotalMinutes >= 1)
                return $"{span.Minutes}m {span.Seconds}s";

            return $"{span.Seconds}s";
        }
    }
}
