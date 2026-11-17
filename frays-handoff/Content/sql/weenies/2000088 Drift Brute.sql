-- @owner: frays
-- @owns: wcid:2000088
-- @requires-restart: no
--
-- Frays creature, lugian family (the fifth), swarm role: a copy of retail 51904 without spells, emotes or generators; dream tint (Translucency 0.25), size x0.85, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000088;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000088, 'dw-drift-brute', 10, '2026-10-30 00:29:00') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000088,   1,         16) /* ItemType - Creature */
     , (2000088,   2,          5) /* CreatureType - Lugian */
     , (2000088,   3,          9) /* PaletteTemplate - Grey */
     , (2000088,   6,         -1) /* ItemsCapacity */
     , (2000088,   7,         -1) /* ContainersCapacity */
     , (2000088,  16,          1) /* ItemUseable - No */
     , (2000088,  25,        240) /* Level */
     , (2000088,  68,         13) /* TargetingTactic - Random, LastDamager, TopDamager */
     , (2000088,  93,    4195336) /* PhysicsState - ReportCollisions, Gravity, EdgeSlide */
     , (2000088, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000088, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000088,   1, True ) /* Stuck */
     , (2000088,  11, False) /* IgnoreCollisions */
     , (2000088,  12, True ) /* ReportCollisions */
     , (2000088,  13, False) /* Ethereal */
     , (2000088,  14, True ) /* GravityStatus */
     , (2000088,  19, True ) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000088,   1,       5) /* HeartbeatInterval */
     , (2000088,   2,       0) /* HeartbeatTimestamp */
     , (2000088,   3,     0.6) /* HealthRate */
     , (2000088,   4,       4) /* StaminaRate */
     , (2000088,   5,       2) /* ManaRate */
     , (2000088,  12,       0) /* Shade */
     , (2000088,  13,       1) /* ArmorModVsSlash */
     , (2000088,  14,     0.8) /* ArmorModVsPierce */
     , (2000088,  15,       1) /* ArmorModVsBludgeon */
     , (2000088,  16,     0.8) /* ArmorModVsCold */
     , (2000088,  17,       1) /* ArmorModVsFire */
     , (2000088,  18,       1) /* ArmorModVsAcid */
     , (2000088,  19,     0.8) /* ArmorModVsElectric */
     , (2000088,  31,      22) /* VisualAwarenessRange */
     , (2000088,  34,       2) /* PowerupTime */
     , (2000088,  36,       1) /* ChargeSpeed */
     , (2000088,  39,    0.85) /* DefaultScale */
     , (2000088,  55,      80) /* HomeRadius */
     , (2000088,  64,     0.4) /* ResistSlash */
     , (2000088,  65,     0.6) /* ResistPierce */
     , (2000088,  66,     0.4) /* ResistBludgeon */
     , (2000088,  67,     0.4) /* ResistFire */
     , (2000088,  68,     0.7) /* ResistCold */
     , (2000088,  69,     0.4) /* ResistAcid */
     , (2000088,  70,     0.7) /* ResistElectric */
     , (2000088,  76,    0.25) /* Translucency */
     , (2000088, 104,      10) /* ObviousRadarRange */
     , (2000088, 125,       1) /* ResistHealthDrain */
     , (2000088, 165,       1) /* ArmorModVsNether */
     , (2000088, 166,       1) /* ResistNether */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000088,   1, 'Drift Brute') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000088,   1, 0x02000A0B) /* Setup */
     , (2000088,   2, 0x09000006) /* MotionTable */
     , (2000088,   3, 0x2000000A) /* SoundTable */
     , (2000088,   4, 0x30000003) /* CombatTable */
     , (2000088,   6, 0x040010C6) /* PaletteBase */
     , (2000088,   7, 0x100002BA) /* ClothingBase */
     , (2000088,   8, 0x06001037) /* Icon */
     , (2000088,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000088,   1, 400, 0, 0) /* Strength */
     , (2000088,   2, 360, 0, 0) /* Endurance */
     , (2000088,   3, 350, 0, 0) /* Quickness */
     , (2000088,   4, 420, 0, 0) /* Coordination */
     , (2000088,   5, 250, 0, 0) /* Focus */
     , (2000088,   6, 270, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000088,   1,  5960, 0, 0, 6140) /* MaxHealth */
     , (2000088,   3,  2752, 0, 0, 3112) /* MaxStamina */
     , (2000088,   5,    50, 0, 0, 320) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000088,  6, 0, 2, 0, 560, 0, 0) /* MeleeDefense        Trained */
     , (2000088,  7, 0, 2, 0, 450, 0, 0) /* MissileDefense      Trained */
     , (2000088, 15, 0, 2, 0, 380, 0, 0) /* MagicDefense        Trained */
     , (2000088, 41, 0, 2, 0, 420, 0, 0) /* TwoHandedCombat     Trained */
     , (2000088, 44, 0, 2, 0, 420, 0, 0) /* HeavyWeapons        Trained */
     , (2000088, 45, 0, 2, 0, 420, 0, 0) /* LightWeapons        Trained */
     , (2000088, 46, 0, 2, 0, 420, 0, 0) /* FinesseWeapons      Trained */
     , (2000088, 47, 0, 2, 0, 450, 0, 0) /* MissileWeapons      Trained */
     , (2000088, 52, 0, 2, 0, 420, 0, 0) /* DirtyFighting       Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000088,  0,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000088,  1,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000088,  2,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000088,  3,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000088,  4,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000088,  5,  4, 200, 0.75,  440,  440,  352,  440,  352,  440,  440,  352,  440, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000088,  6,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000088,  7,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000088,  8,  4, 200, 0.75,  440,  440,  352,  440,  352,  440,  440,  352,  440, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000088, 2, 51910,  1, 0, 0, False) /* Create Lugian Mace (51910) for Wield */;
