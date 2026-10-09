using System;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T2: a landed WAR PROC stacks +2% proc chance (cap 5, 10 second decay). Full ramp
    /// +10 percentage points, so Spellblade runs 20% -> 30%, Runeblade 15% -> 25%, Spellstorm 10% -> 20%.
    /// SPELLSWORD-DESIGN.md sec 5e.
    ///
    /// SINGLE RANK, AND THAT IS THE CONTAINMENT. A chance bonus on a chance mechanic feeds itself, so the
    /// entry was flagged as quadratic at review. The hard 5-stack cap bounds it completely, and because
    /// stacks are earned by proccing, the ramp is self-limiting: the steady state sits below the cap
    /// whenever the base chance is low, which is exactly where the compounding would otherwise be worst
    /// (sec 5e). A rank ladder on top of that would reopen the question for nothing, hence MaxRank 1.
    ///
    /// WHY THIS CARRIES NO HOOK INTERFACE. Same shape as Resonance: the stack pool lives on the Player
    /// (Player_ClassAbilityBuffs.cs). The three war proc handlers add a stack when their proc lands, and
    /// they read the bonus back when they roll their next proc chance. There is no combat event this
    /// handler itself needs to be dispatched for, so it takes IPassiveStatAbility - see the doc comment on
    /// that interface in ClassAbilityHooks.cs.
    ///
    /// ONLY WAR PROCS FEED IT. Sundermark - the T2 Vulnerability proc - deliberately does NOT
    /// (sec 5e, "Does Spellsurge stack from any proc, or only from war procs?"). Sundermark is the class's
    /// one ANY-WEAPON entry while the three war procs are light-weapon-only; letting the any-weapon entry
    /// feed the light-weapon ones would hand a mace user the light-weapon ramp. The exclusion lives in the
    /// Sundermark handler, which simply does not call Player.AddSpellsurgeStack.
    ///
    /// INDEPENDENT OF RESONANCE. Two capped stack pools on two different quantities - chance here, damage
    /// there - so they compose cleanly and are worth one line of player-facing text apart so they are not
    /// read as one mechanic (sec 5e).
    /// </summary>
    public class SpellsurgeAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Spellsurge,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 2,
            Name = "spellsurge",
            DisplayName = "Spellsurge",
            Description = "Each landed war proc raises your proc chance by 2 percentage points, up to 5 stacks " +
                          "(+10 points), decaying after 10 seconds without a proc. Sundermark's vulnerability " +
                          "proc does not build stacks.",
            MaxRank = 1,
            CostPerRank = new[] { 3 },
            Implemented = true,
        };

        /// <summary>
        /// The proc-chance bonus for a given stack count, as a FRACTION to add to a proc chance
        /// (3 stacks at the 0.02 default -> 0.06). Pure for testability.
        ///
        /// Returns 0 for rank &lt;= 0 or stacks &lt;= 0. The caller owns clamping stacks to the cap; this
        /// deliberately does not, so the cap stays a single tunable read in one place
        /// (Player.GetSpellsurgeProcChanceBonus) rather than a constant duplicated here.
        ///
        /// <paramref name="gearPerStack"/> is the SURGE equipment mod (EquipmentModId.Surge), a MACHINERY
        /// mod added to the PER-STACK rate inside the ramp - stacks * (perStack + gear) - so it rides the
        /// same stack count the ability's own rate does (DESIGN.md 3.3). Last and defaulted to 0, so an
        /// unmodded build is bit-identical.
        ///
        /// THIS IS A DELEGATED INJECTION POINT and that is the whole reason Surge is wired here rather than
        /// in each war proc. Player.GetSpellsurgeProcChanceBonus is the single read, and its result is fed
        /// to Spellblade, Runeblade and Spellstorm as their spellsurgeBonus argument, so ONE gear term
        /// reaches all three - exactly as the ability's own bonus does. Adding Surge separately inside any
        /// of those three Chance() helpers would double-count it (DESIGN.md 2.4, the delegation rule). The
        /// three war procs' OWN mods (Spellblade / Runeblade / Spellstorm) are distinct rows and are added
        /// there instead; they do not overlap with this one.
        /// </summary>
        public static float ProcChanceBonus(int rank, int stacks, double perStack, double gearPerStack = 0.0)
        {
            var perStackTotal = perStack + Math.Max(0.0, gearPerStack);

            if (rank <= 0 || stacks <= 0 || perStackTotal <= 0.0)
                return 0.0f;

            return (float)(stacks * perStackTotal);
        }

        /// <summary>
        /// Reports the PER-STACK proc-chance bonus: Skill from the tunable ProcChanceBonus() above is fed,
        /// no affinity rider, Gear from the SURGE mod which is added to that same per-stack rate. The
        /// 5-stack cap bounds how many stacks a player can hold, not the per-stack rate itself, so it never
        /// reduces this readout and CapNote is always null.
        ///
        /// THIS LINE REPORTS SURGE AND NOTHING ELSE, even though the number it describes reaches three
        /// other abilities' proc rolls. Spellblade, Runeblade and Spellstorm each report their OWN mod on
        /// their own line; counting Surge there too would show one item twice on one panel (DESIGN.md 2.4,
        /// the display rule).
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perStack = rank <= 0 ? 0.0 : PropertyManager.GetDouble("class_ability_spellsurge_per_stack").Item;

            var gearPerStack = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Surge) ?? 0.0);

            var skill = perStack * 100.0;
            var gear = gearPerStack * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = skill + gear,
                Unit = "%",
                Label = "proc chance",
                Per = "/stack",
                CapNote = null,
            };
        }
    }
}
