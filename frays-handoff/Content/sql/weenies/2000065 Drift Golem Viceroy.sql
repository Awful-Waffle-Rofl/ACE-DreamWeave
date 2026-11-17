-- @owner: frays
-- @owns: wcid:2000065
-- @requires-restart: no
--
-- Frays creature, golem family, elite role: a copy of retail 40147 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.1, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000065;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000065, 'dw-drift-golem-viceroy', 10, '2026-10-29 20:23:11') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000065,   1,         16) /* ItemType - Creature */
     , (2000065,   2,         13) /* CreatureType - Golem */
     , (2000065,   3,         39) /* PaletteTemplate - Black */
     , (2000065,   6,         -1) /* ItemsCapacity */
     , (2000065,   7,         -1) /* ContainersCapacity */
     , (2000065,  16,          1) /* ItemUseable - No */
     , (2000065,  25,        185) /* Level */
     , (2000065,  81,          2) /* MaxGeneratedObjects */
     , (2000065,  82,          2) /* InitGeneratedObjects */
     , (2000065,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000065, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000065, 146,          0) /* XpOverride */
     , (2000065, 307,          2) /* DamageRating */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000065,   1, True ) /* Stuck */
     , (2000065,   6, True ) /* AiUsesMana */
     , (2000065,  11, False) /* IgnoreCollisions */
     , (2000065,  12, True ) /* ReportCollisions */
     , (2000065,  13, False) /* Ethereal */
     , (2000065,  50, True ) /* NeverFailCasting */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000065,   1,       5) /* HeartbeatInterval */
     , (2000065,   2,       0) /* HeartbeatTimestamp */
     , (2000065,   3,     0.9) /* HealthRate */
     , (2000065,   4,     0.5) /* StaminaRate */
     , (2000065,   5,       2) /* ManaRate */
     , (2000065,   6,     0.1) /* HealthUponResurrection */
     , (2000065,   7,    0.25) /* StaminaUponResurrection */
     , (2000065,   8,     0.3) /* ManaUponResurrection */
     , (2000065,  12,     0.5) /* Shade */
     , (2000065,  13,    0.79) /* ArmorModVsSlash */
     , (2000065,  14,     0.9) /* ArmorModVsPierce */
     , (2000065,  15,       1) /* ArmorModVsBludgeon */
     , (2000065,  16,    0.84) /* ArmorModVsCold */
     , (2000065,  17,    0.84) /* ArmorModVsFire */
     , (2000065,  18,    0.84) /* ArmorModVsAcid */
     , (2000065,  19,    0.84) /* ArmorModVsElectric */
     , (2000065,  31,      13) /* VisualAwarenessRange */
     , (2000065,  34,     3.3) /* PowerupTime */
     , (2000065,  39,    1.21) /* DefaultScale */
     , (2000065,  41,       0) /* RegenerationInterval */
     , (2000065,  43,       4) /* GeneratorRadius */
     , (2000065,  64,    0.33) /* ResistSlash */
     , (2000065,  65,    0.67) /* ResistPierce */
     , (2000065,  66,       1) /* ResistBludgeon */
     , (2000065,  67,     0.5) /* ResistFire */
     , (2000065,  68,     0.5) /* ResistCold */
     , (2000065,  69,     0.5) /* ResistAcid */
     , (2000065,  70,     0.5) /* ResistElectric */
     , (2000065,  71,       1) /* ResistHealthBoost */
     , (2000065,  72,       1) /* ResistStaminaDrain */
     , (2000065,  73,       1) /* ResistStaminaBoost */
     , (2000065,  74,       1) /* ResistManaDrain */
     , (2000065,  75,       1) /* ResistManaBoost */
     , (2000065,  76,    0.25) /* Translucency */
     , (2000065,  80,       3) /* AiUseMagicDelay */
     , (2000065, 104,      10) /* ObviousRadarRange */
     , (2000065, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000065,   1, 'Drift Golem Viceroy') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000065,   1, 0x02001032) /* Setup */
     , (2000065,   2, 0x09000081) /* MotionTable */
     , (2000065,   3, 0x20000015) /* SoundTable */
     , (2000065,   4, 0x30000008) /* CombatTable */
     , (2000065,   6, 0x04001799) /* PaletteBase */
     , (2000065,   7, 0x10000566) /* ClothingBase */
     , (2000065,   8, 0x06001224) /* Icon */
     , (2000065,  22, 0x3400005B) /* PhysicsEffectTable */
     , (2000065,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000065,   1, 480, 0, 0) /* Strength */
     , (2000065,   2, 700, 0, 0) /* Endurance */
     , (2000065,   3, 480, 0, 0) /* Quickness */
     , (2000065,   4, 480, 0, 0) /* Coordination */
     , (2000065,   5, 380, 0, 0) /* Focus */
     , (2000065,   6, 480, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000065,   1,  2900, 0, 0, 3250) /* MaxHealth */
     , (2000065,   3,  4300, 0, 0, 5000) /* MaxStamina */
     , (2000065,   5,  4500, 0, 0, 4980) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000065,  6, 0, 2, 0, 391, 0, 0) /* MeleeDefense        Trained */
     , (2000065,  7, 0, 2, 0, 240, 0, 0) /* MissileDefense      Trained */
     , (2000065, 15, 0, 2, 0, 220, 0, 0) /* MagicDefense        Trained */
     , (2000065, 31, 0, 2, 0, 125, 0, 0) /* CreatureEnchantment Trained */
     , (2000065, 33, 0, 2, 0, 125, 0, 0) /* LifeMagic           Trained */
     , (2000065, 34, 0, 2, 0, 125, 0, 0) /* WarMagic            Trained */
     , (2000065, 45, 0, 2, 0, 400, 0, 0) /* LightWeapons        Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000065,  0,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000065,  1,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000065,  2,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000065,  3,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000065,  4,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000065,  5,  4, 250, 0.75,  350,  277,  315,  350,  294,  294,  294,  294,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000065,  6,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000065,  7,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000065,  8,  4, 250, 0.75,  350,  277,  315,  350,  294,  294,  294,  294,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000065, 9, 44469,  1, 0, 0, False) /* Create Lesser Corrupted Essence (44469) for ContainTreasure */
     , (2000065, 9, 41979,  1, 0, 0.02, False) /* Create Shattered Mana Forge Key (41979) for ContainTreasure */
     , (2000065, 9,     0,  0, 0, 0.98, False) /* Create nothing for ContainTreasure */
     , (2000065, 9, 34277,  1, 0, 0.02, False) /* Create Ancient Falatacot Trinket (34277) for ContainTreasure */
     , (2000065, 9,     0,  0, 0, 0.98, False) /* Create nothing for ContainTreasure */;
