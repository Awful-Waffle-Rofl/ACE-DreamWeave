using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Snapshot;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// End to end for the player side: a synthetic TSV snapshot -> ProfileBuilder.HydrateFromSnapshot ->
    /// PlayerAttackerSpecs.FromPlayer and ProfileBuilder.BuildDefenderProfile, with the melee spec checked
    /// against the engine's own DamageEvent for the same hydrated player.
    ///
    /// DATABASE-GATED: the Player restore constructor calls DatabaseManager.Authentication.GetAccountById
    /// unconditionally (WorldObjects\Player.cs, the restore ctor), so no Player can be built without a reachable
    /// auth database. HydrateFromSnapshot performs that one read and nothing else; nothing is written.
    ///
    /// DAT-GATED too: Player.SetEphemeralValues reads the CombatManeuverTable named by CombatTableDID from
    /// client_portal.dat, so DatManager.PortalDat must be loaded. Same gate as BadSetupDidTests.RequireDats.
    /// </summary>
    [TestClass]
    public class CombatSimulatorPlayerSnapshotTests
    {
        private const uint CharacterId = 0x50000101;
        private const uint Sword = 0x80000201;

        private const float Epsilon = 0.0005f;

        private static uint nextWcid = 996500;
        private static uint nextGuid = 0x7E210000;

        /// <summary>The combat maneuver table retail player characters carry (PropertyDataId.CombatTable).</summary>
        private const uint PlayerCombatTable = 0x30000000;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        private string dir;

        private static void RequireDats()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = InitializeDats();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;

            var candidates = new List<string>();

            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                using (new FileStream(Path.Combine(datDir, "client_portal.dat"), FileMode.Open, FileAccess.Read))
                { }
            }
            catch (IOException ex)
            {
                return $"Skipped: client_portal.dat in {datDir} could not be opened for reading ({ex.Message}).";
            }

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat." : null;
        }

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        [TestInitialize]
        public void Setup()
        {
            dir = Path.Combine(Path.GetTempPath(), "ace_player_snapshot_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private void WriteTable(string table, params string[] lines)
        {
            File.WriteAllText(Path.Combine(dir, table + ".tsv"), lines.Length == 0 ? "" : string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        /// <summary>A level 200 swordsman wielding one slashing sword. Headers spelled as the mysql client prints them.</summary>
        private void WriteSwordsman()
        {
            WriteTable("character",
                "id\taccount_Id\tname\tis_Plussed\tis_Deleted\tdelete_Time\tlast_Login_Timestamp\ttotal_Logins\tcharacter_Options_1\tcharacter_Options_2\tgameplay_Options\tspellbook_Filters\thair_Texture\tdefault_Hair_Texture",
                $"{CharacterId}\t0\tSimSwordsman\t0\t0\t0\t0\t1\t0\t0\tNULL\t16383\t0\t0");

            WriteTable("biota",
                "id\tweenie_Class_Id\tweenie_Type\tpopulated_Collection_Flags",
                $"{CharacterId}\t1\t{(int)WeenieType.Creature}\t4294967295",
                $"{Sword}\t3000\t{(int)WeenieType.MeleeWeapon}\t4294967295");

            WriteTable("biota_properties_i_i_d",
                "object_Id\ttype\tvalue",
                $"{Sword}\t{(int)PropertyInstanceId.Wielder}\t{CharacterId}");

            WriteTable("biota_properties_int",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyInt.Level}\t200",
                $"{Sword}\t{(int)PropertyInt.CurrentWieldedLocation}\t{(int)EquipMask.MeleeWeapon}",
                $"{Sword}\t{(int)PropertyInt.ValidLocations}\t{(int)EquipMask.MeleeWeapon}",
                $"{Sword}\t{(int)PropertyInt.WeaponSkill}\t{(int)Skill.HeavyWeapons}",
                $"{Sword}\t{(int)PropertyInt.DamageType}\t{(int)DamageType.Slash}",
                $"{Sword}\t{(int)PropertyInt.Damage}\t40",
                $"{Sword}\t{(int)PropertyInt.DefaultCombatStyle}\t{(int)CombatStyle.OneHanded}",
                $"{Sword}\t{(int)PropertyInt.WeaponTime}\t40");

            WriteTable("biota_properties_d_i_d",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyDataId.CombatTable}\t{PlayerCombatTable}");

            WriteTable("biota_properties_float",
                "object_Id\ttype\tvalue",
                $"{Sword}\t{(int)PropertyFloat.DamageVariance}\t0.5",
                $"{Sword}\t{(int)PropertyFloat.WeaponDefense}\t1.2",
                $"{Sword}\t{(int)PropertyFloat.WeaponOffense}\t1.1");

            WriteTable("biota_properties_string",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyString.Name}\tSimSwordsman",
                $"{Sword}\t{(int)PropertyString.Name}\tSim Sword");

            WriteTable("biota_properties_attribute",
                "object_Id\ttype\tinit_Level\tlevel_From_C_P\tc_P_Spent",
                $"{CharacterId}\t{(int)PropertyAttribute.Strength}\t200\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Endurance}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Coordination}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Quickness}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Focus}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Self}\t100\t0\t0");

            WriteTable("biota_properties_attribute_2nd",
                "object_Id\ttype\tinit_Level\tlevel_From_C_P\tc_P_Spent\tcurrent_Level",
                $"{CharacterId}\t{(int)PropertyAttribute2nd.MaxHealth}\t300\t0\t0\t350",
                $"{CharacterId}\t{(int)PropertyAttribute2nd.MaxStamina}\t300\t0\t0\t400",
                $"{CharacterId}\t{(int)PropertyAttribute2nd.MaxMana}\t300\t0\t0\t400");

            WriteTable("biota_properties_skill",
                "object_Id\ttype\tlevel_From_P_P\ts_a_c\tp_p\tinit_Level\tresistance_At_Last_Check\tlast_Used_Time",
                $"{CharacterId}\t{(int)Skill.HeavyWeapons}\t200\t{(int)SkillAdvancementClass.Specialized}\t0\t10\t0\t0",
                $"{CharacterId}\t{(int)Skill.MeleeDefense}\t150\t{(int)SkillAdvancementClass.Trained}\t0\t5\t0\t0");
        }

        private static Creature MakeTarget()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = 5000 } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Chest, new PropertiesBodyPart { DType = DamageType.Bludgeon, DVal = 1, DVar = 0.5f, BaseArmor = 50, MLF = 1, MRF = 1, MLB = 1, MRB = 1 } },
                },
            };

            var target = new Creature(weenie, new ObjectGuid(nextGuid++));
            target.Location = new Position(0x00010064, 55.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            Creature.SetBodyPartTable(target.WeenieClassId, new BodyPartTable(weenie));

            return target;
        }

        [TestMethod]
        public void Snapshot_HydratesAPlayerWhoseMeleeSpecMatchesTheEngine()
        {
            TestEnvironment.RequireDatabases();
            RequireDats();

            WriteSwordsman();

            var snapshot = ShardTsvSnapshotReader.Load(dir);
            var player = ProfileBuilder.HydrateFromSnapshot(snapshot);

            Assert.AreEqual("SimSwordsman", player.Name);
            Assert.AreEqual(CombatMode.NonCombat, player.CombatMode, "a hydrated Player starts out of combat");

            var sword = player.GetEquippedMeleeWeapon();
            Assert.IsNotNull(sword, "the wielded snapshot biota is equipped");

            var target = MakeTarget();
            player.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            var spec = PlayerAttackerSpecs.FromPlayer(player, PlayerAttackMode.Melee, target: target);

            Assert.AreEqual(CombatMode.NonCombat, player.CombatMode, "FromPlayer restores the previous stance");
            Assert.AreEqual(CombatType.Melee, spec.CombatType);
            Assert.AreEqual(DamageType.Slash, spec.DamageType);
            Assert.AreEqual(40.0f, spec.BaseDamageMax, Epsilon);
            Assert.AreEqual(20.0f, spec.BaseDamageMin, Epsilon);
            Assert.IsTrue(spec.AttackerIsPlayer);

            // the engine, measured from the same stance and the same full power bar
            DamageEvent ev = null;

            SimulatorStance.With(player, CombatMode.Melee, () =>
            {
                var previousPower = player.PowerLevel;
                player.PowerLevel = 1.0f;

                try
                {
                    for (var i = 0; i < 2000 && (ev == null || !ev.HasDamage || ev.IsCritical); i++)
                        ev = DamageEvent.CalculateDamage(player, target, sword);
                }
                finally
                {
                    player.PowerLevel = previousPower;
                }

                return 0;
            });

            Assert.IsNotNull(ev);
            Assert.IsTrue(ev.HasDamage && !ev.IsCritical, "the engine produced a non-crit landed hit");
            Assert.AreEqual(ev.BaseDamageMod.MaxDamage, spec.BaseDamageMax, Epsilon);
            Assert.AreEqual(ev.BaseDamageMod.MinDamage, spec.BaseDamageMin, Epsilon);
            Assert.AreEqual(ev.AttributeMod * ev.PowerMod * ev.SlayerMod * ev.DamageRatingMod, spec.PreMitigationMod, Epsilon);
            Assert.AreEqual(ev.CriticalChance, spec.CritChance, Epsilon);
            Assert.AreEqual(ev.EffectiveAttackSkill, spec.AttackSkill);
        }

        /// <summary>
        /// A hydrated Player has no PhysicsObj (only Landblock's add-to-world path calls InitPhysicsObj), and the
        /// defender walk reaches Player.IsJumping through GetEffectiveDefenseSkill -> GetDefenseStanceMod. This
        /// used to throw NullReferenceException for every hydrated Player, BuildAsync's offline path included;
        /// IsJumping now treats a missing PhysicsObj as not jumping.
        ///
        /// Also pins the default stance: with no stance given the walk runs in the character's combat stance
        /// (Melee for this swordsman), so the sword's WeaponDefense 1.2 is in the melee defense skill, which an
        /// explicit NonCombat walk drops. And the Player is back in NonCombat afterwards either way.
        /// </summary>
        [TestMethod]
        public void Snapshot_DefenderWalkRunsWithoutAPhysicsObject_InTheCharactersCombatStance()
        {
            TestEnvironment.RequireDatabases();
            RequireDats();

            WriteSwordsman();

            var player = ProfileBuilder.HydrateFromSnapshot(ShardTsvSnapshotReader.Load(dir));

            Assert.IsNull(player.PhysicsObj, "precondition: a hydrated Player is not in the world");
            Assert.IsFalse(player.IsJumping);
            Assert.AreEqual(CombatMode.Melee, ProfileBuilder.DefenderCombatStance(player));

            var byDefault = ProfileBuilder.BuildDefenderProfile(player);
            var inMelee = ProfileBuilder.BuildDefenderProfile(player, CombatMode.Melee);
            var outOfCombat = ProfileBuilder.BuildDefenderProfile(player, CombatMode.NonCombat);

            Assert.AreEqual(CombatMode.NonCombat, player.CombatMode, "the stance override is restored");

            Assert.AreEqual(inMelee.DefenseSkills[CombatType.Melee], byDefault.DefenseSkills[CombatType.Melee], "the default is the character's own stance");
            Assert.IsTrue(byDefault.DefenseSkills[CombatType.Melee] > outOfCombat.DefenseSkills[CombatType.Melee],
                $"melee defense in stance {byDefault.DefenseSkills[CombatType.Melee]} vs NonCombat {outOfCombat.DefenseSkills[CombatType.Melee]}");

            // the engine's own read, in the same stance, is what the profile stores
            var engine = SimulatorStance.With(player, CombatMode.Melee, () => player.GetEffectiveDefenseSkill(CombatType.Melee));
            Assert.AreEqual(engine, byDefault.DefenseSkills[CombatType.Melee]);
        }
        private const uint Wand = 0x80000301;
        private const uint GoodDagger = 0x80000302;
        private const uint BigDaggerTooHigh = 0x80000303;
        private const uint WeakDagger = 0x80000304;

        /// <summary>
        /// A finesse fighter that logged out holding a wand: wand in the Held slot, three finesse weapons in the
        /// main pack - a good one, a better one it cannot wield (RawSkill requirement 9999), and a weak one.
        /// War magic untrained, so the wand is not its attack.
        /// </summary>
        private void WriteWandHoldingFencer()
        {
            WriteTable("character",
                "id\taccount_Id\tname\tis_Plussed\tis_Deleted\tdelete_Time\tlast_Login_Timestamp\ttotal_Logins\tcharacter_Options_1\tcharacter_Options_2\tgameplay_Options\tspellbook_Filters\thair_Texture\tdefault_Hair_Texture",
                $"{CharacterId}\t0\tSimFencer\t0\t0\t0\t0\t1\t0\t0\tNULL\t16383\t0\t0");

            WriteTable("biota",
                "id\tweenie_Class_Id\tweenie_Type\tpopulated_Collection_Flags",
                $"{CharacterId}\t1\t{(int)WeenieType.Creature}\t4294967295",
                $"{Wand}\t2472\t{(int)WeenieType.Caster}\t4294967295",
                $"{GoodDagger}\t3100\t{(int)WeenieType.MeleeWeapon}\t4294967295",
                $"{BigDaggerTooHigh}\t3101\t{(int)WeenieType.MeleeWeapon}\t4294967295",
                $"{WeakDagger}\t3102\t{(int)WeenieType.MeleeWeapon}\t4294967295");

            WriteTable("biota_properties_i_i_d",
                "object_Id\ttype\tvalue",
                $"{Wand}\t{(int)PropertyInstanceId.Wielder}\t{CharacterId}",
                $"{GoodDagger}\t{(int)PropertyInstanceId.Container}\t{CharacterId}",
                $"{BigDaggerTooHigh}\t{(int)PropertyInstanceId.Container}\t{CharacterId}",
                $"{WeakDagger}\t{(int)PropertyInstanceId.Container}\t{CharacterId}");

            string Dagger(uint id, int damage) => string.Join("\n",
                $"{id}\t{(int)PropertyInt.ValidLocations}\t{(int)EquipMask.MeleeWeapon}",
                $"{id}\t{(int)PropertyInt.WeaponSkill}\t{(int)Skill.FinesseWeapons}",
                $"{id}\t{(int)PropertyInt.DamageType}\t{(int)DamageType.Pierce}",
                $"{id}\t{(int)PropertyInt.Damage}\t{damage}",
                $"{id}\t{(int)PropertyInt.DefaultCombatStyle}\t{(int)CombatStyle.OneHanded}",
                $"{id}\t{(int)PropertyInt.WeaponTime}\t30");

            WriteTable("biota_properties_int",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyInt.Level}\t200",
                $"{Wand}\t{(int)PropertyInt.CurrentWieldedLocation}\t{(int)EquipMask.Held}",
                $"{Wand}\t{(int)PropertyInt.ValidLocations}\t{(int)EquipMask.Held}",
                Dagger(GoodDagger, 30),
                Dagger(BigDaggerTooHigh, 90),
                $"{BigDaggerTooHigh}\t{(int)PropertyInt.WieldRequirements}\t{(int)WieldRequirement.RawSkill}",
                $"{BigDaggerTooHigh}\t{(int)PropertyInt.WieldSkillType}\t{(int)Skill.FinesseWeapons}",
                $"{BigDaggerTooHigh}\t{(int)PropertyInt.WieldDifficulty}\t9999",
                Dagger(WeakDagger, 10));

            WriteTable("biota_properties_float",
                "object_Id\ttype\tvalue",
                $"{GoodDagger}\t{(int)PropertyFloat.DamageVariance}\t0.5",
                $"{BigDaggerTooHigh}\t{(int)PropertyFloat.DamageVariance}\t0.5",
                $"{WeakDagger}\t{(int)PropertyFloat.DamageVariance}\t0.5",
                $"{Wand}\t{(int)PropertyFloat.WeaponDefense}\t1.0");

            WriteTable("biota_properties_d_i_d",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyDataId.CombatTable}\t{PlayerCombatTable}");

            WriteTable("biota_properties_string",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyString.Name}\tSimFencer",
                $"{Wand}\t{(int)PropertyString.Name}\tSim Wand",
                $"{GoodDagger}\t{(int)PropertyString.Name}\tGood Dagger",
                $"{BigDaggerTooHigh}\t{(int)PropertyString.Name}\tBig Dagger",
                $"{WeakDagger}\t{(int)PropertyString.Name}\tWeak Dagger");

            WriteTable("biota_properties_attribute",
                "object_Id\ttype\tinit_Level\tlevel_From_C_P\tc_P_Spent",
                $"{CharacterId}\t{(int)PropertyAttribute.Strength}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Endurance}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Coordination}\t200\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Quickness}\t200\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Focus}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Self}\t100\t0\t0");

            WriteTable("biota_properties_skill",
                "object_Id\ttype\tlevel_From_P_P\ts_a_c\tp_p\tinit_Level\tresistance_At_Last_Check\tlast_Used_Time",
                $"{CharacterId}\t{(int)Skill.FinesseWeapons}\t0\t{(int)SkillAdvancementClass.Specialized}\t0\t300\t0\t0",
                $"{CharacterId}\t{(int)Skill.LifeMagic}\t0\t{(int)SkillAdvancementClass.Trained}\t0\t100\t0\t0",
                $"{CharacterId}\t{(int)Skill.WarMagic}\t0\t{(int)SkillAdvancementClass.Untrained}\t0\t0\t0\t0");
        }

        [TestMethod]
        public void Primary_FightsWithThePacksBestWieldableWeapon_NotTheLogoutWand_AndRestoresTheHands()
        {
            TestEnvironment.RequireDatabases();
            RequireDats();

            WriteWandHoldingFencer();

            var player = ProfileBuilder.HydrateFromSnapshot(ShardTsvSnapshotReader.Load(dir));
            var target = MakeTarget();
            player.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            Assert.IsNotNull(player.GetEquippedWand(), "precondition: logged out holding the wand");
            Assert.IsNull(player.GetEquippedMeleeWeapon(), "precondition: no melee weapon in hand");
            Assert.AreEqual(CombatMode.Magic, ProfileBuilder.DefenderCombatStance(player), "the DEFENDER stance still follows what is in hand");

            Assert.AreEqual(Skill.FinesseWeapons, PlayerLoadout.PrimaryAttackSkill(player));
            Assert.AreEqual(PlayerAttackMode.Melee, PlayerAttackerSpecs.PrimaryAttackMode(player), "skill decides the mode, not the wand");

            var loadout = PlayerAttackerSpecs.PrimaryLoadout(player, target);
            Assert.AreEqual("Good Dagger", loadout.Weapon?.Name, "the best weapon it CAN wield: Big Dagger fails its wield requirement, Weak Dagger hits softer");

            var primary = PlayerAttackerSpecs.Primary(player, target);
            Assert.AreEqual(CombatType.Melee, primary.CombatType);
            Assert.AreEqual(DamageType.Pierce, primary.DamageType);
            Assert.AreEqual(30.0f, primary.BaseDamageMax, Epsilon);
            StringAssert.Contains(primary.Label, "Good Dagger");

            // everything put back: the wand in hand, the daggers in the pack and not wielded
            Assert.IsNotNull(player.GetEquippedWand(), "the wand is back in hand");
            Assert.IsNull(player.GetEquippedMeleeWeapon());
            Assert.IsFalse(player.EquippedObjects.ContainsKey(new ObjectGuid(GoodDagger)));

            var good = player.GetAllPossessions().First(i => i.Guid.Full == GoodDagger);
            Assert.IsNull(good.CurrentWieldedLocation);
            Assert.IsNull(good.Wielder);
            Assert.AreEqual(CombatMode.NonCombat, player.CombatMode);
        }
        private const uint Darts = 0x80000311;

        /// <summary>A missile thrower holding a stack of thrown darts (a thrown weapon is its own ammo).</summary>
        private void WriteDartThrower()
        {
            WriteTable("character",
                "id\taccount_Id\tname\tis_Plussed\tis_Deleted\tdelete_Time\tlast_Login_Timestamp\ttotal_Logins\tcharacter_Options_1\tcharacter_Options_2\tgameplay_Options\tspellbook_Filters\thair_Texture\tdefault_Hair_Texture",
                $"{CharacterId}\t0\tSimThrower\t0\t0\t0\t0\t1\t0\t0\tNULL\t16383\t0\t0");

            WriteTable("biota",
                "id\tweenie_Class_Id\tweenie_Type\tpopulated_Collection_Flags",
                $"{CharacterId}\t1\t{(int)WeenieType.Creature}\t4294967295",
                $"{Darts}\t3200\t{(int)WeenieType.Missile}\t4294967295");

            WriteTable("biota_properties_i_i_d",
                "object_Id\ttype\tvalue",
                $"{Darts}\t{(int)PropertyInstanceId.Wielder}\t{CharacterId}");

            WriteTable("biota_properties_int",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyInt.Level}\t200",
                $"{Darts}\t{(int)PropertyInt.CurrentWieldedLocation}\t{(int)EquipMask.MissileWeapon}",
                $"{Darts}\t{(int)PropertyInt.ValidLocations}\t{(int)EquipMask.MissileWeapon}",
                $"{Darts}\t{(int)PropertyInt.WeaponSkill}\t{(int)Skill.MissileWeapons}",
                $"{Darts}\t{(int)PropertyInt.DamageType}\t{(int)DamageType.Pierce}",
                $"{Darts}\t{(int)PropertyInt.Damage}\t20",
                $"{Darts}\t{(int)PropertyInt.DefaultCombatStyle}\t{(int)CombatStyle.ThrownWeapon}",
                $"{Darts}\t{(int)PropertyInt.StackSize}\t50",
                $"{Darts}\t{(int)PropertyInt.WeaponTime}\t30");

            WriteTable("biota_properties_float",
                "object_Id\ttype\tvalue",
                $"{Darts}\t{(int)PropertyFloat.DamageVariance}\t0.5");

            WriteTable("biota_properties_d_i_d",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyDataId.CombatTable}\t{PlayerCombatTable}");

            WriteTable("biota_properties_string",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyString.Name}\tSimThrower",
                $"{Darts}\t{(int)PropertyString.Name}\tSim Darts");

            WriteTable("biota_properties_attribute",
                "object_Id\ttype\tinit_Level\tlevel_From_C_P\tc_P_Spent",
                $"{CharacterId}\t{(int)PropertyAttribute.Strength}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Endurance}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Coordination}\t200\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Quickness}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Focus}\t100\t0\t0",
                $"{CharacterId}\t{(int)PropertyAttribute.Self}\t100\t0\t0");

            WriteTable("biota_properties_skill",
                "object_Id\ttype\tlevel_From_P_P\ts_a_c\tp_p\tinit_Level\tresistance_At_Last_Check\tlast_Used_Time",
                $"{CharacterId}\t{(int)Skill.MissileWeapons}\t0\t{(int)SkillAdvancementClass.Specialized}\t0\t300\t0\t0");
        }

        /// <summary>
        /// Code review 2026-10-23 (F1): a thrown weapon is its own ammo (Candidates gives Weapon == Ammo). Placing it
        /// twice used to overwrite its MissileWeapon slot with MissileAmmo, so GetEquippedMissileWeapon returned null and
        /// the Missile spec threw out of RankedLoadouts, failing PrimaryLoadout and everything built on it; the forward
        /// restore then put back the SECOND (already placed) capture. Now: placed once, restored exactly.
        /// </summary>
        [TestMethod]
        public void Primary_ThrownWeaponIsItsOwnAmmo_BuildsAMissileSpec_AndRestoresTheItemExactly()
        {
            TestEnvironment.RequireDatabases();
            RequireDats();

            WriteDartThrower();

            var player = ProfileBuilder.HydrateFromSnapshot(ShardTsvSnapshotReader.Load(dir));
            var target = MakeTarget();
            player.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            var darts = player.GetAllPossessions().First(i => i.Guid.Full == Darts);
            var slotBefore = darts.CurrentWieldedLocation;
            var parentBefore = darts.ParentLocation;
            var wielderBefore = darts.Wielder;
            var equippedBefore = player.EquippedObjects.ContainsKey(darts.Guid);

            Assert.AreEqual(Skill.MissileWeapons, PlayerLoadout.PrimaryAttackSkill(player));
            Assert.IsFalse(darts.IsAmmoLauncher, "fixture: a thrown weapon, not a launcher");

            var loadout = PlayerAttackerSpecs.PrimaryLoadout(player, target);
            Assert.IsNotNull(loadout, "the thrown weapon is a candidate and its spec builds");
            Assert.AreSame(darts, loadout.Weapon);
            Assert.AreSame(darts, loadout.Ammo, "its own ammo");

            var spec = PlayerAttackerSpecs.Primary(player, target);
            Assert.AreEqual(CombatType.Missile, spec.CombatType);
            StringAssert.Contains(spec.Label, "Sim Darts");

            Assert.IsNotNull(ProfileBuilder.BuildDefenderProfile(player), "the defender walk (which ranks the loadouts) does not throw either");

            Assert.AreEqual(slotBefore, darts.CurrentWieldedLocation, "CurrentWieldedLocation restored");
            Assert.AreEqual(parentBefore, darts.ParentLocation, "ParentLocation restored");
            Assert.AreSame(wielderBefore, darts.Wielder, "Wielder restored");
            Assert.AreEqual(equippedBefore, player.EquippedObjects.ContainsKey(darts.Guid));
            Assert.AreSame(darts, player.GetEquippedMissileWeapon(), "still the wielded missile weapon afterwards");
        }

        /// <summary>
        /// Optional end-to-end smoke over a REAL exported snapshot, which never lives in the repo: set
        /// ACE_SIM_SNAPSHOT_DIR to its directory. Inconclusive when unset. Prints a build summary - wielded items,
        /// combat skills, vitals, the defender profile's armor and resistances - and the character's PRIMARY
        /// attacker spec, plus every other attack mode that applies. Output only; the assertions are structural.
        /// </summary>
        [TestMethod]
        public void RealSnapshot_DescribesTheBuildAndItsPrimaryAttack_WhenDirectoryIsProvided()
        {
            var snapshotDir = Environment.GetEnvironmentVariable("ACE_SIM_SNAPSHOT_DIR");

            if (string.IsNullOrWhiteSpace(snapshotDir))
                Assert.Inconclusive("Set ACE_SIM_SNAPSHOT_DIR to an exported snapshot directory to run this smoke test.");

            TestEnvironment.RequireDatabases();
            RequireDats();

            // A real character needs the REAL formula tables: TestGameTables installs a synthetic SkillTable that
            // carries only the retired skills and the two physical defenses, so every other skill's attribute
            // formula reads 0 there. Swap the portal dat's tables in for this test (as Program.cs does at boot)
            // and put the synthetic ones back after, because GameTables is process-wide static state.
            var syntheticSkills = GameTables.SkillTable;
            var syntheticVitals = GameTables.SecondaryAttributeTable;

            GameTables.Initialize(DatManager.PortalDat.SkillTable, DatManager.PortalDat.SecondaryAttributeTable);

            try
            {
                DescribeRealSnapshot(snapshotDir);
            }
            finally
            {
                GameTables.Initialize(syntheticSkills, syntheticVitals);
            }
        }

        private static void DescribeRealSnapshot(string snapshotDir)
        {
            var player = ProfileBuilder.HydrateFromSnapshot(ShardTsvSnapshotReader.Load(snapshotDir));
            var target = MakeTarget();

            player.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            Console.WriteLine($"{player.Name} level {player.Level}, heritage {player.HeritageGroup}: equipped {player.EquippedObjects.Count}, top-level inventory {player.Inventory.Count}");

            Console.WriteLine("WIELDED (weapons and casters):");
            foreach (var item in player.EquippedObjects.Values.Where(i => i.CurrentWieldedLocation != null
                && (i.CurrentWieldedLocation.Value & (EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.MissileAmmo | EquipMask.Held | EquipMask.TwoHanded | EquipMask.Shield)) != 0))
            {
                Console.WriteLine($"  {item.Name} wcid {item.WeenieClassId} {item.WeenieType} at {item.CurrentWieldedLocation}: " +
                                  $"Damage {item.GetProperty(PropertyInt.Damage)} DamageMod {item.GetProperty(PropertyFloat.DamageMod)} " +
                                  $"ElementalDamageMod {item.GetProperty(PropertyFloat.ElementalDamageMod)} Variance {item.GetProperty(PropertyFloat.DamageVariance)} " +
                                  $"WeaponDefense {item.GetProperty(PropertyFloat.WeaponDefense)} CritFreq {item.GetProperty(PropertyFloat.CriticalFrequency)} " +
                                  $"CritMult {item.GetProperty(PropertyFloat.CriticalMultiplier)} Slayer {item.GetProperty(PropertyInt.SlayerCreatureType)}x{item.GetProperty(PropertyFloat.SlayerDamageBonus)} " +
                                  $"Imbue {item.ImbuedEffect}");
            }

            Console.WriteLine("SKILLS (Current with buffs, Base, class):");
            foreach (var skill in new[] { Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.TwoHandedCombat, Skill.UnarmedCombat,
                                          Skill.MissileWeapons, Skill.WarMagic, Skill.VoidMagic, Skill.LifeMagic,
                                          Skill.MeleeDefense, Skill.MissileDefense, Skill.MagicDefense, Skill.Shield, Skill.DualWield })
            {
                var cs = player.GetCreatureSkill(skill);
                Console.WriteLine($"  {skill}: {cs.Current} (base {cs.Base} = formula {(cs.IsUsable ? AttributeFormula.GetFormula(player, skill, false) : 0)} + init {cs.InitLevel} + ranks {cs.Ranks} " +
                                  $"+ aug {cs.GetAugBonus_Base(player)} + enhanced {player.GetEnhancedSkillBonus(skill)}) {cs.AdvancementClass}");
            }

            Console.WriteLine("ATTRIBUTES (current / base): " + string.Join(", ", player.Attributes.Values.Select(a => $"{a.Attribute} {a.Current}/{a.Base}")));

            Console.WriteLine("PACK WEAPONS AND CASTERS (wield requirement 1 vs this character; Skill reads Current, RawSkill reads Base):");
            foreach (var item in player.GetAllPossessions().Where(i => i is MeleeWeapon || i is MissileLauncher || i is Caster))
            {
                var req = item.WieldRequirements;
                var skillType = (Skill)(item.WieldSkillType ?? 0);
                var difficulty = (uint)(item.WieldDifficulty ?? 0);
                string verdict;

                switch (req)
                {
                    case WieldRequirement.Skill: verdict = player.GetCreatureSkill(skillType).Current >= difficulty ? "ok" : "TOO LOW"; break;
                    case WieldRequirement.RawSkill: verdict = player.GetCreatureSkill(skillType).Base >= difficulty ? "ok" : "TOO LOW"; break;
                    case WieldRequirement.Level: verdict = player.Level >= difficulty ? "ok" : "TOO LOW"; break;
                    case WieldRequirement.Invalid: verdict = "none"; break;
                    default: verdict = "not evaluated"; break;
                }

                Console.WriteLine($"  {item.Name} wcid {item.WeenieClassId} {item.WeenieType} skill {item.WeaponSkill} type {item.W_DamageType} " +
                                  $"dmg {item.GetProperty(PropertyInt.Damage)} var {item.GetProperty(PropertyFloat.DamageVariance)} dmgMod {item.GetProperty(PropertyFloat.DamageMod)} " +
                                  $"elem {item.GetProperty(PropertyFloat.ElementalDamageMod)} off {item.GetProperty(PropertyFloat.WeaponOffense)} | req {req} {skillType} {difficulty}: {verdict}");
            }

            Console.WriteLine($"VITALS (max with buffs / unbuffed): health {player.Health.MaxValue}/{player.Health.GetMaxValue(false)} " +
                              $"stamina {player.Stamina.MaxValue}/{player.Stamina.GetMaxValue(false)} mana {player.Mana.MaxValue}/{player.Mana.GetMaxValue(false)}");

            var stance = ProfileBuilder.DefenderCombatStance(player);
            var profile = ProfileBuilder.BuildDefenderProfile(player);

            Console.WriteLine($"DEFENDER PROFILE in {stance}: melee def {profile.DefenseSkills[CombatType.Melee]}, missile def {profile.DefenseSkills[CombatType.Missile]}, magic def {profile.DefenseSkills[CombatType.Magic]}");

            foreach (var other in new[] { CombatMode.NonCombat, CombatMode.Melee, CombatMode.Missile, CombatMode.Magic }.Where(m => m != stance))
            {
                var alt = ProfileBuilder.BuildDefenderProfile(player, other);
                Console.WriteLine($"  same character in {other}: melee def {alt.DefenseSkills[CombatType.Melee]}, missile def {alt.DefenseSkills[CombatType.Missile]}, magic def {alt.DefenseSkills[CombatType.Magic]}");
            }

            var shownTypes = new[] { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire, DamageType.Cold, DamageType.Acid, DamageType.Electric, DamageType.Nether };

            foreach (var part in profile.Armor.Keys.Where(k => !k.IgnoreMagicArmor && !k.IgnoreMagicResist).Select(k => k.BodyPart).Distinct().OrderBy(b => b))
            {
                var levels = shownTypes.Select(dt => profile.Armor.TryGetValue(new ArmorKey(part, dt, false, false), out var row) ? $"{dt} {row.EffectiveArmorLevel:0}" : $"{dt} -");
                Console.WriteLine($"  armor {part}: {string.Join(", ", levels)}");
            }

            Console.WriteLine("  resistance (P0 protection / V0 vulnerability): " +
                              string.Join(", ", shownTypes.Where(dt => profile.Resistance.ContainsKey(dt)).Select(dt => $"{dt} {profile.Resistance[dt].P0:0.###}/{profile.Resistance[dt].V0:0.###}")));

            var primaryMode = PlayerAttackerSpecs.PrimaryAttackMode(player);
            var primarySkill = PlayerLoadout.PrimaryAttackSkill(player);

            if (primarySkill != null)
            {
                var spellId = primaryMode == PlayerAttackMode.Magic ? PlayerAttackerSpecs.BestWarOrVoidProjectile(player) : 0u;

                Console.WriteLine($"CANDIDATE LOADOUTS for {primarySkill} (expected pre-mitigation damage per landed hit):");
                foreach (var candidate in PlayerLoadout.Candidates(player, primarySkill.Value, spellId))
                {
                    var spec = candidate.With(player, () => PlayerAttackerSpecs.FromPlayer(player, candidate.Mode, candidate.SpellId, target));
                    Console.WriteLine($"  {PlayerAttackerSpecs.ExpectedPreMitigationDamage(spec):0.0}  {Describe(spec)}");
                }
            }

            var loadout = PlayerAttackerSpecs.PrimaryLoadout(player, target);
            var primary = PlayerAttackerSpecs.Primary(player, target);

            Console.WriteLine($"PRIMARY {primaryMode} [{loadout}]: {Describe(primary)}");

            // real-data parity: the engine's own DamageEvent under the same loadout, stance and full bar
            if (loadout != null && loadout.Mode == PlayerAttackMode.Melee)
            {
                DamageEvent normal = null, crit = null;

                loadout.With(player, () => SimulatorStance.With(player, CombatMode.Melee, () =>
                {
                    var previousPower = player.PowerLevel;
                    player.PowerLevel = 1.0f;

                    try
                    {
                        var source = player.GetEquippedMeleeWeapon();

                        for (var i = 0; i < 5000 && (normal == null || crit == null); i++)
                        {
                            var ev = DamageEvent.CalculateDamage(player, target, source);

                            if (ev.HasDamage && !ev.IsCritical && normal == null) normal = ev;
                            if (ev.HasDamage && ev.IsCritical && crit == null) crit = ev;
                        }
                    }
                    finally
                    {
                        player.PowerLevel = previousPower;
                    }

                    return 0;
                }));

                Assert.IsNotNull(normal, "engine produced a normal hit");
                Assert.IsNotNull(crit, "engine produced a crit");

                Console.WriteLine($"ENGINE normal: base {normal.BaseDamageMod.MinDamage:0.##}-{normal.BaseDamageMod.MaxDamage:0.##} pre {normal.AttributeMod * normal.PowerMod * normal.SlayerMod * normal.DamageRatingMod:0.###} " +
                                  $"critChance {normal.CriticalChance:0.###} skill {normal.EffectiveAttackSkill}; crit: CriticalDamageMod {crit.CriticalDamageMod:0.###}");

                Assert.AreEqual(normal.BaseDamageMod.MinDamage, primary.BaseDamageMin, Epsilon);
                Assert.AreEqual(normal.BaseDamageMod.MaxDamage, primary.BaseDamageMax, Epsilon);
                Assert.AreEqual(normal.AttributeMod * normal.PowerMod * normal.SlayerMod * normal.DamageRatingMod, primary.PreMitigationMod, Epsilon);
                Assert.AreEqual(normal.CriticalChance, primary.CritChance, Epsilon);
                Assert.AreEqual(normal.EffectiveAttackSkill, primary.AttackSkill);
                Assert.AreEqual(crit.CriticalDamageMod, primary.CritDamageMod, Epsilon);
                Assert.AreEqual(normal.DamageType, primary.DamageType);
            }            Assert.AreEqual(CombatMode.NonCombat, player.CombatMode);
            Assert.IsTrue(player.EquippedObjects.Values.Any(i => i.CurrentWieldedLocation == EquipMask.Held) == (player.GetEquippedWand() != null), "the loadout swap restored the hands");

            foreach (var mode in new[] { PlayerAttackMode.Melee, PlayerAttackMode.Missile }.Where(m => m != primaryMode))
            {
                try
                {
                    Console.WriteLine($"also {mode}: {Describe(PlayerAttackerSpecs.FromPlayer(player, mode, target: target))}");
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"also {mode}: not applicable ({ex.Message})");
                }
            }

            Assert.AreEqual(CombatMode.NonCombat, player.CombatMode);
            Assert.AreEqual(primaryMode == PlayerAttackMode.Magic ? CombatType.Magic : primaryMode == PlayerAttackMode.Missile ? CombatType.Missile : CombatType.Melee, primary.CombatType);
        }

        private static string Describe(AttackerSpec spec)
        {
            return $"{spec.Label}: {spec.DamageType} base {spec.BaseDamageMin:0.##}-{spec.BaseDamageMax:0.##} skill {spec.AttackSkill} " +
                   $"skillBonus {spec.MagicSkillBonus:0.##} preMit {spec.PreMitigationMod:0.###} crit {spec.CritChance:0.###} " +
                   $"x{(spec.CombatType == CombatType.Magic ? spec.MagicCritDamageMod : spec.CritDamageMod):0.###} rend {spec.ArmorRendingMod:0.###} wrm {spec.WeaponResistanceMod:0.###}";
        }
    }
}