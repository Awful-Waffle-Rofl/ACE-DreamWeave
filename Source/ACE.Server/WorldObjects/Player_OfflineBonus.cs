using System;

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
        /// the combat XP and Luminance THAT CHARACTER RECEIVES is boosted by offline_bonus_multiplier. Banked
        /// time drains 1:1 with time spent online. The intent is to let a player experiment with alternate
        /// characters while their primary catches back up at a relatively similar pace once they return to it.
        ///
        /// The boost is applied per-receiver, after the fellowship split, to combat-sourced XP/Luminance only:
        ///  - a bonus holder's own kills are boosted
        ///  - a bonus holder's share of a FELLOW's kill is also boosted (the bonus follows whoever receives it)
        ///  - but the boost never inflates the shares distributed to OTHER fellows: a fellow always receives,
        ///    and the earner always contributes, at the un-boosted amount (each fellow's own bonus, if any,
        ///    is applied independently when their share lands on them)
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
        /// Wall-clock unix time (seconds) that the online drain of <see cref="OfflineExperienceBonusRemaining"/>
        /// was last reconciled against. Set at login; in-memory only (drain only happens while online).
        /// </summary>
        private double offlineBonusLastCheck;

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
        /// Reconciles the banked bonus time against wall-clock time spent online since the last check,
        /// draining it 1:1 (floored at zero) and re-anchoring the drain clock. Mutating; call from the
        /// player's own thread (Heartbeat / logout).
        /// </summary>
        public void UpdateOfflineBonus()
        {
            var now = Time.GetUnixTime();

            if (offlineBonusLastCheck <= 0)
            {
                offlineBonusLastCheck = now;
                return;
            }

            var elapsed = now - offlineBonusLastCheck;
            offlineBonusLastCheck = now;

            if (elapsed <= 0)
                return;

            OfflineExperienceBonusRemaining = OfflineBonus.Drain(OfflineExperienceBonusRemaining, elapsed);
        }

        /// <summary>
        /// Returns the currently-available banked bonus time, in seconds, without mutating stored state.
        /// Safe to read from any thread (e.g. the combat XP path, which may run off the player's thread).
        /// </summary>
        public double GetOfflineExperienceBonusRemaining()
        {
            var remaining = OfflineExperienceBonusRemaining;
            if (remaining <= 0)
                return 0;

            if (offlineBonusLastCheck <= 0)
                return remaining;

            var elapsed = Time.GetUnixTime() - offlineBonusLastCheck;

            return OfflineBonus.Drain(remaining, elapsed);
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
                Session.Network.EnqueueSend(new GameMessageSystemChat("You have no offline experience bonus banked. Log this character out to bank bonus time while you play others.", ChatMessageType.Broadcast));
                return;
            }

            var percent = (int)Math.Round(PropertyManager.GetDouble("offline_bonus_multiplier").Item * 100);

            Session.Network.EnqueueSend(new GameMessageSystemChat($"Offline experience bonus: +{percent}% to your combat experience and Luminance for the next {FormatOfflineBonusDuration(remaining)}.", ChatMessageType.Broadcast));
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
