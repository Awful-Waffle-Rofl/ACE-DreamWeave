using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Mutations;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Life Rending (HealthRending) imbue salvage bags on White Quartz (Content/wcid-registry.tsv
    /// life-rending-imbue): the icon underlay, the foolproof-tinker set, the source-to-recipe
    /// dispatch, the fork mutation script, and the caster-agnostic "- Life Rending" appraisal line.
    ///
    /// Casters are built from in-memory weenies (never persisted, so nothing here touches the
    /// database), following the PrismaticDriftStoneTests fixture pattern.
    /// </summary>
    [TestClass]
    public class LifeRendingImbueTests
    {
        private static uint nextGuid = 0x7F000000;

        private static Caster CreateCaster(DamageType damageType, double baseElementalDamageMod = 1.0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 2472,   // arbitrary wand-like wcid
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.ElementalDamageMod, baseElementalDamageMod },
                },
            };

            if (damageType != DamageType.Undef)
                weenie.PropertiesInt.Add(PropertyInt.DamageType, (int)damageType);

            return new Caster(weenie, new ObjectGuid(nextGuid++));
        }

        // ---------------------------------------------------------------------------------------
        // icon underlay + foolproof set
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void IconUnderlay_ContainsHealthRending()
        {
            Assert.IsTrue(RecipeManager.IconUnderlay.ContainsKey(ImbuedEffectType.HealthRending),
                "HealthRending must have an icon underlay or the imbue applies with no visible marker");

            Assert.AreEqual(0x0600678Du, RecipeManager.IconUnderlay[ImbuedEffectType.HealthRending],
                "HealthRending's icon underlay must be the retail Honeyed Life Mead underlay (0x0600678D)");
        }

        [TestMethod]
        public void IsFoolproofTinker_RecognizesForkAndRetailFoolproofs()
        {
            Assert.IsTrue(RecipeManager.IsFoolproofTinker(1002700),
                "Foolproof White Quartz (fork addition) must be a foolproof tinker");

            Assert.IsTrue(RecipeManager.IsFoolproofTinker(36626),
                "Foolproof Red Garnet (retail) must still be a foolproof tinker");

            Assert.IsFalse(RecipeManager.IsFoolproofTinker(21085),
                "the STANDARD (non-foolproof) White Quartz bag must not be treated as a foolproof tinker");
        }

        // ---------------------------------------------------------------------------------------
        // source-to-recipe dispatch
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void SourceToRecipe_MapsTheStandardWhiteQuartzBagToTheLifeRendingRecipe()
        {
            // Foolproof White Quartz (1002700) cannot appear in SourceToRecipe at all - it has no
            // WeenieClassName enum member (that enum is ushort-backed, 1002700 does not fit) and is
            // dispatched instead by a raw-wcid guard in RecipeManager_New.GetNewRecipe, ahead of the
            // switch that reads this dictionary. See that guard's comment for the full explanation.
            Assert.AreEqual(1000050u,
                RecipeManager.SourceToRecipe[WeenieClassName.W_MATERIALWHITEQUARTZ_CLASS],
                "the standard White Quartz salvage bag (21085) must map to recipe 1000050");
        }

        // ---------------------------------------------------------------------------------------
        // fork mutation script 0x3A000000
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void MutationScript_0x3A000000_ExistsAndSetsHealthRending()
        {
            var mutation = MutationCache.GetMutation(0x3A000000u);

            Assert.IsNotNull(mutation,
                "Entity/Mutations/Recipes/3A000000 - White Quartz.txt must resolve to a MutationFilter "
                + "(embedded resource missing, or not registered in both csproj lists)");

            var weapon = CreateCaster(DamageType.Fire);

            var mutated = mutation.TryMutate(weapon);

            Assert.IsTrue(mutated, "the mutation must report success against a fresh caster");
            Assert.AreEqual(ImbuedEffectType.HealthRending, weapon.ImbuedEffect,
                "the mutation script must set ImbuedEffect = HealthRending");
            Assert.AreEqual(1, weapon.NumTimesTinkered,
                "a fresh weapon (NumTimesTinkered unset) must end at 1");
        }

        [TestMethod]
        public void MutationScript_0x3A000000_AddsToExistingNumTimesTinkered()
        {
            var mutation = MutationCache.GetMutation(0x3A000000u);
            Assert.IsNotNull(mutation, "mutation script must resolve");

            var weapon = CreateCaster(DamageType.Fire);
            weapon.NumTimesTinkered = 3;

            mutation.TryMutate(weapon);

            Assert.AreEqual(4, weapon.NumTimesTinkered,
                "NumTimesTinkered (>= 1 ? add : set) 1 must ADD when already >= 1, yielding 3 + 1 = 4");
        }

        // ---------------------------------------------------------------------------------------
        // appraisal: "- Life Rending" is caster-agnostic
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void GetAppraisalLines_NonLifeCasterWithHealthRending_ReturnsExactlyOneLine()
        {
            // a Fire wand imbued with HealthRending via the salvage bag - NOT a life caster
            var wand = CreateCaster(DamageType.Fire);
            wand.ImbuedEffect = ImbuedEffectType.HealthRending;

            var lines = new List<string>(LifeCasterDisplay.GetAppraisalLines(wand));

            CollectionAssert.AreEqual(new List<string> { "- Life Rending" }, lines,
                "a non-life caster carrying HealthRending must show exactly one line, the rend line, "
                + "with no cleave/damage-bonus lines (those require IsLifeCaster)");
        }

        [TestMethod]
        public void GetAppraisalLines_LifeCasterWithBoth_CleavingLineComesFirst()
        {
            var wand = CreateCaster(DamageType.Health);
            wand.ResistanceModifierType = DamageType.Health;
            wand.ResistanceModifier = 0.1;
            wand.ImbuedEffect = ImbuedEffectType.HealthRending;

            var lines = new List<string>(LifeCasterDisplay.GetAppraisalLines(wand));

            Assert.IsTrue(lines.Count >= 2, "a life caster with both effects should produce at least two lines");
            Assert.AreEqual("- Resistance Cleaving: Life", lines[0],
                "the fixed-value cleave line must come first when both apply");
            Assert.AreEqual("- Life Rending", lines[1],
                "the HealthRending line must follow the cleaving line");
        }

        [TestMethod]
        public void GetAppraisalLines_NonLifeCasterWithoutHealthRending_ReturnsNoLines()
        {
            var wand = CreateCaster(DamageType.Fire);

            var lines = new List<string>(LifeCasterDisplay.GetAppraisalLines(wand));

            Assert.AreEqual(0, lines.Count,
                "an ordinary Fire wand with no life-magic properties must produce no Property Details lines");
        }
    }
}
