-- @owner: frays
-- @owns: wcid:2000069
-- @requires-restart: no
--
-- Frays creature, undead family, elite role: a copy of retail 88302 without spells, emotes or generators; dream tint (Translucency 0.25), size x1.1, no kill XP or lum, T8 death treasure 2000.

DELETE FROM `weenie` WHERE `class_Id` = 2000069;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000069, 'dw-drift-revenant', 10, '2026-10-29 20:23:11') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000069,   1,         16) /* ItemType - Creature */
     , (2000069,   2,         14) /* CreatureType - Undead */
     , (2000069,   3,         69) /* PaletteTemplate - YellowSlime */
     , (2000069,   6,         -1) /* ItemsCapacity */
     , (2000069,   7,         -1) /* ContainersCapacity */
     , (2000069,  16,          1) /* ItemUseable - No */
     , (2000069,  25,        185) /* Level */
     , (2000069,  27,          0) /* ArmorType - None */
     , (2000069,  40,          1) /* CombatMode - NonCombat */
     , (2000069,  68,          3) /* TargetingTactic - Random, Focused */
     , (2000069,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000069, 101,        183) /* AiAllowedCombatStyle - Unarmed, OneHanded, OneHandedAndShield, Bow, Crossbow, ThrownWeapon */
     , (2000069, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000069, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000069, 146,          0) /* XpOverride */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000069,   1, True ) /* Stuck */
     , (2000069,   6, True ) /* AiUsesMana */
     , (2000069,  11, False) /* IgnoreCollisions */
     , (2000069,  12, True ) /* ReportCollisions */
     , (2000069,  13, False) /* Ethereal */
     , (2000069,  14, True ) /* GravityStatus */
     , (2000069,  19, True ) /* Attackable */
     , (2000069,  50, True ) /* NeverFailCasting */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000069,   1,       5) /* HeartbeatInterval */
     , (2000069,   2,       0) /* HeartbeatTimestamp */
     , (2000069,   3,     0.8) /* HealthRate */
     , (2000069,   4,     0.5) /* StaminaRate */
     , (2000069,   5,       2) /* ManaRate */
     , (2000069,  13,       1) /* ArmorModVsSlash */
     , (2000069,  14,     1.3) /* ArmorModVsPierce */
     , (2000069,  15,       1) /* ArmorModVsBludgeon */
     , (2000069,  16,     1.3) /* ArmorModVsCold */
     , (2000069,  17,       1) /* ArmorModVsFire */
     , (2000069,  18,       1) /* ArmorModVsAcid */
     , (2000069,  19,     1.2) /* ArmorModVsElectric */
     , (2000069,  31,      17) /* VisualAwarenessRange */
     , (2000069,  34,       1) /* PowerupTime */
     , (2000069,  36,       1) /* ChargeSpeed */
     , (2000069,  39,    1.32) /* DefaultScale */
     , (2000069,  64,     0.5) /* ResistSlash */
     , (2000069,  65,    0.45) /* ResistPierce */
     , (2000069,  66,    0.65) /* ResistBludgeon */
     , (2000069,  67,    0.65) /* ResistFire */
     , (2000069,  68,    0.55) /* ResistCold */
     , (2000069,  69,    0.55) /* ResistAcid */
     , (2000069,  70,     0.5) /* ResistElectric */
     , (2000069,  71,       1) /* ResistHealthBoost */
     , (2000069,  72,       1) /* ResistStaminaDrain */
     , (2000069,  73,       1) /* ResistStaminaBoost */
     , (2000069,  74,       1) /* ResistManaDrain */
     , (2000069,  75,       1) /* ResistManaBoost */
     , (2000069,  76,    0.25) /* Translucency */
     , (2000069,  80,       3) /* AiUseMagicDelay */
     , (2000069, 104,      10) /* ObviousRadarRange */
     , (2000069, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000069,   1, 'Drift Revenant') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000069,   1, 0x02000FA4) /* Setup */
     , (2000069,   2, 0x09000017) /* MotionTable */
     , (2000069,   3, 0x20000016) /* SoundTable */
     , (2000069,   4, 0x30000000) /* CombatTable */
     , (2000069,   6, 0x040015F0) /* PaletteBase */
     , (2000069,   7, 0x100004C0) /* ClothingBase */
     , (2000069,   8, 0x06002CF5) /* Icon */
     , (2000069,  22, 0x34000028) /* PhysicsEffectTable */
     , (2000069,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000069,   1, 380, 0, 0) /* Strength */
     , (2000069,   2, 400, 0, 0) /* Endurance */
     , (2000069,   3, 340, 0, 0) /* Quickness */
     , (2000069,   4, 340, 0, 0) /* Coordination */
     , (2000069,   5, 470, 0, 0) /* Focus */
     , (2000069,   6, 470, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000069,   1,  9800, 0, 0, 10000) /* MaxHealth */
     , (2000069,   3,  1500, 0, 0, 1900) /* MaxStamina */
     , (2000069,   5,  3080, 0, 0, 3550) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000069,  6, 0, 3, 0, 335, 0, 0) /* MeleeDefense        Specialized */
     , (2000069,  7, 0, 3, 0, 385, 0, 0) /* MissileDefense      Specialized */
     , (2000069, 14, 0, 3, 0, 240, 0, 0) /* ArcaneLore          Specialized */
     , (2000069, 15, 0, 3, 0, 300, 0, 0) /* MagicDefense        Specialized */
     , (2000069, 20, 0, 3, 0,  90, 0, 0) /* Deception           Specialized */
     , (2000069, 31, 0, 3, 0, 295, 0, 0) /* CreatureEnchantment Specialized */
     , (2000069, 33, 0, 3, 0, 295, 0, 0) /* LifeMagic           Specialized */
     , (2000069, 34, 0, 3, 0, 295, 0, 0) /* WarMagic            Specialized */
     , (2000069, 44, 0, 3, 0, 385, 0, 0) /* HeavyWeapons        Specialized */
     , (2000069, 45, 0, 3, 0, 375, 0, 0) /* LightWeapons        Specialized */
     , (2000069, 46, 0, 3, 0, 385, 0, 0) /* FinesseWeapons      Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000069,  0,  4,  0,    0,  385,  385,  501,  385,  501,  385,  385,  462,    0, 1, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0, 0.33,    0,    0) /* Head */
     , (2000069,  1,  4,  0,    0,  385,  385,  501,  385,  501,  385,  385,  462,    0, 2, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0, 0.44, 0.17,    0) /* Chest */
     , (2000069,  2,  4,  0,    0,  385,  385,  501,  385,  501,  385,  385,  462,    0, 3,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0,    0, 0.17,    0) /* Abdomen */
     , (2000069,  3,  4,  0,    0,  385,  385,  501,  385,  501,  385,  385,  462,    0, 1, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0, 0.23, 0.03,    0) /* UpperArm */
     , (2000069,  4,  4,  0,    0,  385,  385,  501,  385,  501,  385,  385,  462,    0, 2,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0,    0,  0.3,    0) /* LowerArm */
     , (2000069,  5,  4,  5, 0.75,  385,  385,  501,  385,  501,  385,  385,  462,    0, 2,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0,    0,  0.2,    0) /* Hand */
     , (2000069,  6,  4,  0,    0,  385,  385,  501,  385,  501,  385,  385,  462,    0, 3,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18,    0, 0.13, 0.18) /* UpperLeg */
     , (2000069,  7,  4,  0,    0,  385,  385,  501,  385,  501,  385,  385,  462,    0, 3,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6,    0,    0,  0.6) /* LowerLeg */
     , (2000069,  8,  4,  5, 0.75,  385,  385,  501,  385,  501,  385,  385,  462,    0, 3,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22,    0,    0, 0.22) /* Foot */;

INSERT INTO `weenie_properties_create_list` (`object_Id`, `destination_Type`, `weenie_Class_Id`, `stack_Size`, `palette`, `shade`, `try_To_Bond`)
VALUES (2000069, 2, 25500,  1, 0, 1, False) /* Create Khopesh (25500) for Wield */
     , (2000069, 9, 39128,  1, 0, 0.18, False) /* Create Invoking Stone (39128) for ContainTreasure */
     , (2000069, 9, 39123,  1, 0, 0.18, False) /* Create Invoking Stone (39123) for ContainTreasure */
     , (2000069, 9, 39121,  1, 0, 0.18, False) /* Create Invoking Stone (39121) for ContainTreasure */
     , (2000069, 9, 39126,  1, 0, 0.18, False) /* Create Invoking Stone (39126) for ContainTreasure */
     , (2000069, 9,     0,  0, 0, 0.28, False) /* Create nothing for ContainTreasure */;
