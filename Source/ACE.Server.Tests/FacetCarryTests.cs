using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// FacetCarry: the pure helper behind "a copied character keeps its Player Facets" (DoCopyChar, and
    /// the prodcharseed extractor). No database and no Player, per FacetRowFetch's note on why.
    /// </summary>
    [TestClass]
    public class FacetCarryTests
    {
        private const uint OldChar = 0x50001234;
        private const uint NewChar = 0x5A000003;

        private static readonly DateTime Stamp = new DateTime(2026, 10, 1, 12, 30, 45, DateTimeKind.Utc);

        private static CharacterFacet Row(byte slot, string equip, string attrs = null) => new CharacterFacet
        {
            CharacterId = OldChar,
            Slot = slot,
            Name = "Build " + slot,
            SkillsJson = "[]",
            AbilitiesJson = "{}",
            EquipJson = equip,
            AttrsJson = attrs,
            UpdatedAt = Stamp,
        };

        private static string Equip(params uint[] guids)
            => FacetSnapshot.SerializeEquip(guids.Select((g, i) => new FacetEquipEntry { Guid = g, Wcid = (uint)(100 + i), Slot = 1 << i, SpellsActive = i == 0 }));

        [TestMethod]
        public void Remap_RewritesCharacterIdAndSwappedGuids_AndKeepsUnswappedGuids()
        {
            var idSwaps = new System.Collections.Concurrent.ConcurrentDictionary<uint, uint>();
            idSwaps[OldChar] = NewChar;
            idSwaps[0x80000001] = 0x80009001;

            var rows = FacetCarry.Remap(new[] { Row(2, Equip(0x80000001, 0x80000777)) }, NewChar, idSwaps);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(NewChar, rows[0].CharacterId);

            var entries = FacetSnapshot.DeserializeEquip(rows[0].EquipJson);
            Assert.AreEqual(0x80009001u, entries[0].Guid);
            Assert.AreEqual(100u, entries[0].Wcid);
            Assert.IsTrue(entries[0].SpellsActive);
            Assert.AreEqual(0x80000777u, entries[1].Guid, "a guid that was not copied must stay, for FacetGear's wcid fallback");
        }

        [TestMethod]
        public void Remap_ReturnsTheSameEquipStringWhenNothingMaps()
        {
            var equip = Equip(0x80000777);
            var rows = FacetCarry.Remap(new[] { Row(1, equip) }, NewChar, new Dictionary<uint, uint> { [0x80000001] = 0x80009001 });

            Assert.AreSame(equip, rows[0].EquipJson, "an untouched blob must not be re-serialized");
        }

        [TestMethod]
        public void Remap_CopiesEveryNonKeyColumn_NullAttrsStaysNull()
        {
            var source = Row(3, Equip(), attrs: null);
            var copy = FacetCarry.Remap(new[] { source }, NewChar, new Dictionary<uint, uint>()).Single();

            foreach (var p in typeof(CharacterFacet).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "CharacterId"))
                Assert.AreEqual(p.GetValue(source), p.GetValue(copy), $"{p.Name} was not carried by FacetCarry.Remap");

            Assert.IsNull(copy.AttrsJson);
            Assert.AreEqual(OldChar, source.CharacterId);
        }

        [TestMethod]
        public void SaveAll_ContinuesPastAFailureAndReportsTheSlot()
        {
            var rows = new[] { Row(1, "[]"), Row(2, "[]"), Row(3, "[]") };
            var failed = new List<byte>();

            var saved = FacetCarry.SaveAll(rows, r =>
            {
                if (r.Slot == 2) return false;
                if (r.Slot == 3) throw new InvalidOperationException("boom");
                return true;
            }, failed);

            Assert.AreEqual(1, saved);
            CollectionAssert.AreEqual(new byte[] { 2, 3 }, failed);
        }

        // ------------------------------------------------------------------ source pins (no DB here)

        private static string ReadRepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, Path.Combine(relative)));
        }

        /// <summary>
        /// DoCopyChar needs a database and a Session, so its wiring is pinned by source shape. The order
        /// is the property: facets are written only inside the character-save success branch.
        /// </summary>
        [TestMethod]
        public void DoCopyChar_WritesFacetsOnlyAfterTheCharacterSaves()
        {
            var text = ReadRepoFile("Source", "ACE.Server", "Command", "Handlers", "AdminCommands.cs");

            var saveCall = text.IndexOf("DatabaseManager.Shard.AddCharacterInParallel(newPlayer.Biota", StringComparison.Ordinal);
            var failureReturn = text.IndexOf("if (!saveSuccess)", saveCall, StringComparison.Ordinal);
            var remap = text.IndexOf("FacetCarry.Remap(existingFacets, newPlayerGuid.Full, idSwaps)", StringComparison.Ordinal);
            var read = text.IndexOf("BaseDatabase.GetCharacterFacets(existingCharId)", StringComparison.Ordinal);

            var publish = text.IndexOf("PlayerManager.AddOfflinePlayer(newPlayer);", failureReturn, StringComparison.Ordinal);
            var session = text.IndexOf("AddCharacterToSession(session, newPlayer.Character);", failureReturn, StringComparison.Ordinal);
            var save = text.IndexOf("FacetCarry.SaveAll(newFacets", StringComparison.Ordinal);

            Assert.IsTrue(saveCall > 0 && failureReturn > saveCall && remap > failureReturn, "the facet write must sit after the saveSuccess check inside the AddCharacterInParallel callback");
            Assert.IsTrue(publish > 0 && remap < publish && save < publish, "the facet write must precede AddOfflinePlayer, so the copy is never published without its facets");
            Assert.IsTrue(session > 0 && save < session, "the facet write must precede AddCharacterToSession");
            Assert.IsTrue(read > 0 && read < saveCall, "the facet read happens before the save");
            StringAssert.Contains(text, "facetReadFailed = true;");
            StringAssert.Contains(text, "NO facets were copied");
        }

        [TestMethod]
        public void PurgeSlot_ClearsTheOutgoingOccupantsFacets()
        {
            var text = ReadRepoFile("Source", "ACE.Server", "Command", "Handlers", "TesterRosterCommands.cs");

            StringAssert.Contains(text, "BaseDatabase.RemoveCharacterFacets(guid)");
            StringAssert.Contains(text, "&& characterRemoved && facetsRemoved");
        }
    }
}
