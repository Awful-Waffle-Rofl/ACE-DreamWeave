-- @owner: frays
-- @owns: wcid:2000060
-- @requires-restart: no
--
-- Frays creature, olthoi family, standard role: a copy of retail 31005 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000060;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000060, 'dw-drift-ripper', 10, '2026-10-29 20:23:10') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000060,   1,         16) /* ItemType - Creature */
     , (2000060,   2,          1) /* CreatureType - Olthoi */
     , (2000060,   6,         -1) /* ItemsCapacity */
     , (2000060,   7,         -1) /* ContainersCapacity */
     , (2000060,  16,          1) /* ItemUseable - No */
     , (2000060,  25,        185) /* Level */
     , (2000060,  68,         13) /* TargetingTactic - Random, LastDamager, TopDamager */
     , (2000060,  81,          2) /* MaxGeneratedObjects */
     , (2000060,  82,          0) /* InitGeneratedObjects */
     , (2000060,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000060, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000060, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000060, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000060,   1, True ) /* Stuck */
     , (2000060,  12, True ) /* ReportCollisions */
     , (2000060,  14, True ) /* GravityStatus */
     , (2000060,  19, True ) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000060,   1,       5) /* HeartbeatInterval */
     , (2000060,   2,       0) /* HeartbeatTimestamp */
     , (2000060,   3,     0.7) /* HealthRate */
     , (2000060,   4,       4) /* StaminaRate */
     , (2000060,   5,       2) /* ManaRate */
     , (2000060,  12,     0.5) /* Shade */
     , (2000060,  13,    0.69) /* ArmorModVsSlash */
     , (2000060,  14,     0.8) /* ArmorModVsPierce */
     , (2000060,  15,     0.6) /* ArmorModVsBludgeon */
     , (2000060,  16,       1) /* ArmorModVsCold */
     , (2000060,  17,       1) /* ArmorModVsFire */
     , (2000060,  18,     1.1) /* ArmorModVsAcid */
     , (2000060,  19,       1) /* ArmorModVsElectric */
     , (2000060,  31,      24) /* VisualAwarenessRange */
     , (2000060,  34,     0.5) /* PowerupTime */
     , (2000060,  36,       1) /* ChargeSpeed */
     , (2000060,  39,       1) /* DefaultScale */
     , (2000060,  43,       1) /* GeneratorRadius */
     , (2000060,  64,    0.75) /* ResistSlash */
     , (2000060,  65,       1) /* ResistPierce */
     , (2000060,  66,       1) /* ResistBludgeon */
     , (2000060,  67,    0.75) /* ResistFire */
     , (2000060,  68,    0.75) /* ResistCold */
     , (2000060,  69,    0.42) /* ResistAcid */
     , (2000060,  70,    0.25) /* ResistElectric */
     , (2000060,  71,    0.25) /* ResistHealthBoost */
     , (2000060,  72,    0.25) /* ResistStaminaDrain */
     , (2000060,  73,    0.25) /* ResistStaminaBoost */
     , (2000060,  74,    0.25) /* ResistManaDrain */
     , (2000060,  75,    0.25) /* ResistManaBoost */
     , (2000060,  76,    0.25) /* Translucency */
     , (2000060,  77,       1) /* PhysicsScriptIntensity */
     , (2000060, 104,      10) /* ObviousRadarRange */
     , (2000060, 117,     0.6) /* FocusedProbability */
     , (2000060, 125,    0.25) /* ResistHealthDrain */
     , (2000060, 151,       1) /* IgnoreShield */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000060,   1, 'Drift Ripper') /* Name */
     , (2000060,  45, 'olthoiripperkillcount') /* KillQuest */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000060,   1, 0x02000A36) /* Setup */
     , (2000060,   2, 0x090000BA) /* MotionTable */
     , (2000060,   3, 0x2000007C) /* SoundTable */
     , (2000060,   4, 0x3000001B) /* CombatTable */
     , (2000060,   8, 0x060010E7) /* Icon */
     , (2000060,  22, 0x34000092) /* PhysicsEffectTable */
     , (2000060,  30,         86) /* PhysicsScript - BreatheAcid */
     , (2000060,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000060,   1, 390, 0, 0) /* Strength */
     , (2000060,   2, 390, 0, 0) /* Endurance */
     , (2000060,   3, 220, 0, 0) /* Quickness */
     , (2000060,   4, 220, 0, 0) /* Coordination */
     , (2000060,   5, 170, 0, 0) /* Focus */
     , (2000060,   6, 120, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000060,   1,  2505, 0, 0, 2700) /* MaxHealth */
     , (2000060,   3,  3000, 0, 0, 3390) /* MaxStamina */
     , (2000060,   5,     0, 0, 0, 120) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000060,  6, 0, 3, 0, 380, 0, 0) /* MeleeDefense        Specialized */
     , (2000060,  7, 0, 3, 0, 395, 0, 0) /* MissileDefense      Specialized */
     , (2000060, 15, 0, 3, 0, 350, 0, 0) /* MagicDefense        Specialized */
     , (2000060, 20, 0, 2, 0, 100, 0, 0) /* Deception           Trained */
     , (2000060, 22, 0, 2, 0, 200, 0, 0) /* Jump                Trained */
     , (2000060, 24, 0, 2, 0, 350, 0, 0) /* Run                 Trained */
     , (2000060, 45, 0, 3, 0, 440, 0, 0) /* LightWeapons        Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000060,  0,  4,  5,    0,  350,  241,  280,  210,  350,  350,  385,  350,    0, 1,  0.1,    0,    0,  0.1,    0,    0,  0.1,    0,    0,  0.1,    0,    0) /* Head */
     , (2000060, 16,  4,  5,    0,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45) /* Torso */
     , (2000060, 18,  2, 140,  0.5,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2,    0,  0.2,  0.1,    0,  0.2,  0.1,    0,  0.2,  0.1,    0,  0.2,  0.1) /* Arm */
     , (2000060, 19,  2, 140, 0.75,  350,  241,  280,  210,  350,  350,  385,  350,    0, 3,    0,  0.2, 0.45,    0,  0.2, 0.45,    0,  0.2, 0.45,    0,  0.2, 0.45) /* Leg */
     , (2000060, 20,  1, 140, 0.75,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2, 0.45,  0.2,    0, 0.45,  0.2,    0, 0.45,  0.2,    0, 0.45,  0.2,    0) /* Claw */
     , (2000060, 22, 32, 140,  0.5,    0,    0,    0,    0,    0,    0,    0,    0,    0, 0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0) /* Breath */;

INSERT INTO `weenie_properties_event_filter` (`object_Id`, `event`)
VALUES (2000060,  94)
     , (2000060, 414);

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000060, 9, 31354,  0, 0, 0.045, False) /* Create Olthoi Ripper Spine (31354) for ContainTreasure */
     , (2000060, 9,     0,  0, 0, 0.955, False) /* Create nothing for ContainTreasure */
     , (2000060, 9, 24477,  0, 0, 0.03, False) /* Create Sturdy Steel Key (24477) for ContainTreasure */
     , (2000060, 9,     0,  0, 0, 0.97, False) /* Create nothing for ContainTreasure */;
