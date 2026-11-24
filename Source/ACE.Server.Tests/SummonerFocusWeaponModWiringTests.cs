using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan pins for a pet's Execution from its owner's Summoner focus. Each pet line sits on the line directly
    /// after the existing wielder Execution read, inside the crit branch, in the same multiplicative shape. Nothing in the
    /// project drives these crit branches live (they need a random crit roll), so the text is what catches a missing,
    /// duplicated or moved read.
    /// </summary>
    [TestClass]
    public class SummonerFocusWeaponModWiringTests
    {
        private const string DamageEventSignature = "private float DoCalculateDamage(Creature attacker, Creature defender, WorldObject damageSource)";
        private const string SpellSignature = "public float? CalculateDamage(WorldObject source, Creature target, ref bool criticalHit, ref bool critDefended, ref bool overpower)";

        private const string PetCall = "SummonerFocus.PetFocusModValue(";

        private static string DamageEventBody() =>
            PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Entity/DamageEvent.cs"), DamageEventSignature);

        private static string SpellBody() =>
            PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs"), SpellSignature);

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;

            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;

            return count;
        }

        [TestMethod]
        public void Physical_execution_pet_line_follows_the_wielder_read()
        {
            var body = DamageEventBody();

            const string pair =
                "                    CriticalDamageMod *= 1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(Weapon, WeaponModId.Execution, attacker);\n" +
                "                    CriticalDamageMod *= 1.0f + (float)SummonerFocus.PetFocusModValue(attacker, defender, WeaponModId.Execution);\n";

            Assert.AreEqual(1, CountOf(body, pair), "the pet line directly after the wielder Execution read");
            Assert.AreEqual(1, CountOf(body, PetCall), "one pet read in DoCalculateDamage");

            var crit = body.IndexOf("IsCritical = true;", StringComparison.Ordinal);
            var pet = body.IndexOf(PetCall, StringComparison.Ordinal);
            Assert.IsTrue(crit >= 0, "anchor missing: IsCritical = true;");
            Assert.IsTrue(pet >= 0, "missing: " + PetCall);
            Assert.IsTrue(crit < pet, "inside the crit branch");
        }

        [TestMethod]
        public void Spell_execution_pet_lines_follow_both_wielder_reads()
        {
            var body = SpellBody();

            const string pair =
                "                    weaponCritDamageMod *= 1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Execution, sourceCreature);\n" +
                "                    weaponCritDamageMod *= 1.0f + (float)SummonerFocus.PetFocusModValue(sourceCreature, target, WeaponModId.Execution);\n";

            Assert.AreEqual(2, CountOf(body, pair), "life and war/void: the pet line directly after each wielder Execution read");
            Assert.AreEqual(2, CountOf(body, PetCall), "two pet reads in CalculateDamage");

            // each pet read sits after an "if (criticalHit)" that precedes it
            for (var i = body.IndexOf(PetCall, StringComparison.Ordinal); i >= 0; i = body.IndexOf(PetCall, i + PetCall.Length, StringComparison.Ordinal))
            {
                var gate = body.LastIndexOf("if (criticalHit)", i, StringComparison.Ordinal);
                Assert.IsTrue(gate >= 0, "inside a crit branch");
                var critMod = body.IndexOf("GetWeaponCritDamageMod(weapon, sourceCreature, attackSkill, target);", gate, StringComparison.Ordinal);
                Assert.IsTrue(critMod >= 0, "anchor missing: GetWeaponCritDamageMod after the crit gate");
                Assert.IsTrue(critMod < i, "after the crit damage mod is read");
            }
        }

        [TestMethod]
        public void Simulator_mirrors_both_execution_terms()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/CombatSimulator/AttackerTerms.cs");

            Assert.AreEqual(1, CountOf(source,
                "                * (1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Execution, attacker))\n" +
                "                * (1.0f + (float)SummonerFocus.PetFocusModValue(attacker, defender, WeaponModId.Execution));\n"), "physical crit damage mod");
            Assert.AreEqual(1, CountOf(source,
                "                * (1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(wand, WeaponModId.Execution, caster))\n" +
                "                * (1.0f + (float)SummonerFocus.PetFocusModValue(caster, defender, WeaponModId.Execution));\n"), "magic crit damage mod");
            Assert.AreEqual(2, CountOf(source, PetCall));
        }

        [TestMethod]
        public void Wielder_strip_is_central_only()
        {
            Assert.AreEqual(1, CountOf(PooledLootSourceText.Read("Source/ACE.Server/WeaponMods/WeaponModCombat.cs"), "SummonerFocus.IsWielderStrippedMod("));
            Assert.AreEqual(0, CountOf(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_WeaponMods.cs"), "SummonerFocus."));
        }
    }
}
