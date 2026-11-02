using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.PooledLootSourceText;

namespace ACE.Server.Tests.Entity
{
    /// <summary>
    /// The killing-blow suffix on a player's death line: DamageHistory.KillingBlow picks the hit that took the victim to
    /// 0 health only when it is known exactly, and Player.FormatKillingBlowSuffix reports the UNCLAMPED hit, not the
    /// remaining health the write clamped it to. Driven on a Creature (no test here can build a live Player), through
    /// the same UpdateVitalDelta-then-Add order every damage sink uses.
    /// </summary>
    [TestClass]
    public class KillingBlowDeathLineTests
    {
        private static uint nextGuid = 0x7F0003A1;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static Creature NewCreature()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 0x7F0003A1,
                ClassName = "killingblowtest",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Killing Blow Test" },
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

        /// <summary>The order every sink uses: write (clamped), then record the clamped figure plus the thrown one.</summary>
        private static void Hit(Creature victim, WorldObject attacker, uint thrown, bool recordThrown = true)
        {
            var applied = (uint)-victim.UpdateVitalDelta(victim.Health, -(int)thrown);
            if (recordThrown)
                victim.DamageHistory.Add(attacker, DamageType.Slash, applied, thrown);
            else
                victim.DamageHistory.Add(attacker, DamageType.Slash, applied);
        }

        [TestMethod]
        public void Overkill_reports_the_thrown_hit_and_the_health_it_landed_on()
        {
            var victim = NewCreature();
            var attacker = NewCreature();
            var max = victim.Health.MaxValue;

            victim.Health.Current = 456;
            Hit(victim, attacker, 1234);

            Assert.AreEqual(0u, victim.Health.Current, "positive control: the hit killed");

            var blow = victim.DamageHistory.KillingBlow;
            Assert.IsNotNull(blow);
            Assert.AreEqual(-456, blow.Amount, "the log keeps the clamped figure");
            Assert.AreEqual(1234u, blow.Thrown);

            Assert.AreEqual($" (Killing blow: 1234 damage. You had 456/{max} health.)", Player.FormatKillingBlowSuffix(blow));
        }

        [TestMethod]
        public void One_shot_from_full_health_shows_full_health()
        {
            var victim = NewCreature();
            var max = victim.Health.MaxValue;

            Hit(victim, NewCreature(), max + 1);

            Assert.AreEqual($" (Killing blow: {max + 1} damage. You had {max}/{max} health.)", Player.FormatKillingBlowSuffix(victim.DamageHistory.KillingBlow));
        }

        [TestMethod]
        public void A_hit_that_did_not_kill_is_not_a_killing_blow()
        {
            var victim = NewCreature();

            Hit(victim, NewCreature(), 100);

            Assert.IsTrue(victim.Health.Current > 0, "positive control: still alive");
            Assert.IsNull(victim.DamageHistory.KillingBlow);
            Assert.AreEqual("", Player.FormatKillingBlowSuffix(victim.DamageHistory.KillingBlow));
        }

        [TestMethod]
        public void A_sink_that_records_no_thrown_figure_shows_nothing_rather_than_the_clamped_one()
        {
            var victim = NewCreature();
            victim.Health.Current = 50;

            Hit(victim, NewCreature(), 900, recordThrown: false);

            Assert.AreEqual(0u, victim.Health.Current, "positive control: the hit killed");
            Assert.IsNull(victim.DamageHistory.KillingBlow);
        }

        [TestMethod]
        public void A_record_made_before_the_write_is_not_a_killing_blow()
        {
            // ApplyDamageTick records the DoT credit BEFORE the write, so the entry's health is not 0; the tick
            // reports itself in its own "periodic damage" line instead
            var victim = NewCreature();
            victim.Health.Current = 50;

            victim.DamageHistory.Add(NewCreature(), DamageType.Fire, 50, 300);
            victim.UpdateVitalDelta(victim.Health, -300);

            Assert.AreEqual(0u, victim.Health.Current, "positive control: the tick killed");
            Assert.IsNull(victim.DamageHistory.KillingBlow);
        }

        [TestMethod]
        public void An_older_fatal_entry_followed_by_a_heal_is_not_reported()
        {
            var victim = NewCreature();
            victim.Health.Current = 50;
            Hit(victim, NewCreature(), 900);

            victim.UpdateVitalDelta(victim.Health, 10);
            victim.DamageHistory.OnHeal(10);

            Assert.IsNull(victim.DamageHistory.KillingBlow);
        }

        [TestMethod]
        public void Every_player_damage_sink_records_the_thrown_figure()
        {
            // the sinks a monster's hit reaches a player through; a sink left out shows no suffix (never a wrong one)
            StringAssert.Contains(Read("Source/ACE.Server/WorldObjects/Player_Combat.cs"),
                "DamageHistory.Add(source, damageType, damageTaken, thrown: amount);");
            StringAssert.Contains(Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs"),
                "target.DamageHistory.Add(ProjectileSource, Spell.DamageType, amount, thrown: (uint)Math.Round(damage));");

            var magic = Read("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs");
            StringAssert.Contains(magic, "targetCreature.DamageHistory.Add(this, DamageType.Health, (uint)-boost, thrown: (uint)-tryBoost);");
            StringAssert.Contains(magic, "transferSource.DamageHistory.Add(this, DamageType.Health, srcVitalChange, thrown: drainThrown);");

            StringAssert.Contains(Read("Source/ACE.Server/WorldObjects/Player_Death.cs"),
                "playerMsg += FormatKillingBlowSuffix(DamageHistory.KillingBlow);");
        }
    }
}
