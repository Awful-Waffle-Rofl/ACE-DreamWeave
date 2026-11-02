using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// /pvptemplate kit, the pure half (PvpTemplateKitEdit): the argument split, the op parser and the planner. Each refusal test
    /// pairs a valid op with the bad one, so "no writes" proves the WHOLE plan is blocked, not just the bad op.
    /// </summary>
    [TestClass]
    public class PvpTemplateKitEditTests
    {
        private const uint Char = 0x50000001;
        private const uint SidePack = 0x80000100;

        internal static readonly string OwnerBowLine = "Pvp Bow | ~1006750 ~1006751 ~1006752 ~1006753 ~1006754 ~1006755 ~1006756 ~1006757 ~1006758 ~1006765 ~1006764 24207=1006769 1004133=1006775x100 1004135=1006776x100 1004141=1006777 24199=1007101 21961=1006770 43951=1007127x1000 21348=1006773x1000 +1007119 +1007120 +1007121 +1007122";
        internal static readonly string OwnerHeavyLine = "Pvp Heavy Weapon | ~1006750 ~1006751 ~1006752 ~1006753 ~1006754 ~1006755 ~1006756 ~1006757 ~1006758 ~1006765 23358=1006764 24207=1006769 1004133=1006775x100 1004135=1006776x100 1004141=1006777 ~1006766 ~1006763 +1007111 +1007112 +1007113 +1007114";
        internal static readonly string OwnerSuitLine = "Pvp Bow | 1006750=1007204 1006751=1007207 1006752=1007208 1006753=1007201 1006754=1007202 1006755=1007209 1006756=1007210 1006757=1007211 1006758=1007203";

        internal static PvpKitWeenieInfo W(uint wcid, int? maxStack = null, EquipMask valid = EquipMask.None, bool container = false)
            => new PvpKitWeenieInfo { Wcid = wcid, Name = $"Item{wcid}", MaxStackSize = maxStack, ValidLocations = (int)valid, IsContainer = container };

        internal static PvpKitPossession Pack(uint guid, uint wcid, int stack = 1, uint container = Char, int placement = 0)
            => new PvpKitPossession { Guid = guid, Wcid = wcid, Name = $"Old{wcid}", StackSize = stack, ContainerId = container, OwnerId = container, PlacementPosition = placement };

        internal static PvpKitPossession Worn(uint guid, uint wcid, EquipMask at)
            => new PvpKitPossession { Guid = guid, Wcid = wcid, Name = $"Old{wcid}", WielderId = Char, WieldLocation = (int)at };

        private static List<PvpKitOp> Ops(string text)
        {
            Assert.IsTrue(PvpTemplateKitEdit.TryParseOps(text, out var ops, out var problems), string.Join("; ", problems));
            return ops;
        }

        private static PvpKitPlan Plan(string ops, IReadOnlyList<PvpKitPossession> possessions, IEnumerable<PvpKitWeenieInfo> weenies, int capacity = 102)
        {
            var map = weenies.ToDictionary(w => w.Wcid);
            return PvpTemplateKitEdit.Plan(Ops(ops), Char, capacity, possessions, wcid => map.TryGetValue(wcid, out var w) ? w : null);
        }

        // ---- split ----

        [TestMethod]
        public void Split_NameWithSpaces_AndPreview()
        {
            Assert.IsTrue(PvpTemplateKitEdit.TrySplitArgs("Pvp Heavy Weapon | ~1", out var preview, out var name, out var ops));
            Assert.IsFalse(preview);
            Assert.AreEqual("Pvp Heavy Weapon", name);
            Assert.AreEqual("~1", ops);

            Assert.IsTrue(PvpTemplateKitEdit.TrySplitArgs("preview Pvp Bow | -5", out preview, out name, out ops));
            Assert.IsTrue(preview);
            Assert.AreEqual("Pvp Bow", name);
            Assert.AreEqual("-5", ops);
        }

        [TestMethod]
        public void Split_RefusesMissingBarNameOrOps()
        {
            Assert.IsFalse(PvpTemplateKitEdit.TrySplitArgs("Pvp Bow ~1", out _, out _, out _));
            Assert.IsFalse(PvpTemplateKitEdit.TrySplitArgs(" | ~1", out _, out _, out _));
            Assert.IsFalse(PvpTemplateKitEdit.TrySplitArgs("preview | ~1", out _, out _, out _));
            Assert.IsFalse(PvpTemplateKitEdit.TrySplitArgs("Pvp Bow | ", out _, out _, out _));
        }

        // ---- parse ----

        [TestMethod]
        public void Parse_AllFourKinds_WithCounts()
        {
            var ops = Ops("10=20x5 ~30 +40X7 +41 -50 11=21");

            Assert.AreEqual(6, ops.Count);
            Assert.AreEqual(new PvpKitOp(PvpKitOpKind.Replace, 10, 20, 5, "10=20x5"), ops[0]);
            Assert.AreEqual(new PvpKitOp(PvpKitOpKind.Recreate, 30, 30, null, "~30"), ops[1]);
            Assert.AreEqual(new PvpKitOp(PvpKitOpKind.Add, 0, 40, 7, "+40X7"), ops[2]);
            Assert.AreEqual(new PvpKitOp(PvpKitOpKind.Add, 0, 41, null, "+41"), ops[3]);
            Assert.AreEqual(new PvpKitOp(PvpKitOpKind.Delete, 50, 0, null, "-50"), ops[4]);
            Assert.AreEqual(new PvpKitOp(PvpKitOpKind.Replace, 11, 21, null, "11=21"), ops[5]);
        }

        [TestMethod]
        public void Parse_EveryBadTokenIsReported()
        {
            Assert.IsFalse(PvpTemplateKitEdit.TryParseOps("~1 abc 5=5 +7x0 =3 ~ -0 9=x2 +8x2000000 1=2x", out var ops, out var problems));

            Assert.AreEqual(9, problems.Count, string.Join(" / ", problems));
            Assert.IsTrue(problems.Any(p => p.Contains("'5=5'") && p.Contains("~5")), "A=A points at ~A");
            Assert.IsTrue(problems.Any(p => p.Contains("'+7x0'") && p.Contains("count")));
            Assert.AreEqual(1, ops.Count, "the good op still parses, but the parse fails as a whole");
        }

        [TestMethod]
        public void Parse_OwnerLines()
        {
            foreach (var (line, replaces, recreates, adds) in new[] { (OwnerBowLine, 8, 11, 4), (OwnerHeavyLine, 5, 12, 4), (OwnerSuitLine, 9, 0, 0) })
            {
                Assert.IsTrue(PvpTemplateKitEdit.TrySplitArgs(line, out var preview, out var name, out var text), line);
                Assert.IsFalse(preview);
                Assert.IsTrue(name == "Pvp Bow" || name == "Pvp Heavy Weapon", name);

                var ops = Ops(text);
                Assert.AreEqual(replaces, ops.Count(o => o.Kind == PvpKitOpKind.Replace), line);
                Assert.AreEqual(recreates, ops.Count(o => o.Kind == PvpKitOpKind.Recreate), line);
                Assert.AreEqual(adds, ops.Count(o => o.Kind == PvpKitOpKind.Add), line);
            }

            var bow = Ops(OwnerBowLine.Split('|')[1]);
            Assert.AreEqual(new PvpKitOp(PvpKitOpKind.Replace, 43951, 1007127, 1000, "43951=1007127x1000"), bow.Single(o => o.SourceWcid == 43951));
        }

        // ---- replace ----

        [TestMethod]
        public void Replace_InPack_SameContainerAndPlacement_StackFromSource()
        {
            var plan = Plan("10=20", new[] { Pack(1, 10, stack: 40, container: SidePack, placement: 3) }, new[] { W(20, maxStack: 100) });

            Assert.IsTrue(plan.Ok, string.Join("; ", plan.Problems));
            CollectionAssert.AreEqual(new uint[] { 1 }, plan.Removals);
            var c = plan.Creations.Single();
            Assert.AreEqual(20u, c.Wcid);
            Assert.AreEqual(40, c.StackSize, "no xN: the source's stack size");
            Assert.AreEqual(SidePack, c.ContainerId);
            Assert.AreEqual(3, c.PlacementPosition);
            Assert.IsNull(c.WieldLocation);
            Assert.AreEqual(1u, c.ReplacesGuid);
        }

        [TestMethod]
        public void Replace_Count_OverridesSourceStack_AndClampsToMax()
        {
            var plan = Plan("10=20x1000 11=21x5", new[] { Pack(1, 10, stack: 7), Pack(2, 11, stack: 7) }, new[] { W(20, maxStack: 250), W(21, maxStack: 100) });

            Assert.IsTrue(plan.Ok);
            Assert.AreEqual(250, plan.Creations.Single(c => c.Wcid == 20).StackSize, "clamped to MaxStackSize");
            Assert.AreEqual(5, plan.Creations.Single(c => c.Wcid == 21).StackSize, "xN wins over the source's 7");
            StringAssert.Contains(plan.Lines[0], "clamped to 250");
            Assert.IsFalse(plan.Lines[1].Contains("clamped"));
        }

        [TestMethod]
        public void Replace_StackOntoNonStackable_ClampsToOne()
        {
            var plan = Plan("10=20", new[] { Pack(1, 10, stack: 12) }, new[] { W(20) });

            Assert.AreEqual(1, plan.Creations.Single().StackSize);
            StringAssert.Contains(plan.Lines[0], "clamped to 1");
        }

        [TestMethod]
        public void Replace_Worn_KeepsSlot()
        {
            var plan = Plan("10=20", new[] { Worn(1, 10, EquipMask.Held) }, new[] { W(20, valid: EquipMask.Held) });

            Assert.IsTrue(plan.Ok);
            var c = plan.Creations.Single();
            Assert.AreEqual((int)EquipMask.Held, c.WieldLocation);
            Assert.AreEqual(Char, c.WielderId);
            Assert.IsNull(c.ContainerId);
            Assert.IsNull(c.PlacementPosition);
        }

        [TestMethod]
        public void Replace_WornSlotTheTargetCannotTake_BlocksEveryWrite()
        {
            var plan = Plan("11=21 10=20", new[] { Pack(2, 11), Worn(1, 10, EquipMask.MeleeWeapon) }, new[] { W(21), W(20, valid: EquipMask.TwoHanded) });

            Assert.IsFalse(plan.Ok);
            Assert.AreEqual(1, plan.Problems.Count);
            StringAssert.Contains(plan.Problems[0], "MeleeWeapon");
            Assert.AreEqual(0, plan.Creations.Count, "the valid 11=21 is blocked too");
            Assert.AreEqual(0, plan.Removals.Count);
        }

        // ---- recreate ----

        [TestMethod]
        public void Recreate_EveryCopyInPlace_SameStack()
        {
            var plan = Plan("~10", new[] { Worn(1, 10, EquipMask.ChestArmor), Pack(2, 10, stack: 1, placement: 4) },
                new[] { W(10, valid: EquipMask.ChestArmor | EquipMask.UpperArmArmor) });

            Assert.IsTrue(plan.Ok);
            CollectionAssert.AreEquivalent(new uint[] { 1, 2 }, plan.Removals);
            Assert.AreEqual(2, plan.Creations.Count);
            Assert.IsTrue(plan.Creations.All(c => c.Wcid == 10));
            Assert.AreEqual((int)EquipMask.ChestArmor, plan.Creations.Single(c => c.ReplacesGuid == 1).WieldLocation);
            Assert.AreEqual(4, plan.Creations.Single(c => c.ReplacesGuid == 2).PlacementPosition);
        }

        // ---- add ----

        [TestMethod]
        public void Add_DefaultOne_AndSplitOverMaxStack_IntoMainPackAfterLastPlacement()
        {
            var plan = Plan("+20x250 +21 +22x3", new[] { Pack(1, 99, placement: 6) }, new[] { W(20, maxStack: 100), W(21, maxStack: 100), W(22) });

            Assert.IsTrue(plan.Ok);
            CollectionAssert.AreEqual(new[] { 100, 100, 50 }, plan.Creations.Where(c => c.Wcid == 20).Select(c => c.StackSize).ToList());
            Assert.AreEqual(1, plan.Creations.Single(c => c.Wcid == 21).StackSize);
            Assert.AreEqual(3, plan.Creations.Count(c => c.Wcid == 22), "non-stackable x3 = three objects");
            Assert.IsTrue(plan.Creations.All(c => c.ContainerId == Char && c.ReplacesGuid == null));
            CollectionAssert.AreEqual(Enumerable.Range(7, 7).ToList(), plan.Creations.Select(c => c.PlacementPosition.Value).ToList());
            Assert.AreEqual(0, plan.Removals.Count);
        }

        [TestMethod]
        public void Add_OverPackCapacity_BlocksEveryWrite()
        {
            var possessions = Enumerable.Range(1, 100).Select(i => Pack((uint)i, 99, placement: i)).ToList();

            var refused = Plan("-99 +20x103", possessions, new[] { W(20) });
            Assert.IsFalse(refused.Ok, "100 freed, 103 added: over 102");
            Assert.AreEqual(0, refused.Creations.Count + refused.Removals.Count);

            var fits = Plan("+20x2", possessions, new[] { W(20) });
            Assert.IsTrue(fits.Ok, "100 + 2 = 102 fits exactly");

            var over = Plan("+20x3", possessions, new[] { W(20) });
            Assert.IsFalse(over.Ok);
            StringAssert.Contains(over.Problems.Single(), "102 slots");
        }

        [TestMethod]
        public void Add_MoreStacksThanPackSlots_IsRefusedBeforeExpanding()
        {
            var plan = Plan("+20x1000000", new PvpKitPossession[0], new[] { W(20) });

            Assert.IsFalse(plan.Ok);
            Assert.AreEqual(0, plan.Creations.Count + plan.Removals.Count);
            // The early refusal names the stack count; the after-plan capacity check would instead say "would hold 1000000 items".
            StringAssert.Contains(plan.Problems.Single(), "needs 1,000,000 pack slots");
            StringAssert.Contains(plan.Problems.Single(), "102 slots");
            StringAssert.Contains(plan.Lines.Single(), "REFUSED");

            var stackable = Plan("+21x1000000", new PvpKitPossession[0], new[] { W(21, maxStack: 10000) });
            Assert.IsTrue(stackable.Ok, "100 stacks of 10,000 fit 102 slots: " + string.Join("; ", stackable.Problems));
            Assert.AreEqual(100, stackable.Creations.Count);
        }

        [TestMethod]
        public void Add_PackSlotTarget_IsNotRefusedByTheStackCountGuard()
        {
            // A RequiresPackSlot target takes no main-pack item slot, so the after-plan capacity check skips it; the early guard must too.
            var focus = W(30, maxStack: 1);
            focus.RequiresPackSlot = true;

            var plan = Plan("+30x200", new PvpKitPossession[0], new[] { focus });

            Assert.IsTrue(plan.Ok, string.Join("; ", plan.Problems));
            Assert.AreEqual(200, plan.Creations.Count);
        }

        // ---- delete ----

        [TestMethod]
        public void Delete_RemovesEveryCopy()
        {
            var plan = Plan("-10", new[] { Pack(1, 10), Pack(2, 10, container: SidePack), Pack(3, 11) }, new PvpKitWeenieInfo[0]);

            Assert.IsTrue(plan.Ok);
            CollectionAssert.AreEquivalent(new uint[] { 1, 2 }, plan.Removals);
            Assert.AreEqual(0, plan.Creations.Count);
        }

        // ---- refusals ----

        [TestMethod]
        public void MissingSource_BlocksEveryWrite()
        {
            foreach (var bad in new[] { "10=20", "~10", "-10" })
            {
                var plan = Plan("+21 " + bad, new[] { Pack(1, 11) }, new[] { W(20), W(21), W(10) });

                Assert.IsFalse(plan.Ok, bad);
                StringAssert.Contains(plan.Problems.Single(), "carries no wcid 10");
                Assert.AreEqual(0, plan.Creations.Count + plan.Removals.Count, bad);
            }
        }

        [TestMethod]
        public void UnknownWeenie_BlocksEveryWrite()
        {
            var plan = Plan("-11 +404", new[] { Pack(1, 11) }, new PvpKitWeenieInfo[0]);

            Assert.IsFalse(plan.Ok);
            StringAssert.Contains(plan.Problems.Single(), "no weenie 404");
            Assert.AreEqual(0, plan.Removals.Count);
        }

        [TestMethod]
        public void SameSourceInTwoOps_IsRefused()
        {
            var plan = Plan("10=20 -10", new[] { Pack(1, 10) }, new[] { W(20) });

            Assert.IsFalse(plan.Ok);
            Assert.IsTrue(plan.Problems.Any(p => p.Contains("more than one op")));
            Assert.AreEqual(0, plan.Creations.Count + plan.Removals.Count);
        }

        [TestMethod]
        public void PackAsSourceOrTarget_IsRefused()
        {
            var side = Pack(SidePack, 50);
            side.IsContainer = true;

            Assert.IsFalse(Plan("-50", new[] { side }, new PvpKitWeenieInfo[0]).Ok, "deleting a pack would orphan its contents");
            Assert.IsFalse(Plan("+51", new PvpKitPossession[0], new[] { W(51, container: true) }).Ok);
        }

        [TestMethod]
        public void OwnerSuitLine_PlansAgainstAWornSuit()
        {
            var text = OwnerSuitLine.Split('|')[1];
            var ops = Ops(text);
            var slots = new[] { EquipMask.HeadWear, EquipMask.ChestArmor, EquipMask.UpperArmArmor, EquipMask.LowerArmArmor, EquipMask.HandWear, EquipMask.AbdomenArmor, EquipMask.UpperLegArmor, EquipMask.LowerLegArmor, EquipMask.FootWear };
            var possessions = ops.Select((o, i) => Worn((uint)(100 + i), o.SourceWcid, slots[i])).ToList();
            var weenies = ops.Select((o, i) => W(o.TargetWcid, valid: slots[i])).ToList();

            var plan = Plan(text, possessions, weenies);

            Assert.IsTrue(plan.Ok, string.Join("; ", plan.Problems));
            Assert.AreEqual(9, plan.Creations.Count);
            Assert.AreEqual(9, plan.Removals.Count);
            Assert.AreEqual(9, plan.Lines.Count, "one line per op");
            Assert.IsTrue(plan.Creations.All(c => c.WieldLocation == possessions.Single(p => p.Guid == c.ReplacesGuid).WieldLocation));
        }

        /// <summary>The owner's full Bow kit line (suit swap, two recreates, eight replacements, four adds).</summary>
        internal static readonly string OwnerBowKitLine = "Pvp Bow | 1006750=1007204 1006751=1007207 1006752=1007208 1006753=1007201 1006754=1007202 1006755=1007209 1006756=1007210 1006757=1007211 1006758=1007203 ~1006765 ~1006764 24207=1006769 1004133=1006775x100 1004135=1006776x100 1004141=1006777 24199=1007101 21961=1006770 43951=1007127x1000 21348=1006773x1000 +1007119 +1007120 +1007121 +1007122";

        [TestMethod]
        public void OwnerBowKitLine_PlansAgainstSyntheticPossessions()
        {
            Assert.IsTrue(PvpTemplateKitEdit.TrySplitArgs(OwnerBowKitLine, out var preview, out var name, out var text));
            Assert.IsFalse(preview);
            Assert.AreEqual("Pvp Bow", name);

            var ops = Ops(text);
            Assert.AreEqual(23, ops.Count);

            var suitSlots = new[] { EquipMask.HeadWear, EquipMask.ChestArmor, EquipMask.UpperArmArmor, EquipMask.LowerArmArmor, EquipMask.HandWear, EquipMask.AbdomenArmor, EquipMask.UpperLegArmor, EquipMask.LowerLegArmor, EquipMask.FootWear };
            var possessions = new List<PvpKitPossession>();
            var weenies = new List<PvpKitWeenieInfo>();
            uint guid = 100;

            // The worn suit: each old piece in its slot, each new piece able to take it.
            for (var i = 0; i < 9; i++)
            {
                possessions.Add(Worn(guid++, ops[i].SourceWcid, suitSlots[i]));
                weenies.Add(W(ops[i].TargetWcid, valid: suitSlots[i]));
            }

            // ~1006765 (worn bow) and ~1006764 (a spare in the pack).
            possessions.Add(Worn(guid++, 1006765, EquipMask.MissileWeapon));
            weenies.Add(W(1006765, valid: EquipMask.MissileWeapon));
            possessions.Add(Pack(guid++, 1006764, placement: 0));
            weenies.Add(W(1006764, valid: EquipMask.MissileWeapon));

            // Wand worn; the rest in the main pack.
            possessions.Add(Worn(guid++, 24207, EquipMask.Held));
            weenies.Add(W(1006769, valid: EquipMask.Held));

            var placement = 1;
            foreach (var (source, target, max, stack) in new (uint, uint, int?, int)[]
            {
                (1004133, 1006775, 250, 80), (1004135, 1006776, 250, 80), (1004141, 1006777, null, 1), (24199, 1007101, null, 1),
                (21961, 1006770, null, 1), (43951, 1007127, 1000, 500), (21348, 1006773, 1000, 500),
            })
            {
                possessions.Add(Pack(guid++, source, stack: stack, placement: placement++));
                weenies.Add(W(target, maxStack: max));
            }

            foreach (var add in new uint[] { 1007119, 1007120, 1007121, 1007122 })
                weenies.Add(W(add));

            var plan = Plan(text, possessions, weenies);

            Assert.IsTrue(plan.Ok, string.Join("; ", plan.Problems));
            Assert.AreEqual(23, plan.Lines.Count, "one line per op");
            Assert.AreEqual(19, plan.Removals.Count, "every possession named by a source op");
            Assert.AreEqual(23, plan.Creations.Count, "19 replacements in place + 4 adds");
            CollectionAssert.AreEquivalent(possessions.Select(p => p.Guid).ToList(), plan.Removals);

            foreach (var c in plan.Creations.Where(c => c.ReplacesGuid.HasValue))
            {
                var old = possessions.Single(p => p.Guid == c.ReplacesGuid);
                Assert.AreEqual(old.WieldLocation, c.WieldLocation, $"wcid {c.Wcid} keeps the old wield slot");
                Assert.AreEqual(old.IsWorn ? null : old.PlacementPosition, c.PlacementPosition, $"wcid {c.Wcid} keeps the old placement");
            }

            Assert.AreEqual(100, plan.Creations.Single(c => c.Wcid == 1006775).StackSize);
            Assert.AreEqual(100, plan.Creations.Single(c => c.Wcid == 1006776).StackSize);
            Assert.AreEqual(1000, plan.Creations.Single(c => c.Wcid == 1007127).StackSize);
            Assert.AreEqual(1000, plan.Creations.Single(c => c.Wcid == 1006773).StackSize);
            Assert.AreEqual(1006765u, plan.Creations.Single(c => c.ReplacesGuid == possessions.Single(p => p.Wcid == 1006765).Guid).Wcid, "~ recreates the same wcid");
            Assert.AreEqual(4, plan.Creations.Count(c => c.ReplacesGuid == null && c.ContainerId == Char), "the four adds go to the main pack");
        }
    }
}
