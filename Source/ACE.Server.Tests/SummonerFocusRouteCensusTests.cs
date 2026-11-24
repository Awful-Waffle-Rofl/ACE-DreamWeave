using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ROUTE CENSUS for the Summoner caster focus. Pins, as text, exactly which ACE.Server files call each
    /// SummonerFocus / SummonerFocusDisplay / SummonerFocusMath member and how many times, so an edit that adds,
    /// drops or duplicates a damage-route hook (or a stray call anywhere, CombatPet.cs included) fails loudly.
    /// The helper files themselves (Entity/SummonerFocus.cs, SummonerFocusMath.cs, SummonerFocusDisplay.cs) are not
    /// counted: their calls are the implementation, not routes into it.
    ///
    /// Comments are dropped before counting so a comment that merely MENTIONS a call cannot satisfy or trip a pin.
    /// A scan matches TEXT; an alias or using-static call would slip past it, and the guarded failure is a textual
    /// add/drop/duplicate of one of these lines.
    /// </summary>
    [TestClass]
    public class SummonerFocusRouteCensusTests
    {
        private static readonly string[] ExcludedHelperFiles =
        {
            "Entity/SummonerFocus.cs",
            "Entity/SummonerFocusMath.cs",
            "Entity/SummonerFocusDisplay.cs",
        };

        private static readonly Regex CallPattern = new Regex(@"\b(SummonerFocus(?:Display|Math)?\.\w+)\(", RegexOptions.Compiled);

        private static string ServerRoot() =>
            Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server");

        private static string StripComments(string text) =>
            Regex.Replace(text, @"/\*.*?\*/|//[^\r\n]*", string.Empty, RegexOptions.Singleline);

        private static string Read(string relative)
        {
            var path = Path.Combine(ServerRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"{relative} does not exist under Source/ACE.Server.");
            return StripComments(File.ReadAllText(path));
        }

        /// <summary>Every counted call, keyed "Member" then "relative/file.cs:count".</summary>
        private static Dictionary<string, List<string>> Census()
        {
            var root = ServerRoot();
            var excluded = new HashSet<string>(ExcludedHelperFiles, StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, Dictionary<string, int>>();

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (relative.StartsWith("bin/") || relative.StartsWith("obj/") || excluded.Contains(relative))
                    continue;

                foreach (Match m in CallPattern.Matches(StripComments(File.ReadAllText(file))))
                {
                    var member = m.Groups[1].Value;

                    if (!result.TryGetValue(member, out var perFile))
                        result[member] = perFile = new Dictionary<string, int>();

                    perFile[relative] = perFile.TryGetValue(relative, out var n) ? n + 1 : 1;
                }
            }

            return result.ToDictionary(kv => kv.Key, kv => kv.Value.Select(f => $"{f.Key}:{f.Value}").ToList());
        }

        private static List<string> Sites(string member)
        {
            var census = Census();
            return census.TryGetValue(member, out var sites) ? sites : new List<string>();
        }

        private static void AssertSites(string member, string[] expected)
        {
            var sites = Sites(member);
            CollectionAssert.AreEquivalent(expected, sites,
                $"{member} sites under Source/ACE.Server; actual: {string.Join(", ", sites)}");
        }

        // ------------------------------------------------------------ the four damage sinks

        [TestMethod]
        public void HitMod_IsCalledOnlyFromTheFourDamageSinks()
        {
            AssertSites("SummonerFocus.HitMod", new[] { "WorldObjects/Monster_Combat.cs:1", "WorldObjects/SpellProjectile.cs:1", "WorldObjects/WorldObject_Magic.cs:2" });
        }

        /// <summary>
        /// The DoT sizing at cast, the projectile's record of the cut it took (for the Acid burn's sizing), and the
        /// player-victim physical sink, which HitMod's Creature.TakeDamage read never reaches.
        /// </summary>
        [TestMethod]
        public void WielderDamageMod_IsCalledOnlyFromTheDotSizing_TheProjectileRecord_AndThePlayerSink()
        {
            AssertSites("SummonerFocus.WielderDamageMod", new[] { "WorldObjects/WorldObject_Magic.cs:1", "WorldObjects/SpellProjectile.cs:1", "WorldObjects/Player_Combat.cs:1" });
        }

        [TestMethod]
        public void Sinks_CallHitModOnly_NeverThePetHalvesDirectly()
        {
            Assert.AreEqual(0, Sites("SummonerFocus.PetDealtMod").Count + Sites("SummonerFocus.PetTakenMod").Count,
                "sinks call HitMod only");
        }

        [TestMethod]
        public void CombatPet_NeverCallsSummonerFocus()
        {
            Assert.AreEqual(0, Regex.Matches(Read("WorldObjects/CombatPet.cs"), @"SummonerFocus").Count,
                "taken is applied once, in the base sink; never snapshotted in ApplySummonStats");
        }

        // ------------------------------------------------------------ weapon mods feed the pets

        [TestMethod]
        public void PetFocusModValue_IsCalledFromTheCritAndExecutionSites()
        {
            AssertSites("SummonerFocus.PetFocusModValue", new[]
                {
                    "WorldObjects/WorldObject_Weapon.cs:4",
                    "Entity/DamageEvent.cs:1",
                    "WorldObjects/SpellProjectile.cs:2",
                    "CombatSimulator/AttackerTerms.cs:2",
                });
        }

        /// <summary>
        /// The imbue transfer (Critical Strike, Crippling Blow, the rends, Armor Rending): every pet read goes through
        /// PetFocusHasImbue, and the imbue share is read beside it, so the two always appear together.
        /// </summary>
        [TestMethod]
        public void PetFocusHasImbue_IsCalledOnlyFromTheImbueHelpers()
        {
            AssertSites("SummonerFocus.PetFocusHasImbue", new[] { "WorldObjects/WorldObject_Weapon.cs:4" });
            AssertSites("SummonerFocus.PetImbueTransferPercent", new[] { "WorldObjects/WorldObject_Weapon.cs:4" });
            AssertSites("SummonerFocusMath.ScaleImbueChance", new[] { "WorldObjects/WorldObject_Weapon.cs:1" });
            AssertSites("SummonerFocusMath.ScaleImbueMultiplier", new[] { "WorldObjects/WorldObject_Weapon.cs:2" });
            AssertSites("SummonerFocusMath.ScaleArmorRending", new[] { "WorldObjects/WorldObject_Weapon.cs:1" });
        }

        /// <summary>The pet's Defensive Weapon: both GetShieldMod branches and the simulator's shield row.</summary>
        [TestMethod]
        public void PetDefensiveWeaponLevel_IsCalledFromBothShieldBranchesAndTheSimulatorRow()
        {
            AssertSites("SummonerFocus.PetDefensiveWeaponLevel", new[] { "WorldObjects/Creature_Combat.cs:2", "CombatSimulator/MonsterDefenderProfile.cs:1" });
        }

        /// <summary>The utility trio on the pets: the kill half from the death path, Siphon from the two pet damage sinks.</summary>
        [TestMethod]
        public void PetUtility_IsCalledFromTheDeathPathAndTheTwoPetSinks()
        {
            AssertSites("SummonerFocus.EnqueuePetKillUtility", new[] { "WorldObjects/Creature_Death.cs:1" });
            AssertSites("SummonerFocus.TryPetSiphon", new[] { "WorldObjects/Monster_Combat.cs:1", "WorldObjects/SpellProjectile.cs:1" });
            Assert.AreEqual(0, Sites("SummonerFocus.ApplyPetKillUtility").Count, "the kill half runs only through the queue");
        }

        [TestMethod]
        public void RatingGetters_CarryTheTierAPetBonusAndTheWielderStrip()
        {
            AssertSites("SummonerFocus.PetFocusRatingBonus", new[] { "WorldObjects/Creature_Rating.cs:3" });
            AssertSites("SummonerFocus.WielderFocusRatingStrip", new[] { "WorldObjects/Creature_Rating.cs:3" });
        }

        [TestMethod]
        public void WielderStrip_LivesOnlyInTheCentralReader()
        {
            AssertSites("SummonerFocus.IsWielderStrippedMod", new[] { "WeaponMods/WeaponModCombat.cs:1" });
            Assert.AreEqual(0, Regex.Matches(Read("WorldObjects/Player_WeaponMods.cs"), @"SummonerFocus\.").Count,
                "the strip is central only, never at an individual Player_WeaponMods site");
        }

        [TestMethod]
        public void ReadFocusModForPets_IsDefinedInWeaponModCombat_AndCalledOnlyFromSummonerFocus()
        {
            var root = ServerRoot();
            var hits = new List<string>();

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (relative.StartsWith("bin/") || relative.StartsWith("obj/"))
                    continue;

                var n = Regex.Matches(StripComments(File.ReadAllText(file)), @"\bReadFocusModForPets\b").Count;

                if (n > 0)
                    hits.Add($"{relative}:{n}");
            }

            CollectionAssert.AreEquivalent(new[] { "Entity/SummonerFocus.cs:1", "WeaponMods/WeaponModCombat.cs:1" }, hits,
                $"ReadFocusModForPets sites under Source/ACE.Server; actual: {string.Join(", ", hits)}");
        }

        // ------------------------------------------------------------ the rest of the final surface

        /// <summary>
        /// Every OTHER counted call, so a new member call from outside the helpers cannot appear unseen: the
        /// marker check at the wielder cut, the loot funding and the Prismatic Drift Stone refusal, and the
        /// appraisal display seam.
        /// </summary>
        [TestMethod]
        public void RemainingSurface_IsExactlyTheMarkerChecksAndTheDisplaySeam()
        {
            var routes = new HashSet<string>
            {
                "SummonerFocus.HitMod", "SummonerFocus.WielderDamageMod", "SummonerFocus.PetFocusModValue",
                "SummonerFocus.PetFocusRatingBonus", "SummonerFocus.WielderFocusRatingStrip", "SummonerFocus.IsWielderStrippedMod",
                "SummonerFocus.PetFocusHasImbue", "SummonerFocus.PetImbueTransferPercent", "SummonerFocus.PetDefensiveWeaponLevel",
                "SummonerFocus.EnqueuePetKillUtility", "SummonerFocus.TryPetSiphon",
                "SummonerFocusMath.ScaleImbueChance", "SummonerFocusMath.ScaleImbueMultiplier", "SummonerFocusMath.ScaleArmorRending",
            };

            var rest = Census().Where(kv => !routes.Contains(kv.Key))
                .SelectMany(kv => kv.Value.Select(site => $"{kv.Key} {site}")).ToArray();

            CollectionAssert.AreEquivalent(new[]
            {
                "SummonerFocus.IsSummonerFocus Entity/PrismaticDriftStone.cs:2",
                "SummonerFocus.IsSummonerFocus Factories/LootGenerationFactory_Caster.cs:2",
                "SummonerFocus.IsSummonerFocus Factories/LootGenerationFactory_Spells.cs:1",
                "SummonerFocus.IsSummonerFocus Factories/Tables/CasterSlotSpells.cs:1",
                "SummonerFocus.IsSummonerFocus WorldObjects/WorldObject_Weapon.cs:1",
                // the Elemental Proficiency Acid burn sizing: the bolt's cut divided back out, because each tick is cut again at Creature.TakeDamage
                "SummonerFocusMath.BeforeWielderCut WorldObjects/Player_ElementalProficiency.cs:1",
                // the summon-duel harness (SummonDuelBuilder): the pet multipliers from the same math and live scales combat uses, and the mod filter for --summoner-mods
                "SummonerFocus.ReachesPets CombatSimulator/SummonDuelBuilder.cs:1",
                "SummonerFocusMath.DealtMultiplier CombatSimulator/SummonDuelBuilder.cs:1",
                "SummonerFocusMath.TakenMultiplier CombatSimulator/SummonDuelBuilder.cs:1",
                "SummonerFocus.PetDamageScale CombatSimulator/SummonDuelBuilder.cs:1",
                "SummonerFocus.PetDefenseScale CombatSimulator/SummonDuelBuilder.cs:1",
                "SummonerFocus.PetDefenseFloor CombatSimulator/SummonDuelBuilder.cs:1",
                "SummonerFocusDisplay.GetAppraisalLines Network/Structure/AppraiseInfo.cs:1",
                "SummonerFocusDisplay.SuppressClientDamageBonusLine Network/Structure/AppraiseInfo.cs:1",
            }, rest, $"remaining-surface sites under Source/ACE.Server; actual: {string.Join(", ", rest)}");
        }
    }
}
