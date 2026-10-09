using System;
using System.Threading;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// /lph window start (unix seconds). Purely in-memory and per-session by construction - a Player is
        /// built at login and discarded at logout, so nothing here is persisted. Opened by PlayerEnterWorld
        /// and reset by /lph start.
        /// </summary>
        private double lumRateWindowStart;

        /// <summary>
        /// Luminance earned since lumRateWindowStart, for the /lph command. AddLuminance runs on landblock
        /// threads, so this is only ever touched through Interlocked.
        /// </summary>
        private long lumRateWindowEarned;

        /// <summary>
        /// Luminance earned in the current /lph window.
        /// </summary>
        public long LumRateWindowEarned => Interlocked.Read(ref lumRateWindowEarned);

        /// <summary>
        /// Unix-seconds start time of the current /lph window.
        /// </summary>
        public double LumRateWindowStart => lumRateWindowStart;

        /// <summary>
        /// Opens a fresh /lph window: clears the accumulator and re-anchors the start time to now.
        /// </summary>
        public void ResetLumRateWindow()
        {
            lumRateWindowStart = Time.GetUnixTime();
            Interlocked.Exchange(ref lumRateWindowEarned, 0);
        }

        /// <summary>
        /// Minimum window length, in seconds, before <see cref="CalcLumPerHour"/> will report a rate.
        /// </summary>
        public const double MinLumRateWindowSeconds = 5.0;

        /// <summary>
        /// Luminance-per-hour rate for a window. Returns null when the window is too short to
        /// produce a meaningful rate (see <see cref="MinLumRateWindowSeconds"/>), so callers can
        /// report the raw total instead of a number divided by ~0.
        /// </summary>
        public static double? CalcLumPerHour(long earned, double elapsedSeconds)
        {
            if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < MinLumRateWindowSeconds)
                return null;

            return earned / elapsedSeconds * 3600.0;
        }

        /// <summary>
        /// Applies luminance modifiers before adding luminance
        /// </summary>
        public void EarnLuminance(long amount, XpType xpType, ShareType shareType = ShareType.All)
        {
            if (IsOlthoiPlayer)
                return;

            // following the same model as Player_Xp
            var questModifier = PropertyManager.GetDouble("quest_lum_modifier").Item;
            var modifier = PropertyManager.GetDouble("luminance_modifier").Item;
            if (xpType == XpType.Quest)
                modifier *= questModifier;

            // should this be passed upstream to fellowship?
            var enchantment = GetXPAndLuminanceModifier(xpType);

            var product = amount * enchantment * modifier;

            // Match EarnXP: guard against overflow / non-finite results before the cast to long.
            if (!double.IsFinite(product) || Math.Abs(product) >= long.MaxValue)
            {
                log.Warn($"{Name}.EarnLuminance({amount}, {shareType}) - out of range; modifier: {modifier}, enchantment: {enchantment}, product: {product}");
                return;
            }

            var m_amount = (long)Math.Round(product);

            // Never pass a negative into GrantLuminance: SplitLuminance casts to (ulong), which would
            // turn a negative into an enormous positive luminance grant. (EarnXP already guards this.)
            if (m_amount < 0)
            {
                log.Warn($"{Name}.EarnLuminance({amount}, {shareType}) - negative m_amount: {m_amount}");
                return;
            }

            GrantLuminance(m_amount, xpType, shareType);
        }

        /// <summary>
        /// Directly grants luminance to the player, without any additional luminance modifiers
        /// </summary>
        public void GrantLuminance(long amount, XpType xpType, ShareType shareType = ShareType.All)
        {
            GrantLuminance(amount, xpType, shareType, false);
        }

        /// <summary>
        /// Directly grants luminance to the player, without any additional luminance modifiers
        /// </summary>
        /// <param name="combatShare">
        /// TRUE when this grant is a fellowship member's share of a fellow's *kill* (set by Fellowship.SplitLuminance).
        /// Together with xpType == Kill this identifies combat-sourced Luminance eligible for the receiving
        /// player's offline bonus. Quest luminance never enters the fellowship split, so it never needs this flag.
        /// </param>
        public void GrantLuminance(long amount, XpType xpType, ShareType shareType, bool combatShare)
        {
            // Mule (WaffleACE): a mule earns no luminance, ever.
            if (MuleBlocked(MuleAction.GainLuminance))
                return;

            // PvP template (progression lock): a templated player earns no luminance. Silent (a grant per kill).
            if (PvpTemplateBlocked(PvpTemplateAction.Luminance) != null)
                return;

            if (IsOlthoiPlayer)
                return;

            if (Fellowship != null && Fellowship.ShareXP && shareType.HasFlag(ShareType.Fellowship) && xpType != XpType.Quest)
            {
                // this will divy up the luminance, and re-call this function
                // with ShareType.Fellowship removed
                Fellowship.SplitLuminance((ulong)amount, xpType, shareType, this);
            }
            else
            {
                // Boost this player's own combat Luminance with their offline bonus. As in GrantXP, this runs
                // after the fellowship split, so it scales only what THIS player receives - their own kill
                // (XpType.Kill) or their share of a fellow's kill (combatShare) - never a fellow's take or
                // quest Luminance (which is excluded because combatShare is false).
                var addAmount = amount;
                if (xpType == XpType.Kill || combatShare)
                    addAmount = ApplyOfflineExperienceBonus(amount);

                AddLuminance(addAmount, xpType);
            }
        }

        private void AddLuminance(long amount, XpType xpType)
        {
            if (amount <= 0)
                return;

            // Monitoring: aggregate, server-wide Luminance firehose (see ServerMetrics / DESIGN.md §4.1).
            ServerMetrics.LumGranted.Add(amount);

            // Analytics: per-character luminance rate (Tier-1). Lock-free Interlocked.Add, flushed off-thread.
            AnalyticsManager.RecordLuminance(this, amount);

            // /lph per-session window accumulator (see ResetLumRateWindow / CalcLumPerHour).
            Interlocked.Add(ref lumRateWindowEarned, amount);

            // WaffleACE: all earned Luminance goes straight to the persistent, uncapped bank
            // (see Player_Bank.cs) instead of the retail available/maximum pool. There is no
            // luminance-flag requirement and no MaximumLuminance cap: any player earns Luminance
            // from the first kill, and it accumulates without limit. The retail AvailableLuminance
            // UI bar is intentionally left untouched (not driven), so we do NOT call UpdateLuminance().
            ModifyBankBalance(PropertyInt64.BankedLuminance, amount);
            RushNextPlayerSave(60);

            if (xpType == XpType.Quest)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"You've earned {amount:N0} Luminance.", ChatMessageType.Broadcast));
        }

        /// <summary>
        /// Spends the amount of luminance specified, deducting it from available (earned) luminance only.
        /// Earned Luminance now banks straight into the uncapped bank, so available Luminance is normally 0
        /// and this only drains any legacy pre-migration balance; it is the low-level primitive that
        /// <see cref="TrySpendLuminanceIncludingBank"/> uses for the available-first portion of a spend.
        /// Gameplay spends should call <see cref="TrySpendLuminanceIncludingBank"/>, not this directly.
        /// </summary>
        public bool SpendLuminance(long amount)
        {
            var available = AvailableLuminance ?? 0;

            if (amount > available)
                return false;

            AvailableLuminance = available - amount;

            UpdateLuminance();

            return true;
        }

        /// <summary>
        /// Sends network message to update luminance
        /// </summary>
        private void UpdateLuminance()
        {
            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableLuminance, AvailableLuminance ?? 0));
        }
    }
}
