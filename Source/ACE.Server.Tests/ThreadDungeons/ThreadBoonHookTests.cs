using System;
using System.IO;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadBoonTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The six Thread Shrine hooks. The harness cannot build a Player (its constructors read the auth database), so each
    /// hook is proven in two halves: the arithmetic it applies, driven through the same ThreadBoons calls with a holder
    /// inside, a non-holder and an outside holder (presence through IsMemberInside's guid + position overload); and a
    /// source pin that the hook site calls exactly that arithmetic at the place the design names (receiver side for XP and
    /// luminance, inside the ceiling for attack speed, the base term for the ratings). Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadBoonHookTests
    {
        private static double Magnitude(ThreadDungeonRun run, uint guid, ACE.Entity.Position at, ThreadBoonKind kind) => ThreadBoons.ForRun(run, guid, at, kind);

        /// <summary>A run where Owner holds every kind at 1.25 and Member holds nothing.</summary>
        private static ThreadDungeonRun Blessed(Releaser scope)
        {
            var run = scope.Track(Run(Member));

            for (var i = 0; i < 6; i++)
                Use(run, AllKinds(), new[] { Owner });

            return run;
        }

        // ---- damage dealt / mitigation ----------------------------------------------------------------------

        [TestMethod]
        public void Damage_rating_moves_by_25_for_a_holder_inside_only()
        {
            using var scope = new Releaser();
            var run = Blessed(scope);

            Assert.AreEqual(25, ThreadBoons.RatingBonus(Magnitude(run, Owner, Inside(run), ThreadBoonKind.Damage)));
            Assert.AreEqual(0, ThreadBoons.RatingBonus(Magnitude(run, Member, Inside(run), ThreadBoonKind.Damage)), "non-holder");
            Assert.AreEqual(0, ThreadBoons.RatingBonus(Magnitude(run, Owner, OtherInstance(run), ThreadBoonKind.Damage)), "holder outside");

            Assert.AreEqual(0, ThreadBoons.RatingBonus(1.0));
            Assert.AreEqual(0, ThreadBoons.RatingBonus(0.8));
            Assert.AreEqual(0, ThreadBoons.RatingBonus(double.NaN));
            Assert.AreEqual(10, ThreadBoons.RatingBonus(1.1));
        }

        [TestMethod]
        public void Mitigation_moves_resist_rating_by_25_which_is_20_percent_less_damage()
        {
            using var scope = new Releaser();
            var run = Blessed(scope);

            var rating = ThreadBoons.RatingBonus(Magnitude(run, Owner, Inside(run), ThreadBoonKind.Mitigation));
            Assert.AreEqual(25, rating);
            Assert.AreEqual(0, ThreadBoons.RatingBonus(Magnitude(run, Member, Inside(run), ThreadBoonKind.Mitigation)));
            Assert.AreEqual(0, ThreadBoons.RatingBonus(Magnitude(run, Owner, OffLandblock(run), ThreadBoonKind.Mitigation)));

            // The real reduction the rating gives through the engine's own negative-rating curve.
            Assert.AreEqual(0.8f, Creature.GetNegativeRatingMod(rating), 1e-6f);
            Assert.AreEqual(20, ThreadBoons.PercentFor(ThreadBoonKind.Mitigation, 1.25));
        }

        [TestMethod]
        public void Both_rating_hooks_fold_into_the_base_term_for_players_only()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Rating.cs");

            var damage = PooledLootSourceText.MethodBody(source, "public int GetDamageRating()");
            StringAssert.Contains(damage, "if (this is Player tsbPlayer)\n                damageRating += ACE.Server.ThreadDungeons.ThreadBoons.DamageRatingFor(tsbPlayer);");
            Assert.IsTrue(damage.IndexOf("ThreadBoons.DamageRatingFor", StringComparison.Ordinal) < damage.IndexOf("return damageRating", StringComparison.Ordinal));

            var resist = PooledLootSourceText.MethodBody(source, "public int GetDamageResistRating(CombatType? combatType = null, bool directDamage = true)");
            StringAssert.Contains(resist, "if (this is Player tsbPlayer)\n                damageResistRating += ACE.Server.ThreadDungeons.ThreadBoons.DamageResistRatingFor(tsbPlayer);");
            Assert.IsTrue(resist.IndexOf("ThreadBoons.DamageResistRatingFor", StringComparison.Ordinal) < resist.IndexOf("return damageResistRating", StringComparison.Ordinal));

            var boons = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadBoons.cs");
            StringAssert.Contains(boons, "public static int DamageRatingFor(Player player) => RatingBonus(For(player, ThreadBoonKind.Damage));");
            StringAssert.Contains(boons, "public static int DamageResistRatingFor(Player player) => RatingBonus(For(player, ThreadBoonKind.Mitigation));");
        }

        // ---- attack speed ----------------------------------------------------------------------------------------

        [TestMethod]
        public void Attack_speed_multiplies_the_product_and_the_ceiling_still_clamps()
        {
            using var scope = new Releaser();
            var run = Blessed(scope);

            var holder = (float)Magnitude(run, Owner, Inside(run), ThreadBoonKind.AttackSpeed);
            var outside = (float)Magnitude(run, Owner, OtherInstance(run), ThreadBoonKind.AttackSpeed);
            var other = (float)Magnitude(run, Member, Inside(run), ThreadBoonKind.AttackSpeed);

            Assert.AreEqual(1.25f, holder, 1e-6f);
            Assert.AreEqual(1.0f, outside);
            Assert.AreEqual(1.0f, other);

            Assert.AreEqual(1.5f * 1.25f, Player.ComposeAttackSpeed(1.5f, 1.0f * holder, 2.5f), 1e-5f, "+25% under the ceiling");
            Assert.AreEqual(1.5f, Player.ComposeAttackSpeed(1.5f, 1.0f * outside, 2.5f), "no blessing, no change");
            Assert.AreEqual(2.0f, Player.ComposeAttackSpeed(1.8f, 1.1f * holder, 2.0f), 1e-6f, "class_ability_attack_speed_ceiling still clamps");

            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");
            var body = PooledLootSourceText.MethodBody(src, "public float ApplyClassAbilityAttackSpeed(float baseAnimSpeed, bool includeConditional)");

            var product = body.IndexOf("var mod = GetFrenzyAttackSpeedMod() * GetAttackSpeedSkillMod() * GetWeaponModAttackSpeedMod(includeConditional);", StringComparison.Ordinal);
            var boon = body.IndexOf("if (includeConditional)\n                mod *= ACE.Server.ThreadDungeons.ThreadBoons.AttackSpeedFactorFor(this);", StringComparison.Ordinal);
            var clamp = body.IndexOf("return ComposeAttackSpeed(baseAnimSpeed, mod, ceiling);", StringComparison.Ordinal);

            Assert.IsTrue(product >= 0 && product < boon && boon < clamp, "the blessing joins the product, combat call only, before the one ceiling clamp");
        }

        // ---- XP and luminance: receiver side ---------------------------------------------------------------------

        [TestMethod]
        public void Xp_and_luminance_scale_the_receivers_own_amount()
        {
            using var scope = new Releaser();
            var run = Blessed(scope);

            foreach (var kind in new[] { ThreadBoonKind.Xp, ThreadBoonKind.Luminance })
            {
                Assert.AreEqual(1250L, ThreadBoons.ScaleAmount(1000, Magnitude(run, Owner, Inside(run), kind)), kind.ToString());
                Assert.AreEqual(1000L, ThreadBoons.ScaleAmount(1000, Magnitude(run, Member, Inside(run), kind)), kind + " non-holder");
                Assert.AreEqual(1000L, ThreadBoons.ScaleAmount(1000, Magnitude(run, Owner, OtherInstance(run), kind)), kind + " outside");
            }

            Assert.AreEqual(0L, ThreadBoons.ScaleAmount(0, 1.25));
            Assert.AreEqual(-5L, ThreadBoons.ScaleAmount(-5, 1.25), "never touches a refused negative");
            Assert.AreEqual(long.MaxValue - 1, ThreadBoons.ScaleAmount(long.MaxValue - 1, 1.25), "an overflowing product leaves the amount alone");
        }

        [TestMethod]
        public void A_fellows_share_is_not_boosted_by_another_members_blessing()
        {
            using var scope = new Releaser();
            var run = Blessed(scope); // Owner holds XP and luminance, Member holds nothing

            // Fellowship.SplitXp hands each fellow its share and each share re-enters GrantXP on the RECEIVER, so the boon
            // the receiver applies is its own: a 1000-point kill split two ways.
            const long share = 500;
            Assert.AreEqual(625L, ThreadBoons.ScaleAmount(share, Magnitude(run, Owner, Inside(run), ThreadBoonKind.Xp)), "the holder's share");
            Assert.AreEqual(500L, ThreadBoons.ScaleAmount(share, Magnitude(run, Member, Inside(run), ThreadBoonKind.Xp)), "the fellow's share is untouched");

            var xp = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Xp.cs"),
                "public void GrantXP(long amount, XpType xpType, ShareType shareType, bool combatShare)");

            var split = xp.IndexOf("Fellowship.SplitXp((ulong)amount, xpType, shareType, this);", StringComparison.Ordinal);
            var splitReturn = xp.IndexOf("return;", split, StringComparison.Ordinal);
            var boon = xp.IndexOf("ThreadBoons.For(this, ACE.Server.ThreadDungeons.ThreadBoonKind.Xp)", StringComparison.Ordinal);
            var guard = xp.LastIndexOf("if (xpType == XpType.Kill || combatShare)", boon, StringComparison.Ordinal);
            var enqueue = xp.IndexOf("EnqueueAction(new ActionEventDelegate(() => UpdateXpAndLevel(levelAmount, xpType)));", StringComparison.Ordinal);
            var itemCapture = xp.IndexOf("var itemAmount = levelAmount;", StringComparison.Ordinal);

            Assert.IsTrue(split >= 0 && splitReturn < boon, "after the split returns: receiver side");
            Assert.IsTrue(guard > itemCapture && guard < boon && boon < enqueue, "combat XP only, after item XP was captured, before the level grant");
            Assert.AreEqual(1, Count(xp, "ThreadBoonKind.Xp"));
            Assert.IsFalse(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Xp.cs"),
                "public void EarnXP(long amount, XpType xpType, ShareType shareType = ShareType.All)").Contains("ThreadBoons"), "never in EarnXP (that would leak to fellows)");

            var lum = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Luminance.cs"),
                "public void GrantLuminance(long amount, XpType xpType, ShareType shareType, bool combatShare)");

            var lumSplit = lum.IndexOf("Fellowship.SplitLuminance((ulong)amount, xpType, shareType, this);", StringComparison.Ordinal);
            var lumElse = lum.IndexOf("else", lumSplit, StringComparison.Ordinal);
            var lumBoon = lum.IndexOf("ThreadBoons.For(this, ACE.Server.ThreadDungeons.ThreadBoonKind.Luminance)", StringComparison.Ordinal);
            var lumAdd = lum.IndexOf("AddLuminance(addAmount, xpType);", StringComparison.Ordinal);

            Assert.IsTrue(lumSplit >= 0 && lumSplit < lumElse && lumElse < lumBoon && lumBoon < lumAdd, "in the receiver's own branch, before the add");
            Assert.AreEqual(1, Count(lum, "ThreadBoonKind.Luminance"));
        }

        // ---- Thorns ----------------------------------------------------------------------------------------------
        // (No PvP guard here on purpose: owner ruling 2026-11-09, blessings are not suppressed for PK players inside a
        // thread, see Docs/Threads/ENCOUNTERS.md.)

        [TestMethod]
        public void The_mitigation_blessing_never_grows_the_thorns_reflect()
        {
            // Player.ApplyThornsReflect scales the reflect by the TOTAL Damage Resist Rating, which now includes the
            // blessing; the reflect takes the blessing's rating back out. A holder inside gives back exactly the 25 the
            // resist getter added, and a holder outside 0, so the reflect reads the pre-shrine rating either way.
            using var scope = new Releaser();
            var run = Blessed(scope);

            Assert.AreEqual(25, ThreadBoons.RatingBonus(Magnitude(run, Owner, Inside(run), ThreadBoonKind.Mitigation)));
            Assert.AreEqual(0, ThreadBoons.RatingBonus(Magnitude(run, Owner, OtherInstance(run), ThreadBoonKind.Mitigation)));

            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityCombat.cs");
            var reflect = PooledLootSourceText.MethodBody(src, "public void ApplyThornsReflect(Creature attacker, DamageType damageType, double strengthMultiplier)");

            StringAssert.Contains(reflect, "RatingAbility.DamageResistRatingThornsMultiplier(drrGrant, GetDamageResistRating() - ACE.Server.ThreadDungeons.ThreadBoons.DamageResistRatingFor(this))");
            Assert.IsFalse(reflect.Contains("(drrGrant, GetDamageResistRating())", StringComparison.Ordinal), "no uncorrected total read left");
        }

        // ---- text ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Every_line_is_the_owner_text_and_fits_one_chat_line()
        {
            Assert.AreEqual("The shrine blesses you: +25% damage dealt for the rest of this thread.", ThreadBoons.GrantLine(ThreadBoonKind.Damage, 1.25));
            Assert.AreEqual("The shrine blesses you: 20% less damage taken for the rest of this thread.", ThreadBoons.GrantLine(ThreadBoonKind.Mitigation, 1.25));
            Assert.AreEqual("The shrine blesses you: +25% attack speed for the rest of this thread.", ThreadBoons.GrantLine(ThreadBoonKind.AttackSpeed, 1.25));
            Assert.AreEqual("The shrine blesses you: +25% experience for the rest of this thread.", ThreadBoons.GrantLine(ThreadBoonKind.Xp, 1.25));
            Assert.AreEqual("The shrine blesses you: +25% luminance for the rest of this thread.", ThreadBoons.GrantLine(ThreadBoonKind.Luminance, 1.25));
            Assert.AreEqual("The shrine blesses you: +25% more items from kills for the rest of this thread.", ThreadBoons.GrantLine(ThreadBoonKind.Loot, 1.25));

            Assert.AreEqual("Blessing: +25% damage dealt", ThreadBoons.StatusLine(ThreadBoonKind.Damage, 1.25));
            Assert.AreEqual("Blessing: 20% less damage taken", ThreadBoons.StatusLine(ThreadBoonKind.Mitigation, 1.25));

            foreach (var kind in Enum.GetValues(typeof(ThreadBoonKind)).Cast<ThreadBoonKind>())
            {
                Assert.IsTrue(ThreadBoons.GrantLine(kind, 2.0).Length < 255, kind.ToString());
                Assert.IsNotNull(ThreadBoons.StatusLine(kind, 2.0));
            }

            Assert.IsTrue(ThreadBoons.GuardiansLine.Length < 255 && ThreadBoons.SilentLine.Length < 255 && ThreadBoons.SpentLine.Length < 255);
        }

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
