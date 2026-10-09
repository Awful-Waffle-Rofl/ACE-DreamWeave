using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The one-shot Class Ability overhaul respec (the deployment migration
    /// Player.ApplyClassAbilityOverhaulRespec runs as a login sweep).
    ///
    /// ACE.Server.Tests can never construct a live Player, so what is pinned here is every decision
    /// that could be extracted as a pure function: the post-sweep ledger identity, the "is this row
    /// sweepable" predicate, the empty-abilities facet blob, the pool arithmetic a facet switch
    /// performs immediately afterwards, and the notice copy. The Player-bound wiring (the guard, the
    /// ordering inside PlayerEnterWorld, the facet writes actually landing, the token consume) is owed
    /// a live pass - see Docs/VERIFY-QUEUE.md. Nothing here asserts a tautology in place of that.
    /// </summary>
    [TestClass]
    public class ClassAbilityOverhaulRespecTests
    {
        // ---- The acceptance identity -----------------------------------------------------------

        /// <summary>
        /// THE ONE THAT IS EASY TO GET WRONG. `unexplained == 0` is NOT the post-sweep expectation and
        /// a test asserting it would fail correctly: the sweep leaves available == totalEarned and
        /// ownedCost == 0, so totalEarned - available - ownedCost - sinkSpend is exactly -sinkSpend.
        /// </summary>
        [TestMethod]
        public void PostSweep_UnexplainedIsExactlyMinusSinkSpend()
        {
            Assert.AreEqual(-2, CapLedger.UnexplainedAfterOverhaulRespec(totalEarned: 22, sinkSpend: 2));
            Assert.AreEqual(-7, CapLedger.UnexplainedAfterOverhaulRespec(totalEarned: 9, sinkSpend: 7));
            Assert.AreEqual(-5, CapLedger.UnexplainedAfterOverhaulRespec(totalEarned: 0, sinkSpend: 5));
        }

        [TestMethod]
        public void PostSweep_UnexplainedIsZero_ForACharacterWithNoRecordedSinks()
        {
            Assert.AreEqual(0, CapLedger.UnexplainedAfterOverhaulRespec(totalEarned: 22, sinkSpend: 0));
            Assert.AreEqual(0, CapLedger.UnexplainedAfterOverhaulRespec(totalEarned: 0, sinkSpend: 0));
        }

        /// <summary>
        /// The helper must agree with the real identity rather than reimplementing it, so the acceptance
        /// value moves if and only if CapLedger.Unexplained itself does.
        /// </summary>
        [TestMethod]
        public void PostSweep_HelperAgreesWithTheFullIdentity()
        {
            Assert.AreEqual(
                CapLedger.Unexplained(totalEarned: 22, available: 22, ownedCost: 0, sinkSpend: 3),
                CapLedger.UnexplainedAfterOverhaulRespec(totalEarned: 22, sinkSpend: 3));
        }

        // ---- The sweepable-row predicate -------------------------------------------------------

        [TestMethod]
        public void IsSweepableQuestRow_TrueForALiveAbilitysQuestKey()
        {
            foreach (var definition in ClassAbilityRegistry.Abilities.Values)
            {
                Assert.IsTrue(ClassAbilityOverhaulRespec.IsSweepableQuestRow(ClassAbilityRegistry.QuestKey(definition)),
                    $"{definition.Name}'s own quest key must be sweepable - the sweep clears every rank the live registry can resolve");
            }
        }

        /// <summary>
        /// A ClassAbility_* row the registry cannot resolve is a RETIRED ability's row, and it must be
        /// left alone: SweepRetiredClassAbilities prices those from the historical table and refunds
        /// them, and erasing one here would destroy rank data this sweep cannot price.
        /// </summary>
        [TestMethod]
        public void IsSweepableQuestRow_FalseForAnUnresolvableClassAbilityRow()
        {
            Assert.IsFalse(ClassAbilityOverhaulRespec.IsSweepableQuestRow("ClassAbility_NoSuchAbilityNameExistsHere"));
        }

        [TestMethod]
        public void IsSweepableQuestRow_FalseForAnUnrelatedQuestRow_AndForNull()
        {
            Assert.IsFalse(ClassAbilityOverhaulRespec.IsSweepableQuestRow("ArwicRecallPortal"));
            Assert.IsFalse(ClassAbilityOverhaulRespec.IsSweepableQuestRow("PickupBoon_Quickhand"));
            Assert.IsFalse(ClassAbilityOverhaulRespec.IsSweepableQuestRow(string.Empty));
            Assert.IsFalse(ClassAbilityOverhaulRespec.IsSweepableQuestRow(null));
        }

        // ---- The facet blob the sweep writes ---------------------------------------------------

        /// <summary>
        /// Every stored facet row is rewritten to this, so the blob has to read back as "no abilities"
        /// through the same deserializer the switch path uses.
        /// </summary>
        [TestMethod]
        public void EmptyAbilitiesJson_RoundTripsToNoAbilities()
        {
            var json = ClassAbilityOverhaulRespec.EmptyAbilitiesJson;

            Assert.AreEqual("{}", json);
            Assert.AreEqual(0, FacetSnapshot.DeserializeAbilities(json).Count);
        }

        // ---- What a facet switch does immediately after the sweep ------------------------------

        /// <summary>
        /// The reason every stored build is emptied rather than only the live one. With every stored
        /// ability set empty, a switch computes an outgoing spend of 0 and an incoming spend of 0, so
        /// the pool is left exactly as the sweep set it and "Available == Earned" survives the first
        /// switch instead of being corrupted by it.
        /// </summary>
        [TestMethod]
        public void FacetSwitchAfterTheSweep_LeavesAvailablePointsUnchanged()
        {
            foreach (var available in new[] { 0, 1, 22, 1234 })
            {
                var after = FacetPools.AvailableClassAbilityPointsAfterSwap(
                    available, outgoingSpent: 0, incomingSpent: 0, out var shortfall);

                Assert.AreEqual(available, after, "an empty-to-empty swap must not move the class ability point pool");
                Assert.AreEqual(0, shortfall, "an empty incoming build can never be short");
            }
        }

        // ---- The login notice ------------------------------------------------------------------

        [TestMethod]
        public void Notice_CarriesTheApprovedCopy_WithThePointTotalSubstituted()
        {
            var paragraphs = ClassAbilityOverhaulRespec.BuildNoticeParagraphs(12, null);

            Assert.AreEqual(4, paragraphs.Count, "a character with no consumed tokens gets exactly the four approved paragraphs");

            Assert.AreEqual("Your class abilities have been rebuilt.", paragraphs[0]);

            Assert.AreEqual(
                "Every class ability you had learned has been unlearned, and all of your class ability points have been returned - you have 12 to spend. "
                + "Nothing was lost: costs have come down across the board, many abilities are stronger, and several are new, so your old build is very likely not the one you want any more.",
                paragraphs[1]);

            Assert.AreEqual(
                "Affinity skills now multiply an ability's own bonus instead of adding a flat amount, so investing deeply in one ability now beats splashing a single rank into several.",
                paragraphs[2]);

            Assert.AreEqual(
                "Visit a class ability trainer to spend your points. Tier 2 and Tier 3 abilities unlock again as you reinvest.",
                paragraphs[3]);
        }

        [TestMethod]
        public void Notice_MentionsConsumedTokens_OnlyWhenThereWereAny()
        {
            Assert.AreEqual(4, ClassAbilityOverhaulRespec.BuildNoticeParagraphs(3, new List<string>()).Count,
                "an empty token list must not add the token sentence");

            var one = ClassAbilityOverhaulRespec.BuildNoticeParagraphs(3, new List<string> { "Nether Rush Training Token" });

            Assert.AreEqual(5, one.Count);
            StringAssert.Contains(one[4], "Nether Rush Training Token");
            StringAssert.Contains(one[4], "One unused class ability training token was");

            var two = ClassAbilityOverhaulRespec.BuildNoticeParagraphs(3, new List<string> { "Token A", "Token B" });

            Assert.AreEqual(5, two.Count);
            StringAssert.Contains(two[4], "2 unused class ability training tokens were");
            StringAssert.Contains(two[4], "Token A, Token B");
        }

        /// <summary>
        /// The player-facing copy is in-game text, so it must carry ASCII hyphens only - never an
        /// en-dash or em-dash, which is a standing repo-wide rule.
        /// </summary>
        [TestMethod]
        public void Notice_UsesAsciiHyphensOnly()
        {
            foreach (var paragraph in ClassAbilityOverhaulRespec.BuildNoticeParagraphs(12, new List<string> { "Token A" }))
            {
                // Escapes, not the literal characters: this source file is itself covered by the
                // ASCII-only rule, so spelling the dashes out here would plant what it forbids.
                Assert.IsFalse(paragraph.Contains("\u2013"), $"en-dash in player-facing copy: {paragraph}");
                Assert.IsFalse(paragraph.Contains("\u2014"), $"em-dash in player-facing copy: {paragraph}");
            }
        }

        // ---- Clearing the stored facet builds --------------------------------------------------

        private static CharacterFacet Row(byte slot, string abilitiesJson)
            => new CharacterFacet
            {
                CharacterId = 1,
                Slot = slot,
                SkillsJson = "[{\"s\":1}]",
                AbilitiesJson = abilitiesJson,
                EquipJson = "[{\"wcid\":123}]",
                AttrsJson = "{\"Strength\":100}",
            };

        [TestMethod]
        public void TryClearStoredAbilities_EmptiesOnlyTheAbilitySet_LeavingTheOtherColumnsUntouched()
        {
            var row = Row(2, "{\"Nether Rush\":3}");

            var ok = ClassAbilityOverhaulRespec.TryClearStoredAbilities(
                new[] { row }, 50, (r, callback) => callback(true), out var cleared);

            Assert.IsTrue(ok);
            Assert.AreEqual(1, cleared);
            Assert.AreEqual("{}", row.AbilitiesJson, "the stored ability set must be emptied");
            Assert.AreEqual("[{\"s\":1}]", row.SkillsJson, "skills_Json must not be touched");
            Assert.AreEqual("[{\"wcid\":123}]", row.EquipJson, "equip_Json must not be touched");
            Assert.AreEqual("{\"Strength\":100}", row.AttrsJson, "attrs_Json must not be touched");
        }

        [TestMethod]
        public void TryClearStoredAbilities_SkipsRowsAlreadyEmpty_AndWritesNothing()
        {
            var writes = 0;

            var ok = ClassAbilityOverhaulRespec.TryClearStoredAbilities(
                new[] { Row(1, "{}"), Row(2, "{}") }, 50,
                (r, callback) => { writes++; callback(true); }, out var cleared);

            Assert.IsTrue(ok);
            Assert.AreEqual(0, cleared, "a row already holding an empty set is not a row this sweep changed");
            Assert.AreEqual(0, writes, "no write should be issued for a row that needs no change");
        }

        /// <summary>
        /// THE DEFECT THIS GUARDS. When a facet write does not confirm, the sweep must be told so it can
        /// abandon the run WITHOUT stamping its one-shot guard. Stamping over an unwritten row would
        /// leave that slot holding a fully-ranked build with no retry possible, and the first switch back
        /// to it would re-apply a build the player no longer paid for.
        /// </summary>
        [TestMethod]
        public void TryClearStoredAbilities_WriteNeverConfirms_ReturnsFalseSoTheGuardIsNotStamped()
        {
            var ok = ClassAbilityOverhaulRespec.TryClearStoredAbilities(
                new[] { Row(2, "{\"Nether Rush\":3}") }, 50,
                (r, callback) => { /* never confirms - a backed-up shard write queue */ }, out var cleared);

            Assert.IsFalse(ok, "an unconfirmed facet write must be reported as a failure");
            Assert.AreEqual(0, cleared, "nothing may be reported as cleared when the write never landed");
        }

        [TestMethod]
        public void TryClearStoredAbilities_WriteReportsFailure_ReturnsFalse()
        {
            var ok = ClassAbilityOverhaulRespec.TryClearStoredAbilities(
                new[] { Row(2, "{\"Nether Rush\":3}") }, 50,
                (r, callback) => callback(false), out _);

            Assert.IsFalse(ok);
        }

        [TestMethod]
        public void TryClearStoredAbilities_NoFacetRowsAtAll_IsASuccessWithNothingCleared()
        {
            Assert.IsTrue(ClassAbilityOverhaulRespec.TryClearStoredAbilities(
                null, 50, (r, callback) => callback(true), out var cleared));

            Assert.AreEqual(0, cleared);
        }

        // ---- The ordering that makes an abandoned run a true no-op -----------------------------

        /// <summary>
        /// THE PROPERTY THE REORDER BUYS, pinned so it does not rest on someone reading the method.
        ///
        /// The facet clear is the ONLY step of the sweep that can fail, so it must run BEFORE anything
        /// mutates the character. If it ran later, an unconfirmed facet write would abandon the run with
        /// live ranks already erased and points already moved - and those in-memory changes persist via
        /// the routine player save, leaving a half-applied sweep whose stored facet still holds a
        /// paid-for build. Running it first is what makes "a retry costs a repeated sweep, never a double
        /// credit" true of every step rather than most of them.
        ///
        /// A source-text test because ACE.Server.Tests cannot construct a live Player, following
        /// FacetAttributeApplySourceTests and CapLedgerDaoShapeTests. The body is bounded by the NEXT
        /// member's doc comment, never by end-of-file, so a later method mentioning the same identifiers
        /// cannot satisfy it.
        /// </summary>
        [TestMethod]
        public void Sweep_ClearsStoredFacets_BeforeItMutatesAnythingLive()
        {
            var body = SweepMethodBody();

            var facetClear = body.IndexOf("TryClearStoredAbilities", StringComparison.Ordinal);

            Assert.IsTrue(facetClear >= 0, "the sweep no longer calls TryClearStoredAbilities at all");

            foreach (var mutation in new[]
            {
                "EraseQuest",
                "CharacterChangesDetected",
                "AdjustClassAbilityPoints",
                "ConsumeClassAbilityTokensForOverhaulRespec",
                "SetProperty(PropertyInt.ClassAbilityOverhaulRespecDone",
            })
            {
                var at = body.IndexOf(mutation, StringComparison.Ordinal);

                Assert.IsTrue(at >= 0,
                    $"the sweep no longer contains '{mutation}' - this guard has gone stale and must be updated, not deleted");

                Assert.IsTrue(facetClear < at,
                    $"'{mutation}' runs BEFORE the stored facet builds are cleared and confirmed. An unconfirmed facet write would then abandon the sweep with the character already part-way mutated.");
            }
        }

        private static string SweepMethodBody()
        {
            var path = FindUp("Source/ACE.Server/WorldObjects/Player_ClassAbilities.cs");

            Assert.IsNotNull(path, $"could not find Player_ClassAbilities.cs by walking up from {AppContext.BaseDirectory}");

            var src = File.ReadAllText(path).Replace("\r\n", "\n");

            var start = src.IndexOf("public void ApplyClassAbilityOverhaulRespec()", StringComparison.Ordinal);

            Assert.IsTrue(start >= 0, "ApplyClassAbilityOverhaulRespec is gone or renamed");

            // Bounded by the NEXT member's doc comment. An unbounded slice would run to end-of-file and
            // could be satisfied by identifiers belonging to some later method, passing for the wrong
            // reason. Starting at the SIGNATURE also keeps this method's own summary out of the slice.
            var end = src.IndexOf("\n        /// <summary>", start, StringComparison.Ordinal);

            Assert.IsTrue(end > start, "could not find the next member after ApplyClassAbilityOverhaulRespec to bound its body");

            var body = src.Substring(start, end - start);

            // COMMENT LINES ARE STRIPPED, and that is load-bearing rather than tidiness. The first
            // version of this guard searched the raw text and matched 'CharacterChangesDetected' inside
            // the explanatory comment ABOVE the facet clear, so it failed against correctly ordered code.
            // A source-order guard that prose can satisfy - or break - is measuring the wrong thing, so
            // only executable lines survive into what the caller index-searches.
            var code = new List<string>();

            foreach (var line in body.Split('\n'))
            {
                if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    code.Add(line);
            }

            return string.Join("\n", code);
        }

        private static string FindUp(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
