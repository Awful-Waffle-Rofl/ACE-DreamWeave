using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.ThreadDungeons;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.CharacterDetails.CharacterDetailsSourcePins;

namespace ACE.Server.Tests.CharacterDetails
{
    /// <summary>
    /// Character Details round 2: the front and back physical tables (the facing passed explicitly through
    /// MitigationMath.ShieldMod, never by turning the player) and the spell damage table (each spell kind's own
    /// mitigation chain, composed through the same helpers the combat paths call, pinned to those paths).
    /// </summary>
    [TestClass]
    public class CharacterDetailsFacingAndSpellTests
    {
        private static uint nextGuid = 0x7D400000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static readonly DamageType[] Physical =
        {
            DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire, DamageType.Cold, DamageType.Acid, DamageType.Electric, DamageType.Nether,
        };

        private static DefenderProfile Profile(float shieldLevel, double rearGuard, Func<DamageType, BodyPart, float> armorLevel = null, float drrMelee = 0.8f, float drrMissile = 0.8f)
        {
            var armor = new Dictionary<ArmorKey, ArmorRow>();
            var resistance = new Dictionary<DamageType, ResistancePair>();
            var shield = new Dictionary<ShieldKey, ShieldRow>();

            foreach (var type in Physical)
            {
                foreach (BodyPart part in Enum.GetValues(typeof(BodyPart)))
                    foreach (var ima in new[] { false, true })
                        foreach (var imr in new[] { false, true })
                            armor[new ArmorKey(part, type, ima, imr)] = new ArmorRow(armorLevel != null ? armorLevel(type, part) : part == BodyPart.Head ? 200f / 3 : 400f);

                resistance[type] = new ResistancePair(type == DamageType.Nether ? 0.5f : 0.8f, 1.0f);
                shield[new ShieldKey(type, false)] = new ShieldRow(shieldLevel);
                shield[new ShieldKey(type, true)] = new ShieldRow(shieldLevel);
            }

            return new DefenderProfile
            {
                Armor = armor,
                Resistance = resistance,
                Shield = shield,
                RearGuardArmorLevel = rearGuard,
                DamageResistRatingBase = new Dictionary<CombatType, float> { [CombatType.Melee] = drrMelee, [CombatType.Missile] = drrMissile, [CombatType.Magic] = 0.9f },
                BattleHardenedMultiplier = 0.95f,
            };
        }

        private static CharacterDetailsInputs Inputs(DefenderProfile profile) => new CharacterDetailsInputs
        {
            CharacterGuid = 0x50000A02,
            Level = 300,
            Stance = CombatMode.Melee,
            WeaponAttackSkill = 500,
            MagicSkills = new Dictionary<Skill, uint>(),
            Profile = profile,
            WeaponSkill = Skill.HeavyWeapons,
            HeldCritPreResist = 0.15f,
            HeldCritDamageMod = 2.0f,
            CritRating = 5,
            DamageRating = 30,
            CritDamageRating = 15,
            BurdenMod = 1.0f,
            SpellResistanceMod = new Dictionary<DamageType, float>
            {
                [DamageType.Slash] = 0.9f, [DamageType.Pierce] = 0.85f, [DamageType.Bludgeon] = 0.8f, [DamageType.Fire] = 0.5f,
                [DamageType.Cold] = 0.6f, [DamageType.Acid] = 0.7f, [DamageType.Electric] = 0.75f, [DamageType.Nether] = 0.4f,
                [DamageType.Health] = 0.65f,
            },
            SpellAbsorbFront = 0.7f,
            SpellAbsorbBack = 1.0f,
            HarmResistanceMod = 0.65f,
            DrainResistanceMod = 0.55f,
            DotNetherResistanceMod = 0.45f,
            DotUntypedResistanceMod = 1.0f,
            DotResistRating = 20,
            Fortitude = 0.1,
        };

        private static CharacterDetailsSection Section(ACE.Server.Managers.Market.CharacterDetails details, string key)
            => details.Sections.Single(s => s.Key == key);

        /// <summary>A table of the one damage_taken section: "physical" or "magic".</summary>
        private static CharacterDetailsTable Table(ACE.Server.Managers.Market.CharacterDetails details, string key)
            => Section(details, "damage_taken").Tables.Single(t => t.Key == key);

        /// <summary>Every top-level row of every section and every table, children excluded.</summary>
        private static IEnumerable<CharacterDetailsRow> AllRows(ACE.Server.Managers.Market.CharacterDetails details)
            => details.Sections.SelectMany(s => (s.Rows ?? new List<CharacterDetailsRow>()).Concat((s.Tables ?? new List<CharacterDetailsTable>()).SelectMany(t => t.Rows)));

        // ---- the single armor table: Front and Back rows, collapsed to the worst type, children per type ----

        private static readonly string[] TypeKeys = { "slash", "pierce", "bludgeon", "fire", "cold", "acid", "lightning", "nether" };
        private static readonly string[] PartLabels = { "Head", "Chest", "Abdomen", "U Arm", "L Arm", "Hands", "U Leg", "L Leg", "Feet" };

        /// <summary>Per-type armor that differs by type and part, so every child differs and the worst type moves around.</summary>
        private static DefenderProfile VariedProfile(float shieldLevel = 100f, double rearGuard = 200.0)
            => Profile(shieldLevel, rearGuard, (type, part) => new[] { 150f, 120f, 200f, 170f, 60f, 220f, 180f, 250f }[Array.IndexOf(Physical, type)] + 9f * (int)part);

