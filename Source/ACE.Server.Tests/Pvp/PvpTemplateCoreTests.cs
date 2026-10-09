using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.Facets;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvP Template Facets, the pure core (Docs/Pvp/TEMPLATES.md "Tests that must exist"): the overlay's round
    /// trip on a seeded runtime biota, idempotence, the pools it must never touch, the restore record's size cap,
    /// the JSON envelopes, the power-property allowlist against the TEMPLATES.md table, kit capture and cloning,
    /// and the backstop timing. No database and no live Player.
    ///
    /// THE APPLY AND RESTORE SEQUENCES below are the same calls, in the same order, Player_PvpTemplate.cs makes for
    /// the data half of ApplyPvpTemplateNow and RestorePvpTemplateNow (the live paths add only client pushes, the
    /// strip and the kit).
    /// </summary>
    [TestClass]
    public class PvpTemplateCoreTests
    {
        internal const uint PlayerGuid = 0x50000001;
        internal const uint OwnItemGuid = 0x80000111;

        // ================= fixtures =================

        internal static readonly Skill[] OwnSkills = { Skill.MeleeDefense, Skill.MissileDefense, Skill.HeavyWeapons, Skill.ArcaneLore };
        internal static readonly Skill[] TemplateSkills = { Skill.MeleeDefense, Skill.WarMagic, Skill.LifeMagic, Skill.ManaConversion };

        internal static Biota OwnBiota()
        {
            var b = new Biota
            {
                Id = PlayerGuid,
                WeenieType = WeenieType.Creature,
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>(),
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>(),
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>(),
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesInt64 = new Dictionary<PropertyInt64, long>(),
                PropertiesSpellBook = new Dictionary<int, float> { { 1, 2.0f }, { 2, 2.0f }, { 3, 2.0f } },
                PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry>(),
            };

            var i = 0;
            foreach (var attribute in FacetAttributes.PrimaryAttributes)
            {
                b.PropertiesAttribute[attribute] = new PropertiesAttribute { InitLevel = (uint)(10 + 10 * i), LevelFromCP = (uint)(100 + i), CPSpent = (uint)(100000 + i) };
                i++;
            }

            b.PropertiesAttribute2nd[PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { LevelFromCP = 196, CPSpent = 4000000, CurrentLevel = 412 };
            b.PropertiesAttribute2nd[PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { LevelFromCP = 150, CPSpent = 3000000, CurrentLevel = 300 };
            b.PropertiesAttribute2nd[PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { LevelFromCP = 120, CPSpent = 2000000, CurrentLevel = 250 };

            var s = 0;
            foreach (var skill in OwnSkills)
            {
                b.PropertiesSkill[skill] = new PropertiesSkill { SAC = SkillAdvancementClass.Trained, LevelFromPP = (ushort)(50 + s), PP = (uint)(1000000 + s), InitLevel = 5 };
                s++;
            }

            // half of the replaced ints present (even positions), the other half absent
            var n = 0;
            foreach (var property in PvpTemplatePowerProperties.ReplacedInts.OrderBy(p => (int)p))
            {
                if (n % 2 == 0)
                    b.PropertiesInt[property] = 1 + n;
                n++;
            }

            // the pools and ledgers the overlay must never touch
            b.PropertiesInt[PropertyInt.AvailableSkillCredits] = 7;
            b.PropertiesInt[PropertyInt.TotalSkillCredits] = 56;
            b.PropertiesInt[PropertyInt.AvailableClassAbilityPoints] = 3;
            b.PropertiesInt[PropertyInt.TotalClassAbilityPointsEarned] = 19;
            b.PropertiesInt[PropertyInt.Level] = 275;
            b.PropertiesInt[PropertyInt.Enlightenment] = 2;
            b.PropertiesInt64[PropertyInt64.AvailableExperience] = 123456789012L;
            b.PropertiesInt64[PropertyInt64.TotalExperience] = 991234567890L;
            b.PropertiesInt64[PropertyInt64.AvailableLuminance] = 4444444L;
            b.PropertiesInt64[PropertyInt64.MaximumLuminance] = 5555555L;

            // own buff (part way through its countdown), a cooldown, vitae, and an item-granted row
            b.PropertiesEnchantmentRegistry.Add(Ench(100, 1, -300, 1800, PlayerGuid, beneficial: true));
            b.PropertiesEnchantmentRegistry.Add(Ench(0x8000 + 5, 1, -10, 30, PlayerGuid, beneficial: false));
            // Vitae as the live server writes it: Duration -1 (until removed), the same marker item spells use.
            b.PropertiesEnchantmentRegistry.Add(Ench(PvpTemplateOverlay.VitaeSpellId, 1, 0, -1, PlayerGuid, beneficial: false, statModValue: 0.95f));
            b.PropertiesEnchantmentRegistry.Add(Ench(200, 1, 0, -1, OwnItemGuid, beneficial: true));

            return b;
        }

        internal static PropertiesEnchantmentRegistry Ench(int spellId, ushort layer, double startTime, double duration, uint caster, bool beneficial, float statModValue = 10f) => new PropertiesEnchantmentRegistry
        {
            SpellId = spellId,
            LayerId = layer,
            StartTime = startTime,
            Duration = duration,
            CasterObjectId = caster,
            SpellCategory = (SpellCategory)(spellId % 50 + 1),
            PowerLevel = 400,
            StatModType = beneficial ? EnchantmentTypeFlags.Beneficial | EnchantmentTypeFlags.Additive : EnchantmentTypeFlags.Additive,
            StatModKey = 7,
            StatModValue = statModValue,
            EnchantmentCategory = 1,
        };

        internal static PvpTemplateDefinition Template()
        {
            var d = new PvpTemplateDefinition { Key = "mage", Version = 3, DisplayName = "War Mage" };

            var i = 0;
            foreach (var attribute in FacetAttributes.PrimaryAttributes)
            {
                d.Attributes[(int)attribute] = new PvpTemplateAttributeEntry { InitLevel = (uint)(100 - 5 * i), Ranks = (uint)(190 + i), CpSpent = (uint)(4000000 + i) };
                i++;
            }

            d.Vitals[(int)PropertyAttribute2nd.MaxHealth] = new PvpTemplateVitalEntry { Ranks = 196, CpSpent = 4100000 };
            d.Vitals[(int)PropertyAttribute2nd.MaxStamina] = new PvpTemplateVitalEntry { Ranks = 196, CpSpent = 4100000 };
            d.Vitals[(int)PropertyAttribute2nd.MaxMana] = new PvpTemplateVitalEntry { Ranks = 196, CpSpent = 4100000 };

            foreach (var skill in TemplateSkills)
                d.Skills.Add(new FacetSkillEntry { Skill = skill, Sac = SkillAdvancementClass.Specialized, Ranks = 226, Pp = 4000000, InitLevel = 10 });

            // the OTHER half present, with different values, plus every third even-position one overridden
            var n = 0;
            foreach (var property in PvpTemplatePowerProperties.ReplacedInts.OrderBy(p => (int)p))
            {
                if (n % 2 == 1 || n % 3 == 0)
                    d.PowerInts[(int)property] = 500 + n;
                n++;
            }

            d.Buffs.Add(PvpTemplateEnchantment.From(Ench(100, 1, -999, 3600, 0x51111111, beneficial: true)));
            d.Buffs.Add(PvpTemplateEnchantment.From(Ench(300, 1, -50, 3600, 0x51111111, beneficial: true)));

            d.Spells.AddRange(new[] { 3, 4, 5 });

            return d;
        }

        /// <summary>The apply's data half, in Player_PvpTemplate.ApplyPvpTemplateNow's order.</summary>
        internal static PvpTemplateRestoreRecord Apply(Biota biota, ReaderWriterLockSlim rwLock, PvpTemplateDefinition definition, bool returnOwnBuffs = true)
        {
            var record = PvpTemplateOverlay.Capture(biota, rwLock, definition, Guid.Parse("11111111-2222-3333-4444-555555555555"),
                new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc), returnOwnBuffs,
                new List<FacetEquipEntry> { new FacetEquipEntry { Guid = OwnItemGuid, Wcid = 12345, Slot = 1 } });

            PvpTemplateOverlay.ApplyBuild(biota, rwLock, definition);
            PvpTemplateOverlay.RemoveEnchantments(biota, rwLock, PvpTemplateOverlay.NonItemEnchantments(biota, rwLock));
            PvpTemplateOverlay.AddEnchantmentsPreservingLayer(biota, rwLock, PvpTemplateOverlay.BuildTemplateBuffs(definition, PlayerGuid));

            return record;
        }

        /// <summary>The restore's data half, in Player_PvpTemplate.RestorePvpTemplateNow's order.</summary>
        internal static void Restore(Biota biota, ReaderWriterLockSlim rwLock, PvpTemplateRestoreRecord record)
        {
            PvpTemplateOverlay.RestoreBuild(biota, rwLock, record);
            PvpTemplateOverlay.RemoveEnchantments(biota, rwLock, PvpTemplateOverlay.NonItemEnchantments(biota, rwLock));
            PvpTemplateOverlay.AddEnchantmentsPreservingLayer(biota, rwLock, PvpTemplateOverlay.PlanRestoredEnchantments(record));
        }

        /// <summary>A canonical text of every value the overlay touches, plus every pool it must not.</summary>
        internal static string Fingerprint(Biota b, bool includeUntrainedZeroSkills = false)
        {
            var sb = new StringBuilder();

            foreach (var attribute in FacetAttributes.PrimaryAttributes)
            {
                var a = b.PropertiesAttribute[attribute];
                sb.Append($"attr {attribute} {a.InitLevel}/{a.LevelFromCP}/{a.CPSpent}\n");
            }

            foreach (var vital in PvpTemplateOverlay.MaxVitals)
            {
                var v = b.PropertiesAttribute2nd[vital];
                sb.Append($"vital {vital} {v.InitLevel}/{v.LevelFromCP}/{v.CPSpent}/{v.CurrentLevel}\n");
            }

            foreach (var kvp in b.PropertiesSkill.OrderBy(k => (int)k.Key))
            {
                var s = kvp.Value;
                var untrainedZero = s.SAC == SkillAdvancementClass.Untrained && s.LevelFromPP == 0 && s.PP == 0 && s.InitLevel == 0;
                if (untrainedZero && !includeUntrainedZeroSkills)
                    continue;
                sb.Append($"skill {kvp.Key} {s.SAC}/{s.LevelFromPP}/{s.PP}/{s.InitLevel}\n");
            }

            foreach (var property in PvpTemplatePowerProperties.ReplacedInts.OrderBy(p => (int)p))
                sb.Append($"int {(int)property} {(b.PropertiesInt.TryGetValue(property, out var v) ? v.ToString() : "absent")}\n");

            foreach (var property in PvpTemplatePowerProperties.NeverWrittenInts.OrderBy(p => (int)p))
                sb.Append($"pool {property} {(b.PropertiesInt.TryGetValue(property, out var v) ? v.ToString() : "absent")}\n");

            foreach (var property in PvpTemplatePowerProperties.NeverWrittenInt64s.OrderBy(p => (int)p))
                sb.Append($"pool64 {property} {(b.PropertiesInt64.TryGetValue(property, out var v) ? v.ToString() : "absent")}\n");

            foreach (var spell in b.PropertiesSpellBook.Keys.OrderBy(k => k))
                sb.Append($"spell {spell}\n");

            foreach (var e in b.PropertiesEnchantmentRegistry.OrderBy(e => e.SpellId).ThenBy(e => e.LayerId))
                sb.Append($"ench {e.SpellId}/{e.LayerId}/{e.StartTime}/{e.Duration}/{e.CasterObjectId}/{e.StatModType}/{e.StatModKey}/{e.StatModValue}/{e.SpellCategory}/{e.PowerLevel}\n");

            return sb.ToString();
        }

        internal static string Pools(Biota b) =>
            string.Join(",", PvpTemplatePowerProperties.NeverWrittenInts.OrderBy(p => (int)p).Select(p => b.PropertiesInt.TryGetValue(p, out var v) ? v.ToString() : "-")) + "|" +
            string.Join(",", PvpTemplatePowerProperties.NeverWrittenInt64s.OrderBy(p => (int)p).Select(p => b.PropertiesInt64.TryGetValue(p, out var v) ? v.ToString() : "-"));

        // ================= round trip =================

        /// <summary>
        /// Apply then restore equals the original for every allowlisted property, every attribute, vital, skill,
        /// spell and enchantment row (TEMPLATES.md "Tests that must exist", first line). Reaches
        /// PvpTemplateOverlay.Capture / ApplyBuild / RestoreBuild and the enchantment planners.
        /// </summary>
        [TestMethod]
        public void ApplyThenRestore_EqualsOriginal_ForEveryOverlaidValue()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var original = Fingerprint(biota);

            var record = Apply(biota, rwLock, Template());

            Assert.AreNotEqual(original, Fingerprint(biota), "sanity: the apply must have changed the build, or the round trip proves nothing");

            Restore(biota, rwLock, record);

            Assert.AreEqual(original, Fingerprint(biota));
        }

        /// <summary>While templated, every replaced int reads the TEMPLATE's value (absent where the template has none).</summary>
        [TestMethod]
        public void Apply_WritesTemplateValue_ForEveryReplacedInt()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var definition = Template();

            Apply(biota, rwLock, definition);

            foreach (var property in PvpTemplatePowerProperties.ReplacedInts)
            {
                if (definition.PowerInts.TryGetValue((int)property, out var expected))
                    Assert.AreEqual(expected, biota.PropertiesInt[property], $"{property}");
                else
                    Assert.IsFalse(biota.PropertiesInt.ContainsKey(property), $"{property} must be absent while templated: the template does not have it");
            }

            foreach (var skill in OwnSkills.Except(TemplateSkills))
                Assert.AreEqual(SkillAdvancementClass.Untrained, biota.PropertiesSkill[skill].SAC, $"{skill}: the sweep is authoritative");

            foreach (var skill in TemplateSkills)
                Assert.AreEqual(SkillAdvancementClass.Specialized, biota.PropertiesSkill[skill].SAC, $"{skill}");
        }

        /// <summary>Restore twice is a no-op: the second restore writes the same absolute values (crash invariant 3).</summary>
        [TestMethod]
        public void RestoreTwice_IsANoOp()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var record = Apply(biota, rwLock, Template());

            Restore(biota, rwLock, record);
            var once = Fingerprint(biota, includeUntrainedZeroSkills: true);

            Restore(biota, rwLock, record);

            Assert.AreEqual(once, Fingerprint(biota, includeUntrainedZeroSkills: true));
        }

        /// <summary>
        /// XP, skill credits, CAP, AvailableExperience, TotalExperience (and luminance, level, enlightenment) are
        /// byte-identical while templated AND after the round trip (TEMPLATES.md "Model").
        /// </summary>
        [TestMethod]
        public void Pools_AreByteIdentical_WhileTemplatedAndAfterRestore()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var before = Pools(biota);

            var record = Apply(biota, rwLock, Template());
            Assert.AreEqual(before, Pools(biota), "mid-template");

            Restore(biota, rwLock, record);
            Assert.AreEqual(before, Pools(biota), "after restore");
        }

        /// <summary>
        /// A record tampered to name a pool is ignored, not trusted: the restore only ever writes the replaced set.
        /// Discriminates the guard in RestoreBuild (without it AvailableSkillCredits would become 9999).
        /// </summary>
        [TestMethod]
        public void Restore_IgnoresARecordNamingAPool()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var record = Apply(biota, rwLock, Template());

            record.Ints[(int)PropertyInt.AvailableSkillCredits] = 9999;

            Restore(biota, rwLock, record);

            Assert.AreEqual(7, biota.PropertiesInt[PropertyInt.AvailableSkillCredits]);
        }

        // ================= enchantments =================

        /// <summary>
        /// The own buff comes back with exactly the time it had left at entry (StartTime is kept: the record is not
        /// ticked), the item-granted row is never touched, and the template buffs are full length and self-cast.
        /// </summary>
        [TestMethod]
        public void Enchantments_SwapAndReturn_WithDurationsPreserved()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var record = Apply(biota, rwLock, Template());

            var mid = biota.PropertiesEnchantmentRegistry;
            Assert.IsTrue(mid.Any(e => e.SpellId == 200 && e.Duration == -1), "the item-granted row stays through the apply");
            Assert.IsFalse(mid.Any(e => e.SpellId == PvpTemplateOverlay.VitaeSpellId), "vitae is held in the record while templated");
            Assert.IsFalse(mid.Any(e => e.SpellId == 0x8000 + 5), "cooldowns are held in the record while templated");

            var buff = mid.Single(e => e.SpellId == 300);
            Assert.AreEqual(0, buff.StartTime, "template buffs start at full duration");
            Assert.AreEqual(PlayerGuid, buff.CasterObjectId, "template buffs are cast by the player themselves");

            Restore(biota, rwLock, record);

            var own = biota.PropertiesEnchantmentRegistry.Single(e => e.SpellId == 100);
            Assert.AreEqual(-300, own.StartTime, "the own buff resumes its countdown where it was");
            Assert.AreEqual(1800, own.Duration);
            Assert.IsFalse(biota.PropertiesEnchantmentRegistry.Any(e => e.SpellId == 300), "template buffs leave with the template");

            var vitae = biota.PropertiesEnchantmentRegistry.Single(e => e.SpellId == PvpTemplateOverlay.VitaeSpellId);
            Assert.AreEqual(0.95f, vitae.StatModValue, "vitae comes back at exactly the penalty it had");
            Assert.AreEqual(-1, vitae.Duration);
        }

        /// <summary>Vitae (Duration -1) is not item-granted: it is held in the record, not carried into the match.</summary>
        [TestMethod]
        public void Vitae_IsNotItemGranted()
        {
            var vitae = Ench(PvpTemplateOverlay.VitaeSpellId, 1, 0, -1, PlayerGuid, beneficial: false, statModValue: 0.85f);

            Assert.IsFalse(PvpTemplateOverlay.IsItemGranted(vitae));
            Assert.IsTrue(PvpTemplateOverlay.IsItemGranted(Ench(200, 1, 0, -1, OwnItemGuid, beneficial: true)));

            var record = Apply(OwnBiota(), new ReaderWriterLockSlim(), Template());
            Assert.IsTrue(record.RemovedEnchantments.Any(e => e.SpellId == PvpTemplateOverlay.VitaeSpellId), "vitae is held in the record");
        }

        /// <summary>pvp_template_keep_own_buffs off returns only vitae and cooldowns.</summary>
        [TestMethod]
        public void KeepOwnBuffsOff_ReturnsOnlyVitaeAndCooldowns()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var record = Apply(biota, rwLock, Template(), returnOwnBuffs: false);

            Restore(biota, rwLock, record);

            var spells = biota.PropertiesEnchantmentRegistry.Select(e => e.SpellId).OrderBy(s => s).ToList();
            CollectionAssert.AreEqual(new[] { 200, PvpTemplateOverlay.VitaeSpellId, 0x8000 + 5 }.OrderBy(s => s).ToList(), spells);
        }

        /// <summary>
        /// The dispel sent to the client names vitae at layer 0 (how every wire path presents it, matching retail) even
        /// though the registry row is stored at layer 1; every other row keeps its own layer.
        /// </summary>
        [TestMethod]
        public void ToDispelWire_SendsVitaeAtLayerZero_AndOtherRowsAtTheirOwnLayer()
        {
            var vitae = Ench(PvpTemplateOverlay.VitaeSpellId, 1, 0, -1, PlayerGuid, beneficial: false, statModValue: 0.95f);
            var buff = Ench(300, 3, -50, 3600, PlayerGuid, beneficial: true);

            var wire = PvpTemplateOverlay.ToDispelWire(new[] { vitae, buff });

            Assert.AreEqual(2, wire.Count);
            Assert.AreEqual((ushort)PvpTemplateOverlay.VitaeSpellId, wire[0].SpellId);
            Assert.AreEqual((ushort)0, wire[0].Layer, "vitae is dispelled at layer 0");
            Assert.AreEqual((ushort)300, wire[1].SpellId);
            Assert.AreEqual((ushort)3, wire[1].Layer, "a buff keeps its own layer");
            Assert.AreEqual((ushort)1, vitae.LayerId, "the registry row itself is not rewritten");
        }

        /// <summary>
        /// Crash safety: the vitae row lives in the persisted restore record (PropertyString.PvpTemplateRestore), so a
        /// record round-tripped through its JSON form - what a login sweep after a crash reads - still returns it.
        /// </summary>
        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void Vitae_SurvivesTheSerializedRecord(bool returnOwnBuffs)
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var record = Apply(biota, rwLock, Template(), returnOwnBuffs);

            Assert.IsTrue(PvpTemplateJson.TrySerializeRecord(record, out var json, out _));
            Assert.IsTrue(PvpTemplateJson.TryDeserializeRecord(json, out var reloaded, out var error), error);

            Restore(biota, rwLock, reloaded);

            var vitae = biota.PropertiesEnchantmentRegistry.Single(e => e.SpellId == PvpTemplateOverlay.VitaeSpellId);
            Assert.AreEqual(0.95f, vitae.StatModValue);
        }

        /// <summary>
        /// A re-added row keeps its saved layer when free and moves to the lowest free layer when the spell already
        /// holds it - never a duplicate (spell, layer), which the shard's unique index cannot store.
        /// </summary>
        [TestMethod]
        public void AddPreservingLayer_NeverDuplicatesASpellLayer()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = new Biota { PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry> { Ench(100, 1, 0, -1, OwnItemGuid, true) } };

            var added = PvpTemplateOverlay.AddEnchantmentsPreservingLayer(biota, rwLock, new[] { Ench(100, 1, 0, 60, PlayerGuid, true), Ench(101, 4, 0, 60, PlayerGuid, true) });

            Assert.AreEqual(2, added[0].LayerId, "spell 100 layer 1 is taken by the item row");
            Assert.AreEqual(4, added[1].LayerId, "spell 101 layer 4 is free and kept");
            Assert.AreEqual(biota.PropertiesEnchantmentRegistry.Count, biota.PropertiesEnchantmentRegistry.Select(e => (e.SpellId, e.LayerId)).Distinct().Count());
        }

        // ================= spells =================

        /// <summary>Template spells the player lacks are added and tracked; own spells are never removed.</summary>
        [TestMethod]
        public void Spells_AddAndGate_OwnSpellsSurvive()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var record = Apply(biota, rwLock, Template());

            CollectionAssert.AreEquivalent(new[] { 4, 5 }, record.AddedSpells, "3 was already known");
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 }, biota.PropertiesSpellBook.Keys.ToList(), "own spells stay so spell bars survive");

            Restore(biota, rwLock, record);

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, biota.PropertiesSpellBook.Keys.ToList());
        }

        // ================= record =================

        [TestMethod]
        public void Record_RoundTripsThroughJson()
        {
            var rwLock = new ReaderWriterLockSlim();
            var biota = OwnBiota();
            var record = Apply(biota, rwLock, Template());

            Assert.IsTrue(PvpTemplateJson.TrySerializeRecord(record, out var json, out var bytes));
            Assert.IsTrue(bytes < PvpTemplateJson.MaxRecordBytes);
            Assert.IsTrue(PvpTemplateJson.TryDeserializeRecord(json, out var back, out var error), error);

            // restoring from the deserialized copy must equal restoring from the original
            var other = OwnBiota();
            Apply(other, rwLock, Template());
            Restore(other, rwLock, back);

            Assert.AreEqual(Fingerprint(OwnBiota()), Fingerprint(other));
        }

        /// <summary>A record above 60 KB is refused by the serializer, which the apply checks before writing anything.</summary>
        [TestMethod]
        public void Record_AboveTheCap_IsRefused()
        {
            var record = new PvpTemplateRestoreRecord { TemplateKey = "big" };

            for (var i = 0; i < 2000; i++)
                record.RemovedEnchantments.Add(PvpTemplateEnchantment.From(Ench(1000 + i, 1, -1234.5678, 3600, PlayerGuid, true)));

            Assert.IsFalse(PvpTemplateJson.TrySerializeRecord(record, out var json, out var bytes));
            Assert.IsNull(json);
            Assert.IsTrue(bytes > PvpTemplateJson.MaxRecordBytes, $"sanity: the fixture must exceed the cap ({bytes} bytes)");

            record.RemovedEnchantments.RemoveRange(20, record.RemovedEnchantments.Count - 20);
            Assert.IsTrue(PvpTemplateJson.TrySerializeRecord(record, out json, out bytes), "a normal record serializes");
        }

        [TestMethod]
        public void Json_RefusesUnknownSchemaVersionsAndGarbage()
        {
            var json = PvpTemplateJson.SerializeDefinition(Template());

            Assert.IsTrue(PvpTemplateJson.TryDeserializeDefinition(json, out var definition, out var error), error);
            Assert.AreEqual("mage", definition.Key);

            Assert.IsFalse(PvpTemplateJson.TryDeserializeDefinition(json.Replace("\"v\":1", "\"v\":2"), out _, out error));
            StringAssert.Contains(error, "schema version 2");

            Assert.IsFalse(PvpTemplateJson.TryDeserializeDefinition("{not json", out _, out _));
            Assert.IsFalse(PvpTemplateJson.TryDeserializeDefinition("", out _, out _));
            Assert.IsFalse(PvpTemplateJson.TryDeserializeRecord("{\"v\":7,\"rec\":{}}", out _, out _));
            Assert.IsFalse(PvpTemplateJson.TryDeserializeRecord(null, out _, out _));
        }

        // ================= the allowlist =================

        /// <summary>The replaced set is exactly the TEMPLATES.md power-source table's Replace rows, by id.</summary>
        [TestMethod]
        public void Allowlist_MatchesTheTemplatesTable()
        {
            var expected = new List<int>();
            expected.AddRange(Enumerable.Range(218, 11));   // 218-228 innate attributes + tinker specializations
            expected.AddRange(Enumerable.Range(231, 8));    // 231-238
            expected.AddRange(Enumerable.Range(240, 7));    // 240-246 resistances
            expected.AddRange(Enumerable.Range(294, 9));    // 294-302 foci, crit, skilled
            expected.AddRange(new[] { 309, 310, 326, 328 }); // damage bonus/reduction, jack of all trades, void foci
            expected.Add(327);                               // nether, not in AugProps
            expected.AddRange(new[] { 217, 239, 293 });      // family counters
            expected.AddRange(Enumerable.Range(333, 13));   // 333-345 luminance augs
            expected.Add(365);                               // LumAugAllSkills
            expected.Add(9064);                              // custom spell duration
            expected.AddRange(new[] { 307, 308, 313, 314, 315, 316, 317, 323, 350, 351, 381, 382 }); // base ratings

            CollectionAssert.AreEquivalent(expected.OrderBy(i => i).ToList(), PvpTemplatePowerProperties.ReplacedInts.Select(p => (int)p).OrderBy(i => i).ToList());
        }

        /// <summary>Every AugmentationDevice.AugProps entry is classified exactly once: replaced, or listed as ignored.</summary>
        [TestMethod]
        public void Allowlist_ClassifiesEveryAugPropExactlyOnce()
        {
            foreach (var pair in AugmentationDevice.AugProps)
            {
                var replaced = PvpTemplatePowerProperties.ReplacedInts.Contains(pair.Value);
                var ignored = PvpTemplatePowerProperties.IgnoredAugProps.Contains(pair.Value);

                Assert.IsTrue(replaced ^ ignored, $"AugProps {pair.Key} -> {pair.Value} must be in exactly one of ReplacedInts or IgnoredAugProps (replaced={replaced}, ignored={ignored})");
            }
        }

        [TestMethod]
        public void Allowlist_NeverIncludesAPool()
        {
            foreach (var property in PvpTemplatePowerProperties.NeverWrittenInts)
                Assert.IsFalse(PvpTemplatePowerProperties.ReplacedInts.Contains(property), $"{property}");
        }

        // ================= kit capture and cloning =================

        internal static Biota Item(uint id, uint wcid, WeenieType type, string name, int? wieldLocation = null)
        {
            var b = new Biota
            {
                Id = id,
                WeenieClassId = wcid,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 5000 }, { PropertyInt.DamageRating, 3 }, { PropertyInt.PlacementPosition, 4 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint> { { PropertyInstanceId.Container, PlayerGuid }, { PropertyInstanceId.AllowedWielder, PlayerGuid } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesSpellBook = new Dictionary<int, float> { { 2000, 2.0f } },
                PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry> { Ench(4000, 1, -60, 1800, PlayerGuid, true) },
            };

            if (wieldLocation.HasValue)
                b.PropertiesInt[PropertyInt.CurrentWieldedLocation] = wieldLocation.Value;

            return b;
        }

        [TestMethod]
        public void Capture_BuildsTheKitAndSkipsContainersCoinsAndIssued()
        {
            var character = OwnBiota();
            var wielded = new[] { Item(0x80000001, 3001, WeenieType.MeleeWeapon, "Sword", wieldLocation: (int)EquipMask.MeleeWeapon) };

            var issued = Item(0x80000005, 3005, WeenieType.Food, "Old Issued Potion");
            issued.PropertiesBool[PropertyBool.PvpTemplateIssued] = true;

            var inventory = new[]
            {
                Item(0x80000002, 3002, WeenieType.Food, "Health Potion"),
                Item(0x80000003, 3003, WeenieType.Container, "Pack"),
                Item(0x80000004, 273, WeenieType.Coin, "Pyreal"),
                issued,
            };

            var definition = PvpTemplateKit.CaptureDefinition("melee", null, character, wielded, inventory, out var skipped);

            Assert.AreEqual(2, definition.Kit.Count);
            Assert.AreEqual((int)EquipMask.MeleeWeapon, definition.Kit[0].WieldLocation);
            Assert.IsNull(definition.Kit[1].WieldLocation, "pack items carry no wield location");
            Assert.AreEqual(3, skipped.Count, string.Join("; ", skipped));
            Assert.AreEqual("melee", definition.DisplayName, "a blank display name falls back to the key");

            var kitItem = definition.Kit[0];
            Assert.IsFalse(kitItem.Ints.ContainsKey((int)PropertyInt.CurrentWieldedLocation));
            Assert.IsFalse(kitItem.Ints.ContainsKey((int)PropertyInt.PlacementPosition));
            Assert.IsFalse(kitItem.Ints.ContainsKey((int)PropertyInt.Value));
            Assert.AreEqual(3, kitItem.Ints[(int)PropertyInt.DamageRating], "item ratings are carried");
            Assert.AreEqual(0, kitItem.Enchantments[0].StartTime, "item buffs are normalized to full duration");

            // character side: buffs exclude item rows, cooldowns, vitae and harmful rows
            CollectionAssert.AreEqual(new[] { 100 }, definition.Buffs.Select(b => b.SpellId).ToList());
            Assert.AreEqual(0, definition.Buffs[0].StartTime);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, definition.Spells);
            Assert.AreEqual(OwnSkills.Length, definition.Skills.Count);
            Assert.AreEqual(PvpTemplatePowerProperties.ReplacedInts.Count(p => character.PropertiesInt.ContainsKey(p)), definition.PowerInts.Count);
        }

        /// <summary>
        /// Every clone gets its own guid, the issued stamps, no instance ids or positions, and collections of its own
        /// (mutating one clone never reaches another or the definition).
        /// </summary>
        [TestMethod]
        public void Clone_StampsEveryCopy_WithAFreshGuid()
        {
            var definition = PvpTemplateKit.CaptureDefinition("melee", "Melee", OwnBiota(),
                new[] { Item(0x80000001, 3001, WeenieType.MeleeWeapon, "Sword", wieldLocation: (int)EquipMask.MeleeWeapon) },
                new[] { Item(0x80000002, 3002, WeenieType.Food, "Health Potion") }, out _);

            uint next = 0x80100000;
            var clones = new List<Biota>();

            for (var round = 0; round < 3; round++)
            {
                foreach (var kitItem in definition.Kit)
                    clones.Add(PvpTemplateKit.BuildIssuedBiota(kitItem, next++, PlayerGuid));
            }

            Assert.AreEqual(clones.Count, clones.Select(c => c.Id).Distinct().Count(), "every issued copy has a unique guid");
            Assert.IsFalse(clones.Any(c => c.Id == 0x80000001 || c.Id == 0x80000002), "no clone reuses the template's own guid");

            foreach (var clone in clones)
            {
                Assert.IsTrue(PvpTemplateKit.IsIssuedBiota(clone));
                Assert.AreEqual((int)AttunedStatus.Attuned, clone.PropertiesInt[PropertyInt.Attuned]);
                Assert.AreEqual((int)BondedStatus.Bonded, clone.PropertiesInt[PropertyInt.Bonded]);
                Assert.AreEqual(0, clone.PropertiesInt[PropertyInt.Value]);
                Assert.AreEqual(0, clone.PropertiesIID.Count, "no container, wielder or allowed wielder carried over");
                Assert.IsNull(clone.PropertiesPosition);
                Assert.IsFalse(clone.PropertiesInt.ContainsKey(PropertyInt.CurrentWieldedLocation));
                Assert.IsTrue(clone.PropertiesEnchantmentRegistry.All(e => e.CasterObjectId == PlayerGuid), "item buffs are re-owned by the issued player");
            }

            clones[0].PropertiesInt[PropertyInt.DamageRating] = 99;
            Assert.AreEqual(3, clones[2].PropertiesInt[PropertyInt.DamageRating], "clones share no collections");
            Assert.AreEqual(3, definition.Kit[0].Ints[(int)PropertyInt.DamageRating], "the definition is never mutated through a clone");
        }

        // ================= backstop timing and room =================

        [TestMethod]
        public void Backstop_RestoresOnlyAfterTheDelay_AndResetsInAMatch()
        {
            var t0 = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
            var delay = TimeSpan.FromSeconds(10);
            DateTime? since = null;

            Assert.IsFalse(PvpTemplateBackstop.ShouldRestore(true, false, ref since, t0, delay), "first beat only stamps");
            Assert.AreEqual(t0, since);
            Assert.IsFalse(PvpTemplateBackstop.ShouldRestore(true, false, ref since, t0.AddSeconds(9), delay));
            Assert.IsTrue(PvpTemplateBackstop.ShouldRestore(true, false, ref since, t0.AddSeconds(10), delay));

            Assert.IsFalse(PvpTemplateBackstop.ShouldRestore(true, true, ref since, t0.AddSeconds(11), delay), "a live match clears the stamp");
            Assert.IsNull(since);

            Assert.IsFalse(PvpTemplateBackstop.ShouldRestore(false, false, ref since, t0.AddSeconds(30), delay), "never for an untemplated player");
            Assert.IsNull(since);
        }

        /// <summary>L7: worn kit items that cannot equip (invalid slot, a slot an earlier kit item took, no slot) need pack room.</summary>
        [TestMethod]
        public void KitWornFallbacks_CountsPredictableEquipFailures()
        {
            int Count(params (int, int?)[] worn) => Player.CountPvpTemplateKitWornFallbacks(worn);

            var melee = (int)EquipMask.MeleeWeapon;
            var chest = (int)EquipMask.ChestArmor;

            Assert.AreEqual(0, Count((melee, melee), (chest, chest)));
            Assert.AreEqual(0, Count((melee, null)), "unknown ValidLocations: assume it equips");
            Assert.AreEqual(1, Count((melee, melee), (melee, melee)), "the second item into a taken slot falls back");
            Assert.AreEqual(1, Count((chest, melee)), "a slot outside ValidLocations falls back");
            Assert.AreEqual(1, Count((0, melee)), "no slot falls back");
        }

        /// <summary>L4: a definition with no kit needs no warm-up and completes at once, without touching the database.</summary>
        [TestMethod]
        public void WarmKitWeenies_EmptyKit_IsANoOp()
        {
            var task = PvpTemplateSnapshotService.WarmKitWeeniesAsync(new PvpTemplateDefinition { Key = "k" });

            Assert.IsTrue(task.IsCompleted);
            Assert.IsTrue(PvpTemplateSnapshotService.WarmKitWeeniesAsync(null).IsCompleted);
        }

        [TestMethod]
        public void RoomRefusal_CountsTheMainPackAndPackSlotsSeparately()
        {
            Assert.IsNull(Player.ComposePvpTemplateRoomRefusal(5, 1, 5, 1));
            StringAssert.Contains(Player.ComposePvpTemplateRoomRefusal(6, 0, 4, 9), "2 more free slots");
            StringAssert.Contains(Player.ComposePvpTemplateRoomRefusal(1, 2, 9, 1), "1 more free pack slot ");
        }
    }
}
