-- @owner: frays
-- @owns: wcid:2000073
-- @requires-restart: no
--
-- Frays creature, tusker family, elite role: a copy of retail 43593 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.1, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000073;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000073, 'dw-drift-tusker-warrior', 10, '2026-10-29 20:23:12') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000073,   1,         16) /* ItemType - Creature */
     , (2000073,   2,          8) /* CreatureType - Tusker */
     , (2000073,   6,         -1) /* ItemsCapacity */
     , (2000073,   7,         -1) /* ContainersCapacity */
     , (2000073,  16,          1) /* ItemUseable - No */
     , (2000073,  25,        240) /* Level */
     , (2000073,  27,          0) /* ArmorType - None */
     , (2000073,  40,          2) /* CombatMode - Melee */
     , (2000073,  68,          9) /* TargetingTactic - Random, TopDamager */
     , (2000073,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000073, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000073, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000073,   1, True ) /* Stuck */
     , (2000073,  11, False) /* IgnoreCollisions */
     , (2000073,  12, True ) /* ReportCollisions */
     , (2000073,  13, False) /* Ethereal */
     , (2000073,  14, True ) /* GravityStatus */
     , (2000073,  19, True ) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000073,   1,       5) /* HeartbeatInterval */
     , (2000073,   2,       0) /* HeartbeatTimestamp */
     , (2000073,   3,     0.8) /* HealthRate */
     , (2000073,   4,     0.8) /* StaminaRate */
     , (2000073,   5,       2) /* ManaRate */
     , (2000073,  12,       0) /* Shade */
     , (2000073,  13,       1) /* ArmorModVsSlash */
     , (2000073,  14,     0.6) /* ArmorModVsPierce */
     , (2000073,  15,       1) /* ArmorModVsBludgeon */
     , (2000073,  16,       1) /* ArmorModVsCold */
     , (2000073,  17,     0.4) /* ArmorModVsFire */
     , (2000073,  18,       1) /* ArmorModVsAcid */
     , (2000073,  19,       1) /* ArmorModVsElectric */
     , (2000073,  31,      23) /* VisualAwarenessRange */
     , (2000073,  34,     2.5) /* PowerupTime */
     , (2000073,  36,       1) /* ChargeSpeed */
     , (2000073,  39,    1.65) /* DefaultScale */
     , (2000073,  64,     0.5) /* ResistSlash */
     , (2000073,  65,     0.7) /* ResistPierce */
     , (2000073,  66,     0.3) /* ResistBludgeon */
     , (2000073,  67,     0.8) /* ResistFire */
     , (2000073,  68,     0.3) /* ResistCold */
     , (2000073,  69,     0.3) /* ResistAcid */
     , (2000073,  70,     0.4) /* ResistElectric */
     , (2000073,  71,       1) /* ResistHealthBoost */
     , (2000073,  72,       1) /* ResistStaminaDrain */
     , (2000073,  73,       1) /* ResistStaminaBoost */
     , (2000073,  74,       1) /* ResistManaDrain */
     , (2000073,  76,    0.25) /* Translucency */
     , (2000073, 104,      10) /* ObviousRadarRange */
     , (2000073, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000073,   1, 'Drift Tusker Warrior') /* Name */
     , (2000073,  45, 'snowtuskerkillwarriorcount') /* KillQuest */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000073,   1, 0x02001A34) /* Setup */
     , (2000073,   2, 0x0900000C) /* MotionTable */
     , (2000073,   3, 0x20000011) /* SoundTable */
     , (2000073,   4, 0x3000000B) /* CombatTable */
     , (2000073,   8, 0x06001033) /* Icon */
     , (2000073,  22, 0x34000027) /* PhysicsEffectTable */
     , (2000073,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000073,   1, 370, 0, 0) /* Strength */
     , (2000073,   2, 440, 0, 0) /* Endurance */
     , (2000073,   3, 290, 0, 0) /* Quickness */
     , (2000073,   4, 340, 0, 0) /* Coordination */
     , (2000073,   5,  70, 0, 0) /* Focus */
     , (2000073,   6,  60, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000073,   1,  2250, 0, 0, 2470) /* MaxHealth */
     , (2000073,   3,     0, 0, 0, 2390) /* MaxStamina */
     , (2000073,   5,     0, 0, 0, 60) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000073,  6, 0, 3, 0, 390, 0, 0) /* MeleeDefense        Specialized */
     , (2000073,  7, 0, 3, 0, 415, 0, 0) /* MissileDefense      Specialized */
     , (2000073, 15, 0, 3, 0, 470, 0, 0) /* MagicDefense        Specialized */
     , (2000073, 20, 0, 3, 0,  25, 0, 0) /* Deception           Specialized */
     , (2000073, 22, 0, 3, 0, 120, 0, 0) /* Jump                Specialized */
     , (2000073, 24, 0, 3, 0, 500, 0, 0) /* Run                 Specialized */
     , (2000073, 45, 0, 3, 0, 455, 0, 0) /* LightWeapons        Specialized */
     , (2000073, 46, 0, 3, 0, 455, 0, 0) /* FinesseWeapons      Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000073,  0,  4,  0,    0,  650,  650,  390,  650,  650,  260,  650,  650,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000073,  1,  4,  0,    0,  650,  650,  390,  650,  650,  260,  650,  650,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000073,  2,  4,  0,    0,  650,  650,  390,  650,  650,  260,  650,  650,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000073,  3,  4,  0,    0,  650,  650,  390,  650,  650,  260,  650,  650,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000073,  4,  4,  0,    0,  650,  650,  390,  650,  650,  260,  650,  650,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000073,  5,  4, 375, 0.75,  650,  650,  390,  650,  650,  260,  650,  650,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000073,  6,  4,  0,    0,  650,  650,  390,  650,  650,  260,  650,  650,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000073,  7,  4,  0,    0,  650,  650,  390,  650,  650,  260,  650,  650,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000073,  8,  4, 375, 0.75,  650,  650,  390,  650,  650,  260,  650,  650,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000073, 9, 43742,  0, 0, 0.3, False) /* Create Snow Tusker Blood Sample (43742) for ContainTreasure */
     , (2000073, 9,     0,  0, 0, 0.7, False) /* Create nothing for ContainTreasure */
     , (2000073, 9, 48908,  1, 0, 0.01, False) /* Create Shattered Legendary Key (48908) for ContainTreasure */
     , (2000073, 9,     0,  0, 0, 0.99, False) /* Create nothing for ContainTreasure */
     , (2000073, 9, 51370,  1, 0, 0.03, False) /* Create Frozen Fortress Testing Grounds Attunement Shard (Level 180+) (51370) for ContainTreasure */
     , (2000073, 9, 51341,  1, 0, 0.03, False) /* Create Frozen Fortress Laboratory Attunement Shard (Level 180+) (51341) for ContainTreasure */
     , (2000073, 9,     0,  0, 0, 0.94, False) /* Create nothing for ContainTreasure */;
