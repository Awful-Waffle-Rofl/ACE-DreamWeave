using System;
using System.Text.RegularExpressions;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // Pick-up animation speed quest boons - see Docs/Plans/pickup-boon-plan.md. Permanent per-character
        // bonuses are derived at read time from the quest registry, one row PickupBoon_<Key> per claimed
        // boon (max_Solves 1) - NOT a new player biota property. This mirrors the class-ability rank cache
        // shape (Player_ClassAbilities.cs), including cache invalidation on claim.

        /// <summary>
        /// Every pick-up speed boon quest name has this prefix; the suffix after it is the boon's key
        /// (e.g. "Firecut" for quest PickupBoon_Firecut), and must also be the gem's PickupBoonKey value.
        /// </summary>
        public const string PickupBoonQuestPrefix = "PickupBoon_";

        private static readonly Regex PickupBoonKeyPattern = new Regex("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

        /// <summary>
        /// True if the given boon key suffix (the part after PickupBoonQuestPrefix) is well-formed.
        /// </summary>
        public static bool IsValidPickupBoonKey(string key)
        {
            return !string.IsNullOrEmpty(key) && PickupBoonKeyPattern.IsMatch(key);
        }

        /// <summary>
        /// In-memory cache of the player's claimed pick-up speed boon count, so repeated pickups don't pay a
        /// string-keyed quest registry scan every time. Built lazily, invalidated by InvalidatePickupBoonCache
        /// (called by the claim path).
        /// </summary>
        private int? pickupBoonCountCache;

        /// <summary>
        /// Number of permanent pick-up speed boons this character has claimed. Counts quest registry rows
        /// whose name starts with PickupBoonQuestPrefix and whose suffix is a valid boon key - matched
        /// case-insensitively, consistent with how QuestManager compares quest names.
        /// </summary>
        public int PickupBoonCount
        {
            get
            {
                if (pickupBoonCountCache == null)
                {
                    var count = 0;

                    foreach (var quest in Character.GetQuests(CharacterDatabaseLock))
                    {
                        var questName = quest.QuestName;

                        if (questName == null || questName.Length <= PickupBoonQuestPrefix.Length)
                            continue;

                        if (!questName.StartsWith(PickupBoonQuestPrefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var key = questName.Substring(PickupBoonQuestPrefix.Length);

                        if (IsValidPickupBoonKey(key))
                            count++;
                    }

                    pickupBoonCountCache = count;
                }

                return pickupBoonCountCache.Value;
            }
        }

        /// <summary>
        /// Invalidates the cached boon count so it is recomputed from the quest registry on next read.
        /// Called by the claim path (built separately) after a new PickupBoon_* quest row is stamped.
        /// </summary>
        public void InvalidatePickupBoonCache()
        {
            pickupBoonCountCache = null;
        }

        /// <summary>
        /// Composes the player's current pick-up animation playback speed: the live pickup_animation_speed
        /// tunable, boosted by pickup_speed_quest_bonus per claimed boon AND by custom_aug_pickup_speed_bonus
        /// per Custom Dreamweave pick-up augmentation held (PropertyInt.AugmentationPickupSpeed), capped at
        /// pickup_animation_speed_max. The two bonuses are additive inside one term and share one clamp -
        /// see PickupSpeed.Compute for the pure math and its clamp-not-reject semantics.
        /// </summary>
        private float GetPickupAnimationSpeed()
        {
            var baseSpeed = PropertyManager.GetDouble("pickup_animation_speed").Item;
            var perBoon = PropertyManager.GetDouble("pickup_speed_quest_bonus").Item;
            var max = PropertyManager.GetDouble("pickup_animation_speed_max").Item;

            return (float)PickupSpeed.Compute(baseSpeed, PickupBoonCount, perBoon, AugmentationPickupSpeed, CustomAugBroker.PickupSpeedBonus, max);
        }

        /// <summary>
        /// Attempts to claim the permanent pick-up speed boon named by <paramref name="key"/> (the suffix of
        /// quest PickupBoon_&lt;key&gt;). NO state change on any failure path - malformed key, a blocked mule,
        /// or an already-claimed boon all return FALSE with a player-facing <paramref name="error"/> and touch
        /// nothing. On success, the boon is stamped via QuestManager.Update (never Character.GetOrCreateQuest,
        /// which would bypass the mule guard - see Player_ClassAbilities.cs's LearnClassAbility for the trap),
        /// the boon-count cache is invalidated, and the character is saved.
        /// </summary>
        public bool TryClaimPickupBoon(string key, string itemName, out string error)
        {
            error = null;

            if (!IsValidPickupBoonKey(key))
            {
                error = $"The {itemName} is misconfigured and cannot be used.";
                return false;
            }

            // Explicit mule check, mirroring Player_ClassAbilities.LearnClassAbility: QuestManager.Update
            // does have its own internal MuleBlocked(AdvanceQuest) guard, but that guard is a silent no-op
            // (it never surfaces a refusal), so the claim path checks it itself first to refuse cleanly.
            if (MuleBlocked(MuleAction.AdvanceQuest, notify: false))
            {
                error = "A mule cannot make quest progress.";
                return false;
            }

            var questName = PickupBoonQuestPrefix + key;

            if (QuestManager.HasQuest(questName))
            {
                var perBoon = PropertyManager.GetDouble("pickup_speed_quest_bonus").Item;
                var baseSpeed = PropertyManager.GetDouble("pickup_animation_speed").Item;
                var max = PropertyManager.GetDouble("pickup_animation_speed_max").Item;

                var perAug = CustomAugBroker.PickupSpeedBonus;

                var percent = PickupSpeed.PercentBonus(PickupBoonCount, perBoon, AugmentationPickupSpeed, perAug);
                var speed = PickupSpeed.Compute(baseSpeed, PickupBoonCount, perBoon, AugmentationPickupSpeed, perAug, max);

                error = $"You have already claimed this bonus. Your pick-up speed bonus is {percent} percent (pick-up speed {speed:0.00}x).";
                return false;
            }

            QuestManager.Update(questName);

            InvalidatePickupBoonCache();

            SaveCharacterToDatabase();

            return true;
        }

        /// <summary>
        /// The success message for a just-claimed boon, quoting the composed totals AFTER the claim (so the
        /// caller must call this only once TryClaimPickupBoon has already succeeded and invalidated the
        /// cache). Shares its percent/speed math with FormatPickupSpeedStatus via PickupSpeed's helpers, so
        /// the claim message and /pickupspeed can never disagree.
        /// </summary>
        public string FormatPickupBoonClaimedMessage(string itemName)
        {
            var perBoon = PropertyManager.GetDouble("pickup_speed_quest_bonus").Item;
            var baseSpeed = PropertyManager.GetDouble("pickup_animation_speed").Item;
            var max = PropertyManager.GetDouble("pickup_animation_speed_max").Item;

            var perAug = CustomAugBroker.PickupSpeedBonus;

            var perBoonPercent = PickupSpeed.PercentBonus(1, perBoon);
            var totalPercent = PickupSpeed.PercentBonus(PickupBoonCount, perBoon, AugmentationPickupSpeed, perAug);
            var speed = PickupSpeed.Compute(baseSpeed, PickupBoonCount, perBoon, AugmentationPickupSpeed, perAug, max);

            return $"You use the {itemName}. Your pick-up speed is permanently increased by {perBoonPercent} percent. " +
                   $"Total bonus: {totalPercent} percent (pick-up speed {speed:0.00}x). This bonus is permanent and cannot be lost.";
        }

        /// <summary>
        /// /pickupspeed: sends the player's current pick-up speed bonus status, built by the pure
        /// PickupSpeed.FormatPickupSpeedStatus formatter from the live tunables. The figure reported is the
        /// COMBINED one - quest boons plus Custom Dreamweave pick-up augmentations - because PickupSpeed's
        /// contract is that this wording always agrees with what Compute actually applies.
        /// </summary>
        public void ShowPickupSpeedStatus()
        {
            var baseSpeed = PropertyManager.GetDouble("pickup_animation_speed").Item;
            var perBoon = PropertyManager.GetDouble("pickup_speed_quest_bonus").Item;
            var max = PropertyManager.GetDouble("pickup_animation_speed_max").Item;
            var perAug = CustomAugBroker.PickupSpeedBonus;

            foreach (var line in PickupSpeed.FormatPickupSpeedStatus(PickupBoonCount, baseSpeed, perBoon, AugmentationPickupSpeed, perAug, max))
                Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }
    }
}
