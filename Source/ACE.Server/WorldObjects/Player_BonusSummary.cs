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
        /// Kill Luminance uses (1 + gear + aug) x luminance_modifier x (1 + offline) (then the Thread blessing, if any); see ComputeKillLuminanceFactor.
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
            // Thread Shrine blessings apply after the offline/alt bonuses (GrantXP / GrantLuminance), only while the
            // player stands inside the run that granted them. ThreadBoons.ScaleAmount ignores a magnitude <= 1.0.
            var xpBoon = Math.Max(1.0, ACE.Server.ThreadDungeons.ThreadBoons.For(this, ACE.Server.ThreadDungeons.ThreadBoonKind.Xp));
            var lumBoon = Math.Max(1.0, ACE.Server.ThreadDungeons.ThreadBoons.For(this, ACE.Server.ThreadDungeons.ThreadBoonKind.Luminance));

            var total = gearAugFactor * xpModifier * (1.0 + offline) * (1.0 + alt) * xpBoon;

            void Send(string msg) => Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

            Send("--- Experience Bonus Summary (combat / kill XP) ---");
            Send($"  Gear (item enchantments): +{gear * 100:0.#}%");
            Send($"  Augmentation ({augPoints} pt x 5%): +{aug * 100:0.#}%");

            if (altActive)
                Send($"  Alternate character: +{alt * 100:0.#}% (until Enl {AltCharacterBonusTargetEnlightenment}, Level {AltCharacterBonusTargetLevel})");
            else
                Send("  Alternate character: +0% (not active - not far enough behind your furthest-along character)");

            if (offlineActive)
                Send($"  Offline bonus: +{offline * 100:0.#}% ({OfflineExperienceBonusDisplay} remaining)");
            else
                Send("  Offline bonus: +0% (none banked)");

            if (Math.Abs(xpModifier - 1.0) > 0.0001)
                Send($"  Server XP modifier: x{xpModifier:0.###}");

            Send($"Gear + Aug are additive: 1 + {gear * 100:0.#}% + {aug * 100:0.#}% = {gearAugFactor:0.###}x");
            Send($"Offline and Alt each multiply: x(1 + {offline * 100:0.#}%) x(1 + {alt * 100:0.#}%)");
            // Luminance on a combat kill (EarnLuminance -> GrantLuminance) gets the SAME gear + augmentation factor as
            // Kill XP (GetXPAndLuminanceModifier), then luminance_modifier (NOT xp_modifier), then the offline bonus.
            // The alternate-character bonus is XP-only.
            var lumModifier = PropertyManager.GetDouble("luminance_modifier").Item;
            var lumTotal = ComputeKillLuminanceFactor(gear, aug, lumModifier, offline) * lumBoon;

            if (xpBoon > 1.0)
                Send($"  Thread blessing, XP (while inside the run): x{xpBoon:0.###}");
            if (lumBoon > 1.0)
                Send($"  Thread blessing, Luminance (while inside the run): x{lumBoon:0.###}");

            Send($"Total on a kill:  XP {total:0.###}x (+{(total - 1.0) * 100:0.#}%)   |   Luminance {lumTotal:0.###}x (+{(lumTotal - 1.0) * 100:0.#}%)");
            if (Math.Abs(lumModifier - 1.0) > 0.0001)
                Send($"  Server Luminance modifier: x{lumModifier:0.###}");
            Send("Note: quest XP excludes the offline bonus and augmentation; allegiance and item XP get none of these. Kill Luminance gets gear, augmentation and offline (not the alternate-character bonus). Totals are per kill (solo kill; fellowship splits apply first).");
        }

        /// <summary>
        /// Combined multiplier on a kill's Luminance, as EarnLuminance and GrantLuminance apply it:
        /// (1 + gear + aug) x luminance_modifier x (1 + offline).
        /// </summary>
        public static double ComputeKillLuminanceFactor(double gear, double aug, double luminanceModifier, double offline)
        {
            return (1.0 + gear + aug) * luminanceModifier * (1.0 + offline);
        }
    }
}
