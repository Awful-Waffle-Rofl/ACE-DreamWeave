-- @owner: frays
-- @owns: wcid:2000068
-- @requires-restart: no
--
-- Frays creature, undead family, standard role: a copy of retail 88295 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000068;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000068, 'dw-drift-raider', 10, '2026-10-29 20:23:11') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000068,   1,         16) /* ItemType - Creature */
     , (2000068,   2,         14) /* CreatureType - Undead */
     , (2000068,   3,         69) /* PaletteTemplate - YellowSlime */
     , (2000068,   6,         -1) /* ItemsCapacity */
     , (2000068,   7,         -1) /* ContainersCapacity */
     , (2000068,  16,          1) /* ItemUseable - No */
     , (2000068,  25,        185) /* Level */
     , (2000068,  27,          0) /* ArmorType - None */
     , (2000068,  40,          1) /* CombatMode - NonCombat */
     , (2000068,  68,          3) /* TargetingTactic - Random, Focused */
     , (2000068,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000068, 101,        183) /* AiAllowedCombatStyle - Unarmed, OneHanded, OneHandedAndShield, Bow, Crossbow, ThrownWeapon */
     , (2000068, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000068, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000068, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000068,   1, True ) /* Stuck */
     , (2000068,   6, True ) /* AiUsesMana */
     , (2000068,  11, False) /* IgnoreCollisions */
     , (2000068,  12, True ) /* ReportCollisions */
     , (2000068,  13, False) /* Ethereal */
     , (2000068,  14, True ) /* GravityStatus */
     , (2000068,  19, True ) /* Attackable */
     , (2000068,  50, True ) /* NeverFailCasting */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000068,   1,       5) /* HeartbeatInterval */
     , (2000068,   2,       0) /* HeartbeatTimestamp */
     , (2000068,   3,     0.8) /* HealthRate */
     , (2000068,   4,     0.5) /* StaminaRate */
     , (2000068,   5,       2) /* ManaRate */
     , (2000068,  13,       1) /* ArmorModVsSlash */
     , (2000068,  14,     1.3) /* ArmorModVsPierce */
     , (2000068,  15,       1) /* ArmorModVsBludgeon */
     , (2000068,  16,     1.3) /* ArmorModVsCold */
     , (2000068,  17,       1) /* ArmorModVsFire */
     , (2000068,  18,       1) /* ArmorModVsAcid */
     , (2000068,  19,     1.2) /* ArmorModVsElectric */
     , (2000068,  31,      17) /* VisualAwarenessRange */
     , (2000068,  34,       1) /* PowerupTime */
     , (2000068,  36,       1) /* ChargeSpeed */
     , (2000068,  39,     1.3) /* DefaultScale */
     , (2000068,  43,       4) /* GeneratorRadius */
     , (2000068,  64,     0.5) /* ResistSlash */
     , (2000068,  65,    0.45) /* ResistPierce */
     , (2000068,  66,    0.65) /* ResistBludgeon */
     , (2000068,  67,    0.65) /* ResistFire */
     , (2000068,  68,    0.55) /* ResistCold */
     , (2000068,  69,    0.55) /* ResistAcid */
     , (2000068,  70,     0.5) /* ResistElectric */
     , (2000068,  71,       1) /* ResistHealthBoost */
     , (2000068,  72,       1) /* ResistStaminaDrain */
     , (2000068,  73,       1) /* ResistStaminaBoost */
     , (2000068,  74,       1) /* ResistManaDrain */
     , (2000068,  75,       1) /* ResistManaBoost */
     , (2000068,  76,    0.25) /* Translucency */
     , (2000068,  80,       3) /* AiUseMagicDelay */
     , (2000068, 104,      10) /* ObviousRadarRange */
     , (2000068, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000068,   1, 'Drift Raider') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000068,   1, 0x02000FA4) /* Setup */
     , (2000068,   2, 0x09000017) /* MotionTable */
     , (2000068,   3, 0x20000016) /* SoundTable */
     , (2000068,   4, 0x30000000) /* CombatTable */
     , (2000068,   6, 0x040015F0) /* PaletteBase */
     , (2000068,   7, 0x100004C0) /* ClothingBase */
     , (2000068,   8, 0x06002CF5) /* Icon */
     , (2000068,  22, 0x34000028) /* PhysicsEffectTable */
     , (2000068,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000068,   1, 380, 0, 0) /* Strength */
     , (2000068,   2, 400, 0, 0) /* Endurance */
     , (2000068,   3, 340, 0, 0) /* Quickness */
     , (2000068,   4, 340, 0, 0) /* Coordination */
     , (2000068,   5, 470, 0, 0) /* Focus */
     , (2000068,   6, 470, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000068,   1,  2410, 0, 0, 2410) /* MaxHealth */
     , (2000068,   3,  1500, 0, 0, 1900) /* MaxStamina */
     , (2000068,   5,  3080, 0, 0, 3550) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000068,  6, 0, 3, 0, 355, 0, 0) /* MeleeDefense        Specialized */
     , (2000068,  7, 0, 3, 0, 405, 0, 0) /* MissileDefense      Specialized */
     , (2000068, 14, 0, 3, 0, 240, 0, 0) /* ArcaneLore          Specialized */
     , (2000068, 15, 0, 3, 0, 310, 0, 0) /* MagicDefense        Specialized */
     , (2000068, 20, 0, 3, 0,  90, 0, 0) /* Deception           Specialized */
     , (2000068, 31, 0, 3, 0, 275, 0, 0) /* CreatureEnchantment Specialized */
     , (2000068, 33, 0, 3, 0, 275, 0, 0) /* LifeMagic           Specialized */
     , (2000068, 34, 0, 3, 0, 275, 0, 0) /* WarMagic            Specialized */
     , (2000068, 44, 0, 3, 0, 375, 0, 0) /* HeavyWeapons        Specialized */
     , (2000068, 45, 0, 3, 0, 365, 0, 0) /* LightWeapons        Specialized */
     , (2000068, 46, 0, 3, 0, 375, 0, 0) /* FinesseWeapons      Specialized */
     , (2000068, 47, 0, 3, 0, 175, 0, 0) /* MissileWeapons      Specialized */
     , (2000068, 48, 0, 3, 0, 300, 0, 0) /* Shield              Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000068,  0,  4,  0,    0,  405,  405,  527,  405,  527,  405,  405,  486,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000068,  1,  4,  0,    0,  405,  405,  527,  405,  527,  405,  405,  486,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000068,  2,  4,  0,    0,  405,  405,  527,  405,  527,  405,  405,  486,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000068,  3,  4,  0,    0,  405,  405,  527,  405,  527,  405,  405,  486,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000068,  4,  4,  0,    0,  405,  405,  527,  405,  527,  405,  405,  486,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000068,  5,  4,  5, 0.75,  405,  405,  527,  405,  527,  405,  405,  486,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000068,  6,  4,  0,    0,  405,  405,  527,  405,  527,  405,  405,  486,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000068,  7,  4,  0,    0,  405,  405,  527,  405,  527,  405,  405,  486,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000068,  8,  4,  5, 0.75,  405,  405,  527,  405,  527,  405,  405,  486,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000068, 2, 48103,  1, 0, 0.55, False) /* Create Sickle (48103) for Wield */
     , (2000068, 2, 48102,  1, 0, 0.56, False) /* Create Khopesh (48102) for Wield */
     , (2000068, 9, 39128,  1, 0, 0.18, False) /* Create Invoking Stone (39128) for ContainTreasure */
     , (2000068, 9, 39123,  1, 0, 0.18, False) /* Create Invoking Stone (39123) for ContainTreasure */
     , (2000068, 9, 39121,  1, 0, 0.18, False) /* Create Invoking Stone (39121) for ContainTreasure */
     , (2000068, 9, 39126,  1, 0, 0.18, False) /* Create Invoking Stone (39126) for ContainTreasure */
     , (2000068, 9,     0,  0, 0, 0.28, False) /* Create nothing for ContainTreasure */;
