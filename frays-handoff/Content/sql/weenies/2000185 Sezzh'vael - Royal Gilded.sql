-- @owner: frays
-- @owns: wcid:2000185
-- @requires-restart: no
--
-- Colour test copy of the Great Snarl boss 2000179 (royal blue body, gold wings and horns).

DELETE FROM `weenie` WHERE `class_Id` = 2000185;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000185, 'dw-sezzhvael-royal-gilded', 10, '2026-11-05 09:26:05') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000185,   1,         16) /* ItemType - Creature */
     , (2000185,   2,         71) /* CreatureType - Margul */
     , (2000185,   6,         -1) /* ItemsCapacity */
     , (2000185,   7,         -1) /* ContainersCapacity */
     , (2000185,  16,          1) /* ItemUseable - No */
     , (2000185,  25,        400) /* Level */
     , (2000185,  27,          0) /* ArmorType - None */
     , (2000185,  40,          2) /* CombatMode - Melee */
     , (2000185,  68,          9) /* TargetingTactic - Random, TopDamager */
     , (2000185,  72,         22) /* FriendType - Shadow */
     , (2000185,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000185, 101,        131) /* AiAllowedCombatStyle - Unarmed, OneHanded, ThrownWeapon */
     , (2000185, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000185, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000185, 146,    2000000) /* XpOverride */
     , (2000185, 332,        500) /* LuminanceAward */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000185,   1, True ) /* Stuck */
     , (2000185,   6, True ) /* AiUsesMana */
     , (2000185,  11, False) /* IgnoreCollisions */
     , (2000185,  12, True ) /* ReportCollisions */
     , (2000185,  13, False) /* Ethereal */
     , (2000185,  14, True ) /* GravityStatus */
     , (2000185,  19, True ) /* Attackable */
     , (2000185,  50, True ) /* NeverFailCasting */
     , (2000185,  65, True ) /* IgnoreMagicResist */
     , (2000185,  66, True ) /* IgnoreMagicArmor */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000185,   1,       5) /* HeartbeatInterval */
     , (2000185,   2,       0) /* HeartbeatTimestamp */
     , (2000185,   3,       0) /* HealthRate */
     , (2000185,   4,       3) /* StaminaRate */
     , (2000185,   5,       1) /* ManaRate */
     , (2000185,  13,    1.05) /* ArmorModVsSlash */
     , (2000185,  14,       1) /* ArmorModVsPierce */
     , (2000185,  15,    0.95) /* ArmorModVsBludgeon */
     , (2000185,  16,    0.95) /* ArmorModVsCold */
     , (2000185,  17,     1.2) /* ArmorModVsFire */
     , (2000185,  18,     1.2) /* ArmorModVsAcid */
     , (2000185,  19,    0.95) /* ArmorModVsElectric */
     , (2000185,  31,      24) /* VisualAwarenessRange */
     , (2000185,  34,       1) /* PowerupTime */
     , (2000185,  36,       1) /* ChargeSpeed */
     , (2000185,  39,       5) /* DefaultScale */
     , (2000185,  64,    0.85) /* ResistSlash */
     , (2000185,  65,    0.85) /* ResistPierce */
     , (2000185,  66,    0.95) /* ResistBludgeon */
     , (2000185,  67,     0.1) /* ResistFire */
     , (2000185,  68,     1.5) /* ResistCold */
     , (2000185,  69,    0.75) /* ResistAcid */
     , (2000185,  70,    0.95) /* ResistElectric */
     , (2000185,  71,       1) /* ResistHealthBoost */
     , (2000185,  72,       1) /* ResistStaminaDrain */
     , (2000185,  73,       1) /* ResistStaminaBoost */
     , (2000185,  74,       1) /* ResistManaDrain */
     , (2000185,  75,       1) /* ResistManaBoost */
     , (2000185,  77,       1) /* PhysicsScriptIntensity */
     , (2000185,  80,       2) /* AiUseMagicDelay */
     , (2000185, 104,      10) /* ObviousRadarRange */
     , (2000185, 122,       2) /* AiAcquireHealth */
     , (2000185, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000185,   1, 'Sezzh''vael - Royal Gilded') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000185,   1, 0x0200101A) /* Setup */
     , (2000185,   2, 0x0900013F) /* MotionTable */
     , (2000185,   3, 0x200000A8) /* SoundTable */
     , (2000185,   4, 0x3000003A) /* CombatTable */
     , (2000185,   6, 0x040016E8) /* PaletteBase */
     , (2000185,   8, 0x0600304D) /* Icon */
     , (2000185,  19, 0x00000057) /* ActivationAnimation */
     , (2000185,  22, 0x340000A9) /* PhysicsEffectTable */
     , (2000185,  30,         87) /* PhysicsScript - BreatheLightning */
     , (2000185,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000185,   1, 400, 0, 0) /* Strength */
     , (2000185,   2, 400, 0, 0) /* Endurance */
     , (2000185,   3,  60, 0, 0) /* Quickness */
     , (2000185,   4, 400, 0, 0) /* Coordination */
     , (2000185,   5, 400, 0, 0) /* Focus */
     , (2000185,   6, 400, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000185,   1, 799800, 0, 0, 799800) /* MaxHealth */
     , (2000185,   3, 20000, 0, 0, 20000) /* MaxStamina */
     , (2000185,   5, 20000, 0, 0, 20000) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000185,  6, 0, 3, 0,  80, 0, 0) /* MeleeDefense        Specialized */
     , (2000185,  7, 0, 3, 0, 220, 0, 0) /* MissileDefense      Specialized */
     , (2000185, 15, 0, 3, 0,  80, 0, 0) /* MagicDefense        Specialized */
     , (2000185, 24, 0, 3, 0, 120, 0, 0) /* Run                 Specialized */
     , (2000185, 31, 0, 3, 0, 205, 0, 0) /* CreatureEnchantment Specialized */
     , (2000185, 32, 0, 3, 0, 205, 0, 0) /* ItemEnchantment     Specialized */
     , (2000185, 33, 0, 3, 0, 1100, 0, 0) /* LifeMagic           Specialized */
     , (2000185, 34, 0, 3, 0, 1100, 0, 0) /* WarMagic            Specialized */
     , (2000185, 45, 0, 3, 0, 1100, 0, 0) /* LightWeapons        Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000185,  0, 16, 600,  0.2,  525,  551,  525,  499,  249, 1260,  630,  499,    0, 1,  0.4,  0.1,    0,  0.4,  0.1,    0,    0,    0,    0,    0,    0,    0) /* Head */
     , (2000185, 10, 16, 600,  0.2,  525,  551,  525,  499,  249, 1260,  630,  499,    0, 3,    0,  0.2,  0.8,    0,  0.2,  0.8,    0,    0,    0,    0,    0,    0) /* FrontLeg */
     , (2000185, 13, 16, 600,  0.2,  525,  551,  525,  499,  249, 1260,  630,  499,    0, 3,    0,    0,    0,    0,    0,    0,  0.1,  0.3,  0.7,  0.1,  0.3,  0.7) /* RearLeg */
     , (2000185, 16, 16, 600,  0.2,  525,  551,  525,  499,  249, 1260,  630,  499,    0, 2,  0.6,  0.7,  0.2,  0.6,  0.7,  0.2,  0.9,  0.7,  0.3,  0.9,  0.7,  0.3) /* Torso */
     , (2000185, 22, 16, 600,  0.2,    0,    0,    0,    0,    0,    0,    0,    0,    0, 0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0) /* Breath */;

INSERT INTO `weenie_properties_spell_book` (`object_Id`, `spell`, `probability`)
VALUES (2000185,  3806,   2.05)  /* Flame Ring */
     , (2000185,  4423,   2.06)  /* Incantation of Flame Arc */
     , (2000185,  4439,    2.1)  /* Incantation of Flame Bolt */
     , (2000185,  4440,   2.08)  /* Incantation of Flame Streak */
     , (2000185,  4441,   2.06)  /* Incantation of Flame Volley */
     , (2000185,  4481,   2.05)  /* Incantation of Fire Vulnerability Other */;

INSERT INTO `weenie_properties_palette` (`object_Id`, `sub_Palette_Id`, `offset`, `length`)
VALUES (2000185, 67110350, 0, 0)
     , (2000185, 67111920, 31, 10)
     , (2000185, 67115143, 150, 106)
     , (2000185, 67116791, 50, 46);
