-- @owner: frays
-- @owns: wcid:2000184
-- @requires-restart: no
--
-- Colour test copy of the Great Snarl boss 2000179 (black body, acid-green wings).

DELETE FROM `weenie` WHERE `class_Id` = 2000184;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000184, 'dw-sezzhvael-venom', 10, '2026-11-05 09:26:05') /* Creature */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000184,   1,         16) /* ItemType - Creature */
     , (2000184,   2,         71) /* CreatureType - Margul */
     , (2000184,   6,         -1) /* ItemsCapacity */
     , (2000184,   7,         -1) /* ContainersCapacity */
     , (2000184,  16,          1) /* ItemUseable - No */
     , (2000184,  25,        400) /* Level */
     , (2000184,  27,          0) /* ArmorType - None */
     , (2000184,  40,          2) /* CombatMode - Melee */
     , (2000184,  68,          9) /* TargetingTactic - Random, TopDamager */
     , (2000184,  72,         22) /* FriendType - Shadow */
     , (2000184,  93,       1032) /* PhysicsState - ReportCollisions, Gravity */
     , (2000184, 101,        131) /* AiAllowedCombatStyle - Unarmed, OneHanded, ThrownWeapon */
     , (2000184, 133,          2) /* ShowableOnRadar - ShowMovement */
     , (2000184, 140,          1) /* AiOptions - CanOpenDoors */
     , (2000184, 146,    2000000) /* XpOverride */
     , (2000184, 332,        500) /* LuminanceAward */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000184,   1, True ) /* Stuck */
     , (2000184,   6, True ) /* AiUsesMana */
     , (2000184,  11, False) /* IgnoreCollisions */
     , (2000184,  12, True ) /* ReportCollisions */
     , (2000184,  13, False) /* Ethereal */
     , (2000184,  14, True ) /* GravityStatus */
     , (2000184,  19, True ) /* Attackable */
     , (2000184,  50, True ) /* NeverFailCasting */
     , (2000184,  65, True ) /* IgnoreMagicResist */
     , (2000184,  66, True ) /* IgnoreMagicArmor */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000184,   1,       5) /* HeartbeatInterval */
     , (2000184,   2,       0) /* HeartbeatTimestamp */
     , (2000184,   3,       0) /* HealthRate */
     , (2000184,   4,       3) /* StaminaRate */
     , (2000184,   5,       1) /* ManaRate */
     , (2000184,  13,    1.05) /* ArmorModVsSlash */
     , (2000184,  14,       1) /* ArmorModVsPierce */
     , (2000184,  15,    0.95) /* ArmorModVsBludgeon */
     , (2000184,  16,    0.95) /* ArmorModVsCold */
     , (2000184,  17,     1.2) /* ArmorModVsFire */
     , (2000184,  18,     1.2) /* ArmorModVsAcid */
     , (2000184,  19,    0.95) /* ArmorModVsElectric */
     , (2000184,  31,      24) /* VisualAwarenessRange */
     , (2000184,  34,       1) /* PowerupTime */
     , (2000184,  36,       1) /* ChargeSpeed */
     , (2000184,  39,       5) /* DefaultScale */
     , (2000184,  64,    0.85) /* ResistSlash */
     , (2000184,  65,    0.85) /* ResistPierce */
     , (2000184,  66,    0.95) /* ResistBludgeon */
     , (2000184,  67,     0.1) /* ResistFire */
     , (2000184,  68,     1.5) /* ResistCold */
     , (2000184,  69,    0.75) /* ResistAcid */
     , (2000184,  70,    0.95) /* ResistElectric */
     , (2000184,  71,       1) /* ResistHealthBoost */
     , (2000184,  72,       1) /* ResistStaminaDrain */
     , (2000184,  73,       1) /* ResistStaminaBoost */
     , (2000184,  74,       1) /* ResistManaDrain */
     , (2000184,  75,       1) /* ResistManaBoost */
     , (2000184,  77,       1) /* PhysicsScriptIntensity */
     , (2000184,  80,       2) /* AiUseMagicDelay */
     , (2000184, 104,      10) /* ObviousRadarRange */
     , (2000184, 122,       2) /* AiAcquireHealth */
     , (2000184, 125,       1) /* ResistHealthDrain */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000184,   1, 'Sezzh''vael - Venom') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000184,   1, 0x0200101A) /* Setup */
     , (2000184,   2, 0x0900013F) /* MotionTable */
     , (2000184,   3, 0x200000A8) /* SoundTable */
     , (2000184,   4, 0x3000003A) /* CombatTable */
     , (2000184,   6, 0x040016E8) /* PaletteBase */
     , (2000184,   8, 0x0600304D) /* Icon */
     , (2000184,  19, 0x00000057) /* ActivationAnimation */
     , (2000184,  22, 0x340000A9) /* PhysicsEffectTable */
     , (2000184,  30,         87) /* PhysicsScript - BreatheLightning */
     , (2000184,  35,       2000) /* DeathTreasureType - Loot Tier: 8 */;

