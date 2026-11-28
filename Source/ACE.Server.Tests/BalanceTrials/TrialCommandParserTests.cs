using System.Linq;

using ACE.Server.BalanceTrials;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>Pins the @trial argument parser and the status line. Pure: no world, no PropertyManager.</summary>
    [TestClass]
    public class TrialCommandParserTests
    {
        [TestMethod]
        public void Parses_each_verb_and_its_arity()
        {
            var start = TrialCommandParser.Parse(new[] { "start", "solo-elite", "p50-mage" });
            Assert.IsNull(start.Error);
            Assert.AreEqual("start", start.Verb);
            CollectionAssert.AreEqual(new[] { "solo-elite", "p50-mage" }, start.Args);

            foreach (var verb in new[] { "next", "status", "abort", "list" })
            {
                var r = TrialCommandParser.Parse(new[] { verb });
                Assert.IsNull(r.Error, verb);
                Assert.AreEqual(verb, r.Verb);
                Assert.AreEqual(0, r.Args.Length, verb);

                Assert.IsNotNull(TrialCommandParser.Parse(new[] { verb, "extra" }).Error, verb + " takes no arguments");
            }

            var rebuild = TrialCommandParser.Parse(new[] { "REBUILD", "p50-mage", "3" });
            Assert.IsNull(rebuild.Error);
            Assert.AreEqual("rebuild", rebuild.Verb);
            CollectionAssert.AreEqual(new[] { "p50-mage", "3" }, rebuild.Args);

            // start needs exactly two arguments: the archetype has no default
            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "start" }).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "start", "solo-elite" }).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "start", "a", "b", "c" }).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "rebuild", "p50-mage" }).Error);
        }

        [TestMethod]
        public void Campaign_start_follows_the_frozen_grammar()
        {
            // The frozen contract's line, split on spaces the way the command manager hands it over.
            var line = "start t400-arch-solo archmage-400 for 60 campaign c202611241200-ab12 driver rynth-ui:1a2b3c4d".Split(' ');
            var parsed = TrialCommandParser.Parse(line);
            Assert.IsNull(parsed.Error);
            Assert.AreEqual("start", parsed.Verb);
            Assert.IsTrue(TrialCommandParser.TryParseCampaignStart(parsed.Args, out var c, out var error), error);
            Assert.AreEqual(new TrialCommandParser.CampaignStart(60, "c202611241200-ab12", "rynth-ui:1a2b3c4d"), c);

            // The driver is optional.
            Assert.IsTrue(TrialCommandParser.TryParseCampaignStart(new[] { "s", "a", "for", "1", "campaign", "x" }, out c, out error), error);
            Assert.AreEqual(new TrialCommandParser.CampaignStart(1, "x", null), c);
            Assert.IsTrue(TrialCommandParser.TryParseCampaignStart(new[] { "s", "a", "for", "720", "campaign", new string('a', 32), "driver", new string('b', 40) }, out _, out error), error);

            // The plain start is untouched: two arguments, no campaign.
            Assert.IsNull(TrialCommandParser.Parse(new[] { "start", "s", "a" }).Error);
            Assert.IsFalse(TrialCommandParser.TryParseCampaignStart(new[] { "s", "a" }, out _, out _));

            foreach (var bad in new[]
            {
                new[] { "s", "a", "for", "0", "campaign", "x" },
                new[] { "s", "a", "for", "721", "campaign", "x" },
                new[] { "s", "a", "for", "+5", "campaign", "x" },
                new[] { "s", "a", "for", "60.0", "campaign", "x" },
                new[] { "s", "a", "for", "60", "campaign", "X" },
                new[] { "s", "a", "for", "60", "campaign", "a_b" },
                new[] { "s", "a", "for", "60", "campaign", new string('a', 33) },
                new[] { "s", "a", "for", "60", "campaign", "x", "driver", "Rynth" },
                new[] { "s", "a", "for", "60", "campaign", "x", "driver", "a b" },
                new[] { "s", "a", "for", "60", "campaign", "x", "driver", new string('b', 41) },
                new[] { "s", "a", "campaign", "x", "for", "60" },
                new[] { "s", "a", "FOR", "60", "campaign", "x" },
                new[] { "s", "a", "for", "60", "campaign", "x", "driver" },
                new[] { "s", "a", "for", "60", "campaign", "x", "label", "y" },
            })
            {
                var args = new[] { "start" }.Concat(bad).ToArray();
                Assert.IsNotNull(TrialCommandParser.Parse(args).Error, string.Join(" ", args));
            }
        }

        [TestMethod]
        public void Campaign_verbs_take_a_sub_verb_and_an_id()
        {
            foreach (var sub in new[] { "status", "stop", "report", "STOP" })
            {
                var r = TrialCommandParser.Parse(new[] { "campaign", sub, "c1" });
                Assert.IsNull(r.Error, sub);
                Assert.AreEqual("campaign", r.Verb);
                CollectionAssert.AreEqual(new[] { sub.ToLowerInvariant(), "c1" }, r.Args);
            }

            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "campaign" }).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "campaign", "status" }).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "campaign", "start", "c1" }).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "campaign", "status", "C1" }).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(new[] { "campaign", "status", "c1", "x" }).Error);
        }

        [TestMethod]
        public void Unknown_verb_and_bad_rep_index_are_errors()
        {
            Assert.IsNotNull(TrialCommandParser.Parse(new string[0]).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(null).Error);

            var unknown = TrialCommandParser.Parse(new[] { "frobnicate" });
            Assert.IsNotNull(unknown.Error);
            StringAssert.Contains(unknown.Error, "frobnicate");

            foreach (var bad in new[] { "0", "13", "-1", "x", "", "1.5" })
                Assert.IsNotNull(TrialCommandParser.Parse(new[] { "rebuild", "p50-mage", bad }).Error, "rep index '" + bad + "'");

            foreach (var ok in new[] { "1", "8", "9", "12" })
                Assert.IsNull(TrialCommandParser.Parse(new[] { "rebuild", "p50-mage", ok }).Error, "rep index '" + ok + "'");
        }

        [TestMethod]
        public void Status_line_format_is_exact()
        {
            Assert.AreEqual(
                "trial=abc123 phase=Running rep=2/5 alive=trash:3 elite:1 boss:0 last=cleared ready=false rebuild=false",
                TrialCommandParser.FormatStatus("abc123", TrialPhase.Running, 2, 5, 3, 1, 0, TrialEventKinds.EndCleared, false, false));

            Assert.AreEqual(
                "trial=u phase=Ready rep=0/1 alive=trash:0 elite:0 boss:0 last=- ready=true rebuild=true",
                TrialCommandParser.FormatStatus("u", TrialPhase.Ready, 0, 1, 0, 0, 0, null, true, true));

            Assert.AreEqual(
                "trial=u phase=Ready rep=0/1 alive=trash:0 elite:0 boss:0 last=- ready=true rebuild=false next=2:olthoi",
                TrialCommandParser.FormatStatus("u", TrialPhase.Ready, 0, 1, 0, 0, 0, null, true, false, "next=2:olthoi"));
        }
    }
}
