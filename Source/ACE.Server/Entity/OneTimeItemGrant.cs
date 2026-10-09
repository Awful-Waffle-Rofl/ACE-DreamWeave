using System.Text.RegularExpressions;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Why a one-time-per-character UseCreateItem payout was allowed or refused. Returned by
    /// <see cref="OneTimeItemGrant.Decide"/>, which is the whole gate as a pure function so it can be
    /// unit tested without a live Player (ACE.Server.Tests can never build one - the Player constructor
    /// reaches DatabaseManager.Authentication).
    /// </summary>
    public enum OneTimeItemGrantDecision
    {
        /// <summary>The payout may proceed; the caller stamps the claim only after the items are actually created.</summary>
        Allow,

        /// <summary>The item names no grant quest, or names a malformed one. Content bug: refuse, never pay out.</summary>
        MalformedQuestName,

        /// <summary>The character's quest row for this name is not yet solvable again (already claimed, cooldown active).</summary>
        OnCooldown,

        /// <summary>A mule makes no quest progress, so it can never record a claim.</summary>
        MuleBlocked
    }

    /// <summary>
    /// The one-time-per-character payout gate on a UseCreateItem consumable (code review fix, RoZ round
    /// 11 - first carried by Content/sql/weenies/1003862 Laboratory Trade Note Pouch.sql). This is a
    /// DELIBERATE SIBLING of ACE.Server.ClassAbilities.OneTimeClassAbilityGrant, not a rename of it -
    /// see that class's own header for its design citation. That gate is scoped to
    /// ClassAbilityPointValue gems and uses QuestManager.HasQuest (a permanent, cooldown-blind "ever
    /// claimed" check, correct for a lifetime CAP trophy). This gate is scoped to UseCreateItem gems and
    /// uses QuestManager.CanSolve instead, so the quest row's own min_Delta/max_Solves genuinely govern
    /// the payout cadence - the SAME primitives Player_Inventory.VerifyQuest already uses for the
    /// per-character PICKUP gate on the same class of item, so both gates on one item behave
    /// consistently.
    ///
    /// WHY THIS EXISTS: a generator can stock several copies of an item behind a Quest (33) pickup gate
    /// so one character cannot strip a container empty, but PropertyString.Quest is cleared from the
    /// item (GeneratorId is cleared too) the moment it reaches a pack - after that it is an ordinary
    /// item that can be dropped, traded, or handed to an alt. Several legitimately-claimed copies
    /// pooled onto one character would each still pay out under the pickup gate alone. This gate closes
    /// that: the payout itself, not the item copy, is what is limited to once per character.
    ///
    /// ORDERING MATTERS, and is the caller's contract: <see cref="Decide"/> is read-only and must run
    /// BEFORE the payout is created; the claim stamp must be written only AFTER the items have actually
    /// been placed in the character's possession. Stamping first would burn the character's one payout
    /// on an attempt that then failed (full pack, out of inventory slots).
    /// </summary>
    public static class OneTimeItemGrant
    {
        /// <summary>
        /// A grant quest name is the same shape a class-ability grant quest name is
        /// (OneTimeClassAbilityGrant.cs): letters, digits and underscores. QuestManager compares quest
        /// names case-insensitively and parses '@' and '%' as completion/format markers, so neither may
        /// appear in an authored name.
        /// </summary>
        private static readonly Regex QuestNamePattern = new Regex("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

        /// <summary>True if the authored PropertyString 9018 value is a well-formed quest registry name.</summary>
        public static bool IsValidQuestName(string questName)
        {
            return !string.IsNullOrEmpty(questName) && QuestNamePattern.IsMatch(questName);
        }

        /// <summary>
        /// The complete gate over already-read values. Clause order is deliberate: a malformed name is a
        /// content bug and is reported as such even for a mule, and "on cooldown" is checked before the
        /// mule rule so a mule is never told it has already claimed something it has not.
        /// </summary>
        /// <param name="questName">PropertyString 9018 off the item</param>
        /// <param name="canSolve">QuestManager.CanSolve(questName) for this character</param>
        /// <param name="muleBlocked">Player.MuleBlocked(MuleAction.AdvanceQuest)</param>
        public static OneTimeItemGrantDecision Decide(string questName, bool canSolve, bool muleBlocked)
        {
            if (!IsValidQuestName(questName))
                return OneTimeItemGrantDecision.MalformedQuestName;

            if (!canSolve)
                return OneTimeItemGrantDecision.OnCooldown;

            if (muleBlocked)
                return OneTimeItemGrantDecision.MuleBlocked;

            return OneTimeItemGrantDecision.Allow;
        }

        /// <summary>
        /// The player-facing refusal for a non-Allow decision, so the wording lives beside the rule
        /// rather than at the call site. Returns null for <see cref="OneTimeItemGrantDecision.Allow"/>.
        /// Deliberately NOT the raw QuestManager.HandleSolveError countdown text ("998d 23h 59m 12s") -
        /// a player who already claimed this payout and sees more copies of the item sitting nearby
        /// needs a plain reason, not a timestamp arithmetic problem.
        /// </summary>
        public static string RefusalMessage(OneTimeItemGrantDecision decision, string itemName)
        {
            switch (decision)
            {
                case OneTimeItemGrantDecision.MalformedQuestName:
                    return $"The {itemName} is misconfigured and cannot be used.";

                case OneTimeItemGrantDecision.OnCooldown:
                    return $"You have already claimed this reward. The {itemName} is inert in your hands, and cannot be claimed again.";

                case OneTimeItemGrantDecision.MuleBlocked:
                    return "A mule cannot make quest progress.";

                default:
                    return null;
            }
        }
    }
}
