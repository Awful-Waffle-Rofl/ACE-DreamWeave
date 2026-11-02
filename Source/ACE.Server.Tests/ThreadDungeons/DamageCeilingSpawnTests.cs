using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Threads damage ceiling on LIVE creatures (<see cref="ThreadDungeonSpawner.ApplyDamageCeiling"/> and
    /// <see cref="DamageCeiling.Apply"/>): the cached weenie is never written, melee and missile are both read against the
    /// one shared median with each source on its own factor, every source ends at or under cap x median after floor rounding (inventory weapons included,
    /// launchers never touched), a Nightfall Sovereign built from its shipped SQL lands at or under 3x, and the call sits
    /// where the plan says in PrepareForPlacement.
    ///
    /// PropertyManager: only the PrepareForPlacement test reads keys (the three WeakPointSpawnTests seeds); it seeds and
    /// restores them in a finally block. Every other test calls the ceiling directly, which reads none.
    /// </summary>
    [TestClass]
    public class DamageCeilingSpawnTests
    {
        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string Signature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,";
        private const double Cap = DamageCeiling.DefaultCap;
        private const double Top = 8.0;

        private static uint nextGuid = 0x7F0D0001;
        private static uint nextWcid = 0x7F0D8001;

        // ---- fixture ---------------------------------------------------------------------------------------

        private static Weenie CreatureWeenie(uint strength, uint coordination, params (CombatBodyPart Part, int DVal)[] parts) => new Weenie
        {
            WeenieClassId = nextWcid++,
            ClassName = "damageceiling",
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 300 },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Ceiling Probe" },
            PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
            {
                [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = strength },
                [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = coordination },
                [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = 200 },
            },
            PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
            {
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 500 },
                [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 500 },
            },
            PropertiesBodyPart = parts.ToDictionary(p => p.Part, p => new PropertiesBodyPart
            {
                DType = DamageType.Slash, DVal = p.DVal, DVar = 0.5f, BaseArmor = 300,
                HLF = 0.1f, HRF = 0.1f, MLF = 0.1f, MRF = 0.1f, LLF = 0.1f, LRF = 0.1f,
            }),
        };

        private static Weenie ItemWeenie(WeenieType type, int damage, double? damageMod = null, CombatStyle? style = null, Skill? skill = null)
        {
            var ints = new Dictionary<PropertyInt, int>();

            if (damage != 0) ints[PropertyInt.Damage] = damage;
            if (style.HasValue) ints[PropertyInt.DefaultCombatStyle] = (int)style.Value;
            if (skill.HasValue) ints[PropertyInt.WeaponSkill] = (int)skill.Value;

            return new Weenie
            {
                WeenieClassId = nextWcid++,
                ClassName = $"ceilingitem{type}",
                WeenieType = type,
                PropertiesInt = ints,
                PropertiesFloat = damageMod.HasValue ? new Dictionary<PropertyFloat, double> { [PropertyFloat.DamageMod] = damageMod.Value } : new Dictionary<PropertyFloat, double>(),
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = $"Ceiling {type}" },
            };
        }

        private static Creature NewCreature(Weenie weenie)
        {
            TestGameTables.EnsureInitialized();
            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject NewItem(Weenie weenie)
        {
            var guid = new ObjectGuid(nextGuid++);

            switch (weenie.WeenieType)
            {
                case WeenieType.MissileLauncher: return new MissileLauncher(weenie, guid);
                case WeenieType.Ammunition: return new Ammunition(weenie, guid);
                case WeenieType.MeleeWeapon: return new MeleeWeapon(weenie, guid);
                case WeenieType.Missile: return new Missile(weenie, guid);
                default: return new GenericObject(weenie, guid);
            }
        }

        private static void Equip(Creature creature, WorldObject item)
        {
            creature.EquippedObjects[item.Guid] = item;
            item.Wielder = creature;
        }

        private static DungeonSpawnPlan Plan(double median, int groupBonus = 0, double top = Top) => new DungeonSpawnPlan
        {
            PerHitMedian = median,
            DamageCeilingCap = Cap,
            DamageCeilingTop = top,
            GroupDamageRatingBonus = groupBonus,
        };

        private static DungeonSpawnPlanEntry Trash => new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null);

        // ---- 1. the cached weenie is untouched -------------------------------------------------------------

        [TestMethod]
        public void The_cached_weenie_DVal_is_untouched_and_the_creature_gets_its_own_parts()
        {
            var weenie = CreatureWeenie(200, 100, (CombatBodyPart.Head, 900), (CombatBodyPart.Hand, 450), (CombatBodyPart.Foot, 0));
            var partsBefore = weenie.PropertiesBodyPart.ToDictionary(kv => kv.Key, kv => kv.Value);
            var c1 = NewCreature(weenie);
            var c2 = NewCreature(weenie);

            Assert.AreSame(weenie.PropertiesBodyPart, c1.Biota.PropertiesBodyPart, "positive control: the creature shares the weenie's dictionary");

            var bodyBefore = DamageCeiling.Measure(c1).Melee;
            var plan = Plan(bodyBefore / 7.0);
            var result = ThreadDungeonSpawner.ApplyDamageCeiling("test", c1, Trash, plan);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.BodyFactor < 1.0, "positive control: the body was compressed");
            Assert.AreNotSame(weenie.PropertiesBodyPart, c1.Biota.PropertiesBodyPart);

            foreach (var kv in partsBefore)
            {
                Assert.AreSame(kv.Value, weenie.PropertiesBodyPart[kv.Key], $"{kv.Key}: the weenie keeps its own part object");
                Assert.AreNotSame(kv.Value, c1.Biota.PropertiesBodyPart[kv.Key], $"{kv.Key}: the creature's part is a clone");
            }

            Assert.AreEqual(900, weenie.PropertiesBodyPart[CombatBodyPart.Head].DVal, "the cached weenie's DVal is untouched");
            Assert.AreEqual(450, weenie.PropertiesBodyPart[CombatBodyPart.Hand].DVal);
            Assert.AreEqual((int)Math.Floor(900 * result.BodyFactor), c1.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal, "one factor, floored");
            Assert.AreEqual((int)Math.Floor(450 * result.BodyFactor), c1.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal, "the same factor on every part");
            Assert.AreEqual(0, c1.Biota.PropertiesBodyPart[CombatBodyPart.Foot].DVal, "a zero part stays zero");

            // A second spawn from the same weenie starts from the AUTHORED damage.
            Assert.AreSame(weenie.PropertiesBodyPart, c2.Biota.PropertiesBodyPart);
            ThreadDungeonSpawner.ApplyDamageCeiling("test", c2, Trash, plan);
            Assert.AreEqual(c1.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal, c2.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
        }

        [TestMethod]
        public void A_plan_with_no_median_is_a_no_op()
        {
            var weenie = CreatureWeenie(400, 100, (CombatBodyPart.Head, 2000));
            var c = NewCreature(weenie);

            Assert.IsNull(ThreadDungeonSpawner.ApplyDamageCeiling("test", c, Trash, Plan(0, groupBonus: 300)));
            Assert.AreSame(weenie.PropertiesBodyPart, c.Biota.PropertiesBodyPart, "nothing cloned");
            Assert.AreEqual(2000, c.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
        }

        [TestMethod]
        public void A_positive_DVal_never_drops_below_one()
        {
            var weenie = CreatureWeenie(55, 55, (CombatBodyPart.Head, 3), (CombatBodyPart.Hand, 1));
            var c = NewCreature(weenie);

            ThreadDungeonSpawner.ApplyDamageCeiling("test", c, Trash, Plan(0.01, groupBonus: 300));

            Assert.AreEqual(1, c.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
            Assert.AreEqual(1, c.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal);
        }

        // ---- 2. melee and missile share one median, each source on its own factor ---------------------------------

        private static (Creature Creature, WorldObject Bow, WorldObject Arrow) Archer(int bodyDVal, int arrowDamage, double bowMod)
        {
            var c = NewCreature(CreatureWeenie(300, 400, (CombatBodyPart.Hand, bodyDVal)));
            var bow = NewItem(ItemWeenie(WeenieType.MissileLauncher, 0, bowMod, CombatStyle.Bow, Skill.MissileWeapons));
            var arrow = NewItem(ItemWeenie(WeenieType.Ammunition, arrowDamage));

            Equip(c, bow);
            Equip(c, arrow);

            return (c, bow, arrow);
        }

        [TestMethod]
        public void Melee_and_missile_are_read_against_the_one_median_each_source_on_its_own_factor()
        {
            // The arrow far above the median, the body at it: only the arrow moves.
            var (a, aBow, aArrow) = Archer(500, 400, 2.0);
            var (meleeA, missileA) = DamageCeiling.Measure(a);

            Assert.AreEqual(DamageCeiling.Predict(800, 400, true), missileA, 1e-6, "missile: arrow x bow DamageMod at the bow factor");
            Assert.AreEqual(DamageCeiling.Predict(500, 300, false), meleeA, 1e-6, "melee: the body at Strength");
            Assert.IsTrue(missileA > meleeA, "guard: the arrow out-hits the body");

            var resultA = ThreadDungeonSpawner.ApplyDamageCeiling("test", a, Trash, Plan(meleeA));

            Assert.AreEqual(1.0, resultA.BodyFactor, "the body at the median is untouched");
            Assert.AreEqual(500, a.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal);
            Assert.IsTrue((aArrow.GetProperty(PropertyInt.Damage) ?? 0) < 400, "the arrow, above the SAME median, was compressed");
            Assert.AreEqual(2.0, aBow.GetProperty(PropertyFloat.DamageMod) ?? 0, 1e-9, "the launcher is never touched");
            Assert.AreEqual(DamageCeiling.TargetPerHit(missileA, meleeA, Cap, Top), resultA.MissileTarget, 1e-9, "the missile target reads the shared median");

            // The reverse: a heavy body and a light arrow against one median between them: only the body moves.
            var (b, _, bArrow) = Archer(3000, 100, 1.0);
            var (meleeB, missileB) = DamageCeiling.Measure(b);
            var median = missileB * 2;

            Assert.IsTrue(meleeB > median * 2, "guard: the body is far above the median");

            var resultB = ThreadDungeonSpawner.ApplyDamageCeiling("test", b, Trash, Plan(median));

            Assert.IsTrue(resultB.BodyFactor < 1.0, "the body was compressed");
            Assert.AreEqual(100, bArrow.GetProperty(PropertyInt.Damage), "an arrow below the median is untouched");
            Assert.AreEqual(0, resultB.Items.Count);
        }

        // ---- 3. every source ends under the ceiling ----------------------------------------------------------

        [TestMethod]
        public void Every_source_ends_at_or_below_cap_times_median_after_rounding_solo_and_grouped()
        {
            foreach (var groupBonus in new[] { 0, 15, 300 })
            {
                var c = NewCreature(CreatureWeenie(450, 380, (CombatBodyPart.Head, 1700), (CombatBodyPart.Breath, 2600)));
                var equipped = NewItem(ItemWeenie(WeenieType.MeleeWeapon, 1400, 1.3));
                var spare = NewItem(ItemWeenie(WeenieType.MeleeWeapon, 2100, 1.1, skill: Skill.FinesseWeapons));
                var thrown = NewItem(ItemWeenie(WeenieType.Missile, 900, 1.2));
                var bow = NewItem(ItemWeenie(WeenieType.MissileLauncher, 0, 2.4, CombatStyle.Crossbow, Skill.MissileWeapons));
                var bolts = NewItem(ItemWeenie(WeenieType.Ammunition, 700));

                Equip(c, equipped);
                Equip(c, bow);
                Equip(c, bolts);
                c.Inventory[spare.Guid] = spare;
                c.Inventory[thrown.Guid] = thrown;

                c.DamageRating = 5 + groupBonus;

                const double Median = 700.0;

                var before = DamageCeiling.MeasureSources(c);
                Assert.AreEqual(5, before.Count, "body, two melee weapons, the thrown weapon, the bolts (the launcher is no source)");
                Assert.IsTrue(before.All(s => s.PerHit > Cap * Median), "guard: every source starts above the ceiling");

                var result = ThreadDungeonSpawner.ApplyDamageCeiling("test", c, Trash, Plan(Median, groupBonus));
                var g = DamageCeiling.GroupFactor(5, groupBonus);

                Assert.IsTrue(result.Items.Any(i => i.Wcid == spare.WeenieClassId), $"G {groupBonus}: the inventory weapon was scaled");
                Assert.AreEqual(2.4, bow.GetProperty(PropertyFloat.DamageMod) ?? 0, 1e-9, "the launcher is never touched");

                foreach (var s in DamageCeiling.MeasureSources(c))
                    Assert.IsTrue(s.PerHit * g <= Cap * Median * (1 + 1e-9), $"G {groupBonus} {(s.Item?.Name ?? "body")}: {s.PerHit} x {g} above {Cap * Median}");

                if (groupBonus > 0)
                    Assert.IsTrue(result.Withheld > 0, $"G {groupBonus}: a creature at the ceiling withholds rating");
                else
                    Assert.AreEqual(0, result.Withheld, "solo withholds nothing");
            }
        }

        [TestMethod]
        public void The_group_bonus_is_not_written_to_the_rating_stamp()
        {
            var c = NewCreature(CreatureWeenie(450, 380, (CombatBodyPart.Head, 3000)));
            c.DamageRating = 305;

            ThreadDungeonSpawner.ApplyDamageCeiling("test", c, Trash, Plan(800, groupBonus: 300));

            Assert.AreEqual(305, c.DamageRating, "base damage is scaled, never the rating (it also feeds spell damage)");
        }

        // ---- 4. the Nightfall Sovereign ------------------------------------------------------------------------

        /// <summary>
        /// Values read from Content/sql/weenies (pinned by <see cref="The_Sovereign_fixture_matches_its_shipped_SQL"/>):
        /// 1004054 Nightfall Sovereign: Strength 470, Coordination 530, DamageRating 5, d_Val 2208 on body parts 5 and 8;
        /// wields 1004088 Shadowsteel Longbow (MissileLauncher, DefaultCombatStyle 16 = Bow, no DamageMod row, so 1.0) and
        /// 1004089 Shadowsteel Arrow (Ammunition, Damage 2208).
        /// </summary>
        private const uint SovereignStrength = 470;
        private const uint SovereignCoordination = 530;
        private const int SovereignDVal = 2208;
        private const int ArrowDamage = 2208;
        private const int SovereignDamageRating = 5;

        private static (Weenie Sovereign, Weenie Bow, Weenie Arrow) SovereignWeenies()
        {
            var sovereign = CreatureWeenie(SovereignStrength, SovereignCoordination, ((CombatBodyPart)5, SovereignDVal), ((CombatBodyPart)8, SovereignDVal),
                (CombatBodyPart.Head, 0), (CombatBodyPart.Chest, 0));
            var bow = ItemWeenie(WeenieType.MissileLauncher, 0, null, CombatStyle.Bow, Skill.MissileWeapons);
            var arrow = ItemWeenie(WeenieType.Ammunition, ArrowDamage);

            sovereign.PropertiesInt[PropertyInt.Level] = 375;
            sovereign.PropertiesInt[PropertyInt.DamageRating] = SovereignDamageRating;
            sovereign.PropertiesCreateList = new List<PropertiesCreateList>
            {
                new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = bow.WeenieClassId },
                new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = arrow.WeenieClassId },
            };

            return (sovereign, bow, arrow);
        }

        [TestMethod]
        public void A_Sovereign_shaped_archer_lands_at_or_under_three_times_the_median()
        {
            var (sovereignWeenie, bowWeenie, arrowWeenie) = SovereignWeenies();

            // The authored read (the median axis) and the live measurement agree on the creature's per-hit.
            var items = new[] { bowWeenie, arrowWeenie }.ToDictionary(w => w.WeenieClassId);
            var authored = CreaturePerHit.FromWeenie(sovereignWeenie, w => items.TryGetValue(w, out var i) ? i : null);

            var expectedMissile = ArrowDamage * 1.0 * (1 + (SovereignCoordination - 55) * 0.008);
            var expectedMelee = SovereignDVal * (1 + (SovereignStrength - 55) * 0.011);

            Assert.AreEqual(expectedMissile, authored.MissilePerHit, 0.05, "missile: 2208 x 1.0 x (1 + 475 x 0.008)");
            Assert.AreEqual(expectedMelee, authored.MeleePerHit, 0.05, "melee: 2208 x (1 + 415 x 0.011)");

            // A live Creature built from a weenie with Wield rows would resolve them through the world database
            // (GenerateWieldList), so the live copy carries no create list and is handed the same items directly.
            sovereignWeenie.PropertiesCreateList = null;

            foreach (var roster in new[] { 1, 2, 21 })
            {
                var groupBonus = GroupScaling.DamageRatingBonusFor(roster, GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap);
                var c = NewCreature(sovereignWeenie);
                var arrow = NewItem(arrowWeenie);

                Equip(c, NewItem(bowWeenie));
                Equip(c, arrow);
                c.DamageRating = SovereignDamageRating + groupBonus;

                var (liveMelee, liveMissile) = DamageCeiling.Measure(c);
                Assert.AreEqual(authored.MissilePerHit, liveMissile, 1e-6, "live missile == authored");
                Assert.AreEqual(authored.MeleePerHit, liveMelee, 1e-6, "live melee == authored");

                // The 2026-10-26 gem-410 ranking put this archer at about 7.9x the median of what spawns; as the pool's
                // worst hitter it is the automatic top. Its melee is read against the SAME median.
                var median = liveMissile / 7.9;

                ThreadDungeonSpawner.ApplyDamageCeiling("test", c, Trash, Plan(median, groupBonus, top: 7.9));

                var (afterMelee, afterMissile) = DamageCeiling.Measure(c);
                var g = DamageCeiling.GroupFactor(SovereignDamageRating, groupBonus);

                Assert.IsTrue(afterMissile * g <= Cap * median * (1 + 1e-9), $"roster {roster}: missile {afterMissile} x {g} above 3x");
                Assert.IsTrue(afterMelee * g <= Cap * median * (1 + 1e-9), $"roster {roster}: melee above 3x");

                if (roster == 1)
                {
                    Assert.AreEqual(Cap, afterMissile / median, 0.01, "solo: the top of the pool lands on the cap");
                    Assert.AreEqual(DamageCeiling.Compress(liveMelee / median, Cap, 7.9), afterMelee / median, 0.01, "solo: melee on the same curve against the same median");
                }

                Assert.AreEqual(ArrowDamage, arrowWeenie.PropertiesInt[PropertyInt.Damage], "the arrow weenie is untouched");
                Assert.AreEqual(SovereignDVal, sovereignWeenie.PropertiesBodyPart[(CombatBodyPart)5].DVal, "the Sovereign weenie is untouched");
            }
        }

        [TestMethod]
        public void The_Sovereign_fixture_matches_its_shipped_SQL()
        {
            var sovereign = PooledLootSourceText.Read("Content/sql/weenies/1004054 Nightfall Sovereign.sql");
            var bow = PooledLootSourceText.Read("Content/sql/weenies/1004088 Shadowsteel Longbow.sql");
            var arrow = PooledLootSourceText.Read("Content/sql/weenies/1004089 Shadowsteel Arrow.sql");

            void Has(string text, string pattern, string what) => Assert.IsTrue(Regex.IsMatch(text, pattern), $"{what} no longer matches: {pattern}");

            Has(sovereign, @"\(1004054,\s+1,\s+470,", "Strength 470");
            Has(sovereign, @"\(1004054,\s+4,\s+530,", "Coordination 530");
            Has(sovereign, @"\(1004054,\s+307,\s+5\)", "DamageRating 5");
            Has(sovereign, @"\(1004054, 5, 4, 2208,", "body part 5 d_Val 2208");
            Has(sovereign, @"\(1004054, 8, 4, 2208,", "body part 8 d_Val 2208");
            Has(sovereign, @"\(1004054, 2, 1004088,", "wields the longbow");
            Has(sovereign, @"\(1004054, 2, 1004089,", "wields the arrows");
            Assert.IsFalse(Regex.IsMatch(bow, $@"\(1004088,\s+{(int)PropertyFloat.DamageMod},"), "the longbow authors no DamageMod, so it reads 1.0");
            Has(arrow, @"\(1004089,\s+44,\s+2208\)", "the arrow's Damage is 2208");

            // The weenie types and the combat style, read as numbers from the SQL and compared with the enums the fixture uses.
            int Captured(string text, string pattern) => int.Parse(Regex.Match(text, pattern).Groups[1].Value);

            Assert.AreEqual((int)WeenieType.MissileLauncher, Captured(bow, @"\(1004088, 'ace1004088-shadowsteellongbow', (\d+),"), "the longbow is a MissileLauncher");
            Assert.AreEqual((int)WeenieType.Ammunition, Captured(arrow, @"\(1004089, 'ace1004089-shadowsteelarrow', (\d+),"), "the arrow is Ammunition");
            Assert.AreEqual((int)CombatStyle.Bow, Captured(bow, @"\(1004088,\s+46,\s+(\d+)\)"), "the longbow's DefaultCombatStyle is Bow");
        }

        // ---- 5. through PrepareForPlacement ------------------------------------------------------------------

        [TestMethod]
        public void PrepareForPlacement_applies_the_ceiling_and_leaves_the_weenie_alone()
        {
            TestGameTables.EnsureInitialized();

            var keys = new (string Key, bool Value)[]
            {
                ("dynamic_dungeons_uplift_attributes", DungeonPopulationLimits.DefaultUpliftAttributes),
                ("dynamic_dungeons_strip_aoe_spells", DungeonSpellFilter.DefaultStripAoeSpellsEnabled),
            };

            try
            {
                foreach (var (key, value) in keys)
                    Assert.IsTrue(PropertyManager.ModifyBool(key, value), $"ModifyBool({key}) returned false: key not registered");

                Assert.IsTrue(PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds));

                var weenie = CreatureWeenie(300, 200, (CombatBodyPart.Head, 1500), (CombatBodyPart.Hand, 900));
                var on = NewCreature(weenie);
                var off = NewCreature(weenie);
                var bodyBefore = DamageCeiling.Measure(on).Melee;

                var planOn = Plan(bodyBefore / 6.0);
                planOn.DamageRating = 12;
                var planOff = Plan(0);
                planOff.DamageRating = 12;

                ThreadDungeonSpawner.PrepareForPlacement(null, on, Trash, planOn, observedMaxNonBoss: 2500);
                ThreadDungeonSpawner.PrepareForPlacement(null, off, Trash, planOff, observedMaxNonBoss: 2500);

                Assert.AreEqual(1500, weenie.PropertiesBodyPart[CombatBodyPart.Head].DVal, "the cached weenie is untouched");
                Assert.AreEqual(1500, off.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal, "no median, no change");
                Assert.IsTrue(on.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal < 1500, "the ceiling ran inside PrepareForPlacement");
                Assert.IsTrue(DamageCeiling.Measure(on).Melee <= Cap * planOn.PerHitMedian * (1 + 1e-9));
                Assert.AreEqual(12, on.DamageRating, "the rating stamp is the plan's, untouched by the ceiling");
            }
            finally
            {
                foreach (var (key, value) in keys)
                    PropertyManager.ModifyBool(key, value);

                PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds);
            }
        }

        // ---- 5b. an armed creature hits with its weapon, not its body --------------------------------------------

        /// <summary>
        /// Monster_Melee.GetBaseDamage uses the equipped melee weapon INSTEAD of the body part's DVal, so an armed creature whose
        /// body out-hits its weapon (the A'nekshen reavers: body 600, dagger 100) measures the weapon on the melee axis. The body
        /// is still scaled on its own factor, so a creature that ends up unarmed is capped too.
        /// </summary>
        [TestMethod]
        public void An_armed_creature_measures_its_weapon_and_still_scales_its_body()
        {
            var creature = NewCreature(CreatureWeenie(400, 100, (CombatBodyPart.Head, 600), (CombatBodyPart.Hand, 300)));
            var dagger = NewItem(ItemWeenie(WeenieType.MeleeWeapon, 100));

            var unarmed = DamageCeiling.Measure(creature);
            Assert.AreEqual(DamageCeiling.Predict(600, 400, false), unarmed.Melee, 1e-9, "unarmed: the body");

            Equip(creature, dagger);
            Assert.IsTrue(DamageCeiling.HasEquippedMeleeWeapon(creature));

            var armed = DamageCeiling.Measure(creature);
            Assert.AreEqual(DamageCeiling.Predict(100, 400, false), armed.Melee, 1e-9, "armed: the weapon, not the larger body");

            // A median low enough that both sources are cut: the axis maximum is the weapon, and the body is still scaled.
            var median = DamageCeiling.Predict(30, 400, false);
            var result = DamageCeiling.Apply(creature, median, Cap, Top, 0, 0);

            Assert.AreEqual(armed.Melee, result.MeleeBefore, 1e-9, "the ceiling reads the weapon as the melee maximum");
            Assert.IsTrue(result.BodyFactor < 1.0 && result.PartsScaled > 0, "the body parts are scaled on their own factor");
            Assert.IsTrue(creature.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal < 600);
            Assert.AreEqual(1, result.Items.Count, "the dagger is scaled on its own factor");
        }

        // ---- 6. source pins ------------------------------------------------------------------------------------

        [TestMethod]
        public void The_ceiling_runs_after_boss_normalization_and_before_rewards_and_the_weak_point()
        {
            const string Call = "ApplyDamageCeiling(logTag, creature, entry, plan);";

            var source = PooledLootSourceText.Read(Spawner);
            var body = PooledLootSourceText.MethodBody(source, Signature);

            var call = body.IndexOf(Call, StringComparison.Ordinal);
            Assert.IsTrue(call >= 0, "PrepareForPlacement no longer calls the ceiling");
            Assert.AreEqual(call, body.LastIndexOf(Call, StringComparison.Ordinal), "the ceiling is called once");

            foreach (var writer in new[] { "NormalizeBoss(", "ReachUpStamp.Apply(creature", "ArchetypeUplift.Apply(creature", "ApplyUplift(logTag, creature", "creature.DamageRating = " })
            {
                var at = body.IndexOf(writer, StringComparison.Ordinal);
                Assert.IsTrue(at >= 0, $"PrepareForPlacement no longer contains: {writer}");
                Assert.IsTrue(at < call, $"'{writer}' must come before the ceiling");
            }

            foreach (var later in new[] { "ApplyWeakPoint(", "// ---- rewards ----", "creature.XpOverride = " })
            {
                var at = body.IndexOf(later, StringComparison.Ordinal);
                Assert.IsTrue(at >= 0, $"PrepareForPlacement no longer contains: {later}");
                Assert.IsTrue(at > call, $"'{later}' must come after the ceiling");
            }
        }

        /// <summary>
        /// A zero median is reported, not silent (code review F2). TryPopulate needs a live run, landblock and world cache,
        /// so the log line cannot be driven from a unit test; the source is pinned instead: after the plan is built, a zero
        /// shared median warns, only while the switch is on and a delegate was passed.
        /// </summary>
        [TestMethod]
        public void TryPopulate_reports_a_zero_median_after_building_the_plan()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), "public static void TryPopulate(ThreadDungeonRun run, Landblock landblock)");

            var build = body.IndexOf("plan = DungeonPopulationBuilder.Build(", StringComparison.Ordinal);
            var gate = body.IndexOf("if (limits.DamageCeiling && perHitOf != null && !(plan.PerHitMedian > 0))", StringComparison.Ordinal);
            var warn = body.IndexOf("log.Warn($\"[DYNDUNGEON] {run} damage ceiling: the per-hit median", StringComparison.Ordinal);

            Assert.IsTrue(build >= 0 && gate > build, "the check runs after the plan is built, gated on the switch and the delegate");
            Assert.IsTrue(warn > gate, "a zero median warns");
        }

        [TestMethod]
        public void Speed_courses_build_without_a_per_hit_delegate()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/SpeedCourses/LiveSpeedCourseWorld.cs");

            Assert.IsTrue(source.Contains("DungeonPopulationBuilder.Build("), "guard: the course still builds a population plan");
            Assert.IsFalse(source.Contains("PerHitOf"), "a speed course must not pass a per-hit delegate (its medians stay 0, the ceiling a no-op)");
            Assert.IsFalse(source.Contains("perHitOf"), "a speed course must not pass a per-hit delegate");
        }

        [TestMethod]
        public void The_tunables_are_registered_at_their_compiled_defaults_and_read_once_per_run()
        {
            Assert.AreEqual(DamageCeiling.DefaultEnabled, DefaultPropertyManager.DefaultBooleanProperties["dynamic_dungeons_damage_ceiling"].Item);
            Assert.AreEqual(DamageCeiling.DefaultCap, DefaultPropertyManager.DefaultDoubleProperties["dynamic_dungeons_damage_ceiling_ratio"].Item, 0.0);
            Assert.AreEqual(DamageCeiling.DefaultReferenceRatio, DefaultPropertyManager.DefaultDoubleProperties["dynamic_dungeons_damage_ceiling_reference_ratio"].Item, 0.0);
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["dynamic_dungeons_damage_ceiling"].Item, "new settings ship ON");
            Assert.AreEqual(2.0, DefaultPropertyManager.DefaultDoubleProperties["dynamic_dungeons_damage_ceiling_ratio"].Item, 0.0, "owner default cap (2026-10-26: 2x the spawn-pool median)");
            Assert.AreEqual(0.0, DefaultPropertyManager.DefaultDoubleProperties["dynamic_dungeons_damage_ceiling_reference_ratio"].Item, 0.0, "owner decision: 0 = auto (the pool max ratio)");

            var limits = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5);
            Assert.AreEqual(DamageCeiling.DefaultEnabled, limits.DamageCeiling);
            Assert.AreEqual(DamageCeiling.DefaultCap, limits.DamageCeilingRatio);
            Assert.AreEqual(DamageCeiling.DefaultReferenceRatio, limits.DamageCeilingReferenceRatio);

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), "internal static DungeonPopulationLimits ReadLimits(GroupScaling group)");

            StringAssert.Contains(body, "PropertyManager.GetBool(\"dynamic_dungeons_damage_ceiling\", DamageCeiling.DefaultEnabled)");
            StringAssert.Contains(body, "DamageCeiling.SanitizeCap(PropertyManager.GetDouble(\"dynamic_dungeons_damage_ceiling_ratio\", DamageCeiling.DefaultCap)");
            StringAssert.Contains(body, "DamageCeiling.SanitizeReferenceRatio(PropertyManager.GetDouble(\"dynamic_dungeons_damage_ceiling_reference_ratio\", DamageCeiling.DefaultReferenceRatio)");

            // ApplyDamageCeiling reads the plan, never PropertyManager, on the placement path.
            var apply = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner),
                "internal static DamageCeilingResult ApplyDamageCeiling(object logTag, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan)");
            Assert.IsFalse(apply.Contains("PropertyManager"), "the ceiling must not read a tunable per creature");
        }
    }
}
