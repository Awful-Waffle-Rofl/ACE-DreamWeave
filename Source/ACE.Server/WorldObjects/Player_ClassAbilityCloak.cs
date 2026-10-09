using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Cloaked in Power (Vanguard T2): the minimum chance this player's equipped cloak has to fire its
        /// spell proc on a hit that reached them, as a fraction. 0.0 for a player who has not learned it,
        /// which reproduces the shipped <c>cloak_min_proc</c> default and leaves Cloak.RollProc's arithmetic
        /// bit-identical.
        ///
        /// READ FROM Cloak.TryProcSpell, ONCE, FOR ALL FOUR CLOAK SPELL-PROC SITES. TryProcSpell already
        /// takes the cloak's wearer as its defender parameter, so the floor is fetched there rather than
        /// threaded through four call sites that would each have to remember to pass it - see the doc comment
        /// on <see cref="CloakedInPowerAbility"/> for why that is the shape that keeps a future fifth site
        /// honest.
        ///
        /// THIS IS A PER-HIT HOT PATH (every landed hit on a cloak-wearing player), so the cheap
        /// dictionary-count rejection comes first, exactly as TryReflectProjectile does: a player with no
        /// class abilities at all pays one count check and no tunable reads.
        ///
        /// TryGetClassAbility, not GetClassAbilityRank, so class_abilities_enabled and the PK-facet /
        /// arena-mask suppression both apply - a suppressed Vanguard's cloak rolls at the stock chance.
        /// </summary>
        public double GetCloakedInPowerFloor()
        {
            if (GetClassAbilityCache().Count == 0)
                return 0.0;

            if (!TryGetClassAbility(ClassAbilityId.CloakedInPower, out var rank))
                return 0.0;

            // Armor Tinkering MULTIPLIES this ability's own floor, and class_ability_affinity_chance_cap
            // bounds the AMOUNT the multiply adds - the floor is a proc chance, which is that cap's declared
            // scope.
            return CloakedInPowerAbility.Floor(rank,
                PropertyManager.GetDouble("class_ability_cloakedinpower_floor").Item,
                GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);
        }
    }
}
