using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Fragment Mold's pure core (FragmentMoldRules). The defs are built directly rather than parsed from a
    /// roster file, so a test can give a component a Dose of 2 - no shipped component has one, which is exactly
    /// why the unit arithmetic (doses * dose) needs a fixture that does.
    /// </summary>
    [TestClass]
    public class FragmentMoldRulesTests
    {
        private const uint Red = 1650;
        private const uint Blue = 1643;
        private const uint Onyx = 2000;     // powder, Dose 2 (fixture only)
        private const uint Gold = 687;      // scarab
        private const uint Iron = 689;      // scarab (second wcid of a one-slot type)
        private const uint Retired = 9999;  // in no defs

        private static ComponentDef C(uint wcid, string name, string type, int dose = 1)
            => new ComponentDef { Wcid = wcid, Name = name, Type = type, Dose = dose, Limit = RawFragmentRules.SlotCounts[type] };

        private static AttunementDef Defs()
        {
            var all = new[]
            {
                C(Red, "Red Taper", "taper"),
                C(Blue, "Blue Taper", "taper"),
                C(Onyx, "Powdered Onyx", "powder", dose: 2),
                C(Gold, "Gold Scarab", "scarab"),
                C(Iron, "Iron Scarab", "scarab"),
            };
            return new AttunementDef(all.ToDictionary(c => c.Wcid), new Dictionary<string, string>());
        }

        private static DungeonGemSpec Blank(int seed = 0)
            => new DungeonGemSpec("any", 185, 7, "any", seed, null, 0, 0);

        private static Func<uint, int> Held(params (uint Wcid, int Count)[] counts)
        {
            var map = counts.ToDictionary(c => c.Wcid, c => c.Count);
            return w => map.TryGetValue(w, out var n) ? n : 0;
        }

        private static readonly IReadOnlyList<(uint Wcid, int Doses)> FourRedOneOnyx =
            new List<(uint, int)> { (Red, 4), (Onyx, 1) };

        // ---- PlanImprint -----------------------------------------------------------------------------

        [TestMethod]
        public void Plan_full_success_bills_exact_units_including_dose_two_and_four_stacked_tapers()
        {
            var plan = FragmentMoldRules.PlanImprint(Blank(), FourRedOneOnyx, Defs(), Held((Red, 10), (Onyx, 10)));

            Assert.AreEqual(ImprintOutcome.Ready, plan.Outcome);
            // Four tapers of ONE wcid stack onto the four taper slots: 4 units, not 1. One powder dose of Dose 2: 2 units, not 1.
            CollectionAssert.AreEqual(new[] { (Red, 4), (Onyx, 2) }, plan.Needs.ToArray());
            Assert.AreEqual(0, plan.Shortages.Count);
            CollectionAssert.AreEqual(new[] { (Red, 4), (Onyx, 1) }, plan.Result.Load.ToArray());
        }

        [TestMethod]
        public void Plan_with_exactly_the_units_held_is_Ready_one_unit_less_is_a_shortage()
        {
            var exact = FragmentMoldRules.PlanImprint(Blank(), FourRedOneOnyx, Defs(), Held((Red, 4), (Onyx, 2)));
            Assert.AreEqual(ImprintOutcome.Ready, exact.Outcome);

            var oneShort = FragmentMoldRules.PlanImprint(Blank(), FourRedOneOnyx, Defs(), Held((Red, 4), (Onyx, 1)));
            Assert.AreEqual(ImprintOutcome.Shortage, oneShort.Outcome);
            // The Dose 2 powder is short by ONE UNIT even though the imprint holds one dose of it.
            CollectionAssert.AreEqual(new[] { (Onyx, 1) }, oneShort.Shortages.ToArray());
        }

        [TestMethod]
        public void Plan_shortage_names_the_right_wcids_and_missing_units_and_consumes_nothing()
        {
            var imprint = new List<(uint, int)> { (Red, 3), (Blue, 1), (Onyx, 1) };
            var defs = Defs();

            var plan = FragmentMoldRules.PlanImprint(Blank(), imprint, defs, Held((Red, 2), (Blue, 5)));

            Assert.AreEqual(ImprintOutcome.Shortage, plan.Outcome);
            // Red: need 3 hold 2 -> 1. Onyx: need 2 hold 0 -> 2. Blue is covered and must not be listed.
            CollectionAssert.AreEqual(new[] { (Red, 1), (Onyx, 2) }, plan.Shortages.ToArray());
            Assert.AreEqual(0, plan.Needs.Count, "A shortage plan must hand the caller nothing to consume.");
            Assert.IsNull(plan.Result);
            Assert.AreEqual("You are short: Red Taper x1, Powdered Onyx x2.", FragmentMoldRules.ComposeShortageLine(plan.Shortages, defs));
        }

        [TestMethod]
        public void Plan_refuses_a_component_no_longer_in_the_defs_even_when_everything_else_is_held()
        {
            var imprint = new List<(uint, int)> { (Red, 1), (Retired, 1) };

            var plan = FragmentMoldRules.PlanImprint(Blank(), imprint, Defs(), Held((Red, 99), (Retired, 99)));

            Assert.AreEqual(ImprintOutcome.Refused, plan.Outcome);
            Assert.AreEqual(RawFragmentRules.RefuseNotAComponent, plan.Refusal);
            Assert.AreEqual(0, plan.Needs.Count, "A refused plan must load and consume nothing, including the valid Red Taper before it.");
            Assert.IsNull(plan.Result);
        }

        [TestMethod]
        public void Plan_refuses_an_imprint_that_overfills_a_slot_type()
        {
            // Two different scarabs: the scarab type has one slot, so the second dose is over the limit.
            var imprint = new List<(uint, int)> { (Gold, 1), (Iron, 1) };

            var plan = FragmentMoldRules.PlanImprint(Blank(), imprint, Defs(), Held((Gold, 9), (Iron, 9)));

            Assert.AreEqual(ImprintOutcome.Refused, plan.Outcome);
            Assert.AreEqual(RawFragmentRules.RefuseOverLimit, plan.Refusal);
            Assert.AreEqual(0, plan.Needs.Count);
        }

        [TestMethod]
        public void Plan_refuses_a_fragment_that_already_holds_a_load()
        {
            var loaded = Blank().WithLoad(new List<(uint, int)> { (Red, 1) });

            var plan = FragmentMoldRules.PlanImprint(loaded, FourRedOneOnyx, Defs(), Held((Red, 99), (Onyx, 99)));

            Assert.AreEqual(ImprintOutcome.Refused, plan.Outcome);
            Assert.AreEqual(FragmentMoldRules.RefuseNotBlank, plan.Refusal);
            Assert.AreEqual(0, plan.Needs.Count);
        }

        // ---- Decide ----------------------------------------------------------------------------------

        [TestMethod]
        public void Decide_a_non_fragment_target_is_refused()
        {
            var d = FragmentMoldRules.Decide(null, "1650:1", Defs(), Held());
            Assert.AreEqual(MoldStep.Refuse, d.Step);
            Assert.AreEqual("The mold does not answer that.", d.Message);
        }

        [TestMethod]
        public void Decide_an_unparseable_spec_reuses_the_unreadable_message()
        {
            var d = FragmentMoldRules.Decide("not a spec", "1650:1", Defs(), Held());
            Assert.AreEqual(MoldStep.Refuse, d.Step);
            Assert.AreEqual(RawFragment.UnreadableMessage, d.Message);
        }

        [TestMethod]
        public void Decide_a_pressed_fragment_reuses_the_already_finished_message()
        {
            var d = FragmentMoldRules.Decide(Blank(seed: 7).Serialize(), "1650:1", Defs(), Held((Red, 9)));
            Assert.AreEqual(MoldStep.Refuse, d.Step);
            Assert.AreEqual(RawFragment.AlreadyFinishedMessage, d.Message);
        }

        [TestMethod]
        public void Decide_a_loaded_fragment_takes_its_imprint_over_the_apply_path()
        {
            var loaded = Blank().WithLoad(new List<(uint, int)> { (Red, 2), (Onyx, 1) });

            // The mold already holds a DIFFERENT imprint and the player holds everything: the loaded fragment
            // must still be read from, never loaded onto.
            var d = FragmentMoldRules.Decide(loaded.Serialize(), "687:1", Defs(), Held((Gold, 9), (Red, 9), (Onyx, 9)));

            Assert.AreEqual(MoldStep.TakeImprint, d.Step);
            Assert.AreEqual("1650:2,2000:1", d.NewImprint);
            Assert.AreEqual("Imprint: Red Taper x2, Powdered Onyx", d.NewLongDesc);
            Assert.AreEqual("The mold takes the imprint: Red Taper x2, Powdered Onyx.", d.Message);
            Assert.IsNull(d.Plan);
        }

        [TestMethod]
        public void Decide_a_blank_fragment_with_an_empty_mold_is_refused()
        {
            foreach (var empty in new string[] { null, "" })
            {
                var d = FragmentMoldRules.Decide(Blank().Serialize(), empty, Defs(), Held((Red, 9)));
                Assert.AreEqual(MoldStep.Refuse, d.Step);
                Assert.AreEqual("The mold holds no imprint.", d.Message);
            }
        }

        [TestMethod]
        public void Decide_a_corrupt_imprint_is_refused_without_planning()
        {
            var d = FragmentMoldRules.Decide(Blank().Serialize(), "garbage", Defs(), Held((Red, 9)));
            Assert.AreEqual(MoldStep.Refuse, d.Step);
            Assert.AreEqual(FragmentMoldRules.RefuseImprintUnreadable, d.Message);
        }

        [TestMethod]
        public void Decide_a_blank_fragment_with_an_imprint_applies_with_a_plan()
        {
            var d = FragmentMoldRules.Decide(Blank().Serialize(), "1650:4,2000:1", Defs(), Held((Red, 4), (Onyx, 2)));

            Assert.AreEqual(MoldStep.Apply, d.Step);
            Assert.AreEqual(ImprintOutcome.Ready, d.Plan.Outcome);
            CollectionAssert.AreEqual(new[] { (Red, 4), (Onyx, 1) }, d.Imprint.ToArray());
        }

        // ---- LoadConsumed (the interrupted path) -----------------------------------------------------

        [TestMethod]
        public void LoadConsumed_with_every_unit_consumed_equals_the_plan_result()
        {
            var plan = FragmentMoldRules.PlanImprint(Blank(), FourRedOneOnyx, Defs(), Held((Red, 4), (Onyx, 2)));
            var consumed = plan.Needs.ToDictionary(n => n.Wcid, n => n.Units);

            var loaded = FragmentMoldRules.LoadConsumed(Blank(), FourRedOneOnyx, Defs(), consumed, out var doses);

            Assert.AreEqual(5, doses);
            Assert.AreEqual(plan.Result.Serialize(), loaded.Serialize());
        }

        [TestMethod]
        public void LoadConsumed_loads_only_the_doses_whose_units_were_actually_taken()
        {
            // Red fully consumed, but only ONE of the powder's two units left the pack: the Dose 2 powder dose
            // is not covered and must not be recorded, while all four Red Tapers are.
            var consumed = new Dictionary<uint, int> { [Red] = 4, [Onyx] = 1 };

            var loaded = FragmentMoldRules.LoadConsumed(Blank(), FourRedOneOnyx, Defs(), consumed, out var doses);

            Assert.AreEqual(4, doses);
            CollectionAssert.AreEqual(new[] { (Red, 4) }, loaded.Load.ToArray());

            var partialRed = FragmentMoldRules.LoadConsumed(Blank(), FourRedOneOnyx, Defs(), new Dictionary<uint, int> { [Red] = 2 }, out var doses2);
            Assert.AreEqual(2, doses2);
            CollectionAssert.AreEqual(new[] { (Red, 2) }, partialRed.Load.ToArray());

            FragmentMoldRules.LoadConsumed(Blank(), FourRedOneOnyx, Defs(), new Dictionary<uint, int>(), out var none);
            Assert.AreEqual(0, none);
        }

        // ---- the shared load codec -------------------------------------------------------------------

        [TestMethod]
        public void The_load_codec_round_trips_and_is_the_text_Serialize_writes()
        {
            var load = new List<(uint Wcid, int Doses)> { (Red, 4), (Onyx, 1) };

            var text = DungeonGemSpec.SerializeLoad(load);
            Assert.AreEqual("1650:4,2000:1", text);
            Assert.IsTrue(Blank().WithLoad(load).Serialize().Contains("|load=" + text));

            Assert.IsTrue(DungeonGemSpec.TryParseLoad(text, out var back, out var error), error);
            CollectionAssert.AreEqual(load, back);

            Assert.IsTrue(DungeonGemSpec.TryParseLoad("", out var empty, out _));
            Assert.AreEqual(0, empty.Count);
            Assert.IsFalse(DungeonGemSpec.TryParseLoad("1650:4,1650:1", out _, out _), "a repeated wcid is illegal");
            Assert.IsFalse(DungeonGemSpec.TryParseLoad("1650:0", out _, out _), "zero doses is illegal");
        }

        // ---- units lost on an interrupted dose -------------------------------------------------------

        [TestMethod]
        public void UnitsLost_counts_units_taken_that_no_recorded_dose_accounts_for()
        {
            // Red fully loaded (4 doses, 4 units); the Dose 2 powder lost one unit of its dose and recorded nothing.
            var consumed = new Dictionary<uint, int> { [Red] = 4, [Onyx] = 1 };
            var loaded = FragmentMoldRules.LoadConsumed(Blank(), FourRedOneOnyx, Defs(), consumed, out _);

            Assert.AreEqual(1, FragmentMoldRules.UnitsLost(loaded.Load, consumed, Defs()));

            var clean = new Dictionary<uint, int> { [Red] = 4, [Onyx] = 2 };
            var whole = FragmentMoldRules.LoadConsumed(Blank(), FourRedOneOnyx, Defs(), clean, out _);
            Assert.AreEqual(0, FragmentMoldRules.UnitsLost(whole.Load, clean, Defs()));
        }

        [TestMethod]
        public void The_interrupted_line_names_what_loaded_and_the_units_that_did_not()
        {
            Assert.AreEqual("The imprint was interrupted. The fragment took: Red Taper x4. 1 unit taken from your packs did not load.",
                FragmentMoldRules.ComposeInterruptedLine("Red Taper x4", 1));
            Assert.AreEqual("The imprint was interrupted. The fragment took nothing.", FragmentMoldRules.ComposeInterruptedLine(null));
            StringAssert.Contains(FragmentMoldRules.ComposeInterruptedLine(null, 3), "3 units taken");
        }

        // ---- source pins (engine half, which cannot run under this harness) -------------------------

        private const string MoldFile = "Source/ACE.Server/ThreadDungeons/FragmentMold.cs";

        [TestMethod]
        public void The_mold_checks_the_target_is_in_the_pack_before_any_decision()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MoldFile), "public static void UseObjectOnTarget(Player player, Gem mold, WorldObject target)");

            var pack = body.IndexOf("player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) == null", StringComparison.Ordinal);
            var decide = body.IndexOf("FragmentMoldRules.Decide(", StringComparison.Ordinal);

            Assert.IsTrue(pack >= 0, "the MyInventory FindObject guard is missing");
            Assert.IsTrue(decide > pack, "the pack check must run BEFORE FragmentMoldRules.Decide");
            Assert.IsTrue(body.IndexOf("RefuseNotInPack", pack, StringComparison.Ordinal) > pack, "the guard must refuse");
        }

        [TestMethod]
        public void The_world_target_refusal_in_Player_Use_covers_the_mold_before_the_walk()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Use.cs"), "public void HandleActionUseWithTarget(uint sourceObjectGuid, uint targetObjectGuid)");

            var guard = body.IndexOf("sourceItem.GetProperty(PropertyBool.FragmentMold) == true", StringComparison.Ordinal);
            var walk = body.IndexOf("CreateMoveToChain(target", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "the FragmentMold world-target guard is missing");
            Assert.IsTrue(walk > guard, "the guard must come before CreateMoveToChain");
        }

        [TestMethod]
        public void Applying_is_refused_while_trading_before_anything_is_consumed()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MoldFile), "private static void Apply(");

            var trading = body.IndexOf("player.IsTrading", StringComparison.Ordinal);
            var consume = body.IndexOf("TryConsumeFromInventoryWithNetworking(", StringComparison.Ordinal);

            Assert.IsTrue(trading >= 0, "the IsTrading refusal is missing from the apply path");
            Assert.IsTrue(consume > trading, "the trade refusal must precede the first consume");
            Assert.IsTrue(body.IndexOf("RefuseTrading", trading, StringComparison.Ordinal) > trading);

            // Taking an imprint stays allowed: the refusal lives in Apply, never in the dispatcher.
            var dispatcher = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MoldFile), "public static void UseObjectOnTarget(Player player, Gem mold, WorldObject target)");
            Assert.IsFalse(dispatcher.Contains("IsTrading"), "IsTrading must not gate the take-imprint branch");
        }

        [TestMethod]
        public void Components_are_consumed_one_dose_at_a_time()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MoldFile), "private static void Apply(");

            StringAssert.Contains(body, "TryConsumeFromInventoryWithNetworking(entry.Wcid, component.Dose)");
            Assert.IsFalse(body.Contains("need.Units"), "a by-wcid bulk consume would strand more than one dose on failure");
        }

        [TestMethod]
        public void A_target_less_use_of_the_mold_is_refused_before_any_consumption_in_UseGem()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Gem.cs"), "public void UseGem(Player player)");

            var branch = body.IndexOf("GetProperty(PropertyBool.FragmentMold) == true", StringComparison.Ordinal);
            var consume = body.IndexOf("TryConsumeFromInventoryWithNetworking(", StringComparison.Ordinal);

            Assert.IsTrue(branch >= 0, "the FragmentMold branch is missing from UseGem");
            Assert.IsTrue(consume > branch, "the branch must precede every consume in UseGem");
            Assert.IsTrue(body.IndexOf("RefuseNoTarget", branch, StringComparison.Ordinal) > branch);

            // The weenie also carries UnlimitedUse (PropertyBool 63) as the second guard.
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1003606 Fragment Mold.sql");
            StringAssert.Contains(sql, "(1003606,  63, True )");
        }

        [TestMethod]
        public void A_target_less_use_of_the_sacrificial_dagger_is_refused_before_any_consumption_in_UseGem()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Gem.cs"), "public void UseGem(Player player)");

            var branch = body.IndexOf("SacrificialDagger.IsDagger(this)", StringComparison.Ordinal);
            var consume = body.IndexOf("TryConsumeFromInventoryWithNetworking(", StringComparison.Ordinal);

            Assert.IsTrue(branch >= 0, "the SacrificialDagger branch is missing from UseGem");
            Assert.IsTrue(consume > branch, "the dagger branch must precede every consume in UseGem, or a target-less use destroys the dagger");
            Assert.IsTrue(body.IndexOf("NoTargetText", branch, StringComparison.Ordinal) > branch);
        }    }
}
