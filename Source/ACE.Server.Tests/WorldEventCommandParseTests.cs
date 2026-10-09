using ACE.Server.Command.Handlers;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the WP-07 "/worldevent" argument parser (TECH-DESIGN 2.14, D6). Both
    /// TryParseStartArgs and TryParseSimulateArgs are pure statics with no engine dependency, so they are
    /// exercised directly with hand-built string arrays - no live Player/Session/Landblock anywhere here.
    /// </summary>
    [TestClass]
    public class WorldEventCommandParseTests
    {
        // -----------------------------------------------------------------------------------------------
        // TryParseStartArgs
        // -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void StartArgs_HappyPath_Here()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "Ambush", "--family", "Emberwrought", "--goal", "Kill_Count" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.IsTrue(parsed.Here);
            Assert.IsNull(parsed.AnchorId);
            Assert.IsNull(parsed.Preset);
            Assert.AreEqual("ambush", parsed.SourceId);
            Assert.AreEqual("emberwrought", parsed.FamilyId);
            Assert.AreEqual("kill_count", parsed.GoalId);
            Assert.IsFalse(parsed.Dry);

            CollectionAssert.AreEqual(new[] { "emberwrought" }, parsed.FamilyIds);
        }

        // ---- --family with two ids (two-family composition, 2026-08-29) ------------------------------

        [TestMethod]
        public void StartArgs_FamilyAcceptsTwoCommaSeparatedIds()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "ambush", "--family", "Drudge, Virindi", "--goal", "kill_count" },
                out var parsed, out var error);

            Assert.IsTrue(ok, error);
            Assert.IsNull(error);

            // Order is preserved (slot A first) and each element is trimmed and lowercased.
            CollectionAssert.AreEqual(new[] { "drudge", "virindi" }, parsed.FamilyIds);
            Assert.AreEqual("drudge", parsed.FamilyId);
        }

        [TestMethod]
        public void StartArgs_FamilyRejectsThreeIds()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--family", "a,b,c" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.AreEqual("--family takes at most 2 family ids, got 3", error);
        }

        [TestMethod]
        public void StartArgs_FamilyRejectsADuplicateId()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--family", "drudge,Drudge" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.AreEqual("--family lists 'drudge' twice - a run's two families must be different", error);
        }

        [TestMethod]
        public void StartArgs_FamilyRejectsAnEmptyElement()
        {
            foreach (var value in new[] { "drudge,", ",virindi", "drudge,,virindi", "," })
            {
                var ok = WorldEventCommands.TryParseStartArgs(
                    new[] { "--here", "--family", value }, out var parsed, out var error);

                Assert.IsFalse(ok, $"'{value}' must be refused");
                Assert.IsNull(parsed);
                Assert.AreEqual("--family takes one or two family ids separated by a comma, with no empty entries", error);
            }
        }

        [TestMethod]
        public void SimulateArgs_FamilyAcceptsTwoIdsAndRefusesTheSameMistakes()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--family", "Drudge,Virindi" }, out var parsed, out var error);

            Assert.IsTrue(ok, error);
            CollectionAssert.AreEqual(new[] { "drudge", "virindi" }, parsed.FamilyIds);

            Assert.IsFalse(WorldEventCommands.TryParseSimulateArgs(new[] { "--family", "a,b,c" }, out _, out var threeError));
            Assert.AreEqual("--family takes at most 2 family ids, got 3", threeError);

            Assert.IsFalse(WorldEventCommands.TryParseSimulateArgs(new[] { "--family", "a,a" }, out _, out var dupeError));
            Assert.AreEqual("--family lists 'a' twice - a run's two families must be different", dupeError);

            Assert.IsFalse(WorldEventCommands.TryParseSimulateArgs(new[] { "--family", "a," }, out _, out var emptyError));
            Assert.AreEqual("--family takes one or two family ids separated by a comma, with no empty entries", emptyError);
        }

        [TestMethod]
        public void StartArgs_HappyPath_Anchor_LowercasesId()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--anchor", "Blackmoor", "--source", "ambush", "--family", "f", "--goal", "g" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.IsFalse(parsed.Here);
            Assert.AreEqual("blackmoor", parsed.AnchorId);
        }

        [TestMethod]
        public void StartArgs_DryFlag_IsRecorded()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "s", "--family", "f", "--goal", "g", "--dry" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsTrue(parsed.Dry);
        }

        /// <summary>TECH-DESIGN 2.15: the minimum-duration override.</summary>
        [TestMethod]
        public void StartArgs_MinDuration_DefaultsToTheRunDefaultAndIsOverridable()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "s", "--family", "f", "--goal", "g" },
                out var parsed, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(WorldEvent.DefaultMinDurationSeconds, parsed.MinDurationSeconds,
                "omitting the flag is not the same as asking for no minimum");

            ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "s", "--family", "f", "--goal", "g", "--min-duration", "120" },
                out parsed, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(120, parsed.MinDurationSeconds);

            ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "s", "--family", "f", "--goal", "g", "--min-duration", "0" },
                out parsed, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(0, parsed.MinDurationSeconds, "0 is a legitimate 'no minimum'");
        }

        [TestMethod]
        public void StartArgs_MinDuration_RejectsNonNumbersAndNegatives()
        {
            Assert.IsFalse(WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--min-duration", "soon" }, out _, out var wordError));
            Assert.IsTrue(wordError.Contains("min-duration"), wordError);

            Assert.IsFalse(WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--min-duration", "-5" }, out _, out _));

            Assert.IsFalse(WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--min-duration" }, out _, out var missingError));
            Assert.IsTrue(missingError.Contains("needs a value"), missingError);
        }

        [TestMethod]
        public void StartArgs_Preset_IsCapturedVerbatim()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "harvest_festival", "--here" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.AreEqual("harvest_festival", parsed.Preset);
            Assert.IsFalse(parsed.Random);
            Assert.IsTrue(parsed.Here);
        }

        [TestMethod]
        public void StartArgs_RandomToken_SetsRandomAndLeavesPresetNull()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "random", "--here" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(parsed.Preset);
            Assert.IsTrue(parsed.Random);
            Assert.IsTrue(parsed.Here);
        }

        [TestMethod]
        public void StartArgs_RandomToken_IsCaseInsensitive()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "RaNdOm", "--here" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(parsed.Preset);
            Assert.IsTrue(parsed.Random);
        }

        [TestMethod]
        public void StartArgs_RandomToken_HonoursPinnedAxisFlags()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "random", "--here", "--source", "Ambush", "--goal", "Kill_Count" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsTrue(parsed.Random);
            Assert.AreEqual("ambush", parsed.SourceId);
            Assert.AreEqual("kill_count", parsed.GoalId);
        }

        [TestMethod]
        public void StartArgs_MissingValue_Errors()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source" },
                out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.AreEqual("--source needs a value", error);
        }

        [TestMethod]
        public void StartArgs_UnknownFlag_Errors()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--bogus", "x" },
                out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.AreEqual("unknown flag --bogus", error);
        }

        [TestMethod]
        public void StartArgs_HereAndAnchor_MutuallyExclusive()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--anchor", "blackmoor" },
                out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("use --here or --anchor, not both", error);
        }

        [TestMethod]
        public void StartArgs_NeitherHereNorAnchor_Errors()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--source", "s", "--family", "f", "--goal", "g" },
                out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("one of --here or --anchor is required", error);
        }

        [TestMethod]
        public void StartArgs_EmptyArgs_RequiresAnchor()
        {
            var ok = WorldEventCommands.TryParseStartArgs(System.Array.Empty<string>(), out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("one of --here or --anchor is required", error);
        }

        [TestMethod]
        public void StartArgs_NullArgs_DoesNotThrow()
        {
            var ok = WorldEventCommands.TryParseStartArgs(null, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("one of --here or --anchor is required", error);
        }

        [TestMethod]
        public void StartArgs_RepeatedFlag_LastWins()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "first", "--source", "second" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.AreEqual("second", parsed.SourceId);
        }

        [TestMethod]
        public void StartArgs_FlagsAreCaseInsensitive()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--HERE", "--Source", "s" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsTrue(parsed.Here);
            Assert.AreEqual("s", parsed.SourceId);
        }

        [TestMethod]
        public void StartArgs_GarbageInput_NeverThrows()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "--family", "--", "----", "", " " },
                out var parsed, out var error);

            // Whatever the verdict, this must return normally rather than throw.
            Assert.IsFalse(ok);
            Assert.IsNotNull(error);
        }

        // -----------------------------------------------------------------------------------------------
        // TryParseSimulateArgs
        // -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void SimulateArgs_Defaults_WhenNoFlagsGiven()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(System.Array.Empty<string>(), out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.AreEqual(8, parsed.Players);
            Assert.AreEqual(100, parsed.Level);
            Assert.IsNull(parsed.SourceId);
            Assert.IsNull(parsed.FamilyId);
            Assert.IsNull(parsed.GoalId);
        }

        [TestMethod]
        public void SimulateArgs_HappyPath()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--players", "5", "--level", "80", "--source", "Ambush", "--family", "F", "--goal", "G" },
                out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.AreEqual(5, parsed.Players);
            Assert.AreEqual(80, parsed.Level);
            Assert.AreEqual("ambush", parsed.SourceId);
            Assert.AreEqual("f", parsed.FamilyId);
            Assert.AreEqual("g", parsed.GoalId);
        }

        [TestMethod]
        public void SimulateArgs_PlayersMustBePositiveInteger()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--players", "0" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("--players needs a positive integer", error);
        }

        [TestMethod]
        public void SimulateArgs_PlayersNegative_Errors()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--players", "-3" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("--players needs a positive integer", error);
        }

        [TestMethod]
        public void SimulateArgs_PlayersNonNumeric_Errors()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--players", "abc" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("--players needs a positive integer", error);
        }

        [TestMethod]
        public void SimulateArgs_LevelMustBePositiveInteger()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--level", "0" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("--level needs a positive integer", error);
        }

        [TestMethod]
        public void SimulateArgs_MissingValue_Errors()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--players" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("--players needs a value", error);
        }

        [TestMethod]
        public void SimulateArgs_UnknownFlag_Errors()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--bogus", "x" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.AreEqual("unknown flag --bogus", error);
        }

        [TestMethod]
        public void SimulateArgs_RepeatedFlag_LastWins()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--players", "3", "--players", "9" }, out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.AreEqual(9, parsed.Players);
        }

        [TestMethod]
        public void SimulateArgs_NullArgs_ReturnsDefaults()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(null, out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.AreEqual(8, parsed.Players);
            Assert.AreEqual(100, parsed.Level);
        }

        [TestMethod]
        public void SimulateArgs_GarbageInput_NeverThrows()
        {
            var ok = WorldEventCommands.TryParseSimulateArgs(
                new[] { "--players", "--level", "--", "" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNotNull(error);
        }

        // -----------------------------------------------------------------------------------------------
        // Unknown subcommand dispatch never throws.
        //
        // HandleWorldEvent itself cannot be exercised here: every non-parser branch touches
        // session.Network / session.Player (WorldEventManager.Enabled, StatusText, Reply's
        // EnqueueSend, ...), and per TECH-DESIGN D6 no test in this project may construct a live
        // Session or Player. A null Session would NRE inside Reply() the first time any branch tries
        // to respond, so "HandleWorldEvent(null, "bogus")" is not a safe or meaningful test target -
        // skipped per the WP-07 spec's own allowance. The parser-level "never throws on garbage" cases
        // above are the D6-compatible substitute: they cover the same "malformed input must not throw"
        // property for the part of the command that IS pure.
        // -----------------------------------------------------------------------------------------------
    }
}
