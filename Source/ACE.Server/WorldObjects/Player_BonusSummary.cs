using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Sends a full breakdown of the character's experience bonus sources and how they combine, mirroring
        /// the exact math the XP grant path uses so a player (or admin) can verify a kill's XP is boosted
        /// correctly. The four sources and their combine order come straight from EarnXP -> GetXPAndLuminanceModifier
        /// and GrantXP:
        ///
        ///   effective = (1 + gear + aug) x xp_modifier x (1 + offline) x (1 + alt)
        ///
        /// where gear (item XP enchantments) and aug (augmentation points x 5%) are ADDITIVE within the first
        /// factor, while the offline bonus and the alt character bonus are each SEPARATE MULTIPLICATIVE factors.
        /// This is the combat/kill case, the only one where all four apply: quest XP excludes the offline bonus
        /// and augmentation, and allegiance passup / item XP get none of these.
        /// </summary>
        public void ShowExperienceBonusSummary()
        {
            // Current live values, read exactly as the XP path reads them.
            var gear = EnchantmentManager.GetXPBonus();                 // additive fraction, e.g. 0.06 = +6%
            var augPoints = AugmentationBonusXp;
            var aug = augPoints * 0.05;                                 // 5% per augmentation point (Kill XP only)
            var xpModifier = PropertyManager.GetDouble("xp_modifier").Item;

            var offlineActive = IsOfflineExperienceBonusActive;
            var offline = offlineActive ? PropertyManager.GetDouble("offline_bonus_multiplier").Item : 0.0;

            var altActive = IsAltCharacterBonusActive;
            var alt = altActive ? PropertyManager.GetDouble("alt_character_bonus_multiplier").Item : 0.0;

            var gearAugFactor = 1.0 + gear + aug;
            var total = gearAugFactor * xpModifier * (1.0 + offline) * (1.0 + alt);

            void Send(string msg) => Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

            Send("--- Experience Bonus Summary (combat / kill XP) ---");
            Send($"  Gear (item enchantments): +{gear * 100:0.#}%");
            Send($"  Augmentation ({augPoints} pt x 5%): +{aug * 100:0.#}%");

            if (altActive)
                Send($"  Alternate character: +{alt * 100:0.#}% (until Enl {AltCharacterBonusTargetEnlightenment}, Level {AltCharacterBonusTargetLevel})");
            else
                Send("  Alternate character: +0% (not active - this is your furthest-along character)");

            if (offlineActive)
                Send($"  Offline bonus: +{offline * 100:0.#}% ({OfflineExperienceBonusDisplay} remaining)");
            else
                Send("  Offline bonus: +0% (none banked)");

            if (Math.Abs(xpModifier - 1.0) > 0.0001)
                Send($"  Server XP modifier: x{xpModifier:0.###}");

            Send($"Gear + Aug are additive: 1 + {gear * 100:0.#}% + {aug * 100:0.#}% = {gearAugFactor:0.###}x");
            Send($"Offline and Alt each multiply: x(1 + {offline * 100:0.#}%) x(1 + {alt * 100:0.#}%)");
            // Luminance on a combat kill receives ONLY the offline bonus (see Player_Luminance.GrantLuminance,
            // which applies ApplyOfflineExperienceBonus and nothing else): gear and augmentation XP bonuses are
            // Kill-XP-only, the alternate-character bonus is XP-only, and xp_modifier does not touch Luminance.
            var lumTotal = 1.0 + offline;

            Send($"Total on a kill:  XP {total:0.###}x (+{(total - 1.0) * 100:0.#}%)   |   Luminance {lumTotal:0.###}x (+{(lumTotal - 1.0) * 100:0.#}%)");
            Send("Note: quest XP excludes the offline bonus and augmentation; allegiance and item XP get none of these. Luminance receives only the offline bonus.");
        }
    }
}
