-- @owner: frays
-- @owns: wcid:2000070
-- @requires-restart: no
--
-- Frays creature, undead family, snarl role: a copy of retail 33639 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000070;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000070, 'dw-drift-chieftain', 10, '2026-10-29 20:23:11') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000070,   1,         16) /* ItemType - Creature */
     , (2000070,   2,         14) /* CreatureType - Undead */
     , (2000070,   6,         -1) /* ItemsCapacity */
     , (2000070,   7,         -1) /* ContainersCapacity */
     , (2000070,  16,          1) /* ItemUseable - No */
     , (2000070,  25,        185) /* Level */
     , (2000070,  27,          0) /* ArmorType - None */
     , (2000070,  40,          2) /* CombatMode - Melee */
     , (2000070,  68,          9) /* TargetingTactic - Random, TopDamager */
     , (2000070,  81,          2) /* MaxGeneratedObjects */
     , (2000070,  82,          2) /* InitGeneratedObjects */
     , (2000070,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000070, 101,        131) /* AiAllowedCombatStyle - Unarmed, OneHanded, ThrownWeapon */
     , (2000070, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000070, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000070, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000070,   1, True ) /* Stuck */
     , (2000070,  11, False) /* IgnoreCollisions */
     , (2000070,  12, True ) /* ReportCollisions */
     , (2000070,  13, False) /* Ethereal */
     , (2000070,  14, True ) /* GravityStatus */
     , (2000070,  19, True ) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000070,   1,       5) /* HeartbeatInterval */
     , (2000070,   2,       0) /* HeartbeatTimestamp */
     , (2000070,   3,    0.15) /* HealthRate */
     , (2000070,   4,       4) /* StaminaRate */
     , (2000070,   5,     1.5) /* ManaRate */
     , (2000070,  12,       0) /* Shade */
     , (2000070,  13,     0.9) /* ArmorModVsSlash */
     , (2000070,  14,     0.6) /* ArmorModVsPierce */
     , (2000070,  15,       1) /* ArmorModVsBludgeon */
     , (2000070,  16,     0.8) /* ArmorModVsCold */
     , (2000070,  17,     0.6) /* ArmorModVsFire */
     , (2000070,  18,       1) /* ArmorModVsAcid */
     , (2000070,  19,     0.8) /* ArmorModVsElectric */
     , (2000070,  31,      17) /* VisualAwarenessRange */
     , (2000070,  34,       1) /* PowerupTime */
     , (2000070,  36,       1) /* ChargeSpeed */
     , (2000070,  39,     1.3) /* DefaultScale */
     , (2000070,  41,      60) /* RegenerationInterval */
     , (2000070,  43,       4) /* GeneratorRadius */
     , (2000070,  64,     0.2) /* ResistSlash */
     , (2000070,  65,     0.4) /* ResistPierce */
     , (2000070,  66,     0.2) /* ResistBludgeon */
     , (2000070,  67,     0.4) /* ResistFire */
     , (2000070,  68,     0.2) /* ResistCold */
     , (2000070,  69,     0.2) /* ResistAcid */
     , (2000070,  70,     0.2) /* ResistElectric */
     , (2000070,  71,       1) /* ResistHealthBoost */
     , (2000070,  72,     0.5) /* ResistStaminaDrain */
     , (2000070,  73,       1) /* ResistStaminaBoost */
     , (2000070,  74,     0.5) /* ResistManaDrain */
     , (2000070,  75,       1) /* ResistManaBoost */
     , (2000070,  76,    0.25) /* Translucency */
     , (2000070, 104,      10) /* ObviousRadarRange */
     , (2000070, 125,     0.5) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000070,   1, 'Drift Chieftain') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000070,   1, 0x020015CD) /* Setup */
     , (2000070,   2, 0x09000007) /* MotionTable */
     , (2000070,   3, 0x200000BD) /* SoundTable */
     , (2000070,   4, 0x30000004) /* CombatTable */
     , (2000070,   8, 0x060036FD) /* Icon */
     , (2000070,  22, 0x34000084) /* PhysicsEffectTable */
     , (2000070,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000070,   1, 460, 0, 0) /* Strength */
     , (2000070,   2, 390, 0, 0) /* Endurance */
     , (2000070,   3, 350, 0, 0) /* Quickness */
     , (2000070,   4, 390, 0, 0) /* Coordination */
     , (2000070,   5, 390, 0, 0) /* Focus */
     , (2000070,   6, 390, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000070,   1,  9000, 0, 0, 9195) /* MaxHealth */
     , (2000070,   3,  3000, 0, 0, 3390) /* MaxStamina */
     , (2000070,   5,   390, 0, 0, 410) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000070,  6, 0, 2, 0, 411, 0, 0) /* MeleeDefense        Trained */
     , (2000070,  7, 0, 2, 0, 472, 0, 0) /* MissileDefense      Trained */
     , (2000070, 15, 0, 2, 0, 349, 0, 0) /* MagicDefense        Trained */
     , (2000070, 44, 0, 2, 0, 440, 0, 0) /* HeavyWeapons        Trained */
     , (2000070, 45, 0, 2, 0, 440, 0, 0) /* LightWeapons        Trained */
     , (2000070, 46, 0, 2, 0, 476, 0, 0) /* FinesseWeapons      Trained */
     , (2000070, 47, 0, 2, 0, 121, 0, 0) /* MissileWeapons      Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000070,  0,  4,  0,    0,  300,  270,  180,  300,  240,  180,  300,  240,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000070,  1,  4,  0,    0,  300,  270,  180,  300,  240,  180,  300,  240,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000070,  2,  4,  0,    0,  300,  270,  180,  300,  240,  180,  300,  240,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000070,  3,  4,  0,    0,  300,  270,  180,  300,  240,  180,  300,  240,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000070,  4,  4,  0,    0,  300,  270,  180,  300,  240,  180,  300,  240,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000070,  5,  4, 60,  0.5,  300,  270,  180,  300,  240,  180,  300,  240,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000070,  6,  4,  0,    0,  300,  270,  180,  300,  240,  180,  300,  240,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000070,  7,  4,  0,    0,  300,  270,  180,  300,  240,  180,  300,  240,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000070,  8,  4, 50,  0.4,  300,  270,  180,  300,  240,  180,  300,  240,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000070, 9, 44469,  1, 0, 0, False) /* Create Lesser Corrupted Essence (44469) for ContainTreasure */
     , (2000070, 9, 41979,  1, 0, 0.02, False) /* Create Shattered Mana Forge Key (41979) for ContainTreasure */
     , (2000070, 9,     0,  0, 0, 0.98, False) /* Create nothing for ContainTreasure */
     , (2000070, 9, 34277,  1, 0, 0.02, False) /* Create Ancient Falatacot Trinket (34277) for ContainTreasure */
     , (2000070, 9,     0,  0, 0, 0.98, False) /* Create nothing for ContainTreasure */
     , (2000070, 10, 48633,  1, 0, 0.2, False) /* Create Glacial Blade (48633) for WieldTreasure */
     , (2000070, 10, 48630,  1, 0, 0.2, False) /* Create Frozen Dagger (48630) for WieldTreasure */
     , (2000070, 10, 48631,  1, 0, 0.2, False) /* Create Ice Shard (48631) for WieldTreasure */
     , (2000070, 10, 48632,  1, 0, 0.2, False) /* Create Frigid Splinter (48632) for WieldTreasure */
     , (2000070, 10, 48629,  1, 0, 0.2, False) /* Create Icy Club (48629) for WieldTreasure */;
