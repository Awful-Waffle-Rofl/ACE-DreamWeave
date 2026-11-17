-- @owner: frays
-- @owns: wcid:2000064
-- @requires-restart: no
--
-- Frays creature, golem family, standard role: a copy of retail 40149 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.0, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000064;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000064, 'dw-drift-golem', 10, '2026-10-29 20:23:10') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000064,   1,         16) /* ItemType - Creature */
     , (2000064,   2,         13) /* CreatureType - Golem */
     , (2000064,   3,         39) /* PaletteTemplate - Black */
     , (2000064,   6,         -1) /* ItemsCapacity */
     , (2000064,   7,         -1) /* ContainersCapacity */
     , (2000064,  16,          1) /* ItemUseable - No */
     , (2000064,  25,        185) /* Level */
     , (2000064,  81,          1) /* MaxGeneratedObjects */
     , (2000064,  82,          0) /* InitGeneratedObjects */
     , (2000064,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000064, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000064, 146,          0) /* XpOverride */
     , (2000064, 307,          2) /* DamageRating */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000064,   1, True ) /* Stuck */
     , (2000064,   6, True ) /* AiUsesMana */
     , (2000064,  11, False) /* IgnoreCollisions */
     , (2000064,  12, True ) /* ReportCollisions */
     , (2000064,  13, False) /* Ethereal */
     , (2000064,  50, True ) /* NeverFailCasting */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000064,   1,       5) /* HeartbeatInterval */
     , (2000064,   2,       0) /* HeartbeatTimestamp */
     , (2000064,   3,     0.9) /* HealthRate */
     , (2000064,   4,     0.5) /* StaminaRate */
     , (2000064,   5,       2) /* ManaRate */
     , (2000064,   6,     0.1) /* HealthUponResurrection */
     , (2000064,   7,    0.25) /* StaminaUponResurrection */
     , (2000064,   8,     0.3) /* ManaUponResurrection */
     , (2000064,  12,     0.5) /* Shade */
     , (2000064,  13,    0.79) /* ArmorModVsSlash */
     , (2000064,  14,     0.9) /* ArmorModVsPierce */
     , (2000064,  15,       1) /* ArmorModVsBludgeon */
     , (2000064,  16,    0.84) /* ArmorModVsCold */
     , (2000064,  17,    0.84) /* ArmorModVsFire */
     , (2000064,  18,    0.84) /* ArmorModVsAcid */
     , (2000064,  19,    0.84) /* ArmorModVsElectric */
     , (2000064,  31,      13) /* VisualAwarenessRange */
     , (2000064,  34,     3.3) /* PowerupTime */
     , (2000064,  39,     1.1) /* DefaultScale */
     , (2000064,  43,       4) /* GeneratorRadius */
     , (2000064,  64,    0.33) /* ResistSlash */
     , (2000064,  65,    0.67) /* ResistPierce */
     , (2000064,  66,       1) /* ResistBludgeon */
     , (2000064,  67,     0.5) /* ResistFire */
     , (2000064,  68,     0.5) /* ResistCold */
     , (2000064,  69,     0.5) /* ResistAcid */
     , (2000064,  70,     0.5) /* ResistElectric */
     , (2000064,  71,       1) /* ResistHealthBoost */
     , (2000064,  72,       1) /* ResistStaminaDrain */
     , (2000064,  73,       1) /* ResistStaminaBoost */
     , (2000064,  74,       1) /* ResistManaDrain */
     , (2000064,  75,       1) /* ResistManaBoost */
     , (2000064,  76,    0.25) /* Translucency */
     , (2000064,  80,       3) /* AiUseMagicDelay */
     , (2000064, 104,      10) /* ObviousRadarRange */
     , (2000064, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000064,   1, 'Drift Golem') /* Name */
     , (2000064,  45, 'BlackMarketBlackCoralGolemKilltask') /* KillQuest */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000064,   1, 0x020007CA) /* Setup */
     , (2000064,   2, 0x09000081) /* MotionTable */
     , (2000064,   3, 0x20000015) /* SoundTable */
     , (2000064,   4, 0x30000008) /* CombatTable */
     , (2000064,   6, 0x04000F47) /* PaletteBase */
     , (2000064,   7, 0x10000229) /* ClothingBase */
     , (2000064,   8, 0x06001224) /* Icon */
     , (2000064,  22, 0x3400005B) /* PhysicsEffectTable */
     , (2000064,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000064,   1, 290, 0, 0) /* Strength */
     , (2000064,   2, 300, 0, 0) /* Endurance */
     , (2000064,   3, 190, 0, 0) /* Quickness */
     , (2000064,   4, 200, 0, 0) /* Coordination */
     , (2000064,   5, 190, 0, 0) /* Focus */
     , (2000064,   6, 190, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000064,   1,  1350, 0, 0, 1500) /* MaxHealth */
     , (2000064,   3,  1200, 0, 0, 1500) /* MaxStamina */
     , (2000064,   5,  1000, 0, 0, 1190) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000064,  6, 0, 2, 0, 529, 0, 0) /* MeleeDefense        Trained */
     , (2000064,  7, 0, 2, 0, 368, 0, 0) /* MissileDefense      Trained */
     , (2000064, 15, 0, 2, 0, 296, 0, 0) /* MagicDefense        Trained */
     , (2000064, 31, 0, 2, 0, 230, 0, 0) /* CreatureEnchantment Trained */
     , (2000064, 33, 0, 2, 0, 230, 0, 0) /* LifeMagic           Trained */
     , (2000064, 34, 0, 2, 0, 230, 0, 0) /* WarMagic            Trained */
     , (2000064, 45, 0, 2, 0, 530, 0, 0) /* LightWeapons        Trained */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000064,  0,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000064,  1,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000064,  2,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000064,  3,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000064,  4,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000064,  5,  4, 120, 0.75,  350,  277,  315,  350,  294,  294,  294,  294,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000064,  6,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000064,  7,  4,  0,    0,  350,  277,  315,  350,  294,  294,  294,  294,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000064,  8,  4, 150, 0.75,  350,  277,  315,  350,  294,  294,  294,  294,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000064, 9, 41979,  1, 0, 0.02, False) /* Create Shattered Mana Forge Key (41979) for ContainTreasure */
     , (2000064, 9,     0,  0, 0, 0.98, False) /* Create nothing for ContainTreasure */
     , (2000064, 9, 34277,  1, 0, 0.02, False) /* Create Ancient Falatacot Trinket (34277) for ContainTreasure */
     , (2000064, 9,     0,  0, 0, 0.98, False) /* Create nothing for ContainTreasure */
     , (2000064, 9, 42348,  0, 0, 0.05, False) /* Create Black Coral Heart (42348) for ContainTreasure */
     , (2000064, 9,     0,  0, 0, 0.95, False) /* Create nothing for ContainTreasure */;
