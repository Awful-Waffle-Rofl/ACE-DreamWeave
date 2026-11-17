-- @owner: frays
-- @owns: wcid:2000062
-- @requires-restart: no
--
-- Frays creature, olthoi family, snarl role: a copy of retail 72694 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000062;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000062, 'dw-drift-eviscerator', 10, '2026-10-29 20:23:10') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000062,   1,         16) /* ItemType - Creature */
     , (2000062,   2,          1) /* CreatureType - Olthoi */
     , (2000062,   6,         -1) /* ItemsCapacity */
     , (2000062,   7,         -1) /* ContainersCapacity */
     , (2000062,  16,          1) /* ItemUseable - No */
     , (2000062,  25,        220) /* Level */
     , (2000062,  27,          0) /* ArmorType - None */
     , (2000062,  40,          2) /* CombatMode - Melee */
     , (2000062,  68,         13) /* TargetingTactic - Random, LastDamager, TopDamager */
     , (2000062,  72,         35) /* FriendType - OlthoiLarvae */
     , (2000062,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000062, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000062, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000062, 146,          0) /* XpOverride */
     , (2000062, 307,         20) /* DamageRating */
     , (2000062, 308,          5) /* DamageResistRating */
     , (2000062, 316,         10) /* CritDamageResistRating */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000062,   1, True ) /* Stuck */
     , (2000062,  11, False) /* IgnoreCollisions */
     , (2000062,  12, True ) /* ReportCollisions */
     , (2000062,  13, False) /* Ethereal */
     , (2000062,  14, True ) /* GravityStatus */
     , (2000062,  19, True ) /* Attackable */
     , (2000062,  42, True ) /* AllowEdgeSlide */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000062,   1,       5) /* HeartbeatInterval */
     , (2000062,   2,       0) /* HeartbeatTimestamp */
     , (2000062,   3,      80) /* HealthRate */
     , (2000062,   4,       4) /* StaminaRate */
     , (2000062,   5,       2) /* ManaRate */
     , (2000062,  13,     1.1) /* ArmorModVsSlash */
     , (2000062,  14,     0.7) /* ArmorModVsPierce */
     , (2000062,  15,     0.6) /* ArmorModVsBludgeon */
     , (2000062,  16,     0.6) /* ArmorModVsCold */
     , (2000062,  17,       1) /* ArmorModVsFire */
     , (2000062,  18,     1.1) /* ArmorModVsAcid */
     , (2000062,  19,     1.1) /* ArmorModVsElectric */
     , (2000062,  31,      24) /* VisualAwarenessRange */
     , (2000062,  34,       1) /* PowerupTime */
     , (2000062,  36,       1) /* ChargeSpeed */
     , (2000062,  39,     1.1) /* DefaultScale */
     , (2000062,  64,     0.4) /* ResistSlash */
     , (2000062,  65,     0.7) /* ResistPierce */
     , (2000062,  66,     0.9) /* ResistBludgeon */
     , (2000062,  67,     0.4) /* ResistFire */
     , (2000062,  68,     0.8) /* ResistCold */
     , (2000062,  69,     0.4) /* ResistAcid */
     , (2000062,  70,     0.4) /* ResistElectric */
     , (2000062,  71,       1) /* ResistHealthBoost */
     , (2000062,  72,       1) /* ResistStaminaDrain */
     , (2000062,  73,       1) /* ResistStaminaBoost */
     , (2000062,  74,       1) /* ResistManaDrain */
     , (2000062,  75,       1) /* ResistManaBoost */
     , (2000062,  76,    0.25) /* Translucency */
     , (2000062,  77,       1) /* PhysicsScriptIntensity */
     , (2000062, 104,      10) /* ObviousRadarRange */
     , (2000062, 117,     0.6) /* FocusedProbability */
     , (2000062, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000062,   1, 'Drift Eviscerator') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000062,   1, 0x02000A36) /* Setup */
     , (2000062,   2, 0x090000BA) /* MotionTable */
     , (2000062,   3, 0x2000007C) /* SoundTable */
     , (2000062,   4, 0x3000001B) /* CombatTable */
     , (2000062,   8, 0x060010E7) /* Icon */
     , (2000062,  19, 0x00000057) /* ActivationAnimation */
     , (2000062,  22, 0x340000A8) /* PhysicsEffectTable */
     , (2000062,  30,         87) /* PhysicsScript - BreatheLightning */
     , (2000062,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000062,   1, 500, 0, 0) /* Strength */
     , (2000062,   2, 500, 0, 0) /* Endurance */
     , (2000062,   3, 350, 0, 0) /* Quickness */
     , (2000062,   4, 350, 0, 0) /* Coordination */
     , (2000062,   5, 300, 0, 0) /* Focus */
     , (2000062,   6, 300, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000062,   1,  5750, 0, 0, 6000) /* MaxHealth */
     , (2000062,   3,  3420, 0, 0, 3920) /* MaxStamina */
     , (2000062,   5,     0, 0, 0, 300) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000062,  6, 0, 2, 0, 450, 0, 0) /* MeleeDefense        Trained */
     , (2000062,  7, 0, 2, 0, 550, 0, 0) /* MissileDefense      Trained */
     , (2000062, 15, 0, 2, 0, 400, 0, 0) /* MagicDefense        Trained */
     , (2000062, 45, 0, 2, 0, 430, 0, 0) /* LightWeapons        Trained */
     , (2000062, 52, 0, 2, 0, 430, 0, 0) /* DirtyFighting       Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000062,  0,  4,  0,    0,  500,  550,  350,  300,  300,  500,  550,  550,    0, 1,  0.1,    0,    0,  0.1,    0,    0,  0.1,    0,    0,  0.1,    0,    0) /* Head */
     , (2000062, 16,  4,  0,    0,  500,  550,  350,  300,  300,  500,  550,  550,    0, 2, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45) /* Torso */
     , (2000062, 18,  2, 200,  0.5,  500,  550,  350,  300,  300,  500,  550,  550,    0, 2,    0,  0.2,  0.1,    0,  0.2,  0.1,    0,  0.2,  0.1,    0,  0.2,  0.1) /* Arm */
     , (2000062, 19,  2,  0, 0.75,  500,  550,  350,  300,  300,  500,  550,  550,    0, 3,    0,  0.2, 0.45,    0,  0.2, 0.45,    0,  0.2, 0.45,    0,  0.2, 0.45) /* Leg */
     , (2000062, 20,  1, 250, 0.75,  500,  550,  350,  300,  300,  500,  550,  550,    0, 2, 0.45,  0.2,    0, 0.45,  0.2,    0, 0.45,  0.2,    0, 0.45,  0.2,    0) /* Claw */
     , (2000062, 22, 64, 280,  0.5,    0,    0,    0,    0,    0,    0,    0,    0,    0, 0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0) /* Breath */;
