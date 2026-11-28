using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The Avatar fighter behaves like the Custom Summons Warden (Tank) pet (owner ruling 2026-11-25): a capped taunt
    /// pulse over its run's creatures, physical melee damage only, and the Warden's armor as the AUTHORED armor (the start
    /// snapshot replaces it with the start wave's band armor, owner ruling 2026-11-25: real damage with band armor).
    /// <list type="bullet">
    /// <item>The taunt goes through the REAL CombatPet.ApplyTauntPulse and Creature.ApplyTaunt, so each assertion reads
    /// the hold each monster actually took (TauntTarget), not a decider's verdict.</item>
    /// <item>The placement pin reads what LiveGauntletFighterHooks hands the placed fighter (scope and interval).</item>
    /// <item>The damage-type and armor pins read the shipped weenie SQL and the real GetDamageType / ApplyStamp.</item>
    /// </list>
    /// Reads class_ability_taunt_duration (the pulse's hold, through TauntAbility.BaseDuration), seeded from the code
    /// defaults in ClassInitialize and restored in ClassCleanup.
    /// </summary>
    [TestClass]
    public class GauntletFighterWardenTests
    {
        private const string TauntDurationKey = "class_ability_taunt_duration";

        private static double? savedTauntDuration;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();

            try { savedTauntDuration = PropertyManager.GetDouble(TauntDurationKey).Item; } catch { savedTauntDuration = null; }

            PropertyManager.ModifyDouble(TauntDurationKey, DefaultPropertyManager.DefaultDoubleProperties[TauntDurationKey].Item);
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            PropertyManager.ModifyDouble(TauntDurationKey, savedTauntDuration ?? DefaultPropertyManager.DefaultDoubleProperties[TauntDurationKey].Item);
        }

        // ---- fixtures -------------------------------------------------------------------------------------------------------

        private sealed class FakeRun : IGauntletCreatureOwner
        {
            public bool IsLive { get; set; } = true;
            public int CurrentWave => 1;
            public double CentreGx => 0;
            public double CentreGy => 0;
            public double Radius => 60;
            public IEnumerable<Player> OnlineMembersInside() => Array.Empty<Player>();
            public void OnGauntletCreatureDied(Creature creature) { }
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        private static uint nextMember = 0x500F0001;

        private static Player Member()
        {
            var p = BarePlayer();
            GuidField(p) = new ObjectGuid(nextMember++);
            return p;
        }

        // the hold bookkeeping only tests CurrentLandblock for null
        private static readonly Landblock Spawned = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        private static Creature RunMonster(IGauntletCreatureOwner run, WorldObject target, float x)
        {
            var m = Monster(x: x);
            m.P_Gauntlet = run;
            m.AttackTarget = target;
            m.MonsterState = Creature.State.Awake;
            m.IsAwake = true;
            m.CurrentLandblock = Spawned;
            return m;
        }

        private static GauntletAvatarFighter Armed(IGauntletCreatureOwner run, params Player[] participants)
        {
            var fighter = Fighter(x: 50.0f);
            fighter.Participants = () => participants;
            fighter.ArmTauntPulse(run, 5.0);
            return fighter;
        }

        private static bool HeldBy(Creature m, Creature taunter) => ReferenceEquals(m.TauntTarget, taunter);

        // ---- the taunt: who is pulled -----------------------------------------------------------------------------------------

        [TestMethod]
        public void Taunt_PullsThisRunsCreatures_FightingAParticipant_TheirPet_OrTheFighter()
        {
            var run = new FakeRun();
            var other = new FakeRun();
            var member = Member();
            var outsider = Member();
            var fighter = Armed(run, member);

            var memberPet = PlayerPet();
            memberPet.P_PetOwner = member;

            var onMember = RunMonster(run, member, 52);
            var onFighter = RunMonster(run, fighter, 53);
            var onMemberPet = RunMonster(run, memberPet, 54);
            var onOutsider = RunMonster(run, outsider, 51);
            var otherRun = RunMonster(other, member, 51.5f);
            var notGauntlet = RunMonster(null, member, 51.2f);
            var idle = RunMonster(run, null, 51.1f);

            var all = new[] { onOutsider, otherRun, notGauntlet, idle, onMember, onFighter, onMemberPet };

            Assert.AreEqual(3, fighter.ApplyTauntPulse(all, _ => true));

            Assert.IsTrue(HeldBy(onMember, fighter), "fighting a participant");
            Assert.IsTrue(HeldBy(onFighter, fighter), "fighting the fighter");
            Assert.IsTrue(HeldBy(onMemberPet, fighter), "fighting a participant's pet");

            Assert.IsNull(onOutsider.TauntTarget, "fighting someone who is not a participant");
            Assert.IsNull(otherRun.TauntTarget, "another run's creature");
            Assert.IsNull(notGauntlet.TauntTarget, "a creature no Gauntlet owns");
            Assert.IsNull(idle.TauntTarget, "an idle creature is never woken");
            Assert.AreSame(member, otherRun.AttackTarget, "a refused creature keeps its own target");
        }

        [TestMethod]
        public void Taunt_HoldsAtMostThree_NearestFirst()
        {
            var run = new FakeRun();
            var member = Member();
            var fighter = Armed(run, member);

            var m60 = RunMonster(run, member, 60);
            var m52 = RunMonster(run, member, 52);
            var m58 = RunMonster(run, member, 58);
            var m54 = RunMonster(run, member, 54);
            var m56 = RunMonster(run, member, 56);
            var all = new[] { m60, m52, m58, m54, m56 };

            Assert.AreEqual(CombatPet.MaxTauntTargets, fighter.ApplyTauntPulse(all, _ => true));
            Assert.AreEqual(3, all.Count(m => HeldBy(m, fighter)), "the Tank's cap of three");
            Assert.IsTrue(HeldBy(m52, fighter) && HeldBy(m54, fighter) && HeldBy(m56, fighter), "the nearest three");

            // the next pulse refreshes the three it holds and takes no fourth
            Assert.AreEqual(3, fighter.ApplyTauntPulse(all, _ => true));
            Assert.AreEqual(3, all.Count(m => HeldBy(m, fighter)));
            Assert.IsNull(m58.TauntTarget);
            Assert.IsNull(m60.TauntTarget);
        }

        [TestMethod]
        public void Taunt_NothingEngagedWithTheRunsSide_TauntsNothing()
        {
            var run = new FakeRun();
            var outsider = Member();

            // no participant at all, and the run's creatures fight someone else or nobody
            var fighter = Armed(run);
            var all = new[] { RunMonster(run, outsider, 52), RunMonster(run, null, 53) };

            Assert.AreEqual(0, fighter.ApplyTauntPulse(all, _ => true));
            Assert.IsTrue(all.All(m => m.TauntTarget == null));
        }

        [TestMethod]
        public void Taunt_ReachAndRadius_AreTheTanksRules()
        {
            var run = new FakeRun();
            var member = Member();
            var fighter = Armed(run, member);

            var near = RunMonster(run, member, 50.0f + (float)CombatPet.TauntPulseRadius - 1.0f);
            var far = RunMonster(run, member, 50.0f + (float)CombatPet.TauntPulseRadius + 1.0f);
            var unseen = RunMonster(run, member, 52);

            Assert.AreEqual(1, fighter.ApplyTauntPulse(new[] { near, far, unseen }, m => !ReferenceEquals(m, unseen)));
            Assert.IsTrue(HeldBy(near, fighter));
            Assert.IsNull(far.TauntTarget, "beyond the 15 m pulse radius");
            Assert.IsNull(unseen.TauntTarget, "refused by the reach test (landblock group, line of sight)");
        }

        [TestMethod]
        public void Taunt_UnarmedOrRunOver_TauntsNothing()
        {
            var run = new FakeRun();
            var member = Member();

            var unarmed = Fighter(x: 50.0f);
            unarmed.Participants = () => new[] { member };
            Assert.IsNull(unarmed.TauntScope);
            Assert.AreEqual(0.0, unarmed.SummonTauntInterval, "the fighter weenie carries no interval (BuildFighterWeenie strips it)");
            Assert.AreEqual(0, unarmed.ApplyTauntPulse(new[] { RunMonster(run, member, 52) }, _ => true), "never armed: no side, no pulse");

            var armed = Armed(run, member);
            run.IsLive = false;
            var m = RunMonster(run, member, 52);
            Assert.AreEqual(0, armed.ApplyTauntPulse(new[] { m }, _ => true), "a run that is no longer live pulls nothing");
            Assert.IsNull(m.TauntTarget);
        }

        [TestMethod]
        public void Arm_SetsTheInterval_AndOffValuesTurnThePulseOff()
        {
            var run = new FakeRun();
            var f = Fighter();

            f.ArmTauntPulse(run, 5.0);
            Assert.AreSame(run, f.TauntScope);
            Assert.AreEqual(5.0, f.SummonTauntInterval);
            Assert.IsTrue(f.TryConsumeTauntPulse(100.0, f.SummonTauntInterval), "the first combat tick pulses");
            Assert.IsFalse(f.TryConsumeTauntPulse(104.9, f.SummonTauntInterval));
            Assert.IsTrue(f.TryConsumeTauntPulse(105.0, f.SummonTauntInterval), "every 5 s, the Warden's interval");

            foreach (var off in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            {
                f.ArmTauntPulse(run, off);
                Assert.AreEqual(0.0, f.SummonTauntInterval, $"{off} is off");
            }

            f.ArmTauntPulse(null, 5.0);
            Assert.AreEqual(0.0, f.SummonTauntInterval, "no scope is off");
        }

        [TestMethod]
        public void Taunt_TheHold_IsTheTankPulsesHold()
        {
            var run = new FakeRun();
            var member = Member();
            var fighter = Armed(run, member);
            var m = RunMonster(run, member, 52);

            var before = Timers.RunningTime;
            Assert.AreEqual(1, fighter.ApplyTauntPulse(new[] { m }, _ => true));

            var hold = TauntAbility.EffectiveDuration(TauntAbility.BaseDuration());
            Assert.IsTrue(hold > 0, "fixture: the seeded player Taunt hold");
            Assert.AreEqual(before + hold, m.TauntExpiry, 1.0, "the player Taunt's base hold, as the Tank pulse takes it (no gear term)");
        }

        // ---- the taunt: what placement hands the fighter -------------------------------------------------------------------

        [TestMethod]
        public void Placement_ArmsThePulse_ForThisRun_AtTheTunedInterval_AndAgainOnRespawn()
        {
            var h = new FighterRunHarness();
            var run = h.Start();
            h.Advance(0.5);

            var fighter = h.Placed.Single();
            Assert.AreSame(run, fighter.TauntScope, "the scope is the run, the owner its creatures carry in P_Gauntlet");
            Assert.AreEqual(5.0, fighter.SummonTauntInterval, "allegiance_gauntlet_fighter_taunt_seconds' default");

            // a fallen fighter's replacement is armed again, with the same run and interval
            h.Advance(h.Tuning.StagingSeconds + 0.5);
            Assert.AreEqual(GauntletState.Active, run.State, "fixture: the run is Active");
            fighter.OnDeath(null, DamageType.Slash);
            h.Advance(h.Tuning.FighterRespawnSeconds + 1.5);
            Assert.AreEqual(2, h.Placed.Count, "fixture: respawned");

            var respawned = h.Placed[1];
            Assert.AreNotSame(fighter, respawned);
            Assert.AreSame(run, respawned.TauntScope, "the respawn defends the same run");
            Assert.AreEqual(5.0, respawned.SummonTauntInterval, "and pulses at the same interval");

            var off = new FighterRunHarness { Tuning = GauntletTuning.Defaults with { FighterTauntSeconds = 0.0 } };
            off.Start();
            off.Advance(0.5);
            Assert.AreEqual(0.0, off.Placed.Single().SummonTauntInterval, "0 turns the pulse off");
        }

        [TestMethod]
        public void Pin_TheFighterTicksThePulse_ThroughCombatPetsTick()
        {
            // the fighter keeps CombatPet's combat tick (Monster_Tick -> PetCombatTick -> TickTauntPulse); a Gauntlet override of
            // either would silence the pulse in the live world while every ApplyTauntPulse test above stays green
            var src = PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletAvatarFighter.cs");
            Assert.IsFalse(src.Contains("PetCombatTick("), "the fighter must not replace PetCombatTick");
            Assert.IsFalse(src.Contains("TickTauntPulse("), "the fighter must not replace TickTauntPulse");

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/CombatPet.cs"), "public void TickTauntPulse()");
            StringAssert.Contains(body, "ApplyTauntPulse(PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature(), CanTauntReach);");
        }

        // ---- physical damage ---------------------------------------------------------------------------------------------

        private static WorldObject Blade(DamageType type)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 995990,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)EquipMask.MeleeWeapon },
                    { PropertyInt.DefaultCombatStyle, (int)CombatStyle.OneHanded },
                    { PropertyInt.DamageType, (int)type },
                    { PropertyInt.Damage, 300 },
                },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Blade" } },
            };

            return new MeleeWeapon(weenie, NextGuid());
        }

        [TestMethod]
        public void Damage_AMeleeHit_IsTheWieldedBladesType_EvenAfterTheStamp()
        {
            var fighter = Fighter();
            fighter.ItemCapacity = 10;
            Assert.IsTrue(fighter.TryAddToInventory(Blade(DamageType.Slash)));
            fighter.EquipInventoryItems();
            Assert.IsNotNull(fighter.GetEquippedMeleeWeapon(), "fixture: the blade is wielded");

            // a body part of an elemental type: the wielded blade, not the body part, decides the hit's type
            var firePart = new PropertiesBodyPart { DType = DamageType.Fire, DVal = 10 };

            fighter.ApplyStamp(new GauntletFighterStamp { Level = 200, DamageTarget = 500, MinDeathSeconds = 20, BurstSeconds = 5 });

            Assert.AreEqual(DamageType.Slash, fighter.GetDamageType(firePart, CombatType.Melee));
            Assert.AreEqual(DamageType.Slash, fighter.GetEquippedMeleeWeapon().W_DamageType, "the stamp rescales damage, never the type");
        }

        // ---- the shipped content: physical arms, the Warden's armor --------------------------------------------------------

        private const string FighterSql = "Content/sql/weenies/1008504 Allegiance Avatar (Gauntlet).sql";
        private const string WardenSql = "Content/sql/weenies/1007717 Revenant Warden (Slashing).sql";
        private const string BladeSql = "Content/sql/weenies/1007728 Revenant Warden Blade.sql";
        private const string AegisSql = "Content/sql/weenies/1007753 Revenant Warden Aegis.sql";

        /// <summary>One table's INSERT statement, comments stripped first (a comment may hold a ';' or a paren), or null.</summary>
        private static string Insert(string sql, string table)
        {
            var noComments = Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var m = Regex.Match(noComments, $@"INSERT INTO `{table}`[^;]*;", RegexOptions.Singleline);
            return m.Success ? m.Value : null;
        }

        /// <summary>The value rows of one INSERT, each as its comma-separated fields.</summary>
        private static List<string[]> Rows(string sql, string table)
        {
            var insert = Insert(sql, table);

            if (insert == null)
                return new List<string[]>();

            return Regex.Matches(insert, @"\(([^()]*)\)")
                .Cast<Match>()
                .Skip(1) // the column list
                .Select(m => m.Groups[1].Value.Split(',').Select(v => v.Trim()).ToArray())
                .ToList();
        }

        private static Dictionary<int, int> Ints(string sql) =>
            Rows(sql, "weenie_properties_int").ToDictionary(r => int.Parse(r[1]), r => int.Parse(r[2]));

        [TestMethod]
        public void Content_TheFighterWieldsOnlyThePhysicalWardenBlade_AndCarriesNoSpells()
        {
            var fighter = PooledLootSourceText.ReadRaw(FighterSql);
            var blade = PooledLootSourceText.ReadRaw(BladeSql);

            var wields = Rows(fighter, "weenie_properties_create_list").Where(r => r[1] == "2").Select(r => int.Parse(r[2])).ToList();
            CollectionAssert.IsSubsetOf(new[] { 1007728, 1007753 }, wields, "the Warden blade and Aegis");
            Assert.AreEqual(1, wields.Count(w => w == 1007728), "one weapon");

            var bladeInts = Ints(blade);
            Assert.AreEqual((int)DamageType.Slash, bladeInts[(int)PropertyInt.DamageType], "the blade is Slash, a physical type");
            Assert.IsFalse(bladeInts.ContainsKey((int)PropertyInt.ImbuedEffect), "no imbue");
            Assert.IsNull(Insert(blade, "weenie_properties_spell_book"), "no spells on the blade");
            Assert.IsFalse(Rows(blade, "weenie_properties_d_i_d").Any(r => r[1] == ((int)PropertyDataId.ProcSpell).ToString()), "no proc spell");
            Assert.IsFalse(Rows(blade, "weenie_properties_float").Any(r => r[1] == ((int)PropertyFloat.ElementalDamageMod).ToString()), "no elemental damage mod");

            Assert.IsNull(Insert(fighter, "weenie_properties_spell_book"), "the fighter casts nothing");

            var physical = new[] { (int)DamageType.Slash, (int)DamageType.Pierce, (int)DamageType.Bludgeon };
            foreach (var part in Rows(fighter, "weenie_properties_body_part"))
                CollectionAssert.Contains(physical, int.Parse(part[2]), $"body part {part[1]} d_Type");
        }

        [TestMethod]
        public void Content_TheFighterWearsTheWardensArmor_AndItsCosmeticShield()
        {
            var fighter = Rows(PooledLootSourceText.ReadRaw(FighterSql), "weenie_properties_body_part");
            var warden = Rows(PooledLootSourceText.ReadRaw(WardenSql), "weenie_properties_body_part");

            Assert.AreEqual(9, fighter.Count, "the humanoid nine-part table");
            Assert.AreEqual(warden.Count, fighter.Count);

            // key, then base_Armor and the eight armor_Vs columns (fields 5..13)
            for (var i = 0; i < fighter.Count; i++)
                CollectionAssert.AreEqual(warden[i].Skip(5).Take(9).ToArray(), fighter[i].Skip(5).Take(9).ToArray(), $"part {fighter[i][1]}: the Warden's armor");

            Assert.AreEqual("931", fighter[0][5], "the Warden's 931 (a change to the Warden must be a decision for the fighter too)");
            Assert.AreEqual(0, Ints(PooledLootSourceText.ReadRaw(AegisSql))[(int)PropertyInt.ArmorLevel], "the Aegis is cosmetic: ArmorLevel 0");
        }

        [TestMethod]
        public void Survivability_TheStampSetsTheBandArmor_AndAStampWithoutOneLeavesItAuthored()
        {
            // Owner ruling 2026-11-25 (Q2B): real damage, with the body-part armor set from the band standard at ratio 1.0.
            var fighter = Fighter();
            Assert.IsTrue(fighter.Biota.PropertiesBodyPart.Values.All(p => p.BaseArmor == 50), "fixture: authored 50 on every part");

            // a standard whose armor axis (200) differs from the authored 50
            var stamp = GauntletFighterStats.Compute(200, 2000, Standard(), 50, 100, GauntletTuning.Defaults);
            Assert.IsTrue(stamp.DamageTarget > 0, "fixture: the stamp writes the body parts' damage");
            fighter.ApplyStamp(stamp);

            foreach (var kv in fighter.Biota.PropertiesBodyPart)
                Assert.AreEqual(200, kv.Value.BaseArmor, $"{kv.Key}: the band's MaxBaseArmor 200 x 1.0");

            // the pre-first-wave stamp (no plan yet) carries no armor target: the authored (Warden's) armor stands
            var bare = Fighter();
            bare.ApplyStamp(new GauntletFighterStamp { Level = 10, MinDeathSeconds = 20, BurstSeconds = 5 });

            foreach (var kv in bare.Biota.PropertiesBodyPart)
                Assert.AreEqual(50, kv.Value.BaseArmor, $"{kv.Key}: no armor target, armor stays authored");
        }
    }
}
