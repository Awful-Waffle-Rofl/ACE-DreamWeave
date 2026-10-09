using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Per-context, per-weapon-category PvP damage and crit tunables (PvpContextTuning). The pure functions take their
    /// values as arguments; the choke-point entries are driven through swapped seams (PvpRuleTunables.DialSource,
    /// PvpContextTunables.DialSource, PvpClassifier.ArenaScopeSource default, PvpContextTuning.HighestMeleeSource), so
    /// no PropertyManager key is read. Every test that claims a number changes it: a key is set to 2.0 and the
    /// matching hit must double while every other kind, the other context and open-world stay put.
    /// </summary>
    [TestClass]
    public class PvpContextTuningTests
    {
        private const float Hit = 800.0f;

        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private Func<PvpRuleDials> savedRuleDials;
        private Func<PvpContextDials> savedContextDials;
        private Func<Player, Player, bool> savedArenaScope;
        private Func<Player, Skill> savedHighestMelee;
        private Action<PvpChokePoint, double, double> savedObserver;
        private Func<string, double> savedPropertyReader;
        private Func<long> savedEpochSource;

        private int contextReads;

        [TestInitialize]
        public void Setup()
        {
            savedRuleDials = PvpRuleTunables.DialSource;
            savedContextDials = PvpContextTunables.DialSource;
            savedArenaScope = PvpClassifier.ArenaScopeSource;
            savedHighestMelee = PvpContextTuning.HighestMeleeSource;
            savedObserver = PvpRules.Observer;
            savedPropertyReader = PvpContextTunables.PropertyReader;
            savedEpochSource = PvpContextTunables.EpochSource;

            contextReads = 0;
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
            PvpRuleTunables.DialSource = () => PvpRuleTunables.Defaults;
            PvpContextTunables.DialSource = () => { contextReads++; return PvpContextDials.Neutral; };
            PvpContextTuning.HighestMeleeSource = p => Skill.HeavyWeapons;
            PvpRules.Observer = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedRuleDials;
            PvpContextTunables.DialSource = savedContextDials;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
            PvpContextTuning.HighestMeleeSource = savedHighestMelee;
            PvpRules.Observer = savedObserver;
            PvpContextTunables.PropertyReader = savedPropertyReader;
            PvpContextTunables.EpochSource = savedEpochSource;
            PvpContextTunables.Invalidate();
        }

        // ================= fixtures =================

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

        private static Player SeededPlayer()
        {
            var player = Seeded<Player>();
            player.PlayerKillerStatus = PlayerKillerStatus.PK;
            return player;
        }

        private static PvpMatch NewMatch(string modeKey)
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), modeKey, teams, Now);
        }

        /// <summary>One attacker/defender pair per scope: a Live arena match, a Live battleground match, and unbound (open world).</summary>
        private sealed class Pairs
        {
            public (Player A, Player D) Arena = Bound(ArenaMapCatalog.OneVOneKey);
            public (Player A, Player D) Battleground = Bound(BattlegroundModes.KothModeKey);
            public (Player A, Player D) OpenWorld = (SeededPlayer(), SeededPlayer());

            private static (Player, Player) Bound(string modeKey)
            {
                var match = NewMatch(modeKey);
                var a = SeededPlayer();
                var d = SeededPlayer();
                a.SetPvpBindingForTests(new PvpPlayerBinding(match, 0, PvpMatchState.Live, false, false, false, false, false));
                d.SetPvpBindingForTests(new PvpPlayerBinding(match, 1, PvpMatchState.Live, false, false, false, false, false));
                return (a, d);
            }

            public (Player A, Player D) For(PvpContext ctx) => ctx == PvpContext.Arena ? Arena : Battleground;
        }

        /// <summary>One kind of hit: a weapon category, or a war projectile shape.</summary>
        private sealed record Kind(string Name, PvpCombatCategory Category, PvpWarShape Shape, ProjectileSpellType Spell);

        private static readonly Kind[] AllKinds =
        {
            new Kind("light", PvpCombatCategory.Light, PvpWarShape.None, ProjectileSpellType.Undef),
            new Kind("heavy", PvpCombatCategory.Heavy, PvpWarShape.None, ProjectileSpellType.Undef),
            new Kind("finesse", PvpCombatCategory.Finesse, PvpWarShape.None, ProjectileSpellType.Undef),
            new Kind("twohanded", PvpCombatCategory.TwoHanded, PvpWarShape.None, ProjectileSpellType.Undef),
            new Kind("bow", PvpCombatCategory.Bow, PvpWarShape.None, ProjectileSpellType.Undef),
            new Kind("crossbow", PvpCombatCategory.Crossbow, PvpWarShape.None, ProjectileSpellType.Undef),
            new Kind("thrown", PvpCombatCategory.Thrown, PvpWarShape.None, ProjectileSpellType.Undef),
            new Kind("war bolt", PvpCombatCategory.War, PvpWarShape.Bolt, ProjectileSpellType.Bolt),
            new Kind("war arc", PvpCombatCategory.War, PvpWarShape.Arc, ProjectileSpellType.Arc),
            new Kind("war streak", PvpCombatCategory.War, PvpWarShape.Streak, ProjectileSpellType.Streak),
            new Kind("war blast", PvpCombatCategory.War, PvpWarShape.Blast, ProjectileSpellType.Blast),
            new Kind("war ring", PvpCombatCategory.War, PvpWarShape.Ring, ProjectileSpellType.Ring),
            new Kind("war walls", PvpCombatCategory.War, PvpWarShape.Wall, ProjectileSpellType.Wall),
            // no shape damage key, but still the war crit category
            new Kind("war volley", PvpCombatCategory.War, PvpWarShape.None, ProjectileSpellType.Volley),
            new Kind("war strike", PvpCombatCategory.War, PvpWarShape.None, ProjectileSpellType.Strike),
            new Kind("war undef", PvpCombatCategory.War, PvpWarShape.None, ProjectileSpellType.Undef),
        };

        private static WorldObject WeaponFixture(Skill skill, WeaponType type)
        {
            var weapon = Seeded<MeleeWeapon>();
            weapon.WeaponSkill = skill;
            weapon.W_WeaponType = type;
            return weapon;
        }

        private static PvpHitProfile ProfileOf(Kind kind)
        {
            switch (kind.Category)
            {
                case PvpCombatCategory.Light: return PvpHitProfile.ForWeapon(WeaponFixture(Skill.LightWeapons, WeaponType.Sword), CombatType.Melee);
                case PvpCombatCategory.Heavy: return PvpHitProfile.ForWeapon(WeaponFixture(Skill.HeavyWeapons, WeaponType.Axe), CombatType.Melee);
                case PvpCombatCategory.Finesse: return PvpHitProfile.ForWeapon(WeaponFixture(Skill.FinesseWeapons, WeaponType.Dagger), CombatType.Melee);
                case PvpCombatCategory.TwoHanded: return PvpHitProfile.ForWeapon(WeaponFixture(Skill.TwoHandedCombat, WeaponType.TwoHanded), CombatType.Melee);
                case PvpCombatCategory.Bow: return PvpHitProfile.ForWeapon(WeaponFixture(Skill.MissileWeapons, WeaponType.Bow), CombatType.Missile);
                case PvpCombatCategory.Crossbow: return PvpHitProfile.ForWeapon(WeaponFixture(Skill.MissileWeapons, WeaponType.Crossbow), CombatType.Missile);
                case PvpCombatCategory.Thrown: return PvpHitProfile.ForWeapon(WeaponFixture(Skill.MissileWeapons, WeaponType.Thrown), CombatType.Missile);
                default: return PvpHitProfile.ForSpell(MagicSchool.WarMagic, kind.Spell);
            }
        }

        private static PvpDamageKind DamageKindOf(Kind kind)
            => kind.Category == PvpCombatCategory.War ? PvpDamageKind.WarMagic
             : kind.Category == PvpCombatCategory.Bow || kind.Category == PvpCombatCategory.Crossbow || kind.Category == PvpCombatCategory.Thrown ? PvpDamageKind.Missile
             : PvpDamageKind.Melee;

        private void UseContext(PvpContextDials dials) => PvpContextTunables.DialSource = () => { contextReads++; return dials; };

        private static void UseRules(PvpRuleDials dials) => PvpRuleTunables.DialSource = () => dials;

        private static float Damage(Pairs pairs, PvpScope scope, Kind kind, bool crit, float hit = Hit)
        {
            var (a, d) = ScopePair(pairs, scope);
            return PvpRules.ApplyDamageMods(kind.Category == PvpCombatCategory.War ? PvpChokePoint.M2 : PvpChokePoint.M1, a, d, hit, DamageKindOf(kind), crit, ProfileOf(kind));
        }

        private static float Chance(Pairs pairs, PvpScope scope, Kind kind, float chance)
        {
            var (a, d) = ScopePair(pairs, scope);
            return PvpContextTuning.ApplyCritChance(kind.Category == PvpCombatCategory.War ? PvpChokePoint.CC2 : PvpChokePoint.CC1, a, d, ProfileOf(kind), chance);
        }

        private static (Player A, Player D) ScopePair(Pairs pairs, PvpScope scope)
            => scope == PvpScope.Arena ? pairs.Arena : scope == PvpScope.Battleground ? pairs.Battleground : pairs.OpenWorld;

        private static readonly PvpScope[] Scopes = { PvpScope.Arena, PvpScope.Battleground, PvpScope.OpenWorld };

        private static PvpScope ScopeOf(PvpContext ctx) => ctx == PvpContext.Arena ? PvpScope.Arena : PvpScope.Battleground;

        // ================= the generated key list =================

        /// <summary>The nineteen pvp_arena_* / pvp_bg_* doubles that are NOT context tuning keys.</summary>
        private static readonly string[] PreExistingNonContextDoubles =
        {
            "pvp_arena_dmg_mod_1v1",
            "pvp_arena_dmg_mod_ffa",
            "pvp_arena_ffa_ring_dmg",
            "pvp_arena_healkit_restoration_cap_1v1",
            "pvp_arena_overtime_healing_mod",
            "pvp_arena_overtime_damage_ramp_per_minute",
            "pvp_bg_koth_zone_radius",
            "pvp_bg_koth_zone_height",
            "pvp_bg_koth_marker_spacing",
            "pvp_bg_koth_marker_z_offset",
            "pvp_bg_dmg_mod",
            "pvp_bg_ad_kill_chip_pct",
            "pvp_bg_ad_kill_heal_pct",
            "pvp_bg_ad_kill_range",
            "pvp_bg_ad_defender_dr_per",
            "pvp_bg_ad_defender_dr_cap",
            "pvp_bg_ad_defender_dr_radius",
            "pvp_bg_ad_defender_dr_window_s",
            "pvp_bg_marks_scale",
        };

        [TestMethod]
        public void GeneratedKeys_Are116_AndEqualTheRegisteredContextDoubles()
        {
            // 2 contexts x (13 damage + 8 crit chance + 8 crit damage + 14 variant damage + 14 variant crit damage + 1 magic absorb)
            Assert.AreEqual(116, PvpContextTuning.Keys.Count);
            Assert.AreEqual(116, PvpContextTuning.Keys.Distinct().Count(), "keys must be unique");

            var registered = DefaultPropertyManager.DefaultDoubleProperties.Keys
                .Where(k => k.StartsWith("pvp_arena_", StringComparison.Ordinal) || k.StartsWith("pvp_bg_", StringComparison.Ordinal))
                .Except(PreExistingNonContextDoubles)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(PvpContextTuning.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(), registered,
                "the generated list and the registered pvp_(arena|bg)_* doubles (less the nineteen non-context ones) must be the same set");

            foreach (var key in PvpContextTuning.Keys)
            {
                Assert.AreEqual(1.0, DefaultPropertyManager.DefaultDoubleProperties[key].Item, $"{key} default");
                Assert.IsFalse(string.IsNullOrWhiteSpace(DefaultPropertyManager.DefaultDoubleProperties[key].Description), $"{key} description");
            }

            // there is no per-context war damage key: war damage is per shape only
            Assert.IsFalse(PvpContextTuning.Keys.Contains("pvp_arena_war_dmg"));
            Assert.IsFalse(PvpContextTuning.Keys.Contains("pvp_bg_war_dmg"));
            // the per-context magic absorb keys exist (owner reversed the original "no magic absorb keys" ruling)
            CollectionAssert.AreEquivalent(new[] { "pvp_arena_magic_absorb", "pvp_bg_magic_absorb" }, PvpContextTuning.Keys.Where(k => k.Contains("absorb")).ToArray());
        }

        [TestMethod]
        public void KeyNames_FollowTheScheme()
        {
            Assert.AreEqual("pvp_arena_light_dmg", PvpContextTuning.DamageKey(PvpContext.Arena, PvpCombatCategory.Light, PvpWarShape.None));
            Assert.AreEqual("pvp_arena_war_walls_dmg", PvpContextTuning.DamageKey(PvpContext.Arena, PvpCombatCategory.War, PvpWarShape.Wall));
            Assert.AreEqual("pvp_bg_war_walls_dmg", PvpContextTuning.DamageKey(PvpContext.Battleground, PvpCombatCategory.War, PvpWarShape.Wall));
            Assert.AreEqual("pvp_arena_heavy_hollow_dmg", PvpContextTuning.VariantDamageKey(PvpContext.Arena, PvpCombatCategory.Heavy, PvpWeaponVariant.Hollow));
            Assert.AreEqual("pvp_bg_bow_weeping_dmg", PvpContextTuning.VariantDamageKey(PvpContext.Battleground, PvpCombatCategory.Bow, PvpWeaponVariant.Weeping));
            Assert.AreEqual("pvp_arena_thrown_crit_dmg_hollow_dmg", PvpContextTuning.VariantCritDamageKey(PvpContext.Arena, PvpCombatCategory.Thrown, PvpWeaponVariant.Hollow));
            Assert.AreEqual("pvp_bg_twohanded_crit_dmg_weeping_dmg", PvpContextTuning.VariantCritDamageKey(PvpContext.Battleground, PvpCombatCategory.TwoHanded, PvpWeaponVariant.Weeping));
            Assert.IsNull(PvpContextTuning.VariantDamageKey(PvpContext.Arena, PvpCombatCategory.War, PvpWeaponVariant.Hollow), "no war variant keys");
            Assert.AreEqual("pvp_arena_magic_absorb", PvpContextTuning.MagicAbsorbKey(PvpContext.Arena));
            Assert.AreEqual("pvp_bg_magic_absorb", PvpContextTuning.MagicAbsorbKey(PvpContext.Battleground));
            Assert.AreEqual("pvp_bg_war_ring_dmg", PvpContextTuning.DamageKey(PvpContext.Battleground, PvpCombatCategory.War, PvpWarShape.Ring));
            Assert.AreEqual("pvp_bg_twohanded_crit_chance", PvpContextTuning.CritChanceKey(PvpContext.Battleground, PvpCombatCategory.TwoHanded));
            Assert.AreEqual("pvp_arena_war_crit_dmg", PvpContextTuning.CritDamageKey(PvpContext.Arena, PvpCombatCategory.War));
            Assert.IsNull(PvpContextTuning.DamageKey(PvpContext.Arena, PvpCombatCategory.War, PvpWarShape.None), "war has no un-shaped damage key");
            Assert.IsNull(PvpContextTuning.DamageKey(PvpContext.Arena, PvpCombatCategory.None, PvpWarShape.None));
        }

        // ================= pure: categorization =================

        [TestMethod]
        public void WeaponCategory_ByMoASkill_AndMissileWeaponType()
        {
            Assert.AreEqual(PvpCombatCategory.Light, PvpContextTuning.WeaponCategory(true, Skill.LightWeapons, WeaponType.Sword, false, Skill.None));
            Assert.AreEqual(PvpCombatCategory.Heavy, PvpContextTuning.WeaponCategory(true, Skill.HeavyWeapons, WeaponType.Axe, false, Skill.None));
            Assert.AreEqual(PvpCombatCategory.Finesse, PvpContextTuning.WeaponCategory(true, Skill.FinesseWeapons, WeaponType.Dagger, false, Skill.None));
            Assert.AreEqual(PvpCombatCategory.TwoHanded, PvpContextTuning.WeaponCategory(true, Skill.TwoHandedCombat, WeaponType.TwoHanded, false, Skill.None));
            Assert.AreEqual(PvpCombatCategory.Bow, PvpContextTuning.WeaponCategory(true, Skill.MissileWeapons, WeaponType.Bow, true, Skill.None));
            Assert.AreEqual(PvpCombatCategory.Crossbow, PvpContextTuning.WeaponCategory(true, Skill.MissileWeapons, WeaponType.Crossbow, true, Skill.None));
            Assert.AreEqual(PvpCombatCategory.Thrown, PvpContextTuning.WeaponCategory(true, Skill.MissileWeapons, WeaponType.Thrown, true, Skill.None), "atlatls and darts both carry Thrown");
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.WeaponCategory(true, Skill.MissileWeapons, WeaponType.Undef, true, Skill.None), "no usable weapon type");
        }

        [TestMethod]
        public void WeaponCategory_UnarmedMelee_IsTheHighestMeleeSkillsCategory_AndUnarmedMissileHasNone()
        {
            Assert.AreEqual(PvpCombatCategory.Light, PvpContextTuning.WeaponCategory(false, Skill.None, WeaponType.Undef, false, Skill.LightWeapons));
            Assert.AreEqual(PvpCombatCategory.Heavy, PvpContextTuning.WeaponCategory(false, Skill.None, WeaponType.Undef, false, Skill.HeavyWeapons));
            Assert.AreEqual(PvpCombatCategory.Finesse, PvpContextTuning.WeaponCategory(false, Skill.None, WeaponType.Undef, false, Skill.FinesseWeapons));
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.WeaponCategory(false, Skill.None, WeaponType.Undef, true, Skill.HeavyWeapons));
        }

        [TestMethod]
        public void WeaponCategory_NeverCrossesTheMeleeMissileLine_AndIgnoresMagicSkills()
        {
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.WeaponCategory(true, Skill.LightWeapons, WeaponType.Sword, true, Skill.None), "a melee-skill weapon on a missile hit");
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.WeaponCategory(true, Skill.MissileWeapons, WeaponType.Bow, false, Skill.None), "a bow on a melee hit");
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.WeaponCategory(true, Skill.WarMagic, WeaponType.Magic, false, Skill.None), "a wand's war skill is not a physical category");
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.WeaponCategory(true, Skill.None, WeaponType.Undef, false, Skill.HeavyWeapons), "an armed hit never falls back to the unarmed skill");
        }

        [TestMethod]
        public void SpellCategoryAndShape_WarOnly_FiveShapes()
        {
            Assert.AreEqual(PvpCombatCategory.War, PvpContextTuning.SpellCategory(MagicSchool.WarMagic));
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.SpellCategory(MagicSchool.VoidMagic));
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.SpellCategory(MagicSchool.LifeMagic));
            Assert.AreEqual(PvpCombatCategory.None, PvpContextTuning.SpellCategory(MagicSchool.CreatureEnchantment));

            Assert.AreEqual(PvpWarShape.Bolt, PvpContextTuning.SpellShape(ProjectileSpellType.Bolt));
            Assert.AreEqual(PvpWarShape.Arc, PvpContextTuning.SpellShape(ProjectileSpellType.Arc));
            Assert.AreEqual(PvpWarShape.Streak, PvpContextTuning.SpellShape(ProjectileSpellType.Streak));
            Assert.AreEqual(PvpWarShape.Blast, PvpContextTuning.SpellShape(ProjectileSpellType.Blast));
            Assert.AreEqual(PvpWarShape.Ring, PvpContextTuning.SpellShape(ProjectileSpellType.Ring));
            Assert.AreEqual(PvpWarShape.None, PvpContextTuning.SpellShape(ProjectileSpellType.Volley));
            Assert.AreEqual(PvpWarShape.Wall, PvpContextTuning.SpellShape(ProjectileSpellType.Wall));
            Assert.AreEqual(PvpWarShape.None, PvpContextTuning.SpellShape(ProjectileSpellType.Strike));
            Assert.AreEqual(PvpWarShape.None, PvpContextTuning.SpellShape(ProjectileSpellType.Undef));
        }

        [TestMethod]
        public void ContextOf_OnlyMatchScopes()
        {
            Assert.AreEqual(PvpContext.Arena, PvpContextTuning.ContextOf(PvpScope.Arena));
            Assert.AreEqual(PvpContext.Battleground, PvpContextTuning.ContextOf(PvpScope.Battleground));
            Assert.IsNull(PvpContextTuning.ContextOf(PvpScope.OpenWorld));
            Assert.IsNull(PvpContextTuning.ContextOf(PvpScope.None));
        }

        // ================= pure: math =================

        [TestMethod]
        public void ScaleCritChance_ScalesAndClamps()
        {
            Assert.AreEqual(0.5, PvpContextTuning.ScaleCritChance(0.25, 2.0), 1e-12, "the chance doubles");
            Assert.AreEqual(1.0, PvpContextTuning.ScaleCritChance(0.75, 2.0), 1e-12, "clamped at 1");
            Assert.AreEqual(0.0, PvpContextTuning.ScaleCritChance(0.75, 0.0), 1e-12, "0 removes crits");
            Assert.AreEqual(0.125, PvpContextTuning.ScaleCritChance(0.25, 0.5), 1e-12);
            Assert.AreEqual(0.3, PvpContextTuning.ScaleCritChance(0.3, 1.0), "1.0 is the bit-exact identity");

            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -2.0 })
                Assert.AreEqual(0.3, PvpContextTuning.ScaleCritChance(0.3, bad), $"{bad} is the identity");
        }

        [TestMethod]
        public void DamageMultiplier_PicksTheShapeKey_AndTheCritKeyOnACrit()
        {
            var dials = PvpContextDials.Neutral
                .With("pvp_arena_war_bolt_dmg", 3.0)
                .With("pvp_arena_war_crit_dmg", 5.0)
                .With("pvp_arena_light_dmg", 7.0);

            var bolt = new PvpContextHit(PvpContext.Arena, PvpCombatCategory.War, PvpWarShape.Bolt, dials);
            var volley = new PvpContextHit(PvpContext.Arena, PvpCombatCategory.War, PvpWarShape.None, dials);
            var light = new PvpContextHit(PvpContext.Arena, PvpCombatCategory.Light, PvpWarShape.None, dials);

            Assert.AreEqual(3.0, PvpContextTuning.DamageMultiplier(bolt, false));
            Assert.AreEqual(15.0, PvpContextTuning.DamageMultiplier(bolt, true));
            Assert.AreEqual(1.0, PvpContextTuning.DamageMultiplier(volley, false), "a volley has no shape damage key");
            Assert.AreEqual(5.0, PvpContextTuning.DamageMultiplier(volley, true), "but still the war crit key");
            Assert.AreEqual(7.0, PvpContextTuning.DamageMultiplier(light, false));
            Assert.AreEqual(7.0, PvpContextTuning.DamageMultiplier(light, true), "light crit_dmg is still 1");
            Assert.AreEqual(1.0, PvpContextTuning.DamageMultiplier(default, true), "an inactive hit is the identity");
        }

        [TestMethod]
        public void DamageMultiplier_BadValuesAreTheIdentity()
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1.0 })
            {
                var dials = PvpContextDials.Neutral.With("pvp_arena_light_dmg", bad).With("pvp_arena_light_crit_dmg", bad);
                var hit = new PvpContextHit(PvpContext.Arena, PvpCombatCategory.Light, PvpWarShape.None, dials);

                Assert.AreEqual(1.0, PvpContextTuning.DamageMultiplier(hit, true), bad.ToString());
            }
        }

        [TestMethod]
        public void Dials_NeutralIsAll1_WithIsACopy()
        {
            foreach (var key in PvpContextTuning.Keys)
                Assert.AreEqual(1.0, PvpContextDials.Neutral.Get(key), key);

            var changed = PvpContextDials.Neutral.With("pvp_bg_bow_dmg", 4.0);

            Assert.AreEqual(4.0, changed.Get("pvp_bg_bow_dmg"));
            Assert.AreEqual(1.0, PvpContextDials.Neutral.Get("pvp_bg_bow_dmg"), "With must not mutate the source");
            Assert.AreEqual(1.0, changed.Get("pvp_melee_damage_mod"), "a non-context key reads as 1.0");
            Assert.AreEqual(0, PvpContextDials.Neutral.NonDefault().Count);
            CollectionAssert.AreEqual(new[] { ("pvp_bg_bow_dmg", "4") }, changed.NonDefault().ToArray());
            Assert.ThrowsExactly<ArgumentException>(() => PvpContextDials.Neutral.With("pvp_melee_damage_mod", 2.0));
        }

        // ================= table-driven: every one of the 116 keys changes ITS hit by exactly 2x =================

        [TestMethod]
        public void EveryDamageKey_At2_DoublesOnlyItsOwnHit()
        {
            var pairs = new Pairs();
            var exercised = new HashSet<string>();

            foreach (var ctx in new[] { PvpContext.Arena, PvpContext.Battleground })
            {
                foreach (var kind in AllKinds)
                {
                    var key = PvpContextTuning.DamageKey(ctx, kind.Category, kind.Shape);

                    if (key == null)
                        continue;

                    Assert.IsTrue(exercised.Add(key), $"{key} exercised twice");
                    UseContext(PvpContextDials.Neutral.With(key, 2.0));

                    foreach (var scope in Scopes)
                    {
                        foreach (var probe in AllKinds)
                        {
                            foreach (var crit in new[] { false, true })
                            {
                                var expected = scope == ScopeOf(ctx) && probe == kind ? Hit * 2.0f : Hit;

                                Assert.AreEqual(expected, Damage(pairs, scope, probe, crit), 1e-3f, $"{key}=2: {probe.Name} in {scope} crit={crit}");
                            }
                        }
                    }
                }
            }

            Assert.AreEqual(26, exercised.Count, "13 damage keys per context (seven categories + six war shapes)");
        }

        [TestMethod]
        public void EveryCritDamageKey_At2_DoublesOnlyItsCategorysCrits()
        {
            var pairs = new Pairs();
            var exercised = new HashSet<string>();
            var categories = AllKinds.Select(k => k.Category).Distinct().ToArray();

            foreach (var ctx in new[] { PvpContext.Arena, PvpContext.Battleground })
            {
                foreach (var category in categories)
                {
                    var key = PvpContextTuning.CritDamageKey(ctx, category);

                    Assert.IsTrue(exercised.Add(key), $"{key} exercised twice");
                    UseContext(PvpContextDials.Neutral.With(key, 2.0));

                    foreach (var scope in Scopes)
                    {
                        foreach (var probe in AllKinds)
                        {
                            var hits = scope == ScopeOf(ctx) && probe.Category == category;

                            Assert.AreEqual(hits ? Hit * 2.0f : Hit, Damage(pairs, scope, probe, true), 1e-3f, $"{key}=2: {probe.Name} crit in {scope}");
                            Assert.AreEqual(Hit, Damage(pairs, scope, probe, false), 1e-3f, $"{key}=2: {probe.Name} NON-crit in {scope} must not change");
                        }
                    }
                }
            }

            Assert.AreEqual(16, exercised.Count);
        }

        [TestMethod]
        public void EveryCritChanceKey_At2_DoublesOnlyItsCategorysChance_AndClamps()
        {
            var pairs = new Pairs();
            var exercised = new HashSet<string>();
            var categories = AllKinds.Select(k => k.Category).Distinct().ToArray();

            foreach (var ctx in new[] { PvpContext.Arena, PvpContext.Battleground })
            {
                foreach (var category in categories)
                {
                    var key = PvpContextTuning.CritChanceKey(ctx, category);

                    Assert.IsTrue(exercised.Add(key), $"{key} exercised twice");
                    UseContext(PvpContextDials.Neutral.With(key, 2.0));

                    foreach (var scope in Scopes)
                    {
                        foreach (var probe in AllKinds)
                        {
                            var hits = scope == ScopeOf(ctx) && probe.Category == category;

                            Assert.AreEqual(hits ? 0.5f : 0.25f, Chance(pairs, scope, probe, 0.25f), 1e-6f, $"{key}=2: {probe.Name} in {scope}");
                            Assert.AreEqual(hits ? 1.0f : 0.75f, Chance(pairs, scope, probe, 0.75f), 1e-6f, $"{key}=2: {probe.Name} in {scope} (clamp)");
                        }
                    }
                }
            }

            Assert.AreEqual(16, exercised.Count);
        }

        [TestMethod]
        public void TheThreeTables_TogetherCoverEveryKeyExactlyOnce()
        {
            var covered = new List<string>();

            foreach (var ctx in new[] { PvpContext.Arena, PvpContext.Battleground })
            {
                foreach (var kind in AllKinds)
                {
                    var key = PvpContextTuning.DamageKey(ctx, kind.Category, kind.Shape);

                    if (key != null)
                        covered.Add(key);
                }

                foreach (var category in AllKinds.Select(k => k.Category).Distinct())
                {
                    covered.Add(PvpContextTuning.CritChanceKey(ctx, category));
                    covered.Add(PvpContextTuning.CritDamageKey(ctx, category));
                }
            }

            foreach (var ctx in new[] { PvpContext.Arena, PvpContext.Battleground })
            {
                foreach (var category in AllKinds.Select(k => k.Category).Distinct().Where(c => c != PvpCombatCategory.War))
                {
                    foreach (var variant in new[] { PvpWeaponVariant.Hollow, PvpWeaponVariant.Weeping })
                    {
                        covered.Add(PvpContextTuning.VariantDamageKey(ctx, category, variant));
                        covered.Add(PvpContextTuning.VariantCritDamageKey(ctx, category, variant));
                    }
                }

                covered.Add(PvpContextTuning.MagicAbsorbKey(ctx));
            }

            CollectionAssert.AreEquivalent(PvpContextTuning.Keys.ToArray(), covered.ToArray(), "the 26 + 16 + 16 + 28 + 28 + 2 table-driven cases must cover all 116 keys, once each");
        }

        // ================= weapon variants (hollow / weeping) and magic absorb =================

        private static readonly PvpWeaponVariant[] VariantStates =
        {
            PvpWeaponVariant.None,
            PvpWeaponVariant.Hollow,
            PvpWeaponVariant.Weeping,
            PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping,
        };

        private static IEnumerable<Kind> PhysicalKinds => AllKinds.Where(k => k.Category != PvpCombatCategory.War);

        private static bool IsMissileCategory(PvpCombatCategory c)
            => c == PvpCombatCategory.Bow || c == PvpCombatCategory.Crossbow || c == PvpCombatCategory.Thrown;

        /// <summary>Stamps the variant flags a retail Deadly Hollow (65 or 66) or Weeping (166 = Human, 138 present) weenie carries.</summary>
        private static WorldObject Flag(WorldObject weapon, PvpWeaponVariant variants, bool hollowViaArmor = false)
        {
            if ((variants & PvpWeaponVariant.Hollow) != 0)
                weapon.SetProperty(hollowViaArmor ? PropertyBool.IgnoreMagicArmor : PropertyBool.IgnoreMagicResist, true);

            if ((variants & PvpWeaponVariant.Weeping) != 0)
            {
                weapon.SetProperty(PropertyInt.SlayerCreatureType, (int)CreatureType.Human);
                weapon.SetProperty(PropertyFloat.SlayerDamageBonus, 1.5);
            }

            return weapon;
        }

        /// <summary>A physical profile for a kind with the variant flags on profile.Weapon (the object combat uses: launcher ?? ammo, the thrown weapon itself for thrown).</summary>
        private static PvpHitProfile VariantProfile(Kind kind, PvpWeaponVariant variants)
        {
            var weapon = ProfileOf(kind).Weapon;

            return PvpHitProfile.ForWeapon(Flag(weapon, variants), IsMissileCategory(kind.Category) ? CombatType.Missile : CombatType.Melee);
        }

        private static float DamageWith(Pairs pairs, PvpScope scope, Kind kind, bool crit, PvpHitProfile profile)
        {
            var (a, d) = ScopePair(pairs, scope);
            return PvpRules.ApplyDamageMods(PvpChokePoint.M1, a, d, Hit, DamageKindOf(kind), crit, profile);
        }

        [TestMethod]
        public void VariantConstants_MatchTheNumbersTheDetectionRelyOn()
        {
            Assert.AreEqual(31, (int)CreatureType.Human);
            Assert.AreEqual(166, (int)PropertyInt.SlayerCreatureType);
            Assert.AreEqual(138, (int)PropertyFloat.SlayerDamageBonus);
            Assert.AreEqual(65, (int)PropertyBool.IgnoreMagicResist);
            Assert.AreEqual(66, (int)PropertyBool.IgnoreMagicArmor);
        }

        [TestMethod]
        public void VariantDetection_HollowEitherFlag_WeepingNeedsHumanAndABonus_PlainIsNone()
        {
            var kind = AllKinds[0];

            Assert.AreEqual(PvpWeaponVariant.None, PvpContextTuning.VariantsOf((WorldObject)null));
            Assert.AreEqual(PvpWeaponVariant.None, PvpContextTuning.VariantsOf(WeaponFixture(Skill.LightWeapons, WeaponType.Sword)));
            Assert.AreEqual(PvpWeaponVariant.Hollow, PvpContextTuning.VariantsOf(Flag(WeaponFixture(Skill.LightWeapons, WeaponType.Sword), PvpWeaponVariant.Hollow)));
            Assert.AreEqual(PvpWeaponVariant.Hollow, PvpContextTuning.VariantsOf(Flag(WeaponFixture(Skill.LightWeapons, WeaponType.Sword), PvpWeaponVariant.Hollow, hollowViaArmor: true)), "IgnoreMagicArmor alone is hollow too");
            Assert.AreEqual(PvpWeaponVariant.Weeping, PvpContextTuning.VariantsOf(Flag(WeaponFixture(Skill.LightWeapons, WeaponType.Sword), PvpWeaponVariant.Weeping)));
            Assert.AreEqual(PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping, PvpContextTuning.VariantsOf(Flag(WeaponFixture(Skill.LightWeapons, WeaponType.Sword), PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping)));

            var notHuman = WeaponFixture(Skill.LightWeapons, WeaponType.Sword);
            notHuman.SetProperty(PropertyInt.SlayerCreatureType, (int)CreatureType.Olthoi);
            notHuman.SetProperty(PropertyFloat.SlayerDamageBonus, 1.5);
            Assert.AreEqual(PvpWeaponVariant.None, PvpContextTuning.VariantsOf(notHuman), "a non-Human slayer is not Weeping");

            var noBonus = WeaponFixture(Skill.LightWeapons, WeaponType.Sword);
            noBonus.SetProperty(PropertyInt.SlayerCreatureType, (int)CreatureType.Human);
            Assert.AreEqual(PvpWeaponVariant.None, PvpContextTuning.VariantsOf(noBonus), "Human slayer type without a bonus is not Weeping");

            var falseFlag = WeaponFixture(Skill.LightWeapons, WeaponType.Sword);
            falseFlag.SetProperty(PropertyBool.IgnoreMagicResist, false);
            Assert.AreEqual(PvpWeaponVariant.None, PvpContextTuning.VariantsOf(falseFlag), "an explicit false is not Hollow");

            // a profile is read from its Weapon only
            Assert.AreEqual(PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping,
                PvpContextTuning.VariantsOf(VariantProfile(AllKinds.Single(k => k.Category == PvpCombatCategory.Bow), PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping)));
            Assert.IsNotNull(kind);
        }

        [TestMethod]
        public void EveryVariantDamageKey_At2_DoublesOnlyAVariantHitOfItsCategory()
        {
            var pairs = new Pairs();
            var exercised = new HashSet<string>();

            foreach (var ctx in new[] { PvpContext.Arena, PvpContext.Battleground })
            {
                foreach (var kind in PhysicalKinds)
                {
                    foreach (var variant in new[] { PvpWeaponVariant.Hollow, PvpWeaponVariant.Weeping })
                    {
                        var key = PvpContextTuning.VariantDamageKey(ctx, kind.Category, variant);

                        Assert.IsTrue(exercised.Add(key), $"{key} exercised twice");
                        UseContext(PvpContextDials.Neutral.With(key, 2.0));

                        foreach (var scope in Scopes)
                        {
                            foreach (var probe in PhysicalKinds)
                            {
                                foreach (var state in VariantStates)
                                {
                                    foreach (var crit in new[] { false, true })
                                    {
                                        var hits = scope == ScopeOf(ctx) && probe == kind && (state & variant) != 0;

                                        Assert.AreEqual(hits ? Hit * 2.0f : Hit, DamageWith(pairs, scope, probe, crit, VariantProfile(probe, state)), 1e-3f, $"{key}=2: {probe.Name} state={state} in {scope} crit={crit}");
                                    }
                                }
                            }

                            foreach (var probe in AllKinds.Where(k => k.Category == PvpCombatCategory.War))
                                Assert.AreEqual(Hit, Damage(pairs, scope, probe, false), 1e-3f, $"{key}=2: {probe.Name} in {scope} must not change");
                        }
                    }
                }
            }

            Assert.AreEqual(28, exercised.Count);
        }

        [TestMethod]
        public void EveryVariantCritDamageKey_At2_DoublesOnlyAVariantCritOfItsCategory()
        {
            var pairs = new Pairs();
            var exercised = new HashSet<string>();

            foreach (var ctx in new[] { PvpContext.Arena, PvpContext.Battleground })
            {
                foreach (var kind in PhysicalKinds)
                {
                    foreach (var variant in new[] { PvpWeaponVariant.Hollow, PvpWeaponVariant.Weeping })
                    {
                        var key = PvpContextTuning.VariantCritDamageKey(ctx, kind.Category, variant);

                        Assert.IsTrue(exercised.Add(key), $"{key} exercised twice");
                        UseContext(PvpContextDials.Neutral.With(key, 2.0));

                        foreach (var scope in Scopes)
                        {
                            foreach (var probe in PhysicalKinds)
                            {
                                foreach (var state in VariantStates)
                                {
                                    var hits = scope == ScopeOf(ctx) && probe == kind && (state & variant) != 0;

                                    Assert.AreEqual(hits ? Hit * 2.0f : Hit, DamageWith(pairs, scope, probe, true, VariantProfile(probe, state)), 1e-3f, $"{key}=2: {probe.Name} state={state} in {scope} CRIT");
                                    Assert.AreEqual(Hit, DamageWith(pairs, scope, probe, false, VariantProfile(probe, state)), 1e-3f, $"{key}=2: {probe.Name} state={state} in {scope} NON-crit must not change");
                                }
                            }
                        }
                    }
                }
            }

            Assert.AreEqual(28, exercised.Count);
        }

        [TestMethod]
        public void VariantKeys_ComposeWithTheBaseKeys_AndABothWeaponTakesBoth_AndAPlainWeaponReadsNeither()
        {
            var pairs = new Pairs();
            var heavy = AllKinds.Single(k => k.Category == PvpCombatCategory.Heavy);

            UseContext(PvpContextDials.Neutral
                .With("pvp_arena_heavy_dmg", 5.0)
                .With("pvp_arena_heavy_hollow_dmg", 2.0)
                .With("pvp_arena_heavy_weeping_dmg", 3.0)
                .With("pvp_arena_heavy_crit_dmg", 7.0)
                .With("pvp_arena_heavy_crit_dmg_hollow_dmg", 11.0)
                .With("pvp_arena_heavy_crit_dmg_weeping_dmg", 13.0));

            Assert.AreEqual(Hit * 5.0f, DamageWith(pairs, PvpScope.Arena, heavy, false, VariantProfile(heavy, PvpWeaponVariant.None)), 1e-1f, "plain: base key only");
            Assert.AreEqual(Hit * 5.0f * 7.0f, DamageWith(pairs, PvpScope.Arena, heavy, true, VariantProfile(heavy, PvpWeaponVariant.None)), 1e-1f, "plain crit: base keys only");

            Assert.AreEqual(Hit * 5.0f * 2.0f, DamageWith(pairs, PvpScope.Arena, heavy, false, VariantProfile(heavy, PvpWeaponVariant.Hollow)), 1e-1f, "hollow reads hollow, not weeping");
            Assert.AreEqual(Hit * 5.0f * 3.0f, DamageWith(pairs, PvpScope.Arena, heavy, false, VariantProfile(heavy, PvpWeaponVariant.Weeping)), 1e-1f, "weeping reads weeping, not hollow");
            Assert.AreEqual(Hit * 5.0f * 2.0f * 3.0f, DamageWith(pairs, PvpScope.Arena, heavy, false, VariantProfile(heavy, PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping)), 1e-1f, "both reads both");

            Assert.AreEqual(Hit * 5.0f * 2.0f * 7.0f * 11.0f, DamageWith(pairs, PvpScope.Arena, heavy, true, VariantProfile(heavy, PvpWeaponVariant.Hollow)), 1e0f, "hollow crit");
            Assert.AreEqual(Hit * 5.0f * 3.0f * 7.0f * 13.0f, DamageWith(pairs, PvpScope.Arena, heavy, true, VariantProfile(heavy, PvpWeaponVariant.Weeping)), 1e0f, "weeping crit");
            Assert.AreEqual(Hit * 5.0f * 2.0f * 3.0f * 7.0f * 11.0f * 13.0f, DamageWith(pairs, PvpScope.Arena, heavy, true, VariantProfile(heavy, PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping)), 1e1f, "both crit");

            // a hollow arena key does not leak to the battleground
            Assert.AreEqual(Hit, DamageWith(pairs, PvpScope.Battleground, heavy, false, VariantProfile(heavy, PvpWeaponVariant.Hollow)), 1e-3f);
        }

        [TestMethod]
        public void MissileVariant_IsReadFromProfileWeaponOnly_TheLauncherDecides()
        {
            var pairs = new Pairs();

            foreach (var kind in PhysicalKinds.Where(k => IsMissileCategory(k.Category)))
            {
                var hollow = PvpContextTuning.VariantDamageKey(PvpContext.Arena, kind.Category, PvpWeaponVariant.Hollow);
                var weeping = PvpContextTuning.VariantDamageKey(PvpContext.Arena, kind.Category, PvpWeaponVariant.Weeping);

                UseContext(PvpContextDials.Neutral.With(hollow, 2.0).With(weeping, 3.0));

                Assert.AreEqual(Hit * 2.0f, DamageWith(pairs, PvpScope.Arena, kind, false, VariantProfile(kind, PvpWeaponVariant.Hollow)), 1e-3f, $"{kind.Name} hollow weapon");
                Assert.AreEqual(Hit * 3.0f, DamageWith(pairs, PvpScope.Arena, kind, false, VariantProfile(kind, PvpWeaponVariant.Weeping)), 1e-3f, $"{kind.Name} weeping weapon");
                Assert.AreEqual(Hit, DamageWith(pairs, PvpScope.Arena, kind, false, VariantProfile(kind, PvpWeaponVariant.None)), 1e-3f, $"{kind.Name} plain weapon");
            }

            // a plain launcher is Weapon (launcher ?? ammo), so hollow / weeping ammo under it is not a variant hit: the profile never sees the ammo
            var bow = AllKinds.Single(k => k.Category == PvpCombatCategory.Bow);
            UseContext(PvpContextDials.Neutral.With("pvp_arena_bow_hollow_dmg", 2.0).With("pvp_arena_bow_weeping_dmg", 3.0));

            var flaggedAmmo = Flag(Seeded<MeleeWeapon>(), PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping);
            var plainLauncher = PvpHitProfile.ForWeapon(WeaponFixture(Skill.MissileWeapons, WeaponType.Bow), CombatType.Missile);

            Assert.AreEqual(PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping, PvpContextTuning.VariantsOf(flaggedAmmo), "control: that ammo object is flagged");
            Assert.AreEqual(Hit, DamageWith(pairs, PvpScope.Arena, bow, false, plainLauncher), 1e-3f, "plain launcher: flagged ammo is invisible to the profile");
        }

        [TestMethod]
        public void CritChanceResolution_NeverReadsVariants_ButTheDamagePathDoes()
        {
            var pairs = new Pairs();
            var bow = AllKinds.Single(k => k.Category == PvpCombatCategory.Bow);
            var flagged = VariantProfile(bow, PvpWeaponVariant.Hollow | PvpWeaponVariant.Weeping);

            UseContext(PvpContextDials.Neutral.With("pvp_arena_bow_crit_chance", 2.0).With("pvp_arena_bow_hollow_dmg", 2.0));

            var before = Interlocked.Read(ref PvpContextTuning.VariantReadCount);

            Assert.AreEqual(0.5f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC1, pairs.Arena.A, pairs.Arena.D, flagged, 0.25f), 1e-6f, "control: the crit chance key bites");
            Assert.AreEqual(0.5f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC2, pairs.Arena.A, pairs.Arena.D, flagged, 0.25f), 1e-6f);
            Assert.AreEqual(before, Interlocked.Read(ref PvpContextTuning.VariantReadCount), "crit-chance resolution made zero variant property reads");

            Assert.AreEqual(Hit * 2.0f, DamageWith(pairs, PvpScope.Arena, bow, false, flagged), 1e-3f, "the damage path bites");
            Assert.IsTrue(Interlocked.Read(ref PvpContextTuning.VariantReadCount) > before, "control: the damage path DOES read variants, so the counter is live");
        }
        [TestMethod]
        public void VariantKeys_AreNotReadForAPlainWeapon_AndAnUnflaggedWeaponNeverGainsAVariant()
        {
            // every variant key at 5: a plain weapon, and every spell, is untouched
            var variantKeys = PvpContextTuning.Keys.Where(k => k.Contains("hollow") || k.Contains("weeping")).ToArray();
            Assert.AreEqual(56, variantKeys.Length);

            UseContext(variantKeys.Aggregate(PvpContextDials.Neutral, (d, k) => d.With(k, 5.0)));

            var pairs = new Pairs();

            foreach (var scope in Scopes)
            {
                foreach (var kind in AllKinds)
                {
                    foreach (var crit in new[] { false, true })
                        Assert.AreEqual(Hit, Damage(pairs, scope, kind, crit), 1e-3f, $"{kind.Name} {scope} crit={crit}");
                }
            }
        }

        [TestMethod]
        public void EveryMagicAbsorbKey_At2_ScalesOnlyItsContextsPair_AndNoDamageKeyChanges()
        {
            var pairs = new Pairs();

            foreach (var ctx in new[] { PvpContext.Arena, PvpContext.Battleground })
            {
                var key = PvpContextTuning.MagicAbsorbKey(ctx);

                UseContext(PvpContextDials.Neutral.With(key, 2.0));

                foreach (var scope in Scopes)
                {
                    var (a, d) = ScopePair(pairs, scope);
                    var hits = scope == ScopeOf(ctx);

                    // 0.64 absorb = a 0.36 reduction; x2 = 0.72 reduction = 0.28
                    Assert.AreEqual(hits ? 0.28f : 0.64f, PvpRules.ApplyMagicAbsorbMod(a, d, 0.64f), 1e-5f, $"{key}=2 in {scope}");
                    Assert.AreEqual(1.0f, PvpRules.ApplyMagicAbsorbMod(a, d, 1.0f), $"{key}: nothing absorbing stays 1.0 in {scope}");

                    // an absorb key is not a damage key
                    foreach (var kind in AllKinds)
                        Assert.AreEqual(Hit, Damage(pairs, scope, kind, true), 1e-3f, $"{key}=2 must not change {kind.Name} damage in {scope}");
                }
            }
        }

        [TestMethod]
        public void MagicAbsorb_ContextComposesWithTheGlobalMod_GlobalFirst_AndClamps()
        {
            var pairs = new Pairs();

            UseRules(PvpRuleTunables.Defaults with { MagicAbsorbMod = 1.5 });
            UseContext(PvpContextDials.Neutral.With("pvp_arena_magic_absorb", 1.5));

            // 0.64 -> reduction 0.36 x 1.5 = 0.54 (global) -> absorb 0.46 -> reduction 0.54 x 1.5 = 0.81 (context) -> absorb 0.19
            Assert.AreEqual(0.19f, PvpRules.ApplyMagicAbsorbMod(pairs.Arena.A, pairs.Arena.D, 0.64f), 1e-5f, "arena: global then context");
            Assert.AreEqual(0.46f, PvpRules.ApplyMagicAbsorbMod(pairs.Battleground.A, pairs.Battleground.D, 0.64f), 1e-5f, "battleground has no context absorb set: global only");
            Assert.AreEqual(0.46f, PvpRules.ApplyMagicAbsorbMod(pairs.OpenWorld.A, pairs.OpenWorld.D, 0.64f), 1e-5f, "open world: global only");

            // clamped to [0, 1] at the context stage too
            UseContext(PvpContextDials.Neutral.With("pvp_arena_magic_absorb", 10.0));
            Assert.AreEqual(0.0f, PvpRules.ApplyMagicAbsorbMod(pairs.Arena.A, pairs.Arena.D, 0.64f), "10 x clamps to absorbing everything");

            UseContext(PvpContextDials.Neutral.With("pvp_arena_magic_absorb", 0.0));
            Assert.AreEqual(1.0f, PvpRules.ApplyMagicAbsorbMod(pairs.Arena.A, pairs.Arena.D, 0.64f), "0 turns the context stage into no absorption");

            // bad values are the identity
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1.0 })
            {
                UseContext(PvpContextDials.Neutral.With("pvp_arena_magic_absorb", bad));
                Assert.AreEqual(0.46f, PvpRules.ApplyMagicAbsorbMod(pairs.Arena.A, pairs.Arena.D, 0.64f), 1e-5f, $"{bad} is the identity (global still applies)");
            }
        }

        [TestMethod]
        public void MagicAbsorb_ContextKey_MasterSwitchOff_OrNothingAbsorbing_ReadsNothing()
        {
            var pairs = new Pairs();

            UseContext(PvpContextDials.Neutral.With("pvp_arena_magic_absorb", 2.0).With("pvp_bg_magic_absorb", 2.0));
            UseRules(PvpRuleTunables.Defaults with { Enabled = false });

            Assert.AreEqual(0.64f, PvpRules.ApplyMagicAbsorbMod(pairs.Arena.A, pairs.Arena.D, 0.64f), "master switch off");
            Assert.AreEqual(0, contextReads);

            UseRules(PvpRuleTunables.Defaults);
            Assert.AreEqual(1.0f, PvpRules.ApplyMagicAbsorbMod(pairs.Arena.A, pairs.Arena.D, 1.0f));
            Assert.AreEqual(0.64f, PvpRules.ApplyMagicAbsorbMod(pairs.OpenWorld.A, pairs.OpenWorld.D, 0.64f), "open world");
            Assert.AreEqual(0, contextReads, "nothing absorbing and open world read no context key");

            Assert.AreEqual(0.28f, PvpRules.ApplyMagicAbsorbMod(pairs.Arena.A, pairs.Arena.D, 0.64f), 1e-5f, "control: with the switch on and an absorb the key bites");
            Assert.AreEqual(1, contextReads);
        }
        // ================= master switch, void/life, volley/wall/strike, stacking, cap =================

        [TestMethod]
        public void MasterSwitchOff_NewKeysHaveNoEffect_AndAreNotRead()
        {
            var all2 = PvpContextTuning.Keys.Aggregate(PvpContextDials.Neutral, (d, k) => d.With(k, 2.0));
            UseContext(all2);
            UseRules(PvpRuleTunables.Defaults with { Enabled = false });

            var pairs = new Pairs();

            foreach (var scope in Scopes)
            {
                foreach (var kind in AllKinds)
                {
                    Assert.AreEqual(Hit, Damage(pairs, scope, kind, true), 1e-3f, $"{kind.Name} {scope} damage");
                    Assert.AreEqual(0.25f, Chance(pairs, scope, kind, 0.25f), 1e-6f, $"{kind.Name} {scope} chance");
                }
            }

            Assert.AreEqual(0, contextReads, "a switched-off hit must not even read the context keys");

            UseRules(PvpRuleTunables.Defaults);
            Assert.AreEqual(Hit * 2.0f * 2.0f, Damage(pairs, PvpScope.Arena, AllKinds[0], true), 1e-3f, "control: with the switch on the same keys bite (dmg x crit_dmg)");
        }

        [TestMethod]
        public void VoidAndLifeProjectiles_GetNoNewKey()
        {
            var all5 = PvpContextTuning.Keys.Aggregate(PvpContextDials.Neutral, (d, k) => d.With(k, 5.0));
            UseContext(all5);

            var pairs = new Pairs();

            foreach (var school in new[] { MagicSchool.VoidMagic, MagicSchool.LifeMagic })
            {
                foreach (var spellType in new[] { ProjectileSpellType.Bolt, ProjectileSpellType.Ring, ProjectileSpellType.Volley })
                {
                    foreach (var crit in new[] { false, true })
                    {
                        Assert.AreEqual(Hit, PvpRules.ApplyDamageMods(PvpChokePoint.M2, pairs.Arena.A, pairs.Arena.D, Hit, PvpDamageKind.Other, crit, PvpHitProfile.ForSpell(school, spellType)), 1e-3f, $"{school} {spellType} arena crit={crit}");
                        Assert.AreEqual(Hit, PvpRules.ApplyDamageMods(PvpChokePoint.M2, pairs.Battleground.A, pairs.Battleground.D, Hit, PvpDamageKind.Other, crit, PvpHitProfile.ForSpell(school, spellType)), 1e-3f, $"{school} {spellType} bg crit={crit}");
                    }

                    Assert.AreEqual(0.25f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC2, pairs.Arena.A, pairs.Arena.D, PvpHitProfile.ForSpell(school, spellType), 0.25f), 1e-6f);
                    Assert.AreEqual(0.25f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC2, pairs.Battleground.A, pairs.Battleground.D, PvpHitProfile.ForSpell(school, spellType), 0.25f), 1e-6f);
                }
            }

            Assert.AreEqual(0, contextReads, "void and life projectiles have no category, so no context key is read for them");

            // control: the same keys DO bite a war bolt
            Assert.AreNotEqual(Hit, PvpRules.ApplyDamageMods(PvpChokePoint.M2, pairs.Arena.A, pairs.Arena.D, Hit, PvpDamageKind.WarMagic, true, PvpHitProfile.ForSpell(MagicSchool.WarMagic, ProjectileSpellType.Bolt)));
        }

        [TestMethod]
        public void WarVolleyStrike_ShapeDamageIsIdentity_ButWarCritKeysStillApply()
        {
            var pairs = new Pairs();

            // every shape damage key at 5: a volley / strike is untouched (a wall has its own key, covered by the table-driven test)
            var shapeKeys = new[] { PvpWarShape.Bolt, PvpWarShape.Arc, PvpWarShape.Streak, PvpWarShape.Blast, PvpWarShape.Ring, PvpWarShape.Wall }
                .Aggregate(PvpContextDials.Neutral, (d, s) => d.With(PvpContextTuning.DamageKey(PvpContext.Arena, PvpCombatCategory.War, s), 5.0));
            UseContext(shapeKeys);

            foreach (var spellType in new[] { ProjectileSpellType.Volley, ProjectileSpellType.Strike, ProjectileSpellType.Undef })
            {
                Assert.AreEqual(Hit, PvpRules.ApplyDamageMods(PvpChokePoint.M2, pairs.Arena.A, pairs.Arena.D, Hit, PvpDamageKind.WarMagic, false, PvpHitProfile.ForSpell(MagicSchool.WarMagic, spellType)), 1e-3f, $"{spellType} damage");
                Assert.AreEqual(Hit, PvpRules.ApplyDamageMods(PvpChokePoint.M2, pairs.Arena.A, pairs.Arena.D, Hit, PvpDamageKind.WarMagic, true, PvpHitProfile.ForSpell(MagicSchool.WarMagic, spellType)), 1e-3f, $"{spellType} crit damage (crit key still 1)");
            }

            // the war crit keys apply to all of them
            UseContext(PvpContextDials.Neutral
                .With("pvp_arena_war_crit_dmg", 3.0)
                .With("pvp_arena_war_crit_chance", 2.0));

            foreach (var spellType in new[] { ProjectileSpellType.Volley, ProjectileSpellType.Wall, ProjectileSpellType.Strike })
            {
                var profile = PvpHitProfile.ForSpell(MagicSchool.WarMagic, spellType);

                Assert.AreEqual(Hit * 3.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M2, pairs.Arena.A, pairs.Arena.D, Hit, PvpDamageKind.WarMagic, true, profile), 1e-3f, $"{spellType} crit dmg key");
                Assert.AreEqual(Hit, PvpRules.ApplyDamageMods(PvpChokePoint.M2, pairs.Arena.A, pairs.Arena.D, Hit, PvpDamageKind.WarMagic, false, profile), 1e-3f, $"{spellType} non-crit");
                Assert.AreEqual(0.5f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC2, pairs.Arena.A, pairs.Arena.D, profile, 0.25f), 1e-6f, $"{spellType} crit chance key");
            }
        }

        [TestMethod]
        public void ContextMods_StackWithTheGlobalKindAndCritMods()
        {
            var pairs = new Pairs();

            UseRules(PvpRuleTunables.Defaults with { MeleeDamageMod = 2.0, CritDamageMod = 3.0 });
            UseContext(PvpContextDials.Neutral.With("pvp_arena_heavy_dmg", 5.0).With("pvp_arena_heavy_crit_dmg", 7.0));

            var heavy = AllKinds.Single(k => k.Category == PvpCombatCategory.Heavy);

            Assert.AreEqual(Hit * 2.0f * 5.0f, Damage(pairs, PvpScope.Arena, heavy, false), 1e-2f, "kind x context");
            Assert.AreEqual(Hit * 2.0f * 3.0f * 5.0f * 7.0f, Damage(pairs, PvpScope.Arena, heavy, true), 1e-1f, "kind x crit x context dmg x context crit_dmg");
            Assert.AreEqual(Hit * 2.0f, Damage(pairs, PvpScope.OpenWorld, heavy, false), 1e-2f, "open-world gets the global kind mod only");
            Assert.AreEqual(Hit * 2.0f * 3.0f, Damage(pairs, PvpScope.OpenWorld, heavy, true), 1e-2f, "open-world crit gets the global crit mod only");
        }

        [TestMethod]
        public void TheDamageCap_StillAppliesAfterTheNewMultipliers()
        {
            var pairs = new Pairs();

            UseRules(PvpRuleTunables.Defaults with { DamageCap = 1000, DamageCapMaxHealthFraction = 0 });
            UseContext(PvpContextDials.Neutral.With("pvp_bg_light_dmg", 3.0));

            var light = AllKinds.Single(k => k.Category == PvpCombatCategory.Light);
            var scaled = Damage(pairs, PvpScope.Battleground, light, false);

            Assert.AreEqual(Hit * 3.0f, scaled, 1e-2f, "the multiplier itself is not capped");
            Assert.AreEqual(1000.0f, PvpRules.ApplyDamageCap(PvpChokePoint.C1, pairs.Battleground.A, pairs.Battleground.D, scaled), "the cap then clamps the scaled hit");
            Assert.AreEqual(Hit, PvpRules.ApplyDamageCap(PvpChokePoint.C1, pairs.Battleground.A, pairs.Battleground.D, Hit), "control: an unscaled hit is under the cap");
        }

        [TestMethod]
        public void Report_NamesTheChokePoint_AndTheChangedValues()
        {
            var seen = new List<(PvpChokePoint Point, double Before, double After)>();
            PvpRules.Observer = (p, b, a) => seen.Add((p, b, a));

            var pairs = new Pairs();
            UseContext(PvpContextDials.Neutral.With("pvp_arena_bow_crit_chance", 2.0).With("pvp_arena_war_crit_chance", 2.0));

            Chance(pairs, PvpScope.Arena, AllKinds.Single(k => k.Name == "bow"), 0.25f);
            Chance(pairs, PvpScope.Arena, AllKinds.Single(k => k.Name == "war bolt"), 0.25f);
            Chance(pairs, PvpScope.Arena, AllKinds.Single(k => k.Name == "light"), 0.25f);

            Assert.AreEqual(2, seen.Count, "only a changed chance is reported");
            Assert.AreEqual(PvpChokePoint.CC1, seen[0].Point);
            Assert.AreEqual(PvpChokePoint.CC2, seen[1].Point);
            Assert.AreEqual(0.25, seen[0].Before, 1e-6);
            Assert.AreEqual(0.5, seen[0].After, 1e-6);
        }

        // ================= snapshot cache =================

        /// <summary>
        /// The default source serves a cached snapshot: a second arena hit with nothing modified in between does ZERO
        /// property reads, a /modify of one context key is seen on the very next hit, and so is a change that arrives
        /// with no /modify call at all (the DB reload behind /resyncproperties), which only moves the epoch.
        /// </summary>
        [TestMethod]
        public void DefaultSource_IsCached_ModifyAndResyncAreObservedOnTheNextHit()
        {
            var reads = 0;
            long epoch = 0;
            var values = new Dictionary<string, double>();

            PvpContextTunables.DialSource = savedContextDials;   // the real default source
            PvpContextTunables.PropertyReader = key => { reads++; return values.TryGetValue(key, out var v) ? v : 1.0; };
            PvpContextTunables.EpochSource = () => epoch;
            PvpContextTunables.Invalidate();

            var pairs = new Pairs();
            var light = AllKinds.Single(k => k.Category == PvpCombatCategory.Light);

            Assert.AreEqual(Hit, Damage(pairs, PvpScope.Arena, light, false), 1e-3f);
            Assert.AreEqual(PvpContextTuning.Keys.Count, reads, "the first hit reads every key once");

            Assert.AreEqual(Hit, Damage(pairs, PvpScope.Arena, light, false), 1e-3f);
            Assert.AreEqual(0.25f, Chance(pairs, PvpScope.Arena, light, 0.25f), 1e-6f);
            Assert.AreEqual(Hit, Damage(pairs, PvpScope.Battleground, light, true), 1e-3f);
            Assert.AreEqual(PvpContextTuning.Keys.Count, reads, "further hits (damage and crit chance, either context) read no property");

            // a /modify of one context key
            values["pvp_arena_light_dmg"] = 2.0;
            PvpRuleTunables.OnPropertyModified("pvp_arena_light_dmg");
            var afterModifyReads = reads;
            Assert.AreEqual(Hit * 2.0f, Damage(pairs, PvpScope.Arena, light, false), 1e-3f, "the modified key bites on the next hit");
            Assert.AreEqual(afterModifyReads, reads, "the modify's own re-read is the only one; the hit used the cache");

            // a non-context key modify must not drop the snapshot
            PvpRuleTunables.OnPropertyModified("pvp_melee_damage_mod");
            Damage(pairs, PvpScope.Arena, light, false);
            Assert.AreEqual(afterModifyReads, reads);

            // a change with no /modify call: only the epoch moves (resync / periodic reload / ModifyDouble from code)
            values["pvp_arena_light_dmg"] = 3.0;
            epoch++;
            Assert.AreEqual(Hit * 3.0f, Damage(pairs, PvpScope.Arena, light, false), 1e-3f, "a reload that moved the epoch is seen on the next hit");
        }

        /// <summary>The REAL wiring (not the EpochSource seam): ModifyDouble bumps PropertyManager.DoubleSettingsEpoch.</summary>
        [TestMethod]
        public void ModifyDouble_RealWiring_BumpsTheEpoch()
        {
            const string key = "pvp_arena_light_dmg";
            var before = PropertyManager.DoubleSettingsEpoch;

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(key, 2.0, init: true));
                Assert.IsTrue(PropertyManager.DoubleSettingsEpoch > before, "ModifyDouble must bump the epoch the snapshot cache is keyed on");
            }
            finally
            {
                PropertyManager.ModifyDouble(key, 1.0, init: true);
            }
        }

        /// <summary>
        /// Source pin: the epoch is incremented in exactly three places (ModifyDouble, the boot merge in Initialize and
        /// the reload merge in DoWork), and the two merge ones sit inside a finally so a merge that throws partway
        /// still bumps it.
        /// </summary>
        [TestMethod]
        public void EpochIncrement_AppearsThreeTimes_TheTwoMergeOnesInAFinally()
        {
            const string increment = "System.Threading.Interlocked.Increment(ref doubleSettingsEpoch);";

            var code = CodeLines("ACE.Server/Managers/PropertyManager.cs").Where(l => l.Length > 0).ToArray();
            var hits = Enumerable.Range(0, code.Length).Where(i => code[i] == increment).ToArray();

            Assert.AreEqual(3, hits.Length, "expected exactly three increments (ModifyDouble, Initialize, DoWork)");

            var inFinally = hits.Count(i => i >= 2 && code[i - 1] == "{" && code[i - 2] == "finally");

            Assert.AreEqual(2, inFinally, "the Initialize and DoWork increments must each be the body of a finally");
        }

        [TestMethod]
        public void DefaultSource_FailedRead_IsNeutral_AndNeverCached()
        {
            var fail = true;
            var reads = 0;

            PvpContextTunables.DialSource = savedContextDials;
            PvpContextTunables.PropertyReader = key => { reads++; if (fail) throw new InvalidOperationException("no db"); return 2.0; };
            PvpContextTunables.EpochSource = () => 0;
            PvpContextTunables.Invalidate();

            Assert.AreSame(PvpContextDials.Neutral, PvpContextTunables.Read());

            fail = false;
            Assert.AreEqual(2.0, PvpContextTunables.Read().Get("pvp_bg_bow_dmg"), "the failure was not cached");
        }

        // ================= which weapon decides the category =================

        /// <summary>
        /// DamageEvent.Weapon is the swinging weapon (the offhand one on an offhand swing). The category comes from THAT
        /// weapon's skill, never from the attacker's current weapon skill (DualWield on an offhand swing). Two weapons
        /// of different categories give two different keys for the same attacker.
        /// </summary>
        [TestMethod]
        public void OffhandSwing_UsesTheSwingingWeaponsCategory()
        {
            var pairs = new Pairs();
            UseContext(PvpContextDials.Neutral.With("pvp_arena_heavy_dmg", 2.0).With("pvp_arena_finesse_dmg", 3.0));

            var (a, d) = pairs.Arena;
            var mainHand = PvpHitProfile.ForWeapon(WeaponFixture(Skill.HeavyWeapons, WeaponType.Axe), CombatType.Melee);
            var offHand = PvpHitProfile.ForWeapon(WeaponFixture(Skill.FinesseWeapons, WeaponType.Dagger), CombatType.Melee);

            Assert.AreEqual(Hit * 2.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, a, d, Hit, PvpDamageKind.Melee, false, mainHand), 1e-3f);
            Assert.AreEqual(Hit * 3.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, a, d, Hit, PvpDamageKind.Melee, false, offHand), 1e-3f, "same attacker, offhand weapon: the offhand's category");
        }

        [TestMethod]
        public void UnarmedHit_UsesTheHighestMeleeSkillsCategory()
        {
            var pairs = new Pairs();
            UseContext(PvpContextDials.Neutral.With("pvp_arena_heavy_dmg", 2.0).With("pvp_arena_finesse_dmg", 3.0));

            var (a, d) = pairs.Arena;
            var unarmed = PvpHitProfile.ForWeapon(null, CombatType.Melee);

            PvpContextTuning.HighestMeleeSource = p => Skill.HeavyWeapons;
            Assert.AreEqual(Hit * 2.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, a, d, Hit, PvpDamageKind.Melee, false, unarmed), 1e-3f);

            PvpContextTuning.HighestMeleeSource = p => Skill.FinesseWeapons;
            Assert.AreEqual(Hit * 3.0f, PvpRules.ApplyDamageMods(PvpChokePoint.M1, a, d, Hit, PvpDamageKind.Melee, false, unarmed), 1e-3f, "highest melee skill changed, so did the key");
        }

        [TestMethod]
        public void NoHighestMeleeRead_ForAnArmedHit_OrAnOpenWorldHit()
        {
            var pairs = new Pairs();
            var reads = 0;
            PvpContextTuning.HighestMeleeSource = p => { reads++; return Skill.HeavyWeapons; };
            UseContext(PvpContextDials.Neutral.With("pvp_arena_heavy_dmg", 2.0));

            PvpRules.ApplyDamageMods(PvpChokePoint.M1, pairs.Arena.A, pairs.Arena.D, Hit, PvpDamageKind.Melee, false, ProfileOf(AllKinds[0]));
            PvpRules.ApplyDamageMods(PvpChokePoint.M1, pairs.OpenWorld.A, pairs.OpenWorld.D, Hit, PvpDamageKind.Melee, false, PvpHitProfile.ForWeapon(null, CombatType.Melee));

            Assert.AreEqual(0, reads, "an armed hit and an open-world hit never resolve the unarmed fallback");

            PvpRules.ApplyDamageMods(PvpChokePoint.M1, pairs.Arena.A, pairs.Arena.D, Hit, PvpDamageKind.Melee, false, PvpHitProfile.ForWeapon(null, CombatType.Melee));
            Assert.AreEqual(1, reads, "control: an unarmed arena hit does");
        }

        // ================= classify first, read second =================

        [TestMethod]
        public void OpenWorldAndPvE_ReadNoContextKey()
        {
            var pairs = new Pairs();
            UseContext(PvpContextTuning.Keys.Aggregate(PvpContextDials.Neutral, (d, k) => d.With(k, 10.0)));

            var creature = Seeded<Creature>();
            var weapon = ProfileOf(AllKinds[0]);

            foreach (var (source, target) in new (WorldObject, Creature)[]
            {
                (pairs.OpenWorld.A, pairs.OpenWorld.D),
                (pairs.OpenWorld.A, creature),
                (creature, pairs.OpenWorld.D),
                (creature, creature),
                (pairs.Arena.A, pairs.Arena.A),
                (null, pairs.Arena.D),
                (pairs.Arena.A, null),
            })
            {
                Assert.AreEqual(Hit, PvpRules.ApplyDamageMods(PvpChokePoint.M1, source, target, Hit, PvpDamageKind.Melee, true, weapon), 1e-3f);
                Assert.AreEqual(0.25f, PvpContextTuning.ApplyCritChance(PvpChokePoint.CC1, source, target, weapon, 0.25f), 1e-6f);
            }

            Assert.AreEqual(0, contextReads);
        }

        // ================= tunables seam, log line, /pvprules =================

        [TestMethod]
        public void OnPropertyModified_LogsOnlyForContextKeys()
        {
            var reads = 0;
            PvpContextTunables.DialSource = () => { reads++; return PvpContextDials.Neutral.With("pvp_arena_light_dmg", 2.0); };

            PvpRuleTunables.OnPropertyModified("pvp_arena_light_dmg");
            Assert.AreEqual(1, reads, "a context key is re-read and logged");

            PvpRuleTunables.OnPropertyModified("pvp_arena_dmg_mod_1v1");
            PvpRuleTunables.OnPropertyModified("xp_modifier");
            PvpRuleTunables.OnPropertyModified(null);
            Assert.AreEqual(1, reads, "a non-context key does not touch the context seam");
        }

        [TestMethod]
        public void LogLine_ListsOnlyNonNeutralKeys()
        {
            Assert.AreEqual("[PVP] context tuning (all neutral) reason=boot", PvpContextTunables.FormatLogLine(PvpContextDials.Neutral, "boot"));
            Assert.AreEqual("[PVP] context tuning pvp_bg_thrown_crit_chance=0.5 reason=modify:x",
                PvpContextTunables.FormatLogLine(PvpContextDials.Neutral.With("pvp_bg_thrown_crit_chance", 0.5), "modify:x"));
        }

        [TestMethod]
        public void PvpRulesReport_ListsContextKeysOffNeutral()
        {
            var plain = PvpRulesCommands.BuildReport(PvpRuleTunables.Defaults);
            StringAssert.Contains(plain, "116 of 116 keys at the default 1");

            var report = PvpRulesCommands.BuildReport(PvpRuleTunables.Defaults, PvpContextDials.Neutral.With("pvp_arena_war_ring_dmg", 1.5));

            StringAssert.Contains(report, "115 of 116 keys at the default 1");
            StringAssert.Contains(report, "  * pvp_arena_war_ring_dmg = 1.5 (default 1)");

            foreach (PvpChokePoint point in Enum.GetValues(typeof(PvpChokePoint)))
                StringAssert.Contains(report, $"{point}=");

            StringAssert.Contains(report, "CC1=");
            StringAssert.Contains(report, "CC2=");
        }

        [TestMethod]
        public void ThrowingDialSource_FallsBackToNeutral()
        {
            PvpContextTunables.DialSource = () => throw new InvalidOperationException("boom");

            Assert.AreSame(PvpContextDials.Neutral, PvpContextTunables.Read());

            PvpContextTunables.DialSource = () => null;
            Assert.AreSame(PvpContextDials.Neutral, PvpContextTunables.Read());
        }

        // ================= call-site pins =================

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }

        private static string[] CodeLines(string file)
        {
            var path = Path.Combine(FindSourceRoot(), file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            return File.ReadAllLines(path)
                .Select(raw => { var at = raw.IndexOf("//", StringComparison.Ordinal); return (at >= 0 ? raw.Substring(0, at) : raw).Trim(); })
                .ToArray();
        }

        private static int LineOf(string[] lines, string code)
        {
            var hits = Enumerable.Range(0, lines.Length).Where(i => lines[i] == code).ToArray();
            Assert.AreEqual(1, hits.Length, $"expected exactly one code line `{code}`");
            return hits[0];
        }

        /// <summary>CC1 sits after the crit chance is computed and BEFORE the logout always-crit override, so a logging-out target still takes 100%.</summary>
        [TestMethod]
        public void CC1_IsAfterTheChanceAndBeforeTheLogoutOverride()
        {
            var lines = CodeLines("ACE.Server/Entity/DamageEvent.cs");

            var computed = LineOf(lines, "CriticalChance = WorldObject.GetWeaponCriticalChance(Weapon, attacker, attackSkill, defender);");
            var cc1 = LineOf(lines, "CriticalChance = PvpContextTuning.ApplyCritChance(PvpChokePoint.CC1, attacker, defender, PvpHitProfile.ForWeapon(Weapon, CombatType), CriticalChance);");
            var logout = LineOf(lines, "CriticalChance = 1.0f;");

            Assert.IsTrue(computed < cc1 && cc1 < logout, $"order: computed {computed + 1}, CC1 {cc1 + 1}, logout override {logout + 1}");
        }

        [TestMethod]
        public void CC2_IsAtTheSpellCritRollCallSite_BeforeTheRoll()
        {
            var lines = CodeLines("ACE.Server/WorldObjects/SpellProjectile.cs");

            var computed = LineOf(lines, "var criticalChance = GetWeaponMagicCritFrequency(weapon, sourceCreature, attackSkill, target);");
            var cc2 = LineOf(lines, "criticalChance = PvpContextTuning.ApplyCritChance(PvpChokePoint.CC2, source, target, PvpHitProfile.ForSpell(Spell.School, SpellType), criticalChance);");
            var roll = LineOf(lines, "if (ThreadSafeRandom.Next(0.0f, 1.0f) < criticalChance)");

            Assert.IsTrue(computed < cc2 && cc2 < roll, $"order: computed {computed + 1}, CC2 {cc2 + 1}, roll {roll + 1}");
        }

        /// <summary>The category must never come from GetCurrentWeaponSkill (DualWield on an offhand swing; the highest melee skill when unarmed).</summary>
        [TestMethod]
        public void Categorization_NeverCallsGetCurrentWeaponSkill()
        {
            var lines = CodeLines("ACE.Server/Pvp/Rules/PvpContextTuning.cs");

            Assert.IsFalse(lines.Any(l => l.Contains("GetCurrentWeaponSkill") || l.Contains("GetCurrentAttackSkill")), "PvpContextTuning must categorize from the swinging weapon's own skill");
            Assert.IsTrue(lines.Any(l => l.Contains("ConvertToMoASkill(weapon.WeaponSkill)")), "the weapon's own skill, through ConvertToMoASkill");
        }

    }
}
