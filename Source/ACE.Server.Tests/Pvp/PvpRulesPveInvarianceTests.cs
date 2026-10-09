using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// THE ISOLATION INVARIANT (Docs/Pvp/DESIGN.md "PvP rules (levers)"): with every lever at an extreme value,
    /// nothing that is not a PvP interaction changes, and nothing that is not a PvP interaction even READS a lever.
    ///
    ///  - a creature-vs-creature DamageEvent still decomposes exactly as DamageEventTests pins it (the same
    ///    fixture), at both extreme rating scales, and the C1 cap entry hands its damage back unchanged;
    ///  - an instrumented DialSource records ZERO reads, and the max-health seam zero reads, across every choke
    ///    point entry for every non-PvP pair (creature/creature, player/creature, creature/player, self, combat
    ///    pet/player, nulls);
    ///  - every PvpRules function returns its input exactly for PvpScope.None.
    ///
    /// SEEDING: DamageEvent reads server tunables, so ClassInitialize seeds every registered default through
    /// DefaultPropertyManager.LoadDefaultProperties() exactly as DamageEventTests does (defaults only - nothing to
    /// restore). The lever seams (DialSource, MaxHealthSource, Observer, the arena seam) are saved and restored
    /// per test.
    /// </summary>
    [TestClass]
    public class PvpRulesPveInvarianceTests
    {
        private const float Epsilon = 0.001f;

        private Func<PvpRuleDials> savedDialSource;
        private Func<Creature, double> savedMaxHealth;
        private Action<PvpChokePoint, double, double> savedObserver;
        private Func<Player, Player, bool> savedArenaScope;

        private int dialReads;
        private int maxHealthReads;
        private int observerCalls;

        /// <summary>Every lever at an extreme: a 1-point absolute cap, a 0.0001 fraction cap, every bool on, 10^6 windows.</summary>
        private static PvpRuleDials Extreme(double scale) => new PvpRuleDials(
            Enabled: true,
            DamageCap: 1,
            DamageCapMaxHealthFraction: 0.0001,
            DamageRatingScale: scale,
            CritDamageRatingScale: scale,
            BlockAirborneConsumables: true,
            ConsumableMinIntervalMs: 1_000_000,
            DispelVulnLockSeconds: 1_000_000,
            CloakDamageReduction: 1,
            CleaveEnabled: false,
            StripRareBuffs: true,
            WarMagicDamageMod: scale,
            MeleeDamageMod: scale,
            MissileDamageMod: scale,
            CritDamageMod: scale,
            MagicAbsorbMod: scale,
            HealingMod: scale,
            CsCritMod: scale,
            CbCritMod: scale,
            HealthFloor: 1_000_000,
            HealthCeiling: 0);

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpRuleTunables.DialSource;
            savedMaxHealth = PvpRules.MaxHealthSource;
            savedObserver = PvpRules.Observer;
            savedArenaScope = PvpClassifier.ArenaScopeSource;

            dialReads = 0;
            maxHealthReads = 0;
            observerCalls = 0;

            UseExtreme(10.0);
            PvpRules.MaxHealthSource = c => { maxHealthReads++; return 1000; };
            PvpRules.Observer = (p, b, a) => observerCalls++;
            PvpClassifier.ArenaScopeSource = (x, y) => { throw new InvalidOperationException("the arena seam must not be read for a non-PvP pair"); };
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedDialSource;
            PvpRules.MaxHealthSource = savedMaxHealth;
            PvpRules.Observer = savedObserver;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        private void UseExtreme(double scale)
        {
            var dials = Extreme(scale);
            PvpRuleTunables.DialSource = () => { dialReads++; return dials; };
        }

        private void AssertNothingRead(string why)
        {
            Assert.AreEqual(0, dialReads, $"{why}: a non-PvP interaction read the PvP levers");
            Assert.AreEqual(0, maxHealthReads, $"{why}: a non-PvP interaction read the defender's max health");
            Assert.AreEqual(0, observerCalls, $"{why}: a non-PvP interaction reported a lever");
        }

        private static T Seeded<T>() where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return wo;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        // ================= DamageEvent: creature vs creature =================

        /// <summary>
        /// DamageEventTests.UnarmedStrike_DecomposesIntoRetailComponents under extreme levers, at both extreme
        /// rating scales: every hit still decomposes into baseDamage x attributeMod x armorMod (crits: max x 2 x
        /// attributeMod x armorMod), the C1 cap entry returns the damage unchanged, and no lever is read.
        /// </summary>
        [TestMethod]
        public void CreatureVsCreature_DamageEvent_IsIdenticalToBaseline_AtExtremeLevers()
        {
            foreach (var scale in new[] { 0.0, 10.0 })
            {
                UseExtreme(scale);

                var attacker = TestCreatures.CreateAttacker(maxDamage: 10, variance: 0.5f, strength: 100);
                var defender = TestCreatures.CreateDefender(baseArmor: 200);

                var expectedAttributeMod = SkillFormula.GetAttributeMod(100);
                var expectedArmorMod = SkillFormula.CalcArmorMod(200);

                var sawCritical = false;
                var sawNonCritical = false;

                for (var i = 0; i < 500; i++)
                {
                    var damageEvent = DamageEvent.CalculateDamage(attacker, defender, null);

                    Assert.IsFalse(damageEvent.Evaded);
                    Assert.IsFalse(damageEvent.GeneralFailure);
                    Assert.AreEqual(expectedAttributeMod, damageEvent.AttributeMod, Epsilon);
                    Assert.AreEqual(expectedArmorMod, damageEvent.ArmorMod, Epsilon);

                    // no PvP debug record was written for a creature pair
                    Assert.AreEqual(0.0f, damageEvent.PvpDamageRatingScale);
                    Assert.AreEqual(0.0f, damageEvent.PvpCritDamageRatingScale);
                    Assert.IsNull(damageEvent.PvpDamageCapPoint);

                    if (damageEvent.IsCritical)
                    {
                        sawCritical = true;
                        Assert.AreEqual(10.0f * expectedAttributeMod * 2.0f * expectedArmorMod, damageEvent.Damage, Epsilon);
                    }
                    else
                    {
                        sawNonCritical = true;
                        Assert.AreEqual(damageEvent.BaseDamage * expectedAttributeMod * expectedArmorMod, damageEvent.Damage, Epsilon);
                    }

                    // the M1 damage-mod entry, fed this pair: at scale 0 or 10 every mod would change the hit if it ran
                    var afterM1 = PvpRules.ApplyDamageMods(PvpChokePoint.M1, attacker, defender, damageEvent.Damage, PvpRules.WeaponDamageKind(damageEvent.CombatType), damageEvent.IsCritical, default);
                    Assert.AreEqual(damageEvent.Damage, afterM1, "M1 changed a creature-vs-creature hit");

                    // the C1 cap entry, fed this pair: the 1-point absolute cap would bite anything if it ran
                    var afterC1 = PvpRules.ApplyDamageCap(PvpChokePoint.C1, attacker, defender, damageEvent.Damage);
                    Assert.AreEqual(damageEvent.Damage, afterC1, "C1 changed a creature-vs-creature hit");
                }

                Assert.IsTrue(sawCritical, "no critical hit in 500 attacks");
                Assert.IsTrue(sawNonCritical, "no normal hit in 500 attacks");

                AssertNothingRead($"creature vs creature at scale {scale}");
            }
        }

        // ================= zero reads for every non-PvP pair =================

        [TestMethod]
        public void NonPvpPairs_ReadNoLever_AtAnyChokePoint()
        {
            var creatureA = TestCreatures.CreateDefender();
            var creatureB = TestCreatures.CreateDefender();
            var player = Seeded<Player>();
            var otherPlayerAsSelf = player;
            var pet = Seeded<CombatPet>();

            var pairs = new List<(string Name, WorldObject Source, Creature Target)>
            {
                ("creature -> creature", creatureA, creatureB),
                ("player -> creature", player, creatureA),
                ("creature -> player", creatureA, player),
                ("player -> self", player, otherPlayerAsSelf),
                ("combat pet -> player", pet, player),
                ("player -> combat pet", player, pet),
                ("null source", null, player),
                ("null target", player, null),
            };

            foreach (var (name, source, target) in pairs)
            {
                foreach (PvpChokePoint point in Enum.GetValues(typeof(PvpChokePoint)))
                {
                    Assert.AreEqual(800.0f, PvpRules.ApplyDamageCap(point, source, target, 800.0f), $"{name} at {point} (float)");
                    Assert.AreEqual(800u, PvpRules.ApplyDamageCap(point, source, target, 800u), $"{name} at {point} (uint)");
                }

                AssertNothingRead(name);
            }
        }

        // ================= damage mods (M1, M2) and magic absorb mod (AB1) =================

        /// <summary>
        /// Every new damage lever at BOTH extremes (0 and 10): creature/creature, player/creature, creature/player,
        /// self, pet and null pairs come back unchanged at M1 and M2 for every damage kind, crit or not, and at AB1
        /// for a real absorb fraction - and nothing is read. Control_PvpPair_DamageMods_ReadTheLevers proves the same
        /// fixture sees a read when the pair is PvP.
        /// </summary>
        [TestMethod]
        public void DamageModsAndAbsorb_NonPvpPairs_AreUnchanged_AndReadNoLever()
        {
            var creatureA = TestCreatures.CreateDefender();
            var creatureB = TestCreatures.CreateDefender();
            var player = Seeded<Player>();
            var pet = Seeded<CombatPet>();

            var pairs = new List<(string Name, WorldObject Source, Creature Target)>
            {
                ("creature vs creature", creatureA, creatureB),
                ("player vs creature", player, creatureA),
                ("creature vs player", creatureA, player),
                ("player vs self", player, player),
                ("combat pet vs player", pet, player),
                ("player vs combat pet", player, pet),
                ("null source", null, player),
                ("null target", player, null),
            };

            foreach (var scale in new[] { 0.0, 10.0 })
            {
                UseExtreme(scale);

                foreach (var (name, source, target) in pairs)
                {
                    foreach (PvpDamageKind kind in Enum.GetValues(typeof(PvpDamageKind)))
                    {
                        foreach (var crit in new[] { false, true })
                        {
                            Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, source, target, 800.0f, kind, crit, default), $"{name} M1 {kind} crit={crit} scale={scale}");
                            Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M2, source, target, 800.0f, kind, crit, default), $"{name} M2 {kind} crit={crit} scale={scale}");
                        }
                    }

                    Assert.AreEqual(0.64f, PvpRules.ApplyMagicAbsorbMod(source, target, 0.64f), $"{name} AB1 scale={scale}");

                    AssertNothingRead($"{name} at scale {scale}");
                }
            }
        }

        /// <summary>Control for the test above: a real PvP pair DOES read the levers and IS scaled at M1, M2 and AB1.</summary>
        [TestMethod]
        public void Control_PvpPair_DamageMods_ReadTheLevers()
        {
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;

            // 10 (the kind mod) x 1000 / 1,000,000 (pvp_health_floor at the extreme, max health seam 1000) = 0.01
            Assert.AreEqual(8.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, Seeded<Player>(), Seeded<Player>(), 800.0f, PvpDamageKind.Melee, false, default), 1e-3f);
            Assert.AreEqual(1, dialReads);

            Assert.AreEqual(8.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M2, Seeded<Player>(), Seeded<Player>(), 800.0f, PvpDamageKind.WarMagic, false, default), 1e-3f);
            Assert.AreEqual(2, dialReads);

            Assert.AreEqual(0.0f, PvpRules.ApplyMagicAbsorbMod(Seeded<Player>(), Seeded<Player>(), 0.64f), "10 x a 0.36 reduction clamps to absorbing everything");
            Assert.AreEqual(3, dialReads);
            Assert.AreEqual(3, observerCalls);
        }

        // ================= context tuning (pvp_{arena|bg}_{category}_{stat}) =================

        /// <summary>
        /// Every context tuning key (116) at BOTH extremes (0 and 10): every non-PvP pair, and a PvP pair in the OPEN WORLD,
        /// comes back unchanged at M1, M2, CC1 and CC2 for physical and spell profiles, crit or not, and the context
        /// seam is never read. Control_ArenaPair_ContextTuning_ReadsAndScales proves the same fixture sees a read and a
        /// change for an arena pair.
        /// </summary>
        [TestMethod]
        public void ContextTuning_PveAndOpenWorld_AreUnchanged_AndReadNoContextKey()
        {
            var savedContext = PvpContextTunables.DialSource;
            var contextReads = 0;

            try
            {
                var creatureA = TestCreatures.CreateDefender();
                var creatureB = TestCreatures.CreateDefender();
                var player = Seeded<Player>();
                var pet = Seeded<CombatPet>();
                var openA = Seeded<Player>();
                var openB = Seeded<Player>();
                var weapon = Seeded<MeleeWeapon>();
                weapon.WeaponSkill = Skill.LightWeapons;
                var hollowWeepingWeapon = Seeded<MeleeWeapon>();
                hollowWeepingWeapon.WeaponSkill = Skill.LightWeapons;
                hollowWeepingWeapon.SetProperty(PropertyBool.IgnoreMagicResist, true);
                hollowWeepingWeapon.SetProperty(PropertyInt.SlayerCreatureType, (int)CreatureType.Human);
                hollowWeepingWeapon.SetProperty(PropertyFloat.SlayerDamageBonus, 1.5);

                var pairs = new List<(string Name, WorldObject Source, Creature Target)>
                {
                    ("creature vs creature", creatureA, creatureB),
                    ("player vs creature", player, creatureA),
                    ("creature vs player", creatureA, player),
                    ("player vs self", player, player),
                    ("combat pet vs player", pet, player),
                    ("null source", null, player),
                    ("null target", player, null),
                    ("open-world PK pair", openA, openB),
                };

                var profiles = new[]
                {
                    PvpHitProfile.ForWeapon(weapon, CombatType.Melee),
                    PvpHitProfile.ForWeapon(null, CombatType.Melee),
                    PvpHitProfile.ForWeapon(hollowWeepingWeapon, CombatType.Melee),
                    PvpHitProfile.ForSpell(MagicSchool.WarMagic, ProjectileSpellType.Bolt),
                    PvpHitProfile.ForSpell(MagicSchool.WarMagic, ProjectileSpellType.Wall),
                    PvpHitProfile.ForSpell(MagicSchool.WarMagic, ProjectileSpellType.Volley),
                };

                foreach (var scale in new[] { 0.0, 10.0 })
                {
                    var extreme = PvpContextTuning.Keys.Aggregate(PvpContextDials.Neutral, (d, k) => d.With(k, scale));
                    PvpContextTunables.DialSource = () => { contextReads++; return extreme; };

                    // open-world PK reaches the rule levers (the existing M1/M2 behavior), so use identity rule dials:
                    // only the CONTEXT keys are under test
                    PvpRuleTunables.DialSource = () => PvpRuleTunables.Defaults;
                    PvpClassifier.ArenaScopeSource = (x, y) => false;

                    foreach (var (name, source, target) in pairs)
                    {
                        foreach (var profile in profiles)
                        {
                            foreach (var crit in new[] { false, true })
                            {
                                Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, source, target, 800.0f, PvpDamageKind.Melee, crit, profile), $"{name} M1 crit={crit} scale={scale}");
                                Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M2, source, target, 800.0f, PvpDamageKind.WarMagic, crit, profile), $"{name} M2 crit={crit} scale={scale}");
                            }

                            Assert.AreEqual(0.25f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC1, source, target, profile, 0.25f), $"{name} CC1 scale={scale}");
                            Assert.AreEqual(0.25f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC2, source, target, profile, 0.25f), $"{name} CC2 scale={scale}");
                        }

                        Assert.AreEqual(0.64f, PvpRules.ApplyMagicAbsorbMod(source, target, 0.64f), $"{name} AB1 (context absorb keys) scale={scale}");
                        Assert.AreEqual(0, contextReads, $"{name} at scale {scale} read a context key");
                    }
                }
            }
            finally
            {
                PvpContextTunables.DialSource = savedContext;
            }
        }

        [TestMethod]
        public void Control_ArenaPair_ContextTuning_ReadsAndScales()
        {
            var savedContext = PvpContextTunables.DialSource;
            var contextReads = 0;

            try
            {
                var extreme = PvpContextTuning.Keys.Aggregate(PvpContextDials.Neutral, (d, k) => d.With(k, 10.0));
                PvpContextTunables.DialSource = () => { contextReads++; return extreme; };
                PvpRuleTunables.DialSource = () => PvpRuleTunables.Defaults;
                PvpClassifier.ArenaScopeSource = (x, y) => true;

                var a = Seeded<Player>();
                var d2 = Seeded<Player>();
                var weapon = Seeded<MeleeWeapon>();
                weapon.WeaponSkill = Skill.LightWeapons;
                var profile = PvpHitProfile.ForWeapon(weapon, CombatType.Melee);

                Assert.AreEqual(8000.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, a, d2, 800.0f, PvpDamageKind.Melee, false, profile), 1e-2f, "10 x light_dmg");
                Assert.AreEqual(1, contextReads);
                Assert.AreEqual(1.0f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC1, a, d2, profile, 0.25f), 1e-6f, "0.25 x 10 clamps to 1");
                Assert.AreEqual(2, contextReads);

                var hollow = Seeded<MeleeWeapon>();
                hollow.WeaponSkill = Skill.LightWeapons;
                hollow.SetProperty(PropertyBool.IgnoreMagicResist, true);

                // 10 (light) x 10 (hollow); every kind mod is the identity here
                Assert.AreEqual(80000.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, a, d2, 800.0f, PvpDamageKind.Melee, false, PvpHitProfile.ForWeapon(hollow, CombatType.Melee)), 1e-1f, "10 x light_dmg x hollow_dmg");
                Assert.AreEqual(3, contextReads);

                // 0.64 absorb: global mod is 1.0 here, context x10 on a 0.36 reduction clamps to absorbing everything
                Assert.AreEqual(0.0f, PvpRules.ApplyMagicAbsorbMod(a, d2, 0.64f), "10 x magic_absorb");
                Assert.AreEqual(4, contextReads);
            }
            finally
            {
                PvpContextTunables.DialSource = savedContext;
            }
        }

        // ================= healing mod (HL1-HL5) =================

        private static readonly PvpChokePoint[] HealPoints = { PvpChokePoint.HL1, PvpChokePoint.HL2, PvpChokePoint.HL3, PvpChokePoint.HL4, PvpChokePoint.HL5, PvpChokePoint.HL5F };

        /// <summary>
        /// pvp_healing_mod at BOTH extremes (0 and 10): a heal received by a player who is NOT in a Live arena match - an NPK
        /// (IsPKType short-circuits before pk_timer), a PK whose PK timer is not running, or null - is unchanged at
        /// every healing site for every numeric type, and nothing is read. The engagement gate, not a (source,
        /// target) pair, is what isolates PvE here.
        /// </summary>
        [TestMethod]
        public void HealingMod_Unbound_IsUnchanged_AndReadsNoLever()
        {
            var npk = Seeded<Player>();
            npk.PlayerKillerStatus = PlayerKillerStatus.NPK;

            var idlePk = Seeded<Player>();
            idlePk.PlayerKillerStatus = PlayerKillerStatus.PK;
            idlePk.LastPkAttackTimestamp = 0;   // PK timer long expired

            var targets = new List<(string Name, Player Target)> { ("NPK", npk), ("PK, timer not running", idlePk), ("null", null) };

            foreach (var scale in new[] { 0.0, 10.0 })
            {
                UseExtreme(scale);

                foreach (var (name, target) in targets)
                {
                    foreach (var point in HealPoints)
                    {
                        Assert.AreEqual(500.0, PvpRules.ApplyHealingMod(point, target, 500.0), $"{name} {point} double scale={scale}");
                        Assert.AreEqual(123.4f, PvpRules.ApplyHealingMod(point, target, 123.4f), $"{name} {point} float scale={scale}");
                        Assert.AreEqual(500, PvpRules.ApplyHealingMod(point, target, 500), $"{name} {point} int scale={scale}");
                        Assert.AreEqual(500u, PvpRules.ApplyHealingMod(point, target, 500u), $"{name} {point} uint scale={scale}");
                        Assert.AreEqual(500u, PvpRules.ApplyHealingModToRecipient(point, target, 500u), $"{name} {point} recipient scale={scale}");
                    }

                    AssertNothingRead($"healing mod, {name}, scale {scale}");
                }
            }
        }

        /// <summary>
        /// THE AUDIT SCENARIO (owner ruling 2026-09-27, the healing lever is ARENA ONLY): a PK whose PK timer IS
        /// running - which the old, since-deleted engagement gate counted as PvP - is healed (a kit, HL2, and
        /// every other healing site) while fighting a creature. No scaling, and ZERO setting reads, at every site.
        /// </summary>
        [TestMethod]
        public void PkTimerOnly_HealingMod_IsUnchanged_AndReadsNoLever()
        {
            var pk = Seeded<Player>();
            pk.PlayerKillerStatus = PlayerKillerStatus.PK;
            pk.LastPkAttackTimestamp = ACE.Common.Time.GetUnixTime();

            Assert.IsTrue(pk.PKTimerActive, "precondition: the PK timer is running (the old, leaky gate counted this as engaged)");
            Assert.IsNull(pk.PvpBinding, "precondition: no arena binding");

            foreach (var scale in new[] { 0.0, 10.0 })
            {
                UseExtreme(scale);

                foreach (var point in HealPoints)
                {
                    Assert.AreEqual(500u, PvpRules.ApplyHealingMod(point, pk, 500u), $"{point} scale={scale}: a PK-timer-only heal must not be scaled");
                    Assert.AreEqual(500u, PvpRules.ApplyHealingModToRecipient(point, pk, 500u), $"{point} recipient scale={scale}");
                    Assert.AreEqual(123.4f, PvpRules.ApplyHealingMod(point, pk, 123.4f), $"{point} float scale={scale}");
                }

                AssertNothingRead($"PK-timer-only heal at scale {scale}");
            }
        }

        /// <summary>Positive control for the two tests above: a player bound to a LIVE arena match does read the lever and IS scaled.</summary>
        [TestMethod]
        public void Control_ArenaBoundPlayer_HealingMod_ReadsTheLever()
        {
            var arena = Seeded<Player>();
            arena.PlayerKillerStatus = PlayerKillerStatus.NPK;
            arena.SetPvpBindingForTests(new ACE.Server.Pvp.PvpPlayerBinding(LiveMatch(), 0, ACE.Server.Pvp.PvpMatchState.Live, false, false, false, false, false));

            Assert.AreEqual(5000u, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, arena, 500u));
            Assert.AreEqual(1, dialReads);
            Assert.AreEqual(1, observerCalls);
        }

        private static ACE.Server.Pvp.PvpMatch LiveMatch()
        {
            var teams = new List<ACE.Server.Pvp.PvpTeam>
            {
                new ACE.Server.Pvp.PvpTeam(0, new List<ACE.Server.Pvp.PvpParticipant> { new ACE.Server.Pvp.PvpParticipant(1, 1500) }),
                new ACE.Server.Pvp.PvpTeam(1, new List<ACE.Server.Pvp.PvpParticipant> { new ACE.Server.Pvp.PvpParticipant(2, 1500) })
            };

            return new ACE.Server.Pvp.PvpMatch(Guid.NewGuid(), "arena_1v1", teams, DateTime.UtcNow);
        }

        // ================= crit imbue levers (CS1, CS2, CB1) and health normalization (N3, N4) =================

        /// <summary>
        /// pvp_cs_crit_mod / pvp_cb_crit_mod at 0 and 10, and pvp_health_floor at 1,000,000: every non-PvP pair - the
        /// crit statics' null-target callers included - is unchanged at CS1, CS2, CB1, N3 and N4, and nothing (dials,
        /// max health, observer) is read.
        /// </summary>
        [TestMethod]
        public void CritImbuesAndHealthNormalization_NonPvpPairs_AreUnchanged_AndReadNoLever()
        {
            var creatureA = TestCreatures.CreateDefender();
            var creatureB = TestCreatures.CreateDefender();
            var player = Seeded<Player>();
            var pet = Seeded<CombatPet>();

            var pairs = new List<(string Name, Creature Source, Creature Target)>
            {
                ("creature vs creature", creatureA, creatureB),
                ("player vs creature", player, creatureA),
                ("creature vs player", creatureA, player),
                ("player vs self", player, player),
                ("combat pet vs player", pet, player),
                ("null target", player, null),
                ("null wielder", null, player),
            };

            foreach (var scale in new[] { 0.0, 10.0 })
            {
                UseExtreme(scale);

                foreach (var (name, source, target) in pairs)
                {
                    Assert.AreEqual(0.3f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS1, source, target, 0.1f, 0.3f), $"{name} CS1 scale={scale}");
                    Assert.AreEqual(0.3f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS2, source, target, 0.05f, 0.3f), $"{name} CS2 scale={scale}");
                    Assert.AreEqual(4.0f, PvpRules.ApplyCripplingBlowMod(source, target, 4.0f), $"{name} CB1 scale={scale}");
                    Assert.AreEqual(800.0f, PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, source, target, 800.0f), $"{name} N3 scale={scale}");
                    Assert.AreEqual(800u, PvpRules.ApplyHealthNormalization(PvpChokePoint.N4, source, target, 800u), $"{name} N4 scale={scale}");

                    AssertNothingRead($"crit imbues / health normalization, {name}, scale {scale}");
                }
            }
        }

        /// <summary>Control for the test above: a real PvP pair DOES read the levers and IS changed at CS1, CB1 and N3.</summary>
        [TestMethod]
        public void Control_PvpPair_CritImbuesAndHealthNormalization_ReadTheLevers()
        {
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
            UseExtreme(0.0);

            Assert.AreEqual(0.1f, PvpRules.ApplyCriticalStrikeMod(PvpChokePoint.CS1, Seeded<Player>(), Seeded<Player>(), 0.1f, 0.3f), 1e-6f, "m = 0: the imbue adds nothing");
            Assert.AreEqual(1.0f, PvpRules.ApplyCripplingBlowMod(Seeded<Player>(), Seeded<Player>(), 4.0f), 1e-6f, "m = 0: the imbue adds nothing");
            Assert.AreEqual(0.8f, PvpRules.ApplyHealthNormalization(PvpChokePoint.N3, Seeded<Player>(), Seeded<Player>(), 800.0f), 1e-4f, "1000 / 1,000,000");
            Assert.AreEqual(3, dialReads);
            Assert.AreEqual(1, maxHealthReads);
            Assert.AreEqual(3, observerCalls);
        }

        // ================= every function is the identity for Scope None =================

        [TestMethod]
        public void EveryPvpRulesFunction_ReturnsItsInputExactly_ForScopeNone()
        {
            var dials = Extreme(10.0);
            var hits = new[] { 0.0f, 1.0f, 0.5f, 800.0f, 123456.789f, -5.0f, float.MaxValue, float.NaN };

            foreach (PvpChokePoint point in Enum.GetValues(typeof(PvpChokePoint)))
            {
                foreach (var hit in hits)
                {
                    var result = PvpRules.ApplyDamageCap(point, PvpInteraction.None, dials, hit);

                    // bitwise equality, so NaN in means the SAME NaN out
                    Assert.AreEqual(BitConverter.SingleToInt32Bits(hit), BitConverter.SingleToInt32Bits(result), $"{point}: {hit} changed for Scope None");
                }

                Assert.AreEqual(800.0f, PvpRules.ApplyDamageCap(point, default(PvpInteraction), dials, 800.0f), $"{point}: default(PvpInteraction) is None");
            }

            foreach (PvpDamageKind kind in Enum.GetValues(typeof(PvpDamageKind)))
            {
                Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, PvpInteraction.None, dials, 800.0f, kind, true), $"M1 {kind}: changed for Scope None");
                Assert.AreEqual(800.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M2, default(PvpInteraction), dials, 800.0f, kind, true), $"M2 {kind}: changed for default(PvpInteraction)");
            }

            AssertNothingRead("Scope None");
        }

        /// <summary>A control that the fixture can see a read at all: a real PvP pair DOES read the levers once and is capped.</summary>
        [TestMethod]
        public void Control_PvpPair_ReadsTheLevers()
        {
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;

            var result = PvpRules.ApplyDamageCap(PvpChokePoint.C1, Seeded<Player>(), Seeded<Player>(), 800.0f);

            Assert.AreEqual(1, dialReads, "a PvP pair reads the dials exactly once");
            Assert.AreEqual(1, maxHealthReads);
            Assert.AreEqual(0.0f, result, "floor(0.0001 x 1000 max health) = 0, below the 1-point absolute cap");
            Assert.AreEqual(1, observerCalls);
        }

        // ================= new lever: pvp_cloak_damage_reduction (CLK1) =================

        [TestMethod]
        public void CloakDamageReduction_NonPvpPairs_ReadNoLever()
        {
            var creatureA = TestCreatures.CreateDefender();
            var creatureB = TestCreatures.CreateDefender();
            var player = Seeded<Player>();

            // Outside classified PvP, the fork's OLD hardcoded behavior is reproduced exactly, with the
            // setting never read: a Player source still halves 200 to 100 (self-cast Harm - "self" below -
            // and a player hitting a cloaked monster - "player -> creature" below); any other source keeps
            // the un-halved 200.
            Assert.AreEqual(200, PvpRules.ApplyCloakDamageReduction(creatureA, creatureB, 200), "creature -> creature");
            Assert.AreEqual(100, PvpRules.ApplyCloakDamageReduction(player, creatureA, 200), "player -> creature (cloaked monster)");
            Assert.AreEqual(200, PvpRules.ApplyCloakDamageReduction(creatureA, player, 200), "creature -> player");
            Assert.AreEqual(100, PvpRules.ApplyCloakDamageReduction(player, player, 200), "self (self-cast Harm)");
            Assert.AreEqual(200, PvpRules.ApplyCloakDamageReduction(null, player, 200), "null source");
            Assert.AreEqual(100, PvpRules.ApplyCloakDamageReduction(player, null, 200), "null defender, Player source");

            AssertNothingRead("cloak damage reduction: non-PvP pairs");
        }

        /// <summary>DISCRIMINATES: at the extreme lever (1), a real PvP pair returns 1, not the caller's base amount.</summary>
        [TestMethod]
        public void CloakDamageReduction_PvpPair_ReturnsLeverValue()
        {
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;

            var result = PvpRules.ApplyCloakDamageReduction(Seeded<Player>(), Seeded<Player>(), 200);

            Assert.AreEqual(1, result, "the extreme lever value (1) must be applied, not the caller's 200");
            Assert.AreEqual(1, dialReads);
            Assert.AreEqual(1, observerCalls);
        }

        // ================= new lever: pvp_cleave_enabled (CLV1) =================

        [TestMethod]
        public void CleaveGate_NonPvpCandidates_ReadNoLever()
        {
            var player = Seeded<Player>();
            var creature = TestCreatures.CreateDefender();

            Assert.IsFalse(PvpRules.ShouldSkipCleaveTarget(player, creature), "a non-player candidate is never gated");
            Assert.IsFalse(PvpRules.ShouldSkipCleaveTarget(player, player), "self is never gated");
            Assert.IsFalse(PvpRules.ShouldSkipCleaveTarget(null, player), "null attacker never gated (not classified as PvP)");

            AssertNothingRead("cleave gate: non-PvP candidates");
        }

        /// <summary>DISCRIMINATES: pvp_cleave_enabled=false skips a real player candidate for a non-admin attacker.</summary>
        [TestMethod]
        public void CleaveGate_PvpCandidate_SkippedWhenLeverDisabled()
        {
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;

            var attacker = Seeded<Player>();
            var candidate = Seeded<Player>();

            Assert.IsTrue(PvpRules.ShouldSkipCleaveTarget(attacker, candidate), "pvp_cleave_enabled=false must skip a player candidate");
            Assert.AreEqual(1, dialReads);
            Assert.AreEqual(1, observerCalls);
        }

        /// <summary>DISCRIMINATES: Doctide's admin exemption - an admin attacker's cleave is never gated, even against the extreme lever.</summary>
        [TestMethod]
        public void CleaveGate_AdminAttacker_NeverGated()
        {
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;

            var attacker = Seeded<Player>();
            attacker.IsAdmin = true;
            var candidate = Seeded<Player>();

            Assert.IsFalse(PvpRules.ShouldSkipCleaveTarget(attacker, candidate), "an admin attacker's cleave must never be gated");
        }

        // ================= new lever: pvp_strip_rare_buffs (RB1) =================

        /// <summary>
        /// DISCRIMINATES: PvpRules.ShouldStripRareBuffs reads pvp_strip_rare_buffs and returns its value
        /// directly - false at the registered default (which the fixture's Extreme() overrides to true for the
        /// other tests, so this test seeds its own dials rather than relying on ClassInitialize's defaults).
        /// </summary>
        [TestMethod]
        public void StripRareBuffs_ReturnsLeverValue()
        {
            var dials = new PvpRuleDials(true, 0, 0, 1, 1, false, 0, 0, 100, true, false, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0);
            PvpRuleTunables.DialSource = () => { dialReads++; return dials; };

            Assert.IsFalse(PvpRules.ShouldStripRareBuffs(), "pvp_strip_rare_buffs=false must not strip");
            Assert.AreEqual(1, dialReads);
            Assert.AreEqual(0, observerCalls, "no strip means no report");

            UseExtreme(10.0); // StripRareBuffs: true

            Assert.IsTrue(PvpRules.ShouldStripRareBuffs(), "pvp_strip_rare_buffs=true must strip");
            Assert.AreEqual(1, observerCalls);
        }

        /// <summary>
        /// The master switch off means no lever fires, even with pvp_strip_rare_buffs true - same isolation rule
        /// as every other lever in this file.
        /// </summary>
        [TestMethod]
        public void StripRareBuffs_MasterSwitchOff_NeverStrips()
        {
            var dials = new PvpRuleDials(false, 1, 0.0001, 10, 10, true, 1_000_000, 1_000_000, 1, false, true, 10, 10, 10, 10, 10, 10, 10, 10, 1_000_000, 0);
            PvpRuleTunables.DialSource = () => { dialReads++; return dials; };

            Assert.IsFalse(PvpRules.ShouldStripRareBuffs());
            Assert.AreEqual(0, observerCalls);
        }

        /// <summary>
        /// DISCRIMINATES the code-review fix for RB1: a rare-gem buff re-applied BETWEEN two PvP hits still gets
        /// stripped on the second hit. With the old one-shot "inactive-to-active transition" gate this fails,
        /// because the second hit's transition check (!PKTimerActive) is false once the first hit already
        /// started the timer - it would see the reapplied buff and do nothing, exactly the bug the review found.
        ///
        /// Exercises Player.HasAnyStrippableRareGemBuff (the no-allocation pre-check) and
        /// Player.StripRareGemBuffs directly, bypassing DispelPkRares'/UpdatePKTimer's PK-status plumbing, since
        /// that needs a fully-constructed Player this reflection-seeded fixture cannot provide (see
        /// RareGemSpellCategoryTests' note: "Constructing a Player in this test host fails in its static
        /// initializer"). RareGemSpells' DB-backed candidate cache is seeded directly by reflection so this runs
        /// with no world database. EnchantmentManager.Dispel removes the entry from the registry BEFORE it tries
        /// to notify a (here, null) Session, so the expected NullReferenceException from that network tail is
        /// caught and the registry mutation - the actual strip - is asserted on either side of it.
        /// </summary>
        [TestMethod]
        public void StripRareBuffs_ReappliedBetweenHits_StripsAgainOnSecondHit()
        {
            const uint rareSpellId = 90210;

            var cacheField = typeof(RareGemSpells).GetField("strippableSpellIds", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(cacheField, "RareGemSpells.strippableSpellIds was not found by reflection - has it been renamed?");
            var savedCache = cacheField.GetValue(null);

            try
            {
                cacheField.SetValue(null, new HashSet<uint> { rareSpellId });

                var player = Seeded<Player>();

                var equippedField = typeof(Creature).GetField("<EquippedObjects>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(equippedField, "Creature.EquippedObjects's backing field was not found by reflection - has it been renamed?");
                equippedField.SetValue(player, new Dictionary<ACE.Entity.ObjectGuid, WorldObject>());

                // WorldObject.EnchantmentManager is a plain field assigned in WorldObject's constructor
                // (skipped entirely by RuntimeHelpers.GetUninitializedObject), so it is null on a Seeded
                // instance unless set here.
                player.EnchantmentManager = new EnchantmentManagerWithCaching(player);

                player.Biota.PropertiesEnchantmentRegistry = new List<ACE.Entity.Models.PropertiesEnchantmentRegistry>();

                void AddRareBuff() => player.Biota.PropertiesEnchantmentRegistry.Add(new ACE.Entity.Models.PropertiesEnchantmentRegistry
                {
                    SpellId = (int)rareSpellId,
                    CasterObjectId = 0,
                    Duration = 300,
                });

                bool HasRareBuffInRegistry() => player.Biota.PropertiesEnchantmentRegistry.Any(e => e.SpellId == (int)rareSpellId);

                void StripIgnoringNetworkSendFailure()
                {
                    try
                    {
                        player.StripRareGemBuffs();
                    }
                    catch (NullReferenceException)
                    {
                        // Expected: EnchantmentManager.Dispel already removed the entry before trying to
                        // notify a Session this fixture has none of.
                    }
                }

                // First hit: a rare buff is present and gets stripped.
                AddRareBuff();
                Assert.IsTrue(HasRareBuffInRegistry(), "setup: the first buff must be present before stripping");
                Assert.IsTrue(player.HasAnyStrippableRareGemBuff(), "the pre-check must see the first buff");

                StripIgnoringNetworkSendFailure();

                Assert.IsFalse(HasRareBuffInRegistry(), "the first buff must be stripped");
                Assert.IsFalse(player.HasAnyStrippableRareGemBuff(), "the pre-check must see nothing left after the first strip");

                // Reapplied between hits: a second hit's strip must still catch it - this is the fix.
                AddRareBuff();
                Assert.IsTrue(HasRareBuffInRegistry(), "setup: the reapplied buff must be present before the second strip");
                Assert.IsTrue(player.HasAnyStrippableRareGemBuff(), "the pre-check must see the reapplied buff");

                StripIgnoringNetworkSendFailure();

                Assert.IsFalse(HasRareBuffInRegistry(), "the reapplied buff must ALSO be stripped on the second hit - the old one-shot gate would have left it");
                Assert.IsFalse(player.HasAnyStrippableRareGemBuff());
            }
            finally
            {
                cacheField.SetValue(null, savedCache);
            }
        }
    }
}
