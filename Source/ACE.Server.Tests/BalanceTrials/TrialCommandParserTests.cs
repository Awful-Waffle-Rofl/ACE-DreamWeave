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
        public void Unknown_verb_and_bad_rep_index_are_errors()
        {
            Assert.IsNotNull(TrialCommandParser.Parse(new string[0]).Error);
            Assert.IsNotNull(TrialCommandParser.Parse(null).Error);

            var unknown = TrialCommandParser.Parse(new[] { "frobnicate" });
            Assert.IsNotNull(unknown.Error);
            StringAssert.Contains(unknown.Error, "frobnicate");

            foreach (var bad in new[] { "0", "9", "-1", "x", "", "1.5" })
                Assert.IsNotNull(TrialCommandParser.Parse(new[] { "rebuild", "p50-mage", bad }).Error, "rep index '" + bad + "'");

            foreach (var ok in new[] { "1", "8" })
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
        }
    }
}
