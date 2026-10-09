using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for species reference tables (TECH-DESIGN C15, 2026-08-16) - the second, additive
    /// catalog source that lets the event process draw from per-species rosters under
    /// Content/events/axes/species/*.json, retail creatures included, on top of the unchanged
    /// WorldEventCreature flag scan. Covers both halves: WorldEventAxisStore's structural parsing of the
    /// species files, and WorldEventCatalogBuilder's world-aware join/merge against the weenie cache.
    /// No database, no landblock (TECH-DESIGN D6).
    /// </summary>
    [TestClass]
    public class WorldEventSpeciesTableTests
    {
        // ---- fakes and builders ----------------------------------------------------------------------

        private static Weenie BuildCreatureWeenie(uint wcid, string family, int? level,
            bool worldEventCreature = false, bool attackable = true,
            WeenieType type = WeenieType.Creature, bool objectiveFlag = false)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"testcreature{wcid}",
                WeenieType = type,
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string>()
            };

            if (worldEventCreature)
                weenie.PropertiesBool[PropertyBool.WorldEventCreature] = true;

            if (attackable)
                weenie.PropertiesBool[PropertyBool.Attackable] = true;

            if (objectiveFlag)
                weenie.PropertiesBool[PropertyBool.WorldEventObjective] = true;

            if (family != null)
                weenie.PropertiesString[PropertyString.WorldEventFamily] = family;

            weenie.PropertiesString[PropertyString.Name] = $"Test Creature {wcid}";

            if (level != null)
                weenie.PropertiesInt[PropertyInt.Level] = level.Value;

            if (worldEventCreature)
                weenie.PropertiesInt[PropertyInt.WorldEventRole] = 0;

            return weenie;
        }

        private static SpeciesTableDef BuildTable(string id, string displayName, params SpeciesMemberDef[] members)
        {
            return new SpeciesTableDef
            {
                Id = id,
                DisplayName = displayName,
                Members = members.ToList()
            };
        }

        private static SpeciesMemberDef Member(uint wcid, int role = 0)
        {
            return new SpeciesMemberDef { Wcid = wcid, Role = role };
        }

        // ---- (a) catalog builder: species table join -----------------------------------------------

        [TestMethod]
        public void Build_TableMemberJoinsWithLiveLevelAndRoleAndFromTableTrue()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(12, null, 15) };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12, role: 1)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(0, diagnostics.Count, string.Join(" | ", diagnostics));
            Assert.AreEqual(1, catalog.MemberCount);

            var member = catalog.Families["olthoi"][0];
            Assert.AreEqual(12u, member.Wcid);
            Assert.AreEqual(15, member.Level);
            Assert.AreEqual(1, member.Role);
            Assert.IsTrue(member.FromTable);
        }

        [TestMethod]
        public void Build_FlaggedAndTabledWcid_FlagWinsWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(12, "emberwrought", 15, worldEventCreature: true) };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(1, catalog.MemberCount);
            Assert.IsTrue(catalog.Families.ContainsKey("emberwrought"));
            Assert.IsFalse(catalog.Families.ContainsKey("olthoi"));
            Assert.IsFalse(catalog.Families["emberwrought"][0].FromTable);

            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "already flagged WorldEventCreature");
            StringAssert.Contains(diagnostics[0], "emberwrought");
        }

        [TestMethod]
        public void Build_UnknownWcid_DroppedWithADiagnostic()
        {
            var weenies = new List<Weenie>();
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(999)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "not found in the world database");
        }

        [TestMethod]
        public void Build_NonCreatureTableMember_DroppedWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(12, null, 15, type: WeenieType.Generic) };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "not Creature");
        }

        [TestMethod]
        public void Build_NonAttackableTableMember_DroppedWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(12, null, 15, attackable: false) };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "not Attackable");
        }

        [TestMethod]
        public void Build_MissingLevelTableMember_DroppedWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(12, null, null) };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "missing Level");
        }

        [TestMethod]
        public void Build_ObjectiveFlaggedTableMember_DroppedWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(12, null, 15, objectiveFlag: true) };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "WorldEventObjective");
        }

        [TestMethod]
        public void Build_PortalLinkTableMember_DroppedWithADiagnostic()
        {
            var weenie = BuildCreatureWeenie(12, null, 15);
            weenie.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.LinkedPortalOne, 999 } };
            var weenies = new List<Weenie> { weenie };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "summons a portal on death");
            StringAssert.Contains(diagnostics[0], "R9");
        }

        [TestMethod]
        public void Build_PortalLinkFlaggedMember_DroppedWithADiagnostic()
        {
            var weenie = BuildCreatureWeenie(12, "emberwrought", 15, worldEventCreature: true);
            weenie.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.LinkedPortalTwo, 999 } };
            var weenies = new List<Weenie> { weenie };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "summons a portal on death");
            StringAssert.Contains(diagnostics[0], "R9");
        }

        [TestMethod]
        public void Build_DuplicateWcidAcrossTwoTables_LaterDropped()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(12, null, 15) };
            var tables = new List<SpeciesTableDef>
            {
                BuildTable("olthoi", "the Olthoi", Member(12)),
                BuildTable("olthoi_royal", "Olthoi Royalty", Member(12))
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(1, catalog.MemberCount);
            Assert.IsTrue(catalog.Families.ContainsKey("olthoi"));
            Assert.IsFalse(catalog.Families.ContainsKey("olthoi_royal"));

            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "already listed in species/olthoi");
        }

        [TestMethod]
        public void Build_DuplicateWcidWithinOneTable_SecondDroppedWithListedTwiceMessage()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(12, null, 15) };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12), Member(12)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(1, catalog.MemberCount);

            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "species/olthoi: wcid 12 is listed twice in this file - dropped");
        }

        [TestMethod]
        public void Build_TableMemberDecoyNameAndLevel_JoinedMemberUsesTheLiveWeenieInstead()
        {
            var weenie = BuildCreatureWeenie(12, null, 15);
            var weenies = new List<Weenie> { weenie };

            var decoyMember = new SpeciesMemberDef
            {
                Wcid = 12,
                Role = 1,
                Name = "Decoy Name From Table",
                Level = 9999,
                Custom = true,
                Note = "decoy note, never read by the engine"
            };

            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", decoyMember) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

            Assert.AreEqual(0, diagnostics.Count, string.Join(" | ", diagnostics));
            Assert.AreEqual(1, catalog.MemberCount);

            var member = catalog.Families["olthoi"][0];

            // Role is the only table field the engine reads besides wcid; Name/Level always come from the
            // live weenie, never the table's human-facing snapshot (TECH-DESIGN C15).
            Assert.AreEqual(1, member.Role);
            Assert.AreEqual(weenie.GetName(), member.Name);
            Assert.AreNotEqual("Decoy Name From Table", member.Name);
            Assert.AreEqual(15, member.Level);
            Assert.AreNotEqual(9999, member.Level);
        }

        [TestMethod]
        public void Build_TwoArgOverload_StillWorksWithNoTables()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(1002604, "emberwrought", 20, worldEventCreature: true) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, diagnostics.Count);
            Assert.AreEqual(1, catalog.MemberCount);
        }

        [TestMethod]
        public void DescribeCoverage_ShowsFlaggedAndTableCounts()
        {
            var weenies = new List<Weenie>
            {
                BuildCreatureWeenie(1002604, "olthoi", 20, worldEventCreature: true),
                BuildCreatureWeenie(12, null, 15)
            };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out _);

            Assert.AreEqual("olthoi: 2 members, levels 15-20, trash 2 elite 0 champion 0 named 0, flagged 1 table 1, casters 0, minLevel 15",
                catalog.DescribeCoverage("olthoi"));
        }

        // ---- (a2) catalog builder: CollectWcids (2026-08-16, boot warm-up removal) ------------------

        [TestMethod]
        public void CollectWcids_DedupesAcrossFlaggedAndTables()
        {
            var flagged = new List<uint> { 12, 15 };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(15), Member(20)) };

            var wcids = WorldEventCatalogBuilder.CollectWcids(flagged, tables);

            CollectionAssert.AreEqual(new List<uint> { 12, 15, 20 }, wcids);
        }

        [TestMethod]
        public void CollectWcids_ResultIsAscendingOrder()
        {
            var flagged = new List<uint> { 30, 5 };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(18), Member(2)) };

            var wcids = WorldEventCatalogBuilder.CollectWcids(flagged, tables);

            CollectionAssert.AreEqual(new List<uint> { 2, 5, 18, 30 }, wcids);
        }

        [TestMethod]
        public void CollectWcids_NullTableMembersTolerated()
        {
            var flagged = new List<uint> { 7 };
            var tables = new List<SpeciesTableDef>
            {
                null,
                new SpeciesTableDef { Id = "empty", DisplayName = "Empty", Members = null }
            };

            var wcids = WorldEventCatalogBuilder.CollectWcids(flagged, tables);

            CollectionAssert.AreEqual(new List<uint> { 7 }, wcids);
        }

        [TestMethod]
        public void CollectWcids_WcidZeroSkipped()
        {
            var flagged = new List<uint> { 0, 9 };
            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(0), Member(11)) };

            var wcids = WorldEventCatalogBuilder.CollectWcids(flagged, tables);

            CollectionAssert.AreEqual(new List<uint> { 9, 11 }, wcids);
        }

        [TestMethod]
        public void CollectWcids_NullInputsTolerated()
        {
            var wcids = WorldEventCatalogBuilder.CollectWcids(null, null);

            Assert.AreEqual(0, wcids.Count);
        }

        // ---- (b) axis store: species table parsing ---------------------------------------------------

        private static WorldEventAxisStore Parse(IEnumerable<(string fileName, string json)> speciesFiles,
            string familiesJson = null)
        {
            return WorldEventAxisStore.Parse(null, familiesJson, null, null, null, speciesFiles: speciesFiles);
        }

        [TestMethod]
        public void AxisStore_ValidSpeciesTable_RegistersASpeciesTableAndAFamilyDef()
        {
            const string json = @"{
                ""id"": ""olthoi"", ""displayName"": ""the Olthoi"", ""hueKey"": ""none"",
                ""members"": [ { ""wcid"": 12, ""name"": ""Olthoi Grub"", ""level"": 15, ""role"": 0 } ]
            }";

            var store = Parse(new[] { ("olthoi.json", json) });

            Assert.AreEqual(0, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));
            Assert.IsTrue(store.SpeciesTables.ContainsKey("olthoi"));
            Assert.AreEqual(1, store.SpeciesTables["olthoi"].Members.Count);

            Assert.IsTrue(store.Families.ContainsKey("olthoi"));
            Assert.AreEqual("the Olthoi", store.Families["olthoi"].DisplayName);
        }

        [TestMethod]
        public void AxisStore_FamiliesJsonWinsMetadataOnIdCollision()
        {
            const string families = @"{ ""families"": [ { ""id"": ""olthoi"", ""displayName"": ""Families.json Olthoi"" } ] }";
            const string species = @"{ ""id"": ""olthoi"", ""displayName"": ""Species.json Olthoi"", ""members"": [] }";

            var store = Parse(new[] { ("olthoi.json", species) }, families);

            Assert.AreEqual("Families.json Olthoi", store.Families["olthoi"].DisplayName);

            // The collision is the MECHANISM for giving a family a custom event display name, not a
            // defect: you cannot override the name without declaring the id in both files. It therefore
            // belongs in Notes (logged at DEBUG), and Diagnostics - the WARN channel - must stay clean,
            // or every intentional override warns on every boot. Eight shipped families did exactly that.
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(1, store.Notes.Count);
            StringAssert.Contains(store.Notes[0], "also declared in families.json");
        }

        [TestMethod]
        public void AxisStore_SpeciesTableWithNoFamiliesJsonEntry_ProducesNoNote()
        {
            // Control for AxisStore_FamiliesJsonWinsMetadataOnIdCollision: with no collision there is
            // nothing to record, so a note here would mean the channel fires unconditionally.
            const string species = @"{ ""id"": ""olthoi"", ""displayName"": ""Species.json Olthoi"", ""members"": [] }";

            var store = Parse(new[] { ("olthoi.json", species) });

            Assert.AreEqual("Species.json Olthoi", store.Families["olthoi"].DisplayName);
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(0, store.Notes.Count, string.Join(" | ", store.Notes));
        }

        [TestMethod]
        public void AxisStore_MalformedSpeciesTable_StaysADiagnosticNotANote()
        {
            // Guards against the fix over-reaching: only the families.json collision moved channel.
            // A real defect must still reach the WARN channel.
            const string species = @"{ ""id"": ""olthoi"", ""members"": [] }";

            var store = Parse(new[] { ("olthoi.json", species) });

            Assert.AreEqual(1, store.Diagnostics.Count);
            StringAssert.Contains(store.Diagnostics[0], "missing displayName");
            Assert.AreEqual(0, store.Notes.Count, string.Join(" | ", store.Notes));
        }

        [TestMethod]
        public void AxisStore_TwoFilesWithDifferentStemsDeclaringTheSameId_SecondDropped()
        {
            // A duplicate id can only be assigned to the "duplicate species id" check if id == stem for
            // BOTH files, which is impossible with two different filenames sharing one id: whichever file's
            // id does not match ITS OWN stem is dropped by the stem check first. This test asserts whatever
            // diagnostic the code actually emits, per the code's fixed check order, while confirming the
            // real invariant that matters: only the first file's table survives.
            const string first = @"{ ""id"": ""geode"", ""displayName"": ""First Geode"", ""members"": [] }";
            const string second = @"{ ""id"": ""geode"", ""displayName"": ""Second Geode"", ""members"": [] }";

            var store = Parse(new[] { ("geode.json", first), ("geode_variant.json", second) });

            Assert.AreEqual(1, store.SpeciesTables.Count);
            Assert.AreEqual("First Geode", store.SpeciesTables["geode"].DisplayName);

            Assert.AreEqual(1, store.Diagnostics.Count);
            StringAssert.Contains(store.Diagnostics[0], "does not match the file stem");

            // The second table never reaches the id-keyed dictionary under any key.
            Assert.IsFalse(store.SpeciesTables.Values.Any(t => t.DisplayName == "Second Geode"));
        }

        [TestMethod]
        public void AxisStore_IdNotMatchingFileStem_TableDropped()
        {
            const string json = @"{ ""id"": ""wrongid"", ""displayName"": ""X"", ""members"": [] }";

            var store = Parse(new[] { ("olthoi.json", json) });

            Assert.AreEqual(0, store.SpeciesTables.Count);
            Assert.AreEqual(1, store.Diagnostics.Count);
            StringAssert.Contains(store.Diagnostics[0], "does not match the file stem");
        }

        [TestMethod]
        public void AxisStore_MalformedJson_DiagnosticAndDropped()
        {
            var store = Parse(new[] { ("olthoi.json", "{ not json") });

            Assert.AreEqual(0, store.SpeciesTables.Count);
            Assert.AreEqual(1, store.Diagnostics.Count);
            StringAssert.Contains(store.Diagnostics[0], "malformed JSON");
        }

        [TestMethod]
        public void AxisStore_MissingDisplayName_TableDropped()
        {
            const string json = @"{ ""id"": ""olthoi"", ""members"": [] }";

            var store = Parse(new[] { ("olthoi.json", json) });

            Assert.AreEqual(0, store.SpeciesTables.Count);
            Assert.AreEqual(1, store.Diagnostics.Count);
            StringAssert.Contains(store.Diagnostics[0], "missing displayName");
        }

        [TestMethod]
        public void AxisStore_MemberRoleOutOfRange_MemberDropped()
        {
            const string json = @"{
                ""id"": ""olthoi"", ""displayName"": ""the Olthoi"",
                ""members"": [
                    { ""wcid"": 12, ""role"": 0 },
                    { ""wcid"": 13, ""role"": 7 }
                ]
            }";

            var store = Parse(new[] { ("olthoi.json", json) });

            Assert.AreEqual(1, store.SpeciesTables["olthoi"].Members.Count);
            Assert.AreEqual(12u, store.SpeciesTables["olthoi"].Members[0].Wcid);

            Assert.AreEqual(1, store.Diagnostics.Count);
            StringAssert.Contains(store.Diagnostics[0], "outside 0..2");
        }

        [TestMethod]
        public void AxisStore_MemberWcidZero_MemberDropped()
        {
            const string json = @"{
                ""id"": ""olthoi"", ""displayName"": ""the Olthoi"",
                ""members"": [ { ""wcid"": 0, ""role"": 0 } ]
            }";

            var store = Parse(new[] { ("olthoi.json", json) });

            Assert.AreEqual(0, store.SpeciesTables["olthoi"].Members.Count);
            Assert.AreEqual(1, store.Diagnostics.Count);
        }

        [TestMethod]
        public void AxisStore_NoSpeciesFolder_EmptyTablesNoDiagnostic()
        {
            var store = Parse(null);

            Assert.AreEqual(0, store.SpeciesTables.Count);
            Assert.AreEqual(0, store.Diagnostics.Count);
        }

        // ---- (f) caster flag and pairing profiles (two-family composition, 2026-08-29) --------------

        private static Weenie WithSpellbook(Weenie weenie, params int[] spells)
        {
            weenie.PropertiesSpellBook = new Dictionary<int, float>();

            foreach (var spell in spells)
                weenie.PropertiesSpellBook[spell] = 1.0f;

            return weenie;
        }

        [TestMethod]
        public void Caster_IsSetOnTheFlagScanPath()
        {
            var weenies = new List<Weenie>
            {
                WithSpellbook(BuildCreatureWeenie(1002604, "olthoi", 20, worldEventCreature: true), 157),
                BuildCreatureWeenie(1002605, "olthoi", 30, worldEventCreature: true)
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out _);

            var caster = catalog.Families["olthoi"].Single(m => m.Wcid == 1002604);
            var plain = catalog.Families["olthoi"].Single(m => m.Wcid == 1002605);

            Assert.IsTrue(caster.Caster, "a non-empty spellbook makes a member a caster");
            Assert.IsFalse(plain.Caster, "no spellbook at all is not a caster");

            Assert.AreEqual("olthoi", caster.FamilyId);
            Assert.AreEqual("olthoi", plain.FamilyId);
        }

        [TestMethod]
        public void Caster_IsSetOnTheSpeciesTablePath()
        {
            var weenies = new List<Weenie>
            {
                WithSpellbook(BuildCreatureWeenie(12, null, 15), 157, 158),
                BuildCreatureWeenie(13, null, 25)
            };

            var tables = new List<SpeciesTableDef> { BuildTable("olthoi", "the Olthoi", Member(12), Member(13)) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out _);

            var caster = catalog.Families["olthoi"].Single(m => m.Wcid == 12);
            var plain = catalog.Families["olthoi"].Single(m => m.Wcid == 13);

            Assert.IsTrue(caster.Caster);
            Assert.IsFalse(plain.Caster);

            Assert.AreEqual("olthoi", caster.FamilyId, "a table member is filed under the table id");
            Assert.AreEqual("olthoi", plain.FamilyId);
        }

        [TestMethod]
        public void Caster_AnEmptySpellbookIsNotACaster()
        {
            var weenies = new List<Weenie>
            {
                WithSpellbook(BuildCreatureWeenie(1002604, "olthoi", 20, worldEventCreature: true))
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out _);

            Assert.IsFalse(catalog.Families["olthoi"][0].Caster, "an empty spellbook dictionary is not a caster");
        }

        [TestMethod]
        public void Profiles_ExcludeNamedBossesFromHasCasterAndFromTheLevelSpan()
        {
            // The only caster, and the only member outside 20-30, is a role-3 named boss. A named boss is
            // never drawn into a wave or the champion slot, so the profile must not report either.
            var boss = WithSpellbook(BuildCreatureWeenie(1002619, "olthoi", 400, worldEventCreature: true), 157);
            boss.PropertiesInt[PropertyInt.WorldEventRole] = WorldEventRosterSelector.NamedBossRole;

            var weenies = new List<Weenie>
            {
                BuildCreatureWeenie(1002604, "olthoi", 20, worldEventCreature: true),
                BuildCreatureWeenie(1002605, "olthoi", 30, worldEventCreature: true),
                boss
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out _);

            var profile = catalog.Profile("olthoi");

            Assert.AreEqual(2, profile.MemberCount);
            Assert.AreEqual(20, profile.MinLevel);
            Assert.AreEqual(30, profile.MaxLevel);
            Assert.AreEqual(0, profile.CasterCount);
            Assert.IsFalse(profile.HasCaster);

            // The whole-roster span on the same line still counts the boss - the two numbers are allowed
            // to disagree, and DescribeCoverage shows both.
            StringAssert.Contains(catalog.DescribeCoverage("olthoi"), "levels 20-400");
            StringAssert.Contains(catalog.DescribeCoverage("olthoi"), "casters 0, minLevel 20");
        }

        [TestMethod]
        public void Profiles_CountEveryCasterInTheFamily()
        {
            var weenies = new List<Weenie>
            {
                WithSpellbook(BuildCreatureWeenie(1002604, "olthoi", 20, worldEventCreature: true), 157),
                WithSpellbook(BuildCreatureWeenie(1002605, "olthoi", 30, worldEventCreature: true), 158),
                BuildCreatureWeenie(1002606, "olthoi", 40, worldEventCreature: true)
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out _);

            var profile = catalog.Profile("olthoi");

            Assert.AreEqual(3, profile.MemberCount);
            Assert.AreEqual(2, profile.CasterCount);
            Assert.IsTrue(profile.HasCaster);
        }

        [TestMethod]
        public void Profile_UnknownFamily_IsEmptyRatherThanNull()
        {
            var profile = WorldEventCatalog.Empty.Profile("nosuchfamily");

            Assert.IsNotNull(profile);
            Assert.AreEqual(0, profile.MemberCount);
            Assert.IsFalse(profile.HasCaster);
        }
    }
}
