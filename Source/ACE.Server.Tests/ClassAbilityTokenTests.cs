using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the class ability token acquisition layer: the pure use-eligibility decision
    /// (ClassAbilityTokenCatalog.Evaluate, which the Gem token-use path and its messaging are built on) and the
    /// catalog's internal consistency with the skill registry. The full item-use flow (confirmation dialog,
    /// consuming the token, live inventory/Luminance) needs a live Player and is exercised in-game.
    /// </summary>
    [TestClass]
    public class ClassAbilityTokenTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // Multishot is a representative 3-rank skill: MaxRank 3, CostPerRank 2/3/4.
        private static ClassAbilityDefinition Multishot => ClassAbilityRegistry.Get(ClassAbilityId.Multishot);

        [TestMethod]
        public void Evaluate_FirstTier_FromScratch_WithEnoughPoints_IsOk()
        {
            var eval = ClassAbilityTokenCatalog.Evaluate(Multishot, currentRank: 0, tier: 1, availablePoints: 99);
            Assert.AreEqual(ClassAbilityTokenOutcome.Ok, eval.Outcome);
            Assert.IsTrue(eval.CanLearn);
            Assert.AreEqual(Multishot.CostPerRank[0], eval.Cost);
        }

        [TestMethod]
        public void Evaluate_NextTier_InOrder_IsOk_WithThatTiersCost()
        {
            var eval = ClassAbilityTokenCatalog.Evaluate(Multishot, currentRank: 1, tier: 2, availablePoints: 99);
            Assert.AreEqual(ClassAbilityTokenOutcome.Ok, eval.Outcome);
            Assert.AreEqual(Multishot.CostPerRank[1], eval.Cost);
        }

        [TestMethod]
        public void Evaluate_SkippingATier_IsWrongOrder()
        {
            // have rank 0, token is for tier 2 - must learn tier 1 first
            var eval = ClassAbilityTokenCatalog.Evaluate(Multishot, currentRank: 0, tier: 2, availablePoints: 99);
            Assert.AreEqual(ClassAbilityTokenOutcome.WrongTierOrder, eval.Outcome);
            Assert.IsFalse(eval.CanLearn);
        }

        [TestMethod]
        public void Evaluate_TierAlreadyOwned_IsWrongOrder()
        {
            // already rank 2, token is for tier 1 or 2 (<= current) - not the next rank
            Assert.AreEqual(ClassAbilityTokenOutcome.WrongTierOrder,
                ClassAbilityTokenCatalog.Evaluate(Multishot, currentRank: 2, tier: 1, availablePoints: 99).Outcome);
            Assert.AreEqual(ClassAbilityTokenOutcome.WrongTierOrder,
                ClassAbilityTokenCatalog.Evaluate(Multishot, currentRank: 2, tier: 2, availablePoints: 99).Outcome);
        }

        [TestMethod]
        public void Evaluate_AtMaxRank_IsAlreadyMax()
        {
            var eval = ClassAbilityTokenCatalog.Evaluate(Multishot, currentRank: Multishot.MaxRank, tier: 3, availablePoints: 99);
            Assert.AreEqual(ClassAbilityTokenOutcome.AlreadyMaxRank, eval.Outcome);
        }

        [TestMethod]
        public void Evaluate_TierAboveMaxRank_IsWrongOrder_WhenNotYetMaxed()
        {
            // rank 2, next is 3, but token claims tier 4 (beyond MaxRank) - rejected as wrong order, not learned
            var eval = ClassAbilityTokenCatalog.Evaluate(Multishot, currentRank: 2, tier: 4, availablePoints: 99);
            Assert.AreEqual(ClassAbilityTokenOutcome.WrongTierOrder, eval.Outcome);
        }

        [TestMethod]
        public void Evaluate_CorrectTier_ButNotEnoughPoints_IsInsufficient_AndStillReportsCost()
        {
            var cost = Multishot.CostPerRank[0];
            var eval = ClassAbilityTokenCatalog.Evaluate(Multishot, currentRank: 0, tier: 1, availablePoints: cost - 1);
            Assert.AreEqual(ClassAbilityTokenOutcome.InsufficientPoints, eval.Outcome);
            Assert.AreEqual(cost, eval.Cost, "cost must be reported even when unaffordable, for the error message");
        }

        [TestMethod]
        public void Evaluate_NotImplementedSkill_IsNotImplemented()
        {
            var stub = new ClassAbilityDefinition { Implemented = false, MaxRank = 1, CostPerRank = new[] { 1 } };
            var eval = ClassAbilityTokenCatalog.Evaluate(stub, currentRank: 0, tier: 1, availablePoints: 99);
            Assert.AreEqual(ClassAbilityTokenOutcome.NotImplemented, eval.Outcome);
        }

        [TestMethod]
        public void Catalog_HasOneOfferingPerRank_OfEveryTokenSkill()
        {
            // Retired abilities keep a reserved slot in TokenAbilities (wcid stability) but are unregistered
            // and produce no offering - count only registered slots, matching AllOfferings.
            var expected = ClassAbilityTokenCatalog.TokenAbilities
                .Where(id => ClassAbilityRegistry.Abilities.ContainsKey(id))
                .Sum(id => ClassAbilityRegistry.Abilities[id].MaxRank);
            Assert.AreEqual(expected, ClassAbilityTokenCatalog.AllOfferings().Count());
        }

        [TestMethod]
        public void Catalog_EveryOffering_MapsToAValidRegistrySkillAndTier()
        {
            foreach (var o in ClassAbilityTokenCatalog.AllOfferings())
            {
                Assert.AreSame(ClassAbilityRegistry.Get(o.SkillId), o.Definition);
                Assert.IsTrue(o.Definition.Implemented, $"{o.SkillId} token offered but skill not implemented");
                Assert.IsTrue(o.Tier >= 1 && o.Tier <= o.Definition.MaxRank, $"{o.SkillId} tier {o.Tier} out of range");
                Assert.AreEqual(ClassAbilityTokenCatalog.WcidFor(o.SkillId, o.Tier), o.Wcid);
            }
        }

        [TestMethod]
        public void Catalog_WcidsAreUnique_AndInsideTheReservedBlock()
        {
            var offerings = ClassAbilityTokenCatalog.AllOfferings().ToList();
            var wcids = offerings.Select(o => o.Wcid).ToList();

            CollectionAssert.AllItemsAreUnique(wcids);

            // TWO reserved blocks since Spellsword (the 8th class): the catalog outgrew 1000500-1000999 at
            // index OverflowStartIndex, so everything from there on is minted from WcidOverflowBase. Assert
            // each wcid sits inside the block its OWN index belongs to - a single combined range would pass
            // a wcid that fell in the gap between the two blocks, which is exactly the collision (1001000 =
            // Cedric, the Archer trainer NPC) the overflow base exists to avoid.
            var primaryEnd = ClassAbilityTokenCatalog.WcidBase +
                (uint)(ClassAbilityTokenCatalog.OverflowStartIndex * ClassAbilityTokenCatalog.WcidsPerAbility);

            var overflowCount = ClassAbilityTokenCatalog.TokenAbilities.Count - ClassAbilityTokenCatalog.OverflowStartIndex;
            var overflowEnd = ClassAbilityTokenCatalog.WcidOverflowBase +
                (uint)(overflowCount * ClassAbilityTokenCatalog.WcidsPerAbility);

            Assert.IsTrue(primaryEnd <= 1000999 + 1, $"primary token block overflowed its 1000500-1000999 reservation at {primaryEnd}");
            Assert.IsTrue(overflowEnd <= 1002499 + 1, $"overflow token block overflowed its 1002000-1002499 reservation at {overflowEnd}");

            foreach (var offering in offerings)
            {
                var index = ClassAbilityTokenCatalog.TokenAbilities.ToList().IndexOf(offering.SkillId);

                if (index < ClassAbilityTokenCatalog.OverflowStartIndex)
                    Assert.IsTrue(offering.Wcid >= ClassAbilityTokenCatalog.WcidBase && offering.Wcid < primaryEnd,
                        $"wcid {offering.Wcid} (index {index}) outside the primary reserved block");
                else
                    Assert.IsTrue(offering.Wcid >= ClassAbilityTokenCatalog.WcidOverflowBase && offering.Wcid < overflowEnd,
                        $"wcid {offering.Wcid} (index {index}) outside the overflow reserved block");
            }
        }

        [TestMethod]
        public void Catalog_WcidAssignment_IsStable()
        {
            // These are the wcids the generated token content is built against - if the ordering ever changes,
            // this pins the drift so the content can be regenerated deliberately. Block 1000500-1000999 is
            // reserved for tokens (see ClassAbilityTokenCatalog's class comment for the two collision incidents
            // that made this test necessary).
            Assert.AreEqual(1000500u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.Multishot, 1));
            Assert.AreEqual(1000501u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.Multishot, 2));
            Assert.AreEqual(1000502u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.Multishot, 3));
            Assert.AreEqual(1000506u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.Thorns, 1));
            Assert.AreEqual(1000544u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.NetherRush, 3));
        }

        /// <summary>
        /// The content-pointing test: the catalog constant and the committed Content/ token files must describe
        /// the same wcids. This is the test that would have caught the shipped WcidBase defect, where the token
        /// content was renumbered (1000060 -> 1000200 block) but the catalog constant was not - with class
        /// skills enabled, "/abilities token buy multishot 3" would have minted 1000062, the Driftwarden's Mark.
        /// </summary>
        [TestMethod]
        public void Catalog_MatchesCommittedTokenContent()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;
            if (dir == null)
                Assert.Inconclusive("Could not locate the repo's Content/sql/weenies directory by walking up from the test assembly -- skipping.");

            var sqlDir = Path.Combine(dir.FullName, "Content", "sql", "weenies");
            var files = Directory.GetFiles(sqlDir, "*_classskilltoken_*.sql");

            var fileWcids = files.Select(f => uint.Parse(Path.GetFileName(f).Split('_')[0])).ToHashSet();
            var catalogWcids = ClassAbilityTokenCatalog.AllOfferings().Select(o => o.Wcid).ToHashSet();

            Assert.IsTrue(catalogWcids.SetEquals(fileWcids),
                "catalog and Content/sql/weenies token wcids diverge - " +
                $"catalog only: [{string.Join(", ", catalogWcids.Except(fileWcids).OrderBy(w => w))}], " +
                $"content only: [{string.Join(", ", fileWcids.Except(catalogWcids).OrderBy(w => w))}]");

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var wcid = uint.Parse(name.Split('_')[0]);

                StringAssert.Contains(File.ReadAllText(f), $"`class_Id` = {wcid};", $"{name}: class_Id differs from filename");
            }
        }

        /// <summary>
        /// The price-pinning test. A token's CAP price is baked into its weenie TWICE - Value (int 19) and
        /// StackUnitValue (int 15) - and the vendor reads a DIFFERENT one on each side of the transaction:
        /// the shop window displays Value (the DefaultItemsForSale object is created without SetStackSize, so
        /// its Value is the weenie's), while the purchase path runs through Vendor.ItemProfileToWorldObjects,
        /// which calls SetStackSize and thereby recomputes Value = StackUnitValue * StackSize. A weenie whose
        /// two price properties disagree therefore charges a price it never displayed.
        ///
        /// That shipped: 4f9c0ae8f repriced every Enhanced/Training token's Value to the flattened 1/1/1 curve
        /// but left StackUnitValue on the old curves, so a "1 CAP" token silently billed up to 4 CAP - and,
        /// because RefundUnusedVoucher pays back CostPerRank, a buy/refund round trip destroyed the difference.
        /// Both properties and the C# CostPerRank table must agree, so pin all three together.
        /// </summary>
        [TestMethod]
        public void CommittedTokenContent_PricesMatchCostPerRank()
        {
            var sqlDir = FindTokenSqlDir();
            var files = Directory.GetFiles(sqlDir, "*_classskilltoken_*.sql");

            Assert.AreNotEqual(0, files.Length, "no token content files found");

            var byWcid = ClassAbilityTokenCatalog.AllOfferings().ToDictionary(o => o.Wcid);

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var wcid = uint.Parse(name.Split('_')[0]);

                Assert.IsTrue(byWcid.TryGetValue(wcid, out var offering), $"{name}: wcid {wcid} is not a catalog offering");

                var expected = offering.Definition.CostPerRank[offering.Tier - 1];
                var ints = ParseWeenieInts(File.ReadAllText(f));

                Assert.IsTrue(ints.TryGetValue(19, out var value), $"{name}: no Value (int 19) row");
                Assert.IsTrue(ints.TryGetValue(15, out var stackUnitValue), $"{name}: no StackUnitValue (int 15) row");

                Assert.AreEqual(expected, value,
                    $"{name}: Value (int 19, the price the shop window DISPLAYS) is {value}, but " +
                    $"{offering.SkillId} rank {offering.Tier} costs {expected} CAP");

                Assert.AreEqual(expected, stackUnitValue,
                    $"{name}: StackUnitValue (int 15, the price the vendor actually CHARGES via " +
                    $"SetStackSize) is {stackUnitValue}, but {offering.SkillId} rank {offering.Tier} costs {expected} CAP");

                // The equality above is only the right invariant because these vouchers are single-item stacks.
                Assert.IsTrue(ints.TryGetValue(11, out var maxStackSize), $"{name}: no MaxStackSize (int 11) row");
                Assert.AreEqual(1, maxStackSize, $"{name}: tokens are non-stackable bound vouchers (MaxStackSize 1)");
            }
        }

        /// <summary>
        /// Every class ability token is a BOUND, prepaid voucher: the class ability point cost is charged when the
        /// token is acquired, and using it applies the rank for free. Binding is what stops a paid-for rank from
        /// being traded, sold or dropped, and it is carried entirely by the weenie content - Attuned (int 114) and
        /// Bonded (int 33) - not by the acquisition code. The trainer VENDOR path in particular creates its token
        /// through Vendor.ItemProfileToWorldObjects, which sets no instance properties of its own, so a token
        /// weenie missing either row would ship a freely tradeable voucher and nothing in C# would catch it.
        /// </summary>
        [TestMethod]
        public void CommittedTokenContent_AreAttunedAndBonded()
        {
            var sqlDir = FindTokenSqlDir();
            var files = Directory.GetFiles(sqlDir, "*_classskilltoken_*.sql");

            Assert.AreNotEqual(0, files.Length, "no token content files found");

            var problems = new List<string>();

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var ints = ParseWeenieInts(File.ReadAllText(f));

                if (!ints.TryGetValue(114, out var attuned))
                    problems.Add($"{name}: no Attuned (int 114) row - the token would be tradeable");
                else if (attuned != (int)AttunedStatus.Attuned)
                    problems.Add($"{name}: Attuned (int 114) is {attuned}, expected {(int)AttunedStatus.Attuned} (Attuned)");

                if (!ints.TryGetValue(33, out var bonded))
                    problems.Add($"{name}: no Bonded (int 33) row - the token would be droppable");
                else if (bonded != (int)BondedStatus.Bonded)
                    problems.Add($"{name}: Bonded (int 33) is {bonded}, expected {(int)BondedStatus.Bonded} (Bonded)");
            }

            Assert.IsTrue(problems.Count == 0,
                "class ability tokens must all be Attuned + Bonded:\n" + string.Join("\n", problems));
        }

        /// <summary>
        /// The create_list-vs-catalog cross-check: the six Drift Network trainer NPCs are WeenieType.Vendor,
        /// so their live shop stock is driven entirely by their `weenie_properties_create_list` rows in
        /// Content/realms/driftnetwork_hub.sql, NOT by PropertyString 9008 (ClassAbilityTrainerAbilities) -
        /// Vendor.ActOnUse overrides Creature.ActOnUse without calling base, so ClassAbilityTrainer.TryHandleUse
        /// (the code that reads 9008) is unreachable for these NPCs. A prior defect (2026-08-03) updated 9008
        /// for several abilities without updating create_list, leaving Enhanced Heavy Weapons, Enhanced Magic
        /// Defense, Mana Barrier, Nether Bloom, and Soul Tether effectively unbuyable despite being homed to a
        /// class and "documented" in 9008. This test would have caught that: for every catalog offering whose
        /// ability is homed to one of the six trainer-bearing classes, its token wcid must appear in that
        /// trainer's committed create_list.
        /// </summary>
        [TestMethod]
        public void Catalog_HomedOfferings_AppearInCorrectTrainerCreateList()
        {
            var (sql, trainerByClass) = LoadTrainerContent();
            var stockedByTrainer = ParseStockedTokensByTrainer(sql);

            Assert.IsTrue(stockedByTrainer.Count >= 8, "expected at least 8 trainers with stocked create_list rows");

            var missing = new List<string>();
            foreach (var offering in ClassAbilityTokenCatalog.AllOfferings())
            {
                var abilityClass = offering.Definition.AbilityClass;
                if (!trainerByClass.TryGetValue(abilityClass, out var trainerId))
                    continue; // unhomed, or homed to a class with no trainer NPC yet - not tokenable in a shop

                if (!stockedByTrainer.TryGetValue(trainerId, out var stocked) || !stocked.Contains(offering.Wcid))
                    missing.Add($"{offering.SkillId} tier {offering.Tier} (wcid {offering.Wcid}), expected in trainer {trainerId}'s create_list");
            }

            Assert.IsTrue(missing.Count == 0,
                "abilities homed to a class but missing from that trainer's committed create_list:\n" + string.Join("\n", missing));
        }

        /// <summary>
        /// PropertyString 9008 (ClassAbilityTrainerAbilities) is DEAD CODE for shop purposes on all 8 trainers
        /// (all WeenieType.Vendor; Vendor.ActOnUse overrides Creature.ActOnUse without calling base, so
        /// ClassAbilityTrainer.TryHandleUse - the code that reads 9008 - is unreachable). It is kept anyway as
        /// documented design intent, which means nothing enforces that it stays in sync with create_list, the
        /// property that actually drives the live shop. This asserts the two agree by ability name for every
        /// trainer, so a future edit to one without the other is caught here instead of read live in-game as
        /// stale doctrine.
        /// </summary>
        [TestMethod]
        public void TrainerString9008_MatchesCreateListTokenSet_ByAbilityName()
        {
            var (sql, trainerByClass) = LoadTrainerContent();
            var stockedByTrainer = ParseStockedTokensByTrainer(sql);
            var nameStringByTrainer = ParseTrainerAbilityStrings(sql);

            var offeringsBySkill = ClassAbilityTokenCatalog.AllOfferings()
                .GroupBy(o => o.SkillId)
                .ToDictionary(g => g.Key, g => g.First().Definition);

            var mismatches = new List<string>();
            foreach (var (abilityClass, trainerId) in trainerByClass)
            {
                Assert.IsTrue(nameStringByTrainer.TryGetValue(trainerId, out var nameString),
                    $"trainer {trainerId} ({abilityClass}) has no PropertyString 9008 row");

                var namesIn9008 = ClassAbilityTrainer.ParseTrainerAbilities(nameString)
                    .Select(d => d.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var stocked = stockedByTrainer.TryGetValue(trainerId, out var s) ? s : new HashSet<uint>();
                var namesInCreateList = ClassAbilityTokenCatalog.AllOfferings()
                    .Where(o => stocked.Contains(o.Wcid))
                    .Select(o => o.Definition.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var only9008 = namesIn9008.Except(namesInCreateList).ToList();
                var onlyCreateList = namesInCreateList.Except(namesIn9008).ToList();

                if (only9008.Count > 0)
                    mismatches.Add($"trainer {trainerId} ({abilityClass}): 9008 lists {string.Join(",", only9008)} which create_list does not stock");
                if (onlyCreateList.Count > 0)
                    mismatches.Add($"trainer {trainerId} ({abilityClass}): create_list stocks {string.Join(",", onlyCreateList)} which 9008 does not list");
            }

            Assert.IsTrue(mismatches.Count == 0,
                "trainer PropertyString 9008 disagrees with its committed create_list token stock:\n" + string.Join("\n", mismatches));
        }

        /// <summary>
        /// Loads the combined SQL text for all 8 trainers (six inline in the realm file, two standalone) plus
        /// the trainer wcid per class, shared by every test that needs to read committed trainer content.
        /// </summary>
        private static (string sql, Dictionary<ClassAbilityClass, uint> trainerByClass) LoadTrainerContent()
        {
            // Same repo-root walk-up the token-content tests use, retargeted at the Drift Network realm file.
            var repoRoot = new DirectoryInfo(FindTokenSqlDir()).Parent.Parent.Parent;

            // TWO SOURCES, because the trainers are not all authored the same way. The original six
            // (1001000-1001005) have their create_list inline in the realm file; the two classes added later
            // each ship a standalone trainer weenie instead. Both files use byte-identical create_list row
            // syntax, so one regex covers both once the text is concatenated.
            var sqlPath = Path.Combine(repoRoot.FullName, "Content", "realms", "driftnetwork_hub.sql");
            Assert.IsTrue(File.Exists(sqlPath), $"expected {sqlPath} to exist");

            var standaloneTrainers = new[]
            {
                Path.Combine(repoRoot.FullName, "Content", "sql", "weenies", "1001950_npc_bloodmage_trainer.sql"),
                Path.Combine(repoRoot.FullName, "Content", "sql", "weenies", "1001960_npc_spellsword_trainer.sql"),
            };

            foreach (var path in standaloneTrainers)
                Assert.IsTrue(File.Exists(path), $"expected {path} to exist");

            var sql = File.ReadAllText(sqlPath) + "\n" +
                string.Join("\n", standaloneTrainers.Select(File.ReadAllText));

            // Trainer wcid per class: the original six from the realm file's own NPC roster comment, plus the
            // two standalone trainers above.
            var trainerByClass = new Dictionary<ClassAbilityClass, uint>
            {
                [ClassAbilityClass.Archer] = 1001000,
                [ClassAbilityClass.Rogue] = 1001001,
                [ClassAbilityClass.Vanguard] = 1001002,
                [ClassAbilityClass.Berserker] = 1001003,
                [ClassAbilityClass.Archmage] = 1001004,
                [ClassAbilityClass.VoidSummon] = 1001005,
                [ClassAbilityClass.BloodMage] = 1001950,
                [ClassAbilityClass.Spellsword] = 1001960,
            };

            return (sql, trainerByClass);
        }

        /// <summary>
        /// Only matches weenie_properties_create_list token rows: (trainerId, wcid, 4, -1, 0, 0, 0). The
        /// outfit-equip create_list rows in the same file use destination_Type 2, not 4, so this shape is
        /// specific to token stock and does not collide with them.
        /// </summary>
        private static Dictionary<uint, HashSet<uint>> ParseStockedTokensByTrainer(string sql)
        {
            var tokenRow = new Regex(@"\((\d{7}),\s*(\d+),\s*4,\s*-1,\s*0,\s*0,\s*0\)");

            var stockedByTrainer = new Dictionary<uint, HashSet<uint>>();
            foreach (Match m in tokenRow.Matches(sql))
            {
                var trainerId = uint.Parse(m.Groups[1].Value);
                var wcid = uint.Parse(m.Groups[2].Value);
                if (!stockedByTrainer.TryGetValue(trainerId, out var set))
                    stockedByTrainer[trainerId] = set = new HashSet<uint>();
                set.Add(wcid);
            }

            return stockedByTrainer;
        }

        /// <summary>
        /// Matches weenie_properties_string rows for PropertyString 9008 (ClassAbilityTrainerAbilities):
        /// (trainerId, 9008, 'comma,separated,names').
        /// </summary>
        private static Dictionary<uint, string> ParseTrainerAbilityStrings(string sql)
        {
            var stringRow = new Regex(@"\((\d{7}),\s*9008,\s*'([^']*)'\)");

            var result = new Dictionary<uint, string>();
            foreach (Match m in stringRow.Matches(sql))
                result[uint.Parse(m.Groups[1].Value)] = m.Groups[2].Value;

            return result;
        }

        private static string FindTokenSqlDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;
            if (dir == null)
                Assert.Inconclusive("Could not locate the repo's Content/sql/weenies directory by walking up from the test assembly -- skipping.");

            return Path.Combine(dir.FullName, "Content", "sql", "weenies");
        }

        /// <summary>
        /// Property ids are namespaced PER property table - int 15 is StackUnitValue but string 15 is ShortDesc -
        /// so this scopes the scan to the weenie_properties_int statement instead of matching (wcid, id, value)
        /// tuples across the whole file. Comments are stripped first, because a semicolon inside a block comment
        /// would otherwise end the statement early.
        /// </summary>
        private static Dictionary<int, int> ParseWeenieInts(string sql)
        {
            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

            var start = sql.IndexOf("`weenie_properties_int`", StringComparison.Ordinal);
            Assert.AreNotEqual(-1, start, "no weenie_properties_int block");

            var end = sql.IndexOf(';', start);
            var block = end < 0 ? sql.Substring(start) : sql.Substring(start, end - start);

            var result = new Dictionary<int, int>();

            foreach (Match m in Regex.Matches(block, @"\(\s*\d+\s*,\s*(\d+)\s*,\s*(-?\d+)\s*\)"))
                result[int.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);

            return result;
        }

        /// <summary>
        /// The bundle-description drift test. A "Training" bundle's committed token LongDesc (PropertyString
        /// 16) names the skills it raises in prose, separately from the BundleStatAbility.GenerateAll skill
        /// array that actually grants them - nothing keeps the two in sync when a bundle's membership changes.
        /// That drifted for real: the 2026-08-03 skill redistribution moved skills into and out of five of the
        /// eight bundles (Archer, Rogue, Vanguard, Archmage, Void Training), but the committed SQL LongDesc rows
        /// were never regenerated, so five bundles' token descriptions kept naming skills the ability no longer
        /// raises (and omitted skills it now does). This walks the live registry - not a hardcoded skill table -
        /// so a future bundle-membership change that similarly forgets to touch the SQL fails here instead of
        /// shipping a stale description to players.
        /// </summary>
        [TestMethod]
        public void CommittedTokenContent_BundleLongDesc_MatchesRegistryBundledSkills()
        {
            var sqlDir = FindTokenSqlDir();

            var mismatches = new List<string>();

            foreach (var bundle in ClassAbilityRegistry.StatBundleAbilities)
            {
                var expectedList = string.Join(", ", bundle.BundledSkills.Select(s => s.ToSentence()));
                var expectedFragment = $"Raises all of these base skills at once: {expectedList}.";

                foreach (var offering in ClassAbilityTokenCatalog.AllOfferings().Where(o => o.SkillId == bundle.Definition.Id))
                {
                    var files = Directory.GetFiles(sqlDir, $"{offering.Wcid}_*.sql");
                    Assert.AreEqual(1, files.Length, $"expected exactly one committed token file for wcid {offering.Wcid} ({bundle.Definition.Name} tier {offering.Tier})");

                    var name = Path.GetFileName(files[0]);
                    var text = File.ReadAllText(files[0]);

                    if (!text.Contains(expectedFragment))
                    {
                        var foundMatch = Regex.Match(text, @"Raises all of these base skills at once: ([^.]*)\.");
                        var found = foundMatch.Success ? foundMatch.Groups[1].Value : "<no matching LongDesc fragment found>";

                        mismatches.Add($"{name}: expected skill list [{expectedList}], found [{found}]");
                    }
                }
            }

            Assert.IsTrue(mismatches.Count == 0,
                "bundle token LongDesc diverges from BundleStatAbility.GenerateAll's BundledSkills:\n" + string.Join("\n", mismatches));
        }

        [TestMethod]
        public void Catalog_TryResolve_RejectsOutOfRangeTier()
        {
            Assert.IsTrue(ClassAbilityTokenCatalog.TryResolve(ClassAbilityId.Taunt, 1, out _));
            // Taunt is a single-rank skill - there is no tier 2 token
            Assert.IsFalse(ClassAbilityTokenCatalog.TryResolve(ClassAbilityId.Taunt, 2, out _));
            Assert.IsFalse(ClassAbilityTokenCatalog.TryResolve(ClassAbilityId.Multishot, 0, out _));
        }

        [TestMethod]
        public void Catalog_LumCost_ScalesWithTierPointCost()
        {
            var rate = PropertyManager.GetLong("class_ability_token_lum_per_point").Item;
            Assert.AreEqual(Multishot.CostPerRank[0] * rate, ClassAbilityTokenCatalog.LumCost(Multishot, 1));
            Assert.AreEqual(Multishot.CostPerRank[2] * rate, ClassAbilityTokenCatalog.LumCost(Multishot, 3));
        }
    }
}
