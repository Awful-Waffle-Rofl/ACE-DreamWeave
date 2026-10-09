using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Serialization for the three character_facet JSON columns. These blobs are the only record of
    /// a build the player is not currently standing on, so a deserialize that throws would strand a
    /// facet permanently - every reader degrades to empty instead, matching MarketSnapshot's
    /// contract for the same reason.
    /// </summary>
    [TestClass]
    public class FacetSnapshotTests
    {
        [TestMethod]
        public void Skills_RoundTrip()
        {
            var input = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.WarMagic, Sac = SkillAdvancementClass.Specialized, Ranks = 208, Pp = 4145621, InitLevel = 10 },
                new FacetSkillEntry { Skill = Skill.Bow,      Sac = SkillAdvancementClass.Untrained,   Ranks = 0,   Pp = 0,       InitLevel = 0 },
            };

            var round = FacetSnapshot.DeserializeSkills(FacetSnapshot.SerializeSkills(input));

            Assert.AreEqual(2, round.Count);

            var war = round.Single(e => e.Skill == Skill.WarMagic);
            Assert.AreEqual(SkillAdvancementClass.Specialized, war.Sac);
            Assert.AreEqual((ushort)208, war.Ranks);
            Assert.AreEqual(4145621u, war.Pp);
            Assert.AreEqual(10u, war.InitLevel);
        }

        [TestMethod]
        public void Abilities_RoundTrip()
        {
            var input = new Dictionary<string, int> { { "PrecisionShot", 3 }, { "SteadyAim", 2 } };

            var round = FacetSnapshot.DeserializeAbilities(FacetSnapshot.SerializeAbilities(input));

            Assert.AreEqual(2, round.Count);
            Assert.AreEqual(3, round["PrecisionShot"]);
            Assert.AreEqual(2, round["SteadyAim"]);
        }

        [TestMethod]
        public void Equip_RoundTripKeepsBothGuidAndWcid()
        {
            // Both are stored because the vault destroys the biota of any pristine item it collapses to
            // a ledger row - the guid is gone after that, and only the wcid can still resolve it.
            var input = new List<FacetEquipEntry>
            {
                new FacetEquipEntry { Guid = 2154382110u, Wcid = 31812u, Slot = 16777216 },
            };

            var round = FacetSnapshot.DeserializeEquip(FacetSnapshot.SerializeEquip(input));

            Assert.AreEqual(1, round.Count);
            Assert.AreEqual(2154382110u, round[0].Guid);
            Assert.AreEqual(31812u, round[0].Wcid);
            Assert.AreEqual(16777216, round[0].Slot);
        }

        /// <summary>
        /// SpellsActive round trips both ways: an entry captured while the item's spells were active
        /// deserializes back to true, and one captured while they were not stays false.
        /// </summary>
        [TestMethod]
        public void Equip_RoundTripKeepsSpellsActive()
        {
            var input = new List<FacetEquipEntry>
            {
                new FacetEquipEntry { Guid = 1u, Wcid = 100u, Slot = 1, SpellsActive = true },
                new FacetEquipEntry { Guid = 2u, Wcid = 200u, Slot = 2, SpellsActive = false },
            };

            var round = FacetSnapshot.DeserializeEquip(FacetSnapshot.SerializeEquip(input));

            Assert.AreEqual(2, round.Count);
            Assert.IsTrue(round.Single(e => e.Guid == 1u).SpellsActive);
            Assert.IsFalse(round.Single(e => e.Guid == 2u).SpellsActive);
        }

        /// <summary>
        /// CATCHES a deserializer that throws, or defaults the wrong way, on an OLD stored row written
        /// before SpellsActive existed - its JSON simply has no "active" key at all. The safe default is
        /// false (strict): a pre-existing row must not silently start waiving buffed wield requirements or
        /// item-activation checks it never earned.
        /// </summary>
        [TestMethod]
        public void Equip_OldRowWithoutSpellsActiveKey_DeserializesToFalse()
        {
            var oldRowJson = "[{\"guid\":2154382110,\"wcid\":31812,\"slot\":16777216}]";

            var round = FacetSnapshot.DeserializeEquip(oldRowJson);

            Assert.AreEqual(1, round.Count);
            Assert.AreEqual(2154382110u, round[0].Guid);
            Assert.IsFalse(round[0].SpellsActive, "an absent 'active' key must default to false, never throw or default to true");
        }

        [TestMethod]
        public void Deserialize_DegradesToEmpty_OnGarbage()
        {
            // A throw here would strand the facet permanently, so every reader degrades instead.
            Assert.AreEqual(0, FacetSnapshot.DeserializeSkills("not json at all").Count);
            Assert.AreEqual(0, FacetSnapshot.DeserializeAbilities("{{{").Count);
            Assert.AreEqual(0, FacetSnapshot.DeserializeEquip("not json at all").Count);
        }

        [TestMethod]
        public void Deserialize_DegradesToEmpty_OnNullAndEmpty()
        {
            Assert.AreEqual(0, FacetSnapshot.DeserializeSkills(null).Count);
            Assert.AreEqual(0, FacetSnapshot.DeserializeSkills("").Count);
            Assert.AreEqual(0, FacetSnapshot.DeserializeAbilities(null).Count);
            Assert.AreEqual(0, FacetSnapshot.DeserializeEquip(null).Count);
        }

        [TestMethod]
        public void TryDeserializeSkills_RefusesBlankAndMalformedInput()
        {
            // The load-bearing read. An empty skill list on the switch path releases the outgoing build's
            // whole PP into AvailableExperience while committing none of it back, so "I could not read
            // this column" must be distinguishable from "this build has no skills".
            Assert.IsFalse(FacetSnapshot.TryDeserializeSkills(null, out var fromNull));
            Assert.IsNull(fromNull);

            Assert.IsFalse(FacetSnapshot.TryDeserializeSkills("", out var fromEmpty));
            Assert.IsNull(fromEmpty);

            Assert.IsFalse(FacetSnapshot.TryDeserializeSkills("   ", out var fromWhitespace));
            Assert.IsNull(fromWhitespace);

            Assert.IsFalse(FacetSnapshot.TryDeserializeSkills("not json at all", out var fromGarbage));
            Assert.IsNull(fromGarbage);
        }

        [TestMethod]
        public void TryDeserializeSkills_AcceptsAValidList_IncludingAGenuinelyEmptyBuild()
        {
            var input = new List<FacetSkillEntry>
            {
                new FacetSkillEntry { Skill = Skill.WarMagic, Sac = SkillAdvancementClass.Specialized, Ranks = 208, Pp = 4145621, InitLevel = 10 },
            };

            Assert.IsTrue(FacetSnapshot.TryDeserializeSkills(FacetSnapshot.SerializeSkills(input), out var round));
            Assert.AreEqual(1, round.Count);
            Assert.AreEqual(Skill.WarMagic, round[0].Skill);
            Assert.AreEqual(4145621u, round[0].Pp);

            // A build with nothing in it is a legitimate value, not a read failure: SerializeSkills always
            // writes at least "[]", so only the absence of any parseable value is a refusal.
            Assert.IsTrue(FacetSnapshot.TryDeserializeSkills(FacetSnapshot.SerializeSkills(new List<FacetSkillEntry>()), out var empty));
            Assert.AreEqual(0, empty.Count);
        }

        /// <summary>
        /// CATCHES: a value that does not survive the round trip, and a serializer that writes the
        /// PropertyAttribute enum's NUMBER instead of its name. Number-keying would let a future enum
        /// reordering repoint every stored row at a different attribute, which is why abilities_Json
        /// took the same decision.
        /// </summary>
        [TestMethod]
        public void Attributes_RoundTrip_AndAreKeyedByName()
        {
            var input = new Dictionary<PropertyAttribute, uint>
            {
                { PropertyAttribute.Strength,     100 },
                { PropertyAttribute.Endurance,    100 },
                { PropertyAttribute.Coordination, 100 },
                { PropertyAttribute.Quickness,     10 },
                { PropertyAttribute.Focus,         10 },
                { PropertyAttribute.Self,          10 },
            };

            var json = FacetSnapshot.SerializeAttributes(input);

            StringAssert.Contains(json, "\"Strength\"", "the column is keyed by attribute NAME, not by the enum value");
            StringAssert.Contains(json, "\"Coordination\"");
            Assert.IsFalse(json.Contains("\"1\""), "an enum-number key would let a reordering repoint stored values");

            Assert.IsTrue(FacetSnapshot.TryDeserializeAttributes(json, out var round));
            Assert.AreEqual(6, round.Count);

            foreach (var pair in input)
                Assert.AreEqual(pair.Value, round[pair.Key], $"{pair.Key} did not survive the round trip");
        }

        /// <summary>
        /// CATCHES: any of the four degrade cases being read as a real arrangement. All four must return
        /// FALSE, which the switch path turns into "keep the character's live arrangement" - and that is
        /// conserving by construction. NULL is the ordinary one, not an edge case: attrs_Json is NULLABLE
        /// precisely so a row written before this column existed is representable, and every such row
        /// arrives here as null.
        ///
        /// The MISSING-attribute case is the one worth being explicit about. A partial arrangement parses
        /// perfectly well as JSON; accepting it would leave the unnamed attributes at their live values
        /// after the reconciliation had already balanced a surplus against a short stored sum, silently
        /// breaking the conservation guarantee in the one place nothing downstream checks.
        /// </summary>
        [TestMethod]
        public void TryDeserializeAttributes_RefusesNullBlankMalformedAndPartial()
        {
            Assert.IsFalse(FacetSnapshot.TryDeserializeAttributes(null, out var fromNull));
            Assert.IsNull(fromNull);

            Assert.IsFalse(FacetSnapshot.TryDeserializeAttributes("", out var fromEmpty));
            Assert.IsNull(fromEmpty);

            Assert.IsFalse(FacetSnapshot.TryDeserializeAttributes("   ", out var fromWhitespace));
            Assert.IsNull(fromWhitespace);

            Assert.IsFalse(FacetSnapshot.TryDeserializeAttributes("not json at all", out var fromGarbage));
            Assert.IsNull(fromGarbage);

            Assert.IsFalse(FacetSnapshot.TryDeserializeAttributes("{\"Strength\":100,\"Endurance\":100}", out var fromPartial),
                "a parseable object missing four of the six is not an arrangement");
            Assert.IsNull(fromPartial);

            Assert.IsFalse(FacetSnapshot.TryDeserializeAttributes(
                "{\"Strength\":100,\"Endurance\":100,\"Quickness\":10,\"Coordination\":100,\"Focus\":10,\"Self\":-10}", out var fromNegative),
                "a negative value cannot be an InitLevel and must not parse to a wrapped uint");
            Assert.IsNull(fromNegative);
        }

        /// <summary>
        /// CATCHES: a reader that rejects a row carrying an extra key. Only the six are read, and an
        /// unknown key cannot affect the sum of what is applied, so refusing over one would strand a
        /// slot for no gain.
        /// </summary>
        [TestMethod]
        public void TryDeserializeAttributes_IgnoresKeysBeyondTheSix()
        {
            var json = "{\"Strength\":100,\"Endurance\":100,\"Quickness\":10,\"Coordination\":100,\"Focus\":10,\"Self\":10,\"Undef\":7,\"Charisma\":42}";

            Assert.IsTrue(FacetSnapshot.TryDeserializeAttributes(json, out var round));
            Assert.AreEqual(6, round.Count);
            Assert.AreEqual(100u, round[PropertyAttribute.Strength]);
            Assert.IsFalse(round.ContainsKey(PropertyAttribute.Undef));
        }

        [TestMethod]
        public void Serialize_EmptyInput_ProducesParseableEmpty()
        {
            // The columns are NOT NULL, so an empty build still has to serialize to something valid.
            var skills = FacetSnapshot.SerializeSkills(new List<FacetSkillEntry>());

            Assert.IsFalse(string.IsNullOrWhiteSpace(skills));
            Assert.AreEqual(0, FacetSnapshot.DeserializeSkills(skills).Count);
        }
    }
}
