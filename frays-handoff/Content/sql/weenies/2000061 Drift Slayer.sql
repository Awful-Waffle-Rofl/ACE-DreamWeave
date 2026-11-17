-- @owner: frays
-- @owns: wcid:2000061
-- @requires-restart: no
--
-- Frays creature, olthoi family, elite role: a copy of retail 31008 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.15, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000061;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000061, 'dw-drift-slayer', 10, '2026-10-29 20:23:10') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000061,   1,         16) /* ItemType - Creature */
     , (2000061,   2,          1) /* CreatureType - Olthoi */
     , (2000061,   3,         82) /* PaletteTemplate - PinkPurple */
     , (2000061,   6,         -1) /* ItemsCapacity */
     , (2000061,   7,         -1) /* ContainersCapacity */
     , (2000061,  16,          1) /* ItemUseable - No */
     , (2000061,  25,        185) /* Level */
     , (2000061,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000061, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000061, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000061, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000061,   1, True ) /* Stuck */
     , (2000061,  12, True ) /* ReportCollisions */
     , (2000061,  14, True ) /* GravityStatus */
     , (2000061,  19, True ) /* Attackable */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000061,   1,       5) /* HeartbeatInterval */
     , (2000061,   2,       0) /* HeartbeatTimestamp */
     , (2000061,   3,     0.7) /* HealthRate */
     , (2000061,   4,       4) /* StaminaRate */
     , (2000061,   5,       2) /* ManaRate */
     , (2000061,  12,     0.5) /* Shade */
     , (2000061,  13,    0.69) /* ArmorModVsSlash */
     , (2000061,  14,     0.8) /* ArmorModVsPierce */
     , (2000061,  15,     0.6) /* ArmorModVsBludgeon */
     , (2000061,  16,       1) /* ArmorModVsCold */
     , (2000061,  17,       1) /* ArmorModVsFire */
     , (2000061,  18,     1.1) /* ArmorModVsAcid */
     , (2000061,  19,       1) /* ArmorModVsElectric */
     , (2000061,  31,      24) /* VisualAwarenessRange */
     , (2000061,  34,     0.5) /* PowerupTime */
     , (2000061,  36,       1) /* ChargeSpeed */
     , (2000061,  39,   1.265) /* DefaultScale */
     , (2000061,  64,    0.75) /* ResistSlash */
     , (2000061,  65,       1) /* ResistPierce */
     , (2000061,  66,       1) /* ResistBludgeon */
     , (2000061,  67,    0.75) /* ResistFire */
     , (2000061,  68,    0.75) /* ResistCold */
     , (2000061,  69,    0.42) /* ResistAcid */
     , (2000061,  70,    0.25) /* ResistElectric */
     , (2000061,  71,    0.25) /* ResistHealthBoost */
     , (2000061,  72,    0.25) /* ResistStaminaDrain */
     , (2000061,  73,    0.25) /* ResistStaminaBoost */
     , (2000061,  74,    0.25) /* ResistManaDrain */
     , (2000061,  75,    0.25) /* ResistManaBoost */
     , (2000061,  76,    0.25) /* Translucency */
     , (2000061,  77,       1) /* PhysicsScriptIntensity */
     , (2000061, 104,      10) /* ObviousRadarRange */
     , (2000061, 117,     0.6) /* FocusedProbability */
     , (2000061, 125,    0.25) /* ResistHealthDrain */
     , (2000061, 151,       1) /* IgnoreShield */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000061,   1, 'Drift Slayer') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000061,   1, 0x02000AAC) /* Setup */
     , (2000061,   2, 0x09000002) /* MotionTable */
     , (2000061,   3, 0x2000000D) /* SoundTable */
     , (2000061,   4, 0x3000001B) /* CombatTable */
     , (2000061,   6, 0x04001114) /* PaletteBase */
     , (2000061,   7, 0x100002E4) /* ClothingBase */
     , (2000061,   8, 0x060010E7) /* Icon */
     , (2000061,  22, 0x34000021) /* PhysicsEffectTable */
     , (2000061,  30,         86) /* PhysicsScript - BreatheAcid */
     , (2000061,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000061,   1, 370, 0, 0) /* Strength */
     , (2000061,   2, 370, 0, 0) /* Endurance */
     , (2000061,   3, 210, 0, 0) /* Quickness */
     , (2000061,   4, 210, 0, 0) /* Coordination */
     , (2000061,   5, 160, 0, 0) /* Focus */
     , (2000061,   6, 120, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000061,   1,  2115, 0, 0, 2300) /* MaxHealth */
     , (2000061,   3,  4200, 0, 0, 4570) /* MaxStamina */
     , (2000061,   5,     0, 0, 0, 120) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000061,  6, 0, 3, 0, 360, 0, 0) /* MeleeDefense        Specialized */
     , (2000061,  7, 0, 3, 0, 380, 0, 0) /* MissileDefense      Specialized */
     , (2000061, 15, 0, 3, 0, 325, 0, 0) /* MagicDefense        Specialized */
     , (2000061, 20, 0, 2, 0, 100, 0, 0) /* Deception           Trained */
     , (2000061, 22, 0, 2, 0, 200, 0, 0) /* Jump                Trained */
     , (2000061, 24, 0, 2, 0, 380, 0, 0) /* Run                 Trained */
     , (2000061, 45, 0, 3, 0, 310, 0, 0) /* LightWeapons        Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000061,  0,  4,  0,    0,  350,  241,  280,  210,  350,  350,  385,  350,    0, 1,  0.1,    0,    0,  0.1,    0,    0,  0.1,    0,    0,  0.1,    0,    0) /* Head */
     , (2000061, 16,  4,  0,    0,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45, 0.45,  0.4, 0.45) /* Torso */
     , (2000061, 18,  1, 130,  0.5,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2,    0,  0.2,  0.1,    0,  0.2,  0.1,    0,  0.2,  0.1,    0,  0.2,  0.1) /* Arm */
     , (2000061, 19,  4,  0,    0,  350,  241,  280,  210,  350,  350,  385,  350,    0, 3,    0,  0.2, 0.45,    0,  0.2, 0.45,    0,  0.2, 0.45,    0,  0.2, 0.45) /* Leg */
     , (2000061, 20,  2, 130, 0.75,  350,  241,  280,  210,  350,  350,  385,  350,    0, 2, 0.45,  0.2,    0, 0.45,  0.2,    0, 0.45,  0.2,    0, 0.45,  0.2,    0) /* Claw */
     , (2000061, 22, 32, 130,  0.5,    0,    0,    0,    0,    0,    0,    0,    0,    0, 0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0) /* Breath */;

INSERT INTO `weenie_properties_event_filter` (`object_Id`, `event`)
VALUES (2000061,  94)
     , (2000061, 414);

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000061, 9, 31352,  0, 0, 0.045, False) /* Create Olthoi Slayer Carapace (31352) for ContainTreasure */
     , (2000061, 9,     0,  0, 0, 0.955, False) /* Create nothing for ContainTreasure */
     , (2000061, 9, 24477,  0, 0, 0.03, False) /* Create Sturdy Steel Key (24477) for ContainTreasure */
     , (2000061, 9,     0,  0, 0, 0.97, False) /* Create nothing for ContainTreasure */;
