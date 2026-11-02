using System;
using System.Linq;

using ACE.Server.Command.Handlers;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles.Matron;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    [TestClass]
    public class PinnacleCommandParseTests
    {
        [TestMethod]
        public void Status_Parses()
        {
            var r = PinnacleCommandRequest.Parse(new[] { "status" });
            Assert.IsTrue(r.IsValid, r.Error);
            Assert.AreEqual(PinnacleCommandKind.Status, r.Kind);
        }

        [TestMethod]
        public void Reset_Parses_CaseInsensitive()
        {
            var r = PinnacleCommandRequest.Parse(new[] { "RESET" });
            Assert.IsTrue(r.IsValid, r.Error);
            Assert.AreEqual(PinnacleCommandKind.Reset, r.Kind);
        }

        [TestMethod]
        public void Phase_WithNumber_Parses()
        {
            var r = PinnacleCommandRequest.Parse(new[] { "phase", "4" });
            Assert.IsTrue(r.IsValid, r.Error);
            Assert.AreEqual(PinnacleCommandKind.Phase, r.Kind);
            Assert.AreEqual(4, r.Phase);
        }

        [TestMethod]
        public void Phase_BadNumbers_Refused()
        {
            foreach (var bad in new[] { "0", "-1", "abc", "1.5", "100", "", "+3" })
                Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "phase", bad }).IsValid, "phase '" + bad + "' must be refused");
        }

        [TestMethod]
        public void Phase_WrongArgumentCount_Refused()
        {
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "phase" }).IsValid);
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "phase", "2", "3" }).IsValid);
        }

        [TestMethod]
        public void StatusAndReset_ExtraArguments_Refused()
        {
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "status", "x" }).IsValid);
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "reset", "x" }).IsValid);
        }

        [TestMethod]
        public void Enter_ParsesKindAndLevel()
        {
            var r = PinnacleCommandRequest.Parse(new[] { "enter", "matron", "300" });
            Assert.IsTrue(r.IsValid, r.Error);
            Assert.AreEqual(PinnacleCommandKind.Enter, r.Kind);
            Assert.AreEqual("matron", r.PinnacleKind);
            Assert.AreEqual(300, r.Level);
        }

        [TestMethod]
        public void Enter_RefusesLevel0And1000()
        {
            foreach (var l in new[] { "0", "1000", "x", "-5", "+3", "" })
                Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "enter", "matron", l }).IsValid, "level '" + l + "' must be refused");
        }

        [TestMethod]
        public void Enter_WrongArgumentCount_Refused()
        {
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "enter" }).IsValid);
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "enter", "matron" }).IsValid);
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "enter", "matron", "300", "x" }).IsValid);
        }

        [TestMethod]
        public void Enter_ParsesOverrides()
        {
            var r = PinnacleCommandRequest.Parse(new[] { "enter", "matron", "300", "flow_rate=0.05", "damage=15", "max_z=-40.5" });
            Assert.IsTrue(r.IsValid, r.Error);
            Assert.AreEqual(3, r.Overrides.Count);

            // known keys are applied to the script's settings, over the base, for that copy only
            Assert.IsTrue(MatronOverrides.TryCreateScript(MatronSettings.Defaults, r.Overrides, out var script, out var error), error);
            Assert.AreEqual(0.05, script.Settings.FlowRate);
            Assert.AreEqual(15.0, script.Settings.Damage);
            Assert.AreEqual(-40.5, script.Settings.MaxZ);
            Assert.AreEqual(MatronSettings.Defaults.FinaleRate, script.Settings.FinaleRate, "an unnamed key keeps its base value");
            Assert.AreEqual(0.03, MatronSettings.Defaults.FlowRate, "the shared defaults are never mutated");

            // an unknown key is refused by name and builds no script
            var bad = PinnacleCommandRequest.Parse(new[] { "enter", "matron", "300", "flow_rat=0.05" });
            Assert.IsTrue(bad.IsValid, "the parser leaves key vetting to the kind that owns the keys");
            Assert.IsFalse(MatronOverrides.TryCreateScript(MatronSettings.Defaults, bad.Overrides, out var none, out var unknown));
            Assert.IsNull(none);
            StringAssert.Contains(unknown, "flow_rat");

            // an unparsable value is refused by key
            var nan = PinnacleCommandRequest.Parse(new[] { "enter", "matron", "300", "damage=lots" });
            Assert.IsFalse(MatronOverrides.TryCreateScript(MatronSettings.Defaults, nan.Overrides, out _, out var notNumber));
            StringAssert.Contains(notNumber, "damage");

            // a malformed or repeated token fails the parse itself, naming the token
            var malformed = PinnacleCommandRequest.Parse(new[] { "enter", "matron", "300", "flow_rate" });
            Assert.IsFalse(malformed.IsValid);
            StringAssert.Contains(malformed.Error, "flow_rate");
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "enter", "matron", "300", "damage=1", "damage=2" }).IsValid);

            // out-of-range values and broken cross-field rules are refused, naming the key, in a chat-sized line
            foreach (var (token, named) in new[]
            {
                ("flow_escalate_seconds=0", "flow_escalate_seconds"), ("flow_escalate_seconds=601", "flow_escalate_seconds"),
                ("drain_rate=0", "drain_rate"), ("drain_rate=1", "drain_rate"), ("lava_clear_rate=-3", "lava_clear_rate"),
                ("flow_rate=0", "flow_rate"), ("finale_rate=3", "finale_rate"), ("flow_rate_cap=0.0001", "flow_rate_cap"),
                ("damage=-1", "damage"), ("damage_per_m=-1", "damage_per_m"), ("damage_max=-1", "damage_max"),
                ("finale_damage_scale=0", "finale_damage_scale"), ("finale_damage_scale=11", "finale_damage_scale"),
                ("max_z=-37", "max_z"), ("fill_floor_z=-61", "fill_floor_z"), ("hazard_pct_max=6", "hazard_pct_max"),
                ("flow_rate=0.5", "flow_rate_cap"),               // cap 0.24 < rate 0.5
                ("fill_floor_z=-44.5", "fill_floor_z"),           // floor not below max_z -44.5
                ("max_z=-59.99", "fill_floor_z"),                 // max_z at or below the floor -59.95
                ("hazard_pct_min=0.9", "hazard_pct_min"),         // min above max 0.75
                ("wall_fx=3", "wall_fx"), ("wall_fx=-1", "wall_fx"), ("wall_fx=1.5", "wall_fx"),
                ("flow_delay=-1", "flow_delay"), ("flow_delay=31", "flow_delay"),
                ("submerged_damage=-1", "submerged_damage"), ("submerged_damage=10001", "submerged_damage"),
                ("submerged_per_m=-1", "submerged_per_m"), ("submerged_per_m=1001", "submerged_per_m"),
                ("submerged_max=-1", "submerged_max"), ("submerged_max=100001", "submerged_max"),
                ("head_height=0.4", "head_height"), ("head_height=10.1", "head_height"),
            })
            {
                var req = PinnacleCommandRequest.Parse(new[] { "enter", "matron", "300", token });
                Assert.IsTrue(req.IsValid, token);
                Assert.IsFalse(MatronOverrides.TryApply(MatronSettings.Defaults, req.Overrides, out var kept, out var refusal), token + " must be refused");
                StringAssert.Contains(refusal, named, token);
                Assert.IsTrue(refusal.Length <= 250, token);
                Assert.AreEqual(MatronSettings.Defaults, kept, "a refused override leaves the base unchanged");
            }

            // cross-field rules hold over the merged result, so a consistent pair is accepted
            Assert.IsTrue(MatronOverrides.TryApply(MatronSettings.Defaults, HazardArgs.Parse("flow_rate=0.5 flow_rate_cap=1"), out _, out var pairError), pairError);
            Assert.IsTrue(MatronOverrides.TryApply(MatronSettings.Defaults, HazardArgs.Parse("fill_floor_z=-50 max_z=-45"), out _, out pairError), pairError);

            // the submerged tier's boundaries are accepted and land on the record
            Assert.IsTrue(MatronOverrides.TryApply(MatronSettings.Defaults, HazardArgs.Parse("submerged_damage=0 submerged_per_m=1000 submerged_max=100000 head_height=0.5"), out var tier, out pairError), pairError);
            Assert.AreEqual(0.0, tier.SubmergedDamage);
            Assert.AreEqual(1000.0, tier.SubmergedPerM);
            Assert.AreEqual(100000.0, tier.SubmergedMax);
            Assert.AreEqual(0.5, tier.HeadHeight);
            Assert.IsTrue(MatronOverrides.TryApply(MatronSettings.Defaults, HazardArgs.Parse("submerged_damage=10000 head_height=10"), out tier, out pairError), pairError);
            Assert.AreEqual(10000.0, tier.SubmergedDamage);
        }

        [TestMethod]
        public void MatronOverrides_EveryKeyIsTheSnakeCaseOfASettingsField_AndTakesEffect()
        {
            var fields = typeof(MatronSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(int))
                .Select(p => System.Text.RegularExpressions.Regex.Replace(p.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant())
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(fields, MatronOverrides.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(), "one override key per double or int field");

            foreach (var key in MatronOverrides.Keys)
            {
                // some in-range candidate (the ranges differ per key) is accepted and changes the settings
                var changed = new[] { "0.02", "0.5", "1.5", "-1.5", "-0.5", "5", "50", "-50", "2.5", "1" }.Any(v =>
                    MatronOverrides.TryApply(MatronSettings.Defaults, HazardArgs.Parse(key + "=" + v), out var applied, out _) && !applied.Equals(MatronSettings.Defaults));

                Assert.IsTrue(changed, key + " must be accepted for some in-range value and change the settings");
            }
        }

        [TestMethod]
        public void Usage_IsAtMost250Chars() => Assert.IsTrue(PinnacleCommands.Usage.Length <= 250, "usage is " + PinnacleCommands.Usage.Length);

        [TestMethod]
        public void HelpText_ListsEveryMatronOverrideKey_AndEveryLineFitsAChatLine()
        {
            var attribute = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<ACE.Server.Command.CommandHandlerAttribute>(
                typeof(PinnacleCommands).GetMethod(nameof(PinnacleCommands.HandlePinnacle))).Single(a => a.Command == "pinnacle");
            var help = attribute.Usage;
            var words = new System.Collections.Generic.HashSet<string>(System.Text.RegularExpressions.Regex.Split(help, @"[^a-z0-9_]+"), StringComparer.Ordinal);

            Assert.IsTrue(MatronOverrides.Keys.Count > 0);
            foreach (var key in MatronOverrides.Keys)
                Assert.IsTrue(words.Contains(key), $"the /pinnacle help text names {key}");

            Assert.IsFalse(words.Contains("wall_fx_mode"), "control: a key the table does not have is not found by accident");
            StringAssert.Contains(help, "wall_fx (0 panels, 1 fog, 2 both)");

            foreach (var line in help.Split('\n'))
                Assert.IsTrue(line.Length <= 250, $"help line is {line.Length} chars: {line}");
        }

        [TestMethod]
        public void Empty_Null_Unknown_Refused()
        {
            Assert.IsFalse(PinnacleCommandRequest.Parse(null).IsValid);
            Assert.IsFalse(PinnacleCommandRequest.Parse(new string[0]).IsValid);
            Assert.IsFalse(PinnacleCommandRequest.Parse(new[] { "bogus" }).IsValid);
        }
    }
}
