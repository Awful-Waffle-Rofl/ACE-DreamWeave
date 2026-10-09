using System.Text.RegularExpressions;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Why a one-time class-ability-point grant was allowed or refused. Returned by
    /// <see cref="OneTimeClassAbilityGrant.Decide"/>, which is the whole gate as a pure function so it can
    /// be unit tested without a live Player (ACE.Server.Tests can never build one - the Player constructor
    /// reaches DatabaseManager.Authentication).
    /// </summary>
    public enum OneTimeGrantDecision
    {
        /// <summary>The grant may proceed; the caller stamps the claim only after the points are paid.</summary>
        Allow,

        /// <summary>The item names no claim quest, or names a malformed one. Content bug: refuse, never grant.</summary>
        MalformedQuestName,

        /// <summary>The character already carries the claim stamp. Refuse AND do not consume the item.</summary>
        AlreadyClaimed,

        /// <summary>A mule makes no quest progress, so it can never record a claim.</summary>
        MuleBlocked
    }

    /// <summary>
    /// The one-time-per-character gate on a class-ability-point consumable
    /// (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Trophy", step 11).
    ///
    /// A Gem carrying PropertyInt 9020 ClassAbilityPointValue already routes through the existing audited
    /// CAP funnel - Player.GrantClassAbilityPoints, which moves PropertyInt 9017 AvailableClassAbilityPoints
    /// and 9018 TotalClassAbilityPointsEarned together, so tier-unlock accounting stays correct. That funnel
    /// deliberately has no cap and no per-character memory: the Proving Grounds and Meridian Commendations
    /// already shipped as repeatable CAP items and must stay repeatable.
    ///
    /// This adds the memory, OPT-IN by PropertyString 9017 ClassAbilityGrantQuest, using the quest registry
    /// - the repo's existing one-time-per-character mechanism - exactly as Player_PickupBoons does for
    /// pick-up speed boons. Nothing about the CAP funnel itself changes, and no new player state is
    /// invented: the claim is one quest registry row.
    ///
    /// ORDERING MATTERS, and is the caller's contract: <see cref="Decide"/> is read-only and must run
    /// BEFORE the grant; the claim stamp must be written only AFTER GrantClassAbilityPoints has returned
    /// true. Stamping first would burn the character's single claim on a grant that then failed.
    /// </summary>
    public static class OneTimeClassAbilityGrant
    {
        /// <summary>
        /// A claim quest name is the same shape a pick-up boon key is (Player_PickupBoons.cs): letters,
        /// digits and underscores. QuestManager compares quest names case-insensitively and parses '@' and
        /// '%' as completion/format markers, so neither may appear in an authored claim name.
        /// </summary>
        private static readonly Regex QuestNamePattern = new Regex("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

        /// <summary>True if the authored PropertyString 9017 value is a well-formed quest registry name.</summary>
        public static bool IsValidQuestName(string questName)
        {
            return !string.IsNullOrEmpty(questName) && QuestNamePattern.IsMatch(questName);
        }

        /// <summary>
        /// The complete gate over already-read values. Clause order is deliberate: a malformed name is a
        /// content bug and is reported as such even for a mule, and "already claimed" is checked before the
        /// mule rule so a mule is never told it has already claimed something it has not.
        /// </summary>
        /// <param name="questName">PropertyString 9017 off the item</param>
        /// <param name="hasQuest">QuestManager.HasQuest(questName) for this character</param>
        /// <param name="muleBlocked">Player.MuleBlocked(MuleAction.AdvanceQuest)</param>
        public static OneTimeGrantDecision Decide(string questName, bool hasQuest, bool muleBlocked)
        {
            if (!IsValidQuestName(questName))
                return OneTimeGrantDecision.MalformedQuestName;

            if (hasQuest)
                return OneTimeGrantDecision.AlreadyClaimed;

            if (muleBlocked)
                return OneTimeGrantDecision.MuleBlocked;

            return OneTimeGrantDecision.Allow;
        }

        /// <summary>
        /// The player-facing refusal for a non-Allow decision, so the wording lives beside the rule rather
        /// than at the call site. Returns null for <see cref="OneTimeGrantDecision.Allow"/>.
        /// </summary>
        public static string RefusalMessage(OneTimeGrantDecision decision, string itemName)
        {
            switch (decision)
            {
                case OneTimeGrantDecision.MalformedQuestName:
                    return $"The {itemName} is misconfigured and cannot be used.";

                case OneTimeGrantDecision.AlreadyClaimed:
                    return $"You have already claimed this reward. The {itemName} is inert in your hands, and cannot be claimed again.";

                case OneTimeGrantDecision.MuleBlocked:
                    return "A mule cannot make quest progress.";

                default:
                    return null;
            }
        }
    }
}
