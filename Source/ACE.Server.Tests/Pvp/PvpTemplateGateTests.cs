using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvpTemplateGate decision table (TEMPLATES.md "Gates"), row by row. Each row names its expected outcome
    /// as a PvpTemplateText constant name ("allow" for null), so a row that would pass under a broken predicate is
    /// visible as a wrong name, not a silent pass. Player.PvpTemplateBlocked passes its arguments straight through
    /// to Decide (PvpTemplatePlayerTests covers that wiring on a seeded Player).
    /// </summary>
    [TestClass]
    public class PvpTemplateGateTests
    {
        private const PvpTemplateItemKind N = PvpTemplateItemKind.None;
        private const PvpTemplateItemKind P = PvpTemplateItemKind.Personal;
        private const PvpTemplateItemKind I = PvpTemplateItemKind.Issued;

        private static string Text(string name)
        {
            if (name == "allow")
                return null;

            var field = typeof(PvpTemplateText).GetField(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(field, $"PvpTemplateText.{name} does not exist");
            return (string)field.GetValue(null);
        }

        [TestMethod]
        // ---- the system bypass allows everything ----
        [DataRow(PvpTemplateAction.Drop, false, true, I, N, "allow")]
        [DataRow(PvpTemplateAction.Experience, true, true, N, N, "allow")]
        [DataRow(PvpTemplateAction.Equip, true, true, P, N, "allow")]
        // ---- issued-item economy lock: always, templated or not ----
        [DataRow(PvpTemplateAction.GiveToPlayer, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.GiveToNpc, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.Trade, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.Drop, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.MoveToForeignContainer, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.VendorSell, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.Vault, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.Mule, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.MarketList, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.Salvage, false, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.Drop, true, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.Trade, true, false, I, N, "IssuedItemLocked")]
        [DataRow(PvpTemplateAction.MoveToForeignContainer, false, false, P, I, "IssuedItemLocked")]
        // ---- issued items are inert outside a match ----
        [DataRow(PvpTemplateAction.Equip, false, false, I, N, "IssuedItemOutsideMatch")]
        [DataRow(PvpTemplateAction.Use, false, false, I, N, "IssuedItemOutsideMatch")]
        [DataRow(PvpTemplateAction.UseWithTarget, false, false, P, I, "IssuedItemOutsideMatch")]
        [DataRow(PvpTemplateAction.SpellComponent, false, false, I, N, "IssuedItemOutsideMatch")]
        [DataRow(PvpTemplateAction.MergeOrSplit, false, false, I, I, "IssuedItemOutsideMatch")]
        [DataRow(PvpTemplateAction.MoveWithinPack, false, false, I, N, "allow")]
        [DataRow(PvpTemplateAction.Unequip, false, false, I, N, "allow")]
        // ---- untemplated personal play is untouched ----
        [DataRow(PvpTemplateAction.Equip, false, false, P, N, "allow")]
        [DataRow(PvpTemplateAction.Drop, false, false, P, N, "allow")]
        [DataRow(PvpTemplateAction.VendorBuy, false, false, N, N, "allow")]
        [DataRow(PvpTemplateAction.Experience, false, false, N, N, "allow")]
        [DataRow(PvpTemplateAction.Confirmation, false, false, N, N, "allow")]
        [DataRow(PvpTemplateAction.Confirmation, true, false, N, N, "PersonalItemLocked")]
        [DataRow(PvpTemplateAction.FacetSwitch, false, false, N, N, "allow")]
        // ---- templated: personal-item lockdown ----
        [DataRow(PvpTemplateAction.MoveWithinPack, true, false, P, N, "allow")]
        [DataRow(PvpTemplateAction.Equip, true, false, P, N, "PersonalItemLocked")]
        [DataRow(PvpTemplateAction.Equip, true, false, N, N, "PersonalItemLocked")]
        [DataRow(PvpTemplateAction.Equip, true, false, I, N, "allow")]
        [DataRow(PvpTemplateAction.SpellComponent, true, false, P, N, "PersonalItemLocked")]
        [DataRow(PvpTemplateAction.SpellComponent, true, false, I, N, "allow")]
        [DataRow(PvpTemplateAction.Use, true, false, P, N, "PersonalItemLocked")]
        [DataRow(PvpTemplateAction.Use, true, false, I, N, "allow")]
        [DataRow(PvpTemplateAction.Use, true, false, N, N, "allow")]
        [DataRow(PvpTemplateAction.UseWithTarget, true, false, I, P, "PersonalItemLocked")]
        [DataRow(PvpTemplateAction.UseWithTarget, true, false, P, N, "PersonalItemLocked")]
        [DataRow(PvpTemplateAction.UseWithTarget, true, false, I, N, "allow")]
        [DataRow(PvpTemplateAction.UseWithTarget, true, false, I, I, "allow")]
        [DataRow(PvpTemplateAction.Cast, true, false, P, N, "NonTemplateSpell")]
        [DataRow(PvpTemplateAction.Cast, true, false, N, N, "NonTemplateSpell")]
        [DataRow(PvpTemplateAction.Cast, false, false, N, N, "allow")]
        [DataRow(PvpTemplateAction.Unequip, true, false, I, N, "allow")]
        [DataRow(PvpTemplateAction.Unequip, true, false, P, N, "allow")]
        [DataRow(PvpTemplateAction.MergeOrSplit, true, false, I, I, "allow")]
        [DataRow(PvpTemplateAction.MergeOrSplit, true, false, P, P, "allow")]
        [DataRow(PvpTemplateAction.MergeOrSplit, true, false, P, N, "allow")]
        [DataRow(PvpTemplateAction.MergeOrSplit, true, false, I, P, "MixedStack")]
        [DataRow(PvpTemplateAction.MergeOrSplit, true, false, P, I, "MixedStack")]
        // ---- templated: the economy is shut ----
        [DataRow(PvpTemplateAction.VendorBuy, true, false, N, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.VendorSell, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.GiveToPlayer, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.GiveToNpc, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.ReceiveGive, true, false, N, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.GroundPickup, true, false, N, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.Trade, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.Drop, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.MoveToForeignContainer, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.Salvage, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.Vault, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.Mule, true, false, P, N, "EconomyLocked")]
        [DataRow(PvpTemplateAction.MarketList, true, false, P, N, "EconomyLocked")]
        // ---- templated: facets and progression ----
        [DataRow(PvpTemplateAction.FacetSwitch, true, false, N, N, "FacetSwitchLocked")]
        [DataRow(PvpTemplateAction.Experience, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.Luminance, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.QuestStamp, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.ClassAbilityPoints, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.Title, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.Contract, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.Challenge, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.House, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.RaiseAttribute, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.RaiseVital, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.RaiseSkill, true, false, N, N, "ProgressionLocked")]
        [DataRow(PvpTemplateAction.TrainSkill, true, false, N, N, "ProgressionLocked")]
        public void DecisionTable(PvpTemplateAction action, bool templated, bool bypass, PvpTemplateItemKind item, PvpTemplateItemKind target, string expected)
        {
            Assert.AreEqual(Text(expected), PvpTemplateGate.Decide(action, templated, bypass, item, target),
                $"{action} templated={templated} bypass={bypass} item={item} target={target}: expected {expected}");
        }

        /// <summary>Every action has at least one templated row in the table, so a new action cannot slip in unexamined.</summary>
        [TestMethod]
        public void DecisionTable_CoversEveryActionWhileTemplated()
        {
            var rows = typeof(PvpTemplateGateTests).GetMethod(nameof(DecisionTable)).GetCustomAttributes<DataRowAttribute>()
                .Select(r => r.Data)
                .Where(d => (bool)d[1] && !(bool)d[2])
                .Select(d => (PvpTemplateAction)d[0])
                .ToHashSet();

            var missing = Enum.GetValues(typeof(PvpTemplateAction)).Cast<PvpTemplateAction>().Where(a => !rows.Contains(a)).ToList();

            Assert.AreEqual(0, missing.Count, "no templated row for: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void CastTable()
        {
            var spells = new List<int> { 3, 4 };

            Assert.IsNull(PvpTemplateGate.DecideCast(5, false, false, null), "untemplated casts are untouched");
            Assert.IsNull(PvpTemplateGate.DecideCast(5, true, true, null), "the system bypass casts anything");
            Assert.IsNull(PvpTemplateGate.DecideCast(3, true, false, spells));
            Assert.AreEqual(PvpTemplateText.NonTemplateSpell, PvpTemplateGate.DecideCast(5, true, false, spells));
            Assert.AreEqual(PvpTemplateText.NonTemplateSpell, PvpTemplateGate.DecideCast(3, true, false, null), "an unreadable record refuses: inert, never open");
        }

        /// <summary>Every refusal is player-facing text with the [Arena] prefix, ASCII only.</summary>
        [TestMethod]
        public void EveryRefusal_IsPrefixedAscii()
        {
            foreach (var field in typeof(PvpTemplateText).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(string)))
            {
                var text = (string)field.GetValue(null);
                StringAssert.StartsWith(text, "[Arena] ", field.Name);
                Assert.IsTrue(text.All(c => c < 128), $"{field.Name} has a non-ASCII character");
            }
        }
    }
}
