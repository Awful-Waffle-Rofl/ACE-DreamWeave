using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.Pvp;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Shared fixtures for the Summoner focus transfer tests (weapon mods and imbues that reach the pets): a focus or
    /// plain wand with chosen mods and imbue, an owner wielding it, the owner's combat pet with a chosen attack skill, an
    /// Echo, and a monster target. Every PropertyManager key the transfer reads is seeded by <see cref="Seed"/> and put
    /// back from the defaults by <see cref="Restore"/>.
    /// </summary>
    internal static class SummonerTransferFixtures
    {
        private static uint nextGuid = 0x7E720000;
        private static uint nextWcid = 997100;

        internal static readonly string[] DoubleKeys =
        {
            SummonerFocus.PetDamageScaleKey, SummonerFocus.PetDefenseScaleKey, SummonerFocus.PetDefenseFloorKey, SummonerFocus.WielderDamageModKey,
            SummonerFocus.PetModTransferPercentKey, SummonerFocus.PetImbueTransferPercentKey, "weapon_mod_magnitude_scale",
        };

        // A pet hit runs Pet.ApplyUmbralSiphonLeech, which reads the owner's class-ability cache from Character quests, so the
        // seeded owner needs an empty Character and its lock. UnsafeAccessor, never FieldInfo.SetValue on a Player (static ctor).
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Character>k__BackingField")]
        private static extern ref ACE.Database.Models.Shard.Character CharacterField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "CharacterDatabaseLock")]
        private static extern ref ReaderWriterLockSlim CharacterLockField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_IsHuntersEcho")]
        private static extern void SetIsHuntersEcho(CombatPet pet, bool value);

        /// <summary>Neutral pet scales (1:1 damage, no defense) and the full transfer, so a test sees only what it changes.</summary>
        internal static void Seed()
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetDamageScaleKey, 1.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseScaleKey, 0.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseFloorKey, 0.25);
            PropertyManager.ModifyDouble(SummonerFocus.WielderDamageModKey, 0.25);
            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 1.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetImbueTransferPercentKey, 1.0);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);
            PropertyManager.ModifyBool("weapon_mods_enabled", true);
        }

        internal static void Restore()
        {
            foreach (var key in DoubleKeys)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);

            PropertyManager.ModifyBool("weapon_mods_enabled", (bool)DefaultPropertyManager.DefaultBooleanProperties["weapon_mods_enabled"].Item);
            SummonerFocus.SimulatedPetFocusSource = null;
        }

        /// <summary>A Held wand (a Summoner focus, or plain), each mod written at a perfect roll through the applicator, and an optional imbue (stat 179).</summary>
        internal static Caster Wand(bool focus, ImbuedEffectType imbue = ImbuedEffectType.Undef, params WeaponModId[] mods)
        {
            var wand = SummonerFocusTests.Wand(focus);

            foreach (var id in mods)
                WeaponModTinkerSet.ApplySpecialAtFraction(wand, WeaponModRegistry.Get(id), 1.0, 1.0);

            if (imbue != ImbuedEffectType.Undef)
                wand.ImbuedEffect = imbue;

            return wand;
        }

        /// <summary>The magnitude a perfect roll of <paramref name="id"/> resolves to on <paramref name="item"/> at scale 1.</summary>
        internal static double Mag(WeaponModId id, WorldObject item) =>
            WeaponModValue.MagnitudeFromFraction(WeaponModRegistry.Get(id), 1.0, 1.0, item);

        internal static Player Owner(WorldObject wand)
        {
            var owner = SummonerFocusTests.OwnerWielding(wand);
            CharacterField(owner) = new ACE.Database.Models.Shard.Character();
            CharacterLockField(owner) = new ReaderWriterLockSlim();
            return owner;
        }

        private static Weenie CreatureWeenie(WeenieType type, Skill attackSkill, uint attackLevel) => new Weenie
        {
            WeenieClassId = nextWcid++,
            ClassName = "summonertransfertest",
            WeenieType = type,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Transfer Test Creature" },
            PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
            {
                [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 100 },
                [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
                [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = 100 },
                [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = 100 },
                [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = 100 },
                [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = 100 },
            },
            PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
            {
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
            },
            PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
            {
                [attackSkill] = new PropertiesSkill { InitLevel = attackLevel, SAC = SkillAdvancementClass.Trained },
            },
            PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Hand] = new PropertiesBodyPart { DType = DamageType.Slash, DVal = 40, DVar = 0.5f },
            },
        };

        /// <summary>A monster target at full health.</summary>
        internal static Creature Monster()
        {
            var weenie = CreatureWeenie(WeenieType.Creature, Skill.UnarmedCombat, 100);
            var c = new Creature(weenie, new ObjectGuid(nextGuid++));
            Creature.SetBodyPartTable(weenie.WeenieClassId, new BodyPartTable(weenie));
            c.Health.Current = c.Health.MaxValue;
            return c;
        }

        /// <summary>
        /// The owner's combat pet with <paramref name="attackSkill"/> trained at <paramref name="attackLevel"/> (init level),
        /// Shield trained at <paramref name="shieldSkill"/> when above 0, and an Echo when asked. Its body-part table is
        /// registered, so DamageEvent and MonsterDefenderProfile can resolve it without a world database.
        /// </summary>
        internal static CombatPet Pet(Player owner, Skill attackSkill = Skill.UnarmedCombat, uint attackLevel = 400, bool echo = false, uint shieldSkill = 0)
        {
            var weenie = CreatureWeenie(WeenieType.CombatPet, attackSkill, attackLevel);
            if (shieldSkill > 0)
                weenie.PropertiesSkill[Skill.Shield] = new PropertiesSkill { InitLevel = shieldSkill, SAC = SkillAdvancementClass.Specialized };

            var p = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            Creature.SetBodyPartTable(weenie.WeenieClassId, new BodyPartTable(weenie));
            p.P_PetOwner = owner;
            p.Health.Current = p.Health.MaxValue;
            if (echo)
                SetIsHuntersEcho(p, true);
            return p;
        }

        /// <summary>A Player target (the PvP guard's other side).</summary>
        internal static Player PlayerTarget() => PvpTemplatePlayerTests.SeededPlayer();

        // AreEqual(double, double, delta) passes on NaN, so every tolerance compare goes through this
        internal static void AssertNear(double expected, double actual, double tolerance, string because = null) =>
            Assert.IsTrue(!double.IsNaN(actual) && Math.Abs(expected - actual) < tolerance, $"expected {expected}, got {actual}. {because}");
    }
}
