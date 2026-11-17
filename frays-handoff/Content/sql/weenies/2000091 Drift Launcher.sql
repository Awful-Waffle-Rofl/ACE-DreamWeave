-- @owner: frays
-- @owns: wcid:2000091
-- @requires-restart: no
--
-- Frays creature, lugian family (the fifth), snarl role: a copy of retail 46716 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000091;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000091, 'dw-drift-launcher', 10, '2026-10-30 00:29:01') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000091,   1,         16) /* ItemType - Creature */
     , (2000091,   2,          5) /* CreatureType - Lugian */
     , (2000091,   3,         61) /* PaletteTemplate - White */
     , (2000091,   6,         -1) /* ItemsCapacity */
     , (2000091,   7,         -1) /* ContainersCapacity */
     , (2000091,  16,          1) /* ItemUseable - No */
     , (2000091,  25,        265) /* Level */
     , (2000091,  27,          0) /* ArmorType - None */
     , (2000091,  40,          2) /* CombatMode - Melee */
     , (2000091,  68,         13) /* TargetingTactic - Random, LastDamager, TopDamager */
     , (2000091,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000091, 101,        131) /* AiAllowedCombatStyle - Unarmed, OneHanded, ThrownWeapon */
     , (2000091, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000091, 146,          0) /* XpOverride */
     , (2000091, 307,          5) /* DamageRating */
     , (2000091, 308,         10) /* DamageResistRating */
     , (2000091, 386,          5) /* Overpower */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000091,   1, True ) /* Stuck */
     , (2000091,  29, True ) /* NoCorpse */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000091,   1,       5) /* HeartbeatInterval */
     , (2000091,   2,       0) /* HeartbeatTimestamp */
     , (2000091,   3,     0.6) /* HealthRate */
     , (2000091,   4,       4) /* StaminaRate */
     , (2000091,   5,       2) /* ManaRate */
     , (2000091,  12,     0.5) /* Shade */
     , (2000091,  13,    0.75) /* ArmorModVsSlash */
     , (2000091,  14,    0.56) /* ArmorModVsPierce */
     , (2000091,  15,    0.75) /* ArmorModVsBludgeon */
     , (2000091,  16,    0.95) /* ArmorModVsCold */
     , (2000091,  17,    0.75) /* ArmorModVsFire */
     , (2000091,  18,    0.85) /* ArmorModVsAcid */
     , (2000091,  19,    0.56) /* ArmorModVsElectric */
     , (2000091,  34,       2) /* PowerupTime */
     , (2000091,  36,       1) /* ChargeSpeed */
     , (2000091,  39,       1) /* DefaultScale */
     , (2000091,  64,    0.66) /* ResistSlash */
     , (2000091,  65,    0.85) /* ResistPierce */
     , (2000091,  66,    0.66) /* ResistBludgeon */
     , (2000091,  67,    0.25) /* ResistFire */
     , (2000091,  68,    0.42) /* ResistCold */
     , (2000091,  69,    0.65) /* ResistAcid */
     , (2000091,  70,    0.91) /* ResistElectric */
     , (2000091,  71,       1) /* ResistHealthBoost */
     , (2000091,  72,       1) /* ResistStaminaDrain */
     , (2000091,  73,       1) /* ResistStaminaBoost */
     , (2000091,  74,       1) /* ResistManaDrain */
     , (2000091,  75,       1) /* ResistManaBoost */
     , (2000091,  76,    0.25) /* Translucency */
     , (2000091, 104,      10) /* ObviousRadarRange */
     , (2000091, 117,     0.5) /* FocusedProbability */
     , (2000091, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000091,   1, 'Drift Launcher') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000091,   1, 0x02000A0B) /* Setup */
     , (2000091,   2, 0x09000006) /* MotionTable */
     , (2000091,   3, 0x2000000A) /* SoundTable */
     , (2000091,   4, 0x30000003) /* CombatTable */
     , (2000091,   6, 0x040010C6) /* PaletteBase */
     , (2000091,   7, 0x100002BD) /* ClothingBase */
     , (2000091,   8, 0x06001037) /* Icon */
     , (2000091,  22, 0x3400001E) /* PhysicsEffectTable */
     , (2000091,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000091,   1, 400, 0, 0) /* Strength */
     , (2000091,   2, 340, 0, 0) /* Endurance */
     , (2000091,   3, 325, 0, 0) /* Quickness */
     , (2000091,   4, 325, 0, 0) /* Coordination */
     , (2000091,   5, 200, 0, 0) /* Focus */
     , (2000091,   6, 240, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000091,   1, 21500, 0, 0, 21670) /* MaxHealth */
     , (2000091,   3,  7660, 0, 0, 8000) /* MaxStamina */
     , (2000091,   5,     0, 0, 0, 240) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000091,  6, 0, 3, 0, 500, 0, 0) /* MeleeDefense        Specialized */
     , (2000091,  7, 0, 3, 0, 505, 0, 0) /* MissileDefense      Specialized */
     , (2000091, 15, 0, 3, 0, 507, 0, 0) /* MagicDefense        Specialized */
     , (2000091, 20, 0, 2, 0, 100, 0, 0) /* Deception           Trained */
     , (2000091, 22, 0, 2, 0, 462, 0, 0) /* Jump                Trained */
     , (2000091, 24, 0, 2, 0, 425, 0, 0) /* Run                 Trained */
     , (2000091, 45, 0, 3, 0, 442, 0, 0) /* LightWeapons        Specialized */
     , (2000091, 47, 0, 3, 0, 398, 0, 0) /* MissileWeapons      Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000091,  0,  4,  0,    0,  430,  323,  241,  323,  409,  323,  366,  241,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000091,  1,  4,  0,    0,  430,  323,  241,  323,  409,  323,  366,  241,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000091,  2,  4,  0,    0,  430,  323,  241,  323,  409,  323,  366,  241,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000091,  3,  4,  0,    0,  430,  323,  241,  323,  409,  323,  366,  241,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000091,  4,  4,  0,    0,  430,  323,  241,  323,  409,  323,  366,  241,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000091,  5,  4, 175,  0.4,  430,  323,  241,  323,  409,  323,  366,  241,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000091,  6,  4,  0,    0,  430,  323,  241,  323,  409,  323,  366,  241,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000091,  7,  4,  0,    0,  430,  323,  241,  323,  409,  323,  366,  241,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000091,  8,  4, 175,  0.4,  430,  323,  241,  323,  409,  323,  366,  241,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000091, 2, 46715,  1, 0, 1, False) /* Create Rock (46715) for Wield */;
