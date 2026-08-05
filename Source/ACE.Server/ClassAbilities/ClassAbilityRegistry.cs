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
        /// Every skill handler, in registration order - which is also per-hook execution order.
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
            new HeavyDrawAbility(),
            new LongDrawAbility(),
            new SavageBlowsAbility(),
            new BloodFuryAbility(),
            new ExecutionerAbility(),
            new BloodlustAbility(),

            // Phase 4 - bespoke speed skills (compose with Frenzy / Nether Rush)
            new AttackSpeedAbility(),
            new FlatCastSpeedAbility(),
            new OverchannelAbility(),

            // Phase 5 - avoidance tier (rolled before evade; markers, mechanics in Player_ClassAbilityCombat)
            new ParryAbility(),
            new ShieldBlockAbility(),
            new ShieldCheckAbility(),
            new RiposteAbility(),

            // Phase 6 - spell-damage skills
            new VoidDamageAbility(),

            // Phase 7 - missile bespoke (Eagle Eye to-hit, Double Volley re-fire)
            new EagleEyeAbility(),
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
        };

        public static readonly IReadOnlyDictionary<ClassAbilityId, ClassAbilityDefinition> Abilities;

        // per-hook buckets, built once - Player_ClassAbilities filters these down to each
        // player's learned skills so hot paths never scan the full registry
        public static readonly IReadOnlyList<IOutgoingDamageAbility> OutgoingDamageAbilities;
        public static readonly IReadOnlyList<IIncomingDamageAbility> IncomingDamageAbilities;
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

            OutgoingDamageAbilities = Handlers.OfType<IOutgoingDamageAbility>().ToArray();
            IncomingDamageAbilities = Handlers.OfType<IIncomingDamageAbility>().ToArray();
            MissileVolleyAbilities  = Handlers.OfType<IMissileVolleyAbility>().ToArray();
            ItemProcAbilities       = Handlers.OfType<IItemProcAbility>().ToArray();
            SpellHitAbilities       = Handlers.OfType<ISpellHitAbility>().ToArray();
            CreatureDeathAbilities  = Handlers.OfType<ICreatureDeathAbility>().ToArray();
            StatBundleAbilities     = Handlers.OfType<IStatBundleAbility>().ToArray();
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
