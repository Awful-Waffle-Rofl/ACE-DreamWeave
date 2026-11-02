using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom Summons Task 7: the Duo Summon Gem's absorb, binding and summon sources, through the real
    /// DuoSummonGem.Absorb on real PetDevice objects and a logging fake pack. The Player shell
    /// (DuoSummonGem.UseOnTarget, PetDevice.ActOnUse's Summon 2x gate) needs a Session and is an owner in-game check;
    /// the source scans at the bottom pin its wiring order.
    /// </summary>
    [TestClass]
    public class DuoGemTests
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

        // ---- refusals ---------------------------------------------------------------------------------------

        [TestMethod]
        public void SameItemTwice_Refused()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            pack.Put(gem, alpha);

            Assert.AreEqual(DuoAbsorbResult.Absorbed, DuoSummonGem.Absorb(pack, gem, alpha, out _));
            Assert.IsFalse(pack.HasInPack(alpha), "the absorbed item is destroyed");

            // the same object again (e.g. a stale reference or a replayed packet): refused by its recorded guid,
            // before any pack check, and nothing is spent or written
            pack.Put(alpha);
            pack.Log.Clear();

            Assert.AreEqual(DuoAbsorbResult.Refused, DuoSummonGem.Absorb(pack, gem, alpha, out var decision));
            Assert.AreEqual(DuoAbsorbDecision.RefusedSameItem, decision);
            Assert.AreEqual(0, pack.Count("consume:"));
            Assert.AreEqual(0, pack.Count("save:"));
            Assert.IsNull(gem.GetProperty(PropertyString.DuoSummonSlotB));
            Assert.IsFalse(DuoSummonGem.IsBound(gem));

            // and the pure rule
            Assert.AreEqual(DuoAbsorbDecision.RefusedSameItem, DuoSummonGem.Decide(1, 0x1234, DuoTarget.Summon, 0x1234));
            Assert.AreEqual(DuoAbsorbDecision.Absorb, DuoSummonGem.Decide(1, 0x1234, DuoTarget.Summon, 0x1235));
        }

        [TestMethod]
        public void TwoOfTheSameType_BothQualify()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var first = DuoGemTestKit.Summon("Twin", 5001);
            var second = DuoGemTestKit.Summon("Twin", 5001);
            pack.Put(gem, first, second);

            Assert.AreEqual(DuoAbsorbResult.Absorbed, DuoSummonGem.Absorb(pack, gem, first, out _));
            Assert.AreEqual(DuoAbsorbResult.Bound, DuoSummonGem.Absorb(pack, gem, second, out _));
        }

        [TestMethod]
        public void Refusals_ConsumeNothing_AndLeaveTheGemAsItWas()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();

            var blankCaster = DuoGemTestKit.Summon("Blank Caster", 5001);
            blankCaster.SetProperty(PropertyBool.SummonSpellBinder, true);

            var otherGem = DuoGemTestKit.BlankGem();
            var hammer = DuoGemTestKit.Hammer();
            var noPet = DuoGemTestKit.Summon("Broken", 5001);
            noPet.PetClass = null;

            pack.Put(gem, blankCaster, otherGem, hammer, noPet);

            void Refused(WorldObject target, DuoAbsorbDecision expected)
            {
                Assert.AreEqual(DuoAbsorbResult.Refused, DuoSummonGem.Absorb(pack, gem, target, out var d), target?.Name);
                Assert.AreEqual(expected, d, target?.Name);
            }

            Refused(blankCaster, DuoAbsorbDecision.RefusedBlankCaster);
            Refused(otherGem, DuoAbsorbDecision.RefusedDuoGem);
            Refused(gem, DuoAbsorbDecision.RefusedDuoGem);
            Refused(hammer, DuoAbsorbDecision.RefusedNotASummon);
            Refused(noPet, DuoAbsorbDecision.RefusedNotASummon);
            Refused(null, DuoAbsorbDecision.RefusedNotASummon);

            Assert.AreEqual(0, pack.Count("consume:"));
            Assert.AreEqual(0, pack.Count("save:"));
            Assert.AreEqual(0, DuoSummonGem.FilledSlots(gem));
        }

        [TestMethod]
        public void BoundGem_RefusesAThird()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BoundGem(pack, DuoGemTestKit.Summon("Alpha", 5001), DuoGemTestKit.Summon("Beta", 5002));
            var third = DuoGemTestKit.Summon("Gamma", 5003);
            pack.Put(third);

            Assert.AreEqual(DuoAbsorbResult.Refused, DuoSummonGem.Absorb(pack, gem, third, out var d));
            Assert.AreEqual(DuoAbsorbDecision.RefusedGemFull, d);
            Assert.IsTrue(pack.HasInPack(third));
        }

        [TestMethod]
        public void TargetNotInPack_Refused_NothingRecorded()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            pack.Put(gem);   // alpha is NOT in the pack (on the ground, in a trade, on another player)

            Assert.AreEqual(DuoAbsorbResult.NotInPack, DuoSummonGem.Absorb(pack, gem, alpha, out _));
            Assert.AreEqual(0, DuoSummonGem.FilledSlots(gem));
        }

        // ---- ordering ---------------------------------------------------------------------------------------

        [TestMethod]
        public void Absorb_ConsumesExactlyOne_ThenSavesTheGem()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            pack.Put(gem, alpha);

            DuoSummonGem.Absorb(pack, gem, alpha, out _);

            Assert.AreEqual(1, pack.Count("consume:"));
            Assert.AreEqual(1, pack.Count("save:Duo Summon Gem"));
            Assert.IsTrue(pack.IndexOf("consume:Alpha") < pack.IndexOf("save:Duo Summon Gem"),
                "the item's delete is enqueued before the gem's save, so a crash can lose it but never keep it in both places");
        }

        [TestMethod]
        public void Absorb_ConsumeFails_SlotAndRestrictionsUndone_NothingSaved()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            alpha.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            pack.Put(gem, alpha);
            pack.FailConsumeOf = alpha;

            Assert.AreEqual(DuoAbsorbResult.ConsumeFailed, DuoSummonGem.Absorb(pack, gem, alpha, out _));

            Assert.IsNull(gem.GetProperty(PropertyString.DuoSummonSlotA), "no record of an item that still exists");
            Assert.IsNull(gem.GetProperty(PropertyInt.Attuned));
            Assert.AreEqual(0, pack.Count("save:"));
            Assert.IsTrue(pack.HasInPack(alpha));
        }

        [TestMethod]
        public void Absorb_AttunedOrBondedItem_GemTakesTheStricterStatus()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            alpha.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            var beta = DuoGemTestKit.Summon("Beta", 5002);
            beta.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            pack.Put(gem, alpha, beta);

            DuoSummonGem.Absorb(pack, gem, alpha, out _);
            Assert.AreEqual((int)AttunedStatus.Attuned, gem.GetProperty(PropertyInt.Attuned), "a half-filled gem already must not trade an attuned summon away");

            DuoSummonGem.Absorb(pack, gem, beta, out _);
            Assert.AreEqual((int)AttunedStatus.Attuned, gem.GetProperty(PropertyInt.Attuned));
            Assert.AreEqual((int)BondedStatus.Bonded, gem.GetProperty(PropertyInt.Bonded));
        }

        // ---- binding ----------------------------------------------------------------------------------------

        [TestMethod]
        public void GemUnusableUntilSecondAbsorb()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            var beta = DuoGemTestKit.Summon("Beta", 5002);
            pack.Put(gem, alpha, beta);

            void Unusable(string when)
            {
                Assert.IsFalse(DuoSummonGem.IsBound(gem), when);
                Assert.AreEqual(0, gem.GetSummonSources().Count, $"{when}: no sources, so ActOnUse summons nothing");
                Assert.IsNull(gem.PetClass, when);
                Assert.IsNull(gem.Structure, when);
                Assert.IsNull(gem.CooldownId, $"{when}: no cooldown to start, so VTank does not see a summon either");
                Assert.AreEqual(Usable.SourceContainedTargetContained, gem.ItemUseable, when);
            }

            Unusable("blank");

            DuoSummonGem.Absorb(pack, gem, alpha, out _);
            Assert.AreEqual(1, DuoSummonGem.FilledSlots(gem));
            Unusable("after one absorb");

            Assert.AreEqual(DuoAbsorbResult.Bound, DuoSummonGem.Absorb(pack, gem, beta, out _));
            Assert.IsTrue(DuoSummonGem.IsBound(gem));
            Assert.AreEqual(2, gem.GetSummonSources().Count);
        }

        [TestMethod]
        public void SecondAbsorb_RefillsCharges_HighestRequirement_LastElement()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();

            // A: spent, lower requirement but specialized, Frost, carries a mastery; B: higher requirement, unspecialized, Fire
            var alpha = DuoGemTestKit.Summon("Alpha", 5001, structure: 4, reqLevel: 570, reqSpec: true, element: UiEffects.Frost, mastery: 1);
            var beta = DuoGemTestKit.Summon("Beta", 5002, structure: 1, reqLevel: 600, reqSpec: false, element: UiEffects.Fire);
            alpha.SetProperty(PropertyInt.UseRequiresLevel, 200);
            beta.SetProperty(PropertyInt.UseRequiresLevel, 180);
            pack.Put(gem, alpha, beta);

            DuoSummonGem.Absorb(pack, gem, alpha, out _);
            DuoSummonGem.Absorb(pack, gem, beta, out _);

            Assert.AreEqual(5001, gem.PetClass, "PetClass is slot A's pet");
            Assert.AreEqual(DuoSummonGem.Charges, (int?)gem.Structure, "charges refilled to full");
            Assert.AreEqual(DuoSummonGem.Charges, (int?)gem.MaxStructure);

            Assert.AreEqual(600, gem.GetProperty(PropertyInt.UseRequiresSkillLevel), "the higher skill requirement");
            Assert.AreEqual((int)Skill.Summoning, gem.GetProperty(PropertyInt.UseRequiresSkill));
            Assert.AreEqual((int)Skill.Summoning, gem.GetProperty(PropertyInt.UseRequiresSkillSpec), "specialization required because either required it");
            Assert.AreEqual(200, gem.GetProperty(PropertyInt.UseRequiresLevel), "the higher character level");

            Assert.AreEqual((int)UiEffects.Fire, gem.GetProperty(PropertyInt.UiEffects), "the LAST absorbed item's element, and only it");

            Assert.AreEqual(DuoSummonGem.SummonSharedCooldown, gem.CooldownId, "VTank detects a summon by SharedCooldown 213");
            Assert.AreEqual(45.0, gem.CooldownDuration);
            Assert.IsNull(gem.GetProperty(PropertyInt.SummoningMastery), "no summoning mastery on the gem");
            Assert.AreEqual(Usable.Contained, gem.ItemUseable, "a plain double-click reaches ActOnUse");
        }

        [TestMethod]
        public void WithElementOf_ReplacesOnlyTheFourElementBits()
        {
            var gemOwn = (int)(UiEffects.Magical | UiEffects.Frost);

            Assert.AreEqual(UiEffects.Magical | UiEffects.Acid, DuoSummonGem.WithElementOf(gemOwn, (int)(UiEffects.Acid | UiEffects.Poisoned)));
            Assert.AreEqual(UiEffects.Magical, DuoSummonGem.WithElementOf(gemOwn, (int)UiEffects.Slashing), "a non-VTank element clears the old one");
            Assert.AreEqual(UiEffects.Undef, DuoSummonGem.WithElementOf(null, null));
        }

        [TestMethod]
        public void Sources_AreSlotAThenSlotB_WithTheirOwnRatingsAndSpells()
        {
            var pack = new FakeDuoGemInventory();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001, damage: 20, crit: 14);
            var beta = DuoGemTestKit.Summon("Beta", 5002, damage: 3);
            beta.SetProperty(PropertyDataId.SummonBoundSpell1, 111u);
            beta.SetProperty(PropertyDataId.SummonBoundSpell2, 222u);

            var expectedA = PetSummonSource.FromDevice(alpha);
            var expectedB = PetSummonSource.FromDevice(beta);

            var gem = DuoGemTestKit.BoundGem(pack, alpha, beta);

            var sources = gem.GetSummonSources();

            // without Summon 2x ActOnUse summons sources[0] only, and that is slot A
            Assert.AreEqual(expectedA, sources[0]);
            Assert.AreEqual(expectedB, sources[1], "the Summon 2x second pet is slot B's, not a copy of A");
            Assert.AreEqual(5002u, sources[1].PetWcid);
            Assert.IsTrue(sources[1].HasBoundSpells);
        }

        [TestMethod]
        public void UnreadableSlot_GivesNoSources_AndRefusesTheSecondAbsorb()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var beta = DuoGemTestKit.Summon("Beta", 5002);
            pack.Put(gem, beta);

            gem.SetProperty(PropertyString.DuoSummonSlotA, "{\"v\":99}");

            Assert.AreEqual(DuoAbsorbResult.GemUnreadable, DuoSummonGem.Absorb(pack, gem, beta, out _));
            Assert.IsTrue(pack.HasInPack(beta), "refused before the item was spent");
            Assert.AreEqual(0, pack.Count("consume:"));

            gem.SetProperty(PropertyString.DuoSummonSlotB, "{\"v\":99}");
            Assert.AreEqual(0, gem.GetSummonSources().Count);
        }

        [TestMethod]
        public void Description_ShowsBothAbsorbedSummons()
        {
            var oldWeenie = SummonItemDescription.PetWeenieLookup;
            try
            {
                SummonItemDescription.PetWeenieLookup = _ => null;

                var pack = new FakeDuoGemInventory();
                var gem = DuoGemTestKit.BoundGem(pack,
                    DuoGemTestKit.Summon("Alpha Essence", 5001, damage: 20, element: UiEffects.Frost),
                    DuoGemTestKit.Summon("Beta Essence", 5002, damage: 7, element: UiEffects.Acid));

                var text = SummonItemDescription.Compose(gem);

                StringAssert.Contains(text, "Summon 1: Alpha Essence");
                StringAssert.Contains(text, "Summon 2: Beta Essence");
                StringAssert.Contains(text, "Damage 20");
                StringAssert.Contains(text, "Damage 7");
                StringAssert.Contains(text, "Element: Acid");
            }
            finally
            {
                SummonItemDescription.PetWeenieLookup = oldWeenie;
            }
        }

        // ---- fix round 1: passive pets, stacks, pets out, bonded ordering ----------------------------------

        [TestMethod]
        public void PassivePetDevice_Refused_NothingConsumed()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var passive = DuoGemTestKit.Summon("Passive Pet Essence", DuoGemTestKit.PassivePetClass);
            pack.Put(gem, passive);

            Assert.AreEqual(DuoTarget.NotASummon, DuoSummonGem.KindOf(gem, passive), "only a CombatPet essence qualifies");
            Assert.AreEqual(DuoAbsorbResult.Refused, DuoSummonGem.Absorb(pack, gem, passive, out var d));
            Assert.AreEqual(DuoAbsorbDecision.RefusedNotASummon, d);
            Assert.IsTrue(pack.HasInPack(passive));
            Assert.AreEqual(0, pack.Count("consume:"));
            Assert.AreEqual(0, DuoSummonGem.FilledSlots(gem));
        }

        [TestMethod]
        public void StackedGem_Refused_NothingConsumed()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            gem.SetProperty(PropertyInt.StackSize, 2);
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            pack.Put(gem, alpha);

            Assert.AreEqual(DuoAbsorbResult.Refused, DuoSummonGem.Absorb(pack, gem, alpha, out var d));
            Assert.AreEqual(DuoAbsorbDecision.RefusedStacked, d, "two blank gems in one stack would both be bound by one absorb");
            Assert.IsTrue(pack.HasInPack(alpha));
            Assert.AreEqual(0, pack.Count("consume:"));
            Assert.AreEqual(0, DuoSummonGem.FilledSlots(gem));
        }

        [TestMethod]
        public void StackedTarget_Refused_NothingConsumed()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var stack = DuoGemTestKit.Summon("Stacked Essence", 5001);
            stack.SetProperty(PropertyInt.StackSize, 3);
            pack.Put(gem, stack);

            Assert.AreEqual(DuoAbsorbResult.Refused, DuoSummonGem.Absorb(pack, gem, stack, out var d));
            Assert.AreEqual(DuoAbsorbDecision.RefusedStacked, d, "consuming one of a stack would leave a live copy of the recorded item");
            Assert.AreEqual(3, stack.StackSize);
            Assert.AreEqual(0, pack.Count("consume:"));
        }

        [TestMethod]
        public void FakeConsume_DecrementsAStack_LikeTheLiveOne()
        {
            var pack = new FakeDuoGemInventory();
            var stack = DuoGemTestKit.Summon("Stacked Essence", 5001);
            stack.SetProperty(PropertyInt.StackSize, 3);
            pack.Put(stack);

            Assert.IsTrue(pack.TryConsumeOne(stack));
            Assert.AreEqual(2, stack.StackSize);
            Assert.IsTrue(pack.HasInPack(stack), "fixture: a stack of 3 loses one and stays");
        }

        [TestMethod]
        public void TargetWithPetOut_Refused_NothingConsumed()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            alpha.Pet = 0x80001234;
            pack.Put(gem, alpha);

            Assert.AreEqual(DuoAbsorbResult.Refused, DuoSummonGem.Absorb(pack, gem, alpha, out var d));
            Assert.AreEqual(DuoAbsorbDecision.RefusedPetOut, d);
            Assert.IsTrue(pack.HasInPack(alpha));
            Assert.AreEqual(0, pack.Count("consume:"));
        }

        [TestMethod]
        public void StricterBonded_RanksDestroyAsStrictest_AndSlipperyAboveNormal()
        {
            int N = (int)BondedStatus.Normal, S = (int)BondedStatus.Slippery, B = (int)BondedStatus.Bonded,
                K = (int)BondedStatus.Sticky, D = (int)BondedStatus.Destroy;

            Assert.AreEqual(D, DuoSummonGem.StricterBonded(K, D), "Destroy beats Sticky despite its value -2");
            Assert.AreEqual(D, DuoSummonGem.StricterBonded(D, B));
            Assert.AreEqual(K, DuoSummonGem.StricterBonded(B, K));
            Assert.AreEqual(B, DuoSummonGem.StricterBonded(S, B));
            Assert.AreEqual(S, DuoSummonGem.StricterBonded(N, S), "Slippery (-1) is kept over Normal");
            Assert.AreEqual(S, DuoSummonGem.StricterBonded(null, S));
            Assert.AreEqual(B, DuoSummonGem.StricterBonded(B, null));
            Assert.IsNull(DuoSummonGem.StricterBonded(null, null));

            Assert.AreEqual((int)AttunedStatus.Sticky, DuoSummonGem.StricterAttuned((int)AttunedStatus.Attuned, (int)AttunedStatus.Sticky));
        }

        [TestMethod]
        public void Absorb_DestroyBondedItem_GemTakesDestroy_OverABondedOne()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BlankGem();
            var alpha = DuoGemTestKit.Summon("Alpha", 5001);
            alpha.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            var beta = DuoGemTestKit.Summon("Beta", 5002);
            beta.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Destroy);
            pack.Put(gem, alpha, beta);

            DuoSummonGem.Absorb(pack, gem, alpha, out _);
            Assert.AreEqual((int)BondedStatus.Bonded, gem.GetProperty(PropertyInt.Bonded));

            DuoSummonGem.Absorb(pack, gem, beta, out _);
            Assert.AreEqual((int)BondedStatus.Destroy, gem.GetProperty(PropertyInt.Bonded), "the old Raise ignored negative values and kept Bonded");
        }

        [TestMethod]
        public void BoundButUnreadable_GivesFewerThanTwoSources_SoCheckUseRequirementsRefuses()
        {
            var pack = new FakeDuoGemInventory();
            var gem = DuoGemTestKit.BoundGem(pack, DuoGemTestKit.Summon("Alpha", 5001), DuoGemTestKit.Summon("Beta", 5002));
            Assert.AreEqual(2, DuoSummonGem.GetSources(gem).Count);

            gem.SetProperty(PropertyString.DuoSummonSlotB, "{\"v\":99}");

            Assert.IsTrue(DuoSummonGem.IsBound(gem), "still counts as bound by its slots...");
            Assert.AreEqual(0, DuoSummonGem.GetSources(gem).Count, "...but describes no pets, which is what the gate reads");
        }
        // ---- wiring pins (source scans; the shells need a Session) ------------------------------------------

        private const string PetDeviceFile = "Source/ACE.Server/WorldObjects/PetDevice.cs";

        [TestMethod]
        public void ActOnUse_RefusesAnUnboundGem_BeforeAnySummonOrCharge()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PetDeviceFile), "public override void ActOnUse(WorldObject activator)");

            var gate = body.IndexOf("DuoSummonGem.IsDuoGem(this) && !DuoSummonGem.IsBound(this)", StringComparison.Ordinal);
            var summon = body.IndexOf("SummonCreature(", StringComparison.Ordinal);
            var charge = body.IndexOf("Structure--", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "the unbound Duo Gem gate is missing from ActOnUse");
            Assert.IsTrue(summon > gate && charge > gate, "the gate must precede every summon and charge");
        }

        [TestMethod]
        public void CheckUseRequirements_RefusesAnUnboundGem_BeforeThePetClassCast()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PetDeviceFile), "public override ActivationResult CheckUseRequirements(WorldObject activator)");

            var gate = body.IndexOf("DuoSummonGem.IsDuoGem(this) && DuoSummonGem.GetSources(this).Count < 2", StringComparison.Ordinal);
            var cast = body.IndexOf("(uint)PetClass", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "the unbound Duo Gem gate is missing from CheckUseRequirements");
            Assert.IsTrue(cast > gate, "a blank gem has no PetClass; the gate must come before the retail-stow cast");
        }

        [TestMethod]
        public void UseOnTarget_RoutesTheGem_BeforeTheBinderAndRecipeFallThrough()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PetDeviceFile), "public override void HandleActionUseOnTarget(Player player, WorldObject target)");

            var duo = body.IndexOf("DuoSummonGem.IsDuoGem(this)", StringComparison.Ordinal);
            var recipe = body.IndexOf("base.HandleActionUseOnTarget", StringComparison.Ordinal);

            Assert.IsTrue(duo >= 0 && recipe > duo, "a Duo Gem must never fall through to the recipe manager");
            Assert.IsTrue(body.IndexOf("SendUseDoneEvent", duo, StringComparison.Ordinal) > duo);
        }

        [TestMethod]
        public void GetSummonSources_ReadsTheGemSlots()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PetDeviceFile), "public virtual IReadOnlyList<PetSummonSource> GetSummonSources()");

            StringAssert.Contains(body, "DuoSummonGem.GetSources(this)");
        }
    }
}
