using System;
using System.Linq;

using ACE.Common.Extensions;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// A player earns XP through natural progression, ie. kills and quests completed
        /// </summary>
        /// <param name="amount">The amount of XP being added</param>
        /// <param name="xpType">The source of XP being added</param>
        /// <param name="shareable">True if this XP can be shared with Fellowship</param>
        public void EarnXP(long amount, XpType xpType, ShareType shareType = ShareType.All)
        {
            //Console.WriteLine($"{Name}.EarnXP({amount}, {sharable}, {fixedAmount})");

            // apply xp modifiers.  Quest XP is multiplicative with general XP modification
            var questModifier = PropertyManager.GetDouble("quest_xp_modifier").Item;
            var modifier = PropertyManager.GetDouble("xp_modifier").Item;
            if (xpType == XpType.Quest)
                modifier *= questModifier;

            // should this be passed upstream to fellowship / allegiance?
            var enchantment = GetXPAndLuminanceModifier(xpType);

            var product = amount * enchantment * modifier;

            // Guard against overflow / non-finite results before the cast to long. Legitimate XP is
            // well within range; a value outside it indicates a bad modifier/enchantment and would
            // otherwise wrap to a garbage (possibly negative) amount on the cast.
            if (!double.IsFinite(product) || Math.Abs(product) >= long.MaxValue)
            {
                log.Warn($"{Name}.EarnXP({amount}, {shareType}) - out of range; modifier: {modifier}, enchantment: {enchantment}, product: {product}");
                return;
            }

            var m_amount = (long)Math.Round(product);

            if (m_amount < 0)
            {
                log.Warn($"{Name}.EarnXP({amount}, {shareType})");
                log.Warn($"modifier: {modifier}, enchantment: {enchantment}, m_amount: {m_amount}");
                return;
            }

            GrantXP(m_amount, xpType, shareType);
        }

        /// <summary>
        /// Directly grants XP to the player, without the XP modifier
        /// </summary>
        /// <param name="amount">The amount of XP to grant to the player</param>
        /// <param name="xpType">The source of the XP being granted</param>
        /// <param name="shareable">If TRUE, this XP can be shared with fellowship members</param>
        public void GrantXP(long amount, XpType xpType, ShareType shareType = ShareType.All)
        {
            GrantXP(amount, xpType, shareType, false);
        }

        /// <summary>
        /// Directly grants XP to the player, without the XP modifier
        /// </summary>
        /// <param name="combatShare">
        /// TRUE when this grant is a fellowship member's share of a fellow's *kill* (set by Fellowship.SplitXp).
        /// Together with xpType == Kill (a kill landing directly on the player) this identifies combat-sourced
        /// XP that is eligible for the receiving player's offline bonus. Quest XP never enters the fellowship
        /// split, so it never arrives here typed XpType.Fellowship and never needs this flag.
        /// </param>
        public void GrantXP(long amount, XpType xpType, ShareType shareType, bool combatShare)
        {
            // Defense in depth: UpdateXpAndLevel adds the amount to BOTH TotalExperience and
            // AvailableExperience, so a negative one silently DESTROYS a character's experience. EarnXP
            // guards its own product, but the direct GrantXP callers (proficiency, allegiance passup,
            // fellowship shares, emote skill awards, admin grants) did not, and Proficiency.OnSuccessUse
            // reached here with a negative clamp for every character past the retail level-275 total.
            if (amount < 0)
            {
                log.Warn($"{Name}.GrantXP({amount}, {xpType}, {shareType}) - refusing negative amount");
                return;
            }

            // Mule (WaffleACE): a mule earns no experience, ever. Deliberately does NOT mirror the Olthoi
            // branch's UpdateXpVitae call below - a mule never receives vitae in the first place (see
            // Player_Death), so there is nothing to work off here.
            if (MuleBlocked(MuleAction.GainExperience))
                return;

            // PvP template (progression lock): a templated player earns no experience. Silent, because kills in a
            // match reach this on every hit; the entry message already told the player.
            if (PvpTemplateBlocked(PvpTemplateAction.Experience) != null)
                return;

            if (IsOlthoiPlayer)
            {
                if (HasVitae)
                    UpdateXpVitae(amount);

                return;
            }

            if (Fellowship != null && Fellowship.ShareXP && shareType.HasFlag(ShareType.Fellowship) && xpType != XpType.Quest)
            {
                // Item XP is never split: the earner's equipped items take the kill's FULL amount plus the
                // earner's own offline bonus, however many fellows share the character XP. Granted here because
                // only this call still holds the full amount - the earner's share re-enters below as
                // Kill + combatShare, and the item grant at the bottom skips it. Fellows' shares arrive typed
                // XpType.Fellowship and never level items. Previewed rather than applied, and before SplitXp:
                // the earner's share stamps the offline-bonus activity marker when it lands, and a bank read
                // taken after that stamp misclassifies the idle seconds before this kill as drain.
                if (xpType == XpType.Kill)
                    GrantItemXP(PreviewOfflineExperienceBonus(amount));

                // this will divy up the XP, and re-call this function
                // with ShareType.Fellowship removed
                Fellowship.SplitXp((ulong)amount, xpType, shareType, this);
                return;
            }

            // Boost this player's own combat XP by their offline bonus, if any. This runs after the fellowship
            // split, so it only ever scales what THIS player receives - their own kill (XpType.Kill) or their
            // share of a fellow's kill (combatShare) - and never the shares distributed to other fellows. The
            // allegiance passup below deliberately keeps using the un-boosted amount, so the bonus never leaks
            // into vassal passup or a fellow's take. Quest XP is excluded because combatShare is false, and
            // quest XP never enters the fellowship split above, so it can never arrive here typed
            // XpType.Fellowship either.
            var levelAmount = amount;
            if (xpType == XpType.Kill || combatShare)
                levelAmount = ApplyOfflineExperienceBonus(amount);

            // Item XP takes the offline bonus but not the alt bonus below, so it is captured in between.
            var itemAmount = levelAmount;

            // Alt-character catch-up bonus: while this character is below the highest-progressed character on
            // its account, double (or alt_character_bonus_multiplier) the leveling XP it earns. Applied after
            // the offline bonus so the two multiply (a fresh alt with banked offline time gets 4x), and only to
            // this character's own directly-earned XP - its kills, quest turn-ins, or its share of a fellow's
            // kill (combatShare). The allegiance passup and item XP below deliberately exclude it, so the bonus
            // never leaks into vassal passup or item leveling. See Player_AltCharacterBonus.
            if (xpType == XpType.Kill || xpType == XpType.Quest || combatShare)
                levelAmount = ApplyAltCharacterBonus(levelAmount);

            // Make sure UpdateXpAndLevel is done on this players thread
            EnqueueAction(new ActionEventDelegate(() => UpdateXpAndLevel(levelAmount, xpType)));

            // for passing XP up the allegiance chain,
            // this function is only called at the very beginning, to start the process.
            if (shareType.HasFlag(ShareType.Allegiance))
                UpdateXpAllegiance(amount);

            // only certain types of XP are granted to items: this player's own unsplit kill (offline-boosted) or
            // quest turn-in (quest XP never takes the offline bonus). A kill that went through the fellowship
            // split was already granted to items in full above, so the earner's share arriving back here as
            // Kill + combatShare is skipped.
            if (xpType == XpType.Quest || (xpType == XpType.Kill && !combatShare))
                GrantItemXP(itemAmount);
        }

        /// <summary>
        /// Adds XP to a player's total XP, handles triggers (vitae, level up)
        /// </summary>
        private void UpdateXpAndLevel(long amount, XpType xpType)
        {
            // XP accrues until the chart's hard ceiling. This guard used to sit at the character's PERSONAL
            // cap (275 + 5 per enlightenment), which froze all XP - total AND available - the moment a
            // character hit it, leaving nothing to spend at endgame. It now only bites at level 1445.
            var maxLevel = GetPlayerMaxLevel();
            var maxLevelXp = EnlightenmentXpCurve.GetTotalXPRequiredForLevel(maxLevel);

            if (Level < maxLevel)
            {
                var addAmount = amount;

                var amountLeftToEnd = (long)maxLevelXp - TotalExperience ?? 0;
                if (amount > amountLeftToEnd)
                    addAmount = amountLeftToEnd;

                AvailableExperience += addAmount;
                TotalExperience += addAmount;

                // Monitoring: aggregate, server-wide XP firehose (ServerMetrics -> dotnet-monitor ->
                // Prometheus). Counts the amount actually added, post fellowship split and offline bonus.
                // Per-player rates belong to ace_analytics, not here (cardinality). See DESIGN.md §4.1.
                ServerMetrics.XpGranted.Add(addAmount);

                // Analytics: per-character xp rate (Tier-1). Lock-free Interlocked.Add, flushed off-thread.
                AnalyticsManager.RecordXp(this, addAmount);

                var xpTotalUpdate = new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.TotalExperience, TotalExperience ?? 0);
                var xpAvailUpdate = new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0);
                Session.Network.EnqueueSend(xpTotalUpdate, xpAvailUpdate);

                CheckForLevelup();
            }

            if (xpType == XpType.Quest)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"You've earned {amount:N0} experience.", ChatMessageType.Broadcast));

            if (HasVitae && xpType != XpType.Allegiance)
                UpdateXpVitae(amount);
        }

        /// <summary>
        /// Optionally passes XP up the Allegiance tree
        /// </summary>
        private void UpdateXpAllegiance(long amount)
        {
            if (!HasAllegiance) return;

            AllegianceManager.PassXP(AllegianceNode, (ulong)amount);
        }

        /// <summary>
        /// Handles updating the vitae penalty through earned XP
        /// </summary>
        /// <param name="amount">The amount of XP to apply to the vitae penalty</param>
        private void UpdateXpVitae(long amount)
        {
            var vitae = EnchantmentManager.GetVitae();

            if (vitae == null)
            {
                log.Error($"{Name}.UpdateXpVitae({amount}) vitae null, likely due to cross-thread operation or corrupt EnchantmentManager cache. Please report this.");
                log.Error(Environment.StackTrace);
                return;
            }

            var vitaePenalty = vitae.StatModValue;
            var startPenalty = vitaePenalty;

            var maxPool = (int)VitaeCPPoolThreshold(vitaePenalty, DeathLevel.Value);
            var curPool = (VitaeCpPool ?? 0) + amount;
            var vitaeWorkedOff = false;
            while (curPool >= maxPool)
            {
                curPool -= maxPool;
                vitaePenalty = EnchantmentManager.ReduceVitae();
                if (vitaePenalty == 1.0f)
                {
                    vitaeWorkedOff = true;
                    break;
                }
                maxPool = (int)VitaeCPPoolThreshold(vitaePenalty, DeathLevel.Value);
            }

            // VitaeCpPool is a PropertyInt, so the store below narrows a long to an int. On the loop's
            // NORMAL exit that is safe by construction - curPool is under maxPool, which is already an
            // int. The break is not: it fires with the entire unspent remainder still in curPool, and
            // one large grant (a /myxp or @grantxp that dwarfs the pool threshold) makes that remainder
            // exceed int.MaxValue. The cast is unchecked, so it wrapped to a garbage - frequently
            // negative - pool that then persisted on the character and was sent to the client.
            //
            // Zero rather than clamp: the break only happens once vitae is fully worked off, and a
            // remainder has nothing left to buy at that point. Player_Death zeroes the pool again when
            // it arms the next penalty, so this matches what the next death would do anyway.
            VitaeCpPool = vitaeWorkedOff ? 0 : (int)curPool;

            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.VitaeCpPool, VitaeCpPool.Value));

            if (vitaePenalty != startPenalty)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("Your experience has reduced your Vitae penalty!", ChatMessageType.Magic));
                EnchantmentManager.SendUpdateVitae();
            }

            if (vitaePenalty.EpsilonEquals(1.0f) || vitaePenalty > 1.0f)
            {
                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(2.0f);
                actionChain.AddAction(this, () =>
                {
                    var vitae = EnchantmentManager.GetVitae();
                    if (vitae != null)
                    {
                        var curPenalty = vitae.StatModValue;
                        if (curPenalty.EpsilonEquals(1.0f) || curPenalty > 1.0f)
                            EnchantmentManager.RemoveVitae();
                    }
                });
                actionChain.EnqueueChain();
            }
        }

        /// <summary>
        /// Returns the maximum possible character level
        /// </summary>
        public static uint GetMaxLevel()
        {
            return (uint)DatManager.PortalDat.XpTable.CharacterLevelXPList.Count - 1;
        }

        /// <summary>
        /// Returns this character's maximum level: the synthesized chart's hard ceiling
        /// (<see cref="EnlightenmentXpCurve.HardCeilingLevel"/>, 1445), the same for every character.
        ///
        /// Characters used to carry a PERSONAL cap of 275 + 5 per enlightenment, which is why this is a
        /// per-player instance method rather than a constant. That cap is gone: levels now run continuously
        /// from 1 to the ceiling and enlightenment no longer raises anything
        /// (Docs/ClassAbilities/XP-LANE-SPEC.md sec 2.2). The signature is kept so the several call sites
        /// that bound against "this player's max" keep reading correctly.
        ///
        /// XP accrual still freezes AT the returned level - <see cref="UpdateXpAndLevel"/> clamps the final
        /// grant to that level's cumulative total - but the ceiling is now the chart's own limit rather than
        /// a gate a player is expected to pass. Nothing carries a character past it.
        /// </summary>
        public int GetPlayerMaxLevel() => EnlightenmentXpCurve.HardCeilingLevel;

        /// <summary>
        /// Returns TRUE if player >= their personal MaxLevel
        /// </summary>
        public bool IsMaxLevel => Level >= GetPlayerMaxLevel();

        /// <summary>
        /// Returns the remaining XP required to reach a level
        /// </summary>
        public long? GetRemainingXP(uint level)
        {
            if (level < 1 || level > (uint)EnlightenmentXpCurve.HardCeilingLevel)
                return null;

            var levelTotalXP = EnlightenmentXpCurve.ExtendedTotals[(int)level];

            return (long)levelTotalXP - TotalExperience.Value;
        }

        /// <summary>
        /// Returns the remaining XP required to the next level
        /// </summary>
        public ulong GetRemainingXP()
        {
            var maxLevel = GetPlayerMaxLevel();
            if (Level >= maxLevel)
                return 0;

            var nextLevelTotalXP = EnlightenmentXpCurve.ExtendedTotals[Level.Value + 1];
            return nextLevelTotalXP - (ulong)TotalExperience.Value;
        }

        /// <summary>
        /// Returns the total XP required to reach a level
        /// </summary>
        public static ulong GetTotalXP(int level)
        {
            if (level < 0 || level > EnlightenmentXpCurve.HardCeilingLevel)
                return 0;

            return EnlightenmentXpCurve.ExtendedTotals[level];
        }

        /// <summary>
        /// Returns the total amount of XP required for a player reach max level
        /// </summary>
        public static long MaxLevelXP
        {
            get
            {
                var xpTable = DatManager.PortalDat.XpTable.CharacterLevelXPList;

                return (long)xpTable[xpTable.Count - 1];
            }
        }

        /// <summary>
        /// Returns the XP required to go from level A to level B
        /// </summary>
        public ulong GetXPBetweenLevels(int levelA, int levelB)
        {
            // special case for max level - clamp to the synthesized chart's hard ceiling so the
            // fellowship level-proportional split stays correct past the retail cap. The clamp lives in
            // the pure helper, whose ceiling is ExtendedTotals' last index, i.e. HardCeilingLevel.
            return EnlightenmentXpCurve.GetXPBetweenLevels(EnlightenmentXpCurve.ExtendedTotals, levelA, levelB);
        }

        public ulong GetXPToNextLevel(int level)
        {
            return GetXPBetweenLevels(level, level + 1);
        }

        /// <summary>
        /// Determines if the player has advanced a level
        /// </summary>
        private void CheckForLevelup()
        {
            var maxLevel = GetPlayerMaxLevel();

            if (Level >= maxLevel) return;

            var startingLevel = Level;
            bool creditEarned = false;

            // increases until the correct level is found (the +1 index is guarded against the hard ceiling
            // as well, though maxLevel already clamps to it)
            while ((Level ?? 0) + 1 <= EnlightenmentXpCurve.HardCeilingLevel
                && (ulong)(TotalExperience ?? 0) >= EnlightenmentXpCurve.ExtendedTotals[(Level ?? 0) + 1])
            {
                Level++;

                // increase the skill credits if the chart allows this level to grant a credit
                // (0 past level 275 - the synthesized tail grants no skill credits, and this guarded
                // accessor is what keeps the index off the end of CharacterLevelSkillCreditList)
                var levelCredits = EnlightenmentXpCurve.GetSkillCreditsForLevel(Level ?? 0);
                if (levelCredits > 0)
                {
                    AvailableSkillCredits += (int)levelCredits;
                    TotalSkillCredits += (int)levelCredits;
                    creditEarned = true;
                }

                // break if we reach max
                if (Level == maxLevel)
                {
                    PlayParticleEffect(PlayScript.WeddingBliss, Guid);
                    break;
                }
            }

            if (Level > startingLevel)
            {
                // maxLevel is the chart's hard ceiling now, not a per-character gate, so nothing carries a
                // character further and the message must not promise otherwise (see GetPlayerMaxLevel)
                var message = (Level == maxLevel) ? $"You have reached the maximum level of {Level}. There is nothing beyond this." : $"You are now level {Level}!";

                message += (AvailableSkillCredits > 0) ? $"\nYou have {AvailableExperience:#,###0} experience points and {AvailableSkillCredits} skill credits available to raise skills and attributes." : $"\nYou have {AvailableExperience:#,###0} experience points available to raise skills and attributes.";

                var levelUp = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.Level, Level ?? 1);
                var currentCredits = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, AvailableSkillCredits ?? 0);

                // scan up to the player's personal max for the next credit-granting level - this covers both
                // the retail chart (<= 275) and the post-275 milestones (every 25 levels). The message is
                // omitted only when no credit milestone remains below their cap.
                if (Level != maxLevel && !creditEarned)
                {
                    var nextLevelWithCredits = 0;

                    for (int i = (Level ?? 0) + 1; i <= maxLevel; i++)
                    {
                        if (EnlightenmentXpCurve.GetSkillCreditsForLevel(i) > 0)
                        {
                            nextLevelWithCredits = i;
                            break;
                        }
                    }
                    if (nextLevelWithCredits > 0)
                        message += $"\nYou will earn another skill credit at level {nextLevelWithCredits}.";
                }

                if (Fellowship != null)
                    Fellowship.OnFellowLevelUp(this);

                if (AllegianceNode != null)
                    AllegianceNode.OnLevelUp();

                Session.Network.EnqueueSend(levelUp);

                SetMaxVitals();

                // grant any class ability points earned by crossing a level milestone (idempotent catch-up,
                // so a multi-level jump pays every milestone crossed) - DESIGN.md sec 2a
                GrantMilestoneClassAbilityPoints();

                // one-time notice the first time this level-up crosses the facet slot-2 threshold
                SendFacetUnlockNoticeIfDue();

                // one-time Thread-Guide notice the first time this level-up reaches level 50
                SendThreadGuideNoticeIfDue();

                // play level up effect
                PlayParticleEffect(PlayScript.LevelUp, Guid);

                Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Advancement), currentCredits);
            }
        }

        /// <summary>
        /// Spends the amount of XP specified, deducting it from available experience
        /// </summary>
        /// <param name="checkFacetReachability">
        /// TRUE (the default) runs the Player Facets out-of-reach warning after the debit, which is right
        /// for every spend that leaves the per-facet skill system - attributes, vitals, class ability point
        /// purchases, an emote's negative award. Pass FALSE only from SpendSkillXp: a skill raise moves the
        /// experience into the live build's PP, which cannot put a facet out of reach, and at this point
        /// in a skill raise the PP has not been added yet, so the check would warn falsely.
        /// </param>
        public bool SpendXP(long amount, bool sendNetworkUpdate = true, bool checkFacetReachability = true)
        {
            if (amount > AvailableExperience)
                return false;

            var availableBefore = AvailableExperience ?? 0;

            AvailableExperience -= amount;

            if (sendNetworkUpdate)
                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0));

            if (checkFacetReachability)
                WarnFacetsPutOutOfReach(availableBefore);

            return true;
        }

        /// <summary>
        /// Tries to spend all of the players Xp into Attributes, Vitals and Skills
        /// </summary>
        public void SpendAllXp(bool sendNetworkUpdate = true)
        {
            SpendAllAvailableAttributeXp(Strength, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Endurance, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Coordination, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Quickness, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Focus, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Self, sendNetworkUpdate);

            SpendAllAvailableVitalXp(Health, sendNetworkUpdate);
            SpendAllAvailableVitalXp(Stamina, sendNetworkUpdate);
            SpendAllAvailableVitalXp(Mana, sendNetworkUpdate);

            foreach (var skill in Skills)
            {
                if (skill.Value.AdvancementClass >= SkillAdvancementClass.Trained)
                    SpendAllAvailableSkillXp(skill.Value, sendNetworkUpdate);
            }
        }

        /// <summary>
        /// Gives available XP of the amount specified, without increasing total XP
        /// </summary>
        public void RefundXP(long amount)
        {
            AvailableExperience += amount;

            var xpUpdate = new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0);
            Session.Network.EnqueueSend(xpUpdate);
        }

        /// <summary>
        /// Restitution for characters who enlightened before the system was retired
        /// (Docs/ClassAbilities/XP-LANE-SPEC.md sec 5.2 / 5.2a). Called on login; a no-op for everyone else.
        ///
        /// Enlightenment zeroed TotalExperience AND AvailableExperience on every cycle, so the XP those
        /// characters spent left no trace on their own record - only the Enlightenment count survives.
        /// <see cref="EnlightenmentRetirement"/> converts that count back into the level a continuously
        /// leveling character would hold, and this credits it.
        ///
        /// IDEMPOTENT BY CONSTRUCTION, with no marker property: both pools are raised to a computed FLOOR and
        /// never lowered. Re-running recomputes the same floor and changes nothing, and a character who has
        /// since out-earned it keeps their own progress - which is why this is a comparison rather than the
        /// bare assignment sec 5.4 first sketched.
        ///
        /// AvailableExperience is credited too, and that is deliberate: past roughly level 275 the pool grows
        /// far faster than skills and attributes can absorb it, so an enlightening character was sitting on a
        /// large UNSPENT balance that <c>RemoveSkills</c> destroyed outright. Netting off what the character
        /// has actually spent keeps the result equal to the natural continuous-leveling state instead of
        /// gifting the current cycle's training a second time.
        /// </summary>
        public void GrantEnlightenmentRetirementCredit()
        {
            if (Enlightenment <= 0)
                return;

            var totals = EnlightenmentXpCurve.ExtendedTotals;
            var level = EnlightenmentRetirement.EquivalentLevel(Enlightenment, totals);
            if (level <= 0)
                return;

            var target = (long)totals[level];

            // already credited, or the character has out-earned the credit since - either way, leave it alone
            if ((TotalExperience ?? 0) >= target)
                return;

            var spent = 0L;
            foreach (var skill in Skills.Values)
                spent += skill.ExperienceSpent;
            foreach (var attribute in Attributes.Values)
                spent += attribute.ExperienceSpent;
            foreach (var vital in Vitals.Values)
                spent += vital.ExperienceSpent;

            var availableFloor = Math.Max(0, target - spent);

            TotalExperience = target;
            if ((AvailableExperience ?? 0) < availableFloor)
                AvailableExperience = availableFloor;

            Session.Network.EnqueueSend(
                new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.TotalExperience, TotalExperience ?? 0),
                new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0));

            CheckForLevelup();

            SendMessage($"Enlightenment has been retired. The experience your {Enlightenment:N0} enlightenment{(Enlightenment == 1 ? "" : "s")} consumed has been returned to you as levels and unassigned experience; you are now level {Level ?? 1:N0}. Everything they granted you is unchanged.", ChatMessageType.Broadcast);

            SaveBiotaToDatabase();
        }

        public void HandleMissingXp()
        {
            var verifyXp = GetProperty(PropertyInt64.VerifyXp) ?? 0;
            if (verifyXp == 0) return;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(5.0f);
            actionChain.AddAction(this, () =>
            {
                var xpType = verifyXp > 0 ? "unassigned experience" : "experience points";

                var msg = $"This character was missing some {xpType} --\nYou have gained an additional {Math.Abs(verifyXp).ToString("N0")} {xpType}!";

                Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

                if (verifyXp < 0)
                {
                    // add to character's total XP
                    TotalExperience -= verifyXp;

                    CheckForLevelup();
                }

                RemoveProperty(PropertyInt64.VerifyXp);
            });

            actionChain.EnqueueChain();
        }

        /// <summary>
        /// Returns the total amount of XP required to go from vitae to vitae + 0.01
        /// </summary>
        /// <param name="vitae">The current player life force, ie. 0.95f vitae = 5% penalty</param>
        /// <param name="level">The player DeathLevel, their level on last death</param>
        private double VitaeCPPoolThreshold(float vitae, int level)
        {
            return (Math.Pow(level, 2.5) * 2.5 + 20.0) * Math.Pow(vitae, 5.0) + 0.5;
        }

        /// <summary>
        /// Raise the available XP by a percentage of the current level XP or a maximum
        /// </summary>
        public void GrantLevelProportionalXp(double percent, long min, long max)
        {
            // The amount is computed by the pure helper so ThreadGuideLadder.RewardCap can be tested against
            // the exact formula this pays with.
            var scaledXP = EnlightenmentXpCurve.LevelProportionalXp(EnlightenmentXpCurve.ExtendedTotals, Level.Value, percent, min, max);

            // apply xp modifiers?
            EarnXP(scaledXP, XpType.Quest, ShareType.Allegiance);
        }

        /// <summary>
        /// The player earns XP for items that can be leveled up
        /// by killing creatures and completing quests,
        /// while those items are equipped.
        /// </summary>
        public void GrantItemXP(long amount)
        {
            foreach (var item in EquippedObjects.Values.Where(i => i.HasItemLevel))
                GrantItemXP(item, amount);
        }

        public void GrantItemXP(WorldObject item, long amount)
        {
            var prevItemLevel = item.ItemLevel.Value;
            var addItemXP = item.AddItemXP(amount);

            if (addItemXP > 0)
                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(item, PropertyInt64.ItemTotalXp, item.ItemTotalXp.Value));

            // handle item leveling up
            var newItemLevel = item.ItemLevel.Value;
            if (newItemLevel > prevItemLevel)
            {
                OnItemLevelUp(item, prevItemLevel);

                var actionChain = new ActionChain();
                actionChain.AddAction(this, () =>
                {
                    var msg = $"Your {item.Name} has increased in power to level {newItemLevel}!";
                    Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

                    EnqueueBroadcast(new GameMessageScript(Guid, PlayScript.AetheriaLevelUp));
                });
                actionChain.EnqueueChain();
            }
        }

        /// <summary>
        /// Returns the multiplier to XP and Luminance from Trinkets and Augmentations
        /// </summary>
        public float GetXPAndLuminanceModifier(XpType xpType)
        {
            var enchantmentBonus = EnchantmentManager.GetXPBonus();

            var augBonus = 0.0f;
            if (xpType == XpType.Kill && AugmentationBonusXp > 0)
                augBonus = AugmentationBonusXp * 0.05f;

            var modifier = 1.0f + enchantmentBonus + augBonus;
            //Console.WriteLine($"XPAndLuminanceModifier: {modifier}");

            return modifier;
        }
    }
}
