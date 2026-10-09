using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.ClassAbilities
{
    public static class ClassAbilityRegistry
    {
        /// <summary>
        /// Quest registry key prefix - a skill's per-character row is ClassAbility_&lt;Name&gt;.
        /// These keys are deliberately NOT in the world quest table; writes must bypass
        /// QuestManager.SetQuestCompletions, which clamps unknown quests to 0 solves.
        /// </summary>
        public const string QuestKeyPrefix = "ClassAbility_";

        public static string QuestKey(ClassAbilityDefinition skill) => QuestKeyPrefix + skill.Name;

        /// <summary>
        /// Every skill handler, in registration order. That is also per-hook execution order for every hook
        /// EXCEPT two, which carry an explicit order key and are stable-sorted by it:
        /// <see cref="PreWriteDamageAbilities"/> (IPreWriteDamageAbility.MitigationOrder) and
        /// <see cref="OutgoingDamageAbilities"/> (IOutgoingDamageAbility.DispatchOrder). In those two buckets
        /// registration order only breaks ties within a band.
        /// Adding a new hand-written combat skill = adding its handler class under ClassAbilities/Abilities
        /// and one line to <see cref="handWritten"/>. The passive "Enhanced X" stat family is generated
        /// (EnhancedStatAbility.GenerateAll) rather than listed, so it grows automatically with the skill
        /// and attribute enums.
        /// </summary>
        public static readonly IReadOnlyList<IClassAbility> Handlers;

        private static readonly IClassAbility[] handWritten =
        {
            new MultishotAbility(),
            new ThornsAbility(),
            new TauntAbility(),
            new PoisonWeaponAbility(),
            new AcidProcAbility(),
            new SpellAoeAbility(),
            new EchoCastAbility(),
            new ElementalRendAbility(),
            new BattleHardenedAbility(),
            new FrenzyAbility(),
            new NetherRushAbility(),

            // Phase 3 - outgoing-damage % / conditional / surcharge skills
            new DeadeyeAbility(),
            // Heavy Draw RETIRED 2026-09-12 (class ability overhaul): Tier 2 was a wall of silent damage
            // riders and the stamina surcharge played badly. Heavy Draw's id (25) stays reserved in
            // ClassAbilityDefinition, its token catalog slot stays reserved, and
            // RetiredClassAbilities["heavydraw"] refunds held ranks. Pinning Shot takes the Archer T2 slot.
            new LongDrawAbility(),
            new SavageBlowsAbility(),
            // Blood Fury RETIRED 2026-08-17 (Berserker/Rogue balance pass): the Berserker T2 slot it held
            // is taken by Break Armor below. Blood Fury's id (35) stays reserved in ClassAbilityDefinition,
            // its token catalog slot stays reserved, and RetiredClassAbilities["bloodfury"] refunds held
            // ranks.
            new BreakArmorAbility(),
            new ExecutionerAbility(),
            new BloodlustAbility(),

            // Phase 4 - bespoke speed skills (compose with Frenzy / Nether Rush)
            new AttackSpeedAbility(),
            new FlatCastSpeedAbility(),
            new OverchannelAbility(),

            // Phase 5 - avoidance tier (rolled before evade; markers, mechanics in Player_ClassAbilityCombat)
            new ParryAbility(),
            new ShieldBlockAbility(),
            // Shield Check RETIRED 2026-09-12 (class ability overhaul): it only did anything for a character
            // who had also bought Rogue's Parry, which made it a dead entry for most Vanguards. Its id (33)
            // stays reserved in ClassAbilityDefinition, its token catalog slot stays reserved, and
            // RetiredClassAbilities["shieldcheck"] refunds held ranks. The parry-to-Thorns conversion it
            // owned is gone from Player.OnClassAbilityAttackAvoided with it - blocked hits still trigger
            // Thorns at full strength, parried hits now trigger nothing but Riposte. Kinetic Charge takes
            // the Vanguard T2 slot.
            new RiposteAbility(),

            // Berserker/Rogue balance pass 2026-08-17. Pocket Sand is IPassiveStatAbility with no combat
            // hook - it is dispatched by hand from the three avoidance sites (Player.TryPocketSand). Filed
            // here rather than appended so it sits with the avoidance tier it belongs to. It is in no hook
            // bucket, so its position here has no effect on execution order.
            //
            // Surefooted RETIRED 2026-09-12 (class ability overhaul): its stacking avoidance overlapped
            // Parry inside the same pooled cap. Its id (72) stays reserved in ClassAbilityDefinition, its
            // token catalog slot stays reserved, and RetiredClassAbilities["surefooted"] refunds held ranks.
            // Its stack pool, the Player.OnEvade trigger, the melee-hit reset in Player.TakeDamage and the
            // parry-term read in Player.RollClassAbilityAvoidance are all removed with it. Killer Instinct
            // replaces it in the class and has now landed (see Phase 14 below), but at Rogue T3 - NOT the
            // T2 this comment claimed until 2026-09-12. T3 is the signed-off placement and the only one
            // that reproduces Rogue's 44 CAP total, which ClassAbilityCapTotalsTests pins.
            new PocketSandAbility(),

            // Phase 6 - spell-damage skills
            new VoidDamageAbility(),

            // Phase 7 - missile bespoke (Eagle Eye to-hit, Double Volley re-fire)
            new FastAimAbility(),
            new DoubleVolleyAbility(),

            // Phase 8 - void bespoke (Withering DoT tick). Streak-to-Arc RETIRED 2026-07-18
            // (Arc/Streak base ratio ~2.0x = +100% void damage - far too strong; see POWER-LEDGER).
            new WitheringAbility(),

            // Phase 9 - melee cleave bespoke
            new WhirlwindAbility(),

            // Phase 10 - combat-pet subsystem
            new EmpoweredSummonsAbility(),
            new Summon2xSkill(),

            // Phase 11 - Tier-2 additions (mana-as-health barrier, DoT spread on kill, pet tether)
            new ManaBarrierAbility(),
            new NetherBloomAbility(),
            new SoulTetherAbility(),

            // Phase 12 - Blood Mage (7th class), 2026-08-03. Eight bespoke entries; the other three Blood
            // Mage entries ride the generated families instead (Enhanced Life Magic is homed to BloodMage T1
            // in EnhancedStatAbility.Homing, Blood Mage Training is a BundleStatAbility, and Heal Boost
            // Rating is a RatingAbility).
            //
            // ONLY Transfusion has a live mechanic (the Drain surplus cascade in WorldObject_Magic). The
            // other seven are REGISTRATION ONLY: correct tier/rank/cost so the class exists and its tier
            // gate can count spent points, with Implemented = false so they are unlearnable and show as
            // "[Coming soon]" until their mechanic wave lands. See each handler's doc comment.
            new SanguineReserveAbility(),
            new TransfusionAbility(),
            new WeakenedBloodAbility(),
            new MaledictionAbility(),
            new CrimsonHarvestAbility(),
            new ExsanguinateAbility(),
            new BloodPriceAbility(),
            new SanguineWardAbility(),

            // Phase 13 - Spellsword (8th class), 2026-08-03. Eight bespoke handlers listed here; the class's
            // other three members ride the generated families instead (SpellswordTraining is built by
            // BundleStatAbility.GenerateAll, and Enhanced Light Weapons / Enhanced Melee Defense are members
            // of the generated Enhanced-stat family homed to Spellsword T1/T2 in EnhancedStatAbility.Homing).
            // That is why ClassAbilityDefinition lists NINE Spellsword ids but only eight appear below -
            // SpellswordTraining owns id 64 and is generated, not hand-written.
            //
            // The four proc handlers all ride IOutgoingDamageAbility as a pure "landed weapon hit vs a
            // monster" trigger - none of them modifies the strike's damage. The three war procs
            // (Spellblade / Runeblade / Spellstorm) are light-weapon-gated and cast player-sourced war
            // spells with the wielded weapon as launcher; Sundermark is any-weapon and applies a
            // vulnerability instead, which is why it neither feeds nor reads Spellsurge.
            new SpellbladeAbility(),
            new ResonanceAbility(),
            new RunebladeAbility(),
            new SundermarkAbility(),
            new SpellsurgeAbility(),
            new SpellstormAbility(),
            new CascadeAbility(),
            new DispellingEdgeAbility(),

            // Phase 14 - class ability overhaul NEW-ABILITY WAVE, 2026-09-12. Fifteen entries: one 5-rank
            // Tier 1 "splash" ability per class, plus seven others filling the slots this overhaul's three
            // retirements vacated (Pinning Shot for Heavy Draw at Archer T2, Kinetic Charge for Shield Check
            // at Vanguard T2, Killer Instinct for Surefooted - though at Rogue T3, not the T2 the retirement
            // comments anticipated) and the tier gaps its reprice opened.
            //
            // ALL FIFTEEN ARE NOW IMPLEMENTED. They landed REGISTRATION ONLY in Phase 0 - correct
            // class/tier/rank/cost so each class's CAP total and tier gate counted the entry, with
            // Implemented = false, no hook interface and no tunable - and seven parallel mechanic slices
            // then flipped each one to a live mechanic. That two-step is why the CAP totals were closeable
            // before any mechanic existed: CapTotals_MatchTheRegistryForEveryClass reads the registry with
            // no Implemented filter, so a registration-only entry still closes a class's total.
            // BloodMageAbilityDefinitionTests.NoEntryIsPending_AndAPendingOneWouldHookNothing and
            // ClassAbilityRegistryTests.EverySkillHasAHandlerAndImplementedSkillsHookSomething together pin
            // that nothing was left half-wired in either direction.
            //
            // Every one now declares an AffinitySkill backed by a real affinity call, EXCEPT Quickened
            // Casting, whose null is FINAL by design rather than pending. See
            // ClassAbilityAffinityDeclarationTests, which fails a declaration with no backing call and a
            // call with no declaration alike, and has no Implemented exemption.
            //
            // Filed at the end when they carried no hook. Several now do, and their position is still not
            // what decides their order in the two sorted buckets: IOutgoingDamageAbility runs by DispatchOrder
            // (Spellweave, here at the end, still runs BEFORE the Phase 13 war procs because those declare
            // OutgoingDamageDispatchOrder.SpellCastingProc, and Killer Instinct below runs FIRST because it
            // declares OutgoingDamageDispatchOrder.CritDamage) and IPreWriteDamageAbility by MitigationOrder.
            // Registration order only breaks ties inside a band there, and is the execution order only for
            // the unsorted hooks. A slice that hooks one of these into an unsorted hook should check whether
            // its position here is still right.
            new HuntersMarkAbility(),
            new PinningShotAbility(),
            new OpportunistAbility(),
            new KillerInstinctAbility(),
            new RallyingPresenceAbility(),
            new KineticChargeAbility(),
            new ReflectMagicAbility(),
            new AdrenalineAbility(),
            new VengeanceAbility(),
            new QuickenedCastingAbility(),
            new UmbralSiphonAbility(),
            new SoulJumpAbility(),
            new HemomancyAbility(),
            new SpellweaveAbility(),
            new RunicWardAbility(),
            // Appended 2026-09-29. IPassiveStatAbility only, so it joins no hook bucket at all and its
            // position here has no effect on any dispatch order.
            new CloakedInPowerAbility(),
        };

        public static readonly IReadOnlyDictionary<ClassAbilityId, ClassAbilityDefinition> Abilities;

        // per-hook buckets, built once - Player_ClassAbilities filters these down to each
        // player's learned skills so hot paths never scan the full registry

        /// <summary>
        /// Outgoing-damage handlers, sorted by <see cref="IOutgoingDamageAbility.DispatchOrder"/> rather than
        /// left in registration order. OrderBy is a stable sort, so every handler in the default Modify band
        /// keeps its registration order and only two groups move: Killer Instinct to the FRONT (CritDamage
        /// band, so the procs read its stashed crit-damage bonus), and the spell-casting war procs to the END,
        /// after Spellweave's weapon half. See OutgoingDamageDispatchOrder for why each ordering is live combat
        /// state rather than tidiness.
        /// </summary>
        public static readonly IReadOnlyList<IOutgoingDamageAbility> OutgoingDamageAbilities;
        public static readonly IReadOnlyList<IIncomingDamageAbility> IncomingDamageAbilities;

        /// <summary>
        /// Pre-write mitigation, sorted by <see cref="IPreWriteDamageAbility.MitigationOrder"/> rather than
        /// left in registration order like every other bucket. Two mitigations that each scale off what is
        /// left do not commute, so their relative order is live combat arithmetic and must not be decided by
        /// where a line happens to sit in the list above. OrderBy is a stable sort, so equal orders still
        /// fall back to registration order.
        /// </summary>
        public static readonly IReadOnlyList<IPreWriteDamageAbility> PreWriteDamageAbilities;

        public static readonly IReadOnlyList<IMissileVolleyAbility> MissileVolleyAbilities;
        public static readonly IReadOnlyList<IItemProcAbility> ItemProcAbilities;
        public static readonly IReadOnlyList<ISpellHitAbility> SpellHitAbilities;
        public static readonly IReadOnlyList<ICreatureDeathAbility> CreatureDeathAbilities;

        // Multi-skill passive bundles - read (not dispatched) in Player.GetEnhancedSkillBonus.
        public static readonly IReadOnlyList<IStatBundleAbility> StatBundleAbilities;

        private static readonly Dictionary<ClassAbilityId, IClassAbility> handlersById;
        private static readonly Dictionary<string, ClassAbilityDefinition> byName;

        static ClassAbilityRegistry()
        {
            Handlers = handWritten
                .Concat(BundleStatAbility.GenerateAll())
                .Concat(RatingAbility.GenerateAll())
                .Concat(EnhancedStatAbility.GenerateAll())
                .ToArray();

            handlersById = Handlers.ToDictionary(h => h.Definition.Id);
            Abilities = Handlers.ToDictionary(h => h.Definition.Id, h => h.Definition);
            byName = Handlers.ToDictionary(h => h.Definition.Name, h => h.Definition, StringComparer.OrdinalIgnoreCase);

            OutgoingDamageAbilities = Handlers.OfType<IOutgoingDamageAbility>().OrderBy(h => h.DispatchOrder).ToArray();
            IncomingDamageAbilities = Handlers.OfType<IIncomingDamageAbility>().ToArray();
            PreWriteDamageAbilities = Handlers.OfType<IPreWriteDamageAbility>().OrderBy(h => h.MitigationOrder).ToArray();
            MissileVolleyAbilities  = Handlers.OfType<IMissileVolleyAbility>().ToArray();
            ItemProcAbilities       = Handlers.OfType<IItemProcAbility>().ToArray();
            SpellHitAbilities       = Handlers.OfType<ISpellHitAbility>().ToArray();
            CreatureDeathAbilities  = Handlers.OfType<ICreatureDeathAbility>().ToArray();
            StatBundleAbilities     = Handlers.OfType<IStatBundleAbility>().ToArray();
        }

        /// <summary>
        /// The rank filter Player.BuildClassAbilityHookCache applies when it builds a player's per-hook
        /// learned-handler cache: a handler is eligible if the player currently holds a rank in it, OR if it
        /// declares that it runs without one (see <see cref="IPreWriteDamageAbility.RunsWithoutLearnedRank"/>
        /// - a pool granted while the ability was held has to keep draining after an unlearn, a facet swap
        /// or a wholesale rank sweep, exactly as it did before the effect was dispatched from a hook).
        ///
        /// Static, and on the registry rather than on Player, deliberately: this is the entire rule, it is a
        /// pure function of the handler and the rank, and Player's static initializer cannot run under the
        /// unit test host - so homing it here is what makes the rule directly testable.
        /// </summary>
        public static bool IsHookCacheEligible(IClassAbility handler, int rank)
        {
            return rank > 0 || (handler is IPreWriteDamageAbility preWrite && preWrite.RunsWithoutLearnedRank);
        }

        public static ClassAbilityDefinition Get(ClassAbilityId id) => Abilities[id];

        public static IClassAbility GetHandler(ClassAbilityId id) => handlersById[id];

        public static bool TryGetByName(string name, out ClassAbilityDefinition skill)
        {
            if (name == null)
            {
                skill = null;
                return false;
            }
            return byName.TryGetValue(name, out skill);
        }

        /// <summary>
        /// Maps a quest registry key (ClassAbility_&lt;name&gt;) back to its definition
        /// </summary>
        public static bool TryGetByQuestKey(string questKey, out ClassAbilityDefinition skill)
        {
            skill = null;

            if (questKey == null || !questKey.StartsWith(QuestKeyPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return TryGetByName(questKey.Substring(QuestKeyPrefix.Length), out skill);
        }
    }
}
