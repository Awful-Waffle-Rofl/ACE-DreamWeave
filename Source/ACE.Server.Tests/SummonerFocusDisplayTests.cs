using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.Structure;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Appraisal text for a Summoner focus: the client has no label for the pet/wielder trade, so it is drawn as
    /// "Property Details" lines and the native damage-bonus float is suppressed. Seeds every PropertyManager key it
    /// reads and restores it from the code defaults.
    /// </summary>
    [TestClass]
    public class SummonerFocusDisplayTests
    {
        private const string PetLine = "- Summoner Focus: your summons deal +70% damage and take 35% less damage while this is wielded";
        private const string WielderLine = "- Your own damage is 75% lower while this is wielded";
        private const string ImbuePrefix = "- Your summons share this focus's ";

        private static readonly string[] DoubleKeys =
        {
            SummonerFocus.PetDamageScaleKey, SummonerFocus.PetDefenseScaleKey, SummonerFocus.PetDefenseFloorKey, SummonerFocus.WielderDamageModKey,
            SummonerFocus.PetModTransferPercentKey, SummonerFocus.PetImbueTransferPercentKey,
        };

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetDamageScaleKey, 5.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseScaleKey, 2.5);
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseFloorKey, 0.25);
            PropertyManager.ModifyDouble(SummonerFocus.WielderDamageModKey, 0.25);
            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 1.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetImbueTransferPercentKey, 1.0);
            PropertyManager.ModifyBool("weapon_mods_enabled", true);
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (var key in DoubleKeys)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);

            PropertyManager.ModifyBool("weapon_mods_enabled", (bool)DefaultPropertyManager.DefaultBooleanProperties["weapon_mods_enabled"].Item);
        }

        [TestMethod]
        public void Focus_shows_the_pet_bonus_and_the_wielder_cut()
        {
            var focus = SummonerFocusTests.Wand(true, 1.14);

            CollectionAssert.AreEqual(new[] { PetLine, WielderLine }, SummonerFocusDisplay.GetAppraisalLines(focus).ToList());
        }

        [TestMethod]
        public void Non_focus_and_null_show_nothing()
        {
            Assert.AreEqual(0, SummonerFocusDisplay.GetAppraisalLines(SummonerFocusTests.Wand(false, 1.14)).Count());
            Assert.AreEqual(0, SummonerFocusDisplay.GetAppraisalLines(null).Count());
        }

        [TestMethod]
        public void Shown_numbers_follow_the_live_tunables()
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetDamageScaleKey, 10.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseScaleKey, 1.0);
            PropertyManager.ModifyDouble(SummonerFocus.WielderDamageModKey, 0.5);

            var lines = SummonerFocusDisplay.GetAppraisalLines(SummonerFocusTests.Wand(true, 1.14)).ToList();

            Assert.AreEqual("- Summoner Focus: your summons deal +140% damage and take 14% less damage while this is wielded", lines[0]);
            Assert.AreEqual("- Your own damage is 50% lower while this is wielded", lines[1]);
        }

        [TestMethod]
        public void Defense_scale_zero_shows_the_short_line_without_the_less_damage_clause()
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseScaleKey, 0.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetDamageScaleKey, 1.0);

            var lines = SummonerFocusDisplay.GetAppraisalLines(SummonerFocusTests.Wand(true, 1.18)).ToList();

            Assert.AreEqual("- Summoner Focus: your summons deal +18% damage while this is wielded", lines[0]);
            Assert.IsFalse(lines[0].Contains("less damage"));
        }

        [TestMethod]
        public void Defense_scale_above_zero_shows_the_full_line()
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseScaleKey, 2.5);
            PropertyManager.ModifyDouble(SummonerFocus.PetDamageScaleKey, 1.0);

            var lines = SummonerFocusDisplay.GetAppraisalLines(SummonerFocusTests.Wand(true, 1.18)).ToList();

            Assert.AreEqual("- Summoner Focus: your summons deal +18% damage and take 45% less damage while this is wielded", lines[0]);
        }

        [TestMethod]
        public void Tiny_defense_scale_that_rounds_to_zero_shows_the_short_line()
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseScaleKey, 0.001);
            PropertyManager.ModifyDouble(SummonerFocus.PetDamageScaleKey, 1.0);

            var lines = SummonerFocusDisplay.GetAppraisalLines(SummonerFocusTests.Wand(true, 1.14)).ToList();

            Assert.AreEqual("- Summoner Focus: your summons deal +14% damage while this is wielded", lines[0]);
        }

        [TestMethod]
        public void Defense_scale_above_zero_but_mod_at_or_below_one_shows_the_short_line()
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseScaleKey, 2.5);

            var lines = SummonerFocusDisplay.GetAppraisalLines(SummonerFocusTests.Wand(true, 1.0)).ToList();

            Assert.AreEqual("- Summoner Focus: your summons deal +0% damage while this is wielded", lines[0]);
        }

        [TestMethod]
        public void Defense_floor_caps_the_shown_reduction()
        {
            // 1 - 2.5 * 0.5 = -0.25, clamped to the 0.25 floor: 75% less
            var lines = SummonerFocusDisplay.GetAppraisalLines(SummonerFocusTests.Wand(true, 1.5)).ToList();

            Assert.AreEqual("- Summoner Focus: your summons deal +250% damage and take 75% less damage while this is wielded", lines[0]);
        }

        /// <summary>Weapon-mod appraisal lines for an item, the surface the route tag lives on.</summary>
        private static List<string> ModLines(WorldObject wo) => WeaponModDisplay.GetAppraisalLines(wo);

        private static void AssertNoRouteLines(IEnumerable<string> lines)
        {
            Assert.IsFalse(lines.Any(l => l.StartsWith("- Empowers your summons") || l.StartsWith("- Also empowers your summons")), "the old route lines are gone");
        }

        [TestMethod]
        public void A_pet_transferred_record_adds_no_route_line_to_the_focus_lines()
        {
            var bloodthirst = SummonerFocusTests.Wand(true, 1.14);
            bloodthirst.SetProperty(PropertyFloat.WeaponModBloodthirst, 1.0);

            CollectionAssert.AreEqual(new[] { PetLine, WielderLine }, SummonerFocusDisplay.GetAppraisalLines(bloodthirst).ToList());
        }

        [TestMethod]
        public void Weapon_mod_line_is_tagged_with_the_route_between_name_and_bracket()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            wand.SetProperty(PropertyFloat.WeaponModBloodthirst, 1.0);
            wand.SetProperty(PropertyFloat.WeaponModDefensiveWeapon, 1.0);

            var lines = ModLines(wand);

            Assert.AreEqual(2, lines.Count, string.Join(" | ", lines));
            Assert.IsTrue(Regex.IsMatch(lines[0], @"^- Bloodthirst \(Summon\) \[\d+%\]: "), lines[0]);
            Assert.IsTrue(Regex.IsMatch(lines[1], @"^- Defensive Weapon \(You \+ Summon\) \[\d+%\]: "), lines[1]);
        }

        [TestMethod]
        public void Tag_applies_to_paragon_only_and_rolled_plus_paragon_lines()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            wand.SetProperty(PropertyInt.ParagonModSlot1, (int)WeaponModId.Devastation);
            Assert.IsNull(wand.GetProperty(PropertyFloat.WeaponModDevastation), "no Tourmaline record: gems only");

            var paragonOnly = ModLines(wand).Single(l => l.StartsWith("- Devastation"));
            Assert.IsTrue(paragonOnly.StartsWith("- Devastation (Summon) ["), paragonOnly);
            Assert.IsTrue(paragonOnly.Contains("(Paragon)"), paragonOnly);

            wand.SetProperty(PropertyFloat.WeaponModDevastation, 1.0);
            var both = ModLines(wand).Single(l => l.StartsWith("- Devastation"));
            Assert.IsTrue(both.StartsWith("- Devastation (Summon) ["), both);
            Assert.IsTrue(both.Contains("(rolled"), both);
        }

        [TestMethod]
        public void A_non_focus_carrying_the_same_mods_gets_no_tag()
        {
            var wand = SummonerFocusTests.Wand(false, 1.14);
            wand.SetProperty(PropertyFloat.WeaponModBloodthirst, 1.0);
            wand.SetProperty(PropertyFloat.WeaponModDefensiveWeapon, 1.0);

            var lines = ModLines(wand);

            Assert.AreEqual(2, lines.Count);
            Assert.IsFalse(lines.Any(l => l.Contains("(Summon") || l.Contains("(You + Summon")), string.Join(" | ", lines));
            Assert.IsTrue(lines[0].StartsWith("- Bloodthirst ["), lines[0]);
            Assert.IsTrue(lines[1].StartsWith("- Defensive Weapon ["), lines[1]);
        }

        /// <summary>The weapon-mod section, and so every route tag, is behind weapon_mods_enabled in the built profile.</summary>
        [TestMethod]
        public void Weapon_mod_section_and_tags_are_off_while_weapon_mods_are_disabled()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            wand.SetProperty(PropertyFloat.WeaponModBloodthirst, 1.0);
            wand.SetProperty(PropertyInt.ParagonModSlot1, (int)WeaponModId.Devastation);

            // positive control: gate on, the section and the tag are there
            var on = new AppraiseInfo(wand, null).PropertiesString[PropertyString.Use];
            Assert.IsTrue(on.Contains("Weapon Mods:") && on.Contains("(Summon"), on);

            PropertyManager.ModifyBool("weapon_mods_enabled", false);
            Assert.IsFalse(WeaponModRegistry.Enabled());

            new AppraiseInfo(wand, null).PropertiesString.TryGetValue(PropertyString.Use, out var off);
            off ??= string.Empty;

            Assert.IsFalse(off.Contains("Weapon Mods:"), off);
            Assert.IsFalse(off.Contains("(Summon"), off);
            Assert.IsTrue(off.Contains(PetLine), "the focus lines still render with the gate off");
        }

        /// <summary>The route table sorts each mod onto its tag, lines alphabetical by mod name; a Wielder-route mod carries none.</summary>
        [TestMethod]
        public void Route_tags_follow_the_route_table_in_alphabetical_order()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            foreach (var record in new[] { PropertyFloat.WeaponModExecution, PropertyFloat.WeaponModBloodthirst, PropertyFloat.WeaponModFocus,
                PropertyFloat.WeaponModDefensiveWeapon, PropertyFloat.WeaponModSiphon, PropertyFloat.WeaponModEfficiency })
                wand.SetProperty(record, 1.0);

            var names = ModLines(wand).Select(l => l.Substring(2, l.IndexOf(" [", StringComparison.Ordinal) - 2)).ToList();

            Assert.AreEqual(string.Join(" | ", new[]
            {
                "Bloodthirst (Summon)", "Defensive Weapon (You + Summon)", "Efficiency", "Execution (Summon)", "Precision (Summon)", "Siphon (You + Summon)",
            }), string.Join(" | ", names));
        }

        /// <summary>The built profile of a focus: the two focus lines under Property Details, then a blank line, then the tagged mods under Weapon Mods.</summary>
        [TestMethod]
        public void Built_profile_of_a_focus_renders_two_sections()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            wand.SetProperty(PropertyFloat.WeaponModBloodthirst, 1.0);

            var use = new AppraiseInfo(wand, null).PropertiesString[PropertyString.Use];

            Assert.AreEqual(PetLine + "\n" + WielderLine, use.Split("\n\nWeapon Mods:\n")[0].Substring("Property Details:\n".Length));
            Assert.IsTrue(use.StartsWith("Property Details:\n"), use);
            Assert.IsTrue(Regex.IsMatch(use, @"\n\nWeapon Mods:\n- Bloodthirst \(Summon\) \[\d+%\]: [^\n]+$"), use);
        }

        [TestMethod]
        public void Siphon_is_shared_and_attunement_is_untagged()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            wand.SetProperty(PropertyFloat.WeaponModSiphon, 1.0);
            wand.SetProperty(PropertyFloat.WeaponModAttunement, 1.0);

            var lines = ModLines(wand);

            Assert.IsTrue(lines.Any(l => l.StartsWith("- Siphon (You + Summon) [")), string.Join(" | ", lines));
            Assert.IsTrue(lines.Any(l => l.StartsWith("- Attunement [")), "Attunement is Wielder-route: no tag");
        }

        [TestMethod]
        public void Imbue_line_names_the_focus_imbue()
        {
            var cb = SummonerFocusTests.Wand(true, 1.14);
            cb.ImbuedEffect = ImbuedEffectType.CripplingBlow;
            CollectionAssert.AreEqual(new[] { PetLine, WielderLine, ImbuePrefix + "Crippling Blow" }, SummonerFocusDisplay.GetAppraisalLines(cb).ToList());

            var fire = SummonerFocusTests.Wand(true, 1.14);
            fire.ImbuedEffect = ImbuedEffectType.FireRending;
            Assert.AreEqual(ImbuePrefix + "Fire Rending", SummonerFocusDisplay.GetAppraisalLines(fire).Last());

            var ar = SummonerFocusTests.Wand(true, 1.14);
            ar.ImbuedEffect = ImbuedEffectType.ArmorRending;
            Assert.AreEqual(ImbuePrefix + "Armor Rending", SummonerFocusDisplay.GetAppraisalLines(ar).Last());

            var defense = SummonerFocusTests.Wand(true, 1.14);
            defense.ImbuedEffect = ImbuedEffectType.MagicDefense;
            Assert.AreEqual(2, SummonerFocusDisplay.GetAppraisalLines(defense).Count(), "an imbue the pets do not read gets no line");

            // an imbue is not a weapon mod: its line stays with weapon mods off, as its transfer does
            PropertyManager.ModifyBool("weapon_mods_enabled", false);
            Assert.AreEqual(ImbuePrefix + "Crippling Blow", SummonerFocusDisplay.GetAppraisalLines(cb).Last());
        }

        [TestMethod]
        public void Transfer_percent_below_full_marks_the_matching_lines()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            wand.SetProperty(PropertyFloat.WeaponModExecution, 1.0);
            wand.SetProperty(PropertyFloat.WeaponModDefensiveWeapon, 1.0);
            wand.ImbuedEffect = ImbuedEffectType.CripplingBlow;

            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 0.5);
            CollectionAssert.AreEqual(new[]
            {
                PetLine, WielderLine,
                ImbuePrefix + "Crippling Blow",
            }, SummonerFocusDisplay.GetAppraisalLines(wand).ToList(), "only the mod tags follow the mod share; the focus lines carry no route text");

            var modLines = ModLines(wand);
            Assert.IsTrue(modLines.Any(l => l.StartsWith("- Execution (Summon 50%) [")), string.Join(" | ", modLines));
            Assert.IsTrue(modLines.Any(l => l.StartsWith("- Defensive Weapon (You + Summon 50%) [")), string.Join(" | ", modLines));

            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 1.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetImbueTransferPercentKey, 0.333);
            CollectionAssert.AreEqual(new[]
            {
                PetLine, WielderLine,
                ImbuePrefix + "Crippling Blow (at 33% strength)",
            }, SummonerFocusDisplay.GetAppraisalLines(wand).ToList(), "only the imbue line follows the imbue share, rounded");
            Assert.IsTrue(ModLines(wand).Any(l => l.StartsWith("- Execution (Summon) [")), "full mod share: no number in the tag");

            Assert.AreEqual(" (Summon 68%)", RouteTagAt(0.675, "Execution"), "whole number, half away from zero");
            Assert.AreEqual(" (Summon)", RouteTagAt(0.996, "Execution"), "rounds to 100: no number");
            Assert.AreEqual(" (You + Summon 99%)", RouteTagAt(0.994, "Defensive Weapon"), "rounds to 99");

            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 1.0);
            Assert.AreEqual(" (at 68% strength)", SummonerFocusDisplay.StrengthSuffix(0.675), "whole number, half away from zero");
            Assert.AreEqual("", SummonerFocusDisplay.StrengthSuffix(1.0));
            Assert.AreEqual("", SummonerFocusDisplay.StrengthSuffix(0.996), "rounds to 100: no suffix");
            Assert.AreEqual(" (at 99% strength)", SummonerFocusDisplay.StrengthSuffix(0.994), "rounds to 99");
            Assert.AreEqual(" (at 0% strength)", SummonerFocusDisplay.StrengthSuffix(0.0));
        }

        private static string RouteTagAt(double share, string modName)
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, share);

            return SummonerFocusDisplay.RouteTag(SummonerFocusTests.Wand(true, 1.14), WeaponModRegistry.AllMods.Single(d => d.DisplayName == modName));
        }

        [TestMethod]
        public void Attunement_raises_the_shown_pet_numbers()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            wand.SetProperty(PropertyFloat.WeaponModAttunement, 1.0);

            var attunement = WeaponModValue.MagnitudeFromFraction(WeaponModRegistry.Get(WeaponModId.Attunement), 1.0, 1.0, wand);
            Assert.IsTrue(attunement > 0.0);

            var edm = 1.14 + attunement;
            var dealt = (SummonerFocusMath.DealtMultiplier(edm, 5.0) - 1.0) * 100.0;
            var taken = (1.0 - SummonerFocusMath.TakenMultiplier(edm, 2.5, 0.25)) * 100.0;
            var expected = $"- Summoner Focus: your summons deal +{dealt:0.#}% damage and take {taken:0.#}% less damage while this is wielded";

            var lines = SummonerFocusDisplay.GetAppraisalLines(wand).ToList();
            Assert.AreEqual(expected, lines[0]);
            Assert.AreNotEqual(PetLine, lines[0]);
        }

        [TestMethod]
        public void Display_damage_mod_is_the_sum_combat_uses()
        {
            var wand = SummonerFocusTests.Wand(true, 1.14);
            wand.SetProperty(PropertyFloat.WeaponModAttunement, 1.0);

            // unwielded: no wielder
            Assert.AreEqual((double)WorldObject.SumCasterElementalDamageMod(wand, null), SummonerFocusDisplay.DisplayElementalDamageMod(wand), 1e-9);

            // wielded by a seeded owner
            var owner = SummonerFocusTests.OwnerWielding(wand);
            Assert.AreEqual((double)WorldObject.SumCasterElementalDamageMod(wand, wand.Wielder as Creature ?? owner), SummonerFocusDisplay.DisplayElementalDamageMod(wand), 1e-9);
        }

        [TestMethod]
        public void Suppress_removes_the_native_float_for_a_focus_only()
        {
            var focusDict = new Dictionary<PropertyFloat, double> { [PropertyFloat.ElementalDamageMod] = 1.14, [PropertyFloat.ManaRate] = -0.1 };
            SummonerFocusDisplay.SuppressClientDamageBonusLine(SummonerFocusTests.Wand(true), focusDict);
            Assert.IsFalse(focusDict.ContainsKey(PropertyFloat.ElementalDamageMod));
            Assert.IsTrue(focusDict.ContainsKey(PropertyFloat.ManaRate));

            var fireDict = new Dictionary<PropertyFloat, double> { [PropertyFloat.ElementalDamageMod] = 1.14 };
            SummonerFocusDisplay.SuppressClientDamageBonusLine(SummonerFocusTests.Wand(false), fireDict);
            Assert.IsTrue(fireDict.ContainsKey(PropertyFloat.ElementalDamageMod));

            SummonerFocusDisplay.SuppressClientDamageBonusLine(null, fireDict);
            SummonerFocusDisplay.SuppressClientDamageBonusLine(SummonerFocusTests.Wand(true), null);
        }

        [TestMethod]
        public void AppraiseInfo_wires_the_lines_and_the_suppression_after_the_life_caster_lines()
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Server", "Network", "Structure", "AppraiseInfo.cs"));

            var life = text.IndexOf("LifeCasterDisplay.SuppressUnlabelledClientLines(wo, PropertiesInt, PropertiesFloat);", StringComparison.Ordinal);
            var lines = text.IndexOf("propertyDetails.AddRange(SummonerFocusDisplay.GetAppraisalLines(wo));", StringComparison.Ordinal);
            var suppress = text.IndexOf("SummonerFocusDisplay.SuppressClientDamageBonusLine(wo, PropertiesFloat);", StringComparison.Ordinal);

            Assert.IsTrue(life >= 0, "anchor LifeCasterDisplay line moved");
            Assert.IsTrue(lines > life, "GetAppraisalLines call missing or before the LifeCasterDisplay lines");
            Assert.IsTrue(suppress > lines, "SuppressClientDamageBonusLine call missing or out of order");
            Assert.AreEqual(1, Count(text, "SummonerFocusDisplay.GetAppraisalLines("));
            Assert.AreEqual(1, Count(text, "SummonerFocusDisplay.SuppressClientDamageBonusLine("));
        }

        private static int Count(string text, string needle)
        {
            var n = 0;
            for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
