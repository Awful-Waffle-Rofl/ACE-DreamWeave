using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Species table "stretch" / "stretchOverscale" schema fields (Thread family stretch, Task 1).
    /// Pure parser coverage: no database, no PropertyManager.
    /// </summary>
    [TestClass]
    public class SpeciesStretchFieldsTests
    {
        private static (WorldEventAxisStore store, SpeciesTableDef table) ParseOne(string extraFields)
        {
            var json = "{\"id\":\"t\",\"displayName\":\"T\"" + extraFields +
                ",\"members\":[{\"wcid\":7,\"name\":\"M\",\"level\":8,\"role\":0}]}";

            var store = WorldEventAxisStore.Parse(null, null, null, null, null, null, null,
                new[] { ("t.json", json) });

            Assert.IsTrue(store.SpeciesTables.ContainsKey("t"), "table must be kept");

            return (store, store.SpeciesTables["t"]);
        }

        [TestMethod]
        public void Absent_fields_stretch_at_overscale_one()
        {
            var (_, t) = ParseOne("");

            Assert.IsTrue(t.Stretches);
            Assert.AreEqual(1.0, t.Overscale);
        }

        [TestMethod]
        public void Stretch_false_is_read()
        {
            var (_, t) = ParseOne(",\"stretch\":false");

            Assert.IsFalse(t.Stretches);
        }

        [TestMethod]
        public void Overscale_is_read()
        {
            var (_, t) = ParseOne(",\"stretchOverscale\":1.25");

            Assert.AreEqual(1.25, t.Overscale);
        }

        [TestMethod]
        [DataRow("0.5")]
        [DataRow("5")]
        public void Out_of_range_overscale_is_ignored_with_a_diagnostic(string value)
        {
            var (store, t) = ParseOne(",\"stretchOverscale\":" + value);

            Assert.AreEqual(1.0, t.Overscale);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("stretchOverscale")));
        }

        [TestMethod]
        public void Out_of_range_diagnostic_text_is_exact()
        {
            var (store, _) = ParseOne(",\"stretchOverscale\":0.5");

            CollectionAssert.Contains(store.Diagnostics.ToList(), "species/t.json: stretchOverscale 0.5 outside [1, 4], ignored");
        }

        [TestMethod]
        [DataRow("1.0", 1.0)]
        [DataRow("4.0", 4.0)]
        public void Boundary_overscale_is_kept_without_a_diagnostic(string value, double expected)
        {
            var (store, t) = ParseOne(",\"stretchOverscale\":" + value);

            Assert.AreEqual(expected, t.Overscale);
            Assert.IsFalse(store.Diagnostics.Any(d => d.Contains("stretchOverscale")));
        }
    }
}
