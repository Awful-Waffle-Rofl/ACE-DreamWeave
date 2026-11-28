using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pins for the Custom Summons weenies (WaffleACE, 2026-11-02): three blank caster devices and
    /// their pets (1007700-1007705), the Revenant Lich blank caster and its pet (1007751, 1007752), eleven devices and pets (1007706-1007727; the pets are level 300 A'nekshay since round 2), their weapons, the Warden shield (1007753), the Lich wand (1007754), the Ossuary wand (1007755) and
    /// ammo, the Sacrificial Dagger, the Duo Summon Essence, the Summon Gem Hammer, and Vardo's stock rows.
    /// Pure file parsing of the committed SQL (no database, no PropertyManager), so it is order independent.
    /// A hand-written C# fixture would not catch a content regression; these read the shipped units.
    /// </summary>
    [TestClass]
    public class CustomSummonWeenieTests
    {
        private const int SharedCooldownProperty = 280;
        private const int MaxStructureProperty = 91;
        private const int StructureProperty = 92;
        private const int CooldownDurationProperty = 167;
        private const int ValueProperty = 19;
        private const int StackUnitValueProperty = 15;
        private const int ItemsCapacityProperty = 6;
        private const int SummoningMasteryProperty = 362;
        private const int AiAllowedCombatStyleProperty = 101;
        private const int GearRatingFirst = 370;   // gear damage / crit / etc. rating band
        private const int GearRatingLast = 390;

        private const int SummonCasterBrain = 9087;
        private const int SummonNeverInitiates = 9088;
        private const int MultiShot = 9011;
        private const int MultiShotCount = 9016;
        private const int MonsterCombatEffects = 9015;
        private const int AutowieldLeft = 130;

        private const int StubbornMissile = 524288;
        private const int DualWield = 256;
        private const int ShieldLocation = 2097152;

        private const int SkillCreatureEnchantment = 31;
        private const int SkillLifeMagic = 33;
        private const int SkillWarMagic = 34;
        private const int SkillVoidMagic = 43;
        private const int SkillManaConversion = 16;

        private const int LichDevice = 1007751;
        private const int LichPet = 1007752;

        // the Revenant Lich is a blank caster too, at level 300 since round 2 (890 magic = 700 + 190, 25 MMD, the
        // level-225 requirement rows, which the owner kept)
        private static readonly int[] CasterDevices = { 1007700, 1007701, 1007702, LichDevice };
        private static readonly int[] CasterPets = { 1007703, 1007704, 1007705, LichPet };
        private static readonly int[] CasterMagicSkill = { 400, 500, 600, 890 };

        private const int WardenShield = 1007753;
        private const int LichWand = 1007754;
        private const int OssuaryWand = 1007755;
        private const int HeldLocation = 16777216;

        // the three Ossuary caster pets (150 / 180 / 200)
        private static readonly int[] OssuaryPets = { 1007703, 1007704, 1007705 };

        private static readonly int[] TankDevices = { 1007706, 1007707, 1007708 };
        private static readonly int[] ArcherDevices = { 1007709, 1007710, 1007711, 1007712 };
        private static readonly int[] MeleeDevices = { 1007713, 1007714, 1007715, 1007716 };

        private static readonly int[] Level225Devices = TankDevices.Concat(ArcherDevices).Concat(MeleeDevices).ToArray();

        // pets sit 11 above their device, in the same order
        private static int PetOf(int device) => device + 11;

        private static readonly int[] TankPets = TankDevices.Select(PetOf).ToArray();
        private static readonly int[] ArcherPets = ArcherDevices.Select(PetOf).ToArray();
        private static readonly int[] MeleePets = MeleeDevices.Select(PetOf).ToArray();

        private const int DaggerWcid = 1007740;
        private const int DuoGemWcid = 1007741;
        private const int HammerWcid = 1007742;

        // the Vardo shelf: wcid -> MMD price (owner decisions 2026-11-01)
        private static readonly Dictionary<int, int> VardoPrices = BuildVardoPrices();

        private static Dictionary<int, int> BuildVardoPrices()
        {
            var d = new Dictionary<int, int>();

            foreach (var w in CasterDevices) d[w] = 10;
            foreach (var w in Level225Devices) d[w] = 25;

            d[LichDevice] = 25;

            d[DaggerWcid] = 5;
            d[DuoGemWcid] = 5;
            d[HammerWcid] = 5;

            return d;
        }

        // the retail effigies Vardo already sold before this feature
        private static readonly int[] ExistingVardoStock = { 1003152, 1003154, 1003155, 1003156 };

        // every word VTank's mastery regex looks for (docs/summons design, "VTank reads")
        private static readonly string[] VTankWords =
        {
            "Elemental", "Child", "Wisp", "Knight", "K'nath",
            "Zombie", "Spectre", "Skeleton", "Maiden",
            "Moar", "Grievver", "Phyntos",
        };

        [TestMethod]
        public void Devices_IconOverlay_CastersKeepTierBadges_Level300sGetGreenEmblem_DuoGemGetsFigureGlyph()
        {
            const int IconOverlay = 50;

            // retail essences use one overlay per tier: 0x06007426 = "150", 0x06007427 = "180", 0x06007428 = "200".
            // There is no "300" tier badge in the dat, so the level-300 devices use a small green round emblem
            // (0x06002F86) and the Duo Gem a small figure glyph (0x060030AD), both in the bottom-right corner.
            var expected = new Dictionary<int, uint> { [1007700] = 0x06007426, [1007701] = 0x06007427, [1007702] = 0x06007428 };

            foreach (var wcid in Level225Devices)
                expected[wcid] = 0x06002F86;

            expected[DuoGemWcid] = 0x060030AD;

            foreach (var kv in expected)
            {
                Assert.IsTrue(Load(kv.Key).Dids.TryGetValue(IconOverlay, out var overlay), $"{kv.Key} has no IconOverlay.");
                Assert.AreEqual(kv.Value, overlay, $"{kv.Key} IconOverlay is 0x{overlay:X8}, expected 0x{kv.Value:X8}.");
            }
        }

        [TestMethod]
        public void Devices_CarrySharedCooldown213_Fifty_Charges_AndNoRatings_BlanksCarryNoCooldown()
        {
            var problems = new List<string>();

            foreach (var wcid in CasterDevices.Concat(Level225Devices))
            {
                var w = Load(wcid);
                var ints = w.Ints;

                if (CasterDevices.Contains(wcid))
                {
                    // an unbound blank must not look like a summon to VTank (it keys on SharedCooldown 213);
                    // PetDevice.CompleteBinding writes both when the second scroll binds
                    if (ints.ContainsKey(SharedCooldownProperty))
                        problems.Add($"{wcid}: a blank caster must carry no SharedCooldown (280).");

                    if (w.Floats.ContainsKey(CooldownDurationProperty))
                        problems.Add($"{wcid}: a blank caster must carry no CooldownDuration (float 167).");
                }
                else
                {
                    if (!ints.TryGetValue(SharedCooldownProperty, out var cd) || cd != 213)
                        problems.Add($"{wcid}: SharedCooldown (280) must be 213, found {(ints.ContainsKey(280) ? ints[280].ToString() : "none")}.");

                    if (!w.Floats.TryGetValue(CooldownDurationProperty, out var dur) || Math.Abs(dur - 45.0) > 1e-9)
                        problems.Add($"{wcid}: CooldownDuration (float 167) must be 45.");
                }

                if (!ints.TryGetValue(MaxStructureProperty, out var max) || max != 50)
                    problems.Add($"{wcid}: MaxStructure (91) must be 50.");

                if (!ints.TryGetValue(StructureProperty, out var cur) || cur != 50)
                    problems.Add($"{wcid}: Structure (92) must be 50.");

                foreach (var k in ints.Keys.Where(k => k >= GearRatingFirst && k <= GearRatingLast))
                    problems.Add($"{wcid}: carries rating-band property {k}; Vardo sells everything with no ratings.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Level225Devices_AndDuoGem_CarryNoSummoningMastery()
        {
            var problems = new List<string>();

            foreach (var wcid in Level225Devices.Append(DuoGemWcid).Append(LichDevice))
            {
                if (Load(wcid).Ints.ContainsKey(SummoningMasteryProperty))
                    problems.Add($"{wcid}: SummoningMastery (362) must be absent (VTank must never block a level-225 summon or the gem on mastery).");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void NoName_ContainsAVTankMasteryWord()
        {
            var problems = new List<string>();

            foreach (var wcid in AllWcids())
            {
                var name = Load(wcid).Strings.TryGetValue(1, out var n) ? n : null;

                if (string.IsNullOrEmpty(name))
                {
                    problems.Add($"{wcid}: no Name.");
                    continue;
                }

                foreach (var word in VTankWords)
                {
                    if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                        problems.Add($"{wcid} '{name}': contains the VTank mastery word '{word}'.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Archers_AreStubbornMultishot2()
        {
            var problems = new List<string>();

            foreach (var wcid in ArcherPets)
            {
                var pet = Load(wcid);

                if (!pet.Ints.TryGetValue(AiAllowedCombatStyleProperty, out var style) || style != StubbornMissile)
                    problems.Add($"{wcid}: AiAllowedCombatStyle (101) must be StubbornMissile ({StubbornMissile}).");

                if (!pet.Bools.TryGetValue(SummonNeverInitiates, out var ni) || !ni)
                    problems.Add($"{wcid}: SummonNeverInitiates (9088) must be True.");

                // the +2 arrows ride on the launcher: WorldObject_Weapon.IsMultiShot / MultiShotCount
                var bows = pet.CreateList.Select(r => Load(r.Wcid)).Where(c => c.WeenieType == 3).ToList();

                if (bows.Count != 1)
                {
                    problems.Add($"{wcid}: expected exactly one MissileLauncher in the create list, found {bows.Count}.");
                    continue;
                }

                var bow = bows[0];

                if (!bow.Bools.TryGetValue(MultiShot, out var ms) || !ms)
                    problems.Add($"{wcid}: bow {bow.Wcid} must carry MultiShot (9011) True.");

                if (!bow.Ints.TryGetValue(MultiShotCount, out var count) || count != 2)
                    problems.Add($"{wcid}: bow {bow.Wcid} must carry MultiShotCount (9016) 2.");

                // BaseDamageMod multiplies the arrow's Damage by the launcher's DamageMod (float 63); 1.0 keeps the arrow's Damage as the whole missile damage
                if (!bow.Floats.TryGetValue(63, out var damageMod) || Math.Abs(damageMod - 1.0) > 1e-9)
                    problems.Add($"{wcid}: bow {bow.Wcid} DamageMod (float 63) must be 1.0, found {(bow.Floats.ContainsKey(63) ? bow.Floats[63].ToString() : "none")}.");

                if (!pet.CreateList.Any(r => Load(r.Wcid).WeenieType == 5))
                    problems.Add($"{wcid}: no ammunition in the create list.");

                CheckAllWield(pet, problems);
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void MeleePets_AreDualWield_WithOffHand_AndFrenzy()
        {
            var problems = new List<string>();

            foreach (var wcid in MeleePets)
            {
                var pet = Load(wcid);

                if (!pet.Ints.TryGetValue(AiAllowedCombatStyleProperty, out var style) || style != DualWield)
                    problems.Add($"{wcid}: AiAllowedCombatStyle (101) must be DualWield ({DualWield}).");

                CheckAllWield(pet, problems);

                var weapons = pet.CreateList.Select(r => Load(r.Wcid)).Where(c => c.WeenieType == 6).ToList();
                var offHands = weapons.Where(x => x.Bools.TryGetValue(AutowieldLeft, out var b) && b).ToList();
                var mains = weapons.Except(offHands).ToList();

                if (offHands.Count != 1)
                    problems.Add($"{wcid}: expected exactly one AutowieldLeft off-hand weapon, found {offHands.Count}.");
                else if (!offHands[0].Ints.TryGetValue(9, out var loc) || loc != ShieldLocation)
                    problems.Add($"{wcid}: off-hand {offHands[0].Wcid} ValidLocations (9) must be exactly Shield ({ShieldLocation}).");

                if (mains.Count != 1)
                    problems.Add($"{wcid}: expected exactly one main-hand weapon, found {mains.Count}.");
                else if (!mains[0].Ints.TryGetValue(9, out var mainLoc) || mainLoc != 1048576)
                    problems.Add($"{wcid}: main hand {mains[0].Wcid} ValidLocations (9) must be present and MeleeWeapon (1048576).");

                if (!pet.Strings.TryGetValue(MonsterCombatEffects, out var fx) || !Regex.IsMatch(fx, @"(^|;)\s*ramp axis=attackspeed on=hit\b"))
                    problems.Add($"{wcid}: MonsterCombatEffects (9015) must contain 'ramp axis=attackspeed on=hit'.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Casters_HaveBrain_NeverInitiate_AndMagicSkills400_500_600()
        {
            var problems = new List<string>();

            for (var i = 0; i < CasterPets.Length; i++)
            {
                var wcid = CasterPets[i];
                var pet = Load(wcid);

                if (!pet.Bools.TryGetValue(SummonCasterBrain, out var brain) || !brain)
                    problems.Add($"{wcid}: SummonCasterBrain (9087) must be True.");

                if (!pet.Bools.TryGetValue(SummonNeverInitiates, out var ni) || !ni)
                    problems.Add($"{wcid}: SummonNeverInitiates (9088) must be True.");

                foreach (var skill in new[] { SkillWarMagic, SkillLifeMagic, SkillCreatureEnchantment, SkillVoidMagic })
                {
                    if (!pet.Skills.TryGetValue(skill, out var s) || s.InitLevel != CasterMagicSkill[i])
                        problems.Add($"{wcid}: skill {skill} must be {CasterMagicSkill[i]}.");
                }

                if (pet.SpellBookRows > 0)
                    problems.Add($"{wcid}: the spellbook must be empty (the spells come from bound scrolls).");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// The Revenant Lich pair (owner spec 2026-11-02): the pet is level 300 (round 2) and carries Mana Rush exactly as authored
        /// (ramp axis=castspeed on=cast, +6.6% per completed cast, 5 stacks = 1.33x, 20 s lapse - the owner tunes the string),
        /// the device carries the level-225 requirement rows (Summoning 600 specialized, character level 200), which round 2
        /// kept, and the device's IconOverlay (did 50) is the green level-300 corner emblem 0x06002F86.
        /// </summary>
        [TestMethod]
        public void RevenantLich_IsLevel300_CarriesManaRush_AndTheLevel225Requirement()
        {
            var problems = new List<string>();

            var pet = Load(LichPet);
            var device = Load(LichDevice);

            if (!pet.Ints.TryGetValue(25, out var level) || level != 300)
                problems.Add($"{LichPet}: Level (25) must be 300.");

            if (!device.Dids.TryGetValue(50, out var overlay) || overlay != 0x06002F86)
                problems.Add($"{LichDevice}: IconOverlay (did 50) must be 0x06002F86, the green level-300 corner emblem.");

            if (!pet.Strings.TryGetValue(MonsterCombatEffects, out var fx) || fx != "ramp axis=castspeed on=cast per=0.066 max=5 window=20")
                problems.Add($"{LichPet}: MonsterCombatEffects (9015) must be exactly 'ramp axis=castspeed on=cast per=0.066 max=5 window=20'.");

            var expected = new Dictionary<int, int> { [366] = 54, [367] = 600, [368] = 54, [369] = 200 };

            foreach (var kv in expected)
            {
                if (!device.Ints.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    problems.Add($"{LichDevice}: int {kv.Key} must be {kv.Value}.");
            }

            // only the Lich carries a ramp among the casters; the Ossuary line is unchanged
            foreach (var wcid in CasterPets.Where(w => w != LichPet))
            {
                if (Load(wcid).Strings.ContainsKey(MonsterCombatEffects))
                    problems.Add($"{wcid}: an Ossuary caster carries no MonsterCombatEffects.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// The caster AI caches each spell's mana cost; a trained ManaConversion turns that cost into a random roll
        /// (coordinator invariant 2026-11-02). Absent, or Untrained (s_a_c 1), is allowed; Trained (2) or Specialized (3) is not.
        /// </summary>
        [TestMethod]
        public void Casters_DoNotTrainManaConversion()
        {
            var problems = new List<string>();

            foreach (var wcid in CasterPets)
            {
                var pet = Load(wcid);

                if (pet.Skills.TryGetValue(SkillManaConversion, out var s) && s.Sac >= 2)
                    problems.Add($"{wcid}: ManaConversion (16) is {(s.Sac == 3 ? "Specialized" : "Trained")}; it must be absent or Untrained.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// Every caster pet wields exactly one wand from its create list (destination 2), which on MotionTable 0x09000001
        /// puts it in the Magic stance. The Revenant Lich (owner decision 2026-11-02) wields the Revenant Lich Wand, whose
        /// only combat property is the Crippling Blow imbue - no elemental mod (the Lich casts whatever element the player
        /// binds) and no defense mod (a wielded wand would move its melee defense). The three Ossuary casters (owner request
        /// 2026-11-03) wield the Ossuary Wand, a separate wcid so the Crippling Blow never reaches the lower tiers.
        /// </summary>
        [TestMethod]
        public void Casters_EachWieldOneWand_TheLichsCarriesOnlyCripplingBlow()
        {
            var problems = new List<string>();

            foreach (var wcid in CasterPets)
            {
                var pet = Load(wcid);
                var expected = wcid == LichPet ? LichWand : OssuaryWand;
                var wands = pet.CreateList.Where(r => Load(r.Wcid).WeenieType == (int)ACE.Entity.Enum.WeenieType.Caster).ToList();

                if (wands.Count != 1 || wands[0].Wcid != expected)
                    problems.Add($"{wcid}: must wield exactly one wand, {expected}.");

                CheckAllWield(pet, problems);
            }

            var wand = Load(LichWand);

            if (!wand.Ints.TryGetValue(179, out var imbue) || imbue != (int)ACE.Entity.Enum.ImbuedEffectType.CripplingBlow)
                problems.Add($"{LichWand}: ImbuedEffect (179) must be exactly CripplingBlow (2).");

            if (!wand.Ints.TryGetValue(9, out var loc) || loc != HeldLocation)
                problems.Add($"{LichWand}: ValidLocations (9) must be exactly Held ({HeldLocation}).");

            foreach (var forbidden in new[] { 152, 29, 150, 147, 136, 138 })
            {
                if (wand.Floats.ContainsKey(forbidden))
                    problems.Add($"{LichWand}: float {forbidden} must be absent (ElementalDamageMod, defense, crit and slayer mods stay off).");
            }

            foreach (var forbidden in new[] { 45, 158, 160, 166, 106 })
            {
                if (wand.Ints.ContainsKey(forbidden))
                    problems.Add($"{LichWand}: int {forbidden} must be absent (no element, wield requirement, slayer or spellcraft).");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        // every property the plain Ossuary Wand may carry (owner request 2026-11-03: no imbue, no elemental mod, no
        // defense mod, no spell, no requirement). ints: ItemType, EncumbranceVal, ValidLocations, ItemUseable, UiEffects,
        // Value, DefaultCombatStyle, PhysicsState, TargetType, HookType, WeaponType; float: DefaultScale only.
        private static readonly HashSet<int> OssuaryWandInts = new HashSet<int> { 1, 5, 9, 16, 18, 19, 46, 93, 94, 151, 353 };
        private static readonly HashSet<int> OssuaryWandFloats = new HashSet<int> { 39 };

        private static List<string> OssuaryWandProblems(Weenie wand)
        {
            var problems = new List<string>();

            if (wand.WeenieType != (int)ACE.Entity.Enum.WeenieType.Caster)
                problems.Add($"{wand.Wcid}: must be a Caster (35).");

            if (!wand.Ints.TryGetValue(9, out var loc) || loc != HeldLocation)
                problems.Add($"{wand.Wcid}: ValidLocations (9) must be exactly Held ({HeldLocation}).");

            if (wand.Ints.ContainsKey(179))
                problems.Add($"{wand.Wcid}: ImbuedEffect (179) must be absent (the Lich's Crippling Blow stays on 1007754).");

            foreach (var k in wand.Ints.Keys.Where(k => !OssuaryWandInts.Contains(k)))
                problems.Add($"{wand.Wcid}: int {k} is not allowed on the plain wand (imbue, element, damage, requirement, spellcraft or mana).");

            foreach (var k in wand.Floats.Keys.Where(k => !OssuaryWandFloats.Contains(k)))
                problems.Add($"{wand.Wcid}: float {k} is not allowed on the plain wand (ElementalDamageMod, damage, defense, crit or slayer mods).");

            if (wand.SpellBookRows > 0)
                problems.Add($"{wand.Wcid}: the plain wand carries no spells.");

            return problems;
        }

        [TestMethod]
        public void OssuaryWand_IsPlain_NoImbueNoDamageOrDefenseMods_AndEveryOssuaryCasterDependsOnIt()
        {
            var problems = OssuaryWandProblems(Load(OssuaryWand));

            foreach (var wcid in OssuaryPets)
            {
                var header = File.ReadLines(FindWeenieFile(wcid)).Take(8).FirstOrDefault(l => l.StartsWith("-- @depends-on:")) ?? string.Empty;

                if (!Regex.IsMatch(header, $@"weenies/{OssuaryWand}\b"))
                    problems.Add($"{wcid}: @depends-on does not list weenies/{OssuaryWand}.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>The plain-wand guard must discriminate: an imbue, an elemental mod and a defense mod are each reported.</summary>
        [TestMethod]
        public void OssuaryWandGuard_ReportsAnImbueAndMods()
        {
            Assert.AreEqual(0, OssuaryWandProblems(Load(OssuaryWand)).Count);

            var broken = Parse(OssuaryWand, File.ReadAllText(FindWeenieFile(OssuaryWand)));
            broken.Ints[179] = 2;       // CripplingBlow
            broken.Floats[152] = 1.1;   // ElementalDamageMod
            broken.Floats[29] = 1.2;    // WeaponDefense

            var problems = OssuaryWandProblems(broken);

            Assert.IsTrue(problems.Any(p => p.Contains("ImbuedEffect")), string.Join(Environment.NewLine, problems));
            Assert.IsTrue(problems.Any(p => p.Contains("float 152")), string.Join(Environment.NewLine, problems));
            Assert.IsTrue(problems.Any(p => p.Contains("float 29")), string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void SummonNeverInitiates_AppearsOnlyOnArchersAndCasters()
        {
            var expected = new HashSet<int>(ArcherPets.Concat(CasterPets));
            var problems = new List<string>();

            foreach (var wcid in AllWcids())
            {
                var has = Load(wcid).Bools.TryGetValue(SummonNeverInitiates, out var v) && v;

                if (has && !expected.Contains(wcid))
                    problems.Add($"{wcid}: carries SummonNeverInitiates but is not an Archer or caster pet.");

                if (!has && expected.Contains(wcid))
                    problems.Add($"{wcid}: an Archer or caster pet is missing SummonNeverInitiates.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Tanks_Taunt_EveryFiveSeconds_AndNobodyElseDoes()
        {
            var problems = new List<string>();

            foreach (var wcid in AllWcids())
            {
                var has = Load(wcid).Floats.TryGetValue(9021, out var v);

                if (TankPets.Contains(wcid))
                {
                    if (!has || Math.Abs(v - 5.0) > 1e-9)
                        problems.Add($"{wcid}: a Tank pet must carry SummonTauntInterval (9021) 5.");
                }
                else if (has)
                {
                    problems.Add($"{wcid}: only Tank pets carry SummonTauntInterval.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Tanks_WieldOneWeapon_OfTheirDamageType()
        {
            var problems = new List<string>();
            var expectedType = new[] { 1, 4, 2 };   // Slash, Bludgeon, Pierce, in device order

            for (var i = 0; i < TankPets.Length; i++)
            {
                var pet = Load(TankPets[i]);

                CheckAllWield(pet, problems);

                var weapons = pet.CreateList.Select(r => Load(r.Wcid)).Where(c => c.WeenieType == 6).ToList();

                if (weapons.Count != 1)
                    problems.Add($"{pet.Wcid}: expected exactly one weapon, found {weapons.Count}.");
                else if (!weapons[0].Ints.TryGetValue(45, out var dt) || dt != expectedType[i])
                    problems.Add($"{pet.Wcid}: weapon {weapons[0].Wcid} DamageType (45) must be {expectedType[i]}.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void EveryPet_HasItemsCapacityAboveZero_AndLifespan43()
        {
            var problems = new List<string>();

            foreach (var wcid in CasterPets.Concat(TankPets).Concat(ArcherPets).Concat(MeleePets))
            {
                var pet = Load(wcid);

                if (pet.WeenieType != 71)
                    problems.Add($"{wcid}: must be a CombatPet (type 71).");

                if (!pet.Ints.TryGetValue(ItemsCapacityProperty, out var cap) || cap <= 0)
                    problems.Add($"{wcid}: ItemsCapacity (6) must be > 0 or create-list weapons are destroyed.");

                if (!pet.Ints.TryGetValue(267, out var life) || life != 43)
                    problems.Add($"{wcid}: Lifespan (267) must be 43.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        // PropertyDataId.MotionTable (2) per archetype. Round 2 (2026-11-02) put all four level-300 pets on A'nekshay bodies,
        // and every retail A'nekshay (creature type 101) uses 0x09000001, the humanoid table every retail dual-wield creature
        // uses too. The Warden now carries a shield, so its stance is SwordShieldCombat (GetCombatStance adds the shield
        // stance); the Lich and the Ossuary casters (A'nekshay since 2026-11-03) wield a wand, so they cast from the Magic stance.
        private const uint AnekshayMotionTable = 0x09000001;

        private static IEnumerable<(string Archetype, int[] Pets, uint MotionTable, ACE.Entity.Enum.MotionStance Stance)> ArchetypeStances() => new[]
        {
            ("Warden", TankPets, AnekshayMotionTable, ACE.Entity.Enum.MotionStance.SwordShieldCombat),
            ("Marksman", ArcherPets, AnekshayMotionTable, ACE.Entity.Enum.MotionStance.BowCombat),
            ("Reaver", MeleePets, AnekshayMotionTable, ACE.Entity.Enum.MotionStance.DualWieldCombat),
            ("Lich", new[] { LichPet }, AnekshayMotionTable, ACE.Entity.Enum.MotionStance.Magic),
            ("Ossuary", OssuaryPets, AnekshayMotionTable, ACE.Entity.Enum.MotionStance.Magic),
        };

        // the A'nekshay look rows per archetype (round 2): Setup (did 1), ClothingBase (did 7, 0 = none) and PaletteTemplate
        // (int 3, -1 = none), from the retail source weenie named in each pet's header
        private static IEnumerable<(string Archetype, int[] Pets, uint Setup, uint ClothingBase, int PaletteTemplate)> AnekshayLooks() => new[]
        {
            ("Warden", TankPets, 0x02001AA4u, 0x100007D0u, 9),     // A'nekshay Temple Guardian 45803
            ("Marksman", ArcherPets, 0x02001C19u, 0x1000086Bu, 7), // A'nekshen Storm Reaver 52585
            ("Reaver", MeleePets, 0x02001C18u, 0x1000086Bu, 7),    // A'nekshen Thorn Reaver 52589
            ("Lich", new[] { LichPet }, 0x02001C25u, 0u, -1),      // A'nekshen Storm Corruptor 72189
        };

        [TestMethod]
        public void Level300Pets_AreAnekshay_AtLevel300()
        {
            var problems = new List<string>();

            foreach (var (archetype, pets, setup, clothing, template) in AnekshayLooks())
            {
                foreach (var wcid in pets)
                {
                    var pet = Load(wcid);

                    if (!pet.Ints.TryGetValue(25, out var level) || level != 300)
                        problems.Add($"{wcid} ({archetype}): Level (25) must be 300.");

                    if (!pet.Ints.TryGetValue(2, out var ct) || ct != 101)
                        problems.Add($"{wcid} ({archetype}): CreatureType (2) must be 101 (A'nekshay).");

                    if (!pet.Dids.TryGetValue(1, out var s) || s != setup)
                        problems.Add($"{wcid} ({archetype}): Setup must be 0x{setup:X8}.");

                    if (!pet.Dids.TryGetValue(3, out var sound) || sound != 0x20000015)
                        problems.Add($"{wcid} ({archetype}): SoundTable must be 0x20000015.");

                    if (!pet.Dids.TryGetValue(6, out var pb) || pb != 0x0400007E)
                        problems.Add($"{wcid} ({archetype}): PaletteBase must be 0x0400007E.");

                    if (!pet.Dids.TryGetValue(22, out var pet22) || pet22 != 0x34000025)
                        problems.Add($"{wcid} ({archetype}): PhysicsEffectTable must be 0x34000025.");

                    pet.Dids.TryGetValue(7, out var cb);
                    if (cb != clothing)
                        problems.Add($"{wcid} ({archetype}): ClothingBase must be {(clothing == 0 ? "absent" : $"0x{clothing:X8}")}.");

                    var hasTemplate = pet.Ints.TryGetValue(3, out var pt);
                    if (template < 0 ? hasTemplate : (!hasTemplate || pt != template))
                        problems.Add($"{wcid} ({archetype}): PaletteTemplate must be {(template < 0 ? "absent" : template.ToString())}.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// The Ossuary casters (owner request 2026-11-03) are A'nekshay at their own levels 150 / 180 / 200, on the body of
        /// the A'nekshen Thorn Corruptor (retail 72195): a Setup other than the Lich's, Warden's, Marksman's and Reaver's,
        /// and no ClothingBase or PaletteTemplate (the robe is a graft; raw rows would make both inert anyway).
        /// </summary>
        [TestMethod]
        public void OssuaryCasters_AreThornCorruptors_AtTheirOwnLevels()
        {
            var problems = new List<string>();
            var levels = new[] { 150, 180, 200 };
            var otherSetups = AnekshayLooks().Select(l => l.Setup).ToList();

            for (var i = 0; i < OssuaryPets.Length; i++)
            {
                var wcid = OssuaryPets[i];
                var pet = Load(wcid);

                if (!pet.Ints.TryGetValue(25, out var level) || level != levels[i])
                    problems.Add($"{wcid}: Level (25) must stay {levels[i]}.");

                if (!pet.Ints.TryGetValue(2, out var ct) || ct != 101)
                    problems.Add($"{wcid}: CreatureType (2) must be 101 (A'nekshay).");

                var expected = new Dictionary<int, uint> { [1] = 0x02001C26, [2] = 0x09000001, [3] = 0x20000015, [6] = 0x0400007E, [22] = 0x34000025 };

                foreach (var kv in expected)
                {
                    if (!pet.Dids.TryGetValue(kv.Key, out var v) || v != kv.Value)
                        problems.Add($"{wcid}: d_i_d {kv.Key} must be 0x{kv.Value:X8}.");
                }

                if (pet.Dids.ContainsKey(7))
                    problems.Add($"{wcid}: ClothingBase (did 7) must be absent.");

                if (pet.Ints.ContainsKey(3))
                    problems.Add($"{wcid}: PaletteTemplate (int 3) must be absent.");

                if (otherSetups.Contains(pet.Dids.GetValueOrDefault(1)))
                    problems.Add($"{wcid}: shares its Setup with a level-300 archetype.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        // the earthtone robe graft (owner request 2026-11-03, the Lich by owner ruling the same day): the Colosseum Master's
        // Robe (retail 5854, ClothingBase 0x10000198), human-male ClothingBaseEffects map, on the Corruptor Setups
        // 0x02001C25 / 0x02001C26. Head 16 and hands 12 / 15 stay A'nekshay.
        private static readonly Dictionary<int, uint> RobeGraft = new Dictionary<int, uint>
        {
            [0] = 0x01001A16, [1] = 0x01001A0D, [2] = 0x01001A06, [3] = 0x010001EC, [4] = 0x010001EC, [5] = 0x01001A11,
            [6] = 0x01001A09, [7] = 0x010001EC, [8] = 0x010001EC, [9] = 0x0100120D, [10] = 0x010019F7, [11] = 0x010019ED,
            [13] = 0x010019FF, [14] = 0x010019EF,
        };

        // (part, old, new): only the map's non-identity swaps
        private static readonly string[] RobeTextureMap = { "0:05001879:0500188C", "9:050003D5:0500188F", "9:050003D4:05001890" };

        // (palette, offset, length) in row order: the broad body row first, then trim and sash
        // the three Ossuary casters share one set: dark brown body, dull green trim, brown sash
        private static readonly string[] RobePalette = { "04000EFC:40:40", "040005DB:80:12", "040005DB:116:12", "040005E0:96:12" };

        // the Revenant Lich carries its own set (owner ruling 2026-11-03): deep moss green body, bronze trim, dark brown sash
        private static readonly string[] LichRobePalette = { "04000F06:40:40", "0400044C:80:12", "0400044C:116:12", "040005E0:96:12" };

        private static string[] RobePaletteFor(int wcid) => wcid == LichPet ? LichRobePalette : RobePalette;

        private static List<string> RobeGraftProblems(int wcid, string text) => RobeGraftProblems(wcid, text, RobePaletteFor(wcid));

        private static List<string> RobeGraftProblems(int wcid, string text, string[] expectedPalette)
        {
            var problems = new List<string>();
            var body = StripComments(text);

            var parts = new Dictionary<int, uint>();
            foreach (Match m in Regex.Matches(body, @"INSERT INTO `weenie_properties_anim_part`[^;]*;"))
                foreach (Match r in Regex.Matches(m.Value, @"\(" + wcid + @",\s*(\d+),\s*(\d+)\)"))
                    parts[int.Parse(r.Groups[1].Value)] = uint.Parse(r.Groups[2].Value);

            foreach (var kv in RobeGraft)
            {
                if (!parts.TryGetValue(kv.Key, out var model) || model != kv.Value)
                    problems.Add($"{wcid}: anim_part {kv.Key} must be 0x{kv.Value:X8}.");
            }

            foreach (var part in parts.Keys.Where(k => !RobeGraft.ContainsKey(k)))
                problems.Add($"{wcid}: anim_part {part} is not in the robe graft (head 16 and hands 12 / 15 stay A'nekshay).");

            var swaps = new List<string>();
            foreach (Match m in Regex.Matches(body, @"INSERT INTO `weenie_properties_texture_map`[^;]*;"))
                foreach (Match r in Regex.Matches(m.Value, @"\(" + wcid + @",\s*(\d+),\s*(\d+),\s*(\d+)\)"))
                    swaps.Add($"{r.Groups[1].Value}:{uint.Parse(r.Groups[2].Value):X8}:{uint.Parse(r.Groups[3].Value):X8}");

            if (!swaps.SequenceEqual(RobeTextureMap))
                problems.Add($"{wcid}: texture_map rows are [{string.Join(", ", swaps)}], expected [{string.Join(", ", RobeTextureMap)}].");

            var palette = new List<string>();
            foreach (Match m in Regex.Matches(body, @"INSERT INTO `weenie_properties_palette`[^;]*;"))
                foreach (Match r in Regex.Matches(m.Value, @"\(" + wcid + @",\s*(\d+),\s*(\d+),\s*(\d+)\)"))
                    palette.Add($"{uint.Parse(r.Groups[1].Value):X8}:{r.Groups[2].Value}:{r.Groups[3].Value}");

            if (!palette.SequenceEqual(expectedPalette))
                problems.Add($"{wcid}: palette rows are [{string.Join(", ", palette)}], expected [{string.Join(", ", expectedPalette)}] in that order.");

            return problems;
        }

        /// <summary>
        /// Every caster pet carries the robe graft: the Ossuary casters on 0x02001C26 and the Revenant Lich on 0x02001C25, the
        /// two Corruptor Setups whose parts 0-16 share one bone layout. The palette rows only reach the robe while PaletteBase is
        /// 0x0400007E (the robe textures' DefaultPaletteId); the Corruptor skin is R8G8B8 and takes no palette. All four share the
        /// anim_part and texture_map rows; the three Ossuary casters share one palette set and the Lich carries its own, which
        /// must differ (owner ruling 2026-11-03).
        /// </summary>
        [TestMethod]
        public void Casters_CarryTheEarthtoneRobeGraft()
        {
            var problems = new List<string>();

            if (RobePalette.SequenceEqual(LichRobePalette))
                problems.Add("the Lich's robe palette set must differ from the Ossuary casters' set.");

            foreach (var wcid in CasterPets)
            {
                problems.AddRange(RobeGraftProblems(wcid, File.ReadAllText(FindWeenieFile(wcid))));

                var setup = Load(wcid).Dids.GetValueOrDefault(1);
                if (setup != 0x02001C25 && setup != 0x02001C26)
                    problems.Add($"{wcid}: the robe graft is mapped for the Corruptor Setups 0x02001C25 / 0x02001C26, found 0x{setup:X8}.");

                if (Load(wcid).Dids.GetValueOrDefault(6) != 0x0400007E)
                    problems.Add($"{wcid}: PaletteBase (did 6) must be 0x0400007E or the robe palette rows do not apply.");
            }

            if (Load(LichPet).Dids.GetValueOrDefault(1) != 0x02001C25)
                problems.Add($"{LichPet}: the Lich keeps its Storm Corruptor body 0x02001C25.");

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>The robe guard must discriminate: a missing part, a reordered palette, a dropped swap and the wrong caster's palette set are each reported.</summary>
        [TestMethod]
        public void RobeGraftGuard_ReportsABrokenGraft()
        {
            var text = File.ReadAllText(FindWeenieFile(1007704)).Replace("\r\n", "\n");

            Assert.AreEqual(0, RobeGraftProblems(1007704, text).Count);

            var noPart = text.Replace("\n     , (1007704,  6, 16783881) /* 0x01001A09 Colosseum Master's Robe */", "");
            Assert.AreNotEqual(text, noPart, "fixture edit did not apply");
            Assert.IsTrue(RobeGraftProblems(1007704, noPart).Any(p => p.Contains("anim_part 6 ")));

            var reordered = text.Replace("VALUES (1007704, 67112700, 40, 40)", "VALUES (1007704, 67110368, 96, 12)")
                .Replace("     , (1007704, 67110368, 96, 12)", "     , (1007704, 67112700, 40, 40)");
            Assert.AreNotEqual(text, reordered, "fixture edit did not apply");
            Assert.IsTrue(RobeGraftProblems(1007704, reordered).Any(p => p.Contains("palette rows")));

            var noSwap = text.Replace("\n     , (1007704, 9, 83887060, 83892368) /* 0x050003D4 -> 0x05001890 Colosseum Master's Robe */", "");
            Assert.AreNotEqual(text, noSwap, "fixture edit did not apply");
            Assert.IsTrue(RobeGraftProblems(1007704, noSwap).Any(p => p.Contains("texture_map")));

            // the two palette sets are not interchangeable: each caster's file fails against the other set
            var lichText = File.ReadAllText(FindWeenieFile(LichPet)).Replace("\r\n", "\n");
            Assert.AreEqual(0, RobeGraftProblems(LichPet, lichText).Count);
            Assert.IsTrue(RobeGraftProblems(LichPet, lichText, RobePalette).Any(p => p.Contains("palette rows")));
            Assert.IsTrue(RobeGraftProblems(1007704, text, LichRobePalette).Any(p => p.Contains("palette rows")));

            // the Lich wearing the Ossuary colours is reported
            var lichInOssuaryColours = lichText.Replace("(1007752, 67112710, 40, 40)", "(1007752, 67112700, 40, 40)")
                .Replace("(1007752, 67109964, 80, 12)", "(1007752, 67110363, 80, 12)")
                .Replace("(1007752, 67109964, 116, 12)", "(1007752, 67110363, 116, 12)");
            Assert.AreNotEqual(lichText, lichInOssuaryColours, "fixture edit did not apply");
            Assert.IsTrue(RobeGraftProblems(LichPet, lichInOssuaryColours).Any(p => p.Contains("palette rows")));
        }

        // the Haebrean plate grafted onto every Warden (owner ruling 2026-11-02): part -> GfxObj, from the human-male
        // ClothingBaseEffects map of Haebrean 42749-42757, which fits Setup 0x02001AA4 part for part. No helm (16) and no
        // gauntlets (12, 15), so the A'nekshay head and hands show.
        private static readonly Dictionary<int, uint> WardenGraft = new Dictionary<int, uint>
        {
            [0] = 0x01004425, [1] = 0x01004433, [5] = 0x01004435, [2] = 0x01004432, [6] = 0x01004434,
            [3] = 0x0100442D, [7] = 0x0100442E, [4] = 0x01004436, [8] = 0x01004437, [9] = 0x0100442B,
            [10] = 0x01004428, [13] = 0x0100442A, [11] = 0x01004427, [14] = 0x01004429,
        };

        [TestMethod]
        public void Wardens_CarryTheHaebreanGraft_WithoutHelmOrGauntlets()
        {
            var problems = new List<string>();

            foreach (var wcid in TankPets)
            {
                var text = File.ReadAllText(FindWeenieFile(wcid));
                var rows = new Dictionary<int, uint>();

                foreach (Match m in Regex.Matches(text, @"\(" + wcid + @",\s*(\d+),\s*(\d+)\)\s*/\*\s*0x[0-9A-F]{8} Haebrean"))
                    rows[int.Parse(m.Groups[1].Value)] = uint.Parse(m.Groups[2].Value);

                if (!Regex.IsMatch(text, @"INSERT INTO `weenie_properties_anim_part`"))
                    problems.Add($"{wcid}: no weenie_properties_anim_part block.");

                foreach (var kv in WardenGraft)
                {
                    if (!rows.TryGetValue(kv.Key, out var model) || model != kv.Value)
                        problems.Add($"{wcid}: anim_part {kv.Key} must be 0x{kv.Value:X8}.");
                }

                foreach (var part in rows.Keys.Where(k => !WardenGraft.ContainsKey(k)))
                    problems.Add($"{wcid}: anim_part {part} is not in the graft (no helm 16, no gauntlets 12/15).");

                // keep the native silver: a graft palette row would need PaletteBase 0x0400007E and is not wanted
                if (text.Contains("weenie_properties_palette") || text.Contains("weenie_properties_texture_map"))
                    problems.Add($"{wcid}: the graft carries no palette or texture_map rows.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// The Warden's shield (round 2): every Warden wields it beside its one-handed weapon. ValidLocations exactly Shield and
        /// CombatUse Shield are what make GetEquippedShield find it (and so the SwordShieldCombat stance); ArmorLevel 0 keeps it
        /// cosmetic, because Creature.GetShieldMod reads the ArmorLevel for a creature defender and a real value would move
        /// the Warden's sized time to death.
        /// </summary>
        [TestMethod]
        public void Wardens_WieldTheCosmeticShield()
        {
            var problems = new List<string>();
            var shield = Load(WardenShield);

            if (!shield.Ints.TryGetValue(9, out var loc) || loc != ShieldLocation)
                problems.Add($"{WardenShield}: ValidLocations (9) must be exactly Shield ({ShieldLocation}).");

            if (!shield.Ints.TryGetValue(51, out var use) || use != 4)
                problems.Add($"{WardenShield}: CombatUse (51) must be 4 (Shield).");

            if (!shield.Ints.TryGetValue(28, out var al) || al != 0)
                problems.Add($"{WardenShield}: ArmorLevel (28) must be 0 (cosmetic).");

            foreach (var wcid in TankPets)
            {
                var rows = Load(wcid).CreateList;

                if (rows.Count(r => r.Wcid == WardenShield) != 1)
                    problems.Add($"{wcid}: the create list must wield the Warden shield {WardenShield} exactly once.");

                var weapons = rows.Select(r => Load(r.Wcid)).Where(c => c.WeenieType == 6).ToList();

                if (weapons.Count != 1 || !weapons[0].Ints.TryGetValue(46, out var style) || style != 2)
                    problems.Add($"{wcid}: the Warden keeps exactly one one-handed weapon (DefaultCombatStyle 2) beside the shield.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// The Warden is a tank that takes hits (owner ruling 2026-11-02): its three defense skills stay at the level-225 570
        /// while its attack skill and armor keep the level-300 +190, and its health carries the sizing (4750, init 4630) with
        /// body-part damage 300 matching its weapons. A 760-defense Warden reached the same median time to death by dodging.
        /// </summary>
        [TestMethod]
        public void Wardens_TakeHits_Defenses570_Attack760_Health4750()
        {
            var problems = new List<string>();

            foreach (var wcid in TankPets)
            {
                var pet = Load(wcid);

                foreach (var skill in new[] { 6, 7, 15 })
                {
                    if (!pet.Skills.TryGetValue(skill, out var s) || s.InitLevel != 570)
                        problems.Add($"{wcid}: defense skill {skill} must be 570.");
                }

                if (!pet.Skills.TryGetValue(45, out var attack) || attack.InitLevel != 760)
                    problems.Add($"{wcid}: LightWeapons (45) must keep the level-300 760.");

                if (!pet.Vitals.TryGetValue(1, out var health) || health != "4630/4750")
                    problems.Add($"{wcid}: MaxHealth must be init 4630, current 4750 (found {health}).");

                foreach (var kv in pet.BodyParts)
                {
                    var dVal = kv.Value.Split(',')[0].Trim();

                    if (dVal != "0" && dVal != "300")
                        problems.Add($"{wcid}: body part {kv.Key} d_Val {dVal} must be 0 or 300.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        // every combat row of every wield item. The epic-weapon swap is LOOK ONLY
        // (owner ruling 2026-11-02): a donor's Setup, Icon, PaletteBase, ClothingBase, PaletteTemplate and IconUnderlay may
        // change, these may not, so the stance (DefaultCombatStyle, ValidLocations, WeaponType) holds. Damage carries the round-2
        // level-300 sizing (Reaver 1800 -> 1100, arrows 5775 -> 1450, Warden 1275 -> 300); every other row is as shipped before round 2.
        private static readonly string[] CombatRowKeys =
        {
            "int:44", "int:45", "int:46", "int:47", "int:48", "int:49", "int:9", "int:51", "int:353",
            "float:21", "float:22", "float:29", "float:62", "float:63",
        };

        private static Dictionary<string, string> Combat(int damage, int? damageType, int? style, int? attackType, int? skill, int? time,
            int validLocations, int combatUse, int? weaponType, string length, string variance, string defense, string offense, string damageMod)
        {
            var d = new Dictionary<string, string>
            {
                ["int:44"] = damage.ToString(), ["int:9"] = validLocations.ToString(), ["int:51"] = combatUse.ToString(),
            };

            void Put(string key, object value) { if (value != null) d[key] = value.ToString(); }

            Put("int:45", damageType); Put("int:46", style); Put("int:47", attackType); Put("int:48", skill); Put("int:49", time);
            Put("int:353", weaponType); Put("float:21", length); Put("float:22", variance); Put("float:29", defense);
            Put("float:62", offense); Put("float:63", damageMod);
            return d;
        }

        private static Dictionary<int, Dictionary<string, string>> ExpectedCombatRows()
        {
            var e = new Dictionary<int, Dictionary<string, string>>
            {
                [1007728] = Combat(300, 1, 2, 6, 45, 40, 1048576, 1, 2, "0.95", "0.5", "1", "1", null),
                [1007729] = Combat(300, 4, 2, 4, 45, 40, 1048576, 1, 4, "0.62", "0.5", "1", "1", null),
                [1007730] = Combat(300, 2, 2, 2, 45, 30, 1048576, 1, 5, "1.5", "0.5", "1", "1", null),
            };

            var elements = new[] { 16, 8, 32, 64 };   // Fire, Frost (Cold), Acid, Lightning (Electric), in variant order

            for (var i = 0; i < 4; i++)
            {
                e[1007731 + i] = Combat(0, null, 16, null, 47, 45, 4194304, 2, 8, "0", "0", "1", "1", "1");
                e[1007735 + i] = Combat(1100, elements[i], 2, 6, 45, 40, 1048576, 1, 2, "0.95", "0.5", "1", "1", null);
                e[1007743 + i] = Combat(1450, elements[i], null, null, null, null, 8388608, 3, null, null, "0.3", "1", "1", null);
                e[1007747 + i] = Combat(1100, elements[i], 2, 166, 45, 20, ShieldLocation, 1, 6, "0.4", "0.5", "1", "1", null);
            }

            return e;
        }

        [TestMethod]
        public void WieldItems_CarryUnchangedCombatRows_AfterTheLookSwap()
        {
            var problems = CombatRowProblems(Load);
            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>The combat-row guard must discriminate: one changed Damage and one dropped DamageMod are both reported.</summary>
        [TestMethod]
        public void CombatRowGuard_ReportsAChangedRow()
        {
            Weenie Broken(int wcid)
            {
                var w = Parse(wcid, File.ReadAllText(FindWeenieFile(wcid)));

                if (wcid == 1007736)
                    w.Ints[44] = 1801;

                if (wcid == 1007732)
                    w.Floats.Remove(63);

                return w;
            }

            var problems = CombatRowProblems(Broken);

            Assert.AreEqual(2, problems.Count, string.Join(Environment.NewLine, problems));
            Assert.IsTrue(problems.Any(p => p.StartsWith("1007736: int:44")));
            Assert.IsTrue(problems.Any(p => p.StartsWith("1007732: float:63")));
        }

        private static List<string> CombatRowProblems(Func<int, Weenie> load)
        {
            var problems = new List<string>();

            foreach (var kv in ExpectedCombatRows())
            {
                var w = load(kv.Key);
                var actual = new Dictionary<string, string>();

                foreach (var key in CombatRowKeys)
                {
                    var parts = key.Split(':');
                    var id = int.Parse(parts[1]);

                    if (parts[0] == "int" && w.Ints.TryGetValue(id, out var iv))
                        actual[key] = iv.ToString();
                    else if (parts[0] == "float" && w.Floats.TryGetValue(id, out var fv))
                        actual[key] = fv.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                foreach (var key in CombatRowKeys)
                {
                    kv.Value.TryGetValue(key, out var exp);
                    actual.TryGetValue(key, out var act);

                    if (exp != act)
                        problems.Add($"{kv.Key}: {key} is [{act ?? "(none)"}], expected [{exp ?? "(none)"}].");
                }
            }

            return problems;
        }

        [TestMethod]
        public void EveryArchetype_PinsItsMotionTable()
        {
            var problems = new List<string>();

            foreach (var (archetype, pets, table, _) in ArchetypeStances())
            {
                foreach (var wcid in pets)
                {
                    if (!Load(wcid).Dids.TryGetValue(2, out var mt) || mt != table)
                        problems.Add($"{wcid} ({archetype}): MotionTable (d_i_d 2) must be 0x{table:X8}.");
                }
            }

            // every Warden weapon must resolve to the SwordCombat stance (plus the shield: SwordShieldCombat), the bow to
            // BowCombat (Creature_Combat.GetWeaponStance); the shield is not a weapon and carries no combat style
            foreach (var wcid in TankPets)
            {
                foreach (var row in Load(wcid).CreateList.Where(r => Load(r.Wcid).WeenieType == 6))
                {
                    if (!Load(row.Wcid).Ints.TryGetValue(46, out var style) || style != 2)
                        problems.Add($"{wcid}: weapon {row.Wcid} DefaultCombatStyle (46) must be 2 (OneHanded -> SwordCombat).");
                }
            }

            foreach (var wcid in ArcherPets)
            {
                var bow = Load(wcid).CreateList.Select(r => Load(r.Wcid)).FirstOrDefault(c => c.WeenieType == 3);

                if (bow == null || !bow.Ints.TryGetValue(46, out var style) || style != 16)
                    problems.Add($"{wcid}: the bow's DefaultCombatStyle (46) must be 16 (Bow -> BowCombat).");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// Needs client_portal.dat. For each archetype's pinned MotionTable, the stance its weapons put it in must have the
        /// Ready link Monster_Melee.GetCombatManeuver reads (Links[(stance shl 16) | Ready]), and a melee stance must also be in the
        /// combat maneuver table the pets use (0x30000000). Without the link a pet never attacks.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryArchetype_MotionTableHasAReadyLink_ForItsStance()
        {
            var datDir = new[] { System.Environment.GetEnvironmentVariable("ACE_DAT_PATH"), @"C:\ACE\Dats\", @"C:\Turbine\Asheron's Call\" }
                .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, "client_portal.dat")));

            if (datDir == null)
                Assert.Inconclusive("client_portal.dat not found (set ACE_DAT_PATH to the directory holding the client .dat files).");

            if (ACE.DatLoader.DatManager.PortalDat == null)
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                ACE.DatLoader.DatManager.Initialize(datDir, true, false);
            }

            var problems = new List<string>();
            var combatTable = ACE.DatLoader.DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.CombatManeuverTable>(0x30000000);

            foreach (var (archetype, pets, table, stance) in ArchetypeStances())
            {
                foreach (var wcid in pets)
                {
                    var pinned = Load(wcid).Dids[2];
                    var motionTable = ACE.DatLoader.DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.MotionTable>(pinned);

                    if (motionTable == null)
                    {
                        problems.Add($"{wcid} ({archetype}): MotionTable 0x{pinned:X8} is not in client_portal.dat.");
                        continue;
                    }

                    var key = (uint)stance << 16 | ((uint)ACE.Entity.Enum.MotionCommand.Ready & 0xFFFFFF);

                    if (!motionTable.Links.ContainsKey(key))
                        problems.Add($"{wcid} ({archetype}): MotionTable 0x{pinned:X8} has no Ready link for {stance}.");

                    // the missile path and the caster's cast do not use the combat maneuver table; the melee stances do
                    var caster = stance == ACE.Entity.Enum.MotionStance.NonCombat || stance == ACE.Entity.Enum.MotionStance.Magic;
                    var melee = stance != ACE.Entity.Enum.MotionStance.BowCombat && !caster;

                    if (melee && !combatTable.Stances.ContainsKey(stance))
                        problems.Add($"{wcid} ({archetype}): combat table 0x30000000 has no {stance} stance.");

                    // the caster brain plays CastSpell in its cast stance; the table must carry that motion for the stance
                    if (caster)
                    {
                        if (!motionTable.Links.TryGetValue(key, out var fromReady) || !fromReady.ContainsKey((uint)ACE.Entity.Enum.MotionCommand.CastSpell))
                            problems.Add($"{wcid} ({archetype}): MotionTable 0x{pinned:X8} has no {stance} Ready -> CastSpell link.");
                    }
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void EveryDevice_PointsAtItsPet()
        {
            var problems = new List<string>();

            for (var i = 0; i < CasterDevices.Length; i++)
                CheckPetClass(CasterDevices[i], CasterPets[i], problems);

            foreach (var d in Level225Devices)
                CheckPetClass(d, PetOf(d), problems);

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void BlankCasters_AreBinders_AndLevel225Devices_CarryTheirElement()
        {
            var problems = new List<string>();

            foreach (var wcid in CasterDevices)
            {
                var w = Load(wcid);

                if (!w.Bools.TryGetValue(9086, out var binder) || !binder)
                    problems.Add($"{wcid}: SummonSpellBinder (9086) must be True.");

                if (!w.Ints.TryGetValue(16, out var use) || use != 0x080008)
                    problems.Add($"{wcid}: ItemUseable must be SourceContainedTargetContained (524296).");

                if (w.Ints.ContainsKey(18))
                    problems.Add($"{wcid}: a blank caster carries no UiEffects (the code writes it on binding).");
            }

            // Tank = no element; Archers and Melee = Fire 0x20, Frost 0x80, Acid 0x100, Lightning 0x40 in that order
            var elements = new[] { 0x20, 0x80, 0x100, 0x40 };

            foreach (var wcid in TankDevices)
            {
                if (Load(wcid).Ints.ContainsKey(18))
                    problems.Add($"{wcid}: a Tank carries no UiEffects.");
            }

            foreach (var group in new[] { ArcherDevices, MeleeDevices })
            {
                for (var i = 0; i < group.Length; i++)
                {
                    if (!Load(group[i]).Ints.TryGetValue(18, out var ui) || ui != elements[i])
                        problems.Add($"{group[i]}: UiEffects (18) must be {elements[i]}.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void DaggerGemAndHammer_CarryTheirFlags_AndAreUsableOnATarget()
        {
            var problems = new List<string>();

            var dagger = Load(DaggerWcid);
            var gem = Load(DuoGemWcid);
            var hammer = Load(HammerWcid);

            if (dagger.WeenieType != 38 || !dagger.Bools.GetValueOrDefault(9082))
                problems.Add($"{DaggerWcid}: must be a Gem (38) with SacrificialDagger (9082).");

            if (hammer.WeenieType != 38 || !hammer.Bools.GetValueOrDefault(9085))
                problems.Add($"{HammerWcid}: must be a Gem (38) with SummonGemHammer (9085).");

            if (gem.WeenieType != 70 || !gem.Bools.GetValueOrDefault(9084))
                problems.Add($"{DuoGemWcid}: must be a PetDevice (70) with DuoSummonGem (9084).");

            foreach (var w in new[] { dagger, gem, hammer })
            {
                if (!w.Ints.TryGetValue(16, out var use) || use != 0x080008)
                    problems.Add($"{w.Wcid}: ItemUseable must be SourceContainedTargetContained (524296).");
            }

            // blank gem: the code writes all of these when the second summon is absorbed
            foreach (var k in new[] { 266, 91, 92, 280, 362, 366, 367, 368, 369 })
            {
                if (gem.Ints.ContainsKey(k))
                    problems.Add($"{DuoGemWcid}: the blank gem must not carry int {k}.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Vardo_SellsExactlyTheDecidedItems_AtTheDecidedMmdPrices()
        {
            var vardo = Load(1003153);

            Assert.IsTrue(vardo.Floats.TryGetValue(38, out var sellPrice) && Math.Abs(sellPrice - 1.0) < 1e-9,
                "Vardo's SellPrice (float 38) must stay 1 so an item's Value is its MMD price.");

            var shop = vardo.CreateList.Where(r => r.DestinationType == 4).Select(r => r.Wcid).ToList();

            var expected = new HashSet<int>(ExistingVardoStock.Concat(VardoPrices.Keys));
            var problems = new List<string>();

            foreach (var wcid in expected.Except(shop))
                problems.Add($"{wcid}: Vardo does not stock it.");

            foreach (var wcid in shop.Except(expected))
                problems.Add($"{wcid}: Vardo stocks an item this test does not know.");

            foreach (var wcid in shop.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))
                problems.Add($"{wcid}: Vardo stocks it more than once.");

            foreach (var kv in VardoPrices)
            {
                var item = Load(kv.Key);

                // Vendor.GetSellCost = max(1, ceil(SellPrice * unitValue - 0.1)); the unit value is StackUnitValue (15) when set,
                // else Value (19) - Vendor.GetStackUnitValue, a shop listing being a single unit
                var unitValue = item.Ints.TryGetValue(StackUnitValueProperty, out var suv) ? suv : item.Ints.GetValueOrDefault(ValueProperty);
                var cost = Math.Max(1, (int)Math.Ceiling(sellPrice * unitValue - 0.1));

                if (cost != kv.Value)
                    problems.Add($"{kv.Key}: costs {cost} MMD at Vardo, expected {kv.Value}.");
            }

            var header = File.ReadLines(FindWeenieFile(1003153)).Take(8).FirstOrDefault(l => l.StartsWith("-- @depends-on:")) ?? string.Empty;

            foreach (var wcid in VardoPrices.Keys)
            {
                if (!Regex.IsMatch(header, $@"weenies/{wcid}\b"))
                    problems.Add($"{wcid}: Vardo's @depends-on header does not list it.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// A freshly bought device must show its description from the start: Compose otherwise runs only on a state change,
        /// so each device's shipped LongDesc (string 16) must equal what Compose writes for that fresh item.
        /// </summary>
        [TestMethod]
        public void EveryDevice_ShipsALongDesc_EqualToCompose()
        {
            TestGameTables.EnsureInitialized();

            var oldWeenie = ACE.Server.Entity.SummonItemDescription.PetWeenieLookup;
            var oldName = ACE.Server.Entity.SummonItemDescription.SpellNameLookup;
            var problems = new List<string>();

            try
            {
                ACE.Server.Entity.SummonItemDescription.PetWeenieLookup = wcid => ToModel(Load((int)wcid));
                ACE.Server.Entity.SummonItemDescription.SpellNameLookup = id => $"spell {id}";

                foreach (var wcid in CasterDevices.Concat(Level225Devices))
                {
                    var sql = Load(wcid);
                    var device = new ACE.Server.WorldObjects.PetDevice(ToModel(sql), new ACE.Entity.ObjectGuid((uint)(0x7F5D0000 + wcid - 1007700)));
                    // Compose uses AppendLine, so it carries the platform newline, and its TrimEnd of LF leaves a trailing CR on Windows; compare on LF
                    var composed = ACE.Server.Entity.SummonItemDescription.Compose(device).Replace("\r\n", "\n").TrimEnd('\r');

                    if (!sql.Strings.TryGetValue(16, out var shipped))
                        problems.Add($"{wcid}: no LongDesc (string 16). Expected: {composed.Replace("\n", "|")}");
                    else if (composed != shipped.Replace("\r\n", "\n"))
                        problems.Add($"{wcid}: LongDesc differs from Compose. Expected: {composed.Replace("\n", "|")}");
                }
            }
            finally
            {
                ACE.Server.Entity.SummonItemDescription.PetWeenieLookup = oldWeenie;
                ACE.Server.Entity.SummonItemDescription.SpellNameLookup = oldName;
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>The Duo Gem ships its empty-gem LongDesc, because Compose only runs after an absorb, a bind or a dagger use.</summary>
        [TestMethod]
        public void DuoGem_ShipsALongDesc_EqualToComposeOfAnEmptyGem()
        {
            TestGameTables.EnsureInitialized();

            var sql = Load(DuoGemWcid);
            var gem = new ACE.Server.WorldObjects.PetDevice(ToModel(sql), new ACE.Entity.ObjectGuid(0x7F5D0F00u));

            var oldSlots = ACE.Server.Entity.SummonItemDescription.DuoSlotsHook;
            try
            {
                ACE.Server.Entity.SummonItemDescription.DuoSlotsHook = _ => Array.Empty<ACE.Server.Entity.SummonSlotInfo>();

                var composed = ACE.Server.Entity.SummonItemDescription.Compose(gem).Replace("\r\n", "\n").TrimEnd('\r');

                Assert.IsTrue(sql.Strings.TryGetValue(16, out var shipped), "the Duo Gem has no LongDesc (string 16). Expected: " + composed.Replace("\n", "|"));
                Assert.AreEqual(composed, shipped.Replace("\r\n", "\n"));
            }
            finally
            {
                ACE.Server.Entity.SummonItemDescription.DuoSlotsHook = oldSlots;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Converts a parsed SQL weenie into the runtime model so a real PetDevice can be built from shipped content.</summary>
        private static ACE.Entity.Models.Weenie ToModel(Weenie w)
        {
            return new ACE.Entity.Models.Weenie
            {
                WeenieClassId = (uint)w.Wcid,
                WeenieType = (ACE.Entity.Enum.WeenieType)w.WeenieType,
                PropertiesInt = w.Ints.ToDictionary(kv => (ACE.Entity.Enum.Properties.PropertyInt)kv.Key, kv => kv.Value),
                PropertiesBool = w.Bools.ToDictionary(kv => (ACE.Entity.Enum.Properties.PropertyBool)kv.Key, kv => kv.Value),
                PropertiesFloat = w.Floats.ToDictionary(kv => (ACE.Entity.Enum.Properties.PropertyFloat)kv.Key, kv => kv.Value),
                PropertiesDID = w.Dids.ToDictionary(kv => (ACE.Entity.Enum.Properties.PropertyDataId)kv.Key, kv => kv.Value),
                PropertiesString = w.Strings.ToDictionary(kv => (ACE.Entity.Enum.Properties.PropertyString)kv.Key, kv => kv.Value),
                PropertiesSkill = w.Skills.ToDictionary(kv => (ACE.Entity.Enum.Skill)kv.Key,
                    kv => new ACE.Entity.Models.PropertiesSkill { InitLevel = (uint)kv.Value.InitLevel, SAC = (ACE.Entity.Enum.SkillAdvancementClass)kv.Value.Sac }),
            };
        }

        private static IEnumerable<int> AllWcids()
        {
            return Enumerable.Range(1007700, 56).Where(w => w != 1007739);
        }

        /// <summary>
        /// Every create-list row of a 225 pet must be destination_Type 2 (Wield): Creature_Equipment.GenerateWieldList only
        /// reads rows with the Wield flag, so a Contain (1) or Treasure (8) row would never reach the pet's hands.
        /// </summary>
        private static void CheckAllWield(Weenie pet, List<string> problems)
        {
            if (pet.CreateList.Count == 0)
                problems.Add($"{pet.Wcid}: the create list is empty.");

            foreach (var row in pet.CreateList)
            {
                if (row.DestinationType != 2)
                    problems.Add($"{pet.Wcid}: create-list row {row.Wcid} has destination_Type {row.DestinationType}, expected 2 (Wield).");
            }
        }

        private static void CheckPetClass(int device, int pet, List<string> problems)
        {
            if (!Load(device).Ints.TryGetValue(266, out var pc) || pc != pet)
                problems.Add($"{device}: PetClass (266) must be {pet}.");
        }

        // ---- variant identity: the elemental/damage-type siblings of one archetype must carry identical stats ----

        private static readonly int[] ReaverPets = { 1007724, 1007725, 1007726, 1007727 };
        private static readonly int[] MarksmanPets = { 1007720, 1007721, 1007722, 1007723 };
        private static readonly int[] WardenPets = { 1007717, 1007718, 1007719 };
        private static readonly int[] ReaverBlades = { 1007735, 1007736, 1007737, 1007738 };
        private static readonly int[] ReaverDirks = { 1007747, 1007748, 1007749, 1007750 };
        private static readonly int[] Arrows = { 1007743, 1007744, 1007745, 1007746 };
        private static readonly int[] Bows = { 1007731, 1007732, 1007733, 1007734 };
        private static readonly int[] WardenWeapons = { 1007728, 1007729, 1007730 };

        /// <summary>
        /// Every stat a weenie carries as "kind:key" -> value text (properties, vitals, attributes, skill rows, body-part columns
        /// other than the damage type, and the create-list destinations), so a group comparison sees any column at all.
        /// </summary>
        private static Dictionary<string, string> Signature(Weenie w)
        {
            var d = new Dictionary<string, string> { ["weenieType"] = w.WeenieType.ToString() };

            foreach (var kv in w.Ints) d["int:" + kv.Key] = kv.Value.ToString();
            foreach (var kv in w.Bools) d["bool:" + kv.Key] = kv.Value.ToString();
            foreach (var kv in w.Floats) d["float:" + kv.Key] = kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var kv in w.Strings) d["str:" + kv.Key] = kv.Value;
            foreach (var kv in w.Dids) d["did:" + kv.Key] = kv.Value.ToString("X8");
            foreach (var kv in w.Vitals) d["vital:" + kv.Key] = kv.Value;
            foreach (var kv in w.Attributes) d["attr:" + kv.Key] = kv.Value;
            foreach (var kv in w.SkillFull) d["skill:" + kv.Key] = kv.Value;
            foreach (var kv in w.BodyParts) d["body:" + kv.Key] = kv.Value;

            d["createDest"] = string.Join(",", w.CreateList.Select(r => r.DestinationType));
            d["spellRows"] = w.SpellBookRows.ToString();

            return d;
        }

        /// <summary>Every difference between the group's members and its first member, outside the allowed keys. Empty when the group is identical.</summary>
        private static List<string> VariantDifferences(string group, int[] wcids, ISet<string> allowed, Func<int, Weenie> load)
        {
            var problems = new List<string>();
            var first = Signature(load(wcids[0]));

            foreach (var wcid in wcids.Skip(1))
            {
                var other = Signature(load(wcid));

                foreach (var key in first.Keys.Union(other.Keys).Where(k => !allowed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
                {
                    first.TryGetValue(key, out var a);
                    other.TryGetValue(key, out var b);

                    if (a != b)
                        problems.Add($"{group}: {wcids[0]} vs {wcid} differ on {key}: [{a ?? "(none)"}] vs [{b ?? "(none)"}].");
                }
            }

            return problems;
        }

        // name, descriptions, setup, icon, palette base and template, UiEffects: what only exists to present the element
        private static readonly HashSet<string> NameAndLook = new HashSet<string> { "str:1", "str:15", "str:16", "str:20", "did:1", "did:8", "did:6", "did:7", "int:3", "int:18" };

        private static readonly HashSet<string> PetAllowed = new HashSet<string>(NameAndLook);

        // the element or damage type is the only stat a weapon variant may differ on
        private static readonly HashSet<string> WeaponAllowed = new HashSet<string>(NameAndLook) { "int:45" };

        // Warden weapons are three different donor weapons, so their swing speed, attack type, mass, length, encumbrance, weapon type, scale (float 39) and mutation filter (did 46) differ too
        private static readonly HashSet<string> WardenWeaponAllowed = new HashSet<string>(WeaponAllowed) { "int:49", "int:47", "int:8", "float:21", "float:39", "did:46", "int:5", "int:353" };

        [TestMethod]
        public void VariantsOfOneArchetype_CarryIdenticalStats_ApartFromElementAndLook()
        {
            var problems = new List<string>();

            problems.AddRange(VariantDifferences("Reaver pets", ReaverPets, PetAllowed, Load));
            problems.AddRange(VariantDifferences("Marksman pets", MarksmanPets, PetAllowed, Load));
            problems.AddRange(VariantDifferences("Warden pets", WardenPets, PetAllowed, Load));
            problems.AddRange(VariantDifferences("Reaver blades", ReaverBlades, WeaponAllowed, Load));
            problems.AddRange(VariantDifferences("Reaver dirks", ReaverDirks, WeaponAllowed, Load));
            problems.AddRange(VariantDifferences("arrows", Arrows, WeaponAllowed, Load));
            problems.AddRange(VariantDifferences("bows", Bows, WeaponAllowed, Load));
            problems.AddRange(VariantDifferences("Warden weapons", WardenWeapons, WardenWeaponAllowed, Load));

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>The guard must discriminate: a health change in one variant, or a Damage change in one weapon, must be reported.</summary>
        [TestMethod]
        public void VariantIdentityGuard_ReportsABrokenVariant()
        {
            Weenie Broken(int wcid)
            {
                var w = Parse(wcid, File.ReadAllText(FindWeenieFile(wcid)));

                if (wcid == 1007726)
                    w.Vitals[1] = "1/2";

                if (wcid == 1007738)
                    w.Ints[44] = 1;

                return w;
            }

            Assert.AreEqual(1, VariantDifferences("Reaver pets", ReaverPets, PetAllowed, Broken).Count(p => p.Contains("vital:1")));
            Assert.AreEqual(1, VariantDifferences("Reaver blades", ReaverBlades, WeaponAllowed, Broken).Count(p => p.Contains("int:44")));
            Assert.AreEqual(0, VariantDifferences("Marksman pets", MarksmanPets, PetAllowed, Broken).Count);
        }

        private sealed class Row
        {
            public int Wcid;
            public int DestinationType;
        }

        private struct SkillRow
        {
            public int Sac;
            public int InitLevel;
        }

        private sealed class Weenie
        {
            public int Wcid;
            public int WeenieType;
            public Dictionary<int, int> Ints = new Dictionary<int, int>();
            public Dictionary<int, bool> Bools = new Dictionary<int, bool>();
            public Dictionary<int, double> Floats = new Dictionary<int, double>();
            public Dictionary<int, string> Strings = new Dictionary<int, string>();
            public Dictionary<int, uint> Dids = new Dictionary<int, uint>();
            public Dictionary<int, SkillRow> Skills = new Dictionary<int, SkillRow>();
            public List<Row> CreateList = new List<Row>();
            public int SpellBookRows;

            // vitals (type -> init/current), attributes (type -> init), skill rows and body parts (key -> every column but object_Id, key and d_Type)
            public Dictionary<int, string> Vitals = new Dictionary<int, string>();
            public Dictionary<int, string> Attributes = new Dictionary<int, string>();
            public Dictionary<int, string> BodyParts = new Dictionary<int, string>();
            public Dictionary<int, string> SkillFull = new Dictionary<int, string>();
        }

        private static readonly Dictionary<int, Weenie> Cache = new Dictionary<int, Weenie>();

        private static Weenie Load(int wcid)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(wcid, out var cached))
                    return cached;

                var path = FindWeenieFile(wcid);
                var w = Parse(wcid, File.ReadAllText(path));
                Cache[wcid] = w;
                return w;
            }
        }

        private static string FindWeenieFile(int wcid)
        {
            var dir = Path.Combine(FindContentDir(), "sql", "weenies");
            var file = Directory.EnumerateFiles(dir, wcid + " *.sql", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();

            Assert.IsNotNull(file, $"No unit under {dir} starts with '{wcid} '.");
            return file;
        }

        private static string FindContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(candidate))
                    return Path.Combine(dir.FullName, "Content");

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        /// <summary>Drops block comments and `--` line comments, honoring single-quoted literals ('' escapes a quote).</summary>
        private static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            var inQuote = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (inQuote)
                {
                    sb.Append(c);

                    if (c == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i++;
                        }
                        else
                            inQuote = false;
                    }

                    continue;
                }

                if (c == '\'')
                {
                    inQuote = true;
                    sb.Append(c);
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 1;
                    sb.Append(' ');
                }
                else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    sb.Append('\n');
                }
                else
                    sb.Append(c);
            }

            return sb.ToString();
        }

        private static List<string> SplitStatements(string text)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            var inQuote = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (c == '\'')
                {
                    if (inQuote && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        sb.Append("''");
                        i++;
                        continue;
                    }

                    inQuote = !inQuote;
                }

                if (c == ';' && !inQuote)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }

                sb.Append(c);
            }

            if (sb.ToString().Trim().Length > 0)
                list.Add(sb.ToString());

            return list;
        }

        /// <summary>The top-level parenthesised tuples after VALUES, split into fields outside quotes.</summary>
        private static List<List<string>> ParseRows(string valuesPart)
        {
            var rows = new List<List<string>>();
            var depth = 0;
            var inQuote = false;
            var field = new StringBuilder();
            List<string> row = null;

            for (var i = 0; i < valuesPart.Length; i++)
            {
                var c = valuesPart[i];

                if (inQuote)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < valuesPart.Length && valuesPart[i + 1] == '\'')
                        {
                            field.Append('\'');
                            i++;
                            continue;
                        }

                        inQuote = false;
                        continue;
                    }

                    field.Append(c);
                    continue;
                }

                if (c == '\'')
                {
                    inQuote = true;
                    continue;
                }

                if (c == '(')
                {
                    depth++;

                    if (depth == 1)
                    {
                        row = new List<string>();
                        field.Clear();
                        continue;
                    }
                }
                else if (c == ')')
                {
                    depth--;

                    if (depth == 0)
                    {
                        row.Add(field.ToString().Trim());
                        rows.Add(row);
                        row = null;
                        continue;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    row.Add(field.ToString().Trim());
                    field.Clear();
                    continue;
                }

                if (depth >= 1)
                    field.Append(c);
            }

            return rows;
        }

        private static Weenie Parse(int wcid, string text)
        {
            var w = new Weenie { Wcid = wcid };
            var statements = SplitStatements(StripComments(text));

            foreach (var raw in statements)
            {
                var stmt = raw.Trim();
                var m = Regex.Match(stmt, @"^INSERT\s+INTO\s+`(\w+)`\s*\(([^)]*)\)\s*VALUES", RegexOptions.IgnoreCase);

                if (!m.Success)
                    continue;

                var table = m.Groups[1].Value;
                var cols = m.Groups[2].Value.Split(',').Select(c => c.Trim().Trim('`')).ToList();
                var rows = ParseRows(stmt.Substring(m.Length));

                foreach (var r in rows)
                {
                    Assert.AreEqual(cols.Count, r.Count,
                        $"{wcid}: a `{table}` row has {r.Count} fields but its INSERT header names {cols.Count} columns: ({string.Join(", ", r)})");

                    // the emote rows key on @parent_id, which is not a wcid; skip anything non-numeric
                    if (!int.TryParse(r[0], out var id) || id != wcid)
                        continue;

                    switch (table)
                    {
                        case "weenie":
                            w.WeenieType = int.Parse(r[cols.IndexOf("type")]);
                            break;
                        case "weenie_properties_int":
                            w.Ints[int.Parse(r[1])] = int.Parse(r[2]);
                            break;
                        case "weenie_properties_bool":
                            w.Bools[int.Parse(r[1])] = r[2].Equals("True", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "weenie_properties_float":
                            w.Floats[int.Parse(r[1])] = double.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case "weenie_properties_string":
                            w.Strings[int.Parse(r[1])] = r[2];
                            break;
                        case "weenie_properties_d_i_d":
                            w.Dids[int.Parse(r[1])] = Convert.ToUInt32(r[2].Substring(2), 16);   // 0xXXXXXXXX
                            break;
                        case "weenie_properties_skill":
                            w.SkillFull[int.Parse(r[cols.IndexOf("type")])] = string.Join(",", cols.Select((c, i) => (c, i)).Where(x => x.c != "object_Id").Select(x => r[x.i]));
                            w.Skills[int.Parse(r[cols.IndexOf("type")])] = new SkillRow
                            {
                                Sac = int.Parse(r[cols.IndexOf("s_a_c")]),
                                InitLevel = int.Parse(r[cols.IndexOf("init_Level")]),
                            };
                            break;
                        case "weenie_properties_attribute_2nd":
                            w.Vitals[int.Parse(r[cols.IndexOf("type")])] = r[cols.IndexOf("init_Level")] + "/" + r[cols.IndexOf("current_Level")];
                            break;
                        case "weenie_properties_attribute":
                            w.Attributes[int.Parse(r[cols.IndexOf("type")])] = r[cols.IndexOf("init_Level")];
                            break;
                        case "weenie_properties_body_part":
                            w.BodyParts[int.Parse(r[cols.IndexOf("key")])] = string.Join(",", cols.Select((c, i) => (c, i)).Where(x => x.c != "object_Id" && x.c != "key" && x.c != "d_Type").Select(x => r[x.i]));
                            break;
                        case "weenie_properties_create_list":
                            w.CreateList.Add(new Row
                            {
                                DestinationType = int.Parse(r[cols.IndexOf("destination_Type")]),
                                Wcid = int.Parse(r[cols.IndexOf("weenie_Class_Id")]),
                            });
                            break;
                        case "weenie_properties_spell_book":
                            w.SpellBookRows++;
                            break;
                    }
                }
            }

            Assert.IsTrue(w.WeenieType != 0, $"{wcid}: parsed no weenie row.");
            return w;
        }
    }
}
