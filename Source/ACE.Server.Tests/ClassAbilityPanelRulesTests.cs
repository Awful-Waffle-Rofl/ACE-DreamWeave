using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The class ability web panel's pure rules: the one-rank lower's refund and strand refusal, the change
    /// classification, the revision counter, the ability set, the tier rows, the snapshot projection, and the
    /// location sentences (with the vault summon's own messages pinned unchanged).
    ///
    /// The four account_vault_* keys the location tests read are seeded in [TestInitialize] and put back to their
    /// shipped defaults in [TestCleanup] (PropertyManager is a process-wide static cache). The one test that reads the
    /// four class_ability_tier{2,3}_{cap,spent}_required keys seeds them itself and restores the defaults in finally.
    /// </summary>
    [TestClass]
    public class ClassAbilityPanelRulesTests
    {
        private static readonly string[] VaultKeys = { "account_vault_denylist", "account_vault_allowlist", "account_vault_allowlist_name" };

        [TestInitialize]
        public void Seed()
        {
            PropertyManager.ModifyString("account_vault_denylist", "");
            PropertyManager.ModifyString("account_vault_allowlist", "01F5@1");
            PropertyManager.ModifyString("account_vault_allowlist_name", "the Marketplace");
            Assert.IsTrue(PropertyManager.ModifyBool("account_vault_housing_allowed", true), "account_vault_housing_allowed is missing from DefaultBooleanProperties");
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (var key in VaultKeys)
                PropertyManager.ModifyString(key, DefaultPropertyManager.DefaultStringProperties[key].Item);

            PropertyManager.ModifyBool("account_vault_housing_allowed", DefaultPropertyManager.DefaultBooleanProperties["account_vault_housing_allowed"].Item);
        }

        private static Position MakeLocation(ushort landblock) => new Position
        {
            LandblockId = new LandblockId((uint)landblock << 16),
            PositionX = 50f,
            PositionY = 50f,
            RotationW = 1f,
        };

        // ---- the one-rank lower: refund and strand refusal ----

        /// <summary>A class with a Tier 1 ability of max rank >= 2 and a Tier 2 ability, for the strand cases.</summary>
        private static (ClassAbilityDefinition t1, ClassAbilityDefinition t2) Pair()
        {
            foreach (var cls in ClassAbilityPanelModel.ClassOrder)
            {
                var t1 = ClassAbilityPanelModel.AbilitySet.FirstOrDefault(d => d.AbilityClass == cls && d.Tier == 1 && d.MaxRank >= 2);
                var t2 = ClassAbilityPanelModel.AbilitySet.FirstOrDefault(d => d.AbilityClass == cls && d.Tier == 2);
                if (t1 != null && t2 != null)
                    return (t1, t2);
            }

            Assert.Fail("no class has a multi-rank Tier 1 ability and a Tier 2 ability");
            return default;
        }

        [TestMethod]
        public void Refund_LowerIsTheCostOfTheRankRemoved_UnlearnIsCumulative()
        {
            var skill = ClassAbilityPanelModel.AbilitySet.First(d => d.MaxRank >= 3);

            Assert.AreEqual(skill.CostPerRank[2], ClassAbilityRankRemoval.Refund(skill, 3, 2), "3 -> 2 refunds rank 3's cost");
            Assert.AreEqual(skill.CostPerRank[1], ClassAbilityRankRemoval.Refund(skill, 2, 1), "2 -> 1 refunds rank 2's cost");
            Assert.AreEqual(skill.CostPerRank[0], ClassAbilityRankRemoval.Refund(skill, 1, 0), "1 -> 0 refunds rank 1's cost");
            Assert.AreEqual(skill.CumulativeCost(3), ClassAbilityRankRemoval.Refund(skill, 3, 0), "an unlearn refunds every rank, as UnlearnClassAbility always did");
            Assert.AreEqual(0, ClassAbilityRankRemoval.Refund(skill, 2, 2));
        }

        [TestMethod]
        public void Refund_LoweringOneRankAtATime_SumsToTheUnlearnRefund()
        {
            foreach (var skill in ClassAbilityPanelModel.AbilitySet)
            {
                var stepped = 0;
                for (var rank = skill.MaxRank; rank >= 1; rank--)
                    stepped += ClassAbilityRankRemoval.Refund(skill, rank, rank - 1);

                Assert.AreEqual(skill.CumulativeCost(skill.MaxRank), stepped, skill.Name);
            }
        }

        [TestMethod]
        public void StrandRefusal_LoweringTheRankThatHoldsATier2Up_IsRefused()
        {
            var (t1, t2) = Pair();

            // exactly enough lower-tier spend with t1 maxed: tier 2 needs exactly that much
            var required = t1.CumulativeCost(t1.MaxRank);
            var ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = t1.MaxRank, [t2.Id] = 1 };

            var lower = ClassAbilityRankRemoval.StrandRefusal(t1, ranks, t1.MaxRank - 1, required, 999);

            Assert.IsNotNull(lower, "one rank off t1 leaves t2 below its requirement");
            StringAssert.StartsWith(lower, $"You cannot lower {t1.DisplayName}: {t2.DisplayName} is a Tier 2 {t1.AbilityClass} skill");
            StringAssert.EndsWith(lower, $"Unlearn {t2.DisplayName} first.");

            Assert.AreEqual(t1.MaxRank, ranks[t1.Id], "the caller's ranks must not be modified");
        }

        [TestMethod]
        public void StrandRefusal_Unlearn_KeepsTheExactLegacySentence()
        {
            var (t1, t2) = Pair();
            var required = t1.CumulativeCost(t1.MaxRank);
            var ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = t1.MaxRank, [t2.Id] = 1 };

            var unlearn = ClassAbilityRankRemoval.StrandRefusal(t1, ranks, 0, required, 999);

            Assert.AreEqual(
                $"You cannot unlearn {t1.DisplayName}: {t2.DisplayName} is a Tier {t2.Tier} {t1.AbilityClass} skill that needs at least {required:N0} points spent on lower-tier {t1.AbilityClass} skills, and you would have {0:N0}. Unlearn {t2.DisplayName} first.",
                unlearn);
        }

        [TestMethod]
        public void StrandRefusal_WithSpendToSpare_OrOnTheDependentItself_IsAllowed()
        {
            var (t1, t2) = Pair();
            var required = t1.CumulativeCost(t1.MaxRank - 1);
            var ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = t1.MaxRank, [t2.Id] = 1 };

            Assert.IsNull(ClassAbilityRankRemoval.StrandRefusal(t1, ranks, t1.MaxRank - 1, required, 999), "the lower rank still meets the requirement");
            Assert.IsNull(ClassAbilityRankRemoval.StrandRefusal(t2, ranks, 0, required, 999), "removing the higher-tier ability itself never strands");
        }

        [TestMethod]
        public void StrandRefusal_AlreadyStrandedBefore_IsNotBlamedOnTheLower()
        {
            var (t1, t2) = Pair();
            var ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = t1.MaxRank, [t2.Id] = 1 };

            // the requirement is above anything t1 can supply, so t2 is already stranded
            Assert.IsNull(ClassAbilityRankRemoval.StrandRefusal(t1, ranks, t1.MaxRank - 1, 10_000, 10_000));
        }

        // ---- change classification ----

        [TestMethod]
        public void Classify_FollowsTheContract()
        {
            Assert.AreEqual(ClassAbilityPanelChangeKind.Raise, ClassAbilityPanelModel.Classify(0, 1));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Raise, ClassAbilityPanelModel.Classify(2, 3));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Lower, ClassAbilityPanelModel.Classify(3, 2));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Unlearn, ClassAbilityPanelModel.Classify(3, 0));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Unlearn, ClassAbilityPanelModel.Classify(1, 0), "rank 1 -> 0 is the unlearn of one rank");

            Assert.AreEqual(ClassAbilityPanelChangeKind.Invalid, ClassAbilityPanelModel.Classify(0, 0));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Invalid, ClassAbilityPanelModel.Classify(1, 3));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Invalid, ClassAbilityPanelModel.Classify(3, 1));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Invalid, ClassAbilityPanelModel.Classify(2, 2));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Invalid, ClassAbilityPanelModel.Classify(-1, 0));
            Assert.AreEqual(ClassAbilityPanelChangeKind.Invalid, ClassAbilityPanelModel.Classify(1, -1));
        }

        // ---- the world-thread eligibility and compare-and-set seam ----

        [TestMethod]
        public void IsEligible_EveryClauseRefuses()
        {
            const uint account = 77;

            Assert.IsTrue(ClassAbilityPanelModel.IsEligible(account, true, true, account, true, true, false), "control: all clear");

            Assert.IsFalse(ClassAbilityPanelModel.IsEligible(account, false, true, account, true, true, false), "not online");
            Assert.IsFalse(ClassAbilityPanelModel.IsEligible(account, true, false, account, true, true, false), "no session");
            Assert.IsFalse(ClassAbilityPanelModel.IsEligible(account, true, true, account + 1, true, true, false), "another account's session");
            Assert.IsFalse(ClassAbilityPanelModel.IsEligible(account, true, true, account, false, true, false), "not WorldConnected");
            Assert.IsFalse(ClassAbilityPanelModel.IsEligible(account, true, true, account, true, false, false), "the session holds another Player");
            Assert.IsFalse(ClassAbilityPanelModel.IsEligible(account, true, true, account, true, true, true), "logging out");
        }

        [TestMethod]
        public void IsStale_IsAnyDifferenceFromTheLiveRank()
        {
            Assert.IsFalse(ClassAbilityPanelModel.IsStale(2, 2));
            Assert.IsTrue(ClassAbilityPanelModel.IsStale(2, 1));
            Assert.IsTrue(ClassAbilityPanelModel.IsStale(2, 3));
            Assert.IsTrue(ClassAbilityPanelModel.IsStale(0, 1));
        }

        // ---- the revision ----

        [TestMethod]
        public void Revision_ChangesOnEveryBump_AndIsEpochColonCounter()
        {
            var counter = new ClassAbilityRevisionCounter("feed");

            Assert.AreEqual("feed:0", counter.Current);
            counter.Bump();
            Assert.AreEqual("feed:1", counter.Current);
            counter.Bump();
            Assert.AreEqual("feed:2", counter.Current);
        }

        [TestMethod]
        public void Revision_TwoInstances_NeverShareARevision()
        {
            var a = new ClassAbilityRevisionCounter();
            var b = new ClassAbilityRevisionCounter();

            Assert.AreNotEqual(a.Current, b.Current, "a relog is a new Player, so it must answer a new revision");
            Assert.AreNotEqual(ClassAbilityPanelModel.OfflineRevision, a.Current);
            StringAssert.Matches(a.Current, new Regex("^[0-9a-f]{12}:0$"));
        }

        [TestMethod]
        public void Revision_BumpsFromManyThreads_AreAllCounted()
        {
            var counter = new ClassAbilityRevisionCounter("x");

            Parallel.For(0, 10_000, _ => counter.Bump());

            Assert.AreEqual("x:10000", counter.Current);
        }

        // ---- the ability set ----

        [TestMethod]
        public void AbilitySet_IsHomedImplementedAndRegistered()
        {
            var retired = RetiredClassAbilities.All().Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var def in ClassAbilityPanelModel.AbilitySet)
            {
                Assert.AreNotEqual(ClassAbilityClass.None, def.AbilityClass, def.Name);
                Assert.IsTrue(def.Implemented, def.Name);
                Assert.IsTrue(ClassAbilityRegistry.Abilities.ContainsKey(def.Id), def.Name);
                Assert.IsFalse(retired.Contains(def.Name), def.Name);
                Assert.IsTrue(def.Tier >= 1 && def.Tier <= 3, def.Name);
            }

            Assert.AreEqual(ClassAbilityPanelModel.AbilitySet.Count, ClassAbilityPanelModel.AbilitySet.Select(d => d.Id).Distinct().Count());
        }

        /// <summary>
        /// The independent derivation of the set: the union of every class trainer's PropertyString 9008
        /// (ClassAbilityTrainerAbilities) list in the committed content. They agree exactly today, 104 abilities;
        /// a raise runs the trainer's own learn, so an ability the panel offers that no trainer sells (or the
        /// reverse) is a finding, not a detail.
        /// </summary>
        [TestMethod]
        public void AbilitySet_EqualsTheUnionOfTheTrainerLists_104()
        {
            var root = ClassAbilityAffinityDeclarationTests.RepoRoot();
            var trainers = new HashSet<string>(StringComparer.Ordinal);
            var lists = 0;

            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Content"), "*.sql", SearchOption.AllDirectories))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\((\d+), 9008, '([^']*)'"))
                {
                    lists++;
                    foreach (var def in ClassAbilityTrainer.ParseTrainerAbilities(m.Groups[2].Value))
                        trainers.Add(def.Name);
                }
            }

            Assert.AreEqual(8, lists, "one trainer list per class");

            var set = ClassAbilityPanelModel.AbilitySet.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

            CollectionAssert.AreEquivalent(trainers.OrderBy(n => n).ToList(), set.OrderBy(n => n).ToList(),
                $"panel only: [{string.Join(",", set.Except(trainers))}]; trainers only: [{string.Join(",", trainers.Except(set))}]");
            Assert.AreEqual(104, set.Count);
        }

        [TestMethod]
        public void AbilitySet_EveryRankHasATrainerToken()
        {
            // the trainer also refuses "not available yet" when no token resolves; for the panel set it never does
            foreach (var def in ClassAbilityPanelModel.AbilitySet)
                for (var rank = 1; rank <= def.MaxRank; rank++)
                    Assert.IsTrue(ClassAbilityTokenCatalog.TryResolve(def.Id, rank, out _), $"{def.Name} rank {rank}");
        }

        [TestMethod]
        public void TryGetAbility_AcceptsOnlyThePanelSet()
        {
            var first = ClassAbilityPanelModel.AbilitySet[0];
            Assert.IsTrue(ClassAbilityPanelModel.TryGetAbility(first.Name, out var found));
            Assert.AreSame(first, found);

            var unhomed = ClassAbilityRegistry.Abilities.Values.First(d => d.AbilityClass == ClassAbilityClass.None);
            Assert.IsFalse(ClassAbilityPanelModel.TryGetAbility(unhomed.Name, out _), "registered but unhomed");
            Assert.IsFalse(ClassAbilityPanelModel.TryGetAbility("no_such_ability", out _));
            Assert.IsFalse(ClassAbilityPanelModel.TryGetAbility(null, out _));
            Assert.IsFalse(ClassAbilityPanelModel.TryGetAbility(new string('a', 65), out _));
        }

        // ---- tier rows ----

        [TestMethod]
        public void CheckTierUnlock_EarnedFirstThenSpent()
        {
            Assert.AreEqual(ClassAbilityTierGate.TierUnlockShortfall.Earned, ClassAbilityTierGate.CheckTierUnlock(2, 0, 3, 5));
            Assert.AreEqual(ClassAbilityTierGate.TierUnlockShortfall.Spent, ClassAbilityTierGate.CheckTierUnlock(3, 4, 3, 5));
            Assert.AreEqual(ClassAbilityTierGate.TierUnlockShortfall.None, ClassAbilityTierGate.CheckTierUnlock(3, 5, 3, 5));
        }

        [TestMethod]
        public void TierRequirement_Sentences()
        {
            var req = new ClassAbilityPanelTierRequirements(3, 5, 8, 15);

            Assert.IsNull(ClassAbilityPanelModel.TierRequirement(ClassAbilityClass.Archer, 1, 0, 0, req));
            Assert.AreEqual("Tier 2 Archer abilities unlock once you have earned at least 3 class ability points (you have earned 2).",
                ClassAbilityPanelModel.TierRequirement(ClassAbilityClass.Archer, 2, 2, 0, req));
            Assert.AreEqual("Tier 3 Rogue abilities unlock once you have spent at least 15 points on Rogue abilities (you have spent 9).",
                ClassAbilityPanelModel.TierRequirement(ClassAbilityClass.Rogue, 3, 20, 9, req));
            Assert.IsNull(ClassAbilityPanelModel.TierRequirement(ClassAbilityClass.Rogue, 3, 20, 15, req));
        }

        // ---- the snapshot ----

        private sealed class FakeSubject : IClassAbilityPanelSubject
        {
            public uint CharacterGuid => 0x50000B01;
            public string Name => "Subject";
            public int Level => 275;
            public string Revision => "rev:4";
            public int PointsAvailable => 7;
            public int PointsEarned => 40;
            public IReadOnlyDictionary<ClassAbilityId, int> Ranks { get; set; } = new Dictionary<ClassAbilityId, int>();
            public int RaiseAsks;
            public int LowerAsks;

            public bool CanRaise(ClassAbilityDefinition ability, out string refusal)
            {
                RaiseAsks++;
                refusal = "raise no " + ability.Name;
                return ability.Tier == 1;
            }

            public bool CanLower(ClassAbilityDefinition ability, out string refusal)
            {
                LowerAsks++;
                refusal = "lower no " + ability.Name;
                return false;
            }

            /// <summary>The handler readout stand-in; null (the default) means no handler readout.</summary>
            public Func<ClassAbilityDefinition, int, ClassAbilityReadout?> ReadoutFn { get; set; }
            public List<(ClassAbilityId Id, int Rank)> ReadoutAsks { get; } = new();

            public ClassAbilityReadout? Readout(ClassAbilityDefinition ability, int rank)
            {
                ReadoutAsks.Add((ability.Id, rank));
                return ReadoutFn?.Invoke(ability, rank);
            }
        }

        // ToReadoutRow needs the ability so a ZeroIsLimit ceiling is never read as uncapped; these readouts carry no ceilings.
        private static ClassAbilityPanelReadoutRow RowOf(ClassAbilityReadout? r) =>
            ClassAbilityPanelModel.ToReadoutRow(r, ClassAbilityRegistry.Abilities[ClassAbilityId.Parry]);

        private static ClassAbilityReadout SampleReadout(string capNote = "affinity cap") => new ClassAbilityReadout
        {
            HasValue = true,
            Skill = 10,
            Affinity = 3,
            Gear = 2,
            Effective = 14,
            Unit = "%",
            Prefix = "+",
            Label = "melee dmg",
            CapNote = capNote,
            Secondary = new[] { new ClassAbilityReadout { HasValue = true, Skill = 99, Effective = 99, Unit = "s" } },
        };

        private static readonly ClassAbilityPanelTierRequirements Req = new ClassAbilityPanelTierRequirements(3, 5, 8, 15);

        [TestMethod]
        public void Build_Writable_AsksTheSubjectsOwnValidatorsForEveryAbility()
        {
            var subject = new FakeSubject();

            var snap = ClassAbilityPanelModel.Build(subject, online: true, writeRefusal: null, Req);

            Assert.IsTrue(snap.Writable);
            Assert.IsNull(snap.WriteRefusal);
            Assert.AreEqual(104, snap.Abilities.Count);
            Assert.AreEqual(104, subject.RaiseAsks);
            Assert.AreEqual(104, subject.LowerAsks);

            foreach (var row in snap.Abilities)
            {
                Assert.AreEqual(row.Tier == 1, row.CanRaise, row.Id);
                Assert.AreEqual(row.CanRaise ? null : "raise no " + row.Id, row.RaiseRefusal, row.Id);
                Assert.IsFalse(row.CanLower);
                Assert.AreEqual("lower no " + row.Id, row.LowerRefusal);
            }
        }

        [TestMethod]
        public void Build_NotWritable_EveryFlagFalse_WithTheSameReason_AndNoValidatorAsked()
        {
            var subject = new FakeSubject();

            var snap = ClassAbilityPanelModel.Build(subject, online: true, ClassAbilityPanelModel.NotLaunchSessionRefusal, Req);

            Assert.IsFalse(snap.Writable);
            Assert.AreEqual(ClassAbilityPanelModel.NotLaunchSessionRefusal, snap.WriteRefusal);
            Assert.AreEqual(0, subject.RaiseAsks + subject.LowerAsks);
            Assert.IsTrue(snap.Abilities.All(r => !r.CanRaise && !r.CanLower
                && r.RaiseRefusal == ClassAbilityPanelModel.NotLaunchSessionRefusal && r.LowerRefusal == ClassAbilityPanelModel.NotLaunchSessionRefusal));
        }

        [TestMethod]
        public void Build_Offline_IsNeverWritable()
        {
            var snap = ClassAbilityPanelModel.Build(new FakeSubject(), online: false, writeRefusal: null, Req);

            Assert.IsFalse(snap.Online);
            Assert.IsFalse(snap.Writable);
            Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, snap.WriteRefusal);
        }

        [TestMethod]
        public void Build_RowsCarryRanksCostsTiersAndClassSpend()
        {
            var (t1, t2) = Pair();
            var subject = new FakeSubject { Ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = 2, [t2.Id] = 1 } };

            var snap = ClassAbilityPanelModel.Build(subject, online: true, writeRefusal: null, Req);

            Assert.AreEqual(subject.CharacterGuid, snap.CharacterGuid);
            Assert.AreEqual("rev:4", snap.Revision);
            Assert.AreEqual(7, snap.PointsAvailable);
            Assert.AreEqual(40, snap.PointsEarned);

            var row = snap.Abilities.Single(r => r.Id == t1.Name);
            Assert.AreEqual(2, row.Rank);
            Assert.AreEqual(t1.DisplayName, row.Name);
            Assert.AreEqual(t1.AbilityClass.ToString(), row.Class);
            Assert.AreEqual(t1.Category, row.Category);
            Assert.AreEqual(t1.MaxRank, row.CostPerRank.Length);
            CollectionAssert.AreEqual(t1.CostPerRank.Take(t1.MaxRank).ToArray(), row.CostPerRank);
            Assert.IsTrue(snap.Abilities.Where(r => r.Id != t1.Name && r.Id != t2.Name).All(r => r.Rank == 0), "an unowned ability reads rank 0");

            CollectionAssert.AreEqual(ClassAbilityPanelModel.ClassOrder.Select(c => c.ToString()).ToList(), snap.Classes.Select(c => c.Class).ToList());
            Assert.AreEqual(8, snap.Classes.Count);

            var cls = snap.Classes.Single(c => c.Class == t1.AbilityClass.ToString());
            Assert.AreEqual(t1.CumulativeCost(2) + t2.CumulativeCost(1), cls.PointsSpent);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, cls.Tiers.Select(t => t.Tier).ToArray());
            Assert.IsTrue(cls.Tiers[0].Unlocked);
            Assert.IsNull(cls.Tiers[0].Requirement);
            Assert.AreEqual(cls.Tiers[1].Unlocked, cls.Tiers[1].Requirement == null);
        }

        [TestMethod]
        public void Build_TierRows_CarryBothThresholds_ZeroForTier1()
        {
            var snap = ClassAbilityPanelModel.Build(new FakeSubject(), online: true, writeRefusal: null, Req);

            foreach (var cls in snap.Classes)
            {
                Assert.AreEqual(0, cls.Tiers[0].EarnedRequired, cls.Class);
                Assert.AreEqual(0, cls.Tiers[0].SpentRequired, cls.Class);
                Assert.AreEqual(3, cls.Tiers[1].EarnedRequired, cls.Class);
                Assert.AreEqual(5, cls.Tiers[1].SpentRequired, cls.Class);
                Assert.AreEqual(8, cls.Tiers[2].EarnedRequired, cls.Class);
                Assert.AreEqual(15, cls.Tiers[2].SpentRequired, cls.Class);
            }
        }

        private static readonly string[] TierKeys =
        {
            ClassAbilityTierGate.Tier2EarnedKey, ClassAbilityTierGate.Tier2SpentKey, ClassAbilityTierGate.Tier3EarnedKey, ClassAbilityTierGate.Tier3SpentKey,
        };

        [TestMethod]
        public void LiveRequirements_ReadTheTierGatesOwnKeys_IntoTheTierRows()
        {
            CollectionAssert.AreEqual(
                new[] { "class_ability_tier2_cap_required", "class_ability_tier2_spent_required", "class_ability_tier3_cap_required", "class_ability_tier3_spent_required" },
                TierKeys);

            try
            {
                PropertyManager.ModifyLong(ClassAbilityTierGate.Tier2EarnedKey, 11);
                PropertyManager.ModifyLong(ClassAbilityTierGate.Tier2SpentKey, 12);
                PropertyManager.ModifyLong(ClassAbilityTierGate.Tier3EarnedKey, 21);
                PropertyManager.ModifyLong(ClassAbilityTierGate.Tier3SpentKey, 22);

                var snap = ClassAbilityPanelModel.Build(new FakeSubject(), online: true, writeRefusal: null, ClassAbilityPanelTierRequirements.Live());
                var tiers = snap.Classes[0].Tiers;

                Assert.AreEqual((0, 0), (tiers[0].EarnedRequired, tiers[0].SpentRequired));
                Assert.AreEqual((11, 12), (tiers[1].EarnedRequired, tiers[1].SpentRequired));
                Assert.AreEqual((21, 22), (tiers[2].EarnedRequired, tiers[2].SpentRequired));
            }
            finally
            {
                foreach (var key in TierKeys)
                    PropertyManager.ModifyLong(key, DefaultPropertyManager.DefaultLongProperties[key].Item);
            }
        }

        [TestMethod]
        public void Snapshot_Serializes_TheContractsFieldNames_WithExplicitNulls()
        {
            var snap = ClassAbilityPanelModel.Build(new FakeSubject(), online: true, writeRefusal: null, Req);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(snap, MarketApiHost.Json));
            var root = doc.RootElement;

            foreach (var key in new[] { "character_guid", "name", "level", "online", "revision", "writable", "write_refusal", "points_available", "points_earned", "classes", "abilities" })
                Assert.IsTrue(root.TryGetProperty(key, out _), key);

            Assert.AreEqual(JsonValueKind.Null, root.GetProperty("write_refusal").ValueKind, "null, not omitted");

            var cls = root.GetProperty("classes")[0];
            foreach (var key in new[] { "class", "points_spent", "tiers" })
                Assert.IsTrue(cls.TryGetProperty(key, out _), key);
            Assert.AreEqual(JsonValueKind.Null, cls.GetProperty("tiers")[0].GetProperty("requirement").ValueKind);
            Assert.AreEqual(0, cls.GetProperty("tiers")[0].GetProperty("earned_required").GetInt32());
            Assert.AreEqual(0, cls.GetProperty("tiers")[0].GetProperty("spent_required").GetInt32());
            Assert.AreEqual(8, cls.GetProperty("tiers")[2].GetProperty("earned_required").GetInt32());
            Assert.AreEqual(15, cls.GetProperty("tiers")[2].GetProperty("spent_required").GetInt32());

            var ability = root.GetProperty("abilities").EnumerateArray().First(a => a.GetProperty("can_raise").GetBoolean());
            foreach (var key in new[] { "id", "name", "class", "tier", "category", "max_rank", "rank", "cost_per_rank", "can_raise", "raise_refusal", "can_lower", "lower_refusal" })
                Assert.IsTrue(ability.TryGetProperty(key, out _), key);
            Assert.AreEqual(JsonValueKind.Null, ability.GetProperty("raise_refusal").ValueKind);
        }

        // ---- the readout ----

        [TestMethod]
        public void Build_Online_OwnedAbilityReadout_SerializesTheReadoutKeys()
        {
            var (t1, t2) = Pair();
            var subject = new FakeSubject
            {
                Ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = 2, [t2.Id] = 1 },
                ReadoutFn = (def, rank) => def.Id == t1.Id ? SampleReadout() : null,
            };

            var snap = ClassAbilityPanelModel.Build(subject, online: true, writeRefusal: null, Req);

            // asked only for the owned ranks, at the owned rank
            CollectionAssert.AreEquivalent(new[] { (t1.Id, 2), (t2.Id, 1) }, subject.ReadoutAsks);
            Assert.IsNull(snap.Abilities.Single(r => r.Id == t2.Name).Readout, "a handler with no readout carries none");

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(snap, MarketApiHost.Json));
            var rows = doc.RootElement.GetProperty("abilities").EnumerateArray().ToList();
            var readout = rows.Single(r => r.GetProperty("id").GetString() == t1.Name).GetProperty("readout");

            CollectionAssert.AreEquivalent(new[] { "prefix", "base", "effective", "unit", "label", "capped", "affinity", "gear", "ceilings" },
                readout.EnumerateObject().Select(p => p.Name).ToArray(), "exactly these keys; secondaries are not carried");
            Assert.AreEqual("+", readout.GetProperty("prefix").GetString());
            Assert.AreEqual(10d, readout.GetProperty("base").GetDouble(), "base is the rank's own Skill term");
            Assert.AreEqual(14d, readout.GetProperty("effective").GetDouble());
            Assert.AreEqual("%", readout.GetProperty("unit").GetString());
            Assert.AreEqual("melee dmg", readout.GetProperty("label").GetString());
            Assert.IsTrue(readout.GetProperty("capped").GetBoolean());

            Assert.IsTrue(rows.Where(r => r.GetProperty("id").GetString() != t1.Name).All(r => !r.TryGetProperty("readout", out _)),
                "no readout means the key is absent");
        }

        [TestMethod]
        public void Build_Offline_CarriesNoReadout_AndNeverAsks()
        {
            var (t1, _) = Pair();
            var subject = new FakeSubject
            {
                Ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = 2 },
                ReadoutFn = (def, rank) => SampleReadout(),
            };

            var snap = ClassAbilityPanelModel.Build(subject, online: false, writeRefusal: null, Req);

            Assert.AreEqual(0, subject.ReadoutAsks.Count);
            Assert.IsTrue(snap.Abilities.All(r => r.Readout == null));
            StringAssert.DoesNotMatch(JsonSerializer.Serialize(snap, MarketApiHost.Json), new Regex("\"readout\""));

            // Static ceilings are not readings: an offline snapshot still carries them.
            var parry = snap.Abilities.Single(r => r.Id == ClassAbilityRegistry.Abilities[ClassAbilityId.Parry].Name);
            Assert.IsNotNull(parry.Ceilings);
            Assert.IsTrue(parry.Ceilings.Count > 0);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(snap, MarketApiHost.Json));
            var wire = doc.RootElement.GetProperty("abilities").EnumerateArray().Single(a => a.GetProperty("id").GetString() == parry.Id);
            Assert.AreEqual(JsonValueKind.Array, wire.GetProperty("ceilings").ValueKind);
        }

        [TestMethod]
        public void Build_ThrowingReadout_OmitsOnlyThatRow_AndTheReadStillAnswers()
        {
            var (t1, t2) = Pair();
            var subject = new FakeSubject
            {
                Ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = 2, [t2.Id] = 1 },
                ReadoutFn = (def, rank) => def.Id == t1.Id ? throw new InvalidOperationException("handler broke") : SampleReadout(capNote: null),
            };

            ClassAbilityPanelSnapshot snap = null;
            for (var i = 0; i < 2; i++) // a second read takes the already-logged branch and still answers
                snap = ClassAbilityPanelModel.Build(subject, online: true, writeRefusal: null, Req);

            Assert.AreEqual(104, snap.Abilities.Count);
            Assert.IsNull(snap.Abilities.Single(r => r.Id == t1.Name).Readout);

            var other = snap.Abilities.Single(r => r.Id == t2.Name).Readout;
            Assert.IsNotNull(other, "the other rows keep theirs");
            Assert.IsFalse(other.Capped, "no cap note means not capped");

            JsonSerializer.Serialize(snap, MarketApiHost.Json);
        }

        [TestMethod]
        public void ToReadoutRow_NothingToShow_IsNull_AndEmptyTextsAreNull()
        {
            Assert.IsNull(RowOf(null));
            Assert.IsNull(RowOf(new ClassAbilityReadout { HasValue = false, Skill = 5, Effective = 5, Unit = "%" }));
            Assert.IsNull(RowOf(new ClassAbilityReadout { HasValue = true, Skill = double.NaN, Effective = 5 }), "NaN would fail the whole serialize");
            Assert.IsNull(RowOf(new ClassAbilityReadout { HasValue = true, Skill = 5, Effective = double.PositiveInfinity }));

            var row = RowOf(new ClassAbilityReadout { HasValue = true, Skill = 50, Effective = 50, Prefix = "", Label = "", Unit = null });
            Assert.IsNull(row.Prefix);
            Assert.IsNull(row.Label);
            Assert.AreEqual(string.Empty, row.Unit, "unit is a string, never null");

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(row, MarketApiHost.Json));
            Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("prefix").ValueKind, "null, not omitted");
            Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("label").ValueKind, "null, not omitted");
            Assert.IsFalse(doc.RootElement.GetProperty("capped").GetBoolean());
        }

        [TestMethod]
        public void Build_ThrowingReadout_LogsOncePerAbility_AcrossReads()
        {
            // An ability no other test makes throw (Pair() picks a Tier 1 ability; this is the last ability of the last
            // class, a higher tier), so the process-wide log-once set cannot already hold it.
            var (t1, _) = Pair();
            var fresh = ClassAbilityPanelModel.AbilitySet[ClassAbilityPanelModel.AbilitySet.Count - 1];
            Assert.AreNotEqual(t1.Id, fresh.Id);
            Assert.AreEqual(0, ClassAbilityPanelModel.ReadoutFailureLogLines(fresh.Id), "control: nothing logged for this ability yet");

            var subject = new FakeSubject
            {
                Ranks = new Dictionary<ClassAbilityId, int> { [fresh.Id] = 1 },
                ReadoutFn = (def, rank) => throw new InvalidOperationException("handler broke"),
            };

            ClassAbilityPanelModel.Build(subject, online: true, writeRefusal: null, Req);
            Assert.AreEqual(1, ClassAbilityPanelModel.ReadoutFailureLogLines(fresh.Id), "the first failure logs");

            ClassAbilityPanelModel.Build(subject, online: true, writeRefusal: null, Req);
            Assert.AreEqual(1, ClassAbilityPanelModel.ReadoutFailureLogLines(fresh.Id), "the second read does not log again");
            Assert.AreEqual(2, subject.ReadoutAsks.Count, "control: the handler was asked, and threw, on both reads");
        }

        [TestMethod]
        public void ToReadoutRow_PrimaryWithoutValue_ShowsTheFirstSecondary()
        {
            // Nether Bloom's shape: no primary number, the gear jump chance as the only secondary (NetherBloomAbility.GetReadout)
            var secondary = NetherBloomAbility.GearJumpChanceSecondary(0.25);
            Assert.IsTrue(secondary.HasValue, "control: an equipped mod yields the secondary");

            var promoted = RowOf(new ClassAbilityReadout
            {
                HasValue = false,
                Secondary = new List<ClassAbilityReadout> { secondary.Value, new ClassAbilityReadout { HasValue = true, Skill = 7, Effective = 7, Unit = "s" } },
            });

            Assert.IsNotNull(promoted);
            Assert.AreEqual(secondary.Value.Skill, promoted.Base);
            Assert.AreEqual(secondary.Value.Effective, promoted.Effective);
            Assert.AreEqual("%", promoted.Unit);
            Assert.AreEqual("extra jump", promoted.Label);
            Assert.IsFalse(promoted.Capped);

            // the same rules still apply to the promoted entry
            Assert.IsNull(RowOf(new ClassAbilityReadout
            {
                HasValue = false,
                Secondary = new List<ClassAbilityReadout> { new ClassAbilityReadout { HasValue = true, Skill = double.NaN, Effective = 1 } },
            }), "a non-finite secondary is dropped too");

            // a value-less primary with no secondaries, or an empty list, still shows nothing
            Assert.IsNull(RowOf(new ClassAbilityReadout { HasValue = false }));
            Assert.IsNull(RowOf(new ClassAbilityReadout { HasValue = false, Secondary = new List<ClassAbilityReadout>() }));

            // a primary with a value keeps the primary, never the secondary
            Assert.AreEqual(14d, RowOf(SampleReadout()).Effective);
        }

        /// <summary>
        /// The row sends the subject's rank unclamped. Defensive only: the live rank cache and the offline import already
        /// clamp to max_rank at load, so a live rank above max_rank does not reach the panel today; if one ever did, the
        /// row and the compare-and-set would still agree.
        /// </summary>
        [TestMethod]
        public void Build_RankAboveMaxRank_IsSentUnclamped_AndTheReadoutUsesIt()
        {
            var (t1, _) = Pair();
            var live = t1.MaxRank + 2;
            var subject = new FakeSubject { Ranks = new Dictionary<ClassAbilityId, int> { [t1.Id] = live } };

            var snap = ClassAbilityPanelModel.Build(subject, online: true, writeRefusal: null, Req);
            var row = snap.Abilities.Single(r => r.Id == t1.Name);

            Assert.AreEqual(live, row.Rank, "the live rank, not max_rank");
            Assert.IsFalse(ClassAbilityPanelModel.IsStale(live, row.Rank), "a change sent from this row is not stale");
            Assert.AreEqual(ClassAbilityPanelChangeKind.Lower, ClassAbilityPanelModel.Classify(row.Rank, row.Rank - 1), "and it can be lowered");
            CollectionAssert.AreEqual(new[] { (t1.Id, live) }, subject.ReadoutAsks, "the readout is asked at the rank the row reports");
            Assert.AreEqual(t1.CumulativeCost(t1.MaxRank), snap.Classes.Single(c => c.Class == t1.AbilityClass.ToString()).PointsSpent,
                "spend still counts at most max_rank ranks (CumulativeCost clamps)");
        }

        [TestMethod]
        public void WriteRefusal_Order_LaunchThenOnlineThenStateThenArea()
        {
            Assert.AreEqual(ClassAbilityPanelModel.NotLaunchSessionRefusal, ClassAbilityPanelModel.WriteRefusal(false, false, "state", "area"));
            Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, ClassAbilityPanelModel.WriteRefusal(true, false, "state", "area"));
            Assert.AreEqual("state", ClassAbilityPanelModel.WriteRefusal(true, true, "state", "area"));
            Assert.AreEqual("area", ClassAbilityPanelModel.WriteRefusal(true, true, null, "area"));
            Assert.IsNull(ClassAbilityPanelModel.WriteRefusal(true, true, null, null));
        }

        // ---- the character-state gate (owner ruling 2026-11-08) ----

        [TestMethod]
        public void StateRefusal_None_IsNull()
        {
            Assert.IsNull(ClassAbilityPanelModel.StateRefusal(dead: false, busy: false, teleporting: false, pkLogout: false));
        }

        [TestMethod]
        public void StateRefusal_Dead()
        {
            Assert.AreEqual("You cannot change class abilities while dead.",
                ClassAbilityPanelModel.StateRefusal(dead: true, busy: false, teleporting: false, pkLogout: false));
        }

        [TestMethod]
        public void StateRefusal_Busy()
        {
            Assert.AreEqual("You cannot change class abilities while busy.",
                ClassAbilityPanelModel.StateRefusal(dead: false, busy: true, teleporting: false, pkLogout: false));
        }

        [TestMethod]
        public void StateRefusal_Teleporting()
        {
            Assert.AreEqual("You cannot change class abilities while teleporting.",
                ClassAbilityPanelModel.StateRefusal(dead: false, busy: false, teleporting: true, pkLogout: false));
        }

        [TestMethod]
        public void StateRefusal_PkLogoutCountdown()
        {
            Assert.AreEqual("You cannot change class abilities while logging out.",
                ClassAbilityPanelModel.StateRefusal(dead: false, busy: false, teleporting: false, pkLogout: true));
        }

        [TestMethod]
        public void StateRefusal_MakesTheSnapshotReadOnly_WithThatSentence()
        {
            var state = ClassAbilityPanelModel.StateRefusal(dead: true, busy: true, teleporting: true, pkLogout: true);
            Assert.AreEqual(ClassAbilityPanelModel.DeadRefusal, state, "dead outranks the others");

            var subject = new FakeSubject();
            var snap = ClassAbilityPanelModel.Build(subject, online: true, ClassAbilityPanelModel.WriteRefusal(true, true, state, null), Req);

            Assert.IsTrue(snap.Online, "a read still answers");
            Assert.IsFalse(snap.Writable);
            Assert.AreEqual(state, snap.WriteRefusal);
            Assert.IsTrue(snap.Abilities.All(r => !r.CanRaise && !r.CanLower && r.RaiseRefusal == state));
            Assert.AreEqual(0, subject.RaiseAsks + subject.LowerAsks);
        }

        // ---- the catalog ----

        [TestMethod]
        public void AbilitySet_IsASubsetOfTheTokenCatalog()
        {
            var catalog = ClassAbilityTokenCatalog.TokenAbilities.ToHashSet();
            var missing = ClassAbilityPanelModel.AbilitySet.Where(d => !catalog.Contains(d.Id)).Select(d => d.Name).ToList();

            Assert.AreEqual(0, missing.Count, "panel abilities with no token catalog entry: " + string.Join(", ", missing));
            Assert.IsTrue(ClassAbilityPanelModel.AbilitySet.Count > 0, "control: the set is not empty");
        }

        // ---- the location rule: the panel's sentences, the vault's unchanged ----

        [TestMethod]
        public void AreaSentence_Wording()
        {
            Assert.AreEqual("You cannot change class abilities here.",
                MuleSummonHandler.ClassAbilityPanelAreaSentence(MuleSummonHandler.SummonAreaVerdict.Denylisted, "the Marketplace", true));
            Assert.AreEqual("You can only change class abilities in the Marketplace, or at a housing settlement.",
                MuleSummonHandler.ClassAbilityPanelAreaSentence(MuleSummonHandler.SummonAreaVerdict.OutsideAllowlist, "the Marketplace", true));
            Assert.AreEqual("You can only change class abilities in the Marketplace.",
                MuleSummonHandler.ClassAbilityPanelAreaSentence(MuleSummonHandler.SummonAreaVerdict.OutsideAllowlist, "the Marketplace", false));
            Assert.IsNull(MuleSummonHandler.ClassAbilityPanelAreaSentence(MuleSummonHandler.SummonAreaVerdict.Permitted, "the Marketplace", true));
        }

        [TestMethod]
        public void AreaSentence_ShippedAreaNameAlreadyNamingHousing_IsNotSaidTwice()
        {
            var shipped = DefaultPropertyManager.DefaultStringProperties["account_vault_allowlist_name"].Item;

            Assert.AreEqual($"You can only change class abilities in {shipped}.",
                MuleSummonHandler.ClassAbilityPanelAreaSentence(MuleSummonHandler.SummonAreaVerdict.OutsideAllowlist, shipped, true));
        }

        [TestMethod]
        public void AreaRefusal_OutsideTheAllowlist_HousingOnAndOff()
        {
            var outside = MakeLocation(0x0090);

            Assert.AreEqual("You can only change class abilities in the Marketplace, or at a housing settlement.",
                MuleSummonHandler.ClassAbilityPanelAreaRefusal(outside, _ => false));

            PropertyManager.ModifyBool("account_vault_housing_allowed", false);

            Assert.AreEqual("You can only change class abilities in the Marketplace.",
                MuleSummonHandler.ClassAbilityPanelAreaRefusal(outside, _ => false));
        }

        [TestMethod]
        public void AreaRefusal_HousingSettlementPermits_ButTheDenylistWins()
        {
            var settlement = MakeLocation(0x0090);

            Assert.IsNull(MuleSummonHandler.ClassAbilityPanelAreaRefusal(settlement, _ => true), "a housing settlement is permitted");

            PropertyManager.ModifyString("account_vault_denylist", "0090");

            Assert.AreEqual("You cannot change class abilities here.", MuleSummonHandler.ClassAbilityPanelAreaRefusal(settlement, _ => true));

            // the vault summon refuses the same place with its own, unchanged sentence
            Assert.IsFalse(MuleSummonHandler.IsSummonPermitted(settlement, out var vault, _ => true));
            Assert.AreEqual("You cannot summon your vault vendor here.", vault);
        }

        [TestMethod]
        public void VaultSummonMessages_AreUnchanged()
        {
            var outside = MakeLocation(0x0090);

            Assert.IsFalse(MuleSummonHandler.IsSummonPermitted(outside, out var reason, _ => false));
            Assert.AreEqual("You can only summon your vault vendor in the Marketplace.", reason, "housing on must not change the vault's wording");

            Assert.IsTrue(MuleSummonHandler.IsSummonPermitted(outside, out reason, _ => true));
            Assert.IsNull(reason);

            Assert.IsTrue(MuleSummonHandler.IsSummonPermitted(null, out reason, _ => false), "a null location is permitted for the summon");
        }

        /// <summary>The panel fails closed where the summon fails open: an unknown location, and no allowlist configured.</summary>
        [TestMethod]
        public void AreaRefusal_FailsClosed_NullLocation_AndEmptyAllowlist_SummonUnchanged()
        {
            Assert.AreEqual("You cannot change class abilities here.", MuleSummonHandler.ClassAbilityPanelAreaRefusal(null, _ => true));

            // the class-seeded allowlist is realm-qualified (01F5@1); a plain landblock row matches MakeLocation's realm-0 position
            PropertyManager.ModifyString("account_vault_allowlist", "01F5");
            var inside = MakeLocation(0x01F5);
            Assert.IsNull(MuleSummonHandler.ClassAbilityPanelAreaRefusal(inside, _ => false), "control: inside the allowlist is permitted");

            foreach (var blank in new[] { "", "   " })
            {
                PropertyManager.ModifyString("account_vault_allowlist", blank);

                Assert.AreEqual("Class ability changes from the web panel are not set up on this server.",
                    MuleSummonHandler.ClassAbilityPanelAreaRefusal(inside, _ => true), $"allowlist '{blank}'");
                Assert.IsTrue(MuleSummonHandler.IsSummonPermitted(MakeLocation(0x0123), out var vault, _ => false), "the summon still permits everywhere");
                Assert.IsNull(vault);
            }
        }
    }
}
