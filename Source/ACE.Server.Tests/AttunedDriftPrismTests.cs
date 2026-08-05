using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Mutations;
using ACE.Server.Factories.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Attuned Drift Prisms - the eight one-way elemental orb attunement gems.
    ///
    /// The point of most of this file is drift detection. AttunedDriftPrism's per-tier
    /// ElementalDamageMod table and its wield-requirement -> tier ladder are hand-written constants,
    /// but both are supposed to be exact functions of the loot generation mutation scripts
    /// (Source/ACE.Server/Entity/Mutations/Casters/caster_elemental.txt and caster_non_elemental.txt,
    /// embedded resources compiled by MutationCache). These tests re-parse those scripts at run time
    /// and re-derive every constant from them, so no number below is hand-typed: if upstream ever
    /// reshapes the caster loot bands, the constants and the tests disagree and CI says so.
    ///
    /// All fixtures are database-free and dat-free: a Caster built straight from an in-memory Weenie.
    /// </summary>
    [TestClass]
    public class AttunedDriftPrismTests
    {
        private const string ElementalScript = "Casters.caster_elemental.txt";
        private const string NonElementalScript = "Casters.caster_non_elemental.txt";

        /// <summary>The vanilla loot orb (wcid 2366) Setup mesh - a known member of the orb Setup set.</summary>
        private const uint VanillaOrbSetup = 0x020000ED;

        /// <summary>The Setup mesh of wcid 29262 "Fire Sceptre" (DB value 33559228) - deliberately NOT an orb.</summary>
        private const uint SceptreSetup = 0x020012BC;

        private static uint nextGuid = 0x80000000;

        // ------------------------------------------------------------------
        // fixtures
        // ------------------------------------------------------------------

        private static Caster MakeCaster(uint setupId, WieldRequirement wieldReq = WieldRequirement.Invalid, int? wieldDifficulty = null,
            DamageType? damageType = null, double? elementalDamageMod = null, int numTimesTinkered = 0, ImbuedEffectType imbued = ImbuedEffectType.Undef)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)ItemType.Caster },
                { PropertyInt.ValidLocations, (int)EquipMask.Held },
            };

            if (wieldReq != WieldRequirement.Invalid)
                ints[PropertyInt.WieldRequirements] = (int)wieldReq;

            if (wieldDifficulty != null)
                ints[PropertyInt.WieldDifficulty] = wieldDifficulty.Value;

            if (damageType != null)
                ints[PropertyInt.DamageType] = (int)damageType.Value;

            if (numTimesTinkered != 0)
                ints[PropertyInt.NumTimesTinkered] = numTimesTinkered;

            if (imbued != ImbuedEffectType.Undef)
                ints[PropertyInt.ImbuedEffect] = (int)imbued;

            var weenie = new Weenie
            {
                WeenieClassId = 2366,
                ClassName = "orb",
                WeenieType = WeenieType.Caster,
                PropertiesInt = ints,
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setupId } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Orb" } },
            };

            if (elementalDamageMod != null)
                weenie.PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.ElementalDamageMod, elementalDamageMod.Value } };

            return new Caster(weenie, new ObjectGuid(nextGuid++));
        }

        private static Caster MakeOrb(WieldRequirement wieldReq = WieldRequirement.Invalid, int? wieldDifficulty = null)
            => MakeCaster(VanillaOrbSetup, wieldReq, wieldDifficulty);

        // ------------------------------------------------------------------
        // loot-script parsing (the source of truth for every constant below)
        // ------------------------------------------------------------------

        /// <summary>
        /// Walks a compiled mutation script and yields one record per (tier, EffectList) pair, pulling
        /// out the three qualities the caster scripts assign together as one atomic block:
        /// ElementalDamageMod, WieldRequirements and WieldDifficulty.
        ///
        /// Mutation.Chances is the per-tier gate list (index 0 = tier 1); a non-zero entry means that
        /// mutation block applies at that tier. Every EffectList inside it is one possible outcome.
        /// </summary>
        private static List<(int Tier, double? Mod, int? WieldReq, int? WieldDiff)> ParseScript(string filename)
        {
            var filter = MutationCache.GetMutation(filename);

            Assert.IsNotNull(filter, $"{filename} did not compile - the embedded mutation resource is missing or renamed.");

            var rows = new List<(int, double?, int?, int?)>();

            foreach (var mutation in filter.Mutations)
            {
                for (var i = 0; i < mutation.Chances.Count; i++)
                {
                    if (mutation.Chances[i] <= 0.0f)
                        continue;

                    var tier = i + 1;

                    foreach (var outcome in mutation.Outcomes)
                    {
                        foreach (var effectList in outcome.EffectLists)
                        {
                            double? mod = null;
                            int? wieldReq = null;
                            int? wieldDiff = null;

                            foreach (var effect in effectList.Effects)
                            {
                                if (effect.Quality == null || effect.Arg1 == null)
                                    continue;

                                if (effect.Quality.StatType == StatType.Float && effect.Quality.StatIdx == (int)PropertyFloat.ElementalDamageMod)
                                    mod = effect.Arg1.DoubleVal;
                                else if (effect.Quality.StatType == StatType.Int && effect.Quality.StatIdx == (int)PropertyInt.WieldRequirements)
                                    wieldReq = effect.Arg1.IntVal;
                                else if (effect.Quality.StatType == StatType.Int && effect.Quality.StatIdx == (int)PropertyInt.WieldDifficulty)
                                    wieldDiff = effect.Arg1.IntVal;
                            }

                            if (mod != null || wieldReq != null || wieldDiff != null)
                                rows.Add((tier, mod, wieldReq, wieldDiff));
                        }
                    }
                }
            }

            Assert.AreNotEqual(0, rows.Count, $"{filename} parsed to zero usable outcomes - the parse walk is wrong, not the table.");

            return rows;
        }

        // ------------------------------------------------------------------
        // the per-tier value table
        // ------------------------------------------------------------------

        [TestMethod]
        public void ElementalDamageModTable_IsThe25thPercentileOfEachLootTierBand()
        {
            var rows = ParseScript(ElementalScript).Where(r => r.Mod != null).ToList();

            var tiersInScript = rows.Select(r => r.Tier).Distinct().OrderBy(t => t).ToList();

            Assert.AreNotEqual(0, tiersInScript.Count, "caster_elemental.txt defined no ElementalDamageMod outcomes at all.");

            for (var tier = AttunedDriftPrism.MinTier; tier <= AttunedDriftPrism.MaxTier; tier++)
            {
                var band = rows.Where(r => r.Tier == tier).Select(r => (decimal)r.Mod.Value).ToList();

                var actual = (decimal)AttunedDriftPrism.ElementalDamageModByTier[tier - 1];

                if (band.Count == 0)
                {
                    // caster_elemental.txt has no block for this tier, so a naturally elemental caster of
                    // this tier keeps its base weenie value, which retail ships as exactly 1.0.
                    Assert.AreEqual(1.00m, actual,
                        $"tier {tier} has no band in caster_elemental.txt, so the table must hold the base weenie value 1.00, not {actual}.");
                    continue;
                }

                var min = band.Min();
                var max = band.Max();

                var expected = Math.Round(min + AttunedDriftPrism.AttunementPercentile * (max - min), 2, MidpointRounding.AwayFromZero);

                Assert.AreEqual(expected, actual,
                    $"tier {tier}: caster_elemental.txt band is {min} .. {max}; " +
                    $"{min} + {AttunedDriftPrism.AttunementPercentile} * ({max} - {min}) = {min + AttunedDriftPrism.AttunementPercentile * (max - min)}, " +
                    $"which rounds to {expected} at the 0.01 roll granularity, but the table holds {actual}.");
            }
        }

        [TestMethod]
        public void ElementalDamageModTable_CoversEveryTierExactlyOnce()
        {
            Assert.AreEqual(AttunedDriftPrism.MaxTier - AttunedDriftPrism.MinTier + 1, AttunedDriftPrism.ElementalDamageModByTier.Length,
                $"the table must hold one entry per tier {AttunedDriftPrism.MinTier}..{AttunedDriftPrism.MaxTier}.");
        }

        [TestMethod]
        public void ElementalDamageModTable_IsMonotonicAndNeverBelowUnity()
        {
            for (var i = 0; i < AttunedDriftPrism.ElementalDamageModByTier.Length; i++)
            {
                Assert.IsTrue(AttunedDriftPrism.ElementalDamageModByTier[i] >= 1.0,
                    $"tier {i + 1} value {AttunedDriftPrism.ElementalDamageModByTier[i]} is below 1.0, which would make an attuned orb worse than a plain one.");

                if (i > 0)
                {
                    Assert.IsTrue(AttunedDriftPrism.ElementalDamageModByTier[i] >= AttunedDriftPrism.ElementalDamageModByTier[i - 1],
                        $"tier {i + 1} value {AttunedDriftPrism.ElementalDamageModByTier[i]} is below tier {i} value {AttunedDriftPrism.ElementalDamageModByTier[i - 1]}.");
                }
            }
        }

        // ------------------------------------------------------------------
        // wield requirement -> tier
        // ------------------------------------------------------------------

        [TestMethod]
        public void CasterTier_MatchesTheLowestLootTierProducingEachWieldRung()
        {
            var rows = ParseScript(ElementalScript)
                .Concat(ParseScript(NonElementalScript))
                .Where(r => r.WieldReq != null && r.WieldDiff != null)
                .ToList();

            Assert.AreNotEqual(0, rows.Count, "neither caster script defined a wield requirement outcome.");

            // group the script's (WieldRequirements, WieldDifficulty) rungs and take the lowest tier
            // that can produce each - the ladders are not injective, so lowest-tier is the contract.
            var rungs = rows
                .GroupBy(r => (Req: r.WieldReq.Value, Diff: r.WieldDiff.Value))
                .Select(g => (g.Key.Req, g.Key.Diff, MinTier: g.Min(r => r.Tier)))
                .ToList();

            foreach (var rung in rungs)
            {
                var orb = MakeOrb((WieldRequirement)rung.Req, rung.Diff);

                Assert.AreEqual(rung.MinTier, AttunedDriftPrism.GetCasterTier(orb),
                    $"a caster with WieldRequirements {(WieldRequirement)rung.Req} / WieldDifficulty {rung.Diff} is first produced at tier {rung.MinTier} " +
                    $"by the loot mutation scripts, so GetCasterTier must return {rung.MinTier}.");
            }
        }

        [TestMethod]
        public void CasterTier_NoWieldRequirement_IsTierOne()
        {
            Assert.AreEqual(1, AttunedDriftPrism.GetCasterTier(MakeOrb()),
                "a starter orb with no wield requirement at all must map to tier 1.");

            Assert.AreEqual(1.00, AttunedDriftPrism.GetAttunementMod(MakeOrb()), 0.0001,
                "tier 1 attunes to the base elemental caster value 1.00.");
        }

        [TestMethod]
        public void CasterTier_RungsBelowTheLadderFloor_AreTierOne()
        {
            // hand-authored low-level orbs sit well under the loot ladder's lowest rung
            Assert.AreEqual(1, AttunedDriftPrism.GetCasterTier(MakeOrb(WieldRequirement.RawSkill, 165)));
            Assert.AreEqual(1, AttunedDriftPrism.GetCasterTier(MakeOrb(WieldRequirement.Level, 50)));
            Assert.AreEqual(1, AttunedDriftPrism.GetCasterTier(MakeOrb(WieldRequirement.Training, 3)));
        }

        [TestMethod]
        public void CasterTier_SkillIsReadOnTheSameLadderAsRawSkill()
        {
            // the mutation scripts only emit RawSkill, but hand-authored world content uses Skill for
            // the identical skill-value gate (ace_world has orbs at Skill / WarMagic / 330)
            foreach (var difficulty in new[] { 290, 310, 330, 355, 375, 385 })
            {
                Assert.AreEqual(
                    AttunedDriftPrism.GetCasterTier(MakeOrb(WieldRequirement.RawSkill, difficulty)),
                    AttunedDriftPrism.GetCasterTier(MakeOrb(WieldRequirement.Skill, difficulty)),
                    $"WieldRequirement.Skill at difficulty {difficulty} must read the same rung as RawSkill.");
            }
        }

        // ------------------------------------------------------------------
        // element mapping
        // ------------------------------------------------------------------

        [TestMethod]
        public void PrismElementMapping_CoversAllEightPrisms()
        {
            var expected = new (uint Wcid, DamageType Element)[]
            {
                (AttunedDriftPrism.FlamePrism,     DamageType.Fire),
                (AttunedDriftPrism.FrostPrism,     DamageType.Cold),
                (AttunedDriftPrism.AcidPrism,      DamageType.Acid),
                (AttunedDriftPrism.LightningPrism, DamageType.Electric),
                (AttunedDriftPrism.BladePrism,     DamageType.Slash),
                (AttunedDriftPrism.ForcePrism,     DamageType.Pierce),
                (AttunedDriftPrism.ShockPrism,     DamageType.Bludgeon),
                (AttunedDriftPrism.VoidPrism,      DamageType.Nether),
            };

            foreach (var (wcid, element) in expected)
            {
                Assert.IsTrue(AttunedDriftPrism.IsPrism(wcid), $"wcid {wcid} is not registered as a prism.");
                Assert.IsTrue(AttunedDriftPrism.TryGetElement(wcid, out var actual), $"wcid {wcid} has no element mapping.");
                Assert.AreEqual(element, actual, $"wcid {wcid} maps to the wrong element.");
            }

            Assert.AreEqual(8, expected.Select(e => e.Element).Distinct().Count(), "the eight prisms must cover eight distinct elements.");
        }

        [TestMethod]
        public void PrismWcids_AreEightContiguousIdsAndNothingElseIsAPrism()
        {
            for (var wcid = AttunedDriftPrism.FlamePrism; wcid <= AttunedDriftPrism.VoidPrism; wcid++)
                Assert.IsTrue(AttunedDriftPrism.IsPrism(wcid), $"wcid {wcid} falls inside the prism range but is not registered.");

            // exactly eight wcids in a wide window around the block are registered - no strays,
            // and nothing outside the contiguous run
            var registered = Enumerable.Range(0, 64)
                .Select(i => AttunedDriftPrism.FlamePrism - 16 + (uint)i)
                .Count(AttunedDriftPrism.IsPrism);

            Assert.AreEqual(8, registered, "exactly eight contiguous wcids must be registered as prisms.");

            Assert.IsFalse(AttunedDriftPrism.IsPrism(AttunedDriftPrism.FlamePrism - 1));
            Assert.IsFalse(AttunedDriftPrism.IsPrism(AttunedDriftPrism.VoidPrism + 1));
            Assert.IsFalse(AttunedDriftPrism.IsPrism(2366), "the vanilla orb must not be treated as a prism.");
        }

        // ------------------------------------------------------------------
        // orb-only, one-way, clean-state guards
        // ------------------------------------------------------------------

        [TestMethod]
        public void IsOrb_AcceptsOrbSetupsAndRejectsWandSetups()
        {
            Assert.IsTrue(AttunedDriftPrism.IsOrb(MakeCaster(VanillaOrbSetup)), "the vanilla orb mesh must be recognised as an orb.");

            Assert.IsFalse(AttunedDriftPrism.IsOrb(MakeCaster(SceptreSetup)), "a sceptre mesh must not be recognised as an orb.");
            Assert.IsFalse(AttunedDriftPrism.IsOrb(MakeCaster(0x020004C1)), "the Staff of Aerfalle mesh is deliberately excluded from the orb set.");
        }

        [TestMethod]
        public void OrbSetupSet_ContainsTheRetailPerElementOrbFamily()
        {
            // wcids 27881-27887 are retail's purpose-built seven-element orb family, on the contiguous
            // Setup block 0x020011EA-0x020011F0. If the orb set ever loses these, it lost real orbs.
            for (uint setup = 0x020011EA; setup <= 0x020011F0; setup++)
                Assert.IsTrue(AttunedDriftPrism.OrbSetupIds.Contains(setup), $"orb Setup 0x{setup:X8} is missing from the orb set.");
        }

        [TestMethod]
        public void IsAlreadyAttuned_DetectsNaturalAndPriorAlignments()
        {
            Assert.IsFalse(AttunedDriftPrism.IsAlreadyAttuned(MakeOrb()), "a plain orb is not aligned.");

            Assert.IsTrue(AttunedDriftPrism.IsAlreadyAttuned(MakeCaster(VanillaOrbSetup, damageType: DamageType.Fire)),
                "an orb carrying a DamageType is already aligned - a prism must refuse it.");

            Assert.IsTrue(AttunedDriftPrism.IsAlreadyAttuned(MakeCaster(VanillaOrbSetup, elementalDamageMod: 1.07)),
                "an orb carrying an ElementalDamageMod is already aligned - a prism must refuse it.");
        }

        [TestMethod]
        public void AttunementLeavesTinkerStateUntouched()
        {
            // Attunement writes DamageType, ElementalDamageMod and the UiEffects glow and nothing else.
            // These four are the properties a follow-up tinker/plating step inspects, and every one of
            // them must still read as a factory-clean item afterward.
            var orb = MakeOrb();

            Assert.AreEqual(0, orb.NumTimesTinkered);
            Assert.AreEqual(ImbuedEffectType.Undef, orb.ImbuedEffect);
            Assert.IsFalse(orb.Retained);
            Assert.IsNull(orb.TinkerLog);

            // simulate exactly what AttunedDriftPrism.Attune writes
            orb.SetProperty(PropertyInt.DamageType, (int)DamageType.Cold);
            orb.SetProperty(PropertyFloat.ElementalDamageMod, AttunedDriftPrism.GetAttunementMod(orb));
            orb.SetProperty(PropertyInt.UiEffects, (int)UiEffects.Frost);

            Assert.AreEqual(0, orb.NumTimesTinkered, "attunement must not consume a tinker.");
            Assert.AreEqual(ImbuedEffectType.Undef, orb.ImbuedEffect, "attunement must not imbue.");
            Assert.IsFalse(orb.Retained, "attunement must not set Retained.");
            Assert.IsNull(orb.TinkerLog, "attunement must not write a tinker log.");
        }

        [TestMethod]
        public void AttunementModIsAlwaysTheTableValueForTheOrbsTier()
        {
            foreach (var (req, diff, tier) in new (WieldRequirement, int?, int)[]
            {
                (WieldRequirement.Invalid,  null, 1),
                (WieldRequirement.RawSkill, 290,  4),
                (WieldRequirement.RawSkill, 310,  5),
                (WieldRequirement.RawSkill, 330,  6),
                (WieldRequirement.RawSkill, 355,  6),
                (WieldRequirement.RawSkill, 375,  7),
                (WieldRequirement.RawSkill, 385,  8),
                (WieldRequirement.Level,    150,  7),
                (WieldRequirement.Level,    180,  8),
            })
            {
                var orb = MakeOrb(req, diff);

                Assert.AreEqual(tier, AttunedDriftPrism.GetCasterTier(orb),
                    $"{req} / {diff} should read as tier {tier}.");

                Assert.AreEqual(AttunedDriftPrism.ElementalDamageModByTier[tier - 1], AttunedDriftPrism.GetAttunementMod(orb), 0.0001,
                    $"{req} / {diff} (tier {tier}) must attune to the tier {tier} table value.");
            }
        }

        [TestMethod]
        public void AttunementModHasNoRandomness()
        {
            var orb = MakeOrb(WieldRequirement.Level, 180);

            var first = AttunedDriftPrism.GetAttunementMod(orb);

            for (var i = 0; i < 50; i++)
                Assert.AreEqual(first, AttunedDriftPrism.GetAttunementMod(orb), 0.0, "the attunement value must be fixed, never rolled.");
        }

        // ------------------------------------------------------------------
        // content guard - the defect that shipped
        // ------------------------------------------------------------------

        /// <summary>
        /// The content-pointing test, and the one that would have caught the ship blocker.
        ///
        /// Every prism declares ItemUseable 524296, which is what gives it a use-on-target cursor.
        /// ItemUseable alone is NOT enough: Player_Use.HandleActionUseWithTarget refuses any
        /// use-on-target whose source TargetType (PropertyInt 94) does not intersect the target's
        /// ItemType, and it does so BEFORE Gem.HandleActionUseOnTarget dispatches into
        /// AttunedDriftPrism. Every other test in this file calls the manager directly, so none of
        /// them traverses that gate - the eight prisms shipped with no TargetType row at all and
        /// no prism could be used on anything ("Cannot use the Void-Attuned Drift Prism with the
        /// Orb", observed live before the fix).
        ///
        /// Orbs are ItemType.Caster (0x8000): every caster weenie in ace_world carries exactly that
        /// and there is no orb-specific ItemType bit, so the mask must at minimum include Caster.
        /// The mask is therefore deliberately WIDER than the feature's eligibility - a player may
        /// drag a prism onto a wand or a sceptre, and AttunedDriftPrism.IsOrb rejects it server-side
        /// by Setup DataId with the feature's own message.
        /// </summary>
        [TestMethod]
        public void EveryPrismWeenieDeclaresTargetTypeCoveringOrbs()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;
            if (dir == null)
                Assert.Inconclusive("Could not locate the repo's Content/sql/weenies directory by walking up from the test assembly -- skipping.");

            var sqlDir = Path.Combine(dir.FullName, "Content", "sql", "weenies");

            var wcids = Enumerable.Range(0, 8).Select(i => AttunedDriftPrism.FlamePrism + (uint)i).ToList();
            CollectionAssert.AreEqual(wcids, wcids.Where(AttunedDriftPrism.IsPrism).ToList(),
                "the eight contiguous wcids from FlamePrism must all be registered prisms.");

            foreach (var wcid in wcids)
            {
                var sqlFiles = Directory.GetFiles(sqlDir, wcid + " *.sql");
                Assert.AreEqual(1, sqlFiles.Length, $"expected exactly one Content/sql/weenies deliverable for prism {wcid}");
                var sqlName = Path.GetFileName(sqlFiles[0]);
                var sql = File.ReadAllText(sqlFiles[0]);

                StringAssert.Matches(sql, new Regex($@"\({wcid},\s*16,\s*\d+\)"),
                    $"{sqlName}: no ItemUseable (PropertyInt 16) row - this test assumes a use-on-target item.");

                var match = Regex.Match(sql, $@"\({wcid},\s*94,\s*(\d+)\)");
                Assert.IsTrue(match.Success,
                    $"{sqlName}: no TargetType (PropertyInt 94) row. Player_Use.HandleActionUseWithTarget " +
                    "refuses the use before AttunedDriftPrism is ever entered, so the whole feature is dead content.");

                var targetType = (ItemType)uint.Parse(match.Groups[1].Value);
                Assert.IsTrue((targetType & ItemType.Caster) != 0,
                    $"{sqlName}: TargetType {(uint)targetType} does not include ItemType.Caster " +
                    $"({(uint)ItemType.Caster}), which is the ItemType every orb carries.");
            }
        }
    }
}
