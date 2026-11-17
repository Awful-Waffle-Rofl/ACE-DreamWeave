-- @owner: frays
-- @owns: wcid:2000071
-- @requires-restart: no
--
-- Frays creature, tusker family, swarm role: a copy of retail 31313 without spells, emotes or generators; dream tint (Translucency 0.25), size x0.9, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000071;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000071, 'dw-drift-stomper', 10, '2026-10-29 20:23:12') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000071,   1,         16) /* ItemType - Creature */
     , (2000071,   2,          8) /* CreatureType - Tusker */
     , (2000071,   3,         39) /* PaletteTemplate - Black */
     , (2000071,   6,         -1) /* ItemsCapacity */
     , (2000071,   7,         -1) /* ContainersCapacity */
     , (2000071,  16,          1) /* ItemUseable - No */
     , (2000071,  25,        185) /* Level */
     , (2000071,  27,          0) /* ArmorType - None */
     , (2000071,  40,          2) /* CombatMode - Melee */
     , (2000071,  68,          9) /* TargetingTactic - Random, TopDamager */
     , (2000071,  72,          8) /* FriendType - Tusker */
     , (2000071,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000071, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000071, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000071,   1, True ) /* Stuck */
     , (2000071,  65, True ) /* IgnoreMagicResist */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000071,   1,       5) /* HeartbeatInterval */
     , (2000071,   2,       0) /* HeartbeatTimestamp */
     , (2000071,   3,      20) /* HealthRate */
     , (2000071,   4,       4) /* StaminaRate */
     , (2000071,   5,       2) /* ManaRate */
     , (2000071,  13,     0.5) /* ArmorModVsSlash */
     , (2000071,  14,     0.8) /* ArmorModVsPierce */
     , (2000071,  15,    0.66) /* ArmorModVsBludgeon */
     , (2000071,  16,       1) /* ArmorModVsCold */
     , (2000071,  17,     0.7) /* ArmorModVsFire */
     , (2000071,  18,       1) /* ArmorModVsAcid */
     , (2000071,  19,       1) /* ArmorModVsElectric */
     , (2000071,  31,      20) /* VisualAwarenessRange */
     , (2000071,  34,     2.5) /* PowerupTime */
     , (2000071,  36,       1) /* ChargeSpeed */
     , (2000071,  39,    1.17) /* DefaultScale */
     , (2000071,  64,     0.5) /* ResistSlash */
     , (2000071,  65,       1) /* ResistPierce */
     , (2000071,  66,    0.75) /* ResistBludgeon */
     , (2000071,  67,       1) /* ResistFire */
     , (2000071,  68,    0.75) /* ResistCold */
     , (2000071,  69,    0.75) /* ResistAcid */
     , (2000071,  70,    0.25) /* ResistElectric */
     , (2000071,  71,       1) /* ResistHealthBoost */
     , (2000071,  72,     0.1) /* ResistStaminaDrain */
     , (2000071,  73,       1) /* ResistStaminaBoost */
     , (2000071,  74,     0.1) /* ResistManaDrain */
     , (2000071,  75,       1) /* ResistManaBoost */
     , (2000071,  76,    0.25) /* Translucency */
     , (2000071, 104,      10) /* ObviousRadarRange */
     , (2000071, 125,     0.1) /* ResistHealthDrain */
     , (2000071, 151,       1) /* IgnoreShield */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000071,   1, 'Drift Stomper') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000071,   1, 0x02000964) /* Setup */
     , (2000071,   2, 0x0900000C) /* MotionTable */
     , (2000071,   3, 0x20000011) /* SoundTable */
     , (2000071,   4, 0x3000000B) /* CombatTable */
     , (2000071,   6, 0x0400102F) /* PaletteBase */
     , (2000071,   7, 0x10000403) /* ClothingBase */
     , (2000071,   8, 0x06001033) /* Icon */
     , (2000071,  22, 0x34000027) /* PhysicsEffectTable */
     , (2000071,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000071,   1, 270, 0, 0) /* Strength */
     , (2000071,   2, 350, 0, 0) /* Endurance */
     , (2000071,   3, 240, 0, 0) /* Quickness */
     , (2000071,   4, 260, 0, 0) /* Coordination */
     , (2000071,   5, 110, 0, 0) /* Focus */
     , (2000071,   6,  90, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000071,   1,   325, 0, 0, 500) /* MaxHealth */
     , (2000071,   3,   900, 0, 0, 1250) /* MaxStamina */
     , (2000071,   5,     0, 0, 0, 90) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000071,  6, 0, 3, 0, 310, 0, 0) /* MeleeDefense        Specialized */
     , (2000071,  7, 0, 3, 0, 425, 0, 0) /* MissileDefense      Specialized */
     , (2000071, 13, 0, 3, 0, 300, 0, 0) /* UnarmedCombat       Specialized */
     , (2000071, 15, 0, 3, 0, 360, 0, 0) /* MagicDefense        Specialized */
     , (2000071, 20, 0, 3, 0,  25, 0, 0) /* Deception           Specialized */
     , (2000071, 22, 0, 3, 0, 120, 0, 0) /* Jump                Specialized */
     , (2000071, 24, 0, 3, 0,  70, 0, 0) /* Run                 Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000071,  0,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000071,  1,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000071,  2,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000071,  3,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000071,  4,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000071,  5,  4, 60, 0.75,  580,  290,  464,  383,  580,  406,  580,  580,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000071,  6,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000071,  7,  4,  0,    0,  580,  290,  464,  383,  580,  406,  580,  580,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000071,  8,  4, 75, 0.75,  580,  290,  464,  383,  580,  406,  580,  580,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000071, 9, 11692,  0, 0, 0.01, False) /* Create Little Green Seeds (11692) for ContainTreasure */
     , (2000071, 9,     0,  0, 0, 0.99, False) /* Create nothing for ContainTreasure */
     , (2000071, 9, 22578,  0, 0, 0.01, False) /* Create Bunch of Nanners (22578) for ContainTreasure */
     , (2000071, 9,     0,  0, 0, 0.99, False) /* Create nothing for ContainTreasure */
     , (2000071, 9, 29204,  0, 0, 0.01, False) /* Create Tusker Spit (29204) for ContainTreasure */
     , (2000071, 9,     0,  0, 0, 0.99, False) /* Create nothing for ContainTreasure */;
