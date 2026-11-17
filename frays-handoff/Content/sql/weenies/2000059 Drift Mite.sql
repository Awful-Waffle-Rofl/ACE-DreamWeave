-- @owner: frays
-- @owns: wcid:2000059
-- @requires-restart: no
--
-- Frays creature, olthoi family, swarm role: a copy of retail 31007 without spells, emotes or generators; dream tint (Translucency 0.25), size x0.85, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000059;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000059, 'dw-drift-mite', 10, '2026-10-29 20:23:10') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000059,   1,         16) /* ItemType - Creature */
     , (2000059,   2,          1) /* CreatureType - Olthoi */
     , (2000059,   3,         61) /* PaletteTemplate - White */
     , (2000059,   6,         -1) /* ItemsCapacity */
     , (2000059,   7,         -1) /* ContainersCapacity */
     , (2000059,  16,          1) /* ItemUseable - No */
     , (2000059,  25,        185) /* Level */
     , (2000059,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000059, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000059, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000059, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000059,   1, True ) /* Stuck */
     , (2000059,  11, False) /* IgnoreCollisions */
     , (2000059,  12, True ) /* ReportCollisions */
     , (2000059,  13, False) /* Ethereal */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000059,   1,       5) /* HeartbeatInterval */
     , (2000059,   2,       0) /* HeartbeatTimestamp */
     , (2000059,   3,     0.7) /* HealthRate */
     , (2000059,   4,       4) /* StaminaRate */
     , (2000059,   5,       2) /* ManaRate */
     , (2000059,  12,     0.5) /* Shade */
     , (2000059,  13,    0.69) /* ArmorModVsSlash */
     , (2000059,  14,     0.8) /* ArmorModVsPierce */
     , (2000059,  15,     0.6) /* ArmorModVsBludgeon */
     , (2000059,  16,       1) /* ArmorModVsCold */
     , (2000059,  17,       1) /* ArmorModVsFire */
     , (2000059,  18,     1.1) /* ArmorModVsAcid */
     , (2000059,  19,       1) /* ArmorModVsElectric */
     , (2000059,  31,      24) /* VisualAwarenessRange */
     , (2000059,  34,     0.5) /* PowerupTime */
     , (2000059,  36,       1) /* ChargeSpeed */
     , (2000059,  39,    0.85) /* DefaultScale */
     , (2000059,  64,    0.75) /* ResistSlash */
     , (2000059,  65,       1) /* ResistPierce */
     , (2000059,  66,       1) /* ResistBludgeon */
     , (2000059,  67,    0.75) /* ResistFire */
     , (2000059,  68,    0.75) /* ResistCold */
     , (2000059,  69,    0.42) /* ResistAcid */
     , (2000059,  70,    0.25) /* ResistElectric */
     , (2000059,  71,    0.25) /* ResistHealthBoost */
     , (2000059,  72,    0.25) /* ResistStaminaDrain */
     , (2000059,  73,       1) /* ResistStaminaBoost */
     , (2000059,  74,     0.5) /* ResistManaDrain */
     , (2000059,  75,       1) /* ResistManaBoost */
     , (2000059,  76,    0.25) /* Translucency */
     , (2000059,  77,       1) /* PhysicsScriptIntensity */
     , (2000059, 104,      10) /* ObviousRadarRange */
     , (2000059, 117,     0.6) /* FocusedProbability */
     , (2000059, 125,    0.25) /* ResistHealthDrain */
     , (2000059, 151,       1) /* IgnoreShield */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000059,   1, 'Drift Mite') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000059,   1, 0x02000AAC) /* Setup */
     , (2000059,   2, 0x09000002) /* MotionTable */
     , (2000059,   3, 0x2000000D) /* SoundTable */
     , (2000059,   4, 0x3000001B) /* CombatTable */
     , (2000059,   6, 0x04001114) /* PaletteBase */
     , (2000059,   7, 0x10000477) /* ClothingBase */
     , (2000059,   8, 0x060010E7) /* Icon */
     , (2000059,  22, 0x34000021) /* PhysicsEffectTable */
     , (2000059,  30,         86) /* PhysicsScript - BreatheAcid */
     , (2000059,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000059,   1, 370, 0, 0) /* Strength */
     , (2000059,   2, 370, 0, 0) /* Endurance */
     , (2000059,   3, 210, 0, 0) /* Quickness */
     , (2000059,   4, 210, 0, 0) /* Coordination */
     , (2000059,   5, 160, 0, 0) /* Focus */
     , (2000059,   6, 120, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000059,   1,  2115, 0, 0, 2300) /* MaxHealth */
     , (2000059,   3,  4200, 0, 0, 4570) /* MaxStamina */
     , (2000059,   5,     0, 0, 0, 120) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000059,  6, 0, 3, 0, 360, 0, 0) /* MeleeDefense        Specialized */
     , (2000059,  7, 0, 3, 0, 350, 0, 0) /* MissileDefense      Specialized */
     , (2000059, 15, 0, 3, 0, 315, 0, 0) /* MagicDefense        Specialized */
     , (2000059, 20, 0, 2, 0, 100, 0, 0) /* Deception           Trained */
     , (2000059, 22, 0, 2, 0, 200, 0, 0) /* Jump                Trained */
     , (2000059, 24, 0, 2, 0, 380, 0, 0) /* Run                 Trained */
     , (2000059, 45, 0, 3, 0, 300, 0, 0) /* LightWeapons        Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000059,  0,  4,  0,    0,  350,  241,  280,  210,  350,  350,  385,  350,    0, 1,  0.1,    0,    0,  0.1,    0,    0,  0.1,    0,    0,  0.1,    0,    0) /* Head */
     , (2000059, 16,  4,  0,    0,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45) /* Torso */
     , (2000059, 18,  1, 125,  0.5,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2,    0,  0.2,  0.1,    0,  0.2,  0.1,    0,  0.2,  0.1,    0,  0.2,  0.1) /* Arm */
     , (2000059, 19,  4,  0,    0,  350,  241,  280,  210,  350,  350,  385,  350,    0, 3,    0,  0.2, 0.45,    0,  0.2, 0.45,    0,  0.2, 0.45,    0,  0.2, 0.45) /* Leg */
     , (2000059, 20,  2, 125, 0.75,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2, 0.45,  0.2,    0, 0.45,  0.2,    0, 0.45,  0.2,    0, 0.45,  0.2,    0) /* Claw */
     , (2000059, 22, 32, 125,  0.5,    0,    0,    0,    0,    0,    0,    0,    0,    0, 0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0) /* Breath */;

INSERT INTO `weenie_properties_event_filter` (`object_Id`, `event`)
VALUES (2000059,  94)
     , (2000059, 414);

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000059, 9, 31355,  0, 0, 0.05, False) /* Create Olthoi Slasher Carapace (31355) for ContainTreasure */
     , (2000059, 9,     0,  0, 0, 0.95, False) /* Create nothing for ContainTreasure */
     , (2000059, 9, 24477,  0, 0, 0.03, False) /* Create Sturdy Steel Key (24477) for ContainTreasure */
     , (2000059, 9,     0,  0, 0, 0.97, False) /* Create nothing for ContainTreasure */;
