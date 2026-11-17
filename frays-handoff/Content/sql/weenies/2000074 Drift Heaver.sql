-- @owner: frays
-- @owns: wcid:2000074
-- @requires-restart: no
--
-- Frays creature, tusker family, snarl role: a copy of retail 52724 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000074;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000074, 'dw-drift-heaver', 10, '2026-10-29 20:23:12') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000074,   1,         16) /* ItemType - Creature */
     , (2000074,   2,          8) /* CreatureType - Tusker */
     , (2000074,   3,         53) /* PaletteTemplate - BlueDullSilver */
     , (2000074,   6,         -1) /* ItemsCapacity */
     , (2000074,   7,         -1) /* ContainersCapacity */
     , (2000074,  16,          1) /* ItemUseable - No */
     , (2000074,  25,        240) /* Level */
     , (2000074,  27,          0) /* ArmorType - None */
     , (2000074,  40,          2) /* CombatMode - Melee */
     , (2000074,  68,          9) /* TargetingTactic - Random, TopDamager */
     , (2000074,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000074, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000074, 146,          0) /* XpOverride */
     , (2000074, 307,         35) /* DamageRating */
     , (2000074, 313,         30) /* CritRating */
     , (2000074, 386,          5) /* Overpower */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000074,   1, True ) /* Stuck */
     , (2000074,   6, False) /* AiUsesMana */
     , (2000074,  11, False) /* IgnoreCollisions */
     , (2000074,  12, True ) /* ReportCollisions */
     , (2000074,  13, False) /* Ethereal */
     , (2000074,  14, True ) /* GravityStatus */
     , (2000074,  19, True ) /* Attackable */
     , (2000074,  29, True ) /* NoCorpse */
     , (2000074,  50, True ) /* NeverFailCasting */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000074,   1,       5) /* HeartbeatInterval */
     , (2000074,   2,       0) /* HeartbeatTimestamp */
     , (2000074,   3,    0.25) /* HealthRate */
     , (2000074,   4,       4) /* StaminaRate */
     , (2000074,   5,       2) /* ManaRate */
     , (2000074,  13,    0.98) /* ArmorModVsSlash */
     , (2000074,  14,       1) /* ArmorModVsPierce */
     , (2000074,  15,       1) /* ArmorModVsBludgeon */
     , (2000074,  16,       2) /* ArmorModVsCold */
     , (2000074,  17,    0.72) /* ArmorModVsFire */
     , (2000074,  18,       1) /* ArmorModVsAcid */
     , (2000074,  19,       1) /* ArmorModVsElectric */
     , (2000074,  31,      20) /* VisualAwarenessRange */
     , (2000074,  34,     2.5) /* PowerupTime */
     , (2000074,  36,       1) /* ChargeSpeed */
     , (2000074,  39,     0.4) /* DefaultScale */
     , (2000074,  41,      10) /* RegenerationInterval */
     , (2000074,  43,       2) /* GeneratorRadius */
     , (2000074,  64,     0.5) /* ResistSlash */
     , (2000074,  65,    0.55) /* ResistPierce */
     , (2000074,  66,    0.75) /* ResistBludgeon */
     , (2000074,  67,    0.92) /* ResistFire */
     , (2000074,  68,     0.1) /* ResistCold */
     , (2000074,  69,    0.45) /* ResistAcid */
     , (2000074,  70,    0.65) /* ResistElectric */
     , (2000074,  71,       1) /* ResistHealthBoost */
     , (2000074,  72,       1) /* ResistStaminaDrain */
     , (2000074,  73,       1) /* ResistStaminaBoost */
     , (2000074,  74,       1) /* ResistManaDrain */
     , (2000074,  75,       1) /* ResistManaBoost */
     , (2000074,  76,    0.25) /* Translucency */
     , (2000074, 104,      10) /* ObviousRadarRange */
     , (2000074, 121,      10) /* GeneratorInitialDelay */
     , (2000074, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000074,   1, 'Drift Heaver') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000074,   1, 0x02001A33) /* Setup */
     , (2000074,   2, 0x09000119) /* MotionTable */
     , (2000074,   3, 0x20000011) /* SoundTable */
     , (2000074,   4, 0x3000000B) /* CombatTable */
     , (2000074,   7, 0x10000260) /* ClothingBase */
     , (2000074,   8, 0x06001033) /* Icon */
     , (2000074,  22, 0x34000027) /* PhysicsEffectTable */
     , (2000074,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000074,   1, 380, 0, 0) /* Strength */
     , (2000074,   2, 450, 0, 0) /* Endurance */
     , (2000074,   3, 300, 0, 0) /* Quickness */
     , (2000074,   4, 350, 0, 0) /* Coordination */
     , (2000074,   5,  80, 0, 0) /* Focus */
     , (2000074,   6,  60, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000074,   1,  3100, 0, 0, 3325) /* MaxHealth */
     , (2000074,   3,  2900, 0, 0, 3350) /* MaxStamina */
     , (2000074,   5,     0, 0, 0, 60) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000074,  6, 0, 3, 0, 517, 0, 0) /* MeleeDefense        Specialized */
     , (2000074,  7, 0, 3, 0, 533, 0, 0) /* MissileDefense      Specialized */
     , (2000074, 15, 0, 3, 0, 477, 0, 0) /* MagicDefense        Specialized */
     , (2000074, 20, 0, 3, 0, 250, 0, 0) /* Deception           Specialized */
     , (2000074, 22, 0, 3, 0, 465, 0, 0) /* Jump                Specialized */
     , (2000074, 24, 0, 3, 0, 400, 0, 0) /* Run                 Specialized */
     , (2000074, 31, 0, 3, 0, 265, 0, 0) /* CreatureEnchantment Specialized */
     , (2000074, 33, 0, 3, 0, 265, 0, 0) /* LifeMagic           Specialized */
     , (2000074, 45, 0, 3, 0, 343, 0, 0) /* LightWeapons        Specialized */
     , (2000074, 47, 0, 3, 0, 495, 0, 0) /* MissileWeapons      Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000074,  0,  4,  0,    0,  350,  343,  350,  350,  700,  252,  350,  350,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000074,  1,  4,  0,    0,  350,  343,  350,  350,  700,  252,  350,  350,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000074,  2,  4,  0,    0,  350,  343,  350,  350,  700,  252,  350,  350,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000074,  3,  4,  0,    0,  350,  343,  350,  350,  700,  252,  350,  350,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000074,  4,  4,  0,    0,  350,  343,  350,  350,  700,  252,  350,  350,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000074,  5,  4, 80, 0.75,  350,  343,  350,  350,  700,  252,  350,  350,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000074,  6,  4,  0,    0,  350,  343,  350,  350,  700,  252,  350,  350,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000074,  7,  4,  0,    0,  350,  343,  350,  350,  700,  252,  350,  350,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000074,  8,  4, 95, 0.75,  350,  343,  350,  350,  700,  252,  350,  350,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000074, 2, 52725,  1, 0, 0, False) /* Create Frozen Coconut (52725) for Wield */;
