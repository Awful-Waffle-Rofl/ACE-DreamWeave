using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guard tests for live class ability text (ClassAbilityText): the grammar, the closed vocabulary, the
    /// byte-identical rendering of every unmigrated ability, the migrated pilots (Mana Barrier, Echo Cast),
    /// the live-retune path through Describe / RankTail / token appraisal, and the source-scan and bare-digit
    /// guards that keep a template honest about the tunables it quotes.
    ///
    /// ISOLATION. Everything but the Live test reads a defaults or dictionary source, which never touches
    /// PropertyManager. The Live test seeds every key it reads through ModifyDouble / ModifyLong and puts the
    /// previous value back in a finally block, because PropertyManager's caches are process-static.
    /// </summary>
    [TestClass]
    public class ClassAbilityTextTests
    {
        private const string ManaBarrierBase = "class_ability_manabarrier_base";
        private const string ManaBarrierStep = "class_ability_manabarrier_step";
        private const string ManaBarrierCap = "class_ability_manabarrier_max_share";
        private const string ManaBarrierFloor = "class_ability_manabarrier_mana_floor";
        private const string EchoBase = "class_ability_echocast_chance_base";
        private const string EchoStep = "class_ability_echocast_chance_step";

        // Mana Barrier: origin/master text, quoted verbatim. Echo Cast: the wisp rework text (2026-11-22, orchestrator
        // wording, verbatim at defaults); the numbers come from the class_ability_echocast_wisp_* tunables.
        private const string ManaBarrierMasterDescription =
            "Part of the damage you take comes out of your Mana instead of your Health, up to 30%. " +
            "It stops at 50 Mana, so it never drains you below that. " +
            "Your arcane cloud also provides protection from physical attacks: while you are casting a spell, you have " +
            "+100 shield armor against attacks from the front.";

        private const string EchoCastMasterDescription =
            "Your harmful spells from any school, and your Heal and Revitalize spells, can leave an Echo Wisp beside you. " +
            "For 9 seconds it recasts the same spell every 2 to 3 seconds as if you cast it, with your skill, bonuses and " +
            "procs, at no cost. A debuff moves on to enemies that lack it; a damage spell stays on its target until it dies, " +
            "then moves on; a heal moves on to the most hurt member of your fellowship once its target is full. Up to 3 " +
            "wisps at once. A wisp's cast never echoes.";

        private const string WispDuration = "class_ability_echocast_wisp_duration_seconds";
        private const string WispIntervalMin = "class_ability_echocast_wisp_interval_min_seconds";
        private const string WispIntervalMax = "class_ability_echocast_wisp_interval_max_seconds";
        private const string WispMaxActive = "class_ability_echocast_wisp_max_active";

        /// <summary>The Echo Cast description reads the live wisp tunables, so a retune shows up in the text.</summary>
        [TestMethod]
        public void EchoCast_Description_ReadsTheLiveWispTunables()
        {
            var previous = (Duration: PropertyManager.GetDouble(WispDuration).Item, Min: PropertyManager.GetDouble(WispIntervalMin).Item,
                Max: PropertyManager.GetDouble(WispIntervalMax).Item, Cap: PropertyManager.GetLong(WispMaxActive).Item);

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(WispDuration, 12.0));
                Assert.IsTrue(PropertyManager.ModifyDouble(WispIntervalMin, 1.5));
                Assert.IsTrue(PropertyManager.ModifyDouble(WispIntervalMax, 2.5));
                Assert.IsTrue(PropertyManager.ModifyLong(WispMaxActive, 4));

                var text = ClassAbilityText.Describe(EchoCast, ClassAbilityText.Live);

                StringAssert.Contains(text, "For 12 seconds it recasts the same spell every 1.5 to 2.5 seconds");
                StringAssert.Contains(text, "Up to 4 wisps at once.");
            }
            finally
            {
                PropertyManager.ModifyDouble(WispDuration, previous.Duration);
                PropertyManager.ModifyDouble(WispIntervalMin, previous.Min);
                PropertyManager.ModifyDouble(WispIntervalMax, previous.Max);
                PropertyManager.ModifyLong(WispMaxActive, previous.Cap);
            }

            Assert.AreEqual(EchoCastMasterDescription, ClassAbilityText.Describe(EchoCast, ClassAbilityText.Defaults));
        }

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static ClassAbilityDefinition ManaBarrier => ClassAbilityRegistry.Get(ClassAbilityId.ManaBarrier);

        private static ClassAbilityDefinition EchoCast => ClassAbilityRegistry.Get(ClassAbilityId.EchoCast);

        private static IEnumerable<ClassAbilityDefinition> AllDefinitions => ClassAbilityRegistry.Abilities.Values;

        // A synthetic multi-rank ability with no tail template: the "unmigrated" fixture, independent of which real abilities
        // happen to be migrated this week.
        private static ClassAbilityDefinition UnmigratedProbe => new ClassAbilityDefinition
        {
            Id = (ClassAbilityId)99999,
            Name = "unmigratedprobe",
            DisplayName = "Unmigrated Probe",
            DescriptionTemplate = "Does a thing at every rank.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
        };


        // ---------------------------------------------------------------------------------------------
        // grammar, vocabulary, formatters
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void EveryTemplate_Parses_WithOnlyVocabularyPlaceholders()
        {
            var problems = new List<string>();

            foreach (var def in AllDefinitions)
            {
                try
                {
                    ClassAbilityText.Keys(def);
                    ClassAbilityText.HasPlaceholders(def);
                }
                catch (FormatException ex)
                {
                    problems.Add($"{def.Name}: {ex.Message}");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        [TestMethod]
        public void EveryPlaceholderKey_ExistsInDefaults_AsALongOrADouble()
        {
            foreach (var def in AllDefinitions)
            {
                foreach (var key in ClassAbilityText.Keys(def))
                {
                    var isLong = DefaultPropertyManager.DefaultLongProperties.ContainsKey(key);
                    var isDouble = DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key);

                    Assert.IsTrue(isLong ^ isDouble,
                        $"{def.Name}: placeholder key '{key}' must be exactly one of a long default or a double default (long={isLong}, double={isDouble})");
                }
            }
        }

        [TestMethod]
        public void RenderAtDefaults_HasNoBraces_IsAsciiOnly_AndEndsWithAPeriod()
        {
            var problems = new List<string>();

            foreach (var def in AllDefinitions)
            {
                var description = ClassAbilityText.Describe(def, ClassAbilityText.Defaults);

                if (string.IsNullOrWhiteSpace(description))
                {
                    problems.Add($"{def.Name}: empty description");
                    continue;
                }

                if (!description.EndsWith("."))
                    problems.Add($"{def.Name}: description does not end with a period");

                var texts = new List<string> { description };
                for (var rank = 1; rank <= def.MaxRank && def.RankTailTemplate != null; rank++)
                    texts.Add(ClassAbilityText.RankTail(def, rank, ClassAbilityText.Defaults));

                foreach (var text in texts)
                {
                    if (text.Contains('{') || text.Contains('}'))
                        problems.Add($"{def.Name}: rendered text has a brace: [{text}]");

                    if (text.Any(c => c > 127))
                        problems.Add($"{def.Name}: rendered text has a non-ASCII character: [{text}]");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        [TestMethod]
        public void UnmigratedAbilities_RenderByteIdentical_ToTheirAuthoredText()
        {
            var checkedCount = 0;

            foreach (var def in AllDefinitions.Where(d => !ClassAbilityText.HasPlaceholders(d)))
            {
                Assert.AreEqual(def.DescriptionTemplate, ClassAbilityText.Describe(def, ClassAbilityText.Live), $"{def.Name}: Live");
                Assert.AreEqual(def.DescriptionTemplate, ClassAbilityText.Describe(def, ClassAbilityText.Defaults), $"{def.Name}: Defaults");
                Assert.AreEqual(def.RankTailTemplate == null ? 0 : def.MaxRank, ClassAbilityText.RankLines(def, ClassAbilityText.Defaults).Count, $"{def.Name}: rank lines only with a tail template");
                checkedCount++;
            }

            Assert.IsTrue(checkedCount > 50, "expected the large placeholder-free majority to be covered, got " + checkedCount);
        }

        [TestMethod]
        public void Pilots_RenderAtDefaults_AsPinned()
        {
            Assert.AreEqual(ManaBarrierMasterDescription, ClassAbilityText.Describe(ManaBarrier, ClassAbilityText.Defaults));
            Assert.AreEqual(EchoCastMasterDescription, ClassAbilityText.Describe(EchoCast, ClassAbilityText.Defaults));

            Assert.AreEqual("6% of damage taken from Mana", ClassAbilityText.RankTail(ManaBarrier, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("12% of damage taken from Mana", ClassAbilityText.RankTail(ManaBarrier, 2, ClassAbilityText.Defaults));
            Assert.AreEqual("18% of damage taken from Mana", ClassAbilityText.RankTail(ManaBarrier, 3, ClassAbilityText.Defaults));

            Assert.AreEqual("8% chance", ClassAbilityText.RankTail(EchoCast, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("16% chance", ClassAbilityText.RankTail(EchoCast, 2, ClassAbilityText.Defaults));
            Assert.AreEqual("24% chance", ClassAbilityText.RankTail(EchoCast, 3, ClassAbilityText.Defaults));

            Assert.AreEqual($"Mana Barrier - {ManaBarrierMasterDescription} Affinity: Magic Defense. This token teaches rank 2: 12% of damage taken from Mana.",
                ClassAbilityText.TokenLongDesc(ManaBarrier, 2, ClassAbilityText.Defaults));

            CollectionAssert.AreEqual(
                new[] { "Rank 1: 8% chance", "Rank 2: 16% chance", "Rank 3: 24% chance" },
                ClassAbilityText.RankLines(EchoCast, ClassAbilityText.Defaults).ToArray());
        }

        [TestMethod]
        public void Formatters_AreInvariantAndTrimmed()
        {
            Assert.AreEqual("6", ClassAbilityText.Format(0.06, "pct"));
            Assert.AreEqual("12", ClassAbilityText.Format(0.06 + 0.06, "pct"));
            Assert.AreEqual("12.5", ClassAbilityText.Format(0.125, "pct"));
            Assert.AreEqual("0", ClassAbilityText.Format(0.0, "pct"));
            Assert.AreEqual("50", ClassAbilityText.Format(49.6, "int"));
            Assert.AreEqual("1.5", ClassAbilityText.Format(1.5, "num"));
            Assert.AreEqual("2", ClassAbilityText.Format(2.0, "num"));
            Assert.AreEqual("1.5", ClassAbilityText.Format(1500, "s_ms"));
            Assert.AreEqual("1.5x", ClassAbilityText.Format(1.5, "x"));

            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.AreEqual("12.5", ClassAbilityText.Format(0.125, "pct"), "a comma-decimal locale must not leak into player text");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        }

        [TestMethod]
        public void Parser_RejectsAnythingOutsideTheClosedVocabulary()
        {
            foreach (var bad in new[]
            {
                "{bogus:class_ability_echocast_chance_base|pct}",
                "{v:class_ability_echocast_chance_base}",
                "{v:class_ability_echocast_chance_base|nope}",
                "{ladder:class_ability_echocast_chance_base|pct}",
                "{v:a,b,c|pct}",
                "{maxrank|pct}",
                "{v:Class_Ability|pct}",
                "text } stray",
                "text { unterminated",
                "{{v:x|pct}}",
            })
            {
                var def = new ClassAbilityDefinition { Name = "probe", MaxRank = 3, DescriptionTemplate = bad };
                Assert.Throws<FormatException>(() => ClassAbilityText.Describe(def, ClassAbilityText.Defaults), bad);
            }
        }

        [TestMethod]
        public void Vocabulary_PerRanksAndMaxRank_Render()
        {
            var values = new DictionaryTunableValues(new Dictionary<string, double>());

            var def = new ClassAbilityDefinition
            {
                Name = "probe",
                DisplayName = "Probe",
                MaxRank = 3,
                DescriptionTemplate = "{maxrank} ranks: {per:class_ability_echocast_chance_base|pct}% per rank.",
                RankTailTemplate = "{per:class_ability_echocast_chance_base|pct}%",
            };

            Assert.AreEqual("3 ranks: 8/16/24% per rank.", ClassAbilityText.Describe(def, values));
            Assert.AreEqual("24%", ClassAbilityText.RankTail(def, 3, values));
            Assert.AreEqual("Probe - 3 ranks: 8/16/24% per rank. This token teaches rank 2: 16%.", ClassAbilityText.TokenLongDesc(def, 2, values));

            // {ranks:PREFIX} reads PREFIX1..PREFIXn; class_ability_manabarrier_ has no numbered keys, so probe the error path.
            var ranksDef = new ClassAbilityDefinition { Name = "probe2", MaxRank = 2, DescriptionTemplate = "{ranks:class_ability_nothing_|int}" };
            Assert.Throws<ArgumentException>(() => ClassAbilityText.Describe(ranksDef, values));
            CollectionAssert.AreEqual(new[] { "class_ability_nothing_1", "class_ability_nothing_2" }, ClassAbilityText.Keys(ranksDef).ToArray());
        }

        // ---------------------------------------------------------------------------------------------
        // live retune
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void DictionarySource_RetunesEveryRenderPath()
        {
            var values = new DictionaryTunableValues(new Dictionary<string, double>
            {
                [EchoBase] = 0.12,
                [EchoStep] = 0.04,
                [ManaBarrierFloor] = 75,
                [ManaBarrierCap] = 0.35,
            });

            Assert.AreEqual("12% chance", ClassAbilityText.RankTail(EchoCast, 1, values));
            Assert.AreEqual("16% chance", ClassAbilityText.RankTail(EchoCast, 2, values));
            Assert.AreEqual("20% chance", ClassAbilityText.RankTail(EchoCast, 3, values));

            var barrier = ClassAbilityText.Describe(ManaBarrier, values);
            StringAssert.Contains(barrier, "up to 35%.");
            StringAssert.Contains(barrier, "It stops at 75 Mana");
        }

        [TestMethod]
        public void LadderCap_ClampsEachRank_LikeDivertShare()
        {
            var values = new DictionaryTunableValues(new Dictionary<string, double> { [ManaBarrierCap] = 0.10 });

            Assert.AreEqual("10% of damage taken from Mana", ClassAbilityText.RankTail(ManaBarrier, 3, values));
            Assert.AreEqual("6% of damage taken from Mana", ClassAbilityText.RankTail(ManaBarrier, 1, values));
            StringAssert.Contains(ClassAbilityText.Describe(ManaBarrier, values), "up to 10%.");

            // a negative cap clamps to zero, never a negative share
            var negative = new DictionaryTunableValues(new Dictionary<string, double> { [ManaBarrierCap] = -0.5 });
            Assert.AreEqual("0% of damage taken from Mana", ClassAbilityText.RankTail(ManaBarrier, 2, negative));

            // the text agrees with the mechanic at every rank (DivertShare with no rider or gear)
            for (var rank = 1; rank <= 3; rank++)
            {
                var share = ManaBarrierAbility.DivertShare(rank, 0.06, 0.06, 0.0, 0.10);
                Assert.AreEqual(ClassAbilityText.Format(share, "pct") + "% of damage taken from Mana", ClassAbilityText.RankTail(ManaBarrier, rank, values));
            }
        }

        [TestMethod]
        public void UintFormatter_ClampsALongFloorAtZero()
        {
            Assert.AreEqual("0", ClassAbilityText.Format(-5, "uint"));
            Assert.AreEqual("50", ClassAbilityText.Format(50, "uint"));
            StringAssert.Contains(ClassAbilityText.Describe(ManaBarrier, new DictionaryTunableValues(new Dictionary<string, double> { [ManaBarrierFloor] = -20 })), "It stops at 0 Mana");
        }

        [TestMethod]
        public void FloorFormatter_FloorsInvariantly_AndAgreesWithNetherBloomJumpCount()
        {
            Assert.AreEqual("1", ClassAbilityText.Format(1.6, "floor"));
            Assert.AreEqual("3", ClassAbilityText.Format(3.0, "floor"));
            Assert.AreEqual("0", ClassAbilityText.Format(0.99, "floor"));
            Assert.AreEqual("0", ClassAbilityText.Format(-0.5, "floor"), "a negative value clamps to 0, as JumpCount does");
            Assert.AreEqual("0", ClassAbilityText.Format(-0.0, "floor"));
            Assert.AreEqual("3", ClassAbilityText.Format(2.5, "int"), "int rounds away from zero; that is the divergence floor exists for");

            // 1.6 per rank: ranks 1/2/3 grant floor(1.6)/floor(3.2)/floor(4.8) = 1/3/4 jumps; int would have said 2/3/5
            const string key = "class_ability_netherbloom_jumps_per_rank";
            var values = new DictionaryTunableValues(new Dictionary<string, double> { [key] = 1.6 });
            var netherBloom = ClassAbilityRegistry.Get(ClassAbilityId.NetherBloom);

            var expected = string.Join("/", Enumerable.Range(1, netherBloom.MaxRank).Select(r => NetherBloomAbility.JumpCount(r, 1.6)));
            Assert.AreEqual("1/3/4", expected);
            // the counts now live in the rank tails ("spreads to 1 nearby enemy", "... 3 nearby enemies", "... 4 nearby enemies")
            for (var rank = 1; rank <= netherBloom.MaxRank; rank++)
            {
                var jumps = NetherBloomAbility.JumpCount(rank, 1.6);
                Assert.AreEqual($"spreads to {jumps} nearby {(jumps == 1 ? "enemy" : "enemies")}", ClassAbilityText.RankTail(netherBloom, rank, values));
            }
        }

        // ---------------------------------------------------------------------------------------------
        // placeholders that quote a clamped value: the DEFAULT text is exact only while the default satisfies the clamp
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void WeakenedBlood_DefaultSlows_SatisfyTheSpeedFloorClamp()
        {
            var floor = DefaultPropertyManager.DefaultDoubleProperties["class_ability_weakenedblood_slow_floor"].Item;

            for (var rank = 1; rank <= 3; rank++)
            {
                var slow = DefaultPropertyManager.DefaultDoubleProperties["class_ability_weakenedblood_slow_r" + rank].Item;
                Assert.IsTrue(slow <= 1.0 - floor, $"slow_r{rank} {slow} must be <= 1 - slow_floor {floor}, or the quoted slow overstates the mechanic");
                Assert.AreEqual(1.0 - slow, WeakenedBloodMath.SpeedFactor(slow, floor), 1e-9, $"rank {rank}: the mechanic applies exactly the quoted slow");
            }
        }

        [TestMethod]
        public void Exsanguinate_DefaultChargesKept_SatisfyTheStackCapClamp()
        {
            var caps = Enumerable.Range(1, 3).Select(r => DefaultPropertyManager.DefaultLongProperties["class_ability_bloodmage_charge_stack_cap_r" + r].Item).ToArray();
            var kept = Enumerable.Range(1, 3).Select(r => DefaultPropertyManager.DefaultLongProperties["class_ability_exsanguinate_charges_kept_r" + r].Item).ToArray();

            for (var rank = 1; rank <= 3; rank++)
            {
                // the cap that applies is the Sanguine Reserve stack cap, which depends on the Reserve rank; compare against the same rank
                var cap = BloodChargeMath.StackCap(rank, caps[0], caps[1], caps[2]);
                Assert.IsTrue(kept[rank - 1] <= cap - 1L, $"charges_kept_r{rank} {kept[rank - 1]} must be <= stack_cap_r{rank} {cap} - 1, or the quoted count overstates the mechanic");
                Assert.AreEqual((int)kept[rank - 1], ExsanguinateMath.ChargesKept(rank, kept[0], kept[1], kept[2], cap), $"rank {rank}: the mechanic keeps exactly the quoted count");
            }
        }

        [TestMethod]
        public void FastAim_DefaultTotal_SatisfiesTheOneClamp()
        {
            var perRank = DefaultPropertyManager.DefaultDoubleProperties["class_ability_fastaim_low_end_per_rank"].Item;
            var maxRank = ClassAbilityRegistry.Get(ClassAbilityId.EagleEye).MaxRank;

            Assert.IsTrue(maxRank * perRank <= 1.0, $"MaxRank {maxRank} * low_end_per_rank {perRank} must be <= 1.0, or the quoted total overstates the mechanic");
            Assert.AreEqual((float)(maxRank * perRank), FastAimAbility.LowEndBonus(maxRank, perRank), 1e-6f);
        }
        [TestMethod]
        public void Live_FollowsAModifyDoubleRetune_InDescribeRankTailAndAppraisal() => RunLiveRetune();

        /// <summary>
        /// Same retune, but starting from NON-default values another class might have left behind: the test
        /// must restore exactly what it found, not the defaults.
        /// </summary>
        [TestMethod]
        public void Live_RestoresTheTruePriorValues_NotTheDefaults()
        {
            var keys = new[] { EchoBase, EchoStep, ManaBarrierBase, ManaBarrierStep, ManaBarrierCap };
            var outer = keys.ToDictionary(k => k, k => PropertyManager.GetDouble(k).Item);
            var outerFloor = PropertyManager.GetLong(ManaBarrierFloor).Item;

            try
            {
                PropertyManager.ModifyDouble(EchoBase, 0.31);
                PropertyManager.ModifyDouble(EchoStep, 0.01);
                PropertyManager.ModifyDouble(ManaBarrierBase, 0.02);
                PropertyManager.ModifyDouble(ManaBarrierStep, 0.03);
                PropertyManager.ModifyDouble(ManaBarrierCap, 0.99);
                PropertyManager.ModifyLong(ManaBarrierFloor, 7);

                RunLiveRetune();

                Assert.AreEqual(0.31, PropertyManager.GetDouble(EchoBase).Item);
                Assert.AreEqual(0.99, PropertyManager.GetDouble(ManaBarrierCap).Item);
                Assert.AreEqual(7L, PropertyManager.GetLong(ManaBarrierFloor).Item);
            }
            finally
            {
                foreach (var kv in outer)
                    PropertyManager.ModifyDouble(kv.Key, kv.Value);
                PropertyManager.ModifyLong(ManaBarrierFloor, outerFloor);
            }
        }

        /// <summary>The ceiling metadata must reach every surface from the LIVE tunables, not only from a dictionary.</summary>
        [TestMethod]
        public void Live_AvoidanceCapRetune_Reaches_Info_Token_Feed_Panel()
        {
            const string key = "class_ability_avoidance_cap";
            var previous = PropertyManager.GetDouble(key).Item;
            var parry = ClassAbilityRegistry.Abilities[ClassAbilityId.Parry];

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(key, 0.42));

                StringAssert.Contains(string.Join("\n", ACE.Server.Command.Handlers.ClassAbilityCommands.BuildInfoLines(parry, 1, ClassAbilityText.Live)), "42%");
                StringAssert.Contains(ClassAbilityTokenAppraisal.LongDesc("committed", (int)parry.Id, 1, ClassAbilityText.Live), "42%");
                Assert.AreEqual(42.0, ClassAbilityDescriptionFeed.Build(ClassAbilityText.Live).Abilities.Single(a => a.Id == parry.Name)
                    .Ceilings.Single(c => c.Id == "avoidance").Value);

                var subject = new LiveSubject();
                var row = ACE.Server.Managers.Market.ClassAbilityPanelModel
                    .Build(subject, online: false, writeRefusal: null, new ACE.Server.Managers.Market.ClassAbilityPanelTierRequirements(3, 5, 8, 15), ClassAbilityText.Live)
                    .Abilities.Single(a => a.Id == parry.Name);
                Assert.AreEqual(42.0, row.Ceilings.Single(c => c.Id == "avoidance").Max);
            }
            finally
            {
                PropertyManager.ModifyDouble(key, previous);
            }

            Assert.AreEqual(previous, PropertyManager.GetDouble(key).Item, "restored");
        }

        private sealed class LiveSubject : ACE.Server.Managers.Market.IClassAbilityPanelSubject
        {
            public uint CharacterGuid => 0x50000B02;
            public string Name => "Live";
            public int Level => 1;
            public string Revision => "r";
            public int PointsAvailable => 0;
            public int PointsEarned => 0;
            public IReadOnlyDictionary<ClassAbilityId, int> Ranks { get; } = new Dictionary<ClassAbilityId, int>();
            public bool CanRaise(ClassAbilityDefinition ability, out string refusal) { refusal = null; return false; }
            public bool CanLower(ClassAbilityDefinition ability, out string refusal) { refusal = null; return false; }
            public ClassAbilityReadout? Readout(ClassAbilityDefinition ability, int rank) => null;
        }

        private static void RunLiveRetune()
        {
            var doubles = new Dictionary<string, double>
            {
                [EchoBase] = 0.12,
                [EchoStep] = 0.10,
                [ManaBarrierBase] = 0.05,
                [ManaBarrierStep] = 0.07,
                [ManaBarrierCap] = 0.40,
            };

            // The class initializer cached every default, so these reads never reach the database. Capture the
            // TRUE prior values before any change and restore exactly those.
            var previousDoubles = doubles.Keys.ToDictionary(k => k, k => PropertyManager.GetDouble(k).Item);
            var previousFloor = PropertyManager.GetLong(ManaBarrierFloor).Item;

            try
            {
                foreach (var kv in doubles)
                    Assert.IsTrue(PropertyManager.ModifyDouble(kv.Key, kv.Value), kv.Key);
                Assert.IsTrue(PropertyManager.ModifyLong(ManaBarrierFloor, 80));

                // Describe
                Assert.AreEqual("12% chance", ClassAbilityText.RankTail(EchoCast, 1, ClassAbilityText.Live));
                var barrier = ClassAbilityText.Describe(ManaBarrier, ClassAbilityText.Live);
                StringAssert.Contains(barrier, "up to 40%.");
                StringAssert.Contains(barrier, "It stops at 80 Mana");

                // RankTail
                Assert.AreEqual("22% chance", ClassAbilityText.RankTail(EchoCast, 2, ClassAbilityText.Live));
                Assert.AreEqual("19% of damage taken from Mana", ClassAbilityText.RankTail(ManaBarrier, 3, ClassAbilityText.Live));

                // The token appraisal path replaces the committed LongDesc with the live text.
                var props = new Dictionary<PropertyString, string> { [PropertyString.LongDesc] = "committed snapshot" };
                Assert.IsTrue(ClassAbilityTokenAppraisal.Apply(props, (int)ClassAbilityId.EchoCast, 3, ClassAbilityText.Live));
                Assert.IsFalse(props[PropertyString.LongDesc].Contains("committed snapshot"), "the live text replaced the committed one");
                StringAssert.Contains(props[PropertyString.LongDesc], "This token teaches rank 3: 32% chance.");

                // ...and the /abilities info rank lines.
                CollectionAssert.AreEqual(
                    new[] { "Rank 1: 12% chance", "Rank 2: 22% chance", "Rank 3: 32% chance" },
                    ClassAbilityText.RankLines(EchoCast, ClassAbilityText.Live).ToArray());

                // ...and the website feed.
                var feed = ClassAbilityDescriptionFeed.Build(ClassAbilityText.Live);
                var echo = feed.Abilities.Single(a => a.Id == "echocast");
                Assert.AreEqual("32% chance", echo.Ranks.Single(r => r.Rank == 3).Text);
                Assert.AreEqual(ClassAbilityText.Describe(EchoCast, ClassAbilityText.Live), echo.Description);
            }
            finally
            {
                foreach (var kv in previousDoubles)
                    PropertyManager.ModifyDouble(kv.Key, kv.Value);
                PropertyManager.ModifyLong(ManaBarrierFloor, previousFloor);
            }

            // restored to exactly what was there before, whatever that was
            foreach (var kv in previousDoubles)
                Assert.AreEqual(kv.Value, PropertyManager.GetDouble(kv.Key).Item, kv.Key);
            Assert.AreEqual(previousFloor, PropertyManager.GetLong(ManaBarrierFloor).Item);
        }

        // ---------------------------------------------------------------------------------------------
        // the other server-rendered token text paths (hooked item, market listing) and /abilities info
        // ---------------------------------------------------------------------------------------------

        private static ACE.Entity.Models.Weenie EchoCastTokenWeenie(int tier, string committed = "committed snapshot")
        {
            return new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 1000812,
                ClassName = "probe_echocast_token",
                WeenieType = ACE.Entity.Enum.WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Echo Cast Token", [PropertyString.LongDesc] = committed },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    [PropertyInt.ClassAbilityTokenId] = (int)ClassAbilityId.EchoCast,
                    [PropertyInt.ClassAbilityTokenTier] = tier,
                },
            };
        }

        private static readonly DictionaryTunableValues Retuned = new DictionaryTunableValues(new Dictionary<string, double>
        {
            [EchoBase] = 0.12,
            [EchoStep] = 0.10,
        });

        /// <summary>
        /// The shared helper is what the hooked-item appraisal branch (AppraiseInfo) calls on the hooked
        /// item's raw LongDesc.
        /// </summary>
        [TestMethod]
        public void SharedLongDescHelper_RendersEligibleTokens_AndPassesEverythingElseThrough()
        {
            var live = ClassAbilityTokenAppraisal.LongDesc("committed", (int)ClassAbilityId.EchoCast, 2, Retuned);
            StringAssert.StartsWith(live, "Echo Cast - ");
            StringAssert.Contains(live, "This token teaches rank 2: 22% chance.");

            Assert.AreEqual("committed", ClassAbilityTokenAppraisal.LongDesc("committed", 0, 0, Retuned), "not a token");
            // a multi-rank ability with no tail template still renders: the description, then the rank the token teaches
            Assert.AreEqual("Unmigrated Probe - Does a thing at every rank. This token teaches rank 1.", ClassAbilityText.TokenLongDesc(UnmigratedProbe, 1, Retuned));
            Assert.AreEqual("committed", ClassAbilityTokenAppraisal.LongDesc("committed", (int)ClassAbilityId.EchoCast, 9, Retuned), "bad tier");
            Assert.IsNull(ClassAbilityTokenAppraisal.LongDesc(null, 0, 0, Retuned));
        }

        [TestMethod]
        public void MarketListingText_FollowsTheTunables_ForTokens_AndLeavesOtherItemsAlone()
        {
            var weenie = EchoCastTokenWeenie(3);
            var text = ACE.Server.Managers.Market.MarketAppraisal.Description(weenie, Retuned);
            StringAssert.StartsWith(text, "Echo Cast - ");
            StringAssert.Contains(text, "This token teaches rank 3: 32% chance.");

            var plain = EchoCastTokenWeenie(3, "  just an item  ");
            plain.PropertiesInt.Clear();
            Assert.AreEqual("just an item", ACE.Server.Managers.Market.MarketAppraisal.Description(plain, Retuned));
            Assert.IsNull(ACE.Server.Managers.Market.MarketAppraisal.Description((ACE.Entity.Models.Weenie)null, Retuned));

            // the WorldObject overload (MarketSnapshot reads this for a live item)
            var wo = new ACE.Server.WorldObjects.GenericObject(EchoCastTokenWeenie(1), new ACE.Entity.ObjectGuid(0x7F000002));
            var woText = ACE.Server.Managers.Market.MarketAppraisal.Description(wo, Retuned);
            StringAssert.StartsWith(woText, "Echo Cast - ");
            StringAssert.Contains(woText, "This token teaches rank 1: 12% chance.");
        }

        [TestMethod]
        public void InfoLines_AreHeader_Description_RankLines_ThenCost_InThatOrder()
        {
            var lines = ACE.Server.Command.Handlers.ClassAbilityCommands.BuildInfoLines(EchoCast, 2, ClassAbilityText.Defaults);

            // Echo Cast declares ceilings, so its info carries one Limit line each and the rate line, between the affinity line and the ranks.
            var limits = ACE.Server.ClassAbilities.ClassAbilityCeilingText.InfoLines(EchoCast, 2, ClassAbilityText.Defaults);
            Assert.IsTrue(limits.Count > 0, "the fixture must declare ceilings for this ordering pin to mean anything");
            var rate = ACE.Server.ClassAbilities.ClassAbilityCeilingText.AffinityRateLine(EchoCast, ClassAbilityText.Defaults);
            Assert.IsNotNull(rate);

            Assert.AreEqual(2 + 1 + limits.Count + 1 + 3 + 1, lines.Count);
            StringAssert.StartsWith(lines[0], "Echo Cast (echocast)");
            Assert.AreEqual("  " + EchoCastMasterDescription, lines[1]);
            Assert.AreEqual("  Affinity: Magic Item Tinkering", lines[2], "the affinity line follows the description at the same indentation");
            CollectionAssert.AreEqual(limits.ToList(), lines.GetRange(3, limits.Count), "ceiling lines follow the affinity line");
            Assert.AreEqual("  " + rate, lines[3 + limits.Count], "then the rate line");
            var r = 4 + limits.Count;
            Assert.AreEqual("Rank 1: 8% chance", lines[r]);
            Assert.AreEqual("Rank 2: 16% chance", lines[r + 1]);
            Assert.AreEqual("Rank 3: 24% chance", lines[r + 2]);
            StringAssert.StartsWith(lines[r + 3], "  Max rank: 3.");
            StringAssert.EndsWith(lines[r + 3], "Your rank: 2.");

            // an unmigrated ability prints no rank lines: header, description, cost
            var unmigrated = ACE.Server.Command.Handlers.ClassAbilityCommands.BuildInfoLines(UnmigratedProbe, 0, ClassAbilityText.Defaults);
            Assert.AreEqual(3, unmigrated.Count);
        }

        // ---------------------------------------------------------------------------------------------
        // token appraisal gating
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void TokenAppraisal_RendersEveryRegisteredAbility_FromTheCentralText()
        {
            var values = ClassAbilityText.Defaults;

            // a multi-rank ability with no tail template renders too (the synthetic probe pins the line itself)
            Assert.IsNull(UnmigratedProbe.RankTailTemplate);
            Assert.IsTrue(UnmigratedProbe.MaxRank > 1);
            Assert.IsNotNull(ClassAbilityText.TokenLongDesc(UnmigratedProbe, 1, values));

            // every registered ability, generated families included, takes the central text at every tier
            foreach (var handler in ClassAbilityRegistry.Handlers)
            {
                var def = handler.Definition;
                for (var tier = 1; tier <= def.MaxRank; tier++)
                {
                    var p = new Dictionary<PropertyString, string> { [PropertyString.LongDesc] = "replace me" };
                    Assert.IsTrue(ClassAbilityTokenAppraisal.Apply(p, (int)def.Id, tier, values), $"{def.Name} tier {tier}");
                    // The token text is pinned; the appraisal adds the ceiling sentence after it, only where ceilings are declared.
                    var limits = ACE.Server.ClassAbilities.ClassAbilityCeilingText.Sentence(def, values);
                    var expected = ClassAbilityText.TokenLongDesc(def, tier, values) + (limits == null ? string.Empty : " " + limits);
                    Assert.AreEqual(expected, p[PropertyString.LongDesc], $"{def.Name} tier {tier}");
                }
            }

            // unregistered id, bad tier
            var props = new Dictionary<PropertyString, string> { [PropertyString.LongDesc] = "keep me" };
            Assert.IsFalse(ClassAbilityTokenAppraisal.Apply(props, 99999, 1, values));
            Assert.IsFalse(ClassAbilityTokenAppraisal.Apply(props, (int)ClassAbilityId.EchoCast, 0, values));
            Assert.IsFalse(ClassAbilityTokenAppraisal.Apply(props, (int)ClassAbilityId.EchoCast, 4, values));
            Assert.AreEqual("keep me", props[PropertyString.LongDesc]);
        }
        // ---------------------------------------------------------------------------------------------
        // source-scan and bare-digit guards
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void EveryPlaceholderKey_AppearsInTheHandlerFile_OrADeclaredHelperFile()
        {
            var serverDir = FindServerSourceDir();
            var checkedAny = false;

            foreach (var handler in ClassAbilityRegistry.Handlers)
            {
                var def = handler.Definition;
                var keys = ClassAbilityText.Keys(def);
                if (keys.Count == 0)
                    continue;

                checkedAny = true;

                var files = new List<string> { Path.Combine(serverDir, "ClassAbilities", "Abilities", handler.GetType().Name + ".cs") };
                foreach (var helper in def.TunableSourceFiles ?? Array.Empty<string>())
                    files.Add(Path.Combine(serverDir, helper.Replace('/', Path.DirectorySeparatorChar)));

                var texts = files.Select(f =>
                {
                    Assert.IsTrue(File.Exists(f), $"{def.Name}: expected source file {f}");
                    return File.ReadAllText(f);
                }).ToList();

                foreach (var key in UnreadKeys(def, texts))
                {
                    Assert.Fail($"{def.Name}: placeholder key {key} is read by neither the handler file nor a declared TunableSourceFiles entry - the text would quote a tunable no code uses");
                }
            }

            Assert.IsTrue(checkedAny, "no migrated ability was scanned");
        }

        /// <summary>
        /// The smallest rule that accepts a key a handler builds by concatenation or interpolation: the file
        /// quotes the FULL key, or quotes a prefix of it (at least 24 characters, so a generic stem like
        /// "class_ability_" cannot satisfy it) that ends the string literal or an interpolation hole, and the
        /// remainder of the key is a rank number or a suffix that the same file also quotes. Registering a key in
        /// PropertyManager.cs proves nothing about the handler, so that file is never a valid source.
        /// </summary>
        private static bool SourceBuildsKey(string text, string key)
        {
            if (text.Contains("\"" + key + "\"", StringComparison.Ordinal))
                return true;

            for (var length = key.Length - 1; length >= 24; length--)
            {
                var open = "\"" + key.Substring(0, length);
                var rest = key.Substring(length);
                var restIsRank = rest.All(char.IsDigit);

                if (!restIsRank && !text.Contains("\"" + rest + "\"", StringComparison.Ordinal))
                    continue;

                for (var at = text.IndexOf(open, StringComparison.Ordinal); at >= 0; at = text.IndexOf(open, at + 1, StringComparison.Ordinal))
                {
                    var after = at + open.Length;
                    if (after < text.Length && (text[after] == '"' || text[after] == '{'))
                        return true;
                }
            }

            return false;
        }

        private static List<string> UnreadKeys(ClassAbilityDefinition def, IReadOnlyCollection<string> texts) =>
            ClassAbilityText.Keys(def).Where(key => !texts.Any(text => SourceBuildsKey(text, key))).ToList();

        [TestMethod]
        public void SourceBuildsKey_AcceptsConcatenationAndInterpolation_RejectsAKeyTheFileNeverBuilds()
        {
            const string concat = "public const string P = \"class_ability_probe_widget_cooldown_r\"; Get(P + rank);";
            const string interp = "$\"class_ability_probe_widget_bonus_r{Math.Clamp(rank, 1, 3)}\"";
            const string suffixed = "const string K = \"class_ability_probe_widget_\"; var a = K + \"fire_per_rank\"; var b = K + \"acid_seconds\";";

            Assert.IsTrue(SourceBuildsKey("Get(\"class_ability_probe_widget_duration\")", "class_ability_probe_widget_duration"), "full quoted key");
            Assert.IsTrue(SourceBuildsKey(concat, "class_ability_probe_widget_cooldown_r2"));
            Assert.IsTrue(SourceBuildsKey(interp, "class_ability_probe_widget_bonus_r3"));
            Assert.IsTrue(SourceBuildsKey(suffixed, "class_ability_probe_widget_fire_per_rank"));

            // negatives: the prefix is absent, the suffix is never quoted, the remainder is not a rank, the stem is too generic
            Assert.IsFalse(SourceBuildsKey(concat, "class_ability_probe_gadget_cooldown_r2"), "prefix absent from the file");
            Assert.IsFalse(SourceBuildsKey(concat, "class_ability_probe_widget_cooldown_rx"), "remainder is neither a rank nor quoted");
            Assert.IsFalse(SourceBuildsKey(suffixed, "class_ability_probe_widget_frost_per_rank"), "suffix never quoted");
            // a 20-character stem with a quoted remainder would match if the 24-character floor were absent
            const string generic = "var s = \"class_ability_probe_\"; var t = \"widget_bonus_r2\";";
            Assert.IsFalse(SourceBuildsKey(generic, "class_ability_probe_widget_bonus_r2"), "a stem shorter than 24 characters is not a prefix");
            Assert.IsTrue(SourceBuildsKey("var s = \"class_ability_probe_widget_\"; var t = \"bonus_r2\";", "class_ability_probe_widget_bonus_r2"), "positive control: a 27-character stem is");
            Assert.IsFalse(SourceBuildsKey("class_ability_probe_widget_cooldown_r is only a comment", "class_ability_probe_widget_cooldown_r2"), "an unquoted mention is not a read");
        }

        [TestMethod]
        public void PlaceholderKeyWhoseHandlerNeverBuildsIt_FailsTheSourceScan()
        {
            // Run the real scan (UnreadKeys, the helper the scan test uses) over a MUTATED real template: one key is
            // renamed to a stem the Whirlwind handler never builds.
            var serverDir = FindServerSourceDir();
            var handlerText = File.ReadAllText(Path.Combine(serverDir, "ClassAbilities", "Abilities", nameof(WhirlwindAbility) + ".cs"));
            var original = ClassAbilityRegistry.Get(ClassAbilityId.Whirlwind);

            var originalTails = original.RankTailTemplate.All(3, "probe").ToArray();
            var intact = new ClassAbilityDefinition { Name = "probe", MaxRank = 3, RankTailTemplate = originalTails };
            CollectionAssert.AreEqual(Array.Empty<string>(), UnreadKeys(intact, new[] { handlerText }).ToArray(), "the real template passes");

            var mutated = new ClassAbilityDefinition
            {
                Name = "probe",
                MaxRank = 3,
                RankTailTemplate = originalTails.Select(t => t.Replace("class_ability_whirlwind_stamina_surcharge_r", "class_ability_whirlwind_stamina_bogus_r")).ToArray(),
            };
            CollectionAssert.AreNotEqual(originalTails, mutated.RankTailTemplate.All(3, "probe").ToArray(), "the mutation must change the template");
            CollectionAssert.AreEquivalent(
                new[] { "class_ability_whirlwind_stamina_bogus_r1", "class_ability_whirlwind_stamina_bogus_r2", "class_ability_whirlwind_stamina_bogus_r3" },
                UnreadKeys(mutated, new[] { handlerText }).ToArray(), "only the mutated keys fail");
        }

        [TestMethod]
        public void EveryPlaceholderKey_StartsWithClassAbility()
        {
            foreach (var def in AllDefinitions)
            {
                foreach (var key in ClassAbilityText.Keys(def))
                    Assert.IsTrue(key.StartsWith("class_ability_", StringComparison.Ordinal),
                        $"{def.Name}: placeholder key '{key}' is not a class_ability_ tunable; a game-wide key can be inert (cloak_cooldown_seconds is ignored unless use_cloak_proc_custom_scale)");
            }
        }
        [TestMethod]
        public void TunableSourceFiles_AreCodeDirectoriesOnly_NeverTheRegistrationOrDefaultsFiles()
        {
            var allowed = new[] { "WorldObjects/", "ClassAbilities/", "Entity/" };
            var declared = 0;

            foreach (var def in AllDefinitions)
            {
                foreach (var file in def.TunableSourceFiles ?? Array.Empty<string>())
                {
                    declared++;
                    Assert.IsTrue(allowed.Any(prefix => file.StartsWith(prefix, StringComparison.Ordinal)),
                        $"{def.Name}: TunableSourceFiles entry '{file}' must live under one of [{string.Join(", ", allowed)}]; Managers/PropertyManager.cs and config-defaults.tsv register every key and prove nothing about the handler");
                }
            }

            Assert.IsTrue(declared > 10, "expected the migrated abilities to declare helper files");
        }
        [TestMethod]
        public void MigratedTemplates_CarryNoBareNumbers_ExceptDeclaredOnes()
        {
            foreach (var def in AllDefinitions.Where(ClassAbilityText.HasPlaceholders))
            {
                CollectionAssert.AreEqual(Array.Empty<string>(), ClassAbilityText.BareNumbers(def).ToArray(),
                    $"{def.Name}: a number typed into a migrated template goes stale on a retune; bind it to a tunable or declare it in AllowedLiterals with a reason");

                foreach (var allowed in def.AllowedLiterals ?? Array.Empty<(string Literal, string Reason)>())
                    Assert.IsFalse(string.IsNullOrWhiteSpace(allowed.Reason), $"{def.Name}: allowed literal '{allowed.Literal}' needs a reason");
            }
        }

        [TestMethod]
        public void AllowedLiterals_AreEachUsedByATemplate_SoAStaleEntryCannotHideAFutureBareNumber()
        {
            var stale = AllDefinitions
                .SelectMany(def => ClassAbilityText.UnusedAllowedLiterals(def).Select(literal => $"{def.Name}: [{literal}]"))
                .ToList();

            Assert.AreEqual(0, stale.Count,
                "AllowedLiterals entries no template contains any more; delete them (a stale entry exempts that text from the bare-number guard):\n" + string.Join("\n", stale));
        }

        [TestMethod]
        public void UnusedAllowedLiterals_FlagsAnEntryNoTemplateContains_AndAcceptsOneInAnyTemplate()
        {
            var def = new ClassAbilityDefinition
            {
                Name = "probe",
                MaxRank = 3,
                DescriptionTemplate = "{v:class_ability_manabarrier_max_share|pct}% cap, stops at 50 Mana.",
                RankTailTemplate = "twice (2x) over",
                AllowedLiterals = new[] { ("50 Mana", "used by the description"), ("2x", "used by the tail"), ("reduced to 0", "stale") },
            };

            CollectionAssert.AreEqual(new[] { "reduced to 0" }, ClassAbilityText.UnusedAllowedLiterals(def).ToArray());
            Assert.AreEqual(0, ClassAbilityText.UnusedAllowedLiterals(new ClassAbilityDefinition { Name = "none", MaxRank = 1, DescriptionTemplate = "x." }).Count, "no allow-list, nothing unused");
        }

        [TestMethod]
        public void BareNumberCheck_FlagsALiteral_AndHonoursADeclaredOne()
        {
            var flagged = new ClassAbilityDefinition
            {
                Name = "probe",
                MaxRank = 3,
                DescriptionTemplate = "{v:class_ability_manabarrier_max_share|pct}% cap, stops at 50 Mana, then 7 more, at 2x.",
            };
            CollectionAssert.AreEqual(new[] { "50", "7", "2" }, ClassAbilityText.BareNumbers(flagged).ToArray());

            flagged.AllowedLiterals = new[] { ("50 Mana", "structural"), ("2x", "structural") };
            CollectionAssert.AreEqual(new[] { "7" }, ClassAbilityText.BareNumbers(flagged).ToArray(), "only declared literals are exempt");
        }

        // ---------------------------------------------------------------------------------------------
        // the website feed
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Feed_Affinity_ListsTheDeclaredSkills_PrimaryFirst_AndIsEmptyWithoutOne()
        {
            var feed = ClassAbilityDescriptionFeed.Build(ClassAbilityText.Defaults);
            var byId = feed.Abilities.ToDictionary(a => a.Id);

            foreach (var def in ClassAbilityRegistry.Abilities.Values)
            {
                var names = byId[def.Name].Affinity;
                Assert.IsNotNull(names, def.Name);
                if (def.AffinitySkill.HasValue)
                    Assert.AreEqual(ACE.Entity.Enum.SkillExtensions.ToSentence(def.AffinitySkill.Value), names[0], def.Name + ": primary affinity first");
                else if (def.AdditionalAffinitySkills == null || def.AdditionalAffinitySkills.Length == 0)
                    Assert.AreEqual(0, names.Count, def.Name + ": no affinity declared, none emitted");
                Assert.AreEqual(names.Count, names.Distinct().Count(), def.Name + ": no duplicates");
            }

            // Generated families read a stat directly and carry no affinity.
            Assert.AreEqual(0, byId["archer_training"].Affinity.Count);

            // The one ability with an additional affinity: primary first, then the extra, in declaration order.
            CollectionAssert.AreEqual(new[] { "Leadership", "Loyalty" }, byId["empoweredsummons"].Affinity.ToArray());

            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            Assert.IsTrue(JsonSerializer.Serialize(feed, options).Contains("\"affinity\":["), "wire name is affinity");
        }

        [TestMethod]
        public void Feed_HasTheWireShape_NoRawKeysOrBraces_AndEveryRegisteredAbility()
        {
            var feed = ClassAbilityDescriptionFeed.Build(ClassAbilityText.Defaults);

            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var json = JsonSerializer.Serialize(feed, options);

            Assert.IsFalse(json.Contains("class_ability_"), "the feed must never leak a tunable key");
            Assert.IsTrue(json.StartsWith("{\"abilities\":[{\"id\":"), "wire shape: abilities[].id first");

            using var doc = JsonDocument.Parse(json);
            var abilities = doc.RootElement.GetProperty("abilities");
            Assert.AreEqual(ClassAbilityRegistry.Abilities.Count, abilities.GetArrayLength());

            foreach (var a in abilities.EnumerateArray())
            {
                var description = a.GetProperty("description").GetString();
                Assert.IsFalse(description.Contains('{') || description.Contains('}'), a.GetProperty("id").GetString());
                Assert.IsTrue(a.TryGetProperty("ranks", out var ranks) && ranks.ValueKind == JsonValueKind.Array);

                foreach (var r in ranks.EnumerateArray())
                {
                    Assert.IsTrue(r.TryGetProperty("rank", out _));
                    Assert.IsFalse(r.GetProperty("text").GetString().Contains('{'));
                }
            }

            var echo = abilities.EnumerateArray().Single(a => a.GetProperty("id").GetString() == "echocast");
            Assert.AreEqual(3, echo.GetProperty("ranks").GetArrayLength());
            Assert.AreEqual(1, echo.GetProperty("ranks")[0].GetProperty("rank").GetInt32());
            Assert.AreEqual("8% chance", echo.GetProperty("ranks")[0].GetProperty("text").GetString());

            foreach (var def in AllDefinitions.Where(d => d.RankTailTemplate == null))
            {
                var emitted = abilities.EnumerateArray().Single(a => a.GetProperty("id").GetString() == def.Name);
                Assert.AreEqual(0, emitted.GetProperty("ranks").GetArrayLength(), $"{def.Name}: an unmigrated ability emits ranks: []");
            }
        }

        [TestMethod]
        public void Feed_Ids_MatchThePlannerCatalogIds()
        {
            var catalogPath = Path.Combine(FindRepoRoot(), "tools", "ca-planner", "catalog.json");
            Assert.IsTrue(File.Exists(catalogPath), catalogPath);

            using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
            var liveCatalogIds = catalog.RootElement.GetProperty("abilities").EnumerateArray()
                .Where(a => !a.GetProperty("retired").GetBoolean())
                .Select(a => a.GetProperty("id").GetString())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            var feedIds = ClassAbilityDescriptionFeed.Build(ClassAbilityText.Defaults).Abilities
                .Select(a => a.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(liveCatalogIds, feedIds);
        }

        // ---------------------------------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------------------------------

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "ClassAbilities")))
                dir = dir.Parent;

            if (dir == null)
                Assert.Inconclusive("Could not locate the repo root by walking up from the test assembly.");

            return dir.FullName;
        }

        private static string FindServerSourceDir() => Path.Combine(FindRepoRoot(), "Source", "ACE.Server");
    }
}
