using System;
using System.Globalization;
using System.Linq;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// EVERY player-facing line the Thread-Guide feature sends, in one place: the NPC's tells and popups, the
    /// Press refusals and explanations, the gem-use solo line, the clear and fail lines, and the level-50 notice.
    /// Pure, no engine references. The wording is owner-approved text; change it here and nowhere else.
    ///
    /// {N} in the spec is the rung level, rendered by the Compose* helpers. {CHAMP} is the Taper colour whose
    /// modifier is "champions" in Content/dungeons/dynamic/attunement.json ("orange" -> the Orange Taper,
    /// wcid 1648), hard-coded as <see cref="ChampionsTaperColour"/> and pinned against the file by
    /// ThreadGuideTextTests so a re-colouring of the attunement table cannot leave the lesson pointing at the
    /// wrong Taper.
    /// </summary>
    public static class ThreadGuideText
    {
        /// <summary>The Taper colour whose modifier is champions (attunement.json "colors").</summary>
        public const string ChampionsTaperColour = "Orange";

        // ---------------- component introductions ----------------
        // A type whose vendor item names carry the type word (Lead Scarab, Orange Taper, Hazel Talisman) is named
        // plainly. A type whose items do NOT (herbs are Hyssop, Mandrake...; potions are the minerals Brimstone,
        // Cadmia...; powders are "Powdered Amber"..., where "Powder" is not a word of its own) gets two real item
        // names the first time it appears in a text. Every example name is verbatim from the Reagent-Steward's
        // create list (Content/sql/weenies/1003602 Reagent-Steward Aya Torimaru.sql) and attunement.json, and the
        // two powders are NAMED ones (add_or_raise), not the four random ones. Pinned by ThreadGuideWiringTests.

        public const string ScarabIntro = "a Scarab";

        /// <summary>Rung 50's buy step. The Lead Scarab lowers the level the most of any Scarab (set_difficulty -10 in attunement.json).</summary>
        public const string LeadScarabStep = "Buy a Lead Scarab from the Reagent-Steward - it is the gentlest; most other Scarabs raise the thread's level.";

        /// <summary>
        /// Rung 150's concrete pair: the Red Taper adds savage and the Hazel Talisman sharpens savage
        /// (attunement.json), and the Reagent-Steward sells both (wcids 1650 and 746). Pinned by ThreadGuideWiringTests.
        /// </summary>
        public const string Pair150 = "a Red Taper and a Hazel Talisman";
        public const string TaperIntro = "a Taper";
        public const string HerbIntro = "an Herb such as Hyssop or Mandrake";
        public const string PowderIntro = "a Powder such as Powdered Amber or Powdered Azurite";
        public const string TalismanIntro = "a Talisman";
        public const string PotionIntro = "a Potion such as Brimstone or Cadmia";

        /// <summary>The first-mention phrase for a RawFragmentRules type constant, article included.</summary>
        public static string ComposeIntro(string type)
        {
            switch (type)
            {
                case RawFragmentRules.ScarabType: return ScarabIntro;
                case RawFragmentRules.TaperType: return TaperIntro;
                case RawFragmentRules.HerbType: return HerbIntro;
                case RawFragmentRules.PowderType: return PowderIntro;
                case RawFragmentRules.TalismanType: return TalismanIntro;
                case RawFragmentRules.PotionType: return PotionIntro;
                default: return "a " + type;
            }
        }

        // ---------------- grant tells (T-GRANT) ----------------

        public const string Grant50 = "Here is your first Guide Fragment, level 50. " + LeadScarabStep + " Use the fragment on the Scarab, then use it on the Fragment Press to make a working Thread Gem. The Press will not accept it until a Scarab is loaded, and pressing Guide Fragments is free.";
        public const string Grant75 = "Your next Guide Fragment, level 75. This time use the fragment on a Taper. Any colour works except " + ChampionsTaperColour + ", which has nothing to act on at this level. Then press it.";
        public const string Grant100 = "Level 100. Use the fragment on " + HerbIntro + ", then press it.";
        public const string Grant125 = "Level 125. Use the fragment on " + PowderIntro + ". Choose a named one; Agate, Bloodstone, Carnelian and Moonstone pick at random. Then press it.";
        public const string Grant150 = "Level 150. Use the fragment on a Red Taper, then on a Hazel Talisman, then press it. A Talisman strengthens a modifier that is already there.";
        public const string Grant175 = "Level 175. Use the fragment on " + PotionIntro + ", then press it.";
        public const string GrantPlainFormat = "Your level {0} Guide Fragment. Load whatever components you like and press it. You know the craft now.";

        /// <summary>The grant tell for <paramref name="rung"/>: a special rung's own line, else the plain line.</summary>
        public static string ComposeGrant(int rung)
        {
            switch (rung)
            {
                case 50: return Grant50;
                case 75: return Grant75;
                case 100: return Grant100;
                case 125: return Grant125;
                case 150: return Grant150;
                case 175: return Grant175;
                default: return string.Format(CultureInfo.InvariantCulture, GrantPlainFormat, rung);
            }
        }

        // ---------------- grant popups (T-POPUP) ----------------

        /// <summary>
        /// The steps popup for <paramref name="rung"/>, leading with the action: the title, a blank line, then
        /// three numbered steps, then the one closing line. The verb is "use this fragment on the component"
        /// because that is the direction the server loads in: Gem.HandleActionUseOnTarget hands the FRAGMENT as
        /// the source and the component as the target to RawFragment.UseObjectOnTarget, which runs
        /// RawFragmentRules.CanLoad on the target. Every component type is sold by the Reagent-Steward
        /// (wcid 1003602's create list carries Scarabs, Herbs, Powdered gems, the Potion minerals, Talismans and
        /// all twelve Tapers).
        /// </summary>
        public static string ComposePopup(int rung)
        {
            var title = "Guide Fragment (Level " + rung.ToString(CultureInfo.InvariantCulture) + ")\n\n";
            const string press = "3. Use the fragment on the Fragment Press. Pressing Guide Fragments is free.\n\n";
            const string closing = "Then use the finished gem to enter your thread alone, and clear it for your reward and your next fragment.";

            switch (rung)
            {
                case 75:
                    return title
                        + "1. Buy a Taper from the Reagent-Steward.\n"
                        + "Any colour except " + ChampionsTaperColour + ".\n"
                        + "2. Use this fragment on the Taper.\n"
                        + press + closing;

                case 50:
                    return title
                        + "1. " + LeadScarabStep + "\n"
                        + "2. Use this fragment on the Scarab.\n"
                        + press + closing;

                case 125:
                    return title
                        + "1. Buy " + PowderIntro + " from the Reagent-Steward.\n"
                        + "Choose a named Powder; Agate, Bloodstone, Carnelian and Moonstone pick at random.\n"
                        + "2. Use this fragment on the Powder.\n"
                        + press + closing;

                case 150:
                    return title
                        + "1. Buy " + Pair150 + " from the Reagent-Steward.\n"
                        + "2. Use this fragment on the Red Taper, then on the Hazel Talisman.\n"
                        + press + closing;

                default:
                    var type = ThreadGuideLadder.RequiredType(rung);

                    if (type == null)
                        return title
                            + "1. Buy any components you like from the Reagent-Steward.\n"
                            + "2. Use this fragment on each of them.\n"
                            + press + closing;

                    return title
                        + "1. Buy " + ComposeIntro(type) + " from the Reagent-Steward.\n"
                        + "2. Use this fragment on the " + TypeWord(type) + ".\n"
                        + press + closing;
            }
        }

        // ---------------- fragment description (REQUIRED line) ----------------

        public const string RequiredFormat = "REQUIRED: Use this fragment on {0}, then use it on the Fragment Press.";
        public const string Required150 = "REQUIRED: Use this fragment on " + Pair150 + ", then use it on the Fragment Press.";
        public const string Loaded150 = "Taper and Talisman loaded. Use this fragment on the Fragment Press.";
        public const string LoadedFormat = "{0} loaded. Use this fragment on the Fragment Press.";

        /// <summary>
        /// The first line of a SPECIAL guide fragment's description: REQUIRED while no dose of the rung's
        /// required type is loaded, the loaded line once one is. Null for a plain guide rung, a non-guide spec
        /// and a pressed spec. <paramref name="dosesOfType"/> is RawFragmentRules.DosesOfType over the live
        /// attunement table, the same count the Press gate reads, so the description and the gate agree.
        /// </summary>
        public static string ComposeRequirementLine(DungeonGemSpec spec, Func<string, int> dosesOfType)
        {
            if (!ThreadGuideRules.IsGuide(spec) || spec.Seed != 0)
                return null;

            var type = ThreadGuideLadder.RequiredType(spec.Guide.Rung);
            if (type == null)
                return null;

            // The loaded line only once EVERY required type is in (rung 150 needs its Taper and its Talisman).
            var required = ThreadGuideLadder.RequiredTypes(spec.Guide.Rung);
            if (dosesOfType != null && required.All(t => dosesOfType(t) > 0))
                return required.Count > 1 ? Loaded150 : string.Format(CultureInfo.InvariantCulture, LoadedFormat, TypeWord(type));

            return spec.Guide.Rung == 150
                ? Required150
                : string.Format(CultureInfo.InvariantCulture, RequiredFormat, ComposeIntro(type));
        }

        /// <summary>
        /// The fragment's whole description: the requirement line (if any) on its own line, then the normal
        /// fragment text unchanged.
        /// </summary>
        public static string ComposeFragmentLongDesc(string baseDesc, DungeonGemSpec spec, Func<string, int> dosesOfType)
        {
            var line = ComposeRequirementLine(spec, dosesOfType);
            return line == null ? baseDesc : line + "\n" + baseDesc;
        }
        // ---------------- press explanations (T-EXPLAIN) ----------------

        public const string ExplainScarab = "Scarabs set a thread's monster level and how many times you may enter. Each kind of Scarab shifts the level by a different amount.";
        public const string ExplainTaper = "Tapers add a modifier to a thread's monsters. Each colour adds a different one, such as harder hits, tougher hides or faster feet.";
        public const string ExplainHerb = "Herbs add a reward bonus to a thread: experience, luminance or loot.";
        public const string ExplainPowder = "Powders steer which salvage materials a thread's monsters yield.";
        public const string ExplainTalisman = "Talismans strengthen a modifier on the gem as it is pressed. Pair one with its matching Taper; with nothing to strengthen it picks a modifier at random.";
        public const string ExplainPotion = "Potions reroll the weakest modifiers as the gem is pressed. The new roll is usually higher, so the gem gets tougher.";

        /// <summary>The explanation for a RawFragmentRules type constant, or null for a type with none.</summary>
        public static string ComposeExplain(string type)
        {
            switch (type)
            {
                case RawFragmentRules.ScarabType: return ExplainScarab;
                case RawFragmentRules.TaperType: return ExplainTaper;
                case RawFragmentRules.HerbType: return ExplainHerb;
                case RawFragmentRules.PowderType: return ExplainPowder;
                case RawFragmentRules.TalismanType: return ExplainTalisman;
                case RawFragmentRules.PotionType: return ExplainPotion;
                default: return null;
            }
        }

        // ---------------- press refusals ----------------

        public const string NeedsFormat = "This Guide Fragment needs a {0} before the Press will take it.";

        /// <summary>T-NEEDS-&lt;type&gt;: the type's display word, capitalised ("Scarab", "Taper", ...).</summary>
        public static string ComposeNeeds(string type)
            => string.Format(CultureInfo.InvariantCulture, NeedsFormat, TypeWord(type));

        public const string Stale = "This Guide Fragment has gone dark. Speak with the Thread-Guide for a fresh one.";

        // ---------------- gem use ----------------

        public const string Solo = "Guide threads are walked alone. Your fellowship will not follow you in.";

        // ---------------- clear and fail ----------------

        public const string ClearFormat = "You have cleared your level {0} guide thread. Return to the Thread-Guide for your next fragment.";
        public const string FailRegrantFormat = "Your guide thread closed before it was cleared. A fresh level {0} Guide Fragment is in your pack. Load and press it as before.";
        public const string EmptyThread = "That thread held nothing to face. Speak with the Thread-Guide for another fragment.";
        public const string TakesBackFaded = "The Thread-Guide takes back a faded Guide Fragment.";

        /// <summary>
        /// The once-per-login reminder (ThreadGuideFlow.ShouldSendLoginReminder). Neutral on purpose: the same state
        /// follows a failed run AND a cleared rung whose next fragment was not yet collected.
        /// </summary>
        public const string LoginReminder = "Speak with the Thread-Guide in the Drift Network for your next Guide Fragment.";

        /// <summary>The fail line when the regrant is blocked because the guide is unavailable: no NPC pointer.</summary>
        public const string FailDisabled = "Your guide thread closed before it was cleared.";

        public const string FailVisit = "Your guide thread closed before it was cleared. Speak with the Thread-Guide in the Drift Network for a fresh fragment.";

        public static string ComposeClear(int rung) => string.Format(CultureInfo.InvariantCulture, ClearFormat, rung);

        public static string ComposeFailRegrant(int rung) => string.Format(CultureInfo.InvariantCulture, FailRegrantFormat, rung);

        // ---------------- NPC refusals ----------------

        public const string RefuseLevel = "Come back when you have reached level 50.";
        public const string RefuseOutstanding = "You already carry a Guide Fragment or have a guide thread open. Finish that one first.";
        public const string RefuseComplete = "You have walked every thread I can show you. The Press and Teodor's surveys are yours to explore from here.";
        public const string RefuseDisabled = "I have no fragments to give right now.";

        /// <summary>
        /// NOT in the approved text list: the spec required a pack pre-flight at the NPC but gave no line for a
        /// full pack. Worded like the Survey-Archivist's NoRoomMessage; flagged in the PR for owner review.
        /// </summary>
        public const string RefusePackFull = "Your pack is full. Make room and speak with me again.";

        /// <summary>The refusal tell for a DecideGrant refusal reason.</summary>
        public static string ComposeRefusal(GuideRefusal reason)
        {
            switch (reason)
            {
                case GuideRefusal.Disabled: return RefuseDisabled;
                case GuideRefusal.BelowMinimumLevel: return RefuseLevel;
                case GuideRefusal.LadderComplete: return RefuseComplete;
                case GuideRefusal.HoldsGuideItem:
                case GuideRefusal.GuideRunInProgress: return RefuseOutstanding;
                default: return null;
            }
        }

        // ---------------- notice ----------------

        public const string Notice = "You are ready to run your first Thread. Find the Thread-Guide in the Drift Network, beside Survey-Archivist Teodor Brannock.";

        // ---------------- item naming ----------------

        public static string ComposeFragmentName(int rung) => "Guide Fragment (Level " + rung.ToString(CultureInfo.InvariantCulture) + ")";

        public static string ComposeFragmentPluralName(int rung) => "Guide Fragments (Level " + rung.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>The capitalised display word for a RawFragmentRules type constant.</summary>
        public static string TypeWord(string type)
        {
            switch (type)
            {
                case RawFragmentRules.ScarabType: return "Scarab";
                case RawFragmentRules.TaperType: return "Taper";
                case RawFragmentRules.HerbType: return "Herb";
                case RawFragmentRules.PowderType: return "Powder";
                case RawFragmentRules.TalismanType: return "Talisman";
                case RawFragmentRules.PotionType: return "Potion";
                default: return type;
            }
        }
    }
}
