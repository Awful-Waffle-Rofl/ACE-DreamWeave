using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Ember Boons, the boon plan, Heat Gates and perk presets.</summary>
    [TestClass]
    public class FraysBoonsTests
    {
        [TestMethod]
        public void AutoPick_TakesTheHighestListedOfferedBoon_ThenTheTypePlan()
        {
            var offer = new List<string> { "coalpurse", "emberskin", "wildfire" };

            Assert.AreEqual("emberskin", FraysBoons.AutoPick(offer, new List<string> { "kindledblade", "emberskin", "wildfire" }, new List<string> { "embers" }));
            Assert.AreEqual("coalpurse", FraysBoons.AutoPick(offer, new List<string> { "kindledblade" }, new List<string> { "embers" }), "none listed is offered: the type plan");
            Assert.AreEqual(FraysBoons.PlanPick(offer, new List<string>()), FraysBoons.AutoPick(offer, new List<string>(), new List<string>()), "nothing set: balanced");
            Assert.IsNull(FraysBoons.AutoPick(new List<string>(), new List<string> { "emberskin" }, null));
            Assert.AreEqual("your boon order, then embers", FraysBoons.PickRuleText(new List<string> { "emberskin" }, new List<string> { "embers" }));
            Assert.AreEqual("balanced", FraysBoons.PickRuleText(new List<string>(), new List<string>()));
        }

        [TestMethod]
        public void ParseOrder_ReadsNumbersAndNames_DropsRepeatsAndUnknowns()
        {
            Assert.AreEqual(18, FraysBoons.All.Count(b => !b.IsClass), "the generic boons (class boons follow them)");
            CollectionAssert.AreEqual(new List<string> { FraysBoons.All[2].Key, FraysBoons.All[0].Key }, FraysBoons.ParseOrder(new[] { "3", "1", "3", "99", "x" }));
            CollectionAssert.AreEqual(new List<string> { "huntercoal", "emberskin", "wildfire" }, FraysBoons.ParseOrder(new[] { "hunter's", "coal,", "ember", "skin", "wildfire" }));
            CollectionAssert.AreEqual(new List<string> { "snarlsbane", "kindledblade" }, FraysBoons.ParseOrder(new[] { "Snarl's Bane", "3" }));
            Assert.AreEqual(0, FraysBoons.ParseOrder(null).Count);
        }

        [TestMethod]
        public void BoonOrderWindow_ListsEveryBoon_InAsciiText()
        {
            var rec = new FraysPlayerRecord { BoonOrder = new List<string> { "emberskin", "nosuchboon", "wildfire" }, BoonPlan = new List<string> { "aoe" } };
            var text = FraysBoonOrderWindow.OverviewText(rec);

            StringAssert.Contains(text, "#1 Ember Skin");
            StringAssert.Contains(text, "#2 Wildfire Heart");
            StringAssert.Contains(text, "your type plan picks (aoe)");
            Assert.IsFalse(text.Contains("nosuchboon"), "an unknown key is skipped");

            foreach (var b in FraysBoons.All)
                StringAssert.Contains(text, b.Name);

            var all = text + string.Join("\n", FraysBoons.All.Select(FraysBoons.Line)) + FraysBoons.OrderText(rec.BoonOrder);
            Assert.IsTrue(all.All(ch => ch < 128), "ASCII only in player text");
            StringAssert.StartsWith(FraysBoons.Line(FraysBoons.All[0]), "1. Wildfire Heart (aoe): ");
            Assert.AreEqual("none set", FraysBoons.OrderText(new List<string>()));
        }
        [TestMethod]
        public void OffersCrossed_EveryBoonLevelOnce()
        {
            Assert.AreEqual(1, FraysBoons.OffersCrossed(1, 2, 2));
            Assert.AreEqual(2, FraysBoons.OffersCrossed(3, 7, 2), "4 and 6");
            Assert.AreEqual(0, FraysBoons.OffersCrossed(4, 5, 2));
            Assert.AreEqual(0, FraysBoons.OffersCrossed(6, 4, 2), "a drop owes none");
            Assert.AreEqual(0, FraysBoons.OffersCrossed(0, 10, 0), "off");
        }

        [TestMethod]
        public void Offer_ThreeNewOnes_RaresOnlyFromTheirHeat()
        {
            var rng = new Random(7);
            var held = new List<string> { "wildfire", "coalpurse" };

            for (var i = 0; i < 50; i++)
            {
                var low = FraysBoons.Offer(held, 4, 10, rng);
                Assert.AreEqual(3, low.Count);
                Assert.AreEqual(3, low.Distinct().Count());
                Assert.IsFalse(low.Any(held.Contains), "nothing already held");
                Assert.IsFalse(low.Any(k => FraysBoons.Find(k).Rare), "no rares below rare_boon_heat");
            }

            Assert.IsTrue(Enumerable.Range(0, 200).SelectMany(_ => FraysBoons.Offer(held, 12, 10, rng)).Any(k => FraysBoons.Find(k).Rare), "rares appear from rare_boon_heat");
            Assert.AreEqual(0, FraysBoons.Offer(FraysBoons.All.Select(b => b.Key).ToList(), 20, 10, rng).Count, "all held: nothing to offer");
        }

        [TestMethod]
        public void PlanPick_FollowsThePlan_ElseRareThenFirst()
        {
            var offer = new List<string> { "coalpurse", "emberskin", "wildfire" };
            Assert.AreEqual("emberskin", FraysBoons.PlanPick(offer, new List<string> { "sustain", "aoe" }));
            Assert.AreEqual("wildfire", FraysBoons.PlanPick(offer, new List<string> { "snarl", "aoe" }), "the first tag offered wins");
            Assert.AreEqual("coalpurse", FraysBoons.PlanPick(offer, new List<string>()), "balanced: the first offered");
            Assert.AreEqual("infernocore", FraysBoons.PlanPick(new List<string> { "coalpurse", "infernocore" }, null), "balanced: a rare first");
        }

        [TestMethod]
        public void ParsePlan_KeepsKnownTagsInOrder()
        {
            CollectionAssert.AreEqual(new List<string> { "aoe", "sustain", "embers" }, FraysBoons.ParsePlan(new[] { "AoE,", "sustain", "nonsense", "embers", "aoe" }));
        }

        [TestMethod]
        public void BoonText_IsAscii()
        {
            var text = string.Join("", FraysBoons.All.Select(b => b.Name + b.Text)) + FraysBoons.OfferText(new List<string> { "wildfire", "phoenixcoal", "c_frenzy" }) + string.Join("", FraysBoons.All.Select(b => b.TextAt(3)));
            Assert.IsTrue(text.All(ch => ch < 128));
        }

        [TestMethod]
        public void RunScore_PeakFirst()
        {
            Assert.AreEqual(4000 + 500 + 100 - 50, FraysScore.RunScore(20, 500, 2, 2));
            Assert.AreEqual(0, FraysScore.RunScore(0, 0, 0, 10), "never below 0");
        }

        [TestMethod]
        public void HeatChart_ScalesToThePeak()
        {
            var log = new List<(double, int)> { (0, 0), (10, 5), (20, 10), (30, 10), (40, 6) };
            StringAssert.StartsWith(FraysReport.HeatChart(log, 10), "05995");
            Assert.AreEqual("none", FraysReport.HeatChart(new List<(double, int)>(), 5));
        }

        [TestMethod]
        public void EliteAffixes_RotateWeekly_Ascii()
        {
            Assert.AreNotEqual(FraysWards.ForWeek(1).Name, FraysWards.ForWeek(2).Name);
            Assert.AreEqual(FraysWards.ForWeek(1).Name, FraysWards.ForWeek(1 + FraysWards.All.Count).Name);
            var text = string.Join("", FraysWards.All.Select(a => a.Name + a.Text));
            Assert.IsTrue(text.All(ch => ch < 128));
        }

        [TestMethod]
        public void EliteAffixes_ResistOne_WeakToAnother()
        {
            // One type resisted and one weak, never the same, every damage type resisted once
            Assert.AreEqual(7, FraysWards.All.Count);
            Assert.IsTrue(FraysWards.All.All(a => a.Resist != a.Weak));
            Assert.AreEqual(7, FraysWards.All.Select(a => a.Resist).Distinct().Count());
            Assert.AreEqual(7, FraysWards.All.Select(a => a.Weak).Distinct().Count());
            Assert.IsTrue(FraysWards.ResistMult < 1 && FraysWards.WeakMult > 1);
        }

        [TestMethod]
        public void Codex_CountsEveryNote_OldEntriesReadAsOne()
        {
            var rec = new FraysPlayerRecord();
            Assert.IsTrue(FraysCodex.Note(rec, FraysCodex.Family + "x"));
            Assert.IsFalse(FraysCodex.Note(rec, FraysCodex.Family + "x"));
            FraysCodex.Note(rec, FraysCodex.Family + "x");
            Assert.AreEqual(3, FraysCodex.Times(rec, FraysCodex.Family + "x"));

            var old = new FraysPlayerRecord { CodexCounts = null };
            old.Codex.Add(FraysCodex.Rare + "Blackwick");
            Assert.AreEqual(1, FraysCodex.Times(old, FraysCodex.Rare + "Blackwick"));
            Assert.AreEqual(0, FraysCodex.Times(old, FraysCodex.Rare + "Ashmother"));

            var back = FraysPlayerRecord.Parse(rec.ToJson());
            Assert.AreEqual(3, FraysCodex.Times(back, FraysCodex.Family + "x"));
            StringAssert.Contains(FraysCodex.Readout(rec, "companions"), "Companions");
        }

        [TestMethod]
        public void Achievements_GiveEpithets_Once()
        {
            var rec = new FraysPlayerRecord { BestHeat = 12, SnarlKills = 25 };
            var said = FraysCodex.Check(null, rec);
            Assert.IsTrue(rec.Achievements.SetEquals(new[] { "heat5", "heat10", "snarl25" }), string.Join(",", rec.Achievements));
            CollectionAssert.AreEquivalent(new List<string> { "the Kindler", "Snarlbane" }, rec.Epithets);
            Assert.AreEqual(3, said.Count);
            Assert.AreEqual(0, FraysCodex.Check(null, rec).Count, "never twice");
            Assert.IsTrue(said.All(s => s.All(ch => ch < 128)));
        }

        [TestMethod]
        public void Codex_NotesOnce_AndCounts()
        {
            var rec = new FraysPlayerRecord();
            Assert.IsTrue(FraysCodex.Note(rec, FraysCodex.Family + "lugian"));
            Assert.IsFalse(FraysCodex.Note(rec, FraysCodex.Family + "lugian"));
            FraysCodex.Note(rec, FraysCodex.Rare + "Old Smolder");
            Assert.AreEqual(1, FraysCodex.Count(rec, FraysCodex.Family));
            Assert.AreEqual(1, FraysCodex.Count(rec, FraysCodex.Rare));
        }

        [TestMethod]
        public void FireName_LettersOnly_NothingRude()
        {
            Assert.AreEqual("Pyre of Ash", FraysCodex.CleanFireName("  Pyre   of Ash "));
            Assert.AreEqual("Tester's Inferno", FraysCodex.CleanFireName("Tester's Inferno"));
            Assert.IsNull(FraysCodex.CleanFireName("ab"), "too short");
            Assert.IsNull(FraysCodex.CleanFireName("The Very Long Name Of A Fire"), "too long");
            Assert.IsNull(FraysCodex.CleanFireName("Fire 2"), "digits");
            Assert.IsNull(FraysCodex.CleanFireName("Sh it Fire"), "a blocked word, spaced out");

            var rec = new FraysPlayerRecord { FireName = "The Pyre", Epithet = "the Ashen" };
            Assert.AreEqual("The Pyre - Driftwood Flats (Tester the Ashen)", FraysCodex.LitName("Driftwood Flats", "Tester", rec));
            Assert.AreEqual("Frays Campfire - Driftwood Flats (Tester)", FraysCodex.LitName("Driftwood Flats", "Tester", null));
        }

        [TestMethod]
        public void FraysFellowships_TheFirstIsTheFrays_OverflowIsNumbered()
        {
            // The 21st camper starts a second fellowship.
            Assert.AreEqual("The Frays", FraysManager.FraysFellowshipName(1));
            Assert.AreEqual("The Frays 2", FraysManager.FraysFellowshipName(2));
            Assert.AreEqual("The Frays 3", FraysManager.FraysFellowshipName(3));
            Assert.IsFalse(FraysManager.InFraysFellowship(null, 0));
        }

        [TestMethod]
        public void FireText_SoloFireIsYoursAlone_GroupFireIsTheFellowships()
        {
            // Solo camps are not shared with the fellowship, and the creatures do not come to you.
            StringAssert.Contains(FraysFire.FireUseText(false), "yours alone");
            Assert.IsFalse(FraysFire.FireLongDesc(false).Contains("fellowship"));
            Assert.IsFalse(FraysFire.FireLongDesc(false).Contains("come to you"));
            StringAssert.Contains(FraysFire.FireUseText(true), "fellowship");

            foreach (var s in new[] { FraysFire.FireUseText(false), FraysFire.FireUseText(true), FraysFire.FireLongDesc(false), FraysFire.FireLongDesc(true) })
                Assert.IsFalse(s.Contains('–') || s.Contains('—'), "no long dashes in player text");
        }

        [TestMethod]
        public void Effigy_EveryCustomFamilyHasAForm()
        {
            Assert.IsTrue(FraysFamilies.Custom.All(f => FraysEffigies.FormWcid(f) != 0), "every family has a creature to wear");
            Assert.AreEqual(0u, FraysEffigies.FormWcid(null));
        }

        [TestMethod]
        public void AutoSpend_FillsThePresetRoundAndRound()
        {
            var rec = new FraysPlayerRecord { FireTier = 3, PerkPreset = "aoe" };
            var bought = FraysPerks.AutoSpend(rec);
            CollectionAssert.AreEqual(new List<string> { "Bellows", "Searing", "Ash Guard" }, bought);
            Assert.AreEqual(0, FraysPerks.Unspent(rec));

            var off = new FraysPlayerRecord { FireTier = 3, PerkPreset = "off" };
            Assert.AreEqual(0, FraysPerks.AutoSpend(off).Count);
            Assert.AreEqual(3, FraysPerks.Unspent(off));

            var all = new FraysPlayerRecord { FireTier = 10, PerkPreset = "balanced" };
            FraysPerks.AutoSpend(all);
            Assert.AreEqual(10, FraysPerks.Spent(all), "every point spent, no rank past its max");
            Assert.IsTrue(FraysPerks.All.All(p => FraysPerks.Rank(all, p.Key) <= p.MaxRank));
        }

        // ---- class boons and costs ----

        private static Func<ACE.Server.ClassAbilities.ClassAbilityId, int> Ranks(params (ACE.Server.ClassAbilities.ClassAbilityId Id, int Rank)[] have) =>
            id => have.Where(h => h.Id == id).Select(h => h.Rank).FirstOrDefault();

        [TestMethod]
        public void EveryBoon_HasACost()
        {
            foreach (var b in FraysBoons.All)
            {
                Assert.IsFalse(string.IsNullOrEmpty(b.Cost), b.Key);
                Assert.IsNotNull(b.Pay, b.Key);
                Assert.IsTrue(b.Text.Contains("; but "), b.Key);
            }
        }

        [TestMethod]
        public void ClassBand_GrowsWithAbilityRank()
        {
            Assert.AreEqual(0, FraysBoons.ClassBand(0, 3));
            Assert.AreEqual(1, FraysBoons.ClassBand(1, 3));
            Assert.AreEqual(3, FraysBoons.ClassBand(3, 3));
            Assert.AreEqual(1, FraysBoons.ClassBand(1, 5));
            Assert.AreEqual(2, FraysBoons.ClassBand(3, 5));
            Assert.AreEqual(3, FraysBoons.ClassBand(5, 5));
            Assert.AreEqual(3, FraysBoons.ClassBand(9, 3), "past the max: the top band");
        }

        [TestMethod]
        public void Offer_ClassBoonsOnlyForOwnedAbilities_OneOrTwo()
        {
            var rng = new Random(11);
            var ranks = Ranks((ACE.Server.ClassAbilities.ClassAbilityId.Frenzy, 2), (ACE.Server.ClassAbilities.ClassAbilityId.Executioner, 3), (ACE.Server.ClassAbilities.ClassAbilityId.Bloodlust, 1));
            var mine = new[] { "c_frenzy", "c_executioner", "c_bloodlust" };
            var counts = new HashSet<int>();

            for (var i = 0; i < 200; i++)
            {
                var offer = FraysBoons.Offer(new List<string>(), 12, 10, rng, ranks);
                Assert.AreEqual(3, offer.Count);
                var cls = offer.Where(k => FraysBoons.Find(k).IsClass).ToList();
                Assert.IsTrue(cls.All(mine.Contains), "only abilities the player has");
                Assert.IsTrue(cls.Count is >= 1 and <= 2);
                counts.Add(cls.Count);
            }

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, counts.ToList(), "both one and two come up");
            Assert.IsFalse(Enumerable.Range(0, 100).SelectMany(_ => FraysBoons.Offer(new List<string>(), 12, 10, rng)).Any(k => FraysBoons.Find(k).IsClass), "no abilities: generic only");
        }

        [TestMethod]
        public void Balanced_TakesTheClassBoonForTheHighestRankAbility()
        {
            var ranks = Ranks((ACE.Server.ClassAbilities.ClassAbilityId.Frenzy, 1), (ACE.Server.ClassAbilities.ClassAbilityId.Executioner, 3));
            var offer = new List<string> { "infernocore", "c_frenzy", "c_executioner" };
            Assert.AreEqual("c_executioner", FraysBoons.AutoPick(offer, null, null, ranks));
            Assert.AreEqual("c_frenzy", FraysBoons.AutoPick(new List<string> { "coalpurse", "c_frenzy" }, null, null, ranks), "a class boon over a generic one");
            Assert.AreEqual("infernocore", FraysBoons.AutoPick(new List<string> { "coalpurse", "infernocore" }, null, null, ranks), "no class boon: a rare first");
            Assert.AreEqual("coalpurse", FraysBoons.AutoPick(offer.Append("coalpurse").ToList(), new List<string> { "coalpurse" }, null, ranks), "the boon order still wins");
        }

        [TestMethod]
        public void ClassBoon_StrengthensItsAbility_AtItsBand_AndCosts()
        {
            var low = new FraysCampMods();
            FraysBoons.Apply(new[] { "c_executioner" }, low, k => 4, Ranks((ACE.Server.ClassAbilities.ClassAbilityId.Executioner, 1)));
            var top = new FraysCampMods();
            FraysBoons.Apply(new[] { "c_executioner" }, top, k => 1, Ranks((ACE.Server.ClassAbilities.ClassAbilityId.Executioner, 3)));
            var none = new FraysCampMods();
            FraysBoons.Apply(new[] { "c_executioner" }, none, k => 4, Ranks());

            var b = FraysBoons.Find("c_executioner");
            Assert.AreEqual(1 + b.Base, low.AbilityBoost[ACE.Server.ClassAbilities.ClassAbilityId.Executioner], 1e-9, "collection rank does not matter");
            Assert.AreEqual(1 + 2 * b.Base, top.AbilityBoost[ACE.Server.ClassAbilities.ClassAbilityId.Executioner], 1e-9);
            Assert.IsTrue(low.SnarlDamage < 1, "its cost");
            Assert.AreEqual(0, none.AbilityBoost.Count, "without the ability: nothing");
            Assert.AreEqual(1, none.SnarlDamage, "and no cost");
        }

        [TestMethod]
        public void ClassBoons_TakeNoSigils_AndOldKeysStay()
        {
            Assert.IsTrue(FraysBoons.All.Where(b => b.IsClass).All(b => FraysCollection.SigilFor(b, 1) == 0));
            var old = new[] { "wildfire", "packmaster", "kindledblade", "huntercoal", "emberskin", "ironember", "ashenmend", "brightwick", "coalpurse", "slowcoals", "snarlbait", "grudgefire",
                "infernocore", "firestorm", "phoenixcoal", "goldencinders", "undyingflame", "snarlsbane" };
            CollectionAssert.AreEqual(old, FraysBoons.All.Take(old.Length).Select(b => b.Key).ToArray(), "saved camps, orders and window numbers load as they were");
        }

        [TestMethod]
        public void ClassBoons_CostDifferentThings()
        {
            // Class boon costs vary, not all "you take more damage".
            var kinds = FraysBoons.All.Where(b => b.IsClass).Select(b => b.Cost.Split(' ').Last()).Distinct().Count();
            Assert.IsTrue(kinds >= 4, $"{kinds} kinds of cost");
            Assert.IsTrue(FraysBoons.All.Where(b => b.IsClass).Count(b => b.Cost.Contains("more damage")) <= 5, "few cost damage taken");
        }

        [TestMethod]
        public void ClassBoons_TextShowsTheRealEffect()
        {
            // The text shows the real rise, since a bare "+60% dmg to summons" reads as far more than it is.
            var thralls = FraysBoons.Find("c_thralls");
            StringAssert.Contains(thralls.TextForAbilityRank(3), "from +25% to +40%");
            StringAssert.Contains(thralls.TextForAbilityRank(1), "from +8.3% to +10.8%");
            StringAssert.Contains(thralls.Text, "rank 3");
            StringAssert.Contains(FraysBoons.Find("c_executioner").TextAt(3), "from +21% to +33.6%");
            StringAssert.Contains(FraysBoons.Find("c_acid").TextForAbilityRank(2), "from 30% to 41.3%");

            foreach (var b in FraysBoons.All.Where(b => b.IsClass && b.Real != null))
                for (var r = 1; r <= b.AbilityMaxRank; r++)
                {
                    var t = b.TextForAbilityRank(r);
                    Assert.IsFalse(t.Contains('{') || t.Contains('–') || t.Contains('—'), $"{b.Key} {r}: {t}");
                    StringAssert.Contains(t, "; but ", b.Key);
                }

            Assert.AreEqual(19, FraysBoons.All.Count(b => b.IsClass && b.Real != null), "every ability-boost class boon has one");
        }

        [TestMethod]
        public void ClassBoon_AbilitiesExist()
        {
            foreach (var b in FraysBoons.All.Where(b => b.IsClass))
                Assert.IsTrue(ACE.Server.ClassAbilities.ClassAbilityRegistry.Abilities.ContainsKey(b.Ability.Value), b.Key);
        }

        [TestMethod]
        public void StartOffers_OwesTheBoonLevelsAtOrBelowTheStart()
        {
            Assert.AreEqual(0, FraysBoons.StartOffers(0, 3));
            Assert.AreEqual(0, FraysBoons.StartOffers(2, 3));
            Assert.AreEqual(3, FraysBoons.StartOffers(9, 3), "Heat 3, 6 and 9");
            Assert.AreEqual(3, FraysBoons.StartOffers(11, 3));
            Assert.AreEqual(0, FraysBoons.StartOffers(9, 0), "boons off");

            // with the climb's own offers, a camp started at 9 and climbed to 12 has had the same four as one started at 0
            Assert.AreEqual(FraysBoons.OffersCrossed(0, 12, 3), FraysBoons.StartOffers(9, 3) + FraysBoons.OffersCrossed(9, 12, 3));
        }

        [TestMethod]
        public void BloodMageBoons_OfferedOnSanguineReserve_SplashGrowsWithRank()
        {
            // A Blood Mage (Sanguine Reserve, Malediction, Empowered Summons; no Hemomancy) must be offered a class boon.
            var ferp = Ranks((ACE.Server.ClassAbilities.ClassAbilityId.SanguineReserve, 3), (ACE.Server.ClassAbilities.ClassAbilityId.Malediction, 3),
                (ACE.Server.ClassAbilities.ClassAbilityId.EmpoweredSummons, 3));
            var offered = new HashSet<string>();
            var rng = new Random(5);

            for (var i = 0; i < 200; i++)
                foreach (var k in FraysBoons.Offer(new List<string>(), 12, 10, rng, ferp))
                    offered.Add(k);

            foreach (var key in new[] { "c_bloodsplash", "c_hecatomb", "c_sanguine", "c_maledict", "c_thralls" })
                Assert.IsTrue(offered.Contains(key), key);
            Assert.IsFalse(offered.Contains("c_bloodprice"), "no Blood Price, no Ember Tithe");

            int Splash(int rank)
            {
                var m = new FraysCampMods();
                FraysBoons.Apply(new[] { "c_bloodsplash" }, m, k => 1, Ranks((ACE.Server.ClassAbilities.ClassAbilityId.SanguineReserve, rank)));
                return m.BloodSplash;
            }

            var max = ACE.Server.ClassAbilities.ClassAbilityRegistry.Abilities[ACE.Server.ClassAbilities.ClassAbilityId.SanguineReserve].MaxRank;
            Assert.AreEqual(0, Splash(0));
            Assert.AreEqual(1, Splash(1));
            Assert.AreEqual(3, Splash(max));

            var reach = new FraysCampMods();
            FraysBoons.Apply(new[] { "c_hecatomb" }, reach, k => 1, ferp);
            Assert.IsTrue(reach.HecatombReach);
            Assert.IsTrue(reach.GildedDamage < 1, "its cost");
            Assert.IsFalse(FraysBoons.All.Any(b => b.Text.Contains('–') || b.Text.Contains('—')));
        }
    }
}
