using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The unlearn-time tier gate's pure half (<see cref="ClassAbilityTierGate"/>) against real registry
    /// definitions, plus a source pin that Player.UnlearnClassAbility runs it before charging the fee.
    ///
    /// READS NO PropertyManager KEY: the requirements are parameters, so this passes alone or in any order.
    /// </summary>
    [TestClass]
    public class ClassAbilityTierGateTests
    {
        private static IEnumerable<ClassAbilityDefinition> Defs(ClassAbilityClass c, int tier) =>
            ClassAbilityRegistry.Abilities.Values
                .Where(d => d.AbilityClass == c && d.Tier == tier)
                .OrderBy(d => (int)d.Id);

        private static int Full(ClassAbilityDefinition d) => d.CumulativeCost(d.MaxRank);

        /// <summary>A class with at least one T1, one T2 and two T3 abilities, all with a positive full cost.</summary>
        private static (ClassAbilityClass cls, ClassAbilityDefinition t1, ClassAbilityDefinition t2,
            ClassAbilityDefinition t3a, ClassAbilityDefinition t3b) PickClass(ClassAbilityClass? exclude = null)
        {
            foreach (var c in Enum.GetValues(typeof(ClassAbilityClass)).Cast<ClassAbilityClass>())
            {
                if (c == ClassAbilityClass.None || c == exclude)
                    continue;

                var t1 = Defs(c, 1).FirstOrDefault(d => Full(d) > 0);
                var t2 = Defs(c, 2).FirstOrDefault(d => Full(d) > 0);
                var t3 = Defs(c, 3).Where(d => Full(d) > 0).Take(2).ToList();

                if (t1 != null && t2 != null && t3.Count == 2)
                    return (c, t1, t2, t3[0], t3[1]);
            }

            Assert.Fail("no class with a T1, a T2 and two T3 abilities in the registry");
            return default;
        }

        private static Dictionary<ClassAbilityId, int> Own(params ClassAbilityDefinition[] defs) =>
            defs.ToDictionary(d => d.Id, d => d.MaxRank);

        [TestMethod]
        public void GrandfatheredAcidProc_DoesNotBlockUnlearningATier1Rogue()
        {
            // Acid Proc moved to Rogue T3 (reprice wave 8) and existing owners were grandfathered. A rank-3 owner
            // whose lower-tier spend is under the T3 requirement was already failing the gate BEFORE any removal,
            // so unlearning a T1 ability strands nothing new.
            var acid = ClassAbilityRegistry.Abilities[ClassAbilityId.AcidProc];
            Assert.AreEqual(ClassAbilityClass.Rogue, acid.AbilityClass);
            Assert.AreEqual(3, acid.Tier);

            var t1 = Defs(ClassAbilityClass.Rogue, 1).First(d => Full(d) > 0);
            const int tier2Required = 5, tier3Required = 15;
            Assert.IsTrue(Full(t1) < tier3Required, "control: the lone T1 leaves lower-tier spend under the T3 requirement");

            var before = new Dictionary<ClassAbilityId, int> { [ClassAbilityId.AcidProc] = acid.MaxRank, [t1.Id] = t1.MaxRank };
            var after = new Dictionary<ClassAbilityId, int> { [ClassAbilityId.AcidProc] = acid.MaxRank };

            var stranded = ClassAbilityTierGate.FindNewlyStrandedDependent(before, after, ClassAbilityClass.Rogue,
                tier2Required, tier3Required, out _, out _);

            Assert.IsNull(stranded);
        }

        [TestMethod]
        public void RemovingTheT1_StrandsAnOwnedT3()
        {
            var p = PickClass();
            var req3 = Full(p.t1);

            var stranded = ClassAbilityTierGate.FindStrandedDependent(Own(p.t3a), p.cls, 1, req3, out var spent, out var required);

            Assert.AreSame(p.t3a, stranded);
            Assert.AreEqual(0, spent);
            Assert.AreEqual(req3, required);
        }

        [TestMethod]
        public void EnoughRemainingLowerTierSpend_ReturnsNull()
        {
            var p = PickClass();

            var stranded = ClassAbilityTierGate.FindStrandedDependent(Own(p.t1, p.t3a), p.cls, 1, Full(p.t1), out _, out _);

            Assert.IsNull(stranded);
        }

        [TestMethod]
        public void TwoT3s_DoNotPropEachOtherUp()
        {
            var p = PickClass();
            var req3 = Full(p.t3a) + Full(p.t3b);

            var stranded = ClassAbilityTierGate.FindStrandedDependent(Own(p.t3a, p.t3b), p.cls, 1, req3, out var spent, out _);

            Assert.IsNotNull(stranded);
            Assert.IsTrue(stranded.Id == p.t3a.Id || stranded.Id == p.t3b.Id);
            Assert.AreEqual(0, spent, "same-tier spend must not count");
        }

        [TestMethod]
        public void T2StrandedBelowItsRequirement_IsReturned()
        {
            var p = PickClass();
            var req2 = Full(p.t1);

            var stranded = ClassAbilityTierGate.FindStrandedDependent(Own(p.t2), p.cls, req2, 1, out _, out var required);

            Assert.AreSame(p.t2, stranded);
            Assert.AreEqual(req2, required, "a T2 uses the tier2 requirement");
        }

        [TestMethod]
        public void AnotherClassesT3_IsUnaffected()
        {
            var p = PickClass();
            var other = PickClass(exclude: p.cls);

            var stranded = ClassAbilityTierGate.FindStrandedDependent(Own(other.t3a), p.cls, 1, 1, out _, out _);

            Assert.IsNull(stranded);
        }

        [TestMethod]
        public void RemovingTheT3Itself_ReturnsNull()
        {
            var p = PickClass();

            // the T3 is gone from the remaining set; only the T1 is left and it has no prerequisite
            var stranded = ClassAbilityTierGate.FindStrandedDependent(Own(p.t1), p.cls, 1, int.MaxValue, out _, out _);

            Assert.IsNull(stranded);
        }

        [TestMethod]
        public void NoneClass_IsNeverGated()
        {
            var p = PickClass();

            Assert.IsNull(ClassAbilityTierGate.FindStrandedDependent(Own(p.t3a), ClassAbilityClass.None, 1, int.MaxValue, out _, out _));
        }

        private static Dictionary<ClassAbilityId, int> Without(Dictionary<ClassAbilityId, int> ranks, ClassAbilityDefinition d)
        {
            var copy = new Dictionary<ClassAbilityId, int>(ranks);
            copy.Remove(d.Id);
            return copy;
        }

        private static ClassAbilityDefinition Newly(Dictionary<ClassAbilityId, int> before, ClassAbilityDefinition removed,
            ClassAbilityClass cls, int req2, int req3) =>
            ClassAbilityTierGate.FindNewlyStrandedDependent(before, Without(before, removed), cls, req2, req3, out _, out _);

        [TestMethod]
        public void Newly_RemovingOneOfTwoStrandedT3s_IsAllowed()
        {
            var p = PickClass();
            var before = Own(p.t3a, p.t3b);

            Assert.IsNull(Newly(before, p.t3a, p.cls, 1, 1));
            Assert.IsNull(Newly(before, p.t3b, p.cls, 1, 1));
        }

        [TestMethod]
        public void Newly_T1AtExactlyTheRequirement_RefusesItsRemoval()
        {
            var p = PickClass();
            var before = Own(p.t1, p.t3a);

            Assert.AreSame(p.t3a, Newly(before, p.t1, p.cls, 1, Full(p.t1)));
        }

        [TestMethod]
        public void Newly_AlreadyStrandedT3_DoesNotBlockAnUnrelatedT1()
        {
            var p = PickClass();
            var before = Own(p.t1, p.t3a);

            // the T3 already fails with the T1 owned, so dropping the T1 strands nothing new
            Assert.IsNull(Newly(before, p.t1, p.cls, 1, Full(p.t1) + 1));
        }

        [TestMethod]
        public void Newly_StrandedT2AndT3FromOneRemoval_ReturnsTheT2()
        {
            var p = PickClass();
            // higher tier inserted FIRST, so only an explicit OrderBy(Tier) puts the T2 ahead of the T3
            var before = new Dictionary<ClassAbilityId, int>
            {
                [p.t3a.Id] = p.t3a.MaxRank, [p.t2.Id] = p.t2.MaxRank, [p.t1.Id] = p.t1.MaxRank,
            };

            var stranded = ClassAbilityTierGate.FindNewlyStrandedDependent(before, Without(before, p.t1), p.cls,
                Full(p.t1), Full(p.t1) + Full(p.t2), out var spent, out var required);

            Assert.AreSame(p.t2, stranded, "lowest Tier first");
            Assert.AreEqual(0, spent);
            Assert.AreEqual(Full(p.t1), required);
        }

        [TestMethod]
        public void Newly_SameTierTie_BreaksOnTheLowestId()
        {
            var p = PickClass();
            var lowest = new[] { p.t3a, p.t3b }.OrderBy(d => (int)d.Id).First();
            var highest = new[] { p.t3a, p.t3b }.OrderByDescending(d => (int)d.Id).First();

            // higher Id inserted FIRST, so only an explicit ThenBy(Id) returns the lower one
            var before = new Dictionary<ClassAbilityId, int>
            {
                [highest.Id] = highest.MaxRank, [lowest.Id] = lowest.MaxRank, [p.t1.Id] = p.t1.MaxRank,
            };

            Assert.AreSame(lowest, Newly(before, p.t1, p.cls, 1, Full(p.t1)));
        }

        [TestMethod]
        public void PartialRankT1JustUnderTheRequirement_Strands()
        {
            foreach (var c in Enum.GetValues(typeof(ClassAbilityClass)).Cast<ClassAbilityClass>().Where(c => c != ClassAbilityClass.None))
            {
                var t1 = Defs(c, 1).FirstOrDefault(d => d.MaxRank >= 2 && d.CumulativeCost(d.MaxRank) > d.CumulativeCost(d.MaxRank - 1));
                var t3 = Defs(c, 3).FirstOrDefault();
                if (t1 == null || t3 == null)
                    continue;

                var ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = t1.MaxRank - 1, [t3.Id] = t3.MaxRank };

                Assert.AreSame(t3, ClassAbilityTierGate.FindStrandedDependent(ranks, c, 1, t1.CumulativeCost(t1.MaxRank), out var spent, out _),
                    "a partial rank must count at its own cumulative cost, not the full cost");
                Assert.AreEqual(t1.CumulativeCost(t1.MaxRank - 1), spent);
                Assert.IsNull(ClassAbilityTierGate.FindStrandedDependent(ranks, c, 1, t1.CumulativeCost(t1.MaxRank - 1), out _, out _));
                return;
            }

            Assert.Fail("no class with a multi-rank T1 and a T3");
        }

        [TestMethod]
        public void MissingRegistryId_IsSkipped()
        {
            var p = PickClass();
            var ranks = Own(p.t1);
            ranks[(ClassAbilityId)(-424242)] = 5;

            Assert.IsFalse(ClassAbilityRegistry.Abilities.ContainsKey((ClassAbilityId)(-424242)));
            Assert.IsNull(ClassAbilityTierGate.FindStrandedDependent(ranks, p.cls, 1, 1, out _, out _));
        }

        [TestMethod]
        public void RankZero_IsSkipped()
        {
            var p = PickClass();
            var ranks = new Dictionary<ClassAbilityId, int> { [p.t3a.Id] = 0 };

            Assert.IsNull(ClassAbilityTierGate.FindStrandedDependent(ranks, p.cls, 1, int.MaxValue, out _, out _));
        }

        [TestMethod]
        public void TierToRequirementMapping_T2UsesReq2_T3UsesReq3()
        {
            var p = PickClass();
            var ranks = Own(p.t1, p.t2, p.t3a);

            // req2 met, req3 not: only the T3 can fail
            var a = ClassAbilityTierGate.FindStrandedDependent(ranks, p.cls, Full(p.t1), Full(p.t1) + Full(p.t2) + 1, out _, out var reqA);
            Assert.AreSame(p.t3a, a);
            Assert.AreEqual(Full(p.t1) + Full(p.t2) + 1, reqA);

            // req2 not met, req3 met: only the T2 can fail
            var b = ClassAbilityTierGate.FindStrandedDependent(ranks, p.cls, Full(p.t1) + 1, Full(p.t1), out _, out var reqB);
            Assert.AreSame(p.t2, b);
            Assert.AreEqual(Full(p.t1) + 1, reqB);
        }

        [TestMethod]
        public void UnlearnClassAbility_RunsTheTierGate_AfterTheRefusals_AndBeforeTheFee()
        {
            var text = File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(),
                "Source", "ACE.Server", "WorldObjects", "Player_ClassAbilities.cs"));

            // The refusals live in ValidateClassAbilityRankRemoval, the core UnlearnClassAbility shares with
            // LowerClassAbilityRank and the web panel's CanLower; the fee is charged by the caller only after it.
            var start = text.IndexOf("public bool UnlearnClassAbility(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "UnlearnClassAbility not found");

            var validate = text.IndexOf("ValidateClassAbilityRankRemoval(skill, lowerOneRank: false, reason,", start, StringComparison.Ordinal);
            var charge = text.IndexOf("TryChargeClassAbilityRemovalFee(out error)", start, StringComparison.Ordinal);
            var refund = text.IndexOf("refunded = skill.CumulativeCost(rank);", start, StringComparison.Ordinal);

            Assert.IsTrue(validate >= 0 && validate < charge, "every refusal must run before the fee is charged");
            Assert.IsTrue(charge >= 0 && charge < refund, "the fee is charged before anything is refunded");

            var core = text.IndexOf("private bool ValidateClassAbilityRankRemoval(", StringComparison.Ordinal);
            Assert.IsTrue(core >= 0, "ValidateClassAbilityRankRemoval not found");
            var coreEnd = text.IndexOf("private static long ClassAbilityRemovalFee()", core, StringComparison.Ordinal);
            Assert.IsTrue(coreEnd > core, "could not isolate ValidateClassAbilityRankRemoval");

            var body = text.Substring(core, coreEnd - core);

            int At(string needle) => body.IndexOf(needle, StringComparison.Ordinal);

            var pvp = At("if (IsInPvpMatch)");
            var pending = At("ClassAbilityRepricePendingFor(skill.Id)");
            var gate = At("ClassAbilityRankRemoval.StrandRefusal(");
            var respecExempt = At("reason != CapLedgerReason.Respec");

            Assert.IsTrue(pvp >= 0 && pvp < pending, "PvP refusal must come first");
            Assert.IsTrue(pending >= 0 && pending < respecExempt, "reprice refusal must precede the tier gate");
            Assert.IsTrue(respecExempt >= 0 && respecExempt < gate, "the gate must be skipped for a full respec");
            Assert.AreEqual(-1, At("TrySpendLuminanceIncludingBank"), "the shared core must never charge anything");
        }
    }
}
