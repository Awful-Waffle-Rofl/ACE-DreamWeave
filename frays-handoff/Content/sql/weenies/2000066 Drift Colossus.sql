-- @owner: frays
-- @owns: wcid:2000066
-- @requires-restart: no
--
-- Frays creature, golem family, snarl role: a copy of retail 40775 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000066;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000066, 'dw-drift-colossus', 10, '2026-10-29 20:23:11') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000066,   1,         16) /* ItemType - Creature */
     , (2000066,   2,         13) /* CreatureType - Golem */
     , (2000066,   3,         39) /* PaletteTemplate - Black */
     , (2000066,   6,         -1) /* ItemsCapacity */
     , (2000066,   7,         -1) /* ContainersCapacity */
     , (2000066,  16,          1) /* ItemUseable - No */
     , (2000066,  25,        185) /* Level */
     , (2000066,  27,          0) /* ArmorType - None */
     , (2000066,  40,          2) /* CombatMode - Melee */
     , (2000066,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000066, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000066, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000066,   1, True ) /* Stuck */
     , (2000066,  11, False) /* IgnoreCollisions */
     , (2000066,  12, True ) /* ReportCollisions */
     , (2000066,  13, False) /* Ethereal */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000066,   1,       5) /* HeartbeatInterval */
     , (2000066,   2,       0) /* HeartbeatTimestamp */
     , (2000066,   3,     1.2) /* HealthRate */
     , (2000066,   4,     0.5) /* StaminaRate */
     , (2000066,   5,       2) /* ManaRate */
     , (2000066,   6,     0.1) /* HealthUponResurrection */
     , (2000066,   7,    0.25) /* StaminaUponResurrection */
     , (2000066,   8,     0.3) /* ManaUponResurrection */
     , (2000066,  12,       0) /* Shade */
     , (2000066,  13,     0.8) /* ArmorModVsSlash */
     , (2000066,  14,     0.8) /* ArmorModVsPierce */
     , (2000066,  15,     0.8) /* ArmorModVsBludgeon */
     , (2000066,  16,       1) /* ArmorModVsCold */
     , (2000066,  17,       1) /* ArmorModVsFire */
     , (2000066,  18,       1) /* ArmorModVsAcid */
     , (2000066,  19,       1) /* ArmorModVsElectric */
     , (2000066,  31,      20) /* VisualAwarenessRange */
     , (2000066,  34,     2.3) /* PowerupTime */
     , (2000066,  39,     0.8) /* DefaultScale */
     , (2000066,  64,    0.33) /* ResistSlash */
     , (2000066,  65,    0.67) /* ResistPierce */
     , (2000066,  66,     0.8) /* ResistBludgeon */
     , (2000066,  67,     0.5) /* ResistFire */
     , (2000066,  68,     0.5) /* ResistCold */
     , (2000066,  69,     0.8) /* ResistAcid */
     , (2000066,  70,     0.5) /* ResistElectric */
     , (2000066,  71,       1) /* ResistHealthBoost */
     , (2000066,  72,       1) /* ResistStaminaDrain */
     , (2000066,  73,       1) /* ResistStaminaBoost */
     , (2000066,  74,       1) /* ResistManaDrain */
     , (2000066,  75,       1) /* ResistManaBoost */
     , (2000066,  76,    0.25) /* Translucency */
     , (2000066,  80,       3) /* AiUseMagicDelay */
     , (2000066, 104,      10) /* ObviousRadarRange */
     , (2000066, 122,       2) /* AiAcquireHealth */
     , (2000066, 123,       2) /* AiAcquireStamina */
     , (2000066, 124,       2) /* AiAcquireMana */
     , (2000066, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000066,   1, 'Drift Colossus') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000066,   1, 0x02001496) /* Setup */
     , (2000066,   2, 0x09000190) /* MotionTable */
     , (2000066,   3, 0x20000015) /* SoundTable */
     , (2000066,   4, 0x30000008) /* CombatTable */
     , (2000066,   6, 0x04001EC3) /* PaletteBase */
     , (2000066,   7, 0x10000637) /* ClothingBase */
     , (2000066,   8, 0x06001224) /* Icon */
     , (2000066,  22, 0x34000064) /* PhysicsEffectTable */
     , (2000066,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000066,   1, 290, 0, 0) /* Strength */
     , (2000066,   2, 300, 0, 0) /* Endurance */
     , (2000066,   3, 190, 0, 0) /* Quickness */
     , (2000066,   4, 200, 0, 0) /* Coordination */
     , (2000066,   5, 190, 0, 0) /* Focus */
     , (2000066,   6, 190, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000066,   1,  1350, 0, 0, 1500) /* MaxHealth */
     , (2000066,   3,  1200, 0, 0, 1500) /* MaxStamina */
     , (2000066,   5,  1000, 0, 0, 1190) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000066,  6, 0, 3, 0, 530, 0, 0) /* MeleeDefense        Specialized */
     , (2000066,  7, 0, 3, 0, 310, 0, 0) /* MissileDefense      Specialized */
     , (2000066, 15, 0, 3, 0, 260, 0, 0) /* MagicDefense        Specialized */
     , (2000066, 20, 0, 2, 0, 100, 0, 0) /* Deception           Trained */
     , (2000066, 22, 0, 2, 0,  10, 0, 0) /* Jump                Trained */
     , (2000066, 24, 0, 2, 0,  10, 0, 0) /* Run                 Trained */
     , (2000066, 45, 0, 3, 0, 490, 0, 0) /* LightWeapons        Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000066,  0,  4,  0,    0,  380,  304,  304,  304,  380,  380,  380,  380,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000066,  1,  4,  0,    0,  380,  304,  304,  304,  380,  380,  380,  380,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000066,  2,  4,  0,    0,  380,  304,  304,  304,  380,  380,  380,  380,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000066,  3,  4,  0,    0,  380,  304,  304,  304,  380,  380,  380,  380,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000066,  4,  4,  0,    0,  380,  304,  304,  304,  380,  380,  380,  380,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000066,  5,  4, 200, 0.75,  380,  304,  304,  304,  380,  380,  380,  380,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000066,  6,  4,  0,    0,  380,  304,  304,  304,  380,  380,  380,  380,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000066,  7,  4,  0,    0,  380,  304,  304,  304,  380,  380,  380,  380,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000066,  8,  4, 200, 0.75,  380,  304,  304,  304,  380,  380,  380,  380,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;
