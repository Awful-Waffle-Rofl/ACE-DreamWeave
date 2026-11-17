-- @owner: frays
-- @owns: wcid:2000067
-- @requires-restart: no
--
-- Frays creature, undead family, swarm role: a copy of retail 33735 without spells, emotes or generators; dream tint (Translucency 0.25), size x0.9, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000067;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000067, 'dw-drift-husk', 10, '2026-10-29 20:23:11') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000067,   1,         16) /* ItemType - Creature */
     , (2000067,   2,         14) /* CreatureType - Undead */
     , (2000067,   6,         -1) /* ItemsCapacity */
     , (2000067,   7,         -1) /* ContainersCapacity */
     , (2000067,  16,          1) /* ItemUseable - No */
     , (2000067,  25,        185) /* Level */
     , (2000067,  27,          0) /* ArmorType - None */
     , (2000067,  40,          2) /* CombatMode - Melee */
     , (2000067,  68,          9) /* TargetingTactic - Random, TopDamager */
     , (2000067,  81,          1) /* MaxGeneratedObjects */
     , (2000067,  82,          0) /* InitGeneratedObjects */
     , (2000067,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000067, 101,        131) /* AiAllowedCombatStyle - Unarmed, OneHanded, ThrownWeapon */
     , (2000067, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000067, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000067, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000067,   1, True ) /* Stuck */
     , (2000067,  11, False) /* IgnoreCollisions */
     , (2000067,  12, True ) /* ReportCollisions */
     , (2000067,  13, False) /* Ethereal */
     , (2000067,  14, True ) /* GravityStatus */
     , (2000067,  19, True ) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000067,   1,       5) /* HeartbeatInterval */
     , (2000067,   2,       0) /* HeartbeatTimestamp */
     , (2000067,   3,    0.15) /* HealthRate */
     , (2000067,   4,       4) /* StaminaRate */
     , (2000067,   5,     1.5) /* ManaRate */
     , (2000067,  12,       0) /* Shade */
     , (2000067,  13,     0.9) /* ArmorModVsSlash */
     , (2000067,  14,     0.6) /* ArmorModVsPierce */
     , (2000067,  15,       1) /* ArmorModVsBludgeon */
     , (2000067,  16,     0.8) /* ArmorModVsCold */
     , (2000067,  17,    0.55) /* ArmorModVsFire */
     , (2000067,  18,       1) /* ArmorModVsAcid */
     , (2000067,  19,     0.8) /* ArmorModVsElectric */
     , (2000067,  31,      17) /* VisualAwarenessRange */
     , (2000067,  34,       1) /* PowerupTime */
     , (2000067,  36,       1) /* ChargeSpeed */
     , (2000067,  39,     0.9) /* DefaultScale */
     , (2000067,  43,       4) /* GeneratorRadius */
     , (2000067,  64,     0.4) /* ResistSlash */
     , (2000067,  65,    0.75) /* ResistPierce */
     , (2000067,  66,     0.4) /* ResistBludgeon */
     , (2000067,  67,    0.75) /* ResistFire */
     , (2000067,  68,     0.2) /* ResistCold */
     , (2000067,  69,     0.4) /* ResistAcid */
     , (2000067,  70,     0.4) /* ResistElectric */
     , (2000067,  71,       1) /* ResistHealthBoost */
     , (2000067,  72,     0.5) /* ResistStaminaDrain */
     , (2000067,  73,       1) /* ResistStaminaBoost */
     , (2000067,  74,     0.5) /* ResistManaDrain */
     , (2000067,  75,       1) /* ResistManaBoost */
     , (2000067,  76,    0.25) /* Translucency */
     , (2000067, 104,      10) /* ObviousRadarRange */
     , (2000067, 125,     0.5) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000067,   1, 'Drift Husk') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000067,   1, 0x020015CD) /* Setup */
     , (2000067,   2, 0x09000007) /* MotionTable */
     , (2000067,   3, 0x200000BD) /* SoundTable */
     , (2000067,   4, 0x30000004) /* CombatTable */
     , (2000067,   8, 0x060036FD) /* Icon */
     , (2000067,  22, 0x34000084) /* PhysicsEffectTable */
     , (2000067,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000067,   1, 310, 0, 0) /* Strength */
     , (2000067,   2, 240, 0, 0) /* Endurance */
     , (2000067,   3, 200, 0, 0) /* Quickness */
     , (2000067,   4, 240, 0, 0) /* Coordination */
     , (2000067,   5, 210, 0, 0) /* Focus */
     , (2000067,   6, 210, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000067,   1,   740, 0, 0, 860) /* MaxHealth */
     , (2000067,   3,   800, 0, 0, 1040) /* MaxStamina */
     , (2000067,   5,   200, 0, 0, 410) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000067,  6, 0, 2, 0, 456, 0, 0) /* MeleeDefense        Trained */
     , (2000067,  7, 0, 2, 0, 500, 0, 0) /* MissileDefense      Trained */
     , (2000067, 15, 0, 2, 0, 400, 0, 0) /* MagicDefense        Trained */
     , (2000067, 44, 0, 2, 0, 430, 0, 0) /* HeavyWeapons        Trained */
     , (2000067, 45, 0, 2, 0, 430, 0, 0) /* LightWeapons        Trained */
     , (2000067, 46, 0, 2, 0, 466, 0, 0) /* FinesseWeapons      Trained */
     , (2000067, 47, 0, 2, 0, 196, 0, 0) /* MissileWeapons      Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000067,  0,  4,  0,    0,  450,  405,  270,  450,  360,  248,  450,  360,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000067,  1,  4,  0,    0,  450,  405,  270,  450,  360,  248,  450,  360,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000067,  2,  4,  0,    0,  450,  405,  270,  450,  360,  248,  450,  360,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000067,  3,  4,  0,    0,  450,  405,  270,  450,  360,  248,  450,  360,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000067,  4,  4,  0,    0,  450,  405,  270,  450,  360,  248,  450,  360,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000067,  5,  4, 60,  0.5,  450,  405,  270,  450,  360,  248,  450,  360,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000067,  6,  4,  0,    0,  450,  405,  270,  450,  360,  248,  450,  360,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000067,  7,  4,  0,    0,  450,  405,  270,  450,  360,  248,  450,  360,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000067,  8,  4, 50,  0.4,  450,  405,  270,  450,  360,  248,  450,  360,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000067, 9, 41979,  1, 0, 0.02, False) /* Create Shattered Mana Forge Key (41979) for ContainTreasure */
     , (2000067, 9,     0,  0, 0, 0.98, False) /* Create nothing for ContainTreasure */
     , (2000067, 9, 34277,  1, 0, 0.02, False) /* Create Ancient Falatacot Trinket (34277) for ContainTreasure */
     , (2000067, 9,     0,  0, 0, 0.98, False) /* Create nothing for ContainTreasure */
     , (2000067, 10, 48633,  1, 0, 0.2, False) /* Create Glacial Blade (48633) for WieldTreasure */
     , (2000067, 10, 48630,  1, 0, 0.2, False) /* Create Frozen Dagger (48630) for WieldTreasure */
     , (2000067, 10, 48631,  1, 0, 0.2, False) /* Create Ice Shard (48631) for WieldTreasure */
     , (2000067, 10, 48632,  1, 0, 0.2, False) /* Create Frigid Splinter (48632) for WieldTreasure */
     , (2000067, 10, 48629,  1, 0, 0.2, False) /* Create Icy Club (48629) for WieldTreasure */;
