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
    /// their pets (1007700-1007705), eleven level-225 devices and pets (1007706-1007727), their weapons and
    /// ammo, the Sacrificial Dagger, the Duo Summon Gem, the Summon Gem Hammer, and Vardo's stock rows.
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

        private static readonly int[] CasterDevices = { 1007700, 1007701, 1007702 };
        private static readonly int[] CasterPets = { 1007703, 1007704, 1007705 };
        private static readonly int[] CasterMagicSkill = { 400, 500, 600 };

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

            foreach (var wcid in Level225Devices.Append(DuoGemWcid))
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
        /// The caster pets' MotionTable 0x09000017 has no Magic stance. That is harmless only while they carry no wand,
        /// so no caster pet's create list may hold an item whose WeenieType is Caster.
        /// </summary>
        [TestMethod]
        public void Casters_CarryNoWand()
        {
            var problems = new List<string>();

            foreach (var wcid in CasterPets)
            {
                foreach (var row in Load(wcid).CreateList)
                {
                    if (Load(row.Wcid).WeenieType == (int)ACE.Entity.Enum.WeenieType.Caster)
                        problems.Add($"{wcid}: create list carries wcid {row.Wcid}, a Caster (wand); the caster MotionTable has no Magic stance.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
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
        public void Tanks_Taunt_EveryTenSeconds_AndNobodyElseDoes()
        {
            var problems = new List<string>();

            foreach (var wcid in AllWcids())
            {
                var has = Load(wcid).Floats.TryGetValue(9021, out var v);

                if (TankPets.Contains(wcid))
                {
                    if (!has || Math.Abs(v - 10.0) > 1e-9)
                        problems.Add($"{wcid}: a Tank pet must carry SummonTauntInterval (9021) 10.");
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

        // PropertyDataId.MotionTable (2) per archetype. The Warden and Marksman keep the Skeleton Lord table; the Reaver cannot:
        // 0x09000025 has no DualWieldCombat stance (dat probe 2026-11-02), so a dual wielder on it never gets a combat maneuver
        // and never swings. 0x09000001 is the table every retail dual-wield creature uses.
        private const uint SkeletonLordMotionTable = 0x09000025;
        private const uint HumanoidDualWieldMotionTable = 0x09000001;

        private static IEnumerable<(string Archetype, int[] Pets, uint MotionTable, ACE.Entity.Enum.MotionStance Stance)> ArchetypeStances() => new[]
        {
            ("Warden", TankPets, SkeletonLordMotionTable, ACE.Entity.Enum.MotionStance.SwordCombat),
            ("Marksman", ArcherPets, SkeletonLordMotionTable, ACE.Entity.Enum.MotionStance.BowCombat),
            ("Reaver", MeleePets, HumanoidDualWieldMotionTable, ACE.Entity.Enum.MotionStance.DualWieldCombat),
        };

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

            // every Warden weapon must resolve to the SwordCombat stance, the bow to BowCombat (Creature_Combat.GetWeaponStance)
            foreach (var wcid in TankPets)
            {
                foreach (var row in Load(wcid).CreateList)
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

                    // the missile path does not use the combat maneuver table; the melee stances do
                    if (stance != ACE.Entity.Enum.MotionStance.BowCombat && !combatTable.Stances.ContainsKey(stance))
                        problems.Add($"{wcid} ({archetype}): combat table 0x30000000 has no {stance} stance.");
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
            return Enumerable.Range(1007700, 51).Where(w => w != 1007739);
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
