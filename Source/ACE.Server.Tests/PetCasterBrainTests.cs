using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using DatSpellBase = ACE.DatLoader.Entity.SpellBase;
using DbSpell = ACE.Database.Models.World.Spell;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom Summons Task 6, the caster pet brain. Three layers:
    ///   - PetCasterBrain.Decide as a table (pure, no world): heal priority, buff and debuff redundancy, engaged
    ///     enemies only, rings closing in, Follow when the owner cannot be read;
    ///   - the world-touching seams that feed it, on real CombatPet/Creature objects: the engaged-enemy list
    ///     (CollectCasterEnemies), the owner-unavailable early return of BuildCasterContext, and IsCasterPet;
    ///   - one delivery test through the real cast core (Creature.CastSpellAt -> HandleCastSpell -> the Boost
    ///     path) with a pet healing its sibling, on a faked Spell (new Spell(...) needs the client dat, which this
    ///     assembly never loads - see PvpArenaSpellRulesIntegrationTests for the same technique).
    ///
    /// A Player cannot be constructed here, so owners are RuntimeHelpers.GetUninitializedObject Players that are
    /// only ever compared by reference (as in PetEngagementTests); a heal landing ON a Player is an owner in-game
    /// check. PropertyManager is loaded from the code defaults in ClassInitialize; no test changes a key.
    /// </summary>
    [TestClass]
    public class PetCasterBrainTests
    {
        private static uint nextGuid = 0x7F5C0000;
        private static uint nextWcid = 995700;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- spell fixtures (ids are labels only; nothing is looked up) ------------------------------------

        private const int Heal = 1;
        private const int Revitalize = 2;
        private const int StrengthBuff = 3;
        private const int FireVuln = 4;
        private const int FlameBolt = 5;
        private const int FlameRing = 6;
        private const int ColdVuln = 7;

        private const EnchantmentTypeFlags VulnType = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative;

        private static CasterSpell HealSpell(float range = 30.0f) => new CasterSpell(Heal, CasterSpellKind.Heal, CasterVital.Health, range: range);
        private static CasterSpell RevitalizeSpell() => new CasterSpell(Revitalize, CasterSpellKind.Heal, CasterVital.Stamina, range: 30.0f);
        private static CasterSpell Buff(uint power = 300) => new CasterSpell(StrengthBuff, CasterSpellKind.Buff, category: SpellCategory.StrengthRaising, power: power, range: 30.0f);

        private static CasterSpell FireVulnSpell() => new CasterSpell(FireVuln, CasterSpellKind.Harmful,
            shape: MonsterSpellShape.FromSpellFields(VulnType, (uint)PropertyFloat.ResistFire, 2.5f, DamageType.Undef, SpellCategory.FireVulnerability), range: 30.0f);

        private static CasterSpell ColdVulnSpell() => new CasterSpell(ColdVuln, CasterSpellKind.Harmful,
            shape: MonsterSpellShape.FromSpellFields(VulnType, (uint)PropertyFloat.ResistCold, 2.5f, DamageType.Undef, SpellCategory.ColdVulnerability), range: 30.0f);

        private static CasterSpell Bolt(float range = 30.0f) => new CasterSpell(FlameBolt, CasterSpellKind.Harmful,
            shape: MonsterSpellShape.FromSpellFields(0, 0, 0.0f, DamageType.Fire), range: range);

        /// <summary>
        /// A ring, with Flame Ring's (3806) dat range: BaseRangeConstant 4.5, BaseRangeMod 0 (read from the local
        /// client_portal.dat, see PetCasterBrain.UnboundedHarmfulReach), so the formula gives its 4.5 m reach.
        /// </summary>
        private static CasterSpell Ring() => new CasterSpell(FlameRing, CasterSpellKind.Harmful,
            shape: MonsterSpellShape.FromSpellFields(0, 0, 0.0f, DamageType.Fire),
            range: PetCasterBrain.ResolveRange(CasterSpellKind.Harmful, 4.5f, 0.0f, 600, Player.MaxRadarRange_Outdoors));

        /// <summary>A profile carrying a fire vulnerability at 2.5 in its own category.</summary>
        private static VulnerabilityProfile FireVulned() => new VulnerabilityProfile(1, 1, 1, 1, 2.5f, 1, 1, 0,
            new Dictionary<SpellCategory, CarriedEnchantment>
            {
                [SpellCategory.FireVulnerability] = new CarriedEnchantment(VulnType, (uint)PropertyFloat.ResistFire, 2.5f, 300),
            });

        private static CasterContext Ctx(CasterVitals owner, float ownerDistance = 3.0f, IEnumerable<CasterSpell> spells = null,
            IEnumerable<CasterAlly> siblings = null, IEnumerable<float> enemies = null,
            Func<int, VulnerabilityProfile> profile = null, IReadOnlyDictionary<SpellCategory, CarriedEnchantment> ownerLayers = null,
            IReadOnlyDictionary<SpellCategory, CarriedEnchantment> petLayers = null)
        {
            return new CasterContext
            {
                OwnerAvailable = true,
                Owner = new CasterAlly(owner, ownerDistance),
                Spells = (spells ?? Enumerable.Empty<CasterSpell>()).ToList(),
                Siblings = (siblings ?? Enumerable.Empty<CasterAlly>()).ToList(),
                EnemyDistances = (enemies ?? Enumerable.Empty<float>()).ToList(),
                EnemyProfile = profile ?? (_ => VulnerabilityProfile.None),
                OwnerTopLayers = () => ownerLayers,
                PetTopLayers = () => petLayers,
            };
        }

        private static readonly CasterVitals Full = new CasterVitals(1.0f, 1.0f, 1.0f);

        // ---- heal priority -----------------------------------------------------------------------------------

        [TestMethod]
        public void HealOwnerFirst()
        {
            // the owner below 75% is healed even with a lower sibling, a missing buff and an enemy in range
            var ctx = Ctx(new CasterVitals(0.5f), spells: new[] { Buff(), HealSpell(), Bolt() },
                siblings: new[] { new CasterAlly(new CasterVitals(0.1f), 2.0f) }, enemies: new[] { 10.0f });

            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void HealOwner_ThresholdIsStrictlyBelow75Percent()
        {
            var atThreshold = Ctx(new CasterVitals(PetCasterBrain.HealThreshold), spells: new[] { HealSpell() });
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(atThreshold), "exactly 75% is not below 75%");

            var justBelow = Ctx(new CasterVitals(0.74f), spells: new[] { HealSpell() });
            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Owner), PetCasterBrain.Decide(justBelow));
        }

        [TestMethod]
        public void HealOwner_OutOfRange_MovesToTheOwner()
        {
            var ctx = Ctx(new CasterVitals(0.3f), ownerDistance: 40.0f, spells: new[] { HealSpell(range: 25.0f) });

            Assert.AreEqual(CasterDecision.MoveInRange(Heal, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void RevitalizeCountsAsAHealForItsOwnVital()
        {
            var tired = Ctx(new CasterVitals(1.0f, 0.5f, 1.0f), spells: new[] { RevitalizeSpell() });
            Assert.AreEqual(CasterDecision.Cast(Revitalize, CasterTargetKind.Owner), PetCasterBrain.Decide(tired));

            // low health does not make a stamina spell a heal
            var hurtNotTired = Ctx(new CasterVitals(0.2f, 1.0f, 1.0f), spells: new[] { RevitalizeSpell() });
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(hurtNotTired));

            // health first when both are low and both heals are bound
            var both = Ctx(new CasterVitals(0.6f, 0.1f, 1.0f), spells: new[] { RevitalizeSpell(), HealSpell() });
            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Owner), PetCasterBrain.Decide(both));
        }

        [TestMethod]
        public void HealSiblingWhenOwnerHealthy()
        {
            var ctx = Ctx(new CasterVitals(0.9f), spells: new[] { HealSpell(), Bolt() },
                siblings: new[] { new CasterAlly(new CasterVitals(0.7f), 5.0f), new CasterAlly(new CasterVitals(0.4f), 6.0f) },
                enemies: new[] { 10.0f });

            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Sibling, 1), PetCasterBrain.Decide(ctx), "the LOWEST sibling below 75%");
        }

        [TestMethod]
        public void HealSibling_OutOfRange_MovesInRangeOfIt()
        {
            // ruling 2026-11-02: the lowest sibling below 75% is closed on when out of range, not skipped - even
            // with a healthy sibling standing in range
            var ctx = Ctx(Full, spells: new[] { HealSpell(range: 20.0f) },
                siblings: new[] { new CasterAlly(new CasterVitals(0.8f), 3.0f), new CasterAlly(new CasterVitals(0.1f), 35.0f) });

            Assert.AreEqual(CasterDecision.MoveInRange(Heal, CasterTargetKind.Sibling, 1), PetCasterBrain.Decide(ctx));

            var healthy = Ctx(Full, spells: new[] { HealSpell(range: 20.0f) },
                siblings: new[] { new CasterAlly(new CasterVitals(0.8f), 3.0f) });
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(healthy), "a sibling at or above 75% is left alone");
        }

        // ---- buffs -----------------------------------------------------------------------------------------

        [TestMethod]
        public void BuffOwner_WhenTheCategoryIsMissingOrWeaker()
        {
            var missing = Ctx(Full, spells: new[] { Buff(300) });
            Assert.AreEqual(CasterDecision.Cast(StrengthBuff, CasterTargetKind.Owner), PetCasterBrain.Decide(missing));

            var weaker = Ctx(Full, spells: new[] { Buff(300) }, ownerLayers: Layer(SpellCategory.StrengthRaising, 250));
            Assert.AreEqual(CasterDecision.Cast(StrengthBuff, CasterTargetKind.Owner), PetCasterBrain.Decide(weaker), "a weaker buff in the category is surpassed");
        }

        [TestMethod]
        public void SkipRedundantBuff()
        {
            var equal = Ctx(Full, spells: new[] { Buff(300) }, ownerLayers: Layer(SpellCategory.StrengthRaising, 300));
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(equal), "equal power is skipped");

            var stronger = Ctx(Full, spells: new[] { Buff(300) }, ownerLayers: Layer(SpellCategory.StrengthRaising, 400));
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(stronger), "greater power is skipped");

            // a different category stacks in the engine, so it never blocks the buff
            var otherCategory = Ctx(Full, spells: new[] { Buff(300) }, ownerLayers: Layer(SpellCategory.EnduranceRaising, 400));
            Assert.AreEqual(CasterDecision.Cast(StrengthBuff, CasterTargetKind.Owner), PetCasterBrain.Decide(otherCategory));
        }

        [TestMethod]
        public void Buff_GoesToTheOwnerOnly_ThenTheHarmfulSpell()
        {
            // the owner already has it; the low sibling is NOT buffed - the harmful spell goes out instead
            var ctx = Ctx(Full, spells: new[] { Buff(300), Bolt() }, ownerLayers: Layer(SpellCategory.StrengthRaising, 300),
                siblings: new[] { new CasterAlly(new CasterVitals(0.9f), 2.0f) }, enemies: new[] { 10.0f });

            Assert.AreEqual(CasterDecision.Cast(FlameBolt, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(ctx));
        }

        private static IReadOnlyDictionary<SpellCategory, CarriedEnchantment> Layer(SpellCategory category, uint power) =>
            new Dictionary<SpellCategory, CarriedEnchantment> { [category] = new CarriedEnchantment(EnchantmentTypeFlags.Attribute, 1, 35.0f, power) };

        // ---- harmful spells ----------------------------------------------------------------------------------

        [TestMethod]
        public void SkipRedundantDebuff_PicksNextTarget()
        {
            var asked = new List<int>();

            var ctx = Ctx(Full, spells: new[] { FireVulnSpell() }, enemies: new[] { 8.0f, 12.0f },
                profile: i => { asked.Add(i); return i == 0 ? FireVulned() : VulnerabilityProfile.None; });

            Assert.AreEqual(CasterDecision.Cast(FireVuln, CasterTargetKind.Enemy, 1), PetCasterBrain.Decide(ctx),
                "the current target already carries the vuln at equal strength, so the next engaged enemy gets it");
            CollectionAssert.AreEqual(new[] { 0, 1 }, asked, "each enemy's debuffs are read at most once, in preference order");
        }

        [TestMethod]
        public void RedundantDebuffOnEveryEnemy_Follows()
        {
            var ctx = Ctx(Full, spells: new[] { FireVulnSpell() }, enemies: new[] { 8.0f, 12.0f }, profile: _ => FireVulned());

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx), "never wastes a cast");
        }

        [TestMethod]
        public void RedundantDebuff_OtherBoundSpellStillHitsTheCurrentTarget()
        {
            // spell selection: whichever bound spell is useful now. The vuln is wasted on enemy 0, the bolt is not.
            var ctx = Ctx(Full, spells: new[] { FireVulnSpell(), Bolt() }, enemies: new[] { 8.0f, 12.0f }, profile: _ => FireVulned());

            Assert.AreEqual(CasterDecision.Cast(FlameBolt, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Harmful_PrefersTheSpellClassifiedPreferred()
        {
            // a fresh target: the vuln it lacks is Preferred, the bolt Neutral - book order does not matter
            var fresh = Ctx(Full, spells: new[] { Bolt(), ColdVulnSpell() }, enemies: new[] { 8.0f });
            Assert.AreEqual(CasterDecision.Cast(ColdVuln, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(fresh));

            // fire-vulned target: the fire bolt is promoted to Preferred, level with the cold vuln, so book order decides
            var vulned = Ctx(Full, spells: new[] { Bolt(), ColdVulnSpell() }, enemies: new[] { 8.0f }, profile: _ => FireVulned());
            Assert.AreEqual(CasterDecision.Cast(FlameBolt, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(vulned));
        }

        [TestMethod]
        public void IdleEnemyNeverChosen_NoEngagedEnemyMeansFollow()
        {
            // the brain only ever sees engaged enemies (CollectCasterEnemies, tested below); with none it follows
            var ctx = Ctx(Full, spells: new[] { Bolt(), FireVulnSpell() });

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void RingSpellMovesInRange()
        {
            Assert.AreEqual(4.5f, Ring().Range, "a ring's formula range is its engine reach (BaseRangeConstant, mod 0)");

            var far = Ctx(Full, spells: new[] { Ring() }, enemies: new[] { 6.0f });
            Assert.AreEqual(CasterDecision.MoveInRange(FlameRing, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(far), "6 m is outside the ring's 4.5 m reach");

            var close = Ctx(Full, spells: new[] { Ring() }, enemies: new[] { 4.0f });
            Assert.AreEqual(CasterDecision.Cast(FlameRing, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(close), "4 m is inside it");
        }

        // ---- mana (fix 1) ------------------------------------------------------------------------------------

        [TestMethod]
        public void UnaffordableHeal_GivesFollow_AndACheaperSpellStillGoesOut()
        {
            var heal = new CasterSpell(Heal, CasterSpellKind.Heal, CasterVital.Health, range: 30.0f, manaCost: 50);

            var dry = Ctx(new CasterVitals(0.2f), spells: new[] { heal });
            dry.PetMana = 10;
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(dry), "a heal the pet cannot pay for is never chosen");

            var cheapBolt = new CasterSpell(FlameBolt, CasterSpellKind.Harmful, shape: MonsterSpellShape.FromSpellFields(0, 0, 0.0f, DamageType.Fire), range: 30.0f, manaCost: 5);
            var fallsThrough = Ctx(new CasterVitals(0.2f), spells: new[] { heal, cheapBolt }, enemies: new[] { 8.0f });
            fallsThrough.PetMana = 10;
            Assert.AreEqual(CasterDecision.Cast(FlameBolt, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(fallsThrough), "falls through to the spell it can pay for");

            var paid = Ctx(new CasterVitals(0.2f), spells: new[] { heal });
            paid.PetMana = 50;
            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Owner), PetCasterBrain.Decide(paid), "exactly enough mana casts");
        }

        // ---- line of sight (fix 3) ----------------------------------------------------------------------------

        private static CasterSpell ProjectileBolt() => new CasterSpell(FlameBolt, CasterSpellKind.Harmful,
            shape: MonsterSpellShape.FromSpellFields(0, 0, 0.0f, DamageType.Fire), range: 30.0f, isProjectile: true);

        [TestMethod]
        public void NoLineOfSight_ProjectileIsUnusableOnThatEnemy()
        {
            var asked = new List<int>();

            // blind on enemy 0: the bolt is skipped there, so the next enemy (in sight) gets it
            var ctx = Ctx(Full, spells: new[] { ProjectileBolt() }, enemies: new[] { 8.0f, 12.0f });
            ctx.EnemyHasLos = i => { asked.Add(i); return i != 0; };
            Assert.AreEqual(CasterDecision.Cast(FlameBolt, CasterTargetKind.Enemy, 1), PetCasterBrain.Decide(ctx));
            CollectionAssert.AreEqual(new[] { 0, 1 }, asked, "line of sight is asked once per enemy");

            // a non-projectile spell is unaffected: it still goes to the blind current target
            var withVuln = Ctx(Full, spells: new[] { ProjectileBolt(), FireVulnSpell() }, enemies: new[] { 8.0f });
            withVuln.EnemyHasLos = _ => false;
            Assert.AreEqual(CasterDecision.Cast(FireVuln, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(withVuln), "falls through to the next spell");

            var blind = Ctx(Full, spells: new[] { ProjectileBolt() }, enemies: new[] { 8.0f, 12.0f });
            blind.EnemyHasLos = _ => false;
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(blind), "no enemy in sight: follow");
        }

        // ---- assist pick vs brain (fix 2) -----------------------------------------------------------------------

        [TestMethod]
        public void AssistPick_KeepsTheEnemyTheCasterBrainChose()
        {
            // assist on, assist target A valid and not current, current target B valid, nothing animating
            Assert.AreEqual(PetTargetAction.SwitchToAssist, PetAssistTargeting.Decide(true, true, false, true, false),
                "control: today's rule switches to the assist target");
            Assert.AreEqual(PetTargetAction.Keep, PetAssistTargeting.Decide(true, true, false, true, false, casterHoldsCurrent: true),
                "the caster's chosen enemy is kept");
            Assert.AreEqual(PetTargetAction.SwitchToAssist, PetAssistTargeting.Decide(true, true, false, false, false, casterHoldsCurrent: true),
                "an invalid current target is never held");

            var owner = Owner();
            var landblock = BareLandblock();
            var caster = CasterPet(owner);
            var b = Monster(owner, 55.0f, landblock);
            var a = Monster(owner, 56.0f, landblock);

            caster.SetLastCasterEnemyForTest(b);
            Assert.IsTrue(caster.CasterHoldsTarget(b, true), "the brain named B last tick and B is still valid");
            Assert.IsFalse(caster.CasterHoldsTarget(a, true), "a different enemy is not held");
            Assert.IsFalse(caster.CasterHoldsTarget(b, false), "B no longer valid (dead, out of view, or disengaged)");

            caster.SetLastCasterEnemyForTest(null);
            Assert.IsFalse(caster.CasterHoldsTarget(b, true), "no enemy named (the brain followed or healed)");

            var plain = CasterPet(owner, casterFlag: false);
            plain.SetLastCasterEnemyForTest(b);
            Assert.IsFalse(plain.CasterHoldsTarget(b, true), "only a caster pet holds");
        }

        [TestMethod]
        public void InRangeSpellBeatsMovingForAnOutOfRangeOne()
        {
            var ctx = Ctx(Full, spells: new[] { Ring(), Bolt() }, enemies: new[] { 12.0f });

            Assert.AreEqual(CasterDecision.Cast(FlameBolt, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(ctx));
        }

        // ---- Self spells are aimed at the owner ----------------------------------------------------------------

        /// <summary>A Self spell's CasterSpell as BuildCasterSpells makes it: classified, then ranged (Self spells are 0 / 0).</summary>
        private static CasterSpell SelfSpell(int id, SpellType type, DamageType vital, SpellCategory category = SpellCategory.Undef, uint power = 0)
        {
            var kind = PetCasterBrain.ClassifySpell(type, true, true, type == SpellType.Boost ? 100 : 0, vital, DispelType.All, out var casterVital);
            var range = PetCasterBrain.ResolveRange(kind, 0.0f, 0.0f, 400, Player.MaxRadarRange_Outdoors);
            return new CasterSpell(id, kind, casterVital, category, power, default, range);
        }

        [TestMethod]
        public void SelfHeal_HealsTheOwnerBelow75Percent()
        {
            var healSelf = SelfSpell(Heal, SpellType.Boost, DamageType.Health);

            var hurt = Ctx(new CasterVitals(0.5f), ownerDistance: 20.0f, spells: new[] { healSelf });
            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Owner), PetCasterBrain.Decide(hurt));

            var healthy = Ctx(new CasterVitals(0.9f), spells: new[] { healSelf });
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(healthy), "same 75% threshold as Heal Other");
        }

        [TestMethod]
        public void SelfBuff_TheOwnerLacks_BuffsTheOwner()
        {
            var strengthSelf = SelfSpell(StrengthBuff, SpellType.Enchantment, DamageType.Undef, SpellCategory.StrengthRaising, 300);

            var lacking = Ctx(Full, spells: new[] { strengthSelf });
            Assert.AreEqual(CasterDecision.Cast(StrengthBuff, CasterTargetKind.Owner), PetCasterBrain.Decide(lacking));

            var has = Ctx(Full, spells: new[] { strengthSelf }, ownerLayers: Layer(SpellCategory.StrengthRaising, 300));
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(has), "same equal-or-greater check as the Other buff");
        }

        // ---- owner unavailable ---------------------------------------------------------------------------------

        [TestMethod]
        public void OwnerUnavailable_ReturnsFollow()
        {
            var ctx = Ctx(new CasterVitals(0.1f), spells: new[] { HealSpell(), Buff(), Bolt() },
                siblings: new[] { new CasterAlly(new CasterVitals(0.1f), 2.0f) }, enemies: new[] { 5.0f },
                profile: _ => throw new AssertFailedException("an enemy profile was read for an unavailable owner"));

            ctx.OwnerAvailable = false;
            ctx.OwnerTopLayers = () => throw new AssertFailedException("the owner's enchantments were read while unavailable");

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(null));
        }

        [TestMethod]
        public void OwnerUnavailable_BuildCasterContextReadsNothing()
        {
            // world side: a pet with no owner stops at the guard - no spellbook, no visible-target read (this pet
            // has no PhysicsObj, so reaching either would throw)
            var pet = CasterPet(owner: null);

            var ctx = pet.BuildCasterContext();

            Assert.IsFalse(ctx.OwnerAvailable);
            Assert.AreEqual(0, ctx.Spells.Count);
            Assert.AreEqual(0, ctx.EnemyDistances.Count);
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void LazyReads_AHealNeverTouchesEnemyOrOwnerEnchantments()
        {
            var ctx = Ctx(new CasterVitals(0.2f), spells: new[] { HealSpell(), Buff(), Bolt() }, enemies: new[] { 5.0f },
                profile: _ => throw new AssertFailedException("enemy profile read before the heal decided"));
            ctx.OwnerTopLayers = () => throw new AssertFailedException("owner enchantments read before the heal decided");

            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        // ---- spell classification and range --------------------------------------------------------------------

        [TestMethod]
        public void ClassifySpell_Table()
        {
            Assert.AreEqual(CasterSpellKind.Heal, PetCasterBrain.ClassifySpell(SpellType.Boost, true, false, 100, DamageType.Health, DispelType.All, out var v1));
            Assert.AreEqual(CasterVital.Health, v1);
            Assert.AreEqual(CasterSpellKind.Heal, PetCasterBrain.ClassifySpell(SpellType.Boost, true, false, 100, DamageType.Stamina, DispelType.All, out var v2));
            Assert.AreEqual(CasterVital.Stamina, v2);
            Assert.AreEqual(CasterSpellKind.Heal, PetCasterBrain.ClassifySpell(SpellType.Boost, true, false, 100, DamageType.Mana, DispelType.All, out var v3));
            Assert.AreEqual(CasterVital.Mana, v3);

            Assert.AreEqual(CasterSpellKind.Harmful, PetCasterBrain.ClassifySpell(SpellType.Boost, false, false, -100, DamageType.Health, DispelType.All, out _), "Harm");
            Assert.AreEqual(CasterSpellKind.Buff, PetCasterBrain.ClassifySpell(SpellType.Enchantment, true, false, 0, DamageType.Undef, DispelType.All, out _));
            Assert.AreEqual(CasterSpellKind.Harmful, PetCasterBrain.ClassifySpell(SpellType.Enchantment, false, false, 0, DamageType.Undef, DispelType.All, out _), "a debuff");
            Assert.AreEqual(CasterSpellKind.Harmful, PetCasterBrain.ClassifySpell(SpellType.Projectile, false, false, 0, DamageType.Undef, DispelType.All, out _), "war/void");
            Assert.AreEqual(CasterSpellKind.Harmful, PetCasterBrain.ClassifySpell(SpellType.Transfer, false, false, 0, DamageType.Undef, DispelType.All, out _), "drain other");

            // a self-targeted BENEFICIAL spell is friendly, like its Other twin (ruling 2026-11-02); a self-targeted
            // harmful one is never cast
            Assert.AreEqual(CasterSpellKind.Heal, PetCasterBrain.ClassifySpell(SpellType.Boost, true, true, 100, DamageType.Health, DispelType.All, out var v4), "Heal Self");
            Assert.AreEqual(CasterVital.Health, v4);
            Assert.AreEqual(CasterSpellKind.Buff, PetCasterBrain.ClassifySpell(SpellType.Enchantment, true, true, 0, DamageType.Undef, DispelType.All, out _), "Strength Self");
            Assert.AreEqual(CasterSpellKind.Unusable, PetCasterBrain.ClassifySpell(SpellType.Enchantment, false, true, 0, DamageType.Undef, DispelType.All, out _), "a harmful self spell");
            Assert.AreEqual(CasterSpellKind.Unusable, PetCasterBrain.ClassifySpell(SpellType.Boost, false, true, -100, DamageType.Health, DispelType.All, out _), "a harmful self boost");
            Assert.AreEqual(CasterSpellKind.Unusable, PetCasterBrain.ClassifySpell(SpellType.Transfer, true, false, 0, DamageType.Undef, DispelType.All, out _), "a self-drain");
            Assert.AreEqual(CasterSpellKind.Unusable, PetCasterBrain.ClassifySpell(SpellType.PortalRecall, true, false, 0, DamageType.Undef, DispelType.All, out _));
        }

        // ---- dispels: Cleanse (owner / self) and Strip (enemy) ---------------------------------------------------

        private const int CleanseCreature = 40;
        private const int CleanseLife = 41;
        private const int CleanseAll = 42;
        private const int CleanseSelfCreature = 43;
        private const int StripAll = 44;
        private const int StripLife = 45;

        private static CasterSpell Cleanse(int id = CleanseCreature, MagicSchool school = MagicSchool.CreatureEnchantment, float range = 30.0f) =>
            new CasterSpell(id, CasterSpellKind.Cleanse, range: range, dispelSchool: school);

        private static CasterSpell CleanseSelfSpell(int id = CleanseSelfCreature, MagicSchool school = MagicSchool.CreatureEnchantment) =>
            new CasterSpell(id, CasterSpellKind.CleanseSelf, range: 75.0f, dispelSchool: school);

        private static CasterSpell Strip(int id = StripAll, MagicSchool school = MagicSchool.None, float range = 30.0f) =>
            new CasterSpell(id, CasterSpellKind.Strip, range: range, dispelSchool: school);

        private static IReadOnlyDictionary<SpellCategory, CarriedEnchantment> Layer(SpellCategory category, bool beneficial, MagicSchool school, bool dispellable = true, uint power = 100) =>
            new Dictionary<SpellCategory, CarriedEnchantment>
            {
                [category] = new CarriedEnchantment(VulnType, 0, 1.0f, power, 0.0, beneficial, school, dispellable),
            };

        private static VulnerabilityProfile ProfileWith(IReadOnlyDictionary<SpellCategory, CarriedEnchantment> layers) =>
            new VulnerabilityProfile(1, 1, 1, 1, 1, 1, 1, 0, layers);

        [TestMethod]
        public void ClassifySpell_DispelTable()
        {
            // the retail Other cleanse (4345 / 4336: align Negative, Beneficial flag set)
            Assert.AreEqual(CasterSpellKind.Cleanse, PetCasterBrain.ClassifySpell(SpellType.Dispel, true, false, 0, DamageType.Undef, DispelType.Negative, out _), "Other, Negative");

            // the retail Self cleanse (4346 / 4337: SelfTargeted, NOT Beneficial in the dat) - must not fall to the
            // harmful-self refusal
            Assert.AreEqual(CasterSpellKind.CleanseSelf, PetCasterBrain.ClassifySpell(SpellType.Dispel, false, true, 0, DamageType.Undef, DispelType.Negative, out _), "Self, Negative, not flagged beneficial");
            Assert.AreEqual(CasterSpellKind.CleanseSelf, PetCasterBrain.ClassifySpell(SpellType.Dispel, true, true, 0, DamageType.Undef, DispelType.Negative, out _), "Self, Negative, flagged beneficial");

            // strips: Other, Positive or All, whatever the Beneficial flag says
            Assert.AreEqual(CasterSpellKind.Strip, PetCasterBrain.ClassifySpell(SpellType.Dispel, false, false, 0, DamageType.Undef, DispelType.Positive, out _), "Other, Positive");
            Assert.AreEqual(CasterSpellKind.Strip, PetCasterBrain.ClassifySpell(SpellType.Dispel, false, false, 0, DamageType.Undef, DispelType.All, out _), "Other, All");
            Assert.AreEqual(CasterSpellKind.Strip, PetCasterBrain.ClassifySpell(SpellType.Dispel, true, false, 0, DamageType.Undef, DispelType.All, out _), "Other, All, flagged beneficial");

            // a Self strip would strip the pet's own buffs
            Assert.AreEqual(CasterSpellKind.Unusable, PetCasterBrain.ClassifySpell(SpellType.Dispel, false, true, 0, DamageType.Undef, DispelType.Positive, out _), "Self, Positive");
            Assert.AreEqual(CasterSpellKind.Unusable, PetCasterBrain.ClassifySpell(SpellType.Dispel, false, true, 0, DamageType.Undef, DispelType.All, out _), "Self, All");

            Assert.AreEqual(CasterSpellKind.Cleanse, PetCasterBrain.ClassifySpell(SpellType.FellowDispel, true, false, 0, DamageType.Undef, DispelType.Negative, out _), "fellowship cleanse");
        }

        [TestMethod]
        public void ResolveRange_StripClosesInLikeAHarmfulSpell()
        {
            Assert.AreEqual(PetCasterBrain.UnboundedHarmfulReach, PetCasterBrain.ResolveRange(CasterSpellKind.Strip, 0.0f, 0.0f, 400, 75.0f));
            Assert.AreEqual(75.0f, PetCasterBrain.ResolveRange(CasterSpellKind.Cleanse, 0.0f, 0.0f, 400, 75.0f));
        }

        [TestMethod]
        public void Cleanse_ChosenWhenTheOwnerCarriesAHarmfulLayerInItsSchool()
        {
            var ctx = Ctx(Full, spells: new[] { Cleanse() },
                ownerLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment));

            Assert.AreEqual(CasterDecision.Cast(CleanseCreature, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_NotChosen_OnlyABeneficialLayer()
        {
            var ctx = Ctx(Full, spells: new[] { Cleanse() },
                ownerLayers: Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment));

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_NotChosen_HarmfulLayerInAnotherSchool()
        {
            var ctx = Ctx(Full, spells: new[] { Cleanse() },
                ownerLayers: Layer(SpellCategory.HealthLowering, false, MagicSchool.LifeMagic));

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_NotChosen_UndispellableHarmfulLayer()
        {
            // vitae and item entries (Duration -1) are never removed by a dispel
            var ctx = Ctx(Full, spells: new[] { Cleanse() },
                ownerLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment, dispellable: false));

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_NotChosen_UnresolvedLayerNeverMatches()
        {
            var unresolved = new Dictionary<SpellCategory, CarriedEnchantment>
            {
                [SpellCategory.StrengthLowering] = new CarriedEnchantment(VulnType, 0, 1.0f, 100),
            };

            var ctx = Ctx(Full, spells: new[] { Cleanse() }, ownerLayers: unresolved);

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_AllSchoolSpell_MatchesAnySchool()
        {
            var all = Cleanse(CleanseAll, MagicSchool.None);

            Assert.AreEqual(CasterDecision.Cast(CleanseAll, CasterTargetKind.Owner),
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { all }, ownerLayers: Layer(SpellCategory.HealthLowering, false, MagicSchool.LifeMagic))));

            Assert.AreEqual(CasterDecision.Cast(CleanseAll, CasterTargetKind.Owner),
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { all }, ownerLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment))));
        }

        [TestMethod]
        public void Cleanse_PicksTheSpellWhoseSchoolMatches()
        {
            var ctx = Ctx(Full, spells: new[] { Cleanse(CleanseCreature, MagicSchool.CreatureEnchantment), Cleanse(CleanseLife, MagicSchool.LifeMagic) },
                ownerLayers: Layer(SpellCategory.HealthLowering, false, MagicSchool.LifeMagic));

            Assert.AreEqual(CasterDecision.Cast(CleanseLife, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_HealStillBeatsIt()
        {
            var ctx = Ctx(new CasterVitals(0.4f), spells: new[] { Cleanse(), HealSpell() },
                ownerLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment));

            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_BeatsABuff()
        {
            var ctx = Ctx(Full, spells: new[] { Buff(), Cleanse() },
                ownerLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment));

            Assert.AreEqual(CasterDecision.Cast(CleanseCreature, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_NothingToCleanse_TheBuffGoesAhead()
        {
            var ctx = Ctx(Full, spells: new[] { Cleanse(), Buff() }, ownerLayers: null);

            Assert.AreEqual(CasterDecision.Cast(StrengthBuff, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_OwnerOutOfRange_MovesToTheOwner()
        {
            var ctx = Ctx(Full, ownerDistance: 40.0f, spells: new[] { Cleanse(range: 25.0f) },
                ownerLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment));

            Assert.AreEqual(CasterDecision.MoveInRange(CleanseCreature, CasterTargetKind.Owner), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_UnaffordableNeverReadsTheOwnerSnapshot()
        {
            var read = 0;
            var ctx = Ctx(Full, spells: new[] { new CasterSpell(CleanseCreature, CasterSpellKind.Cleanse, range: 30.0f, manaCost: 50, dispelSchool: MagicSchool.CreatureEnchantment) });
            ctx.PetMana = 10;
            ctx.OwnerTopLayers = () => { read++; return Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment); };

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
            Assert.AreEqual(0, read, "an unaffordable cleanse never clones the owner's registry");
        }

        [TestMethod]
        public void Cleanse_OwnerUnavailable_ReadsNothing()
        {
            var read = 0;
            var ctx = Ctx(Full, spells: new[] { Cleanse() });
            ctx.OwnerAvailable = false;
            ctx.OwnerTopLayers = () => { read++; return null; };

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
            Assert.AreEqual(0, read);
        }

        [TestMethod]
        public void CleanseSelf_CastOnThePetWhenThePetCarriesAHarmfulLayer()
        {
            var ctx = Ctx(Full, spells: new[] { CleanseSelfSpell() },
                petLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment));

            Assert.AreEqual(CasterDecision.Cast(CleanseSelfCreature, CasterTargetKind.Self), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void CleanseSelf_NotChosen_PetCleanOrWrongSchool_EvenIfTheOwnerIsAfflicted()
        {
            var afflictedOwner = Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment);

            Assert.AreEqual(CasterDecision.Follow,
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { CleanseSelfSpell() }, ownerLayers: afflictedOwner, petLayers: null)),
                "a Self cleanse is about the pet");

            Assert.AreEqual(CasterDecision.Follow,
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { CleanseSelfSpell() }, petLayers: Layer(SpellCategory.HealthLowering, false, MagicSchool.LifeMagic))),
                "wrong school");

            Assert.AreEqual(CasterDecision.Follow,
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { CleanseSelfSpell() }, petLayers: Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment))),
                "only a beneficial layer");
        }

        [TestMethod]
        public void CleanseSelf_HealStillBeatsIt_AndItBeatsABuff()
        {
            var petAfflicted = Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment);

            Assert.AreEqual(CasterDecision.Cast(Heal, CasterTargetKind.Owner),
                PetCasterBrain.Decide(Ctx(new CasterVitals(0.4f), spells: new[] { CleanseSelfSpell(), HealSpell() }, petLayers: petAfflicted)));

            Assert.AreEqual(CasterDecision.Cast(CleanseSelfCreature, CasterTargetKind.Self),
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { Buff(), CleanseSelfSpell() }, petLayers: petAfflicted)));
        }

        [TestMethod]
        public void Strip_ChosenOnAnEnemyCarryingABeneficialLayer()
        {
            var ctx = Ctx(Full, spells: new[] { Strip() }, enemies: new[] { 10.0f },
                profile: _ => ProfileWith(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment)));

            Assert.AreEqual(CasterDecision.Cast(StripAll, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Strip_RedundantOnAnEnemyWithNothingToStrip()
        {
            var harmfulOnly = Ctx(Full, spells: new[] { Strip() }, enemies: new[] { 10.0f },
                profile: _ => ProfileWith(Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment)));
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(harmfulOnly), "only a harmful layer");

            var bare = Ctx(Full, spells: new[] { Strip() }, enemies: new[] { 10.0f });
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(bare), "no layers at all");

            var wrongSchool = Ctx(Full, spells: new[] { Strip(StripLife, MagicSchool.LifeMagic) }, enemies: new[] { 10.0f },
                profile: _ => ProfileWith(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment)));
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(wrongSchool), "a Life strip against a Creature buff");

            var undispellable = Ctx(Full, spells: new[] { Strip() }, enemies: new[] { 10.0f },
                profile: _ => ProfileWith(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment, dispellable: false)));
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(undispellable), "an exempt or permanent buff");
        }

        [TestMethod]
        public void Strip_FallsThroughToTheNextEnemy()
        {
            var ctx = Ctx(Full, spells: new[] { Strip() }, enemies: new[] { 10.0f, 12.0f },
                profile: i => i == 1
                    ? ProfileWith(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment))
                    : VulnerabilityProfile.None);

            Assert.AreEqual(CasterDecision.Cast(StripAll, CasterTargetKind.Enemy, 1), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Strip_OutOfRange_ClosesIn()
        {
            var ctx = Ctx(Full, spells: new[] { Strip(range: 5.0f) }, enemies: new[] { 20.0f },
                profile: _ => ProfileWith(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment)));

            Assert.AreEqual(CasterDecision.MoveInRange(StripAll, CasterTargetKind.Enemy, 0), PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Strip_NeverTargetsTheOwnerOrASibling_EvenWithNoEnemies()
        {
            // the owner and the pet carry beneficial layers, which a strip must never be aimed at
            var buffed = Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment);

            var ctx = Ctx(Full, spells: new[] { Strip() }, ownerLayers: buffed, petLayers: buffed,
                siblings: new[] { new CasterAlly(new CasterVitals(0.1f), 2.0f) });

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Strip_NoHarmfulOrStripBound_NeverReadsAnEnemyProfile()
        {
            var read = 0;
            var ctx = Ctx(Full, spells: new[] { Cleanse() }, enemies: new[] { 10.0f }, profile: _ => { read++; return VulnerabilityProfile.None; });

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
            Assert.AreEqual(0, read, "a pet with no harmful spell never clones an enemy's registry");
        }

        [TestMethod]
        public void Strip_AgainstABolt_SpellbookOrderBreaksTheTie_AndABareEnemyGetsTheBolt()
        {
            var buffedEnemy = ProfileWith(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment));

            // a bolt is Neutral, the same tier as a strip on a buffed enemy
            Assert.AreEqual(CasterDecision.Cast(StripAll, CasterTargetKind.Enemy, 0),
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { Strip(), Bolt() }, enemies: new[] { 10.0f }, profile: _ => buffedEnemy)));

            Assert.AreEqual(CasterDecision.Cast(FlameBolt, CasterTargetKind.Enemy, 0),
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { Bolt(), Strip() }, enemies: new[] { 10.0f }, profile: _ => buffedEnemy)));

            // a strip with nothing to strip never blocks the bolt
            Assert.AreEqual(CasterDecision.Cast(FlameBolt, CasterTargetKind.Enemy, 0),
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { Strip(), Bolt() }, enemies: new[] { 10.0f })));
        }

        [TestMethod]
        public void BuildTopLayers_CarriesTheResolvedDispelFacts()
        {
            var entries = new List<PropertiesEnchantmentRegistry>
            {
                new PropertiesEnchantmentRegistry { SpellId = 11, SpellCategory = SpellCategory.StrengthRaising, PowerLevel = 100, StatModType = VulnType },
                new PropertiesEnchantmentRegistry { SpellId = 12, SpellCategory = SpellCategory.StrengthLowering, PowerLevel = 100, StatModType = VulnType },
                new PropertiesEnchantmentRegistry { SpellId = 13, SpellCategory = SpellCategory.HealthLowering, PowerLevel = 100, StatModType = VulnType },
            };

            EnchantmentDispelFacts? Resolve(PropertiesEnchantmentRegistry e) =>
                e.SpellId == 11 ? new EnchantmentDispelFacts(true, MagicSchool.CreatureEnchantment, true)
                : e.SpellId == 12 ? new EnchantmentDispelFacts(false, MagicSchool.CreatureEnchantment, false)
                : (EnchantmentDispelFacts?)null;

            var layers = VulnerabilityProfile.BuildTopLayers(entries, Resolve);

            Assert.IsTrue(layers[SpellCategory.StrengthRaising].IsDispellableBy(true, MagicSchool.None));
            Assert.IsFalse(layers[SpellCategory.StrengthRaising].IsDispellableBy(false, MagicSchool.None), "a beneficial layer is not a cleanse target");
            Assert.IsFalse(layers[SpellCategory.StrengthLowering].IsDispellableBy(false, MagicSchool.None), "not dispellable");
            Assert.IsFalse(layers[SpellCategory.HealthLowering].IsDispellableBy(false, MagicSchool.None), "unresolved never matches");
            Assert.IsFalse(layers[SpellCategory.HealthLowering].IsDispellableBy(true, MagicSchool.None), "unresolved never matches");

            // the one-argument overload keeps every layer unresolved
            Assert.IsFalse(VulnerabilityProfile.BuildTopLayers(entries)[SpellCategory.StrengthRaising].IsBeneficial.HasValue);
        }

        // ---- the dispel power window (SelectDispel: min_power <= PowerLevel <= max_power, before school and align) ----
        //
        // Real data: every Incantation of Nullify spell (ids 4330-4350) has min_Power 0 and max_Power 435
        // (local ace_world: SELECT id,name,dispel_School,align,number,max_Power FROM spell WHERE name LIKE
        // 'Incantation of Nullify%'). The window is carried per spell, so a spell with a narrower one is judged by it.

        private const int NullifyMinPower = 0;
        private const int NullifyMaxPower = 435;

        [DataTestMethod]
        [DataRow(100u, true)]
        [DataRow(0u, true)]
        [DataRow(435u, true)]
        [DataRow(436u, false)]
        [DataRow(500u, false)]
        public void Cleanse_OnlyALayerInsideThePowerWindowIsCleansed(uint layerPower, bool chosen)
        {
            var spell = new CasterSpell(CleanseCreature, CasterSpellKind.Cleanse, range: 30.0f, dispelSchool: MagicSchool.CreatureEnchantment, minPower: NullifyMinPower, maxPower: NullifyMaxPower);
            var ctx = Ctx(Full, spells: new[] { spell }, ownerLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment, power: layerPower));

            Assert.AreEqual(chosen ? CasterDecision.Cast(CleanseCreature, CasterTargetKind.Owner) : CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void Cleanse_LayerBelowMinPower_IsNotChosen()
        {
            var spell = new CasterSpell(CleanseCreature, CasterSpellKind.Cleanse, range: 30.0f, dispelSchool: MagicSchool.CreatureEnchantment, minPower: 200, maxPower: 435);
            var ctx = Ctx(Full, spells: new[] { spell }, ownerLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment, power: 100));

            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));
        }

        [TestMethod]
        public void CleanseSelf_LayerAboveMaxPower_IsNotChosen()
        {
            var spell = new CasterSpell(CleanseSelfCreature, CasterSpellKind.CleanseSelf, range: 75.0f, dispelSchool: MagicSchool.CreatureEnchantment, minPower: NullifyMinPower, maxPower: NullifyMaxPower);

            Assert.AreEqual(CasterDecision.Follow,
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { spell }, petLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment, power: 500))));

            Assert.AreEqual(CasterDecision.Cast(CleanseSelfCreature, CasterTargetKind.Self),
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { spell }, petLayers: Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment, power: 300))));
        }

        [TestMethod]
        public void Strip_EnemyLayerAboveMaxPower_IsRedundant()
        {
            var spell = new CasterSpell(StripAll, CasterSpellKind.Strip, range: 30.0f, minPower: NullifyMinPower, maxPower: NullifyMaxPower);

            Assert.AreEqual(CasterDecision.Follow,
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { spell }, enemies: new[] { 10.0f },
                    profile: _ => ProfileWith(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment, power: 500)))));

            Assert.AreEqual(CasterDecision.Cast(StripAll, CasterTargetKind.Enemy, 0),
                PetCasterBrain.Decide(Ctx(Full, spells: new[] { spell }, enemies: new[] { 10.0f },
                    profile: _ => ProfileWith(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment, power: 400)))));
        }

        // ---- a cleanse already in its pre-cast is dropped when its target was cleaned meanwhile ----------------

        [TestMethod]
        public void CleanseStillNeeded_Table()
        {
            var afflicted = Layer(SpellCategory.StrengthLowering, false, MagicSchool.CreatureEnchantment, power: 300);

            Assert.IsTrue(PetCasterBrain.CleanseStillNeeded(afflicted, MagicSchool.CreatureEnchantment, NullifyMinPower, NullifyMaxPower));
            Assert.IsTrue(PetCasterBrain.CleanseStillNeeded(afflicted, MagicSchool.None, NullifyMinPower, NullifyMaxPower), "an all-school cleanse");

            Assert.IsFalse(PetCasterBrain.CleanseStillNeeded(null, MagicSchool.None, NullifyMinPower, NullifyMaxPower), "the other pet already cleansed it");
            Assert.IsFalse(PetCasterBrain.CleanseStillNeeded(Layer(SpellCategory.StrengthRaising, true, MagicSchool.CreatureEnchantment), MagicSchool.None, NullifyMinPower, NullifyMaxPower), "only a beneficial layer is left");
            Assert.IsFalse(PetCasterBrain.CleanseStillNeeded(afflicted, MagicSchool.LifeMagic, NullifyMinPower, NullifyMaxPower), "wrong school");
            Assert.IsFalse(PetCasterBrain.CleanseStillNeeded(afflicted, MagicSchool.CreatureEnchantment, NullifyMinPower, 200), "above the power window");
        }

        [TestMethod]
        public void ResolveRange_FormulaCapAndUnbounded()
        {
            Assert.AreEqual(5.0f + 600 * 0.05f, PetCasterBrain.ResolveRange(CasterSpellKind.Harmful, 5.0f, 0.05f, 600, 75.0f), 1e-4f);
            Assert.AreEqual(75.0f, PetCasterBrain.ResolveRange(CasterSpellKind.Harmful, 5.0f, 1.0f, 600, 75.0f), "capped at the radar range");
            Assert.AreEqual(75.0f, PetCasterBrain.ResolveRange(CasterSpellKind.Heal, 0.0f, 0.0f, 600, 75.0f), "an unbounded friendly (Self) spell reaches what its Other twin does: the radar cap");
            Assert.AreEqual(75.0f, PetCasterBrain.ResolveRange(CasterSpellKind.Buff, 0.0f, 0.0f, 400, 75.0f));
            Assert.AreEqual(PetCasterBrain.UnboundedHarmfulReach, PetCasterBrain.ResolveRange(CasterSpellKind.Harmful, 0.0f, 0.0f, 600, 75.0f));
        }

        // ---- the world seams ------------------------------------------------------------------------------------

        private static Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> Vitals() =>
            new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
            {
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 },
                [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 100 },
            };

        private static Player Owner() => (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

        private static Position At(float x) => new Position(0x00010064, x, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

        private static Landblock BareLandblock() => (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        private static CombatPet CasterPet(Player owner, bool casterFlag = true, bool usesMana = true)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Caster" } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesAttribute2nd = Vitals(),
            };

            if (casterFlag)
                weenie.PropertiesBool[PropertyBool.SummonCasterBrain] = true;

            if (!usesMana)
                weenie.PropertiesBool[PropertyBool.AiUsesMana] = false;

            var pet = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            pet.P_PetOwner = owner;
            pet.Location = At(50.0f);
            return pet;
        }

        private static Creature Monster(WorldObject attackTarget, float x, Landblock landblock)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Monster" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = Vitals(),
            };

            var monster = new Creature(weenie, new ObjectGuid(nextGuid++));
            monster.Location = At(x);
            monster.AttackTarget = attackTarget;
            monster.MonsterState = attackTarget == null ? Creature.State.Idle : Creature.State.Awake;
            monster.IsAwake = attackTarget != null;
            monster.CurrentLandblock = landblock;
            return monster;
        }

        [TestMethod]
        public void IdleEnemyNeverChosen_EnemyListHoldsOnlyEngagedMonsters_CurrentTargetFirst()
        {
            var owner = Owner();
            var landblock = BareLandblock();

            var pet = CasterPet(owner);
            pet.CurrentLandblock = landblock;

            var idle = Monster(null, 51.0f, landblock);
            var strangers = Monster(Owner(), 52.0f, landblock);
            var nearEngaged = Monster(owner, 54.0f, landblock);
            var farEngagedOnPet = Monster(pet, 58.0f, landblock);
            var current = Monster(owner, 60.0f, landblock);
            var elsewhere = Monster(owner, 53.0f, null);   // not in the pet's landblock group: never listed

            pet.AttackTarget = current;

            var enemies = new List<Creature>();
            var distances = new List<float>();

            pet.CollectCasterEnemies(new[] { idle, strangers, farEngagedOnPet, current, nearEngaged, elsewhere },
                wo => (float)pet.Location.DistanceTo(wo.Location), enemies, distances);

            CollectionAssert.AreEqual(new[] { current, nearEngaged, farEngagedOnPet }, enemies,
                "engaged only (idle and another player's fight excluded), current target first, then nearest first");
            Assert.AreEqual(10.0f, distances[0], 1e-3f);
            Assert.AreEqual(4.0f, distances[1], 1e-3f);
            Assert.AreEqual(8.0f, distances[2], 1e-3f);
        }

        [TestMethod]
        public void IdleEnemyNeverChosen_EvenWhenItIsCloser()
        {
            var owner = Owner();
            var landblock = BareLandblock();

            var pet = CasterPet(owner);
            pet.CurrentLandblock = landblock;

            var idle = Monster(null, 51.0f, landblock);       // 1 m away
            var engaged = Monster(owner, 60.0f, landblock);   // 10 m away
            var beyondChase = Monster(owner, 50.0f + Creature.MaxChaseRange + 1.0f, landblock);

            var enemies = new List<Creature>();
            var distances = new List<float>();

            pet.CollectCasterEnemies(new[] { idle, beyondChase, engaged }, wo => (float)pet.Location.DistanceTo(wo.Location), enemies, distances);

            CollectionAssert.AreEqual(new[] { engaged }, enemies, "the idle monster and the one past MaxChaseRange are not listed");

            var ctx = Ctx(Full, spells: new[] { Bolt() }, enemies: distances);
            var decision = PetCasterBrain.Decide(ctx);

            Assert.AreEqual(CasterAction.Cast, decision.Action);
            Assert.AreSame(engaged, enemies[decision.TargetIndex], "the engaged monster is the one cast at, not the closer idle one");
        }

        [TestMethod]
        public void BuildCasterContext_CarriesThePetsMana_SoADryPetFollows()
        {
            var pet = CasterPet(owner: null);
            Assert.IsTrue(pet.Mana.MaxValue >= 50, "harness: the pet needs a mana pool");

            pet.Mana.Current = 0;
            var ctx = pet.BuildCasterContext();
            Assert.AreEqual(0u, ctx.PetMana, "the brain sees the pet's real mana");

            // the owner half cannot be read here (see TickCaster_NoOwner below), so it is filled by hand: with the
            // pet's own mana, an owner below 75% and a heal costing 10, the brain follows
            ctx.OwnerAvailable = true;
            ctx.Owner = new CasterAlly(new CasterVitals(0.2f), 3.0f);
            ctx.Spells = new List<CasterSpell> { new CasterSpell(Heal, CasterSpellKind.Heal, range: 30.0f, manaCost: 10) };
            Assert.AreEqual(CasterDecision.Follow, PetCasterBrain.Decide(ctx));

            pet.Mana.Current = 50;
            Assert.AreEqual(50u, pet.BuildCasterContext().PetMana);

            var noMana = CasterPet(owner: null, usesMana: false);
            Assert.AreEqual(uint.MaxValue, noMana.BuildCasterContext().PetMana, "a pet that does not spend mana is never limited");
        }

        /// <summary>
        /// The real TickCaster entry, end to end. HARNESS LIMIT: only the owner-unavailable path can run here. An
        /// available owner must pass CanFollowOwner (a Player with a PhysicsObj and a landblock) and IsDead (a
        /// Player with vitals), and a Player cannot be constructed in this assembly; seeding one by reflection is
        /// the FieldInfo.SetValue-on-Player pattern that poisons the test process. The decision logic past that
        /// guard is covered by the Decide tables, and the cast core by the Delivery tests.
        /// </summary>
        [TestMethod]
        public void TickCaster_NoOwner_FollowsAndDropsAPendingHarmfulApproach()
        {
            var pet = CasterPet(owner: null);
            pet.SetCasterSpellsForTest(new List<CasterSpell> { Bolt() });

            // as if the previous tick had been chasing an enemy
            pet.CurrentAttack = CombatType.Magic;
            pet.MaxRange = 30.0f;

            pet.TickCaster();

            Assert.AreEqual(CasterDecision.Follow, pet.LastCasterDecision);
            Assert.IsNull(pet.CurrentAttack, "the chase state is cleared, so no stale spell or range leaks into a later tick");
            Assert.AreEqual(0.0f, pet.MaxRange);
        }

        [TestMethod]
        public void IsCasterPet_FromTheWeenieFlagOrABoundSpell_AndACasterNeverInitiates()
        {
            var flagged = CasterPet(Owner());
            Assert.IsTrue(flagged.IsCasterPet);
            Assert.IsTrue(flagged.NeverInitiates, "a caster never starts a fight even without SummonNeverInitiates");

            var plain = CasterPet(Owner(), casterFlag: false);
            Assert.IsFalse(plain.IsCasterPet);
            Assert.IsFalse(plain.NeverInitiates);

            plain.ApplySummonStats(new PetSummonSource(1, null, null, null, null, null, null, null, 2101), 1.0f, 1.0f);
            Assert.IsTrue(plain.IsCasterPet, "a bound spell puts the pet on the caster brain");
            Assert.IsTrue(plain.NeverInitiates);

            var retail = CasterPet(Owner(), casterFlag: false);
            retail.ApplySummonStats(new PetSummonSource(1, null, null, null, null, null, null, null, null), 1.0f, 1.0f);
            Assert.IsFalse(retail.IsCasterPet, "a retail source stays off the caster brain");
        }

        [TestMethod]
        public void Pin_MonsterTick_CasterBranchReturnsBeforeTheAttackBranch()
        {
            // a caster never reaches GetNextAttackType (whose failed spell roll falls back to melee) because its
            // branch returns first. Pinned on source because Monster_Tick needs a live physics object.
            var tick = File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Monster_Tick.cs"));

            var branch = tick.IndexOf("if (this is CombatPet casterPet && casterPet.IsCasterPet && MonsterState != State.Return)", StringComparison.Ordinal);
            var call = tick.IndexOf("casterPet.TickCaster();", StringComparison.Ordinal);
            var attackType = tick.IndexOf("CurrentAttack = GetNextAttackType();", StringComparison.Ordinal);
            var findTarget = tick.IndexOf("HandleFindTarget();", StringComparison.Ordinal);

            Assert.IsTrue(branch > findTarget && findTarget >= 0, "the caster branch runs after target acquisition");
            Assert.IsTrue(call > branch, "and calls the caster tick");
            Assert.IsTrue(attackType > call, "before the attack branch that could choose melee");
            StringAssert.Contains(tick.Substring(call, attackType - call), "return;", "and returns from it");
        }

        // ---- delivery: a heal through the real cast core --------------------------------------------------------

        [TestMethod]
        public void Delivery_PetHealsItsSiblingThroughCastSpellAt()
        {
            Assert.IsFalse(LandblockManager.CurrentlyTickingLandblockGroupsMultiThreaded, "harness: no parallel tick runs in tests");

            var owner = Owner();
            var landblock = BareLandblock();

            var caster = CasterPet(owner);
            caster.CurrentLandblock = landblock;

            var sibling = CasterPet(owner, casterFlag: false);
            sibling.CurrentLandblock = landblock;

            var max = sibling.Health.MaxValue;
            Assert.IsTrue(max > 40, "harness: the sibling needs room to be healed");

            sibling.Health.Current = 10;

            caster.CastSpellAt(FakeHeal(25), sibling, friendly: true);

            Assert.AreEqual(35u, sibling.Health.Current, "Heal Other's boost landed on the sibling through HandleCastSpell's Boost path");

            // control: the cross-landblock-group guard still refuses a target outside the caster's group
            var away = CasterPet(owner, casterFlag: false);
            away.CurrentLandblock = null;
            away.Health.Current = 10;

            caster.CastSpellAt(FakeHeal(25), away, friendly: true);

            Assert.AreEqual(10u, away.Health.Current, "a target with no landblock is never written");
        }

        [TestMethod]
        public void Delivery_ASelfHealIsAimedAtTheExplicitTarget()
        {
            var owner = Owner();
            var landblock = BareLandblock();

            var caster = CasterPet(owner);
            caster.CurrentLandblock = landblock;
            caster.Health.Current = 10;

            var sibling = CasterPet(owner, casterFlag: false);
            sibling.CurrentLandblock = landblock;
            sibling.Health.Current = 10;

            caster.CastSpellAt(FakeHeal(25, selfTargeted: true), sibling, friendly: true);

            Assert.AreEqual(35u, sibling.Health.Current, "a friendly Heal Self lands on the explicit target");
            Assert.AreEqual(10u, caster.Health.Current, "and not on the caster");

            // control: without friendly the engine's own redirect still sends a Self spell to the caster
            caster.CastSpellAt(FakeHeal(25, selfTargeted: true), sibling, friendly: false);

            Assert.AreEqual(35u, caster.Health.Current, "a non-friendly Self spell is redirected onto the caster, as before");
            Assert.AreEqual(35u, sibling.Health.Current);
        }

        // ---- friendly fire and the heal re-check at release (2026-11-04) ----------------------------------------

        [TestMethod]
        public void CanDamage_ACombatPetNeverDamagesAPlayer()
        {
            var owner = Owner();
            var pet = CasterPet(owner);

            Assert.IsFalse(pet.CanDamage(owner), "a pet's ring/blast/volley colliding with its owner lands nothing");
            Assert.IsFalse(pet.CanDamage(Owner()), "nor on any other player");

            // nor on a friendly pet: the owner's other summon, or another player's pet
            Assert.IsFalse(pet.CanDamage(CasterPet(owner, casterFlag: false)), "the owner's other pet is never damaged");
            Assert.IsFalse(pet.CanDamage(CasterPet(Owner(), casterFlag: false)), "another player's pet is never damaged");

            // control: a monster still damages a player, and the pet still damages a monster
            var monster = Monster(null, 55.0f, BareLandblock());

            Assert.IsTrue(monster.CanDamage(owner), "a monster attacking a player is unchanged");
            Assert.IsTrue(pet.CanDamage(monster), "a pet attacking a monster is unchanged");
        }

        [DataTestMethod]
        [DataRow(0.10f, true)]
        [DataRow(0.749f, true)]
        [DataRow(0.75f, false)]
        [DataRow(1.0f, false)]
        public void HealStillNeeded_IsBelowTheHealThreshold(float health, bool expected)
        {
            Assert.AreEqual(expected, PetCasterBrain.HealStillNeeded(new CasterVitals(health), CasterVital.Health));
            Assert.AreEqual(expected, PetCasterBrain.HealStillNeeded(new CasterVitals(1.0f, stamina: health), CasterVital.Stamina), "per vital");
            Assert.IsFalse(PetCasterBrain.HealStillNeeded(new CasterVitals(health), CasterVital.Stamina), "a full vital never needs its heal");
        }

        [TestMethod]
        public void FriendlyCastStillWanted_DropsAHealOnceTheTargetIsToppedUp()
        {
            var owner = Owner();
            var landblock = BareLandblock();

            var caster = CasterPet(owner);
            caster.CurrentLandblock = landblock;

            var sibling = CasterPet(owner, casterFlag: false);
            sibling.CurrentLandblock = landblock;

            var max = sibling.Health.MaxValue;

            sibling.Health.Current = max / 10;
            Assert.IsTrue(caster.FriendlyCastStillWanted(FakeHeal(25), sibling), "still low at release: the heal goes out");

            sibling.Health.Current = max;
            Assert.IsFalse(caster.FriendlyCastStillWanted(FakeHeal(25), sibling), "topped up during the pre-cast: the heal is dropped");

            sibling.Health.Current = (uint)Math.Ceiling(max * PetCasterBrain.HealThreshold);
            Assert.IsFalse(caster.FriendlyCastStillWanted(FakeHeal(25), sibling), "at the threshold: not needed");

            // a target no longer on this pet's tick thread is never read, and never healed
            sibling.Health.Current = max / 10;
            sibling.CurrentLandblock = null;
            Assert.IsFalse(caster.FriendlyCastStillWanted(FakeHeal(25), sibling), "an unreadable target is not healed");
        }

        [TestMethod]
        public void FriendlyCastStillWanted_LeavesNonHealSpellsAlone()
        {
            var owner = Owner();
            var caster = CasterPet(owner);
            var sibling = CasterPet(owner, casterFlag: false);

            sibling.Health.Current = sibling.Health.MaxValue;

            // a harmful Boost (Harm) is not a Heal-kind spell: the re-check never applies to it
            Assert.IsTrue(caster.FriendlyCastStillWanted(FakeHeal(-25), sibling));
        }

        [TestMethod]
        public void TryStartExplicitCast_RechecksAFriendlyCastBeforeReleasingIt()
        {
            var source = File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Monster_Magic.cs")).Replace("\r\n", "\n");

            var method = source.IndexOf("internal bool TryStartExplicitCast(", StringComparison.Ordinal);
            Assert.IsTrue(method >= 0, "TryStartExplicitCast was not found");

            var recheck = source.IndexOf("FriendlyCastStillWanted(spell, target)", method, StringComparison.Ordinal);
            var release = source.IndexOf("CastSpellAt(spell, target, friendly);", method, StringComparison.Ordinal);

            Assert.IsTrue(recheck > method, "the release action re-checks a friendly cast");
            Assert.IsTrue(release > recheck, "before CastSpellAt releases it");

            var drop = source.Substring(recheck, release - recheck);
            StringAssert.Contains(drop, "CombatPet.RefundedMana(Mana.Current, Mana.MaxValue, manaUsed)", "a dropped heal refunds its mana");
            StringAssert.Contains(drop, "NextAttackTime = Timers.RunningTime", "and releases the post-cast cooldown");
        }

        [DataTestMethod]
        [DataRow(50u, 100u, 0u, 50u)]
        [DataRow(50u, 100u, 30u, 80u)]
        [DataRow(90u, 100u, 30u, 100u)]
        [DataRow(uint.MaxValue - 1, uint.MaxValue, 30u, uint.MaxValue)]
        public void RefundedMana_ReturnsTheCostCappedAtMax(uint current, uint max, uint used, uint expected)
        {
            Assert.AreEqual(expected, CombatPet.RefundedMana(current, max, used));
        }

        /// <summary>
        /// A faked Heal Other: Life Magic, Boost, beneficial (and therefore not resistable), targeted at a creature,
        /// no caster/target effect script. Built without DatManager (see the class remarks).
        /// </summary>
        private static Spell FakeHeal(int boost, bool selfTargeted = false)
        {
            var spellBase = (DatSpellBase)RuntimeHelpers.GetUninitializedObject(typeof(DatSpellBase));

            SetBackingField(spellBase, "Name", "Test Heal Other");
            SetBackingField(spellBase, "School", MagicSchool.LifeMagic);
            SetBackingField(spellBase, "Category", SpellCategory.Undef);
            SetBackingField(spellBase, "MetaSpellId", 9999u);
            SetBackingField(spellBase, "MetaSpellType", SpellType.Boost);
            SetBackingField(spellBase, "Bitfield", (uint)(selfTargeted ? SpellFlags.Beneficial | SpellFlags.SelfTargeted : SpellFlags.Beneficial));
            SetBackingField(spellBase, "NonComponentTargetType", (uint)ItemType.Creature);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            spell._spell = new DbSpell { Boost = boost, BoostVariance = 0, DamageType = (int)DamageType.Health };

            return spell;
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            var field = target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{target.GetType().Name}.<{propertyName}>k__BackingField was not found by reflection");
            field.SetValue(target, value);
        }
    }
}
