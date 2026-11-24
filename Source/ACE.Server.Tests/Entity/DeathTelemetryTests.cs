using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers.Analytics;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.PooledLootSourceText;

namespace ACE.Server.Tests.Entity
{
    /// <summary>
    /// player_death_event's pure halves (DeathTelemetry): the damage type -> element -> spell category mapping, the
    /// strongest-enchantment selection over a fake registry, the row built from a killing entry whose attacker is gone,
    /// and the queue. No live Player can be built here, so the Player-only steps (the capture gate in DamageHistory.Add,
    /// RecordDeath in OnDeath) are pinned by source scans at the bottom.
    /// </summary>
    [TestClass]
    public class DeathTelemetryTests
    {
        private static uint nextGuid = 0x7F0004A1;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static Creature NewCreature(string name = "Death Telemetry Test", int level = 100)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 0x7F0004A1,
                ClassName = "deathtelemetrytest",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = name },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
                },
            };

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.Health.Current = creature.Health.MaxValue;
            return creature;
        }

        private static PropertiesEnchantmentRegistry Ench(SpellCategory category, int spellId, uint powerLevel, ushort layer = 1)
            => new PropertiesEnchantmentRegistry { SpellCategory = category, SpellId = spellId, PowerLevel = powerLevel, LayerId = layer };

        private static Func<SpellCategory, IEnumerable<PropertiesEnchantmentRegistry>> Registry(params PropertiesEnchantmentRegistry[] all)
            => category => all.Where(e => e.SpellCategory == category).ToList();

        // ---------------------------------------------------------------- element mapping

        [TestMethod]
        public void Each_of_the_seven_elements_maps_to_its_own_vulnerability_and_protection_categories()
        {
            var expected = new (DamageType Type, string Name, SpellCategory Vuln, SpellCategory Prot, SpellCategory ProtRare)[]
            {
                (DamageType.Acid, "acid", SpellCategory.AcidVulnerability, SpellCategory.AcidProtection, SpellCategory.AcidProtectionRare),
                (DamageType.Bludgeon, "bludgeon", SpellCategory.BludgeonVulnerability, SpellCategory.BludgeonProtection, SpellCategory.BludgeonProtectionRare),
                (DamageType.Cold, "cold", SpellCategory.ColdVulnerability, SpellCategory.ColdProtection, SpellCategory.ColdProtectionRare),
                (DamageType.Electric, "electric", SpellCategory.ElectricVulnerability, SpellCategory.ElectricProtection, SpellCategory.ElectricProtectionRare),
                (DamageType.Fire, "fire", SpellCategory.FireVulnerability, SpellCategory.FireProtection, SpellCategory.FireProtectionRare),
                (DamageType.Pierce, "pierce", SpellCategory.PierceVulnerability, SpellCategory.PierceProtection, SpellCategory.PierceProtectionRare),
                (DamageType.Slash, "slash", SpellCategory.SlashVulnerability, SpellCategory.SlashProtection, SpellCategory.SlashProtectionRare),
            };

            foreach (var e in expected)
            {
                Assert.AreEqual(e.Type, DeathTelemetry.ResistElement(e.Type), e.Name);
                Assert.AreEqual(e.Name, DeathTelemetry.ElementName(e.Type));
                Assert.AreEqual(e.Vuln, DeathTelemetry.VulnerabilityCategory(e.Type), e.Name);
                CollectionAssert.AreEqual(new[] { e.Prot, e.ProtRare }, DeathTelemetry.ProtectionCategories(e.Type), e.Name);
            }

            // the numbering the spec cites (SpellCategory.cs:106-121), so a renumbered enum cannot pass silently
            Assert.AreEqual(102, (int)SpellCategory.AcidVulnerability);
            Assert.AreEqual(113, (int)SpellCategory.SlashProtection);
            Assert.AreEqual(116, (int)SpellCategory.ArmorLowering);
            Assert.AreEqual(115, (int)SpellCategory.ArmorRaising);
        }

        [TestMethod]
        public void Undef_health_base_and_multi_flag_types_map_to_none_with_no_categories()
        {
            var none = new[]
            {
                DamageType.Undef, DamageType.Health, DamageType.Stamina, DamageType.Mana, DamageType.Base,
                DamageType.Slash | DamageType.Fire, DamageType.Physical, DamageType.Elemental, DamageType.Cold | DamageType.Base,
            };

            foreach (var dt in none)
            {
                Assert.AreEqual(DamageType.Undef, DeathTelemetry.ResistElement(dt), dt.ToString());
                Assert.AreEqual("none", DeathTelemetry.ElementName(DeathTelemetry.ResistElement(dt)));
                Assert.IsNull(DeathTelemetry.VulnerabilityCategory(DeathTelemetry.ResistElement(dt)));
                Assert.AreEqual(0, DeathTelemetry.ProtectionCategories(DeathTelemetry.ResistElement(dt)).Length);
            }
        }

        [TestMethod]
        public void Nether_is_an_element_with_no_life_category()
        {
            Assert.AreEqual(DamageType.Nether, DeathTelemetry.ResistElement(DamageType.Nether));
            Assert.AreEqual("nether", DeathTelemetry.ElementName(DamageType.Nether));
            Assert.IsNull(DeathTelemetry.VulnerabilityCategory(DamageType.Nether));
            Assert.AreEqual(0, DeathTelemetry.ProtectionCategories(DamageType.Nether).Length);
        }

        // ---------------------------------------------------------------- snapshot selection

        [TestMethod]
        public void Snapshot_reads_the_hit_element_only_and_picks_the_strongest_layer()
        {
            var registry = Registry(
                Ench(SpellCategory.FireVulnerability, 1001, 300),
                Ench(SpellCategory.FireVulnerability, 1002, 400),          // strongest fire vuln
                Ench(SpellCategory.ColdVulnerability, 1003, 999),          // wrong element: ignored
                Ench(SpellCategory.FireProtection, 1004, 350),
                Ench(SpellCategory.FireProtectionRare, 1005, 450),         // rare beats regular on power
                Ench(SpellCategory.ArmorLowering, 1006, 300, layer: 1),
                Ench(SpellCategory.ArmorLowering, 1007, 300, layer: 2),    // tie on power: newer layer
                Ench(SpellCategory.ArmorRaising, 1008, 200));

            var s = DeathTelemetry.BuildDefenseSnapshot(DamageType.Fire, registry);

            Assert.AreEqual("fire", s.Element);
            Assert.AreEqual(true, s.HadVulnerability);
            Assert.AreEqual(1002u, s.VulnerabilitySpellId);
            Assert.AreEqual(true, s.HadElementProtection);
            Assert.AreEqual(1005u, s.ElementProtectionSpellId);
            Assert.IsTrue(s.HadImperil);
            Assert.AreEqual(1007u, s.ImperilSpellId);
            Assert.IsTrue(s.HadArmorSelf);
            Assert.AreEqual(1008u, s.ArmorSelfSpellId);

            // the effective mods are the live capture's job, never the pure builder's
            Assert.IsNull(s.ProtResistMod);
            Assert.IsNull(s.VulnResistMod);
            Assert.IsNull(s.BodyArmorMod);
        }

        [TestMethod]
        public void Snapshot_reports_absence_as_false_and_no_category_as_null()
        {
            var empty = DeathTelemetry.BuildDefenseSnapshot(DamageType.Slash, Registry());
            Assert.AreEqual(false, empty.HadVulnerability);
            Assert.IsNull(empty.VulnerabilitySpellId);
            Assert.AreEqual(false, empty.HadElementProtection);
            Assert.IsFalse(empty.HadImperil);
            Assert.IsFalse(empty.HadArmorSelf);

            // a health hit (drain, harm) still reports Imperil and Armor Self, whatever the kind
            var health = DeathTelemetry.BuildDefenseSnapshot(DamageType.Health, Registry(
                Ench(SpellCategory.SlashVulnerability, 2001, 400),
                Ench(SpellCategory.ArmorLowering, 2002, 400),
                Ench(SpellCategory.ArmorRaisingRare, 2003, 400)));

            Assert.AreEqual("none", health.Element);
            Assert.IsNull(health.HadVulnerability, "no element: not applicable, not false");
            Assert.IsNull(health.HadElementProtection);
            Assert.IsTrue(health.HadImperil);
            Assert.AreEqual(2002u, health.ImperilSpellId);
            Assert.IsTrue(health.HadArmorSelf);
            Assert.AreEqual(2003u, health.ArmorSelfSpellId);

            var nether = DeathTelemetry.BuildDefenseSnapshot(DamageType.Nether, Registry());
            Assert.AreEqual("nether", nether.Element);
            Assert.IsNull(nether.HadVulnerability);
            Assert.IsNull(nether.HadElementProtection);
        }

        [TestMethod]
        public void Snapshot_tolerates_a_null_list_from_the_lookup()
        {
            var s = DeathTelemetry.BuildDefenseSnapshot(DamageType.Acid, _ => null);
            Assert.AreEqual(false, s.HadVulnerability);
            Assert.IsFalse(s.HadImperil);
        }

        // ---------------------------------------------------------------- the killing entry and the row

        [TestMethod]
        public void The_thrown_overload_stamps_kind_crit_and_spell_on_the_entry()
        {
            var victim = NewCreature();
            var attacker = NewCreature();

            victim.Health.Current = 100;
            var applied = (uint)-victim.UpdateVitalDelta(victim.Health, -900);
            victim.DamageHistory.Add(attacker, DamageType.Fire, applied, 900, kind: DamageAttackKind.Magic, isCritical: true, spellId: 4433);

            var kb = victim.DamageHistory.KillingBlow;
            Assert.IsNotNull(kb);
            Assert.AreEqual(DamageAttackKind.Magic, kb.AttackKind);
            Assert.AreEqual(true, kb.IsCritical);
            Assert.AreEqual(4433u, kb.SpellId);

            // a non-player victim never reaches the capture: no attacker or defensive snapshot
            Assert.IsNull(kb.AttackerWcid);
            Assert.IsNull(kb.Defense);
        }

        [TestMethod]
        public void Killing_blow_context_survives_the_attacker_reference_going_null()
        {
            var victim = NewCreature();
            var attacker = NewCreature("Shadow of Doom", 480);
            var attackerGuid = attacker.Guid.Full;
            var attackerWcid = attacker.WeenieClassId;

            victim.Health.Current = 478;
            var applied = (uint)-victim.UpdateVitalDelta(victim.Health, -4106);
            victim.DamageHistory.Add(attacker, DamageType.Slash, applied, 4106, kind: DamageAttackKind.Melee, isCritical: false);

            var kb = victim.DamageHistory.KillingBlow;
            DeathTelemetry.SnapshotAttacker(kb, attacker);
            kb.Defense = DeathTelemetry.BuildDefenseSnapshot(kb.DamageType, Registry(Ench(SpellCategory.SlashVulnerability, 3001, 400)));

            // the attacker is gone before the row is built: nothing on the row may need it
            attacker = null;

            var row = new AnalyticsDatabase.PlayerDeathRow();
            DeathTelemetry.ApplyKillingBlow(row, kb);

            Assert.IsTrue(row.KillingBlowKnown);
            Assert.AreEqual(4106u, row.Thrown);
            Assert.AreEqual(478u, row.HealthBefore);
            Assert.AreEqual(kb.MaxHealth, row.MaxHealth);
            Assert.AreEqual("melee", row.AttackKind);
            Assert.AreEqual("Slashing", row.DamageType);
            Assert.AreEqual(false, row.IsCritical);
            Assert.IsNull(row.SpellId);
            Assert.AreEqual(attackerGuid, row.KillerGuid);
            Assert.AreEqual(attackerWcid, row.KillerWcid);
            Assert.AreEqual("Shadow of Doom", row.KillerName);
            Assert.AreEqual(480, row.KillerLevel);
            Assert.AreEqual(false, row.KillerIsPlayer);
            Assert.AreEqual("slash", row.Element);
            Assert.AreEqual(true, row.HadVulnerability);
            Assert.AreEqual(3001u, row.VulnerabilitySpellId);
        }

        [TestMethod]
        public void A_null_attacker_at_the_hit_leaves_the_snapshot_empty_but_keeps_the_guid()
        {
            var victim = NewCreature();
            var attacker = NewCreature();

            victim.Health.Current = 10;
            var applied = (uint)-victim.UpdateVitalDelta(victim.Health, -50);
            victim.DamageHistory.Add(attacker, DamageType.Acid, applied, 50, kind: DamageAttackKind.Hazard);

            var kb = victim.DamageHistory.KillingBlow;
            DeathTelemetry.SnapshotAttacker(kb, null);

            var row = new AnalyticsDatabase.PlayerDeathRow();
            DeathTelemetry.ApplyKillingBlow(row, kb);

            Assert.AreEqual(attacker.Guid.Full, row.KillerGuid);
            Assert.IsNull(row.KillerWcid);
            Assert.IsNull(row.KillerName);
            Assert.IsNull(row.KillerLevel);
            Assert.AreEqual("hazard", row.AttackKind);
            Assert.IsNull(row.Element, "no defensive snapshot captured: element columns stay null");
        }

        [TestMethod]
        public void An_unknown_killing_blow_marks_the_row_and_falls_back_to_the_last_damager()
        {
            var row = new AnalyticsDatabase.PlayerDeathRow { MaxHealth = 478 };
            DeathTelemetry.ApplyKillingBlow(row, null);

            Assert.IsFalse(row.KillingBlowKnown);
            Assert.IsNull(row.Thrown);
            Assert.IsNull(row.AttackKind);
            Assert.AreEqual(478u, row.MaxHealth, "an unknown blow leaves the death-time max");

            var dotter = NewCreature("Plague Rat", 300);
            DeathTelemetry.ApplyFallbackKiller(row, new DamageHistoryInfo(dotter), DamageType.Acid);

            Assert.AreEqual("Acid", row.DamageType);
            Assert.AreEqual(dotter.Guid.Full, row.KillerGuid);
            Assert.AreEqual("Plague Rat", row.KillerName);
            Assert.AreEqual(dotter.WeenieClassId, row.KillerWcid);
            Assert.AreEqual(300, row.KillerLevel);

            var none = new AnalyticsDatabase.PlayerDeathRow();
            DeathTelemetry.ApplyFallbackKiller(none, null, DamageType.Bludgeon);
            Assert.AreEqual("Bludgeoning", none.DamageType);
            Assert.IsNull(none.KillerGuid);
        }

        [TestMethod]
        public void A_thrown_figure_alone_is_what_makes_the_blow_known()
        {
            var victim = NewCreature();
            victim.Health.Current = 10;
            var applied = (uint)-victim.UpdateVitalDelta(victim.Health, -50);
            victim.DamageHistory.Add(NewCreature(), DamageType.Cold, applied);   // a three-argument sink

            var row = new AnalyticsDatabase.PlayerDeathRow();
            DeathTelemetry.ApplyKillingBlow(row, victim.DamageHistory.KillingBlow);
            Assert.IsFalse(row.KillingBlowKnown);
        }

        // ---------------------------------------------------------------- attack kind resolution

        [TestMethod]
        public void Attack_kind_follows_the_combat_type_and_the_caller_flags()
        {
            Assert.AreEqual(DamageAttackKind.Melee, DamageAttackKindNames.Of(CombatType.Melee));
            Assert.AreEqual(DamageAttackKind.Missile, DamageAttackKindNames.Of(CombatType.Missile));
            Assert.AreEqual(DamageAttackKind.Magic, DamageAttackKindNames.Of(CombatType.Magic));

            Assert.AreEqual(DamageAttackKind.Hazard, DamageAttackKindNames.Default(isHazard: true, sendDefenderNotification: true));
            Assert.AreEqual(DamageAttackKind.Hazard, DamageAttackKindNames.Default(isHazard: true, sendDefenderNotification: false));
            Assert.AreEqual(DamageAttackKind.Effect, DamageAttackKindNames.Default(isHazard: false, sendDefenderNotification: false));
            Assert.AreEqual(DamageAttackKind.Other, DamageAttackKindNames.Default(isHazard: false, sendDefenderNotification: true));
        }

        [TestMethod]
        public void Crit_is_recorded_only_for_kinds_that_roll_one()
        {
            foreach (var kind in new[] { DamageAttackKind.Melee, DamageAttackKind.Missile, DamageAttackKind.Magic })
            {
                Assert.AreEqual(true, DamageAttackKindNames.CritFor(kind, true), kind.ToString());
                Assert.AreEqual(false, DamageAttackKindNames.CritFor(kind, false), kind.ToString());
            }

            foreach (var kind in new[] { DamageAttackKind.Hazard, DamageAttackKind.Effect, DamageAttackKind.Other })
            {
                Assert.IsNull(DamageAttackKindNames.CritFor(kind, false), kind + ": not applicable, never false");
                Assert.IsNull(DamageAttackKindNames.CritFor(kind, true), kind.ToString());
            }

            // Player.TakeDamage resolves the kind once and passes it through CritFor (no live Player here)
            var take = Code(MethodBody(Read("Source/ACE.Server/WorldObjects/Player_Combat.cs"),
                "public int TakeDamage(WorldObject source, DamageType damageType, float _amount, BodyPart bodyPart, bool crit = false, AttackConditions attackConditions = AttackConditions.None, bool sendDefenderNotification = true, bool isHazard = false, DamageAttackKind? attackKind = null)"));
            StringAssert.Contains(take, "isCritical: DamageAttackKindNames.CritFor(recordedKind, crit)");
        }

        [TestMethod]
        public void A_creature_at_zero_gets_the_hit_context_but_never_the_player_snapshot()
        {
            var victim = NewCreature();
            var attacker = NewCreature("Lugian Juggernaut", 350);

            victim.Health.Current = 30;
            var applied = (uint)-victim.UpdateVitalDelta(victim.Health, -777);
            Assert.AreEqual(0u, victim.Health.Current, "precondition: the hit ends at 0 health");

            victim.DamageHistory.Add(attacker, DamageType.Bludgeon, applied, 777, kind: DamageAttackKind.Missile, isCritical: false, spellId: null);

            var entry = victim.DamageHistory.Log[^1];
            Assert.AreEqual(0u, entry.CurrentHealth);
            Assert.AreEqual(777u, entry.Thrown);
            Assert.AreEqual(DamageAttackKind.Missile, entry.AttackKind);
            Assert.AreEqual(false, entry.IsCritical);
            Assert.IsNull(entry.SpellId);

            // the capture is Player-only: a creature victim gets no attacker or defensive snapshot
            Assert.IsNull(entry.Defense);
            Assert.IsNull(entry.AttackerWcid);
            Assert.IsNull(entry.AttackerName);
            Assert.IsNull(entry.AttackerLevel);
            Assert.IsNull(entry.AttackerIsPlayer);

            // the snapshot itself, driven directly with a real Creature attacker
            DeathTelemetry.SnapshotAttacker(entry, attacker);
            Assert.AreEqual(attacker.WeenieClassId, entry.AttackerWcid);
            Assert.AreEqual("Lugian Juggernaut", entry.AttackerName);
            Assert.AreEqual(350, entry.AttackerLevel);
            Assert.AreEqual(false, entry.AttackerIsPlayer);
        }

        [TestMethod]
        public void ApplyKillingBlow_without_a_defense_snapshot_leaves_every_defensive_column_null()
        {
            var victim = NewCreature();
            victim.Health.Current = 20;
            var applied = (uint)-victim.UpdateVitalDelta(victim.Health, -90);
            victim.DamageHistory.Add(NewCreature(), DamageType.Pierce, applied, 90, kind: DamageAttackKind.Melee, isCritical: true);

            var row = new AnalyticsDatabase.PlayerDeathRow();
            DeathTelemetry.ApplyKillingBlow(row, victim.DamageHistory.KillingBlow);

            Assert.IsTrue(row.KillingBlowKnown);
            Assert.AreEqual(true, row.IsCritical);
            Assert.AreEqual("Piercing", row.DamageType);
            Assert.IsNull(row.Element);
            Assert.IsNull(row.HadVulnerability);
            Assert.IsNull(row.VulnerabilitySpellId);
            Assert.IsNull(row.HadImperil);
            Assert.IsNull(row.ImperilSpellId);
            Assert.IsNull(row.HadElementProtection);
            Assert.IsNull(row.ElementProtectionSpellId);
            Assert.IsNull(row.HadArmorSelf);
            Assert.IsNull(row.ArmorSelfSpellId);
            Assert.IsNull(row.ProtResistMod);
            Assert.IsNull(row.VulnResistMod);
            Assert.IsNull(row.BodyArmorMod);
        }

        [TestMethod]
        public void ApplyKillingBlow_with_a_defense_snapshot_copies_every_defensive_column()
        {
            var victim = NewCreature();
            victim.Health.Current = 20;
            var applied = (uint)-victim.UpdateVitalDelta(victim.Health, -90);
            victim.DamageHistory.Add(NewCreature(), DamageType.Cold, applied, 90, kind: DamageAttackKind.Magic, isCritical: false, spellId: 77);

            var kb = victim.DamageHistory.KillingBlow;
            kb.Defense = new DeathDefenseSnapshot
            {
                Element = "cold",
                HadVulnerability = true, VulnerabilitySpellId = 11,
                HadImperil = true, ImperilSpellId = 12,
                HadElementProtection = false, ElementProtectionSpellId = null,
                HadArmorSelf = true, ArmorSelfSpellId = 13,
                ProtResistMod = 0.5f, VulnResistMod = 2.1f, BodyArmorMod = -150,
            };

            var row = new AnalyticsDatabase.PlayerDeathRow();
            DeathTelemetry.ApplyKillingBlow(row, kb);

            Assert.AreEqual(77u, row.SpellId);
            Assert.AreEqual("cold", row.Element);
            Assert.AreEqual(true, row.HadVulnerability);
            Assert.AreEqual(11u, row.VulnerabilitySpellId);
            Assert.AreEqual(true, row.HadImperil);
            Assert.AreEqual(12u, row.ImperilSpellId);
            Assert.AreEqual(false, row.HadElementProtection);
            Assert.IsNull(row.ElementProtectionSpellId);
            Assert.AreEqual(true, row.HadArmorSelf);
            Assert.AreEqual(13u, row.ArmorSelfSpellId);
            Assert.AreEqual(0.5f, row.ProtResistMod);
            Assert.AreEqual(2.1f, row.VulnResistMod);
            Assert.AreEqual(-150, row.BodyArmorMod);
        }

        [TestMethod]
        public void An_unknown_blow_leaves_every_killing_blow_column_null_after_the_fallback()
        {
            var row = new AnalyticsDatabase.PlayerDeathRow { MaxHealth = 478 };
            DeathTelemetry.ApplyKillingBlow(row, null);
            DeathTelemetry.ApplyFallbackKiller(row, new DamageHistoryInfo(NewCreature("Plague Rat", 300)), DamageType.Acid);

            Assert.IsFalse(row.KillingBlowKnown);
            Assert.IsNull(row.Thrown);
            Assert.IsNull(row.HealthBefore);
            Assert.IsNull(row.AttackKind);
            Assert.IsNull(row.IsCritical);
            Assert.IsNull(row.SpellId);
            Assert.IsNull(row.Element);
            Assert.IsNull(row.HadImperil);
            Assert.IsNull(row.BodyArmorMod);
            Assert.AreEqual(478u, row.MaxHealth);

            // what the fallback DOES fill
            Assert.AreEqual("Acid", row.DamageType);
            Assert.IsNotNull(row.KillerGuid);
            Assert.AreEqual("Plague Rat", row.KillerName);
            Assert.AreEqual(false, row.KillerIsPlayer);
        }

        // ---------------------------------------------------------------- queue

        [TestMethod]
        public void Queue_is_a_no_op_while_disabled_and_bounded_while_enabled()
        {
            try
            {
                AnalyticsManager.ResetPlayerDeathQueueForTests();

                AnalyticsManager.SetEnabledForTests(false);
                AnalyticsManager.RecordPlayerDeath(new AnalyticsDatabase.PlayerDeathRow());
                Assert.AreEqual(0, AnalyticsManager.DrainPlayerDeathQueueForTests().Count, "disabled: nothing queued");

                AnalyticsManager.SetEnabledForTests(true);
                for (var i = 0; i < AnalyticsManager.PlayerDeathQueueCap + 3; i++)
                    AnalyticsManager.RecordPlayerDeath(new AnalyticsDatabase.PlayerDeathRow());
                AnalyticsManager.RecordPlayerDeath(null);

                Assert.AreEqual(3L, AnalyticsManager.PlayerDeathsDropped);
                Assert.AreEqual(AnalyticsManager.PlayerDeathQueueCap, AnalyticsManager.DrainPlayerDeathQueueForTests().Count);
            }
            finally
            {
                AnalyticsManager.SetEnabledForTests(false);
                AnalyticsManager.ResetPlayerDeathQueueForTests();
            }
        }

        [TestMethod]
        public void RecordDeath_with_analytics_off_queues_nothing_and_never_throws()
        {
            try
            {
                AnalyticsManager.SetEnabledForTests(false);
                AnalyticsManager.ResetPlayerDeathQueueForTests();

                DeathTelemetry.RecordDeath(null, null, DamageType.Fire, "You died.");
                Assert.AreEqual(0, AnalyticsManager.DrainPlayerDeathQueueForTests().Count);
            }
            finally
            {
                AnalyticsManager.ResetPlayerDeathQueueForTests();
            }
        }

        // ---------------------------------------------------------------- wiring (source scans)

        private static string Code(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*", string.Empty);

        private static void AssertBefore(string body, string earlier, string later)
        {
            var a = body.IndexOf(earlier, StringComparison.Ordinal);
            var b = body.IndexOf(later, StringComparison.Ordinal);
            Assert.IsTrue(a >= 0, "missing: " + earlier);
            Assert.IsTrue(b >= 0, "missing: " + later);
            Assert.IsTrue(a < b, $"'{earlier}' must precede '{later}'");
        }

        [TestMethod]
        public void Wiring_OnDeath_records_the_finished_victim_line()
        {
            var onDeath = Code(MethodBody(Read("Source/ACE.Server/WorldObjects/Player_Death.cs"),
                "public override DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)"));

            AssertBefore(onDeath, "playerMsg += FormatKillingBlowSuffix(DamageHistory.KillingBlow);",
                "ACE.Server.Managers.Analytics.DeathTelemetry.RecordDeath(this, lastDamager, damageType, playerMsg);");
            AssertBefore(onDeath, "DeathTelemetry.RecordDeath(", "var msgYourDeath = new GameEventVictimNotification(Session, playerMsg);");
        }

        [TestMethod]
        public void Wiring_the_thrown_overload_captures_only_a_player_at_zero()
        {
            var add = Code(MethodBody(Read("Source/ACE.Server/Entity/DamageHistory.cs"),
                "public void Add(WorldObject attacker, DamageType damageType, uint amount, uint thrown, DamageAttackKind? kind = null, bool? isCritical = null, uint? spellId = null)"));

            StringAssert.Contains(add, "if (entry.CurrentHealth == 0 && Creature is Player victim)");
            AssertBefore(add, "entry.Thrown = thrown;", "DeathTelemetry.CaptureKillingHit(entry, victim, attacker);");
        }

        [TestMethod]
        public void Wiring_explicit_kinds_at_the_callers_that_are_not_derivable()
        {
            StringAssert.Contains(Read("Source/ACE.Server/WorldObjects/Hotspot.cs"),
                "player.TakeDamage(this, DamageType, amount, BodyPart.Foot, attackKind: DamageAttackKind.Hazard);");

            var reflect = Read("Source/ACE.Server/MonsterEffects/Effects/ReflectEffect.cs");
            Assert.AreEqual(2, reflect.Split("attackerPlayer.TakeDamage(defender, damageType, reflected, BodyPart.Chest, attackKind: DamageAttackKind.Effect);").Length - 1);

            var forwarder = Code(MethodBody(Read("Source/ACE.Server/WorldObjects/Player_Combat.cs"), "public int TakeDamage(WorldObject source, DamageEvent damageEvent)"));
            StringAssert.Contains(forwarder, "attackKind: DamageAttackKindNames.Of(damageEvent.CombatType)");
        }

        [TestMethod]
        public void Wiring_DrainPlayerDeaths_runs_in_the_worker_loop_and_the_final_pass()
        {
            var manager = Read("Source/ACE.Server/Managers/Analytics/AnalyticsManager.cs");

            var loop = MethodBody(manager, "private static void WorkerLoop(");
            AssertBefore(loop, "DrainTrialEvents();", "DrainPlayerDeaths();");
            AssertBefore(loop, "DrainPlayerDeaths();", "DrainSessions();");

            var final = MethodBody(manager, "internal static void FinalSessionPass(");
            AssertBefore(final, "while (!playerDeathQueue.IsEmpty", "DrainPlayerDeaths();");
            AssertBefore(final, "DrainPlayerDeaths();", "if (sessionWriter == null)");
        }
    }
}