        [TestMethod]
        public void Armor_IsOneTable_TwoRows_NineCells_EightChildrenEach()
        {
            var details = CharacterDetailsProjector.Build(Inputs(VariedProfile()), null);

            Assert.IsFalse(details.Sections.Any(s => s.Key == "armor_front" || s.Key == "armor_back"), "the two tables are gone");

            var armor = Table(details, "physical");
            Assert.AreEqual("Physical", armor.Corner);
            CollectionAssert.AreEqual(PartLabels, armor.Columns);
            CollectionAssert.AreEqual(new[] { "armor_front", "armor_back" }, armor.Rows.Select(r => r.Key).ToArray());
            CollectionAssert.AreEqual(new[] { "Front", "Back" }, armor.Rows.Select(r => r.Label).ToArray());

            foreach (var row in armor.Rows)
            {
                Assert.AreEqual(9, row.Cells.Count, row.Key);
                Assert.AreEqual(8, row.Children.Count, row.Key);
                CollectionAssert.AreEqual(new[] { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Acid", "Lightning", "Nether" }, row.Children.Select(c => c.Label).ToArray(), row.Key);
                CollectionAssert.AreEqual(TypeKeys.Select(t => row.Key + "_" + t).ToArray(), row.Children.Select(c => c.Key).ToArray());

                foreach (var child in row.Children)
                {
                    Assert.AreEqual(9, child.Cells.Count, child.Key);
                    Assert.IsNull(child.Children, "no nesting: " + child.Key);
                    Assert.IsNull(child.Value);
                }
            }

            Assert.IsFalse(AllRows(details).SelectMany(r => new[] { r }.Concat(r.Children ?? new List<CharacterDetailsRow>())).Any(r => r.Key.Contains("protection_only")), "protections only is gone");
        }

        [TestMethod]
        public void Armor_CollapsedCell_IsTheWorstChild_PerBodyPart_AndTheTypesReallyDiffer()
        {
            var details = CharacterDetailsProjector.Build(Inputs(VariedProfile()), null);
            var armor = Table(details, "physical");

            foreach (var row in armor.Rows)
            {
                var parts = new[] { BodyPart.Head, BodyPart.Chest, BodyPart.Abdomen, BodyPart.UpperArm, BodyPart.LowerArm, BodyPart.Hand, BodyPart.UpperLeg, BodyPart.LowerLeg, BodyPart.Foot };

                for (var col = 0; col < 9; col++)
                {
                    // parse the children's cells back: the collapsed cell must equal the largest of them, Nether excluded
                    var values = row.Children.Where(c => c.Label != "Nether").Select(c => double.Parse(c.Cells[col].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture)).ToList();
                    Assert.AreEqual(CharacterDetailsProjector.Pct(values.Max() / 100.0), row.Cells[col], $"{row.Key} {PartLabels[col]}");

                    // and the engine-side maximum, straight from PhysicalTaken
                    var expected = Physical.Where(t => t != DamageType.Nether).Max(t => CharacterDetailsProjector.PhysicalTakenWithLayers(VariedProfile(), parts[col], t, row.Key == "armor_front", 0.1));
                    Assert.AreEqual(CharacterDetailsProjector.Pct(expected), row.Cells[col], $"{row.Key} {PartLabels[col]} vs PhysicalTakenWithLayers");
                }

                // positive control: the children differ across types, and the worst type is not always the first
                Assert.IsTrue(row.Children.Select(c => c.Cells[1]).Distinct().Count() > 1, row.Key + ": children differ");
                Assert.AreNotEqual(row.Children[0].Cells[0], row.Cells[0], row.Key + ": the worst type (Cold, the thinnest armor) is not the first child");
            }
        }

        [TestMethod]
        public void Armor_CollapsedCell_IgnoresNether_EvenWhenNetherIsTheWorstChild()
        {
            // Nether armor far thinner than every other type, so its child is the largest on every part and facing
            var profile = Profile(100f, 200.0, (type, part) => type == DamageType.Nether ? 1f : 200f + 9f * (int)part);
            var armor = Table(CharacterDetailsProjector.Build(Inputs(profile), null), "physical");

            foreach (var row in armor.Rows)
            {
                var nether = row.Children.Single(c => c.Label == "Nether");

                for (var col = 0; col < 9; col++)
                {
                    var all = row.Children.Select(c => double.Parse(c.Cells[col].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture)).ToList();
                    var others = row.Children.Where(c => c.Label != "Nether").Select(c => double.Parse(c.Cells[col].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture)).ToList();
                    var netherValue = double.Parse(nether.Cells[col].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture);

                    // positive control: Nether really is the worst child here, so a summary that still read it would show its value
                    Assert.AreEqual(all.Max(), netherValue, $"{row.Key} {PartLabels[col]}: Nether is the worst child");
                    Assert.IsTrue(netherValue > others.Max(), $"{row.Key} {PartLabels[col]}: Nether is strictly worse than the rest");

                    Assert.AreEqual(CharacterDetailsProjector.Pct(others.Max() / 100.0), row.Cells[col], $"{row.Key} {PartLabels[col]}: the summary skips Nether");
                    Assert.AreNotEqual(nether.Cells[col], row.Cells[col], $"{row.Key} {PartLabels[col]}");
                }
            }
        }

        [TestMethod]
        public void Armor_RowKeys_AreUniqueAcrossTheWholeResponse_IncludingChildren()
        {
            foreach (var profile in new[] { VariedProfile(), Profile(100f, 200.0) })
            {
                var details = CharacterDetailsProjector.Build(Inputs(profile), null);

                var keys = AllRows(details).SelectMany(r => new[] { r.Key }.Concat((r.Children ?? new List<CharacterDetailsRow>()).Select(c => c.Key))).ToList();
                Assert.IsTrue(keys.Count > 15, "positive control: children are in the key set");
                Assert.AreEqual(keys.Count, keys.Distinct().Count(), "row keys are unique, children included");
            }
        }

        /// <summary>Damage resist rating (melee base 0.8), Battle Hardened (0.95) and Fortitude 0.1 (x0.9), as the fixtures build them.</summary>
        private const float Layers = 0.8f * 0.95f * 0.9f;

        [TestMethod]
        public void Armor_TooltipsListTheFacingFactors_WithValues()
        {
            var armor = Table(CharacterDetailsProjector.Build(Inputs(Profile(100f, 200.0)), null), "physical");
            var shield = CharacterDetailsProjector.Pct(SkillFormula.CalcArmorMod(100f)).Replace("%", "");
            var shieldMult = SkillFormula.CalcArmorMod(100f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var rearMult = CloakMantleMath.RearGuardMod(200.0, 1.0f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

            Assert.AreEqual("Each body part shows its worst damage type other than Nether; expand for all eight. Includes your shield or Defensive Weapon (x" + shieldMult
                + ", front only), damage resist rating with its specialized-defense bonus (x0.80), Battle Hardened (x0.95) and Jewelry Aspect Fortitude (x0.90), against a monster's melee hit.",
                armor.Rows[0].Tooltip);
            Assert.AreEqual("Each body part shows its worst damage type other than Nether; expand for all eight. Includes a Mantle cloak's rear guard (x" + rearMult
                + "; a shield does not cover the back), damage resist rating with its specialized-defense bonus (x0.80), Battle Hardened (x0.95) and Jewelry Aspect Fortitude (x0.90), against a monster's melee hit.",
                armor.Rows[1].Tooltip);

            foreach (var row in armor.Rows)
                Assert.IsFalse(row.Tooltip.Contains("Defense rows"), "the layers are in the cells now");

            Assert.IsFalse(string.IsNullOrEmpty(shield));
        }

        [TestMethod]
        public void Armor_MissileSentence_OnlyWhenTheMissileLayersDiffer()
        {
            var profile = Profile(100f, 200.0);
            Assert.IsFalse(Table(CharacterDetailsProjector.Build(Inputs(profile), null), "physical").Rows[0].Tooltip.Contains("missile"), "equal bases: no sentence");

            var tip = Table(CharacterDetailsProjector.Build(Inputs(Profile(100f, 200.0, drrMissile: 0.6f)), null), "physical").Rows[0].Tooltip;
            StringAssert.EndsWith(tip, "Against a monster's missile the damage resist rating layers are x0.57 instead of x0.76.");
        }

        [TestMethod]
        public void Armor_ChildTooltip_NamesTheTypesResistance_AndTheCellsCarryTheRatingLayers()
        {
            var profile = Profile(0f, 0.0);
            var details = CharacterDetailsProjector.Build(Inputs(profile), null);
            var front = Table(details, "physical").Rows[0];

            Assert.AreEqual("Slash resistance from protections, natural resistance and augmentations: x0.80. Armor varies per body part.", front.Children[0].Tooltip);
            Assert.AreEqual("Nether resistance from protections, natural resistance and augmentations: x0.50. Armor varies per body part.", front.Children[7].Tooltip);

            // the Slash head cell is armor x resistance x damage resist rating x Battle Hardened x Fortitude
            Assert.AreEqual(CharacterDetailsProjector.Pct(SkillFormula.CalcArmorMod(200f / 3) * 0.8f * Layers), front.Children[0].Cells[0]);

            // positive control: a better damage resist rating lowers the cell
            var better = Table(CharacterDetailsProjector.Build(Inputs(Profile(0f, 0.0, drrMelee: 0.6f)), null), "physical").Rows[0];
            Assert.AreNotEqual(front.Children[0].Cells[0], better.Children[0].Cells[0]);
            Assert.AreEqual(CharacterDetailsProjector.Pct(SkillFormula.CalcArmorMod(200f / 3) * 0.8f * 0.6f * 0.95f * 0.9f), better.Children[0].Cells[0]);
        }

        [TestMethod]
        public void Front_CarriesTheShield_Back_CarriesTheRearGuard()
        {
            var profile = Profile(100f, 200.0);
            var details = CharacterDetailsProjector.Build(Inputs(profile), null);

            var armorHead = SkillFormula.CalcArmorMod(200f / 3);
            var shield = SkillFormula.CalcArmorMod(100f);
            var rearGuard = CloakMantleMath.RearGuardMod(200.0, 1.0f);

            Assert.IsTrue(shield < 1.0f && rearGuard < 1.0f && shield != rearGuard, "positive control: the two facing layers differ and both bite");

            // the Slash child, Head column (all types share one armor level here, Slash is the 0.8 protection)
            var armor = Table(details, "physical");
            Assert.AreEqual(CharacterDetailsProjector.Pct(armorHead * shield * 0.8f * Layers), armor.Rows[0].Children[0].Cells[0]);
            Assert.AreEqual(CharacterDetailsProjector.Pct(armorHead * rearGuard * 0.8f * Layers), armor.Rows[1].Children[0].Cells[0]);

            // no Mantle: the back is armor and protections alone; no shield: the front is too
            var bare = Table(CharacterDetailsProjector.Build(Inputs(Profile(0f, 0.0)), null), "physical");
            Assert.AreEqual(CharacterDetailsProjector.Pct(armorHead * 0.8f * Layers), bare.Rows[1].Children[0].Cells[0]);
            Assert.AreEqual(CharacterDetailsProjector.Pct(armorHead * 0.8f * Layers), bare.Rows[0].Children[0].Cells[0]);
        }

        [TestMethod]
        public void ShieldMod_RearArc_IsTheRearGuard_PvEOnly_AndPhantomIgnoresIt()
        {
            var profile = Profile(100f, 200.0);

            AttackerSpec Rear(bool player = false, bool phantom = false, float ignoreShield = 1.0f)
                => new AttackerSpec { DamageType = DamageType.Slash, WithinShieldArc = false, AttackerIsPlayer = player, IgnoreAllArmor = phantom, IgnoreShieldMod = ignoreShield };

            Assert.AreEqual(CloakMantleMath.RearGuardMod(200.0, 1.0f), MitigationMath.ShieldMod(profile, Rear()), 1e-7f);
            Assert.AreEqual(CloakMantleMath.RearGuardMod(200.0, 0.6f), MitigationMath.ShieldMod(profile, Rear(ignoreShield: 0.6f)), 1e-7f, "a shield-cleaving weapon cleaves the rear guard too");
            Assert.AreEqual(1.0f, MitigationMath.ShieldMod(profile, Rear(player: true)), "PvP: no rear guard");
            Assert.AreEqual(1.0f, MitigationMath.ShieldMod(profile, Rear(phantom: true)), "phantom weapons ignore it");
            Assert.AreEqual(1.0f, MitigationMath.ShieldMod(Profile(100f, 0.0), Rear()), "no Mantle");

            // the frontal arc is unchanged: the shield row, never the rear guard
            var frontSpec = new AttackerSpec { DamageType = DamageType.Slash };
            Assert.AreEqual(SkillFormula.CalcArmorMod(100f), MitigationMath.ShieldMod(profile, frontSpec), 1e-7f);
        }

        /// <summary>The engine's rear branch and the profile's rear-guard read share one method; pinned so neither drifts.</summary>
        [TestMethod]
        public void Pin_RearGuard_EngineAndProfileShareOneRead()
        {
            const string cloak = "ACE.Server/WorldObjects/Player_CloakMantle.cs";
            AssertLineOnce(cloak, "public float GetCloakRearGuardMod(WorldObject attacker, WorldObject weapon)", "var armorLevel = GetCloakRearGuardArmorLevel(PvpClassifier.IsPvp(attacker, this));");
            AssertLineOnce(cloak, "public float GetCloakRearGuardMod(WorldObject attacker, WorldObject weapon)", "return CloakMantleMath.RearGuardMod(armorLevel, attacker.GetIgnoreShieldMod(weapon));");

            AssertLineOnce("ACE.Server/CombatSimulator/ProfileBuilder.cs", "private static DefenderProfile Walk(Player player, CombatMode? stance = null)",
                "RearGuardArmorLevel = player.GetCloakRearGuardArmorLevel(pvp: false),");

            AssertLineOnce("ACE.Server/WorldObjects/Creature_Combat.cs", "public float GetShieldMod(WorldObject attacker, DamageType damageType, WorldObject weapon, WorldObject angleSource = null)",
                "return rearPlayer.GetCloakRearGuardMod(attacker, weapon);");

            AssertLineOnce("ACE.Server/CombatSimulator/MitigationMath.cs", "public static float ShieldMod(DefenderProfile profile, AttackerSpec spec)",
                "return RearGuardMod(profile, spec);");
        }

        // ---- spell damage table ----

        [TestMethod]
        public void SpellTable_IsFrontAndBackRows_ElevenCells_AndComposesEachKindsOwnChain()
        {
            var profile = Profile(0f, 0.0);
            var input = Inputs(profile);
            var section = Table(CharacterDetailsProjector.Build(input, null), "magic");

            Assert.AreEqual("Magic", section.Corner);
            CollectionAssert.AreEqual(new[] { "Slash", "Pierce", "Bludg", "Fire", "Cold", "Acid", "Light", "Nether", "Harm", "Drain", "DoT" }, section.Columns);
            CollectionAssert.AreEqual(new[] { "spell_taken_front", "spell_taken_back" }, section.Rows.Select(r => r.Key).ToArray());
            CollectionAssert.AreEqual(new[] { "Front", "Back" }, section.Rows.Select(r => r.Label).ToArray());

            var front = section.Rows[0];
            var back = section.Rows[1];
            foreach (var row in section.Rows)
            {
                Assert.AreEqual(11, row.Cells.Count, row.Key);
                Assert.IsNull(row.Children);
            }

            var drr = 0.9f * 0.95f;
            var fort = 0.9f;

            // bolts: resistance x the facing's absorb x magic DRR with Battle Hardened x Fortitude
            Assert.AreEqual(CharacterDetailsProjector.Pct(0.5f * 0.7f * drr * fort), front.Cells[3], "Fire, front");
            Assert.AreEqual(CharacterDetailsProjector.Pct(0.4f * 0.7f * drr * fort), front.Cells[7], "Nether, front");
            Assert.AreEqual(CharacterDetailsProjector.Pct(0.5f * 1.0f * drr * fort), back.Cells[3], "Fire, back");
            for (var i = 0; i < 8; i++)
                Assert.AreNotEqual(front.Cells[i], back.Cells[i], $"positive control: the shield absorbs in front only, column {i}");

            // Harm and Drain stay SEPARATE columns from their own resistances; no absorb and no DRR; the same in both rows
            Assert.AreEqual(CharacterDetailsProjector.Pct(0.65f * fort), front.Cells[8], "Harm");
            Assert.AreEqual(CharacterDetailsProjector.Pct(0.55f * fort), front.Cells[9], "Drain Health");
            Assert.AreNotEqual(front.Cells[8], front.Cells[9], "positive control: the two differ");

            // DoT: the Nether tick; the DoT resist rating applies
            var dotRating = Creature.GetNegativeRatingMod(20);
            Assert.IsTrue(dotRating < 1.0f, "positive control: the DoT resist rating bites");
            Assert.AreEqual(CharacterDetailsProjector.Pct(0.45f * drr * dotRating * fort), front.Cells[10]);

            CollectionAssert.AreEqual(front.Cells.Skip(8).ToArray(), back.Cells.Skip(8).ToArray(), "Harm, Drain Health and DoT take no absorb");

            Assert.AreEqual("A non-critical hit. Harm and Drain Health take no magic absorb or damage resist rating; Drain Health is of the amount drained, before the spell's cap. DoT is per Nether tick; other damage over time ticks at "
                + CharacterDetailsProjector.Pct(1.0f * drr * dotRating * fort) + ". Mana Barrier, wards and cloak procs are not counted.", front.Tooltip);
            Assert.AreEqual("A shield's magic absorb covers the front only.", back.Tooltip);
            Assert.IsFalse(front.Tooltip.Contains("These are from the front"), "the conditional sentence is gone");
        }

        [TestMethod]
        public void DamageTaken_IsOneTablesSection_WithPhysicalAndMagicTables()
        {
            var details = CharacterDetailsProjector.Build(Inputs(VariedProfile()), null);

            Assert.IsFalse(details.Sections.Any(s => s.Key == "armor" || s.Key == "magic_damage"), "the two old sections are gone");

            var section = Section(details, "damage_taken");
            Assert.AreEqual("Damage taken", section.Title);
            Assert.AreEqual("tables", section.Kind);
            Assert.AreEqual(0, section.Rows.Count, "no top-level rows");
            Assert.IsNull(section.Columns, "no top-level columns");
            Assert.IsNull(section.Corner);
            CollectionAssert.AreEqual(new[] { "physical", "magic" }, section.Tables.Select(t => t.Key).ToArray());
            CollectionAssert.AreEqual(new[] { "Physical", "Magic" }, section.Tables.Select(t => t.Corner).ToArray());
            Assert.AreEqual(9, section.Tables[0].Columns.Count);
            Assert.AreEqual(11, section.Tables[1].Columns.Count);
        }

        [TestMethod]
        public void MagicTable_WithAShield_HasFrontAndBackRows_WithoutOne_HasOneRow()
        {
            var shield = Inputs(Profile(0f, 0.0));
            var magic = Table(CharacterDetailsProjector.Build(shield, null), "magic");
            CollectionAssert.AreEqual(new[] { "spell_taken_front", "spell_taken_back" }, magic.Rows.Select(r => r.Key).ToArray());
            Assert.AreNotEqual(magic.Rows[0].Cells[3], magic.Rows[1].Cells[3], "the shield changes the element cells");
            Assert.AreEqual("A shield's magic absorb covers the front only.", magic.Rows[1].Tooltip);

            var bare = Inputs(Profile(0f, 0.0));
            bare.SpellAbsorbFront = 1.0f;
            bare.SpellAbsorbBack = 1.0f;
            var single = Table(CharacterDetailsProjector.Build(bare, null), "magic");
            Assert.AreEqual(1, single.Rows.Count);
            Assert.AreEqual("spell_taken", single.Rows[0].Key);
            Assert.AreEqual("Front & back", single.Rows[0].Label);
            Assert.AreEqual(11, single.Rows[0].Cells.Count);
            CollectionAssert.AreEqual(magic.Rows[1].Cells.Skip(8).ToArray(), single.Rows[0].Cells.Skip(8).ToArray(), "Harm, Drain Health and DoT are facing-independent");
            Assert.AreEqual(magic.Rows[0].Tooltip, single.Rows[0].Tooltip, "the caveats tooltip");

            // keys stay unique in both branches
            foreach (var input in new[] { shield, bare })
            {
                var keys = AllRows(CharacterDetailsProjector.Build(input, null)).SelectMany(r => new[] { r.Key }.Concat((r.Children ?? new List<CharacterDetailsRow>()).Select(c => c.Key))).ToList();
                Assert.AreEqual(keys.Count, keys.Distinct().Count());
            }
        }

        [TestMethod]
        public void Serialization_OmitsTablesAndCorner_OnOtherSections_AndEmitsEmptyRowsOnTheTablesSection()
        {
            var details = CharacterDetailsProjector.Build(Inputs(VariedProfile()), null);
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(details, MarketApiHost.Json));

            foreach (var section in doc.RootElement.GetProperty("sections").EnumerateArray())
            {
                var key = section.GetProperty("key").GetString();

                if (key == "damage_taken")
                {
                    Assert.IsTrue(section.TryGetProperty("tables", out var tables));
                    Assert.AreEqual(2, tables.GetArrayLength());
                    Assert.AreEqual(0, section.GetProperty("rows").GetArrayLength(), "rows is present and empty");
                    Assert.IsFalse(section.TryGetProperty("columns", out _), "columns omitted");
                    Assert.IsFalse(section.TryGetProperty("corner", out _));
                    Assert.IsTrue(section.GetProperty("collapsed").GetBoolean(), "damage_taken is drawn collapsed");
                    Assert.AreEqual("Physical", tables[0].GetProperty("corner").GetString());
                    Assert.AreEqual("magic", tables[1].GetProperty("key").GetString());
                }
                else
                {
                    Assert.IsFalse(section.TryGetProperty("tables", out _), key + " carries no tables");
                    Assert.IsFalse(section.TryGetProperty("corner", out _), key + " carries no corner");
                    Assert.IsFalse(section.TryGetProperty("collapsed", out _), key + " omits collapsed");
                    Assert.IsTrue(section.TryGetProperty("rows", out _), key);
                }
            }
        }

        [TestMethod]
        public void NullProfile_DropsTheDamageTakenSectionEntirely()
        {
            var input = Inputs(Profile(0f, 0.0));
            input.Profile = null;
            var details = CharacterDetailsProjector.Build(input, null);

            Assert.IsFalse(details.Sections.Any(s => s.Key == "damage_taken"));
            CollectionAssert.AreEqual(new[] { "offense" }, details.Sections.Select(s => s.Key).ToArray(), "no baseline drops Defense too, and no profile drops Damage taken");
        }

        [TestMethod]
        public void DamageTakenRows_AreGoneFromDefense_AndTheSectionsAreOffenseDefenseDamageTaken()
        {
            var details = CharacterDetailsProjector.Build(Inputs(Profile(0f, 0.0)), null);

            CollectionAssert.AreEqual(new[] { "offense", "damage_taken" }, details.Sections.Select(s => s.Key).ToArray(), "without a baseline Defense is empty and dropped; with one the order is offense, defense, damage_taken");

            var keys = AllRows(details).SelectMany(r => new[] { r.Key }.Concat((r.Children ?? new List<CharacterDetailsRow>()).Select(c => c.Key))).ToList();

            foreach (var gone in new[] { "damage_taken_melee", "damage_taken_missile", "damage_taken_magic", "crit_physical", "crit_magic", "crit_magic_slash",
                "rating_damage", "rating_crit", "natural_resist", "burden", "xp_to_next", "luminance", "points_lum_price", "points_xp_price" })
                CollectionAssert.DoesNotContain(keys, gone);
        }

        /// <summary>
        /// A REAL baseline at level 300: ThreadBaseline.For over the shipped roster, the derivation the live view uses. TestGameTables
        /// carries portal formulas for only some skills, and a skill with no formula has no effective median (so no baseline value);
        /// this adds a stand-in formula for each missing attack or defense skill and gives every roster creature all of them,
        /// restoring the table afterwards. Magic and weapon attack values are therefore stand-ins, not retail numbers.
        /// </summary>
        private static ThreadBaselineCreature SampleBaseline300()
        {
            TestGameTables.EnsureInitialized();
            var added = new List<uint>();

            try
            {
                foreach (var skill in DungeonCombatNormalizer.AttackSkills.Concat(DungeonCombatNormalizer.DefenseSkills).Distinct())
                {
                    if (!GameTables.SkillTable.SkillBaseHash.ContainsKey((uint)skill))
                    {
                        GameTables.SkillTable.SkillBaseHash[(uint)skill] = new ACE.DatLoader.Entity.SkillBase(new ACE.DatLoader.Entity.SkillFormula(PropertyAttribute.Focus, PropertyAttribute.Self, 4));
                        added.Add((uint)skill);
                    }
                }

                ThreadPlanCache.Invalidate();

                DungeonStatProfile RichProfile(uint wcid)
                {
                    var level = ShippedRosterFixture.LevelOf(wcid);
                    if (level <= 0)
                        return DungeonStatProfile.Empty;

                    var l = (uint)level;
                    var attrs = new Dictionary<PropertyAttribute, uint>
                    {
                        [PropertyAttribute.Strength] = l, [PropertyAttribute.Endurance] = l, [PropertyAttribute.Quickness] = l,
                        [PropertyAttribute.Coordination] = l, [PropertyAttribute.Focus] = l, [PropertyAttribute.Self] = l,
                    };
                    var skills = DungeonCombatNormalizer.AttackSkills.Concat(DungeonCombatNormalizer.DefenseSkills).Distinct().ToDictionary(k => k, k => 2 * l);

                    return new DungeonStatProfile(level, 0, skills, l, 2 * l, 0, 0.0, attrs, critResistRating: 7u);
                }

                var baseline = ThreadBaseline.For(300, ThreadBaselineTests.Limits(), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, RichProfile);
                Assert.IsNotNull(baseline, "the shipped roster yields a level 300 baseline");
                Assert.IsTrue(baseline.MeleeDefense > 0 && baseline.MagicDefense > 0 && baseline.MeleeAttack > 0 && baseline.MissileAttack > 0 && baseline.MagicAttack > 0 && baseline.MissileDefense > 0);
                return baseline;
            }
            finally
            {
                foreach (var key in added)
                    GameTables.SkillTable.SkillBaseHash.Remove(key);

                ThreadPlanCache.Invalidate();
            }
        }

        private static void WriteSample(string envVar, ACE.Server.Managers.Market.CharacterDetails response)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(response, MarketApiHost.Json);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var path = Environment.GetEnvironmentVariable(envVar);

            if (!string.IsNullOrEmpty(path))
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        }

        /// <summary>Sword and shield: writes CHARDETAILS_SAMPLE_PATH when set.</summary>
        [TestMethod]
        public void SampleResponse_SwordAndShield_Serializes()
        {
            var baseline = SampleBaseline300();
            var input = Inputs(VariedProfile());
            input.SpellAbsorbFront = 0.8f;
            input.SpellAbsorbBack = 1.0f;
            input.MeleeDefense = 760;
            input.MissileDefense = 700;
            input.MagicDefense = 650;
            input.WeaponAttackSkill = 780;
            input.HeldCritPreResist = 0.15f;
            input.KillerInstinctCritBonus = 0.04f;

            var response = CharacterDetailsProjector.Build(input, baseline);
            Assert.IsTrue(response.BaselineAvailable);
            foreach (var key in new[] { "hit_melee", "evade_melee", "evade_missile", "resist_spells", "crit_chance", "crit_damage" })
                Assert.IsTrue(AllRows(response).Any(r => r.Key == key), "present: " + key);
            Assert.IsFalse(AllRows(response).Any(r => r.Key.StartsWith("spell_war")), "a sword wielder has no spell rows");

            WriteSample("CHARDETAILS_SAMPLE_PATH", response);
        }

        /// <summary>Two swords with different skills and mods (off hand capped at Dual Wield): writes CHARDETAILS_SAMPLE_DUAL_PATH when set.</summary>
        [TestMethod]
        public void SampleResponse_DualWield_Serializes()
        {
            var baseline = SampleBaseline300();
            var input = Inputs(Profile(0f, 0.0, (type, part) => 110f + 11f * Array.IndexOf(Physical, type) + 6f * (int)part));
            input.MeleeDefense = 780;
            input.MissileDefense = 700;
            input.MagicDefense = 650;
            input.WeaponAttackSkill = 790;
            input.OffWeaponAttackSkill = 700;
            input.OffWeaponSkill = Skill.DualWield;
            input.SpellAbsorbFront = 1.0f;
            input.SpellAbsorbBack = 1.0f;
            input.HeldCritPreResist = 0.15f;
            input.OffCritPreResist = 0.22f;
            input.HeldCritDamageMod = 2.0f;
            input.OffCritDamageMod = 2.5f;
            input.KillerInstinctCritBonus = 0.0f;

            var response = CharacterDetailsProjector.Build(input, baseline);
            var hit = AllRows(response).Single(r => r.Key == "hit_melee");
            StringAssert.Contains(hit.Value, " / ");
            StringAssert.Contains(AllRows(response).Single(r => r.Key == "crit_chance").Value, " / ");

            WriteSample("CHARDETAILS_SAMPLE_DUAL_PATH", response);
        }

        /// <summary>A wand with War and Life trained, Void untrained: writes CHARDETAILS_SAMPLE_CASTER_PATH when set.</summary>
        [TestMethod]
        public void SampleResponse_Caster_Serializes()
        {
            var baseline = SampleBaseline300();
            var input = Inputs(Profile(0f, 0.0, (type, part) => 90f + 12f * Array.IndexOf(Physical, type) + 5f * (int)part));
            input.Stance = CombatMode.Magic;
            input.WeaponAttackSkill = 0;
            input.WeaponSkill = Skill.None;
            input.MagicSkills = new Dictionary<Skill, uint> { [Skill.WarMagic] = 820, [Skill.LifeMagic] = 760 };
            input.MeleeDefense = 700;
            input.MissileDefense = 680;
            input.MagicDefense = 760;
            input.SpellAbsorbFront = 1.0f;
            input.SpellAbsorbBack = 1.0f;
            input.HeldCritPreResist = 0.12f;
            input.HeldCritDamageMod = 1.0f;
            input.KillerInstinctCritBonus = 0.0f;

            var response = CharacterDetailsProjector.Build(input, baseline);
            var keys = AllRows(response).Select(r => r.Key).ToList();

            CollectionAssert.Contains(keys, "spell_war");
            CollectionAssert.Contains(keys, "spell_life");
            CollectionAssert.DoesNotContain(keys, "spell_void");
            CollectionAssert.DoesNotContain(keys, "hit_melee");

            WriteSample("CHARDETAILS_SAMPLE_CASTER_PATH", response);
        }

        /// <summary>Fortitude is the one every physical and spell damage site runs; pinned at those lines.</summary>
        [TestMethod]
        public void Pin_Fortitude_Composition()
        {
            const string model = "ACE.Server/Managers/Market/CharacterDetailsModel.cs";
            AssertLineOnce(model, "public static float PhysicalTakenWithLayers(DefenderProfile profile, BodyPart part, DamageType type, bool front, double fortitude)",
                "* (float)JewelryAspectMath.FortitudeMultiplier(fortitude);");
            AssertLineOnce(model, "public static float PhysicalTakenWithLayers(DefenderProfile profile, BodyPart part, DamageType type, bool front, double fortitude)",
                "* MitigationMath.DamageResistRatingMod(profile, NeutralPhysicalSpec(type, front))");

            AssertLineOnce("ACE.Server/WorldObjects/Player_Combat.cs",
                "public int TakeDamage(WorldObject source, DamageType damageType, float _amount, BodyPart bodyPart, bool crit = false, AttackConditions attackConditions = AttackConditions.None, bool sendDefenderNotification = true, bool isHazard = false, DamageAttackKind? attackKind = null)",
                "var afterMitigation = ApplyPreWriteDamageClassAbilities(source, damageType, amount, isHazard: isHazard);");

            AssertLineOnce("ACE.Server/Managers/Market/CharacterDetailsService.cs", "public static CharacterDetailsInputs Read(Player player)", "Fortitude = player.GetFortitude(null),");

            // the melee and missile damage resist rating layers come from one per-combat-type base, with Battle Hardened on top (PvE only)
            AssertLineOnce("ACE.Server/CombatSimulator/MitigationMath.cs", "public static float DamageResistRatingMod(DefenderProfile profile, AttackerSpec spec)",
                "if (profile.DamageResistRatingBase == null || !profile.DamageResistRatingBase.TryGetValue(spec.CombatType, out var mod))");
            AssertLineOnce("ACE.Server/CombatSimulator/MitigationMath.cs", "public static float DamageResistRatingMod(DefenderProfile profile, AttackerSpec spec)",
                "mod *= profile.BattleHardenedMultiplier;");
        }

        /// <summary>The crit damage paths the Crit damage row mirrors, pinned at the exact lines.</summary>
        [TestMethod]
        public void Pin_CritDamagePaths()
        {
            const string damageEvent = "ACE.Server/Entity/DamageEvent.cs";
            AssertLineOnce(damageEvent, "private float DoCalculateDamage(Creature attacker, Creature defender, WorldObject damageSource)",
                "CriticalDamageMod = 1.0f + WorldObject.GetWeaponCritDamageMod(Weapon, attacker, attackSkill, defender);");
            AssertLineOnce(damageEvent, "private float DoCalculateDamage(Creature attacker, Creature defender, WorldObject damageSource)",
                "DamageRatingMod = Creature.AdditiveCombine(DamageRatingBaseMod, CriticalDamageRatingMod, SneakAttackMod, HeritageMod);");

            const string projectile = "ACE.Server/WorldObjects/SpellProjectile.cs";
            const string calc = "public float? CalculateDamage(WorldObject source, Creature target, ref bool criticalHit, ref bool critDefended, ref bool overpower)";
            AssertLineOnce(projectile, calc, "critDamageBonus = lifeMagicDamage * 0.5f * weaponCritDamageMod;");
            AssertLineOnce(projectile, calc, "critDamageBonus *= weaponCritDamageMod;");
            AssertLineOnce(projectile, "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)",
                "damageRatingMod = Creature.AdditiveCombine(damageRatingMod, critDamageRatingMod);");

            const string service = "ACE.Server/Managers/Market/CharacterDetailsService.cs";
            const string read = "public static CharacterDetailsInputs Read(Player player)";
            AssertLineOnce(service, read, "heldCritDamageMod = AttackerTerms.MagicCritDamageMod(wand, player, magicSkill, null);");
            AssertLineOnce(service, read, "heldCritDamageMod = AttackerTerms.PhysicalCritDamageMod(weapon, player, weaponSkill, null);");
            AssertLineOnce(service, read, "offCritDamageMod = AttackerTerms.PhysicalCritDamageMod(offWeapon, player, offSkill, null);");
        }

        [TestMethod]
        public void FortitudeMultiplier_MatchesApplyFortitude_UpToItsRounding()
        {
            foreach (var f in new[] { 0.0, 0.05, 0.1, 0.25, 0.6, 1.0, 1.5, double.NaN, -0.2 })
            {
                var applied = JewelryAspectMath.ApplyFortitude(100000u, 100000u, f);
                Assert.AreEqual(applied, (uint)Math.Round(100000 * JewelryAspectMath.FortitudeMultiplier(f), MidpointRounding.AwayFromZero), $"f = {f}");
            }

            // half points and partial eligibility, where the whole-point rounding decides the answer: ApplyFortitude removes
            // round-half-away(eligible x (1 - multiplier)), so it agrees with the unrounded multiplier to within half a point
            foreach (var (damage, eligible, f, expected) in new (uint, uint, double, uint)[]
            {
                (10u, 10u, 0.25, 7u),     // 2.5 removed -> 3
                (7u, 5u, 0.1, 6u),        // 0.5 removed -> 1, eligible < damage
                (101u, 33u, 0.5, 84u),    // 16.5 removed -> 17
                (40u, 9u, 0.5, 35u),      // 4.5 removed -> 5
                (12u, 12u, 0.2, 10u),     // 2.4 removed -> 2
                (3u, 3u, 1.0, 0u),        // the whole hit, floored at 0
            })
            {
                var applied = JewelryAspectMath.ApplyFortitude(damage, eligible, f);
                var removedUnrounded = eligible * (1.0 - JewelryAspectMath.FortitudeMultiplier(f));

                Assert.AreEqual(expected, applied, $"({damage}, {eligible}, {f})");
                Assert.IsTrue(Math.Abs(applied - (damage - removedUnrounded)) <= 0.5 + 1e-9, $"({damage}, {eligible}, {f}) within half a point");
            }
        }

        // ---- the magic absorb seam: AbsorbModFor with an explicit facing equals the projectile's own reading ----

        private static Creature ShieldedCaster(float absorbMagicDamage)
        {
            var defender = TestCreatures.CreateDefender(shieldSkill: 400);
            defender.Location = new Position(0x00010064, 55.0f, 45.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            var weenie = new Weenie
            {
                WeenieClassId = 993100,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                    { PropertyInt.ArmorLevel, 100 },
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.Shield },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.ArmorModVsSlash, 1.0 },
                    { PropertyFloat.AbsorbMagicDamage, absorbMagicDamage },
                },
            };

            var shield = new Clothing(weenie, new ObjectGuid(nextGuid++));
            defender.EquippedObjects[shield.Guid] = shield;

            return defender;
        }

