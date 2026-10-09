using System;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-shape guards for the two halves of per-facet attributes that no behavioural test in this
    /// assembly can reach: the live apply path (needs a real Player, a session and a client dat) and
    /// the shard write path (needs a database - nothing here ever reaches SaveChanges).
    ///
    /// Written in the style of EnhancedVitalRefreshTests, and for the same reason it exists: a source
    /// assertion is a weak test, but it is strictly better than the alternative here, which is nothing.
    /// Each test below says what it catches; where an assertion is a PROXY for the property it is
    /// standing in for, it says that too rather than overclaiming.
    /// </summary>
    [TestClass]
    public class FacetAttributeApplySourceTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string ReadRepoFile(params string[] relative)
            => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(relative)));

        /// <summary>
        /// The body of one method, by brace balance from its signature. Fails loudly if the signature
        /// has moved, rather than silently asserting against an empty string - the failure mode a fixed
        /// character window has.
        /// </summary>
        private static string MethodBody(string text, string signature)
        {
            var at = text.IndexOf(signature, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, $"could not find `{signature}` - this test's anchor has moved, it is not evidence about behaviour");

            var open = text.IndexOf('{', at);

            Assert.IsTrue(open >= 0, $"no method body found after `{signature}`");

            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                        return text.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"unbalanced braces after `{signature}`");
            return null;
        }

        private static int Count(string haystack, string needle)
        {
            var total = 0;

            for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
                total++;

            return total;
        }

        /// <summary>
        /// SIX ATTRIBUTE SENDS. The apply enqueues one GameMessagePrivateUpdateAttribute per primary
        /// attribute, and there are exactly six primaries - so this is two assertions rather than a
        /// textual count of six constructor calls: the send sits inside a loop over
        /// FacetAttributes.PrimaryAttributes (source), and that array has exactly the six entries
        /// (runtime, pinned harder in FacetAttributesTests).
        ///
        /// CATCHES: the send being dropped, or moved off the primary-attribute loop onto some other
        /// collection - Biota.PropertiesAttribute in particular, which can carry keys outside
        /// Strength..Self and is exactly what CaptureFacetAttributes is documented not to iterate.
        ///
        /// PROXY, stated plainly: a textual check cannot prove the enqueue runs six times, only that it
        /// is written inside a loop over a six-element array.
        /// </summary>
        [TestMethod]
        public void ApplyFacetAttributes_SendsOneAttributeUpdatePerPrimaryAttribute()
        {
            var body = MethodBody(
                ReadRepoFile("Source", "ACE.Server", "WorldObjects", "Player_Facets.cs"),
                "public void ApplyFacetAttributes(");

            StringAssert.Contains(body, "foreach (var attribute in FacetAttributes.PrimaryAttributes)",
                "the attribute sends must be driven by the primary-attribute list, never by Biota.PropertiesAttribute");

            StringAssert.Contains(body, "new GameMessagePrivateUpdateAttribute(this, creatureAttribute)",
                "the client redraws the attribute panel from this message - AttributeTransferDevice is the precedent");

            Assert.AreEqual(6, FacetAttributes.PrimaryAttributes.Length,
                "six sends is six primaries - if this array changes size, the claim above changes with it");
        }

        /// <summary>
        /// THREE VITAL SENDS, ALL UNCONDITIONAL. The client rebuilds max health/stamina/mana itself from
        /// the terms these messages carry, and a facet switch can move all six attributes at once.
        ///
        /// CATCHES: the specific regression of "optimizing" this back into the Endurance-then-Self
        /// conditional in Player_Attributes.HandleActionRaiseAttribute. That conditional is correct for
        /// raising ONE attribute by one rank and wrong here: it never sends stamina at all, and it sends
        /// nothing when only Strength or Coordination moved.
        /// </summary>
        [TestMethod]
        public void ApplyFacetAttributes_SendsAllThreeVitalsUnconditionally()
        {
            var body = MethodBody(
                ReadRepoFile("Source", "ACE.Server", "WorldObjects", "Player_Facets.cs"),
                "public void ApplyFacetAttributes(");

            StringAssert.Contains(body, "new GameMessagePrivateUpdateVital(this, Health)");
            StringAssert.Contains(body, "new GameMessagePrivateUpdateVital(this, Stamina)");
            StringAssert.Contains(body, "new GameMessagePrivateUpdateVital(this, Mana)");

            Assert.AreEqual(3, Count(body, "new GameMessagePrivateUpdateVital("),
                "exactly three vital sends - no more (a duplicate would double the client's sequence) and no fewer");

            Assert.IsFalse(body.Contains("PropertyAttribute.Endurance") || body.Contains("PropertyAttribute.Self"),
                "the vital sends must not be gated on which attribute moved - that conditional belongs to the single-attribute raise path");
        }

        /// <summary>
        /// CATCHES: the clamp being dropped and left to VitalHeartBeat. That is up to ~5 seconds late,
        /// and in the meantime CreatureVital.Missing is a uint subtraction that wraps to roughly four
        /// billion whenever Current exceeds MaxValue - a value read by Creature_Vitals.SetMaxVitals,
        /// Healer and two sites in WorldObject_Magic.
        /// </summary>
        [TestMethod]
        public void ApplyFacetAttributes_ClampsCurrentVitalsToTheNewMaximum()
        {
            var body = MethodBody(
                ReadRepoFile("Source", "ACE.Server", "WorldObjects", "Player_Facets.cs"),
                "public void ApplyFacetAttributes(");

            StringAssert.Contains(body, "vital.Current > vital.MaxValue",
                "an over-max Current must be clamped here, not left for the vital heartbeat");

            StringAssert.Contains(body, "UpdateVital(vital, vital.MaxValue)",
                "Player.UpdateVital both clamps and enqueues the level message, matching HandleMaxHealthUpdate");
        }

        /// <summary>
        /// THE NAMED TRAP, generalized. ShardDatabase.SaveCharacterFacet copies field by field in its
        /// update branch, so a column added to the entity and omitted there persists on the INSERT and
        /// then silently never updates again - the row keeps its first value forever, with no error
        /// anywhere and nothing in this assembly able to notice (there is no database here and
        /// SaveChanges is never reached).
        ///
        /// This asserts the property that actually matters rather than just the one column that
        /// prompted it: EVERY CLR property of CharacterFacet except the two composite-key columns (which
        /// the lookup matched on and must not be reassigned) has an `existing.X = row.X;` line. A future
        /// column gets caught by this test on the day it is added.
        /// </summary>
        [TestMethod]
        public void SaveCharacterFacet_UpdateBranchAssignsEveryNonKeyColumn()
        {
            var body = MethodBody(
                ReadRepoFile("Source", "ACE.Database", "ShardDatabase.cs"),
                "public bool SaveCharacterFacet(");

            var keyColumns = new[] { "CharacterId", "Slot" };

            var properties = typeof(CharacterFacet)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(name => !keyColumns.Contains(name))
                .ToList();

            Assert.IsTrue(properties.Contains("AttrsJson"), "AttrsJson is missing from the entity, so this test is not checking what it claims to");

            foreach (var name in properties)
            {
                StringAssert.Contains(body, $"existing.{name} = row.{name};",
                    $"SaveCharacterFacet's update branch never writes {name}, so it persists on INSERT and then silently never updates again");
            }
        }
    }
}
