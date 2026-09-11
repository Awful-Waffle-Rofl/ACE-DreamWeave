using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The gem's reward-summary lines (owner report, 2026-09-07: "It seemed like an experience bonus was in
    /// place but this was not apparent on the gem (it should be)"). ComposeLongDesc's RenderEffect switches
    /// only on MonsterEffectKind, so before this block a modifier that doubled a run's XP printed nothing at
    /// all about the reward it changed.
    ///
    /// Every test here goes through the PURE core overload with an explicit lookup delegate and explicit caps
    /// AND explicit scales. That is the point of the split: PropertyManager reads throw under this harness, so
    /// a test that reached the store-bound overload would fail for a reason that has nothing to do with the
    /// text it asserts.
    ///
    /// FOUR lines, not three, because XP is two axes. DungeonRewardMath splits the XP product on a modifier's
    /// Target - XpMultiplier excludes boss-target modifiers and applies to every kill, BossXpMultiplier takes
    /// only those and applies to the boss alone - while luminance and loot quantity are each a single
    /// undivided product. Several tests below assert the boss-target cases specifically, because their
    /// absence is exactly what let the block ship reporting only half the XP a gem grants.
    ///
    /// Second owner follow-up, same day (2026-09-06): "Wanted to confirm that the total xp/lum multiplier will
    /// be shown on the gem as well after these changes." Experience and Luminance now show the TOTAL - the
    /// gem's own capped product times the axis's global scale (dynamic_dungeons_xp_scale / _lum_scale) - with
    /// the product itself appended as a parenthetical; Boss Experience and Loot Quantity are unchanged and
    /// still show only the gem's own capped product with no scale folded in. Tests below default `xpScale`
    /// and `lumScale` to 1.0 in <see cref="Compose"/> so cases that predate the scale change keep asserting
    /// exactly what they did before it (a scale of 1.0 collapses the total to the bare product - see
    /// <see cref="A_scale_of_one_collapses_the_line_to_the_bare_modifier_product"/>); tests that exercise the
    /// scale itself pass a non-1.0 value explicitly.
    /// </summary>
    [TestClass]
    public class DungeonGemRewardLinesTests
    {
        /// <summary>
        /// ModifierDef's reward fields default to the IDENTITY (rewardXpBase 1.0, rewardLumBase 1.0,
        /// lootQuantityMult 1.0, DungeonDefs.cs), not to zero, so a def built without naming them contributes
        /// no factor at all. Every modifier in these tests therefore names exactly the one axis it means to
        /// move, and any line that appears is attributable to a field the test set on purpose.
        /// </summary>
        private static ModifierDef Modifier(string id, string display, double xpBase = 1.0, double lumBase = 1.0,
            double lootQuantityMult = 1.0, string xpKind = "constant", double xpSlope = 0.0, string target = "monster")
            => new ModifierDef
            {
                Id = id,
                Display = display,
                MonsterEffectKind = "none",
                Target = target,
                RewardXpKind = xpKind,
                RewardXpBase = xpBase,
                RewardXpSlope = xpSlope,
                RewardLumBase = lumBase,
                LootQuantityMult = lootQuantityMult,
            };

        /// <summary>
        /// A boss-target modifier shaped like the shipped boss_guarded: target "boss", linear XP off
        /// rewardXpSlope 0.15, no luminance or loot effect. 1.0 + 0.15 * 3.0 = 1.45 at its top magnitude,
        /// which is the figure Content/dungeons/dynamic/modifiers.json actually ships.
        /// </summary>
        private static ModifierDef BossGuarded()
            => Modifier("boss_guarded", "Guarded", xpKind: "linear", xpBase: 1.0, xpSlope: 0.15, target: "boss");

        /// <summary>
        /// The modifier table this test's Lookup resolves against. An INSTANCE field, not a static one: MSTest
        /// builds a fresh instance per test method, so a table set by one test can never leak into another
        /// regardless of ordering or of how test.runsettings is configured later.
        /// </summary>
        private ModifierDef[] table = new ModifierDef[0];

        private ModifierDef Lookup(string id)
        {
            foreach (var def in table)
                if (def.Id == id)
                    return def;

            return null;
        }

        private static string ComponentName(uint wcid) => $"wcid {wcid}";

        /// <summary>A finished gem carrying the given modifier ids and magnitudes.</summary>
        private static DungeonGemSpec Gem(params (string Id, double Magnitude)[] mods)
            => new DungeonGemSpec("filos_doom", 100, 6, "any", 4242, mods ?? new (string, double)[0], 0, 0, 1, null, null);

        /// <summary>
        /// xpScale/lumScale default to 1.0, NOT the shipped 2.0 (DungeonPopulationLimits.DefaultXpScale) -
        /// deliberately, so every pre-existing test in this file that does not care about the scale keeps
        /// asserting the bare modifier product exactly as it did before the scale was folded in. Tests that
        /// exercise the scale itself (see the bottom of this file) pass a non-1.0 value explicitly.
        /// </summary>
        private string Compose(DungeonGemSpec spec, double xpCap = 3.0, double lumCap = 3.0, double lootQuantityCap = 3.0,
            double xpScale = 1.0, double lumScale = 1.0)
            => ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 2, 3, Lookup, ComponentName, xpCap, lumCap, lootQuantityCap, null, xpScale, lumScale);

        /// <summary>
        /// The rule that keeps an ordinary gem from growing three lines that say nothing, at scale 1.0: a
        /// total of 1.0 is not printed. This is the case that covers the ten shipped rung fragments (P2-R20)
        /// UNDER A SCALE OF ONE; at the shipped 2.0 default the same specs DO print Experience/Luminance
        /// (see the scale section below and DungeonGemLongDescTests.Fragment_shows_Pressed_nothing_and_matches_the_shipped_rung_row,
        /// which composes through the real default and asserts the ten rungs' regenerated text).
        /// </summary>
        [TestMethod]
        public void No_reward_line_is_emitted_when_every_multiplier_is_one()
        {
            table = new ModifierDef[0];

            var desc = Compose(Gem());

            Assert.IsFalse(desc.Contains("Experience:"), desc);
            Assert.IsFalse(desc.Contains("Luminance:"), desc);
            Assert.IsFalse(desc.Contains("Loot Quantity:"), desc);
        }

        /// <summary>
        /// The same rule for a gem that HAS a modifier which simply does not touch the reward axes - a pure
        /// difficulty modifier. Distinct from the empty-spec case above: the product loop runs and multiplies
        /// by the identity, rather than never running at all, and both paths must stay silent (at scale 1.0).
        /// </summary>
        [TestMethod]
        public void A_modifier_that_moves_no_reward_axis_emits_no_line()
        {
            table = new[] { Modifier("hardy", "Hardy") };

            var desc = Compose(Gem(("hardy", 2.0)));

            StringAssert.Contains(desc, "\nHardy: ", "guard: the modifier itself is still listed");
            Assert.IsFalse(desc.Contains("Experience:"), desc);
            Assert.IsFalse(desc.Contains("Luminance:"), desc);
            Assert.IsFalse(desc.Contains("Loot Quantity:"), desc);
        }

        /// <summary>
        /// The owner's actual complaint: an XP bonus was applied and the gem said nothing. Two decimals and
        /// InvariantCulture, matching every other number this description carries - a comma decimal separator
        /// would be stamped into the item's persisted LongDesc on a machine with a European locale. At scale
        /// 1.0 the total equals the bare product, so no parenthetical is shown (see
        /// <see cref="A_scale_of_one_collapses_the_line_to_the_bare_modifier_product"/> for that rule asserted
        /// directly).
        /// </summary>
        [TestMethod]
        public void An_experience_modifier_is_summarised()
        {
            table = new[] { Modifier("rich", "Rich Veins", xpBase: 1.16) };

            var desc = Compose(Gem(("rich", 1.0)));

            StringAssert.Contains(desc, "\nExperience: x1.16\n");
            Assert.IsFalse(desc.Contains("Luminance:"), desc);
            Assert.IsFalse(desc.Contains("Loot Quantity:"), desc);
        }

        /// <summary>Luminance and loot quantity are their own axes and their own lines, in that fixed order.</summary>
        [TestMethod]
        public void Luminance_and_loot_quantity_are_summarised_on_their_own_lines()
        {
            table = new[]
            {
                Modifier("lucent", "Lucent", lumBase: 1.25),
                Modifier("bountiful", "Bountiful", lootQuantityMult: 1.20),
            };

            var desc = Compose(Gem(("lucent", 1.0), ("bountiful", 1.0)));

            StringAssert.Contains(desc, "\nLuminance: x1.25\n");
            StringAssert.Contains(desc, "\nLoot Quantity: x1.20\n");
            Assert.IsFalse(desc.Contains("Experience:"), desc);

            Assert.IsTrue(desc.IndexOf("Luminance: x1.25") < desc.IndexOf("Loot Quantity: x1.20"),
                "the three axes print in a fixed order so the panel does not reshuffle between gems");
        }

        /// <summary>
        /// The reason these are SUMMARY lines rather than per-modifier suffixes. Two modifiers worth x2.00
        /// each compose to x4.00, which the cap clips to x3.00 - the number the run will actually pay. A
        /// per-modifier rendering would have printed "x2.00" twice and left the player to multiply them into a
        /// figure that is simply wrong, and wrong by more the harder the cap binds.
        /// </summary>
        [TestMethod]
        public void The_experience_line_shows_the_capped_product_not_the_raw_one()
        {
            table = new[]
            {
                Modifier("rich", "Rich Veins", xpBase: 2.0),
                Modifier("richer", "Richer Veins", xpBase: 2.0),
            };

            var desc = Compose(Gem(("rich", 1.0), ("richer", 1.0)), xpCap: 3.0);

            StringAssert.Contains(desc, "\nExperience: x3.00\n");
            Assert.IsFalse(desc.Contains("x4.00"), desc);
        }

        /// <summary>
        /// A "linear" reward kind reads base + slope * magnitude, so the gem's ROLLED magnitude is what the
        /// line reflects. 1.0 + 0.02 * 20 = 1.40; a gem of the same modifier rolled lower must print a lower
        /// number, which is the second half here and is the whole reason the figure is composed per gem rather
        /// than read off the modifier definition.
        /// </summary>
        [TestMethod]
        public void A_linear_experience_modifier_reflects_the_rolled_magnitude()
        {
            table = new[] { Modifier("veined", "Veined", xpKind: "linear", xpBase: 1.0, xpSlope: 0.02) };

            StringAssert.Contains(Compose(Gem(("veined", 20.0))), "\nExperience: x1.40\n");
            StringAssert.Contains(Compose(Gem(("veined", 5.0))), "\nExperience: x1.10\n");
        }

        /// <summary>
        /// Placement, asserted as a strict ordering rather than a substring: the block sits AFTER the modifier
        /// list and BEFORE the Entries line. That is what keeps it reading as a consequence of the modifiers
        /// above it instead of as another property of the item, and it is the position ruling P2-R20's shipped
        /// rung text was checked against.
        ///
        /// The trailing anchor was "\nStability: " until the instability mechanic was removed (owner ruling,
        /// 2026-09-07). It is now the Entries line, which is the line the Stability line used to sit
        /// immediately above - so the ordering this pins is unchanged, only its lower bound is renamed. The
        /// test also now asserts the Stability line is ABSENT, so a reinstated line would fail here rather
        /// than silently satisfy a looser bound.
        /// </summary>
        [TestMethod]
        public void The_reward_block_sits_between_the_modifiers_and_the_entries_line()
        {
            table = new[] { Modifier("rich", "Rich Veins", xpBase: 1.16, lumBase: 1.25, lootQuantityMult: 1.20) };

            var desc = Compose(Gem(("rich", 1.0)));

            var modifiers = desc.IndexOf("\nRich Veins: ");
            var xp = desc.IndexOf("\nExperience: x1.16\n");
            var lum = desc.IndexOf("\nLuminance: x1.25\n");
            var loot = desc.IndexOf("\nLoot Quantity: x1.20\n");
            var entries = desc.IndexOf("\nEntries: ");

            Assert.IsFalse(desc.Contains("Stability:"), desc);
            Assert.IsTrue(modifiers >= 0 && xp >= 0 && lum >= 0 && loot >= 0 && entries >= 0, desc);
            Assert.IsTrue(modifiers < xp, desc);
            Assert.IsTrue(xp < lum, desc);
            Assert.IsTrue(lum < loot, desc);
            Assert.IsTrue(loot < entries, desc);
        }

        /// <summary>
        /// THE regression test for this block's own original bug, and it FAILS without the Boss Experience
        /// line. DungeonRewardMath splits the XP product on Target: XpMultiplier excludes boss-target
        /// modifiers (DungeonRewardMath.cs:98) and BossXpMultiplier takes only those (:102). A gem carrying
        /// nothing but boss_guarded therefore has XpMultiplier exactly 1.0, the differs-from-1.00 rule
        /// suppresses the Experience line, and before the fix the gem advertised no experience bonus at all
        /// while the run paid 45% extra on the boss - the owner's original complaint, reproduced by the very
        /// change meant to answer it.
        ///
        /// Asserting the ABSENCE of the plain Experience line matters as much as the presence of the boss
        /// one: printing both would tell a player their trash kills were worth 45% more, which they are not.
        /// </summary>
        [TestMethod]
        public void A_boss_only_experience_modifier_shows_a_boss_line_and_no_plain_line()
        {
            table = new[] { BossGuarded() };

            var desc = Compose(Gem(("boss_guarded", 3.0)));

            StringAssert.Contains(desc, "\nBoss Experience: x1.45\n");
            Assert.IsFalse(desc.Contains("\nExperience:"), "trash kills get no XP bonus from a boss-target modifier: " + desc);
            Assert.IsFalse(desc.Contains("Luminance:"), desc);
            Assert.IsFalse(desc.Contains("Loot Quantity:"), desc);
        }

        /// <summary>
        /// Both axes at once, with DIFFERENT values, so the test would catch either line being computed from
        /// the other's product. The boss kill pays 1.20 * 1.45 and every trash kill pays 1.20; that product
        /// is deliberately not printed anywhere, because no single kill pays only it and a merged figure
        /// would overstate every trash kill in the run.
        ///
        /// Order is asserted too - Boss Experience directly after Experience - so the two read as a pair
        /// rather than being separated by the luminance line.
        /// </summary>
        [TestMethod]
        public void A_gem_with_both_axes_shows_both_lines_with_separate_values()
        {
            table = new[] { Modifier("rich", "Rich Veins", xpBase: 1.20), BossGuarded() };

            var desc = Compose(Gem(("rich", 1.0), ("boss_guarded", 3.0)));

            StringAssert.Contains(desc, "\nExperience: x1.20\n");
            StringAssert.Contains(desc, "\nBoss Experience: x1.45\n");

            Assert.IsTrue(desc.IndexOf("Experience: x1.20") < desc.IndexOf("Boss Experience: x1.45"),
                "the boss line reads directly after the line it qualifies");

            // 1.20 * 1.45 = 1.74, the number no single kill pays. It must appear nowhere.
            Assert.IsFalse(desc.Contains("x1.74"), desc);
        }

        /// <summary>
        /// A boss-target modifier that moves no reward axis stays silent on both experience lines, so the
        /// boss line is governed by the same differs-from-1.00 rule as the other three rather than appearing
        /// whenever a boss-target modifier happens to be present. boss_enraged's monster-side effect is
        /// damage_rating, so this is the shape of a boss modifier that is purely a difficulty knob.
        /// </summary>
        [TestMethod]
        public void A_boss_modifier_with_no_reward_effect_prints_neither_experience_line()
        {
            table = new[] { Modifier("boss_stoic", "Stoic", target: "boss") };

            var desc = Compose(Gem(("boss_stoic", 2.0)));

            StringAssert.Contains(desc, "\nStoic: ", "guard: the modifier itself is still listed");
            Assert.IsFalse(desc.Contains("Experience:"), desc);
        }

        /// <summary>
        /// The shipped boss_enraged shape, which is the second of the two common boss-target XP modifiers and
        /// carries a loot-quantity effect as well. It is worth its own case because it proves the two axes are
        /// independent: LootQuantityMultiplier has NO target filter (DungeonRewardMath.cs:273-284), so a
        /// boss-target modifier's loot effect lands on the ordinary Loot Quantity line while its XP effect
        /// lands on the boss line. Getting this wrong in either direction would be invisible without it.
        /// </summary>
        [TestMethod]
        public void A_boss_modifier_contributes_loot_quantity_to_the_ordinary_line()
        {
            table = new[] { Modifier("boss_enraged", "Enraged", xpKind: "linear", xpBase: 1.0, xpSlope: 0.005, lootQuantityMult: 1.25, target: "boss") };

            var desc = Compose(Gem(("boss_enraged", 60.0)));

            StringAssert.Contains(desc, "\nBoss Experience: x1.30\n");   // 1.0 + 0.005 * 60
            StringAssert.Contains(desc, "\nLoot Quantity: x1.25\n");     // no target filter on this axis
            Assert.IsFalse(desc.Contains("\nExperience:"), desc);
        }

        /// <summary>
        /// Luminance needs no boss-specific line, and this asserts the mechanism rather than trusting the
        /// doc comment. LumMultiplier passes null as its target filter (DungeonRewardMath.cs:106) and Product
        /// skips filtering when it is null (:88), so a BOSS-target luminance modifier is already inside the
        /// single Luminance product. If that ever changed, luminance would acquire the same hole XP had and
        /// this test is what would catch it.
        /// </summary>
        [TestMethod]
        public void A_boss_target_luminance_modifier_lands_on_the_single_luminance_line()
        {
            table = new[] { Modifier("boss_lucent", "Boss Lucent", lumBase: 1.25, target: "boss") };

            var desc = Compose(Gem(("boss_lucent", 1.0)));

            StringAssert.Contains(desc, "\nLuminance: x1.25\n");
            Assert.IsFalse(desc.Contains("Boss Luminance:"), "luminance is a single undivided axis: " + desc);
        }

        /// <summary>
        /// Pins the one case where a reward line appears on a gem carrying NO reward modifier, so that a
        /// future change to it has to be deliberate. DungeonRewardMath.Product ends in Math.Min(product, cap)
        /// with no lower floor (DungeonRewardMath.cs:92), so a cap tunable set below 1.0 drags even an empty
        /// modifier set under 1.0 - unlike LootQuantityMultiplier, which clamps to [1.0, max(1.0, cap)]
        /// (DungeonRewardMath.cs:281) and is asserted here as the control that proves the asymmetry is real
        /// rather than a misreading.
        ///
        /// The line is CORRECT in the sense that matters: ThreadDungeonSpawner feeds the same live cap into
        /// the population math, so a run opened under that setting genuinely pays the reduced rate, and the
        /// gem showing x0.50 is telling the truth about what the player will be paid. It is recorded as a test
        /// rather than fixed here because flooring Product would change what every run PAYS, which is an
        /// economy decision and not this change's to take.
        /// </summary>
        [TestMethod]
        public void A_cap_below_one_is_shown_because_the_run_really_pays_it()
        {
            table = new ModifierDef[0];

            var desc = Compose(Gem(), xpCap: 0.5, lumCap: 0.75, lootQuantityCap: 0.5);

            StringAssert.Contains(desc, "\nExperience: x0.50\n");
            StringAssert.Contains(desc, "\nLuminance: x0.75\n");

            // Both XP axes go through the same Product and the same cap, so the boss line is dragged under
            // 1.0 too. Asserted rather than left implicit: it is the only place a reward line appears on a
            // gem with no modifiers at all, and a future floor on Product must silence BOTH of them.
            StringAssert.Contains(desc, "\nBoss Experience: x0.50\n");

            // The control: loot quantity floors at 1.0, so it stays silent on the same settings.
            Assert.IsFalse(desc.Contains("Loot Quantity:"), desc);
        }

        // ---- the reward scale (owner follow-up, 2026-09-06) ----------------------------------------------

        /// <summary>
        /// A plain gem (no XP modifiers) at the shipped 2.0 default scale shows the bare scale alone, no
        /// parenthetical: there is nothing this gem itself contributed worth calling out, so
        /// "(x1.00 from this gem)" would be noise rather than information.
        /// </summary>
        [TestMethod]
        public void A_gem_with_no_xp_modifiers_shows_the_bare_scale()
        {
            table = new ModifierDef[0];

            var desc = Compose(Gem(), xpScale: 2.7, lumScale: 2.7);

            StringAssert.Contains(desc, "\nExperience: x2.70\n");
            StringAssert.Contains(desc, "\nLuminance: x2.70\n");
            Assert.IsFalse(desc.Contains("from this gem"), desc);
        }

        /// <summary>
        /// A gem WITH an XP modifier at a non-1.0 scale shows both numbers: the total (what a kill actually
        /// pays) and the gem's own product in parentheses (what this gem alone contributed), and the total is
        /// exactly product * scale - 1.16 * 2.7 = 3.132, which rounds to x3.13.
        /// </summary>
        [TestMethod]
        public void A_gem_with_xp_modifiers_shows_the_total_and_the_gems_own_product()
        {
            table = new[] { Modifier("rich", "Rich Veins", xpBase: 1.16, lumBase: 1.25) };

            var desc = Compose(Gem(("rich", 1.0)), xpScale: 2.7, lumScale: 2.7);

            StringAssert.Contains(desc, "\nExperience: x3.13 (x1.16 from this gem)\n");   // 1.16 * 2.7
            StringAssert.Contains(desc, "\nLuminance: x3.38 (x1.25 from this gem)\n");    // 1.25 * 2.7 = 3.375, rounds to 3.38
        }

        /// <summary>
        /// A scale of 1.0 must reproduce the pre-scale behaviour exactly: the total and the gem's own product
        /// are then the SAME number, so the parenthetical (which would just repeat the total) is omitted and
        /// the line collapses to the bare product - "Experience: x1.16", not "x1.16 (x1.16 from this gem)".
        /// And when the product is also 1.0, both are 1.0 and the line is suppressed entirely, same as before
        /// the scale existed.
        /// </summary>
        [TestMethod]
        public void A_scale_of_one_collapses_the_line_to_the_bare_modifier_product()
        {
            table = new[] { Modifier("rich", "Rich Veins", xpBase: 1.16) };

            var modified = Compose(Gem(("rich", 1.0)), xpScale: 1.0);
            StringAssert.Contains(modified, "\nExperience: x1.16\n");
            Assert.IsFalse(modified.Contains("from this gem"), modified);

            table = new ModifierDef[0];
            var plain = Compose(Gem(), xpScale: 1.0);
            Assert.IsFalse(plain.Contains("Experience:"), plain);
        }

        /// <summary>
        /// Boss Experience is unaffected by xpScale even when the plain Experience line on the same gem is
        /// scaled: BossXpMultiplier is a separate factor XpForKill multiplies on top of the already-scaled
        /// Experience total for the boss kill alone (DungeonRewardMath.cs:133), so folding xpScale into this
        /// line too would double it for every boss kill. lumScale is passed too, as a control proving it
        /// cannot leak into the XP side either.
        /// </summary>
        [TestMethod]
        public void Boss_experience_is_unaffected_by_the_scale()
        {
            table = new[] { Modifier("rich", "Rich Veins", xpBase: 1.20), BossGuarded() };

            var desc = Compose(Gem(("rich", 1.0), ("boss_guarded", 3.0)), xpScale: 2.7, lumScale: 2.7);

            StringAssert.Contains(desc, "\nExperience: x3.24 (x1.20 from this gem)\n");   // 1.20 * 2.7
            StringAssert.Contains(desc, "\nBoss Experience: x1.45\n");                    // unscaled, unchanged
            Assert.IsFalse(desc.Contains("x3.92"), desc); // 1.45 * 2.7, the wrong (double-scaled) figure
        }

        /// <summary>
        /// Loot Quantity has no global scale at all - there is no dynamic_dungeons_loot_quantity_scale
        /// tunable and DungeonPopulationBuilder carries no analogous field - so it keeps suppressing at 1.00
        /// regardless of what xpScale/lumScale are set to, unlike Experience/Luminance which now suppress on
        /// the TOTAL instead.
        /// </summary>
        [TestMethod]
        public void Loot_quantity_still_suppresses_at_one_regardless_of_the_scale()
        {
            table = new ModifierDef[0];

            var desc = Compose(Gem(), xpScale: 2.7, lumScale: 2.7);

            Assert.IsFalse(desc.Contains("Loot Quantity:"), desc);
        }

        /// <summary>
        /// Review finding, 2026-09-07: the store-bound ComposeLongDesc overload originally read
        /// dynamic_dungeons_xp_scale/_lum_scale RAW off PropertyManager, while ThreadDungeonSpawner reads the
        /// identical tunables through SanitizeDoubleDial (falls back to the compiled default on NaN/Infinity/
        /// negative, clamps to DungeonPopulationLimits.MaxRewardScale on the high end) before a run's own
        /// XpScale/LumScale ever see it. An admin setting the tunable to 50 would have had runs paying a
        /// clamped 20 while every gem printed "Experience: x50.00" - a number the run would never actually
        /// pay, which is exactly the failure ComposeLongDesc's own doc comment claims cannot happen.
        ///
        /// The fix moved the sanitizing call to the store-bound overload (the only caller PropertyManager can
        /// reach), NOT into this pure core - the pure core takes xpScale/lumScale as ordinary parameters and
        /// deliberately trusts them, the same contract xpCap/lumCap/lootQuantityCap already had. This test is
        /// the proof of that contract: an out-of-range value handed directly to the pure core comes back
        /// rendered exactly as given, unclamped and un-defaulted. If the pure core ever grows its own
        /// clamping, this test would need to change to assert THAT instead - it must never be weakened to
        /// pass under both behaviours at once.
        /// </summary>
        [TestMethod]
        public void An_out_of_range_scale_passed_to_the_pure_core_is_rendered_as_given()
        {
            table = new ModifierDef[0];

            // Above DungeonPopulationLimits.MaxRewardScale (20.0) - the spawner would clamp this to 20,
            // but the pure core must not.
            var overCeiling = Compose(Gem(), xpScale: 50.0, lumScale: 50.0);
            StringAssert.Contains(overCeiling, "\nExperience: x50.00\n");
            StringAssert.Contains(overCeiling, "\nLuminance: x50.00\n");
            Assert.IsFalse(overCeiling.Contains("x20.00"), overCeiling);

            // Negative - the spawner would fall back to the compiled default (2.0), but the pure core must
            // not: it renders the nonsensical negative total exactly as handed to it.
            var negative = Compose(Gem(), xpScale: -1.0, lumScale: -1.0);
            StringAssert.Contains(negative, "\nExperience: x-1.00\n");
            StringAssert.Contains(negative, "\nLuminance: x-1.00\n");
            Assert.IsFalse(negative.Contains("x2.00"), negative);
        }
    }
}
