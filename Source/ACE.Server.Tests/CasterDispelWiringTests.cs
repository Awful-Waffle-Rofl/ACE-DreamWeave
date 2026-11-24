using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The seams of caster-pet dispels that PetCasterBrainTests' pure tables cannot see: what the snapshot readers
    /// feed the brain. Removing the resolver argument from a reader leaves every brain table green and the feature
    /// dead, so these pin the wiring:
    ///   - EnchantmentDispelFactsCache.ResolveWith's dispellable bit (Duration -1, CreatureDispelExempt);
    ///   - Creature.BuildVulnerabilityProfile(target, true) on a real Creature registry carries dispel facts, and
    ///     (target, false) does not;
    ///   - CombatPet.BuildDispelLayers applies the PvP dispel vulnerability lock the way SelectDispel does;
    ///   - a source scan that both Read*TopLayers go through BuildDispelLayers, which passes the resolver.
    ///
    /// No dat is needed: the spell lookup behind EnchantmentDispelFactsCache.Resolve is a seam
    /// (SpellInfoLookup), swapped for a fake and restored in a finally. No PropertyManager key is read; the PvP
    /// dials come from a swapped PvpRuleTunables.DialSource, also restored.
    /// </summary>
    [TestClass]
    public class CasterDispelWiringTests
    {
        private const EnchantmentTypeFlags VulnType = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative;

        private const int BeneficialSpell = 7001;
        private const int HarmfulSpell = 7002;

        private const uint PlayerCasterGuid = 0x50000002;
        private const uint MonsterCasterGuid = 0x80000001;
        private const uint TargetGuid = 0x50000003;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static (bool Beneficial, MagicSchool School)? FakeLookup(int spellId) =>
            spellId == BeneficialSpell ? (true, MagicSchool.CreatureEnchantment)
            : spellId == HarmfulSpell || spellId == 8001 || spellId == 8002 || spellId == 8003 ? (false, MagicSchool.CreatureEnchantment)
            : ((bool, MagicSchool)?)null;

        private static PropertiesEnchantmentRegistry Entry(int spellId, SpellCategory category, uint power = 300, double duration = 60.0, uint casterObjectId = 0) =>
            new PropertiesEnchantmentRegistry
            {
                SpellId = spellId,
                SpellCategory = category,
                LayerId = 1,
                PowerLevel = power,
                StartTime = 0,
                Duration = duration,
                StatModType = VulnType,
                StatModKey = 0,
                StatModValue = 1.5f,
                EnchantmentCategory = (uint)SpellType.Enchantment,
                CasterObjectId = casterObjectId,
            };

        // ---- Resolve's dispellable bit ------------------------------------------------------------------------

        [TestMethod]
        public void ResolveWith_DispellableBit_TracksDurationAndTheCreatureExemptList()
        {
            (bool, MagicSchool)? Lookup(int _) => (false, MagicSchool.CreatureEnchantment);

            var ordinary = EnchantmentDispelFactsCache.ResolveWith(Entry(HarmfulSpell, SpellCategory.StrengthLowering), i => Lookup(i));
            Assert.IsTrue(ordinary.Value.Dispellable, "an ordinary timed enchantment");
            Assert.IsFalse(ordinary.Value.IsBeneficial);
            Assert.AreEqual(MagicSchool.CreatureEnchantment, ordinary.Value.School);

            var permanent = EnchantmentDispelFactsCache.ResolveWith(Entry(HarmfulSpell, SpellCategory.StrengthLowering, duration: -1), i => Lookup(i));
            Assert.IsFalse(permanent.Value.Dispellable, "Duration -1 (item and vitae entries) is never dispelled");

            var exempt = EnchantmentDispelFactsCache.ResolveWith(Entry((int)SpellId.AsheronsBenediction, SpellCategory.StrengthRaising), i => Lookup(i));
            Assert.IsFalse(exempt.Value.Dispellable, "a CreatureDispelExempt buff is out of a pet's dispel's reach");

            Assert.IsNull(EnchantmentDispelFactsCache.ResolveWith(Entry(HarmfulSpell, SpellCategory.StrengthLowering), _ => null), "an unknown spell resolves to nothing");
        }

        // ---- BuildVulnerabilityProfile(target, withDispelFacts) on a real Creature ------------------------------

        [TestMethod]
        public void BuildVulnerabilityProfile_WithDispelFacts_CarriesThemOnARealRegistry()
        {
            var saved = EnchantmentDispelFactsCache.SpellInfoLookup;
            EnchantmentDispelFactsCache.SpellInfoLookup = FakeLookup;

            try
            {
                var target = TestCreatures.CreateDefender();
                var rwLock = target.BiotaDatabaseLock;

                target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(Entry(BeneficialSpell, SpellCategory.StrengthRaising), rwLock);
                target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(Entry(HarmfulSpell, SpellCategory.StrengthLowering), rwLock);
                target.EnchantmentManager.InvalidateCaches();

                var withFacts = Creature.BuildVulnerabilityProfile(target, true);

                Assert.IsTrue(PetCasterBrain.CarriesDispellable(withFacts.TopLayers, true, MagicSchool.CreatureEnchantment), "the beneficial layer is strippable");
                Assert.IsTrue(PetCasterBrain.CarriesDispellable(withFacts.TopLayers, false, MagicSchool.CreatureEnchantment), "the harmful layer is cleansable");
                Assert.IsFalse(PetCasterBrain.CarriesDispellable(withFacts.TopLayers, true, MagicSchool.LifeMagic), "another school");
                Assert.AreEqual(true, withFacts.TopLayers[SpellCategory.StrengthRaising].IsBeneficial);
                Assert.AreEqual(false, withFacts.TopLayers[SpellCategory.StrengthLowering].IsBeneficial);

                var without = Creature.BuildVulnerabilityProfile(target);

                Assert.IsFalse(without.TopLayers[SpellCategory.StrengthRaising].IsBeneficial.HasValue, "the monster spell roll pays for no spell lookups");
                Assert.IsFalse(PetCasterBrain.CarriesDispellable(without.TopLayers, true, MagicSchool.None));
            }
            finally
            {
                EnchantmentDispelFactsCache.SpellInfoLookup = saved;
            }
        }

        [TestMethod]
        public void BuildDispelLayers_OnARealCreature_ResolvesThroughTheCache()
        {
            var saved = EnchantmentDispelFactsCache.SpellInfoLookup;
            EnchantmentDispelFactsCache.SpellInfoLookup = FakeLookup;

            try
            {
                var pet = TestCreatures.CreateDefender();

                Assert.IsNull(CombatPet.BuildDispelLayers(pet), "no enchantments, no snapshot");

                pet.Biota.PropertiesEnchantmentRegistry.AddEnchantment(Entry(HarmfulSpell, SpellCategory.StrengthLowering), pet.BiotaDatabaseLock);
                pet.EnchantmentManager.InvalidateCaches();

                var layers = CombatPet.BuildDispelLayers(pet);

                Assert.IsTrue(PetCasterBrain.CarriesDispellable(layers, false, MagicSchool.CreatureEnchantment));
            }
            finally
            {
                EnchantmentDispelFactsCache.SpellInfoLookup = saved;
            }
        }

        // ---- the PvP dispel vulnerability lock, mirrored in the owner snapshot ---------------------------------

        private static Player SeededPlayer(double lastPkAttackTimestamp)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "<Guid>k__BackingField", new ObjectGuid(TargetGuid));

            player.LastPkAttackTimestamp = lastPkAttackTimestamp;

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        /// <summary>Runs <paramref name="body"/> with the lock dial on (300 s) or off, restoring DialSource and Observer after.</summary>
        private static void WithLockDials(bool enabled, Action body)
        {
            var savedDials = PvpRuleTunables.DialSource;
            var savedObserver = PvpRules.Observer;
            var savedLookup = EnchantmentDispelFactsCache.SpellInfoLookup;

            var observed = 0;
            PvpRules.Observer = (p, b, a) => observed++;
            PvpRuleTunables.DialSource = () => PvpRuleTunables.Defaults with { Enabled = enabled, DispelVulnLockSeconds = 300 };
            EnchantmentDispelFactsCache.SpellInfoLookup = FakeLookup;

            try
            {
                body();

                Assert.AreEqual(0, observed, "a pet's look at the snapshot never reports a lever application");
            }
            finally
            {
                PvpRuleTunables.DialSource = savedDials;
                PvpRules.Observer = savedObserver;
                EnchantmentDispelFactsCache.SpellInfoLookup = savedLookup;
            }
        }

        // Acid Vulnerability and Slash Vulnerability are in PvpRules.VulnerabilityCategories; Strength Lowering is not
        private static List<PropertiesEnchantmentRegistry> Registry(uint vulnCaster) => new List<PropertiesEnchantmentRegistry>
        {
            Entry(8001, SpellCategory.AcidVulnerability, casterObjectId: vulnCaster),
            Entry(8002, SpellCategory.StrengthLowering, casterObjectId: PlayerCasterGuid),
        };

        [TestMethod]
        public void BuildDispelLayers_PvpLockActive_AnotherPlayersVulnerabilityIsNotDispellable()
        {
            WithLockDials(true, () =>
            {
                var owner = SeededPlayer(Time.GetUnixTime());

                var layers = CombatPet.BuildDispelLayers(owner, Registry(PlayerCasterGuid));

                Assert.IsFalse(layers[SpellCategory.AcidVulnerability].Dispellable, "locked: the engine would not remove it");
                Assert.IsTrue(layers[SpellCategory.AcidVulnerability].IsBeneficial == false, "its facts are still resolved");
                Assert.IsTrue(layers[SpellCategory.StrengthLowering].Dispellable, "a non-vulnerability debuff stays dispellable");

                // only the locked layer is excluded: a cleanse still sees the other one
                Assert.IsTrue(PetCasterBrain.CarriesDispellable(layers, false, MagicSchool.CreatureEnchantment));

                var onlyLocked = CombatPet.BuildDispelLayers(owner, new List<PropertiesEnchantmentRegistry> { Entry(8001, SpellCategory.AcidVulnerability, casterObjectId: PlayerCasterGuid) });
                Assert.IsFalse(PetCasterBrain.CarriesDispellable(onlyLocked, false, MagicSchool.None), "nothing the engine would remove: no cleanse");
            });
        }

        [TestMethod]
        public void BuildDispelLayers_PvpLock_DoesNotApplyToSelfCastMonsterCastOutsideTheWindowOrSwitchedOff()
        {
            WithLockDials(true, () =>
            {
                var owner = SeededPlayer(Time.GetUnixTime());

                Assert.IsTrue(CombatPet.BuildDispelLayers(owner, Registry(TargetGuid))[SpellCategory.AcidVulnerability].Dispellable, "the owner's own vulnerability");
                Assert.IsTrue(CombatPet.BuildDispelLayers(owner, Registry(MonsterCasterGuid))[SpellCategory.AcidVulnerability].Dispellable, "a monster-cast vulnerability");

                var rested = SeededPlayer(Time.GetUnixTime() - 10000);
                Assert.IsTrue(CombatPet.BuildDispelLayers(rested, Registry(PlayerCasterGuid))[SpellCategory.AcidVulnerability].Dispellable, "outside the lock window");

                var neverPk = SeededPlayer(0);
                Assert.IsTrue(CombatPet.BuildDispelLayers(neverPk, Registry(PlayerCasterGuid))[SpellCategory.AcidVulnerability].Dispellable, "no PK attack on record");
            });

            WithLockDials(false, () =>
            {
                var owner = SeededPlayer(Time.GetUnixTime());

                Assert.IsTrue(CombatPet.BuildDispelLayers(owner, Registry(PlayerCasterGuid))[SpellCategory.AcidVulnerability].Dispellable, "master switch off");
            });
        }

        [TestMethod]
        public void BuildDispelLayers_PvpLock_NeverAppliesToANonPlayerSubject()
        {
            WithLockDials(true, () =>
            {
                var pet = TestCreatures.CreateDefender();

                Assert.IsTrue(CombatPet.BuildDispelLayers(pet, Registry(PlayerCasterGuid))[SpellCategory.AcidVulnerability].Dispellable);
            });
        }

        // ---- source-scan pin: the readers go through the resolver ----------------------------------------------

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.sln")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.sln by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }

        private static string ReadSource(string relative) =>
            File.ReadAllText(Path.Combine(FindSourceRoot(), relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

        /// <summary>The text of the method whose declaration line contains <paramref name="signature"/>, up to its closing brace at 8-space indent.</summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"`{signature}` was not found - renamed?");

            var end = source.IndexOf("\n        }\n", at, StringComparison.Ordinal);
            Assert.IsTrue(end > at, $"the end of `{signature}` was not found");

            return source.Substring(at, end - at);
        }

        [TestMethod]
        public void SourcePin_BothSnapshotReadersPassTheResolver()
        {
            var caster = ReadSource("ACE.Server/WorldObjects/CombatPet_Caster.cs");

            // each reader builds its layers through BuildDispelLayers, never a bare BuildTopLayers (no facts: a dead feature)
            StringAssert.Contains(MethodBody(caster, "ReadOwnerTopLayers()\n"), "BuildDispelLayers(owner)");
            StringAssert.Contains(MethodBody(caster, "ReadPetTopLayers()\n"), "BuildDispelLayers(this)");

            // and the registry overload hands the resolver to BuildTopLayers on BOTH of its paths (lock inactive, lock active)
            var helper = MethodBody(caster, "BuildDispelLayers(WorldObject subject, List<PropertiesEnchantmentRegistry> registry)");

            Assert.AreEqual(1, Regex.Matches(helper, @"BuildTopLayers\(registry, EnchantmentDispelFactsCache\.Resolve\)").Count, "the unlocked path");
            Assert.AreEqual(1, Regex.Matches(helper, @"BuildTopLayers\(registry, e =>[\s\S]*?EnchantmentDispelFactsCache\.Resolve\(e\)").Count, "the locked path");

            // the enemy snapshot asks for the facts exactly when a strip is bound
            StringAssert.Contains(caster, "BuildVulnerabilityProfile(casterEnemies[i], casterHasStrip)");

            // and the profile builder honours the flag
            var magic = ReadSource("ACE.Server/WorldObjects/Monster_Magic.cs");
            StringAssert.Contains(MethodBody(magic, "internal static VulnerabilityProfile BuildVulnerabilityProfile(Creature target, bool withDispelFacts = false)"),
                "BuildTopLayers(registry, EnchantmentDispelFactsCache.Resolve)");
        }

        [TestMethod]
        public void SourcePin_FriendlyCastStillWanted_DropsAnUnneededCleanse()
        {
            var caster = ReadSource("ACE.Server/WorldObjects/CombatPet_Caster.cs");

            var body = MethodBody(caster, "internal bool FriendlyCastStillWanted(Spell spell, WorldObject target)");

            // the cleanse block is there, for both kinds
            StringAssert.Contains(body, "kind == CasterSpellKind.Cleanse || kind == CasterSpellKind.CleanseSelf");
            StringAssert.Contains(body, "var forOwner = kind == CasterSpellKind.Cleanse;");

            // the owner guard: the target must be the owner and readable (CanFollowOwner); the self variant must target the pet
            StringAssert.Contains(body, "forOwner ? !ReferenceEquals(target, P_PetOwner) || !CanFollowOwner(P_PetOwner) : !ReferenceEquals(target, this)");

            // the re-check itself, reading the owner's snapshot for Cleanse and the pet's for CleanseSelf, with the spell's own school and window
            StringAssert.Contains(body, "PetCasterBrain.CleanseStillNeeded(forOwner ? ReadOwnerTopLayers() : ReadPetTopLayers(), spell.DispelSchool, spell.MinPower, spell.MaxPower)");
        }    }
}
