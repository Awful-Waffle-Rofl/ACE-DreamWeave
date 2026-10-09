using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Survey-Archivist Teodor Brannock (wcid 1003613), the Meridian NPC who pays the daily-survey reward.
    /// Marked by PropertyBool.DungeonSurveyArchivist and dispatched from Creature.ActOnUse, beside the
    /// ClassAbilityTrainer intercept.
    ///
    /// WHY THIS IS C# AND NOT EMOTES. The NPC used to carry a 19-set Use emote cascade that both spoke and
    /// paid (emote types 21 InqQuest / 30 InqQuestSolves / 49 AwardLevelProportionalXP / 113 AwardLuminance /
    /// 22 StampQuest). Every award was a LITERAL: the XP cap was a constant derived from the level 274 to 275
    /// delta, so a level-275 player farming cheap level-185 gems was paid at the endgame rate for trivial
    /// content. Scaling the award needs computation, and there is no seam to intercept the NPC before its
    /// emotes: WorldObject.OnActivate runs EmoteManager.OnUse(creature) and THEN ActOnUse(activator)
    /// (WorldObject_Use.cs:177-186), so "keep the emotes for flavour, pay in C#" would double-pay. The whole
    /// cascade was therefore deleted from the weenie and reimplemented here, tell lines included.
    ///
    /// THE LATCHING INVARIANT - a mistake here is a repeatable-reward exploit. What is guaranteed is NOT that
    /// paying and stamping happen together: within one tier the payment necessarily runs first, and there is
    /// no way to make the pair atomic. What IS guaranteed is that every tier reached in a visit is STAMPED
    /// EXACTLY ONCE, in one synchronous pass, before any ActionChain is enqueued, WHETHER OR NOT its payment
    /// completed - the stamp is issued from a finally around the grant calls. A tier whose grant throws
    /// mid-payment is therefore still latched, which costs the player part of one tier's reward rather than
    /// leaving that tier unstamped and replayable on their next Use: Plan reads unstamped as unpaid, so the
    /// alternative failure mode is a repeatable grant. On an economy path that trade is the right way round,
    /// and it is deliberate. The throw is logged at ERROR with its tier so a real throw path is diagnosable
    /// rather than silent, and the remaining tiers of the visit still pay.
    ///
    /// The emote rig's protection was EmoteManager.IsBusy, which no longer applies because there are no
    /// emotes; this latch is its only replacement. A second use arriving while the first visit's text is
    /// still printing re-runs <see cref="DungeonSurveyRules.Plan"/> against a ledger that ALREADY carries the
    /// stamps, so it pays nothing and says HELD. Never move a Stamp, a GrantLevelProportionalXp or an
    /// EarnLuminance into the delayed chain: the chain carries TEXT ONLY.
    ///
    /// This file holds every impure read the feature makes - PropertyManager (including the reference level,
    /// whose default is DungeonGemSpec.MaxGemLevel), EnlightenmentXpCurve (which touches DatManager) and the
    /// player's own ring property - exactly the way
    /// FragmentPressStation.BuildLimits holds the attunement feature's four tunables. SurveyLevelRing,
    /// SurveyRewardMath and DungeonSurveyRules.Plan stay pure so they can be unit tested; the Player-driven
    /// branches in this file cannot be (the test harness builds no live Player) and ride on live
    /// verification instead, which is why the reasoning is written out here rather than assumed.
    /// </summary>
    public static class SurveyArchivistStation
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string ScalingEnabledProperty = "dynamic_dungeons_survey_level_scaling";
        public const string RatioExponentProperty = "dynamic_dungeons_survey_ratio_exponent";
        public const string RatioFloorProperty = "dynamic_dungeons_survey_ratio_floor";
        public const string ReferenceLevelProperty = "dynamic_dungeons_survey_reference_level";
        public const string ResetTimezoneProperty = "dynamic_dungeons_survey_reset_timezone";
        public const string ResetHourProperty = "dynamic_dungeons_survey_reset_hour";

        /// <summary>
        /// The daily-reset fallback zone: a fixed UTC-5 offset, used when
        /// <see cref="ResetTimezoneProperty"/> names a zone id .NET cannot resolve. Not a real time zone (no
        /// DST), but it keeps the feature working with a deterministic, roughly-Eastern boundary rather than
        /// throwing.
        /// </summary>
        private static readonly TimeZoneInfo FallbackZone =
            TimeZoneInfo.CreateCustomTimeZone("DynDungeonSurveyFallback", TimeSpan.FromHours(-5), "Survey Fallback (UTC-5)", "Survey Fallback (UTC-5)");

        private static readonly ConcurrentDictionary<string, TimeZoneInfo> ZoneCache = new ConcurrentDictionary<string, TimeZoneInfo>();
        private static readonly ConcurrentDictionary<string, bool> WarnedZoneIds = new ConcurrentDictionary<string, bool>();

        // ---------------- first-contact guide book ----------------

        public const uint GuideBookWcid = 1003625;
        public const string GuideQuest = "DynDungeonGuide";

        public const string GivenMessage =
            "Take this. It is what I have written down so far about the threads, and it is not finished. Add to it.";
        public const string NoRoomMessage =
            "I have something written for you, and no hand free to put it in. Make room and speak to me again.";

        // ---------------- tell text ----------------
        // Reproduced verbatim from the emote sets this replaced, so the NPC's voice did not change when its
        // rig did. Set 3 (window expired) and set 4 (nothing filed today) spoke the same line; one constant
        // covers both, which is also what Plan collapses them into (SurveyBand.None).

        public const string NothingFiledMessage =
            "The wing pins doors. Fragments do not pin; they open, they are walked, and then they are gone, so what you count inside is the only page we will ever hold. File a survey and come back.";

        public const string BandTenMessage = "The ledger shows ten or more surveys under your name today.";
        public const string BandFiveMessage = "The ledger shows five or more surveys under your name today.";
        public const string BandLt5Message = "The ledger shows fewer than five surveys under your name today.";

        public const string PayTenMessage = "Ten. There is a page above mine for this, and I do not know who reads it.";
        public const string PayFiveMessage = "Five in a day. The column is filling faster than the founders said it could.";
        public const string PayOneMessage = "One survey filed. The ledger takes the page and pays for it.";

        /// <summary>
        /// {0} is the time until the survey day turns over, rendered by <see cref="SurveyDay.FormatDuration"/>
        /// (e.g. "3h 12m", or "12m" under one hour).
        /// </summary>
        public const string HeldMessageFormat =
            "The ledger holds your page for today. The book turns to a clean page in {0}; come back then.";

        /// <summary>
        /// Seconds between the band line and each line downstream of it. The deleted rig used emote `delay`,
        /// which EmoteManager applies as a PRE-delay (EmoteManager.cs:1690-1702): the first tell of a visit
        /// had delay 0 and every tell downstream of it had delay 1, to stop the lines stacking. Same here -
        /// the band line goes out immediately and each later line gets a one second pre-delay.
        /// </summary>
        private const double LineDelaySeconds = 1.0;

        /// <summary>
        /// Dispatch gate. Returns false for every NPC that is not the Archivist, at the cost of one property
        /// dictionary miss, so the hot Creature.ActOnUse path is not measurably changed.
        /// </summary>
        public static bool TryHandleUse(WorldObject npc, Player player)
        {
            if (npc == null || player == null)
                return false;

            if (!(npc.GetProperty(PropertyBool.DungeonSurveyArchivist) ?? false))
                return false;

            HandleTurnIn(npc, player);

            return true;
        }

        /// <summary>
        /// One visit. Decide, pay and latch synchronously; then let the text catch up.
        /// </summary>
        private static void HandleTurnIn(WorldObject npc, Player player)
        {
            var questManager = player.QuestManager;
            if (questManager == null)
                return;

            TryGiveGuide(npc, player, questManager);

            var clock = BuildClock();
            var ledger = new QuestManagerSurveyLedger(questManager);
            var plan = DungeonSurveyRules.Plan(ledger, clock);

            if (plan.Band == SurveyBand.None)
            {
                Tell(npc, player, NothingFiledMessage);
                return;
            }

            // The band line is the first tell of the visit, so it goes out with no delay.
            Tell(npc, player, BandMessage(plan.Band));

            var limits = BuildLimits();
            var ring = SurveyLevelRing.Parse(player.GetProperty(PropertyString.DungeonSurveyLevels));
            var playerLevel = player.Level ?? 1;

            var lines = new List<string>();

            // ---- THE SYNCHRONOUS PASS. Everything that changes state happens here, in this loop, before a
            // ---- single ActionChain exists. See the latching invariant in the type doc comment.
            foreach (var tier in plan.TiersToPay)
            {
                if (!SurveyRewardMath.TryGetTier(tier, out var definition))
                {
                    log.Error($"[DYNDUNGEON] survey turn-in for {player.Name} named tier {tier}, which is not in SurveyRewardMath.Tiers; skipping it");
                    continue;
                }

                // Deliberately OUTSIDE the try below: Compute is pure and its only throw is a null-limits
                // guard that cannot fire here, and nothing has been paid yet at this point, so this tier
                // needs no latch if it somehow fails.
                var award = SurveyRewardMath.Compute(definition, ring, limits, playerLevel);

                try
                {
                    // GrantLevelProportionalXp treats max <= 0 as NO CAP (Player_Xp.cs:609-623), so a zero
                    // cap would pay the full uncapped percentage of next-level XP - the exact opposite of
                    // what a zero means here. Refuse the XP instead; the luminance half still pays.
                    if (award.XpCap > 0)
                        player.GrantLevelProportionalXp(award.XpPercent, 0, award.XpCap);
                    else
                        log.Warn($"[DYNDUNGEON] survey tier {tier} for {player.Name} computed a zero XP cap (xpLevel={award.XpLevel}, chartEntries={limits.XpTotals?.Count ?? 0}); paying no XP for it");

                    if (award.Luminance > 0)
                        player.EarnLuminance(award.Luminance, XpType.Quest, ShareType.None);

                    lines.Add(PayMessage(tier));

                    log.Info($"[DYNDUNGEON] survey payout {player.Name} tier={tier} n={award.Sampled} ring=[{string.Join(",", ring.Levels)}] playerLevel={playerLevel} avg={award.AverageLevel} xpLevel={award.XpLevel} ratio={award.Ratio.ToString("0.####", CultureInfo.InvariantCulture)} cap={award.XpCap} lum={award.Luminance}");
                }
                catch (Exception ex)
                {
                    // Neither grant has a known throw path for legitimate input - both guard NaN, overflow
                    // and negatives before touching state - so reaching here means something genuinely
                    // unexpected happened, and it must be diagnosable rather than silent. The tier is still
                    // latched by the finally below, so the player loses part of one tier's reward instead of
                    // being handed a replayable grant. No pay line is added: this tier did not pay.
                    log.Error($"[DYNDUNGEON] survey tier {tier} for {player.Name} threw mid-payment (cap={award.XpCap} lum={award.Luminance}); latching it anyway", ex);
                }
                finally
                {
                    // THE LATCH, and the reason it is in a finally rather than after the grants: an unstamped
                    // tier reads as unpaid to Plan, so a throw between the grant and an ordinary stamp would
                    // leave the tier replayable on the next Use.
                    ledger.Stamp(definition.Quest);
                }
            }

            if (plan.Held)
                lines.Add(string.Format(HeldMessageFormat, SurveyDay.FormatDuration(TimeSpan.FromSeconds(clock.NextReset() - clock.Now))));

            if (lines.Count == 0)
                return;

            // TEXT ONLY from here. Awards and stamps are already done, so a chain that never runs (the player
            // logs out mid-visit) costs the player nothing but the lines.
            var chain = new ActionChain();

            foreach (var line in lines)
            {
                var text = line;
                chain.AddDelaySeconds(LineDelaySeconds);
                chain.AddAction(player, () => Tell(npc, player, text));
            }

            chain.EnqueueChain();
        }

        /// <summary>
        /// Hands the player the "Thread Gems: What We Know" guide book the first time they USE this NPC
        /// (their first conversation at this station), not their first survey turn-in - this runs BEFORE
        /// DungeonSurveyRules.Plan is even consulted, so a player who has never filed a survey still gets
        /// the book on that first Use. Then stamps <see cref="GuideQuest"/> so it is never handed out twice.
        /// Called as the FIRST thing in HandleTurnIn; this is a separate feature from the survey payout and
        /// does not touch the ledger.
        ///
        /// DELIBERATELY THE OPPOSITE LATCHING RULE from the survey payout above. The payout stamps in a
        /// finally BECAUSE an unstamped tier is a repeatable ECONOMY grant - losing part of one payment to a
        /// throw is the safer failure mode there. This is a one-off flavour item with no economy value, so
        /// the trade goes the other way: a failed give must NOT latch, or the player silently never receives
        /// it. Only a confirmed TryCreateInInventoryWithNetworking success stamps the quest; a missing weenie
        /// or a full pack both leave the quest unstamped so the book is offered again on the player's next
        /// visit. The quest row's max_Solves 1 is a second guard against a double-grant race.
        /// </summary>
        private static void TryGiveGuide(WorldObject npc, Player player, QuestManager questManager)
        {
            if (questManager.HasQuest(GuideQuest))
                return;

            var book = WorldObjectFactory.CreateNewWorldObject(GuideBookWcid);
            if (book == null)
            {
                log.Warn($"[DYNDUNGEON] guide book weenie {GuideBookWcid} is not in the world database; apply Content/sql/weenies/1003625 Thread Gems What We Know.sql. Not stamping {GuideQuest} so it is offered again next visit.");
                return;
            }

            if (!player.TryCreateInInventoryWithNetworking(book))
            {
                book.Destroy();
                Tell(npc, player, NoRoomMessage);
                return;
            }

            questManager.Stamp(GuideQuest);

            // ORDERING NOTE: this Tell goes out immediately, in the same tick as the band line or
            // NothingFiledMessage HandleTurnIn sends right after this call returns. That is accepted rather
            // than sequenced into the delayed chain below, because that chain carries TEXT ONLY - nothing
            // that changes state may move into it, and the give itself (book creation, the stamp) already
            // happened above, synchronously.
            Tell(npc, player, GivenMessage);
        }

        /// <summary>
        /// The daily-reset clock for "now": dynamic_dungeons_survey_reset_timezone and
        /// dynamic_dungeons_survey_reset_hour read fresh, against Time.GetUnixTime(). Callers build one of
        /// these per visit (or per filed survey) and read every subsequent day-boundary decision from it, so
        /// the whole visit measures against the same instant. See the doc comment on
        /// <see cref="ThreadDungeonManager.RecordSurvey"/> for why the filing path builds its own clock
        /// INSIDE the enqueued delegate rather than reusing one built earlier.
        /// </summary>
        public static SurveyDayClock BuildClock()
        {
            var zone = ResolveZone(PropertyManager.GetString(ResetTimezoneProperty).Item);
            var resetHour = (int)Math.Clamp(PropertyManager.GetLong(ResetHourProperty).Item, 0L, 23L);

            return new SurveyDayClock((uint)Time.GetUnixTime(), zone, resetHour);
        }

        /// <summary>
        /// Resolves a time zone id to a <see cref="TimeZoneInfo"/>, caching per id string so a live server
        /// does not re-resolve on every visit. An id .NET cannot find (TimeZoneNotFoundException /
        /// InvalidTimeZoneException - the runtime may have no tzdata, see FallbackZone) falls back to a fixed
        /// UTC-5 offset, with a WARN logged once per id rather than once per visit.
        /// Internal, not private: the PvP arena Blood daily cap (Pvp/PvpArenaRewards.ResolveZone) shares this
        /// resolver and its cache rather than growing a second one with different fallback behaviour.
        /// </summary>
        internal static TimeZoneInfo ResolveZone(string zoneId)
        {
            return ZoneCache.GetOrAdd(zoneId ?? string.Empty, id =>
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
                {
                    if (WarnedZoneIds.TryAdd(id, true))
                        log.Warn($"[DYNDUNGEON] daily reset time zone '{id}' (survey or arena Blood reset) could not be resolved; falling back to a fixed UTC-5 offset", ex);

                    return FallbackZone;
                }
            });
        }

        /// <summary>
        /// Every PropertyManager and dat-backed read the feature makes, resolved once per visit. The same
        /// object then feeds every tier of the visit, so the figure logged and the figure paid are provably
        /// the same number read the same way. Sanitizing lives in the SurveyRewardLimits constructor.
        /// </summary>
        public static SurveyRewardLimits BuildLimits()
        {
            var referenceLevel = ReadReferenceLevel();

            return new SurveyRewardLimits(
                referenceLevel,
                EnlightenmentXpCurve.ExtendedTotals,
                PropertyManager.GetDouble(RatioExponentProperty).Item,
                PropertyManager.GetDouble(RatioFloorProperty).Item,
                PropertyManager.GetBool(ScalingEnabledProperty).Item);
        }

        /// <summary>
        /// The reference level the survey reward curve measures a run against: the denominator of
        /// (avgGemLevel / reference)^k, which scales luminance only (XP is measured at min(player level, avgGemLevel), not at this level). Defaults to
        /// <see cref="DungeonGemSpec.MaxGemLevel"/>, which is what ruling R6 asks for - a raised gem ceiling
        /// rescales the payout curve with it, rather than paying the endgame rate for what is now mid-tier
        /// content.
        ///
        /// It is a TUNABLE rather than a direct MaxGemLevel read because the two answer different questions.
        /// MaxGemLevel is how high a gem may go; this is what "the endgame rate" means to the payout. Pinning
        /// this below the ceiling is the one-command way to raise the ceiling WITHOUT repricing the surveys
        /// players have already been filing - the rescale is the default, not the only option.
        ///
        /// Sanitized into the XP chart's own index range (a chart too short to index collapses the range to 1).
        /// The value only feeds the luminance ratio now; the clamp is kept so the tunable stays bounded.
        /// </summary>
        public static int ReadReferenceLevel()
        {
            var totals = EnlightenmentXpCurve.ExtendedTotals;
            var top = totals == null || totals.Count < 2 ? 1 : totals.Count - 1;

            return (int)Math.Clamp(PropertyManager.GetLong(ReferenceLevelProperty).Item, 1L, (long)top);
        }

        private static string BandMessage(SurveyBand band)
        {
            switch (band)
            {
                case SurveyBand.Ten: return BandTenMessage;
                case SurveyBand.Five: return BandFiveMessage;
                default: return BandLt5Message;
            }
        }

        /// <summary>
        /// The line spoken when a tier actually pays. Keyed on the tier's survey count, matching the deleted
        /// sets 10/12/14 (ten and five) and 16/18 (one).
        /// </summary>
        private static string PayMessage(int tier)
        {
            if (tier == SurveyRewardMath.Tiers[SurveyRewardMath.Tiers.Count - 1].Surveys)
                return PayTenMessage;

            if (tier == SurveyRewardMath.Tiers[1].Surveys)
                return PayFiveMessage;

            return PayOneMessage;
        }

        /// <summary>The exact shape EmoteType.Tell sends (EmoteManager.cs:1436-1443).</summary>
        private static void Tell(WorldObject npc, Player player, string message)
        {
            if (player?.Session == null)
                return;

            player.Session.Network.EnqueueSend(new GameEventTell(npc, message, player, ChatMessageType.Tell));
        }
    }
}
