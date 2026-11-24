using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>What an imbue-style gem application does once the re-verify and the roll are known.</summary>
    public enum ImbueGemOutcome
    {
        /// <summary>The re-verify failed: nothing is consumed and nothing changes.</summary>
        Abort,

        /// <summary>The gem is consumed and the target is stamped.</summary>
        Apply,

        /// <summary>The gem is consumed and the target is destroyed.</summary>
        Destroy,
    }

    /// <summary>
    /// The shared risk rules of the Jewelers Aspect Gem, the Rear Guard Mantle and the Cloak Weave Amplifier: each is applied
    /// like a retail imbue (a base chance, plus the imbue augmentation, and a failure destroys the TARGET). The chance is flat:
    /// the matching tinkering skill only has to be trained. None of this is a tinker, so the tinker counter, the tinker log and
    /// the imbue counters are never touched. The math is pure; only <see cref="Roll"/> and <see cref="DestroyTarget"/> reach
    /// the world.
    /// </summary>
    public static class ImbueGemRisk
    {
        public const string ChanceKey = "imbue_gem_success_chance";

        /// <summary>Per rank of the imbue augmentation, same figure as RecipeManager's retail imbue.</summary>
        public const double AugmentationPerRank = 0.05;

        public const Skill AspectSkill = Skill.MagicItemTinkering;
        public const Skill CloakGemSkill = Skill.ItemTinkering;

        /// <summary>The success chance: base plus 0.05 per augmentation rank, clamped to 0-1. A NaN base is 0.</summary>
        public static double Chance(double baseChance, int augRanks)
        {
            if (double.IsNaN(baseChance))
                return 0.0;

            return Math.Clamp(baseChance + Math.Max(0, augRanks) * AugmentationPerRank, 0.0, 1.0);
        }

        /// <summary>
        /// True when the draw succeeds. The draw is in [0,1) but ThreadSafeRandom's max is inclusive, so a chance of 1 or more
        /// always succeeds explicitly; a chance of 0 or less, or NaN, never does.
        /// </summary>
        public static bool Succeeds(double chance, double draw)
        {
            if (double.IsNaN(chance) || chance <= 0.0)
                return false;

            if (chance >= 1.0)
                return true;

            return draw < chance;
        }

        /// <summary>The whole-percent form of a chance for display, rounded to nearest (0.33 -> 33, 0.38 -> 38).</summary>
        public static int DisplayPercent(double chance)
        {
            return (int)Math.Round(chance * 100.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>Null when the skill is trained or better, otherwise the refusal naming the gem and the skill.</summary>
        public static string SkillRefusal(Skill skill, SkillAdvancementClass advancementClass, string gemName)
        {
            if (advancementClass >= SkillAdvancementClass.Trained)
                return null;

            return $"You must be trained in {skill.ToSentence()} to use the {gemName}.";
        }

        /// <summary>Apply, destroy, or abort. A failed re-verify aborts before the roll matters.</summary>
        public static ImbueGemOutcome Resolve(bool verifyOk, bool succeeded)
        {
            if (!verifyOk)
                return ImbueGemOutcome.Abort;

            return succeeded ? ImbueGemOutcome.Apply : ImbueGemOutcome.Destroy;
        }

        public static string ChanceSuffix(int percent, string targetName)
        {
            return $" You have a {percent}% chance to succeed. If it fails, the {targetName} is destroyed.";
        }

        public static string FailureText(string gemName, string targetName)
        {
            return $"The {gemName} fails to take and the {targetName} is destroyed.";
        }

        /// <summary>The live chance for this player: the tunable plus the imbue augmentation.</summary>
        public static double ChanceFor(Player player)
        {
            return Chance(PropertyManager.GetDouble(ChanceKey).Item, player.AugmentationBonusImbueChance);
        }

        /// <summary>Rolls the live chance. INCLUSIVE max in ThreadSafeRandom is handled by Succeeds.</summary>
        public static bool Roll(double chance)
        {
            return Succeeds(chance, ThreadSafeRandom.Next(0f, 1f));
        }

        /// <summary>Destroys the failed target through the same networked removal a destroyed tinker target uses (pack or worn), with the failure line on the Craft channel.</summary>
        public static void DestroyTarget(Player player, WorldObject target, string message)
        {
            RecipeManager.DestroyItem(player, null, target, 1, message);
        }
    }
}