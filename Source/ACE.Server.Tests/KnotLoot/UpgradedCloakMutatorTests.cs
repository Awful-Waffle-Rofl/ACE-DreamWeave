using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories.Tables;
using ACE.Server.KnotLoot;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>UpgradedCloak.Mutate stamps a consistent set of fields for every proc outcome, and the materializer applies it only to a mutation drop.</summary>
    [TestClass]
    public class UpgradedCloakMutatorTests
    {
        private int calls;

        private Func<double> Script(params double[] values)
        {
            calls = 0;
            return () => values[calls++];
        }

        private static WorldObject Spines()
        {
            var item = UpgradedCloakTestItems.Item(cloak: true);
            item.WieldDifficulty = 180;
            item.IconOverlayId = 0x6006C37;
            item.ProcSpell = 6151;
            item.CloakWeaveProc = 1;
            item.ItemMaxLevel = 4;
            return item;
        }

        [TestMethod]
        public void Mutate_DrawOrder_LevelThenProc_SetFromItsOwnRoll()
        {
            var item = Spines();

            // 375 -> window 8-10; u 0.9 -> 10. r = 8.5/13 -> index 8 = Cloaked in Skill
            UpgradedCloak.Mutate(item, 375, Script(0.9, 8.5 / 13), () => EquipmentSet.CloakWarMagic);

            Assert.AreEqual(2, calls, "exactly two draws: level, then proc");
            Assert.AreEqual(10, item.ItemMaxLevel);
            Assert.IsNull(item.IconOverlayId, "no level-10 overlay exists in the dat");
            Assert.AreEqual(EquipmentSet.CloakWarMagic, item.EquipmentSetId);
            Assert.AreEqual(10, item.GetProperty(PropertyInt.SetBonusMaxLevel));
            Assert.AreEqual((uint)SpellId.CloakAllSkill, item.ProcSpell);
            Assert.IsTrue(item.ProcSpellSelfTargeted);
            Assert.AreEqual(1, item.CloakWeaveProc);
            Assert.AreEqual(40.0, item.GetProperty(PropertyFloat.CloakProcStatModValue));
            Assert.IsNull(item.GetProperty(PropertyInt.CloakDamageReductionAmount));
            Assert.AreEqual(180, item.WieldDifficulty, "WieldDifficulty stays authored");
        }

        [TestMethod]
        public void EveryProcOutcome_StampsAConsistentFieldSet()
        {
            for (var i = 0; i < 13; i++)
            {
                var item = Spines();
                var proc = i < 12 ? CloakChance.UpgradedSurgeSpells[i] : SpellId.Undef;

                // pre-stamp the opposite outcome's fields so a stale field would show
                item.SetProperty(PropertyFloat.CloakProcStatModValue, 99.0);
                item.SetProperty(PropertyInt.CloakDamageReductionAmount, 999);
                item.ProcSpellSelfTargeted = true;

                UpgradedCloak.ApplyProc(item, proc);

                if (proc == SpellId.Undef)
                {
                    Assert.IsNull(item.ProcSpell, "damage reduction removes ProcSpell");
                    Assert.IsFalse(item.ProcSpellSelfTargeted);
                    Assert.AreEqual(2, item.CloakWeaveProc);
                    Assert.AreEqual(400, item.GetProperty(PropertyInt.CloakDamageReductionAmount));
                    Assert.IsNull(item.GetProperty(PropertyFloat.CloakProcStatModValue));
                }
                else
                {
                    Assert.AreEqual((uint)proc, item.ProcSpell, proc.ToString());
                    Assert.AreEqual(1, item.CloakWeaveProc, proc.ToString());
                    Assert.AreEqual(proc == SpellId.CloakAllSkill, item.ProcSpellSelfTargeted, proc.ToString());
                    Assert.IsNull(item.GetProperty(PropertyInt.CloakDamageReductionAmount), proc.ToString());

                    if (proc == SpellId.CloakAllSkill)
                        Assert.AreEqual(40.0, item.GetProperty(PropertyFloat.CloakProcStatModValue));
                    else
                        Assert.IsNull(item.GetProperty(PropertyFloat.CloakProcStatModValue), proc.ToString());
                }
            }
        }

        [TestMethod]
        public void Overlay_RetailOneToFive_NumeralsSixToNine_NoneAtTen()
        {
            CollectionAssert.AreEqual(ACE.Server.Factories.LootGenerationFactory.IconOverlay_ItemMaxLevel, new List<uint>(UpgradedCloak.IconOverlayByMaxLevel).GetRange(0, 5), "1-5 are the retail overlays");

            Assert.AreEqual(0x6006C38u, UpgradedCloak.IconOverlay(5));
            Assert.AreEqual(0x6006C39u, UpgradedCloak.IconOverlay(6));
            Assert.AreEqual(0x6006C3Cu, UpgradedCloak.IconOverlay(9));
            Assert.IsNull(UpgradedCloak.IconOverlay(10));
            Assert.IsNull(UpgradedCloak.IconOverlay(0));
        }

        [TestMethod]
        public void Mutate_LowKnot_Level5_KeepsTheRetailFiveOverlay_AndDamageReduction()
        {
            var item = Spines();

            UpgradedCloak.Mutate(item, 200, Script(0.0, 0.999), () => EquipmentSet.CloakAlchemy);

            Assert.AreEqual(5, item.ItemMaxLevel);
            Assert.AreEqual(0x6006C38u, item.IconOverlayId);
            Assert.AreEqual(2, item.CloakWeaveProc);
            Assert.IsNull(item.ProcSpell);
            Assert.AreEqual(400, item.GetProperty(PropertyInt.CloakDamageReductionAmount));
        }

        [TestMethod]
        public void Materializer_AppliesTheMutation_OnlyToAMutationDrop()
        {
            var drops = new List<KnotLootDrop>
            {
                new(1007602, 1, null, 0.0, KnotLootMutation.UpgradedCloak),
                new(1007601, 1, null, 0.0),
            };

            var items = KnotLootMaterializer.Create(drops, "matron", 400, wcid => UpgradedCloakTestItems.Item(cloak: wcid == 1007602, wcid), Script(0.0, 0.0));

            Assert.AreEqual(2, calls, "the materializer feeds the mutator its draws");
            Assert.AreEqual(8, items[0].ItemMaxLevel);
            Assert.AreEqual((uint)SpellId.SearingDiscII, items[0].ProcSpell);
            Assert.IsNotNull(items[0].EquipmentSetId);
            Assert.AreEqual(10, items[0].GetProperty(PropertyInt.SetBonusMaxLevel));
            Assert.IsNull(items[0].GetProperty(PropertyInt.KnotLootLevel), "no trait stamps on a trait-less drop");

            Assert.IsNull(items[1].ItemMaxLevel, "the Torc is plain");
            Assert.IsNull(items[1].EquipmentSetId);
            Assert.IsNull(items[1].GetProperty(PropertyInt.SetBonusMaxLevel));
        }

        [TestMethod]
        public void Mutate_LiveOverload_RollsARetailCloakSet()
        {
            var item = Spines();

            UpgradedCloak.Mutate(item, 300, Script(0.5, 0.5));

            var set = item.EquipmentSetId;
            Assert.IsNotNull(set);
            StringAssert.StartsWith(set.Value.ToString(), "Cloak", "the set comes from CloakChance.RollEquipmentSet");
        }
    }
}
