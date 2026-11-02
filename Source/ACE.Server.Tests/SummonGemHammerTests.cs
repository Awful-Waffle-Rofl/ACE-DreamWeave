using System;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom Summons Task 7: the Summon Gem Hammer. SummonGemHammer.Break is driven against real objects and a fake pack
    /// that logs every operation in order and can fail at any step, so each rollback is checked for "gem intact, nothing
    /// duplicated". The animated Player shell (UseOnTarget under IsBusy) is an owner in-game check; its wiring in Gem.cs
    /// is pinned by the source scans at the bottom.
    /// </summary>
    [TestClass]
    public class SummonGemHammerTests
    {
        private static Func<uint, WeenieType?> previousPetTypes;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            previousPetTypes = DuoGemTestKit.InstallPetTypes();
        }

        [ClassCleanup]
        public static void Cleanup() => DuoSummonGem.PetWeenieTypeLookup = previousPetTypes;

        private static (FakeDuoGemInventory pack, PetDevice gem, Gem hammer) Setup()
        {
            var pack = new FakeDuoGemInventory();

            var alpha = DuoGemTestKit.Summon("Alpha", 5001, structure: 7, damage: 20, crit: 14);
            alpha.SetProperty(PropertyString.Inscription, "first");
            var beta = DuoGemTestKit.Summon("Beta", 5002, structure: 2, damage: 5);

            var gem = DuoGemTestKit.BoundGem(pack, alpha, beta);
            var hammer = DuoGemTestKit.Hammer();
            pack.Put(hammer);

            return (pack, gem, hammer);
        }

        private static void AssertGemIntact(FakeDuoGemInventory pack, PetDevice gem, Gem hammer)
        {
            Assert.IsTrue(pack.HasInPack(gem), "the gem stays in the pack");
            Assert.IsTrue(DuoSummonGem.IsBound(gem), "with both slots");
            Assert.IsTrue(pack.HasInPack(hammer), "the hammer is not spent");
            Assert.AreEqual(0, pack.Committed.Count, "nothing restored reached the client or the database");
            Assert.AreEqual(0, pack.Destroyed.Count);
            Assert.AreEqual(2, pack.Pack.Count, "only the gem and the hammer: no restored item was left behind");
        }

        [TestMethod]
        public void Hammer_Restores_BothItems_AsAbsorbed_ThenDestroysGem_ThenOneHammer()
        {
            var (pack, gem, hammer) = Setup();

            var result = SummonGemHammer.Break(pack, hammer, gem, out _, out var restored);

            Assert.AreEqual(HammerResult.Broken, result);
            Assert.AreEqual(2, restored.Count);

            var a = (PetDevice)restored[0];
            var b = (PetDevice)restored[1];

            Assert.AreEqual("Alpha", a.Name);
            Assert.AreEqual(7, (int?)a.Structure, "charges as they were when absorbed, not the gem's pool");
            Assert.AreEqual(20, a.GearDamage, "ratings preserved");
            Assert.AreEqual(14, a.GearCrit);
            Assert.AreEqual("first", a.GetProperty(PropertyString.Inscription), "inscription preserved");
            Assert.AreEqual(2, (int?)b.Structure);
            Assert.AreEqual(5002, b.PetClass);
            Assert.IsNull(a.GetProperty(PropertyBool.PvpTemplateIssued), "no PvP issued stamp");
            Assert.AreNotEqual(a.Guid, b.Guid);

            Assert.IsFalse(pack.HasInPack(gem), "the gem is destroyed");
            Assert.IsTrue(pack.HasInPack(a) && pack.HasInPack(b));
            Assert.AreEqual(1, pack.Destroyed.Count(w => ReferenceEquals(w, hammer)), "exactly one hammer");
            Assert.AreEqual(2, pack.Count("consume:"), "the gem and one hammer, nothing else");

            // order: room checked, both added in memory, gem consumed, THEN the items are committed (sent + saved)
            var canAdd = pack.IndexOf("canadd:2");
            var addB = pack.IndexOf("add:Beta");
            var gemGone = pack.IndexOf("consume:Duo Summon Gem");
            var commitA = pack.IndexOf("commit:Alpha");
            var hammerGone = pack.IndexOf("consume:Summon Gem Hammer");

            Assert.IsTrue(canAdd >= 0 && canAdd < pack.IndexOf("add:Alpha"));
            Assert.IsTrue(addB < gemGone, "both items are in the pack before the gem goes");
            Assert.IsTrue(gemGone < commitA, "the items are saved only after the gem's delete is enqueued");
            Assert.IsTrue(commitA < hammerGone);
        }

        [TestMethod]
        public void Hammer_NoPackSpace_RefusesAndKeepsGem()
        {
            var (pack, gem, hammer) = Setup();
            pack.FreeSlots = 1;

            var result = SummonGemHammer.Break(pack, hammer, gem, out _, out var restored);

            Assert.AreEqual(HammerResult.NoPackSpace, result);
            Assert.AreEqual(0, restored.Count);
            Assert.AreEqual(0, pack.Count("add:"), "room is checked for BOTH before anything is added");
            Assert.AreEqual(0, pack.Count("consume:"));
            Assert.AreEqual(2, pack.Discarded.Count, "the two built objects are thrown away");
            AssertGemIntact(pack, gem, hammer);
        }

        [TestMethod]
        public void Hammer_FailurePartway_GemIntact()
        {
            // the second add fails after the first succeeded
            {
                var (pack, gem, hammer) = Setup();
                pack.FailAddNumber = 2;

                Assert.AreEqual(HammerResult.AddFailed, SummonGemHammer.Break(pack, hammer, gem, out _, out _));
                Assert.AreEqual(1, pack.Count("undo:"), "the first add is undone");
                AssertGemIntact(pack, gem, hammer);
            }

            // the second add THROWS
            {
                var (pack, gem, hammer) = Setup();
                pack.FailAddNumber = 2;
                pack.ThrowOnFailedAdd = true;

                var threw = false;
                try
                {
                    SummonGemHammer.Break(pack, hammer, gem, out _, out _);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }

                Assert.IsTrue(threw, "the fixture's add throws and the break rethrows after rolling back");
                AssertGemIntact(pack, gem, hammer);
            }

            // both added, then the gem will not be consumed
            {
                var (pack, gem, hammer) = Setup();
                pack.FailConsumeOf = gem;

                Assert.AreEqual(HammerResult.GemNotConsumed, SummonGemHammer.Break(pack, hammer, gem, out _, out _));
                Assert.AreEqual(2, pack.Count("undo:"), "both adds are undone");
                Assert.AreEqual(0, pack.Count("consume:Summon Gem Hammer"));
                AssertGemIntact(pack, gem, hammer);
            }

            // the second restored object will not build
            {
                var (pack, gem, hammer) = Setup();
                pack.FailBuildNumber = 2;

                Assert.AreEqual(HammerResult.BuildFailed, SummonGemHammer.Break(pack, hammer, gem, out _, out _));
                Assert.AreEqual(1, pack.Discarded.Count);
                AssertGemIntact(pack, gem, hammer);
            }
        }

        [TestMethod]
        public void Hammer_NotConsumable_StillNoDuplicate()
        {
            var (pack, gem, hammer) = Setup();
            pack.FailConsumeOf = hammer;

            Assert.AreEqual(HammerResult.BrokenHammerKept, SummonGemHammer.Break(pack, hammer, gem, out _, out var restored));
            Assert.IsFalse(pack.HasInPack(gem), "the gem is gone, so the two restored items are the only copies");
            Assert.AreEqual(2, restored.Count);
        }

        [TestMethod]
        public void Hammer_OnBlankHalfFilledOrOther_Refused_NothingConsumed()
        {
            var pack = new FakeDuoGemInventory();
            var hammer = DuoGemTestKit.Hammer();
            var blank = DuoGemTestKit.BlankGem();
            var half = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            var plain = DuoGemTestKit.Summon("Plain", 5002);
            pack.Put(hammer, blank, half, alpha, plain);

            Assert.AreEqual(DuoAbsorbResult.Absorbed, DuoSummonGem.Absorb(pack, half, alpha, out _));
            pack.Log.Clear();

            Assert.AreEqual(HammerResult.Refused, SummonGemHammer.Break(pack, hammer, blank, out var d1, out _));
            Assert.AreEqual(HammerDecision.RefusedUnbound, d1);

            Assert.AreEqual(HammerResult.Refused, SummonGemHammer.Break(pack, hammer, half, out var d2, out _));
            Assert.AreEqual(HammerDecision.RefusedUnbound, d2);
            Assert.AreEqual(1, DuoSummonGem.FilledSlots(half), "the half-filled gem keeps its summon");

            Assert.AreEqual(HammerResult.Refused, SummonGemHammer.Break(pack, hammer, plain, out var d3, out _));
            Assert.AreEqual(HammerDecision.RefusedNotADuoGem, d3);

            Assert.AreEqual(HammerResult.Refused, SummonGemHammer.Break(pack, hammer, hammer, out var d4, out _));
            Assert.AreEqual(HammerDecision.RefusedNotADuoGem, d4);

            Assert.AreEqual(0, pack.Count("consume:"));
            Assert.AreEqual(0, pack.Count("build:"));
        }

        [TestMethod]
        public void Hammer_GemNotInPack_Refused()
        {
            var (pack, gem, hammer) = Setup();
            pack.Pack.Remove(gem);

            Assert.AreEqual(HammerResult.NotInPack, SummonGemHammer.Break(pack, hammer, gem, out _, out _));
            Assert.AreEqual(0, pack.Count("build:"));
        }

        [TestMethod]
        public void RestoredItems_CanBeReabsorbed_ByANewGem()
        {
            var (pack, gem, hammer) = Setup();
            SummonGemHammer.Break(pack, hammer, gem, out _, out var restored);

            var fresh = DuoGemTestKit.BlankGem();
            pack.Put(fresh);

            Assert.AreEqual(DuoAbsorbResult.Absorbed, DuoSummonGem.Absorb(pack, fresh, restored[0], out _));
            Assert.AreEqual(DuoAbsorbResult.Bound, DuoSummonGem.Absorb(pack, fresh, restored[1], out _));
        }

        // ---- fix round 1 -----------------------------------------------------------------------------------

        [TestMethod]
        public void Hammer_StackedGem_Refused_NothingConsumed()
        {
            var (pack, gem, hammer) = Setup();
            gem.SetProperty(PropertyInt.StackSize, 2);

            Assert.AreEqual(HammerResult.Refused, SummonGemHammer.Break(pack, hammer, gem, out var d, out _));
            Assert.AreEqual(HammerDecision.RefusedStacked, d);
            Assert.AreEqual(0, pack.Count("build:"));
            Assert.AreEqual(0, pack.Count("consume:"));
        }

        [TestMethod]
        public void Hammer_GemWithPetOut_Refused_NothingConsumed()
        {
            var (pack, gem, hammer) = Setup();
            gem.Pet = 0x80001234;

            Assert.AreEqual(HammerResult.Refused, SummonGemHammer.Break(pack, hammer, gem, out var d, out _));
            Assert.AreEqual(HammerDecision.RefusedPetOut, d);
            Assert.AreEqual(0, pack.Count("consume:"));
            AssertGemIntact(pack, gem, hammer);
        }

        [TestMethod]
        public void Hammer_GemConsumeThrowsAfterRemoval_GemIsDestroyed_AndItemsKept()
        {
            var (pack, gem, hammer) = Setup();
            pack.ThrowAfterRemoving = gem;

            var threw = false;
            try
            {
                SummonGemHammer.Break(pack, hammer, gem, out _, out _);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.IsTrue(threw);
            Assert.IsFalse(pack.HasInPack(gem));
            Assert.IsTrue(pack.Discarded.Contains(gem), "a gem that left the pack but was not destroyed is destroyed before the items are committed");
            Assert.IsTrue(pack.IndexOf("discard:Duo Summon Gem") < pack.IndexOf("commit:Alpha"));
            Assert.AreEqual(2, pack.Committed.Count, "the two restored items are the only copies now, so they are kept");
            Assert.AreEqual(0, pack.Count("undo:"));
        }

        [TestMethod]
        public void EndToEnd_DaggerApply_Absorb_Hammer_KeepsDaggerRatingsAndBoundSpells()
        {
            var pack = new FakeDuoGemInventory();

            // a caster summon with two bound spells and no ratings
            var caster = DuoGemTestKit.Summon("Bonecaller", 5003, structure: 9);
            caster.SetProperty(PropertyDataId.SummonBoundSpell1, 111u);
            caster.SetProperty(PropertyDataId.SummonBoundSpell2, 222u);

            // a dagger imprinted from a rated summon, then applied to the caster (replaces all six)
            var dagger = DuoGemTestKit.Dagger();
            SacrificialDagger.Imprint(dagger, DuoGemTestKit.Summon("Donor", 5004, damage: 19, crit: 12));
            SacrificialDagger.ApplyTo(dagger, caster);
            var applied = SummonRatings.Read(caster);
            Assert.AreEqual(19, applied.Damage, "fixture: the dagger applied its ratings");

            var other = DuoGemTestKit.Summon("Other", 5005);
            var gem = DuoGemTestKit.BoundGem(pack, caster, other);
            var hammer = DuoGemTestKit.Hammer();
            pack.Put(hammer);

            Assert.AreEqual(HammerResult.Broken, SummonGemHammer.Break(pack, hammer, gem, out _, out var restored));

            var back = (PetDevice)restored[0];

            Assert.AreEqual("Bonecaller", back.Name);
            Assert.AreEqual(applied, SummonRatings.Read(back), "the dagger-applied ratings survive the gem, zeros included");
            Assert.AreEqual(111u, back.GetProperty(PropertyDataId.SummonBoundSpell1));
            Assert.AreEqual(222u, back.GetProperty(PropertyDataId.SummonBoundSpell2));
            Assert.AreEqual(9, (int?)back.Structure);
            Assert.AreNotEqual(caster.Guid, back.Guid);
        }
        // ---- wiring pins in Gem.cs -------------------------------------------------------------------------

        private const string GemFile = "Source/ACE.Server/WorldObjects/Gem.cs";

        [TestMethod]
        public void A_target_less_use_of_the_hammer_or_a_gem_typed_duo_gem_is_refused_before_any_consumption_in_UseGem()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(GemFile), "public void UseGem(Player player)");

            var consume = body.IndexOf("TryConsumeFromInventoryWithNetworking(", StringComparison.Ordinal);

            var hammer = body.IndexOf("SummonGemHammer.IsHammer(this)", StringComparison.Ordinal);
            Assert.IsTrue(hammer >= 0, "the SummonGemHammer branch is missing from UseGem");
            Assert.IsTrue(consume > hammer, "the hammer branch must precede every consume in UseGem, or a target-less use destroys the hammer");
            Assert.IsTrue(body.IndexOf("SummonGemHammer.NoTargetText", hammer, StringComparison.Ordinal) > hammer);

            var duo = body.IndexOf("DuoSummonGem.IsDuoGem(this)", StringComparison.Ordinal);
            Assert.IsTrue(duo >= 0 && consume > duo, "a Gem-typed Duo Gem must refuse before any consume");
        }

        [TestMethod]
        public void HandleActionUseOnTarget_RoutesTheHammer_BeforeTheRecipeFallThrough()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(GemFile), "public override void HandleActionUseOnTarget(Player player, WorldObject target)");

            var hammer = body.IndexOf("SummonGemHammer.IsHammer(this)", StringComparison.Ordinal);
            var recipe = body.IndexOf("base.HandleActionUseOnTarget", StringComparison.Ordinal);

            Assert.IsTrue(hammer >= 0 && recipe > hammer);
            StringAssert.Contains(body, "SummonGemHammer.UseOnTarget(player, this, target)");
        }

        [TestMethod]
        public void UseOnTarget_RunsTheBreakUnderIsBusy_AndClearsItInAFinally()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Entity/SummonGemHammer.cs"), "public static void UseOnTarget(Player player, Gem hammer, WorldObject target)");

            var busyCheck = body.IndexOf("if (player.IsBusy)", StringComparison.Ordinal);
            var busySet = body.IndexOf("player.IsBusy = true", StringComparison.Ordinal);
            var breakCall = body.IndexOf("Break(new PlayerDuoGemInventory(player)", StringComparison.Ordinal);
            var fin = body.IndexOf("finally", breakCall, StringComparison.Ordinal);
            var cleared = body.IndexOf("player.IsBusy = false", fin, StringComparison.Ordinal);

            Assert.IsTrue(busyCheck >= 0 && busySet > busyCheck && breakCall > busySet, "busy check, busy set, then the re-verifying break");
            Assert.IsTrue(fin > breakCall && cleared > fin, "IsBusy is cleared in a finally so a throw cannot lock the player");
        }
    }
}
