using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.KnotLoot;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// Task 9: Shield of the Rose's armor half (KnotTraitId.ShieldTrade, Primary) is added to the BASE shield armor
    /// level at every place a shield's AL is read: the melee/missile shield mod, Thorns, the combat simulator and
    /// appraisal. It is resolved at read time and never written to the item.
    /// </summary>
    [TestClass]
    public class KnotTraitShieldArmorTests
    {
        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys =
        {
            "knot_trait_shield_trade_enabled",
        };

        private static readonly string[] LongKeys =
        {
            "knot_loot_grael_window_low_level", "knot_loot_grael_window_high_level",
        };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_grael_window_low_start", "knot_loot_grael_window_high_start", "knot_loot_grael_window_width",
            "knot_trait_shield_trade_armor_min", "knot_trait_shield_trade_armor_max",
            "knot_trait_shield_trade_health_min", "knot_trait_shield_trade_health_max",
        };

        private static uint nextGuid = 0x7F190000;

        private Func<Player, KnotTraitSuppressionKind, bool> savedSource;

        [TestInitialize]
        public void Seed()
        {
            savedSource = KnotTraitSuppression.PlayerSuppressedSource;
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
            RestoreDefaults();
        }

        [TestCleanup]
        public void Cleanup()
        {
            KnotTraitSuppression.PlayerSuppressedSource = savedSource;
            RestoreDefaults();
        }

        private static void RestoreDefaults()
        {
            foreach (var k in BoolKeys)
                Assert.IsTrue(PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item), k);

            foreach (var k in LongKeys)
                Assert.IsTrue(PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item), k);

            foreach (var k in DoubleKeys)
                Assert.IsTrue(PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item), k);
        }

        // ---------------- the helper ----------------

        [TestMethod]
        public void Bonus_P50_Is313_AndTopIs500()
        {
            // level 300, u 0.5 on the Grael window: p = 0.5, so 125 + 0.5 * 375 = 312.5, rounded away from zero.
            var mid = Shield(300, 0.5);
            Assert.AreEqual(0.5, KnotTraitValue.ResolveOn(mid, KnotTraitId.ShieldTrade, null).P, 1e-9, "harness control: p = 0.5");
            Assert.AreEqual(313, KnotTraitCombat.ShieldArmorBonus(mid, null));

            // level 400, u 1.0: p = 0.70 + 0.30 = 1.0, the top of the range.
            Assert.AreEqual(500, KnotTraitCombat.ShieldArmorBonus(Shield(400, 1.0), null));

            // level 200, u 0.0: p = 0, the bottom of the range.
            Assert.AreEqual(125, KnotTraitCombat.ShieldArmorBonus(Shield(200, 0.0), null));
        }

        [TestMethod]
        public void Bonus_MatchesTheAppraisalLine()
        {
            // the displayed number and the applied number must be the same rounding of the same Primary
            var shield = Shield(300, 0.5);
            var lines = string.Join("\n", KnotTraitDisplay.GetAppraisalLines(shield));

            StringAssert.Contains(lines, $"+{KnotTraitCombat.ShieldArmorBonus(shield, null)} shield armor");
        }

        [TestMethod]
        public void Bonus_MechanicOff_IsZero_BaseUntouched()
        {
            var shield = Shield(300, 0.5);
            shield.ChangesDetected = false;

            Assert.AreEqual(313, KnotTraitCombat.ShieldArmorBonus(shield, null), "control: on by default");

            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_shield_trade_enabled", false));
            Assert.AreEqual(0, KnotTraitCombat.ShieldArmorBonus(shield, null), "mechanic switch off removes the bonus");

            Assert.AreEqual(350, shield.GetProperty(PropertyInt.ArmorLevel), "the stored base AL is never touched");
            Assert.AreEqual(0.5, shield.GetProperty(PropertyFloat.KnotTraitShieldTrade).Value, 1e-12);
            Assert.IsFalse(shield.ChangesDetected, "the helper must never write to the item");
        }

        [TestMethod]
        public void Bonus_AbsentTrait_OrNullShield_IsZero()
        {
            Assert.AreEqual(0, KnotTraitCombat.ShieldArmorBonus(Shield(300, null), null), "an ordinary shield gets nothing");
            Assert.AreEqual(0, KnotTraitCombat.ShieldArmorBonus(null, null));
        }

        [TestMethod]
        public void Bonus_FollowsTheWielder()
        {
            var shield = Shield(300, 0.5);
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            var monster = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

            Assert.AreEqual(313, KnotTraitCombat.ShieldArmorBonus(shield, player), "control: an unsuppressed Player gets it");
            Assert.AreEqual(313, KnotTraitCombat.ShieldArmorBonus(shield, null), "item-centric (appraisal) read gets it");
            Assert.AreEqual(0, KnotTraitCombat.ShieldArmorBonus(shield, monster), "traits apply only to Player wielders");

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => kind == KnotTraitSuppressionKind.Equipment;
            Assert.AreEqual(0, KnotTraitCombat.ShieldArmorBonus(shield, player), "Equipment-kind suppression removes it");
            Assert.AreEqual(313, KnotTraitCombat.ShieldArmorBonus(shield, null), "appraisal stays item-centric under suppression");
        }

        // ---------------- source pins ----------------

        private const string ServerRoot = "Source/ACE.Server/";
        private const string Bonus = "KnotTraitCombat.ShieldArmorBonus(";

        [TestMethod]
        public void EveryShieldArmorLevelRead_AddsTheKnotBonus()
        {
            foreach (var (file, method) in new[]
            {
                ("WorldObjects/Creature_Combat.cs", "public float GetShieldMod(WorldObject attacker"),
                ("WorldObjects/Player_ClassAbilityCombat.cs", "public void ApplyThornsReflect("),
                ("CombatSimulator/ProfileBuilder.cs", "private static ShieldRow BuildShieldRow("),
                ("CombatSimulator/MonsterDefenderProfile.cs", "private static ShieldRow BuildShieldRow("),
                ("Network/Structure/AppraiseInfo.cs", "private void AddPropertyEnchantments("),
            })
            {
                var body = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + file), method));
                StringAssert.Contains(body, Bonus, file);
                Assert.IsFalse(body.Contains("PropertyFloat.KnotTrait", StringComparison.Ordinal), $"{file} must not read a Knot trait property directly");
            }

            // sweep: every server file that reads a shield's stored AL also adds the bonus
            var swept = 0;

            foreach (var path in Directory.EnumerateFiles(ServerDir(), "*.cs", SearchOption.AllDirectories))
            {
                var text = Code(File.ReadAllText(path));

                if (!text.Contains("shield.GetProperty(PropertyInt.ArmorLevel)", StringComparison.Ordinal))
                    continue;

                swept++;
                StringAssert.Contains(text, Bonus, path);
            }

            Assert.AreEqual(4, swept, "harness control: GetShieldMod, Thorns and the player and monster BuildShieldRow each read shield.GetProperty(PropertyInt.ArmorLevel)");
        }

        [TestMethod]
        public void Bonus_SitsInTheBaseTerm_AtEverySite()
        {
            // GetShieldMod: base term, before the Hollow scaling of the enchantment half (modSL)
            var shieldMod = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "WorldObjects/Creature_Combat.cs"), "public float GetShieldMod(WorldObject attacker"));
            StringAssert.Contains(shieldMod, "var baseSL = (shield.GetProperty(PropertyInt.ArmorLevel) ?? 0.0f) + KnotTraitCombat.ShieldArmorBonus(shield, this);");
            StringAssert.Contains(shieldMod, "modSL = HollowMath.ScaleArmorInt(modSL, ignoreMagicArmor, hollowPvp, IgnoreMagicArmorPvpScalar);", "Hollow scales the enchantment half only");
            StringAssert.Contains(shieldMod, "var effectiveSL = baseSL + modSL;");
            StringAssert.Contains(shieldMod, "effectiveLevel = Math.Min(effectiveLevel, shieldCap);", "the shield-skill cap stays");
            Assert.AreEqual(1, Count(shieldMod, Bonus));

            // Thorns
            var thorns = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "WorldObjects/Player_ClassAbilityCombat.cs"), "public void ApplyThornsReflect("));
            StringAssert.Contains(thorns, "var shieldArmorLevel = (shield.GetProperty(PropertyInt.ArmorLevel) ?? 0) + KnotTraitCombat.ShieldArmorBonus(shield, this) + shield.EnchantmentManager.GetArmorMod();");
            Assert.AreEqual(1, Count(thorns, Bonus));

            // combat simulator: baseSL, so ComposeShieldLevel's ignoreMagicArmor zeroing of modSL never touches it
            var row = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "CombatSimulator/ProfileBuilder.cs"), "private static ShieldRow BuildShieldRow("));
            StringAssert.Contains(row, "var baseSL = (shield.GetProperty(PropertyInt.ArmorLevel) ?? 0.0f) + KnotTraitCombat.ShieldArmorBonus(shield, player);");
            Assert.AreEqual(1, Count(row, Bonus));

            // appraisal: item-centric, wielder null
            var appraise = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "Network/Structure/AppraiseInfo.cs"), "private void AddPropertyEnchantments("));
            StringAssert.Contains(appraise, "PropertiesInt[PropertyInt.ArmorLevel] += wo.EnchantmentManager.GetArmorMod() + KnotTraitCombat.ShieldArmorBonus(wo, null);");
            Assert.AreEqual(1, Count(appraise, Bonus));
        }

        /// <summary>
        /// UPDATED DELIBERATELY for the ward shield (Runic Ward / Sanguine Ward, owner ruling 2026-10-24), which is a
        /// second flat term in GetShieldMod beside this file's Knot bonus. It is NOT a stored shield AL and NOT in the
        /// base term: with a real shield it is added AFTER the Shield-skill cap and BEFORE the ignore-shield term; with
        /// no shield it joins Defensive Weapon (formerly Arcane Defender) on the uncapped virtual-shield path. Since 2026-10-25 Defensive
        /// Weapon also joins the ward in the equipped-shield slot (DefensiveWeaponMath.AfterShieldCap), summed with it. It must never reach Thorns or
        /// Shield Block (both gate on an equipped shield directly), so GetWardShieldLevel has exactly one caller.
        ///
        /// THE COMBAT SIMULATOR MIRROR (CombatSimulator/ProfileBuilder.cs BuildShieldRow) IS DELIBERATELY NOT
        /// UPDATED: a ward is a transient pool, not a gear property, and the simulator profiles gear.
        /// </summary>
        [TestMethod]
        public void WardShield_AfterTheCap_BeforeIgnoreShield_UncappedWithNoShield_AndNowhereElse()
        {
            var shieldMod = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "WorldObjects/Creature_Combat.cs"), "public float GetShieldMod(WorldObject attacker"));

            // equipped-shield path: cap, then ward (with Defensive Weapon in the same call), then ignore-shield
            var cap = shieldMod.IndexOf("effectiveLevel = Math.Min(effectiveLevel, shieldCap);", StringComparison.Ordinal);
            var ward = shieldMod.IndexOf("effectiveLevel = (float)DefensiveWeaponMath.AfterShieldCap(effectiveLevel, wardShieldPlayer.GetDefensiveWeaponShieldLevel(), wardShieldPlayer.GetWardShieldLevel(attacker, facing));", StringComparison.Ordinal);
            var ignore = shieldMod.IndexOf("effectiveLevel *= ignoreShieldMod;", StringComparison.Ordinal);
            Assert.IsTrue(cap >= 0 && ward > cap && ignore > ward, "the ward is added after the Shield-skill cap and before the ignore-shield term");

            // no-shield path: the ward joins Defensive Weapon's level on the uncapped ArcaneShieldMod row
            var noShield = shieldMod.IndexOf("if (shield == null)", StringComparison.Ordinal);
            var virtualLevel = shieldMod.IndexOf("var virtualShieldLevel = defensiveWeapon + wardShieldLevel;", StringComparison.Ordinal);
            var arcaneRow = shieldMod.IndexOf("return WeaponModCombat.ArcaneShieldMod(virtualShieldLevel, attacker.GetIgnoreShieldMod(weapon));", StringComparison.Ordinal);
            Assert.IsTrue(noShield >= 0 && virtualLevel > noShield && arcaneRow > virtualLevel && arcaneRow < cap,
                "with no shield the ward rides the uncapped virtual-shield row, before the equipped-shield path");
            Assert.AreEqual(2, Count(shieldMod, ".GetWardShieldLevel(attacker, facing)"), "one read per branch");

            // Thorns never sees the ward, and GetShieldMod is the only caller anywhere
            var thorns = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "WorldObjects/Player_ClassAbilityCombat.cs"), "public void ApplyThornsReflect("));
            Assert.IsFalse(thorns.Contains("GetWardShieldLevel", StringComparison.Ordinal));

            var callers = new List<string>();

            foreach (var path in Directory.EnumerateFiles(ServerDir(), "*.cs", SearchOption.AllDirectories))
            {
                var text = Code(File.ReadAllText(path));

                if (text.Contains(".GetWardShieldLevel(", StringComparison.Ordinal))
                    callers.Add(Path.GetFileName(path));
            }

            CollectionAssert.AreEqual(new[] { "Creature_Combat.cs" }, callers, "the ward shield is read only by GetShieldMod");
        }

        [TestMethod]
        public void Helper_ReadsThroughResolveOnOnly()
        {
            var body = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "KnotLoot/KnotTraitCombat.cs"), "public static int ShieldArmorBonus(WorldObject shield, WorldObject wielder)"));

            StringAssert.Contains(body, "KnotTraitValue.ResolveOn(shield, KnotTraitId.ShieldTrade, wielder)");
            StringAssert.Contains(body, "MidpointRounding.AwayFromZero");
            Assert.IsFalse(body.Contains("GetProperty(PropertyFloat.KnotTrait", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("SetProperty(", StringComparison.Ordinal));
        }

        // ---------------- helpers ----------------

        private static WorldObject Shield(int level, double? u)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007451,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ArmorLevel, 350 },
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                    { PropertyInt.KnotLootLevel, level },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "grael" } },
            };

            if (u.HasValue)
                weenie.PropertiesFloat[PropertyFloat.KnotTraitShieldTrade] = u.Value;

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static string ServerDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");

                if (Directory.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        /// <summary>The body with // comments removed, so a pin cannot be satisfied by a comment.</summary>
        private static string Code(string body) => Regex.Replace(body.Replace("\r\n", "\n"), @"//[^\n]*", "");

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