        private static SpellProjectile ProjectileAt(float x, float y)
        {
            var projectile = new SpellProjectile(new Weenie { WeenieClassId = 993101, WeenieType = WeenieType.ProjectileSpell }, new ObjectGuid(nextGuid++));
            projectile.Location = new Position(0x00010064, x, y, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            return projectile;
        }

        [TestMethod]
        public void AbsorbModFor_EqualsGetAbsorbMod_InEveryStance_AndBothFacings_AndWritesNothing()
        {
            var defender = ShieldedCaster(0.5f);
            var front = ProjectileAt(55.0f, 60.0f);
            var behind = ProjectileAt(55.0f, 30.0f);

            foreach (var mode in new[] { CombatMode.NonCombat, CombatMode.Melee, CombatMode.Missile, CombatMode.Magic })
            {
                defender.SetCombatModeForSimulator(mode);

                Assert.AreEqual(front.GetAbsorbMod(defender), SpellProjectile.AbsorbModFor(defender, mode, withinShieldArc: true), $"{mode}, front");
                Assert.AreEqual(behind.GetAbsorbMod(defender), SpellProjectile.AbsorbModFor(defender, mode, withinShieldArc: false), $"{mode}, behind");
            }

            // positive control: in melee the shield absorbs in front and not behind, so the facing argument is live
            defender.SetCombatModeForSimulator(CombatMode.Melee);
            Assert.IsTrue(front.GetAbsorbMod(defender) < 1.0f);
            Assert.AreEqual(1.0f, behind.GetAbsorbMod(defender));

            // a stance argument is a pure read: it neither needs nor writes the live stance
            defender.SetCombatModeForSimulator(CombatMode.Magic);
            var viaArgument = SpellProjectile.AbsorbModFor(defender, CombatMode.Melee, withinShieldArc: true);
            Assert.AreEqual(CombatMode.Magic, defender.CombatMode);
            Assert.IsTrue(viaArgument < 1.0f, "the Melee argument reads the shield although the creature is in Magic");
        }

        /// <summary>Each spell kind's live chain, pinned at the exact lines the spell table composes, so a new layer there fails here.</summary>
        [TestMethod]
        public void Pin_SpellPaths_TheTableComposes()
        {
            const string projectile = "ACE.Server/WorldObjects/SpellProjectile.cs";
            const string calc = "public float? CalculateDamage(WorldObject source, Creature target, ref bool criticalHit, ref bool critDefended, ref bool overpower)";
            AssertLineOnce(projectile, calc, "var absorbMod = GetAbsorbMod(target);");
            AssertLineOnce(projectile, calc, "resistanceMod = (float)Math.Max(0.0f, target.GetResistanceMod(resistanceType, this, null, weaponResistanceMod, archmageInnate));");
            AssertLineOnce(projectile, calc, "finalDamage *= elementalDamageMod * slayerMod * resistanceMod * absorbMod;");
            AssertLineOnce(projectile, calc, "finalDamage = (lifeMagicDamage + critDamageBonus) * elementalDamageMod * slayerMod * resistanceMod * absorbMod;");

            const string target = "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)";
            AssertLineOnce(projectile, target, "damageResistRatingMod = target.GetDamageResistRatingMod(CombatType.Magic, true, ProjectileSource, ratingScales.DamageRatingScale, out var rawDamageResistRating, out var scaledDamageResistRating);");
            AssertLineOnce(projectile, target, "damage *= damageRatingMod * damageResistRatingMod;");
            AssertLineOnce(projectile, target, "var afterMitigation = targetPlayer.ApplyPreWriteDamageClassAbilities(ProjectileSource, Spell.DamageType, beforeMitigation, isSpell: true);");

            AssertLineOnce(projectile, "public float GetAbsorbMod(Creature target)", "return AbsorbModCore(target, target.CombatMode, this, false);");

            const string magic = "ACE.Server/WorldObjects/WorldObject_Magic.cs";
            const string boost = "private void HandleCastSpell_Boost(Spell spell, Creature targetCreature, bool fromProc = false)";
            AssertLineOnce(magic, boost, "var harmResistanceMod = targetCreature.GetResistanceMod(resistanceType, this, null, lifeWeaponResistMod);");
            AssertNoLineContains(magic, boost, "GetDamageResistRatingMod");
            AssertNoLineContains(magic, boost, "GetAbsorbMod");

            AssertLineOnce(magic, "private void HandleCastSpell_Transfer(Spell spell, Creature targetCreature, bool isSecondaryStrike = false)", "? (float)transferSource.GetHealthDrainResistanceOnly()");

            const string ticks = "public void ApplyDamageTick(List<PropertiesEnchantmentRegistry> enchantments, DamageType damageType, bool aetheria = false)";
            const string enchantments = "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs";
            AssertLineOnce(enchantments, ticks, "var resistanceMod = creature.GetResistanceMod(damageType, damager, null);");
            AssertLineOnce(enchantments, ticks, "var dotResistRatingMod = Creature.GetNegativeRatingMod(creature.GetDotResistanceRating());");
            AssertLineOnce(enchantments, ticks, "tickAmount *= resistanceMod * damageResistRatingMod * dotResistRatingMod;");

            AssertLineOnce("ACE.Server/WorldObjects/Player_ClassAbilities.cs",
                "private uint ApplyGearFortitude(WorldObject source, uint damage, bool isHazard, uint? fortitudeEligibleDamage, uint removedSoFar, out uint eligible)",
                "return JewelryAspectMath.ApplyFortitude(damage, eligible, GetFortitude(source));");
        }
    }
}