INSERT INTO `weenie_properties_attribute` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`)
VALUES (2000184,   1, 400, 0, 0) /* Strength */
     , (2000184,   2, 400, 0, 0) /* Endurance */
     , (2000184,   3,  60, 0, 0) /* Quickness */
     , (2000184,   4, 400, 0, 0) /* Coordination */
     , (2000184,   5, 400, 0, 0) /* Focus */
     , (2000184,   6, 400, 0, 0) /* Self */;

INSERT INTO `weenie_properties_attribute_2nd` (`object_Id`, `type`, `init_Level`, `level_From_C_P`, `c_P_Spent`, `current_Level`)
VALUES (2000184,   1, 799800, 0, 0, 799800) /* MaxHealth */
     , (2000184,   3, 20000, 0, 0, 20000) /* MaxStamina */
     , (2000184,   5, 20000, 0, 0, 20000) /* MaxMana */;

INSERT INTO `weenie_properties_skill` (`object_Id`, `type`, `level_From_P_P`, `s_a_c`, `p_p`, `init_Level`, `resistance_At_Last_Check`, `last_Used_Time`)
VALUES (2000184,  6, 0, 3, 0,  80, 0, 0) /* MeleeDefense        Specialized */
     , (2000184,  7, 0, 3, 0, 220, 0, 0) /* MissileDefense      Specialized */
     , (2000184, 15, 0, 3, 0,  80, 0, 0) /* MagicDefense        Specialized */
     , (2000184, 24, 0, 3, 0, 120, 0, 0) /* Run                 Specialized */
     , (2000184, 31, 0, 3, 0, 205, 0, 0) /* CreatureEnchantment Specialized */
     , (2000184, 32, 0, 3, 0, 205, 0, 0) /* ItemEnchantment     Specialized */
     , (2000184, 33, 0, 3, 0, 1100, 0, 0) /* LifeMagic           Specialized */
     , (2000184, 34, 0, 3, 0, 1100, 0, 0) /* WarMagic            Specialized */
     , (2000184, 45, 0, 3, 0, 1100, 0, 0) /* LightWeapons        Specialized */;

INSERT INTO `weenie_properties_body_part` (`object_Id`, `key`, `d_Type`, `d_Val`, `d_Var`, `base_Armor`, `armor_Vs_Slash`, `armor_Vs_Pierce`, `armor_Vs_Bludgeon`, `armor_Vs_Cold`, `armor_Vs_Fire`, `armor_Vs_Acid`, `armor_Vs_Electric`, `armor_Vs_Nether`, `b_h`, `h_l_f`, `m_l_f`, `l_l_f`, `h_r_f`, `m_r_f`, `l_r_f`, `h_l_b`, `m_l_b`, `l_l_b`, `h_r_b`, `m_r_b`, `l_r_b`)
VALUES (2000184,  0, 16, 600,  0.2,  525,  551,  525,  499,  249, 1260,  630,  499,    0, 1,  0.4,  0.1,    0,  0.4,  0.1,    0,    0,    0,    0,    0,    0,    0) /* Head */
     , (2000184, 10, 16, 600,  0.2,  525,  551,  525,  499,  249, 1260,  630,  499,    0, 3,    0,  0.2,  0.8,    0,  0.2,  0.8,    0,    0,    0,    0,    0,    0) /* FrontLeg */
     , (2000184, 13, 16, 600,  0.2,  525,  551,  525,  499,  249, 1260,  630,  499,    0, 3,    0,    0,    0,    0,    0,    0,  0.1,  0.3,  0.7,  0.1,  0.3,  0.7) /* RearLeg */
     , (2000184, 16, 16, 600,  0.2,  525,  551,  525,  499,  249, 1260,  630,  499,    0, 2,  0.6,  0.7,  0.2,  0.6,  0.7,  0.2,  0.9,  0.7,  0.3,  0.9,  0.7,  0.3) /* Torso */
     , (2000184, 22, 16, 600,  0.2,    0,    0,    0,    0,    0,    0,    0,    0,    0, 0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0) /* Breath */;

INSERT INTO `weenie_properties_spell_book` (`object_Id`, `spell`, `probability`)
VALUES (2000184,  3806,   2.05)  /* Flame Ring */
     , (2000184,  4423,   2.06)  /* Incantation of Flame Arc */
     , (2000184,  4439,    2.1)  /* Incantation of Flame Bolt */
     , (2000184,  4440,   2.08)  /* Incantation of Flame Streak */
     , (2000184,  4441,   2.06)  /* Incantation of Flame Volley */
     , (2000184,  4481,   2.05)  /* Incantation of Fire Vulnerability Other */;

INSERT INTO `weenie_properties_palette` (`object_Id`, `sub_Palette_Id`, `offset`, `length`)
VALUES (2000184, 67110350, 0, 0)
     , (2000184, 67113847, 50, 46)
     , (2000184, 67114919, 31, 10)
     , (2000184, 67116820, 150, 106);
