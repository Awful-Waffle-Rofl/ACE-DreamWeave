using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom Summons Task 5: SummonItemDescription.Compose. The pet-weenie and spell-name lookups are
    /// injectable hooks (the real ones read the world DB and the DAT), faked here and restored in a finally.
    /// </summary>
    [TestClass]
    public class SummonItemDescriptionTests
    {
        private static int nextGuid = 0x7F5C0000;
        private static uint nextWcid = 997000;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static ObjectGuid NextGuid() => new ObjectGuid((uint)Interlocked.Increment(ref nextGuid));

        private static Weenie PetWeenie(int level, int magicSkill)
        {
            return new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Level, level } },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.WarMagic, new PropertiesSkill { InitLevel = (uint)magicSkill, SAC = SkillAdvancementClass.Trained } },
                    { Skill.LifeMagic, new PropertiesSkill { InitLevel = (uint)magicSkill, SAC = SkillAdvancementClass.Trained } },
                },
            };
        }

        private static PetDevice MakeDevice(bool binder, uint? spell1, uint? spell2, UiEffects? element = null, int ratingDamage = 12)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.PetClass, 5000 },
                { PropertyInt.GearDamage, ratingDamage },
                { PropertyInt.GearCrit, 9 },
            };
            if (element.HasValue) ints[PropertyInt.UiEffects] = (int)element.Value;

            var dids = new Dictionary<PropertyDataId, uint>();
            if (spell1.HasValue) dids[PropertyDataId.SummonBoundSpell1] = spell1.Value;
            if (spell2.HasValue) dids[PropertyDataId.SummonBoundSpell2] = spell2.Value;

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.PetDevice,
                PropertiesInt = ints,
                PropertiesDID = dids,
                PropertiesBool = binder ? new Dictionary<PropertyBool, bool> { { PropertyBool.SummonSpellBinder, true } } : null,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Lich" } },
            };

            return new PetDevice(weenie, NextGuid());
        }

        private static string Compose(WorldObject item, int level = 200, int skill = 600)
        {
            var oldWeenie = SummonItemDescription.PetWeenieLookup;
            var oldName = SummonItemDescription.SpellNameLookup;
            try
            {
                SummonItemDescription.PetWeenieLookup = wcid => wcid == 5000 ? PetWeenie(level, skill) : null;
                SummonItemDescription.SpellNameLookup = id => id == 111 ? "Fire Bolt" : id == 222 ? "Heal Self" : $"spell {id}";

                return SummonItemDescription.Compose(item);
            }
            finally
            {
                SummonItemDescription.PetWeenieLookup = oldWeenie;
                SummonItemDescription.SpellNameLookup = oldName;
            }
        }

        [TestMethod]
        [DataRow(150, 400)]
        [DataRow(180, 500)]
        [DataRow(200, 600)]
        public void Description_ContainsLevelAndMagicSkill(int level, int skill)
        {
            var text = Compose(MakeDevice(true, null, null), level, skill);

            StringAssert.Contains(text, $"Level {level}");
            StringAssert.Contains(text, $"Magic skill: {skill}");
        }

        [TestMethod]
        [DataRow(300, 890)]
        public void Description_ContainsLevelAndMagicSkill_ForTheLevel300Lich(int level, int skill)
        {
            var text = Compose(MakeDevice(true, null, null), level, skill);

            StringAssert.Contains(text, $"Level {level}");
            StringAssert.Contains(text, $"Magic skill: {skill}");
        }

        /// <summary>
        /// A caster pet carrying the Mana Rush ramp (axis=castspeed on=cast) gets one line derived from the authored
        /// numbers, between the spell slots and the ratings; a pet with no such ramp (every Ossuary caster) gets none,
        /// and neither does a cast-speed ramp that accrues on hit rather than on cast.
        /// </summary>
        [TestMethod]
        public void ManaRush_IsDescribedFromTheAuthoredRamp_AndOnlyForOnCastCastSpeed()
        {
            Weenie Pet(string effects)
            {
                var w = PetWeenie(225, 700);
                w.PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.MonsterCombatEffects, effects } };
                return w;
            }

            const string expected = "Mana Rush: each spell it casts speeds its casting by 6.6%, up to 5 times (+33%), and the bonus fades 20-25 s after its last cast";

            Assert.AreEqual(expected, SummonItemDescription.ManaRushText(Pet("ramp axis=castspeed on=cast per=0.066 max=5 window=20")));
            Assert.AreEqual("Mana Rush: each spell it casts speeds its casting by 10%, up to 3 times (+30%), and the bonus fades 12-17 s after its last cast",
                SummonItemDescription.ManaRushText(Pet("ramp axis=castspeed on=cast per=0.1 max=3 window=12")), "the text follows a retuned string");

            var fastHeartbeat = Pet("ramp axis=castspeed on=cast per=0.066 max=5 window=20");
            fastHeartbeat.PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.HeartbeatInterval, 2.0 } };
            StringAssert.Contains(SummonItemDescription.ManaRushText(fastHeartbeat), "fades 20-22 s after", "the lapse waits for the pet's own heartbeat");

            Assert.IsNull(SummonItemDescription.ManaRushText(Pet("ramp axis=castspeed on=hit per=0.066 max=5 window=20")), "on=hit is not Mana Rush");
            Assert.IsNull(SummonItemDescription.ManaRushText(Pet("ramp axis=attackspeed on=hit per=0.04 max=5 window=8")), "Frenzy is not Mana Rush");
            Assert.IsNull(SummonItemDescription.ManaRushText(PetWeenie(200, 600)), "no effects, no line");

            var device = MakeDevice(true, null, null);
            var oldWeenie = SummonItemDescription.PetWeenieLookup;
            try
            {
                SummonItemDescription.PetWeenieLookup = wcid => wcid == 5000 ? Pet("ramp axis=castspeed on=cast per=0.066 max=5 window=20") : null;

                var lines = SummonItemDescription.Compose(device).Replace("\r\n", "\n").Split('\n');

                CollectionAssert.Contains(lines, expected);
                Assert.AreEqual(Array.IndexOf(lines, "Spell 2: unbound slot") + 1, Array.IndexOf(lines, expected), "the line sits right after the spell slots");
            }
            finally
            {
                SummonItemDescription.PetWeenieLookup = oldWeenie;
            }
        }

        [TestMethod]
        public void Taunt_IsDescribedFromTheAuthoredInterval_AndAbsentWithoutOne()
        {
            Weenie Tank(double interval)
            {
                var w = PetWeenie(225, 0);
                w.PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.SummonTauntInterval, interval } };
                return w;
            }

            const string expected = "Taunt: every 10 s, draws up to 3 enemies within 15 m that are already fighting you or your summons onto itself";

            Assert.AreEqual(expected, SummonItemDescription.TauntText(Tank(10.0)));
            Assert.AreEqual("Taunt: every 7.5 s, draws up to 3 enemies within 15 m that are already fighting you or your summons onto itself",
                SummonItemDescription.TauntText(Tank(7.5)), "the text follows the authored interval");
            Assert.IsNull(SummonItemDescription.TauntText(Tank(0.0)));

            var device = MakeDevice(false, null, null);
            var oldWeenie = SummonItemDescription.PetWeenieLookup;
            try
            {
                SummonItemDescription.PetWeenieLookup = wcid => wcid == 5000 ? Tank(10.0) : null;

                var lines = SummonItemDescription.Compose(device).Replace("\r\n", "\n").Split('\n');

                CollectionAssert.Contains(lines, expected);
                Assert.AreEqual(Array.IndexOf(lines, "Level 225") + 1, Array.IndexOf(lines, expected), "the line sits right after the level");
            }
            finally
            {
                SummonItemDescription.PetWeenieLookup = oldWeenie;
            }
        }

        [TestMethod]
        public void Taunt_APetWithoutTheIntervalGetsNoTauntLine()
        {
            Assert.IsNull(SummonItemDescription.TauntText(PetWeenie(225, 0)));
            Assert.IsNull(SummonItemDescription.TauntText(null));

            StringAssert.DoesNotMatch(Compose(MakeDevice(true, null, null)), new System.Text.RegularExpressions.Regex("Taunt"));
            StringAssert.DoesNotMatch(Compose(MakeDevice(false, null, null)), new System.Text.RegularExpressions.Regex("Taunt"));
        }

        [TestMethod]
        public void BlankCaster_ShowsUnboundSlots()
        {
            var text = Compose(MakeDevice(true, null, null));

            StringAssert.Contains(text, "Spell 1: unbound slot");
            StringAssert.Contains(text, "Spell 2: unbound slot");
        }

        [TestMethod]
        public void HalfBound_ShowsNameAndOneUnboundSlot()
        {
            var text = Compose(MakeDevice(true, 111, null));

            StringAssert.Contains(text, "Spell 1: Fire Bolt");
            StringAssert.Contains(text, "Spell 2: unbound slot");
        }

        [TestMethod]
        public void FullyBound_ShowsBothSpellsAndElement()
        {
            var text = Compose(MakeDevice(false, 111, 222, UiEffects.Fire));

            StringAssert.Contains(text, "Spell 1: Fire Bolt");
            StringAssert.Contains(text, "Spell 2: Heal Self");
            StringAssert.Contains(text, "Element: Fire");
            Assert.IsFalse(text.Contains("unbound"));
        }

        [TestMethod]
        public void NoElement_OmitsTheElementLine()
        {
            Assert.IsFalse(Compose(MakeDevice(false, 222, 222)).Contains("Element:"));
        }

        [TestMethod]
        public void ShowsAllSixRatings_ZerosIncluded()
        {
            var text = Compose(MakeDevice(false, 111, 222));

            StringAssert.Contains(text, "Damage 12");
            StringAssert.Contains(text, "Damage Resist 0");
            StringAssert.Contains(text, "Crit 9");
            StringAssert.Contains(text, "Crit Resist 0");
            StringAssert.Contains(text, "Crit Damage 0");
            StringAssert.Contains(text, "Crit Damage Resist 0");
        }

        [TestMethod]
        public void NonCaster_HasNoMagicSkillOrSpellLines()
        {
            var text = Compose(MakeDevice(false, null, null));

            Assert.IsFalse(text.Contains("Magic skill"));
            Assert.IsFalse(text.Contains("Spell 1"));
        }

        [TestMethod]
        public void MissingPetWeenie_StillComposes()
        {
            var old = SummonItemDescription.PetWeenieLookup;
            try
            {
                SummonItemDescription.PetWeenieLookup = _ => null;

                var text = SummonItemDescription.Compose(MakeDevice(true, null, null));

                StringAssert.Contains(text, "Spell 1: unbound slot");
                Assert.IsFalse(text.Contains("Level "), "no pet weenie, no level to show");
                Assert.IsFalse(text.Contains("Magic skill"));
            }
            finally
            {
                SummonItemDescription.PetWeenieLookup = old;
            }
        }

        [TestMethod]
        public void Description_IsAscii()
        {
            foreach (var c in Compose(MakeDevice(true, 111, null)))
                Assert.IsTrue(c < 128, $"non-ASCII char U+{(int)c:X4}");
        }

        // ---- Duo Gem (Task 7 owns the gem; this is the hook contract, exercised by a fake) -------------

        [TestMethod]
        public void DuoGem_ShowsBothSlotsFromTheHook()
        {
            var gem = new PetDevice(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.PetDevice,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.DuoSummonGem, true } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Duo Gem" } },
            }, NextGuid());

            var oldSlots = SummonItemDescription.DuoSlotsHook;
            var oldWeenie = SummonItemDescription.PetWeenieLookup;
            var oldName = SummonItemDescription.SpellNameLookup;
            try
            {
                SummonItemDescription.PetWeenieLookup = wcid => PetWeenie(225, 0);
                SummonItemDescription.SpellNameLookup = id => "Fire Bolt";
                SummonItemDescription.DuoSlotsHook = _ => new[]
                {
                    new SummonSlotInfo("Archer", new PetSummonSource(5000, 18, 0, 14, 0, 20, 0, null, null)),
                    new SummonSlotInfo("Lich", new PetSummonSource(5001, 3, 0, 0, 0, 0, 0, 111u, null)),
                };

                var text = SummonItemDescription.Compose(gem);

                StringAssert.Contains(text, "Summon 1: Archer");
                StringAssert.Contains(text, "Summon 2: Lich");
                StringAssert.Contains(text, "Damage 18");
                StringAssert.Contains(text, "Damage 3");
                StringAssert.Contains(text, "Spell 1: Fire Bolt");
            }
            finally
            {
                SummonItemDescription.DuoSlotsHook = oldSlots;
                SummonItemDescription.PetWeenieLookup = oldWeenie;
                SummonItemDescription.SpellNameLookup = oldName;
            }
        }

        private static int Summon2xLines(string[] lines) => Array.FindAll(lines, l => l.Contains("Without Summon 2x")).Length;

        private static PetDevice BareDuoGem() => new PetDevice(new Weenie
        {
            WeenieClassId = nextWcid++,
            WeenieType = WeenieType.PetDevice,
            PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.DuoSummonGem, true } },
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Duo Gem" } },
        }, NextGuid());

        [TestMethod]
        public void DuoGem_Empty_SaysOnlyTheFirstAbsorbedSummonIsCalledWithoutSummon2x()
        {
            var oldSlots = SummonItemDescription.DuoSlotsHook;
            try
            {
                SummonItemDescription.DuoSlotsHook = _ => Array.Empty<SummonSlotInfo>();

                var lines = SummonItemDescription.Compose(BareDuoGem()).Replace("\r", "").Split('\n');

                Assert.AreEqual(2, lines.Length, "an empty gem is the absorb line plus the Summon 2x line");
                Assert.AreEqual("Absorbs two summoning items into one.", lines[0]);
                Assert.AreEqual("Without Summon 2x, only the first absorbed summon is called.", lines[1]);
                Assert.AreEqual(1, Summon2xLines(lines), "exactly one Summon 2x line");
            }
            finally
            {
                SummonItemDescription.DuoSlotsHook = oldSlots;
            }
        }

        [TestMethod]
        public void DuoGem_OneSlotFilled_ShowsNoSummon2xLine_BecauseItCannotSummon()
        {
            var oldSlots = SummonItemDescription.DuoSlotsHook;
            var oldWeenie = SummonItemDescription.PetWeenieLookup;
            var oldName = SummonItemDescription.SpellNameLookup;
            try
            {
                SummonItemDescription.PetWeenieLookup = wcid => PetWeenie(225, 0);
                SummonItemDescription.SpellNameLookup = id => "Fire Bolt";
                SummonItemDescription.DuoSlotsHook = _ => new[]
                {
                    new SummonSlotInfo("Archer", new PetSummonSource(5000, 18, 0, 14, 0, 20, 0, null, null)),
                };

                var lines = SummonItemDescription.Compose(BareDuoGem()).Replace("\r", "").Split('\n');

                StringAssert.StartsWith(lines[0], "Summon 1:");
                Assert.AreEqual(0, Summon2xLines(lines), "a one-slot gem cannot summon, so it carries no Summon 2x line");
            }
            finally
            {
                SummonItemDescription.DuoSlotsHook = oldSlots;
                SummonItemDescription.PetWeenieLookup = oldWeenie;
                SummonItemDescription.SpellNameLookup = oldName;
            }
        }

        [TestMethod]
        public void DuoGem_Bound_EndsWithTheSummon2xLine_AfterTheElementLine()
        {
            var oldSlots = SummonItemDescription.DuoSlotsHook;
            var oldWeenie = SummonItemDescription.PetWeenieLookup;
            var oldName = SummonItemDescription.SpellNameLookup;
            try
            {
                SummonItemDescription.PetWeenieLookup = wcid => PetWeenie(225, 0);
                SummonItemDescription.SpellNameLookup = id => "Fire Bolt";
                SummonItemDescription.DuoSlotsHook = _ => new[]
                {
                    new SummonSlotInfo("Archer", new PetSummonSource(5000, 18, 0, 14, 0, 20, 0, null, null)),
                    new SummonSlotInfo("Lich", new PetSummonSource(5001, 3, 0, 0, 0, 0, 0, 111u, null)),
                };

                var gem = BareDuoGem();
                gem.SetProperty(PropertyInt.UiEffects, (int)UiEffects.Fire);

                var lines = SummonItemDescription.Compose(gem).Replace("\r", "").Split('\n');

                StringAssert.StartsWith(lines[0], "Summon 1:");
                Assert.AreEqual("Without Summon 2x, only Summon 1 is called.", lines[lines.Length - 1]);
                Assert.AreEqual(1, Summon2xLines(lines), "exactly one Summon 2x line");
                StringAssert.StartsWith(lines[lines.Length - 2], "Element:", "the Summon 2x line comes after the element line");
                Assert.IsFalse(Array.Exists(lines, l => l.StartsWith("Absorbs", StringComparison.Ordinal)), "a bound gem has no absorb line");
            }
            finally
            {
                SummonItemDescription.DuoSlotsHook = oldSlots;
                SummonItemDescription.PetWeenieLookup = oldWeenie;
                SummonItemDescription.SpellNameLookup = oldName;
            }
        }

        // ---- dagger ---------------------------------------------------------------------------------------

        private static Gem Dagger(bool imprinted)
        {
            var bools = new Dictionary<PropertyBool, bool> { { PropertyBool.SacrificialDagger, true } };
            if (imprinted) bools[PropertyBool.SummonImprinted] = true;

            return new Gem(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesBool = bools,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 20 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Sacrificial Dagger" } },
            }, NextGuid());
        }

        [TestMethod]
        public void IsCustomSummon_OnlyForCustomItems()
        {
            Assert.IsFalse(SummonItemDescription.IsCustomSummon(MakeDevice(false, null, null)), "a retail essence keeps its LongDesc");
            Assert.IsTrue(SummonItemDescription.IsCustomSummon(MakeDevice(true, null, null)), "blank caster (binder mark)");
            Assert.IsTrue(SummonItemDescription.IsCustomSummon(MakeDevice(false, 111, null)), "bound spell");
            Assert.IsTrue(SummonItemDescription.IsCustomSummon(Dagger(false)), "the dagger");

            var duo = new PetDevice(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.PetDevice,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.DuoSummonGem, true } },
            }, NextGuid());
            Assert.IsTrue(SummonItemDescription.IsCustomSummon(duo), "Duo Gem");

            foreach (var wcid in new uint[] { 1007700, 1007799 })
            {
                var inBlock = new PetDevice(new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.PetDevice }, NextGuid());
                Assert.IsTrue(SummonItemDescription.IsCustomSummon(inBlock), $"wcid {wcid}");
            }

            foreach (var wcid in new uint[] { 1007699, 1007800 })
            {
                var outside = new PetDevice(new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.PetDevice }, NextGuid());
                Assert.IsFalse(SummonItemDescription.IsCustomSummon(outside), $"wcid {wcid}");
            }
        }

        [TestMethod]
        public void Dagger_DescribesItsImprintOrItsEmptiness()
        {
            StringAssert.Contains(SummonItemDescription.Compose(Dagger(false)), "not imprinted");

            var imprinted = SummonItemDescription.Compose(Dagger(true));
            StringAssert.Contains(imprinted, "Imprinted");
            StringAssert.Contains(imprinted, "Damage 20");
        }
    }
}
