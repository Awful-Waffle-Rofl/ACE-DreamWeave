-- @owner: frays
-- @owns: wcid:2000072
-- @requires-restart: no
--
-- Frays creature, tusker family, standard role: a copy of retail 43733 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000072;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000072, 'dw-drift-tusker', 10, '2026-10-29 20:23:12') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000072,   1,         16) /* ItemType - Creature */
     , (2000072,   2,          8) /* CreatureType - Tusker */
     , (2000072,   3,         39) /* PaletteTemplate - Black */
     , (2000072,   6,         -1) /* ItemsCapacity */
     , (2000072,   7,         -1) /* ContainersCapacity */
     , (2000072,  16,          1) /* ItemUseable - No */
     , (2000072,  25,        200) /* Level */
     , (2000072,  27,          0) /* ArmorType - None */
     , (2000072,  40,          2) /* CombatMode - Melee */
     , (2000072,  68,          9) /* TargetingTactic - Random, TopDamager */
     , (2000072,  72,          8) /* FriendType - Tusker */
     , (2000072,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000072, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000072, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000072,   1, True ) /* Stuck */
     , (2000072,  11, False) /* IgnoreCollisions */
     , (2000072,  12, True ) /* ReportCollisions */
     , (2000072,  13, False) /* Ethereal */
     , (2000072,  14, True ) /* GravityStatus */
     , (2000072,  19, True ) /* Attackable */
     , (2000072,  42, True ) /* AllowEdgeSlide */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000072,   1,       5) /* HeartbeatInterval */
     , (2000072,   2,       0) /* HeartbeatTimestamp */
     , (2000072,   3,      20) /* HealthRate */
     , (2000072,   4,       4) /* StaminaRate */
     , (2000072,   5,       2) /* ManaRate */
     , (2000072,  13,     0.5) /* ArmorModVsSlash */
     , (2000072,  14,     0.8) /* ArmorModVsPierce */
     , (2000072,  15,    0.66) /* ArmorModVsBludgeon */
     , (2000072,  16,       1) /* ArmorModVsCold */
     , (2000072,  17,     0.7) /* ArmorModVsFire */
     , (2000072,  18,       1) /* ArmorModVsAcid */
     , (2000072,  19,       1) /* ArmorModVsElectric */
     , (2000072,  31,      20) /* VisualAwarenessRange */
     , (2000072,  34,     2.5) /* PowerupTime */
     , (2000072,  36,       1) /* ChargeSpeed */
     , (2000072,  39,     1.5) /* DefaultScale */
     , (2000072,  64,     0.5) /* ResistSlash */
     , (2000072,  65,       1) /* ResistPierce */
     , (2000072,  66,    0.75) /* ResistBludgeon */
     , (2000072,  67,       1) /* ResistFire */
     , (2000072,  68,    0.75) /* ResistCold */
     , (2000072,  69,    0.75) /* ResistAcid */
     , (2000072,  70,    0.25) /* ResistElectric */
     , (2000072,  71,       1) /* ResistHealthBoost */
     , (2000072,  72,     0.1) /* ResistStaminaDrain */
     , (2000072,  73,       1) /* ResistStaminaBoost */
     , (2000072,  74,     0.1) /* ResistManaDrain */
     , (2000072,  75,       1) /* ResistManaBoost */
     , (2000072,  76,    0.25) /* Translucency */
     , (2000072, 104,      10) /* ObviousRadarRange */
     , (2000072, 125,     0.1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000072,   1, 'Drift Tusker') /* Name */
     , (2000072,  45, 'snowtuskerkillcount') /* KillQuest */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000072,   1, 0x02001A33) /* Setup */
     , (2000072,   2, 0x0900000C) /* MotionTable */
     , (2000072,   3, 0x20000011) /* SoundTable */
     , (2000072,   4, 0x3000000B) /* CombatTable */
     , (2000072,   6, 0x0400102F) /* PaletteBase */
     , (2000072,   7, 0x10000403) /* ClothingBase */
     , (2000072,   8, 0x06001033) /* Icon */
     , (2000072,  22, 0x34000027) /* PhysicsEffectTable */
     , (2000072,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000072,   1, 360, 0, 0) /* Strength */
     , (2000072,   2, 430, 0, 0) /* Endurance */
     , (2000072,   3, 280, 0, 0) /* Quickness */
     , (2000072,   4, 330, 0, 0) /* Coordination */
     , (2000072,   5,  70, 0, 0) /* Focus */
     , (2000072,   6,  50, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000072,   1,  2000, 0, 0, 2215) /* MaxHealth */
     , (2000072,   3,  1900, 0, 0, 2330) /* MaxStamina */
     , (2000072,   5,     0, 0, 0, 50) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000072,  6, 0, 3, 0, 310, 0, 0) /* MeleeDefense        Specialized */
     , (2000072,  7, 0, 3, 0, 425, 0, 0) /* MissileDefense      Specialized */
     , (2000072, 15, 0, 3, 0, 360, 0, 0) /* MagicDefense        Specialized */
     , (2000072, 20, 0, 3, 0,  25, 0, 0) /* Deception           Specialized */
     , (2000072, 22, 0, 3, 0, 120, 0, 0) /* Jump                Specialized */
     , (2000072, 24, 0, 3, 0,  70, 0, 0) /* Run                 Specialized */
     , (2000072, 45, 0, 3, 0, 300, 0, 0) /* LightWeapons        Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000072,  0,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000072,  1,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000072,  2,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000072,  3,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000072,  4,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000072,  5,  4, 60, 0.75,  580,  290,  464,  383,  580,  406,  580,  580,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000072,  6,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000072,  7,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000072,  8,  4, 75, 0.75,  580,  290,  464,  383,  580,  406,  580,  580,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000072, 9, 41979,  1, 0, 0.1, False) /* Create Shattered Mana Forge Key (41979) for ContainTreasure */
     , (2000072, 9,     0,  0, 0, 0.9, False) /* Create nothing for ContainTreasure */
     , (2000072, 9, 43742,  1, 0, 0.2, False) /* Create Snow Tusker Blood Sample (43742) for ContainTreasure */
     , (2000072, 9,     0,  0, 0, 0.8, False) /* Create nothing for ContainTreasure */;
