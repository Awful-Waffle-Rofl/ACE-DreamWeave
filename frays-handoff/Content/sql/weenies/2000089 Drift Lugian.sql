-- @owner: frays
-- @owns: wcid:2000089
-- @requires-restart: no
--
-- Frays creature, lugian family (the fifth), standard role: a copy of retail 51902 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000089;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000089, 'dw-drift-lugian', 10, '2026-10-30 00:29:01') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000089,   1,         16) /* ItemType - Creature */
     , (2000089,   2,          5) /* CreatureType - Lugian */
     , (2000089,   3,         39) /* PaletteTemplate - Black */
     , (2000089,   6,         -1) /* ItemsCapacity */
     , (2000089,   7,         -1) /* ContainersCapacity */
     , (2000089,  16,          1) /* ItemUseable - No */
     , (2000089,  25,        240) /* Level */
     , (2000089,  68,         13) /* TargetingTactic - Random, LastDamager, TopDamager */
     , (2000089,  93,    4195336) /* PhysicsState - ReportCollisions, Gravity, EdgeSlide */
     , (2000089, 101,     524288) /* AiAllowedCombatStyle - StubbornMissile */
     , (2000089, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000089, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000089,   1, True ) /* Stuck */
     , (2000089,  11, False) /* IgnoreCollisions */
     , (2000089,  12, True ) /* ReportCollisions */
     , (2000089,  13, False) /* Ethereal */
     , (2000089,  14, True ) /* GravityStatus */
     , (2000089,  19, True ) /* Attackable */
     , (2000089,  52, True ) /* AiImmobile */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000089,   1,       5) /* HeartbeatInterval */
     , (2000089,   2,       0) /* HeartbeatTimestamp */
     , (2000089,   3,     0.6) /* HealthRate */
     , (2000089,   4,       4) /* StaminaRate */
     , (2000089,   5,       2) /* ManaRate */
     , (2000089,  12,       0) /* Shade */
     , (2000089,  13,       1) /* ArmorModVsSlash */
     , (2000089,  14,     0.8) /* ArmorModVsPierce */
     , (2000089,  15,       1) /* ArmorModVsBludgeon */
     , (2000089,  16,     0.8) /* ArmorModVsCold */
     , (2000089,  17,       1) /* ArmorModVsFire */
     , (2000089,  18,       1) /* ArmorModVsAcid */
     , (2000089,  19,     0.8) /* ArmorModVsElectric */
     , (2000089,  31,      22) /* VisualAwarenessRange */
     , (2000089,  34,       2) /* PowerupTime */
     , (2000089,  36,       1) /* ChargeSpeed */
     , (2000089,  39,       1) /* DefaultScale */
     , (2000089,  55,      80) /* HomeRadius */
     , (2000089,  64,     0.4) /* ResistSlash */
     , (2000089,  65,     0.6) /* ResistPierce */
     , (2000089,  66,     0.4) /* ResistBludgeon */
     , (2000089,  67,     0.4) /* ResistFire */
     , (2000089,  68,     0.7) /* ResistCold */
     , (2000089,  69,     0.4) /* ResistAcid */
     , (2000089,  70,     0.7) /* ResistElectric */
     , (2000089,  76,    0.25) /* Translucency */
     , (2000089, 104,      10) /* ObviousRadarRange */
     , (2000089, 125,       1) /* ResistHealthDrain */
     , (2000089, 165,       1) /* ArmorModVsNether */
     , (2000089, 166,       1) /* ResistNether */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000089,   1, 'Drift Lugian') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000089,   1, 0x02000A0B) /* Setup */
     , (2000089,   2, 0x09000006) /* MotionTable */
     , (2000089,   3, 0x2000000A) /* SoundTable */
     , (2000089,   4, 0x30000003) /* CombatTable */
     , (2000089,   6, 0x040010C6) /* PaletteBase */
     , (2000089,   7, 0x100002BA) /* ClothingBase */
     , (2000089,   8, 0x06001037) /* Icon */
     , (2000089,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000089,   1, 400, 0, 0) /* Strength */
     , (2000089,   2, 360, 0, 0) /* Endurance */
     , (2000089,   3, 350, 0, 0) /* Quickness */
     , (2000089,   4, 420, 0, 0) /* Coordination */
     , (2000089,   5, 250, 0, 0) /* Focus */
     , (2000089,   6, 270, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000089,   1,  6170, 0, 0, 6350) /* MaxHealth */
     , (2000089,   3,  3890, 0, 0, 4250) /* MaxStamina */
     , (2000089,   5,    50, 0, 0, 260) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000089,  6, 0, 2, 0, 560, 0, 0) /* MeleeDefense        Trained */
     , (2000089,  7, 0, 2, 0, 450, 0, 0) /* MissileDefense      Trained */
     , (2000089, 15, 0, 2, 0, 380, 0, 0) /* MagicDefense        Trained */
     , (2000089, 41, 0, 2, 0, 400, 0, 0) /* TwoHandedCombat     Trained */
     , (2000089, 44, 0, 2, 0, 400, 0, 0) /* HeavyWeapons        Trained */
     , (2000089, 45, 0, 2, 0, 400, 0, 0) /* LightWeapons        Trained */
     , (2000089, 46, 0, 2, 0, 400, 0, 0) /* FinesseWeapons      Trained */
     , (2000089, 47, 0, 2, 0, 450, 0, 0) /* MissileWeapons      Trained */
     , (2000089, 52, 0, 2, 0, 400, 0, 0) /* DirtyFighting       Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000089,  0,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000089,  1,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000089,  2,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000089,  3,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000089,  4,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000089,  5,  4, 200, 0.75,  440,  440,  352,  440,  352,  440,  440,  352,  440, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000089,  6,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000089,  7,  4, 200,  0.3,  440,  440,  352,  440,  352,  440,  440,  352,  440, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000089,  8,  4, 200, 0.75,  440,  440,  352,  440,  352,  440,  440,  352,  440, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000089, 2, 23745,  1, 0, 0, False) /* Create Rock (23745) for Wield */;
