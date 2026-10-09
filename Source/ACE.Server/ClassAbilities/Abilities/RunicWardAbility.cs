using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T2: each landed weapon hit inscribes a ward, adding 3/5/7% of that hit's damage by rank to
    /// the pool, capped at 15% of the Spellsword's maximum health. The ward absorbs incoming damage and
    /// lapses after 12 seconds without a weapon hit - and landing a war spell SPENDS THE WHOLE WARD into
    /// that spell's damage, which is the decision the entry exists to pose: hold it as defence, or cash it.
    ///
    /// TEMPORARY HP, NOT DAMAGE REDUCTION, and the distinction is the same one BLOOD-MAGE-DESIGN.md settled
    /// for Sanguine Ward: damage reduction is a SHARED ADDITIVE AXIS (POWER-LEDGER lists Mana Barrier and
    /// Battle Hardened on it) that would need a cross-class ceiling check, while an absorb pool touches
    /// neither the mitigation nor the avoidance budget. Do not "improve" this into DR later.
    ///
    /// THREE HALVES, THREE SITES, and none of them is optional:
    ///  - INSCRIBE rides IOutgoingDamageAbility purely as a "landed weapon hit vs a monster" trigger and
    ///    changes the strike's damage by nothing, the same way the Spellsword proc handlers use that hook.
    ///  - ABSORB rides IPreWriteDamageAbility at the AbsorbPool band, which is what covers all five sites a
    ///    player can lose health through in one dispatch. A pool wired only into Player.TakeDamage would be
    ///    physical-only - the gap both Mana Barrier and Sanguine Ward were reported for on 2026-09-03.
    ///  - SPEND is read at SpellProjectile.CalculateDamage's war/void branch, the only place a spell's
    ///    damage can still be changed. It is added as FLAT damage, not as a multiplier, because the ward is
    ///    a number of points rather than a percentage of anything.
    ///
    /// WHY THE AbsorbPool BAND AND NOT ANOTHER. A finite pool that eats damage outright must be spent
    /// against the full hit, ahead of any proportional diversion (Mana Barrier), or the two do not commute:
    /// the same hit leaves different damage on Health and spends a different amount of Mana depending on the
    /// order. Sanguine Ward sits at the same band; two finite pools DO commute with each other, so the
    /// stable sort's fallback to registration order between them is correct rather than merely tolerable.
    ///
    /// PvE ONLY. The inscribe half is reached only through ApplyOutgoingDamageClassAbilities (Player targets
    /// excluded), the spend half only inside SpellProjectile's `target is not Player` branch, and the absorb
    /// half requires the dispatch's filtered Attacker. That last one is a DELIBERATE DIVERGENCE FROM
    /// SANGUINE WARD, which absorbs PvP and self-damage unconditionally: this wave's entries are PvE-only by
    /// ruling, so a Spellsword's ward does nothing against another player. Do not unify the two gates.
    /// </summary>
    public class RunicWardAbility : IClassAbility, IPreWriteDamageAbility, IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.RunicWard,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 2,
            Name = "runic_ward",
            DisplayName = "Runic Ward",
            Description = "Each landed weapon hit inscribes a ward, adding 3/5/7% of that hit's damage (by " +
                          "rank) to the ward, up to 15% of your maximum health. The ward absorbs incoming " +
                          "damage and lapses after 12 seconds without a weapon hit. Landing a war spell " +
                          "spends the entire ward into that spell's damage. Higher Item Tinkering " +
                          "multiplies the ward.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ItemTinkering,
        };

        /// <summary>
        /// AbsorbPool band, alongside Sanguine Ward: a finite pool that eats damage outright runs FIRST,
        /// ahead of any proportional diversion, so the pool is spent against the full hit. See the type doc
        /// for why the two orders are not interchangeable.
        /// </summary>
        public int MitigationOrder => DamageMitigationOrder.AbsorbPool;

        /// <summary>
        /// TRUE, for the same load-bearing reason Sanguine Ward declares it: THE WARD OUTLIVES THE RANK THAT
        /// INSCRIBED IT. Its size was fixed by rank at inscribe time and is carried in the pool itself, and
        /// <see cref="ModifyIncomingDamage"/> applies no rank check at all. The per-player hook cache is
        /// rank-filtered, so without this declaration a Spellsword who unlearned the ability, swapped facet,
        /// or was caught by a wholesale rank sweep would drop out of the dispatch and their still-live ward
        /// would silently absorb NOTHING for the rest of its window.
        ///
        /// The cost is one array slot and one comparison per damaging hit for every player alive, which is
        /// only acceptable because Player.AbsorbWithRunicWard returns immediately on an empty pool.
        /// </summary>
        public bool RunsWithoutLearnedRank => true;

        /// <summary>
        /// RANK-INDEPENDENT. <paramref name="rank"/> is deliberately unused and may be 0 (see
        /// <see cref="RunsWithoutLearnedRank"/>): what the ward absorbs was decided when it was inscribed.
        ///
        /// The filtered Attacker is the PvE gate - null means PvP, self-damage, a dead attacker or an
        /// unattributed DoT tick, and the ward covers none of those.
        /// </summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            if (context.Attacker == null)
                return;

            context.Damage = defender.AbsorbWithRunicWard(context.Damage);
        }

        /// <summary>
        /// The inscribe half: a landed weapon hit against a monster adds its share to the ward. This handler
        /// does NOT modify the strike - the hook is used purely as the "a weapon hit landed" trigger, the
        /// same way Spellblade, Runeblade, Spellstorm and Sundermark use it.
        ///
        /// The ward is fed from damageEvent.Damage, the POST-mitigation number the target is actually about
        /// to take, so a hit soaked by armour inscribes proportionally less. That is the figure the player
        /// sees in their own combat line, which makes the ward's growth legible.
        /// </summary>
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || rank <= 0 || damageEvent == null || damageEvent.Damage <= 0.0f)
                return;

            // Item Tinkering MULTIPLIES this ability's OWN rank gain (the 2026-09-12 model) rather than
            // riding additively beside it, so it is worth more the more ranks are bought and exactly nothing
            // at rank 0. At zero effective Item Tinkering the factor is exactly 1.0 and the ward is
            // bit-identical to rank alone. Acid Proc's Item Tinkering rider moved to Alchemy in the same
            // overhaul, so this is the only Item Tinkering affinity call in the Abilities tree that belongs
            // to the ability whose file it sits in.
            var gainFraction = RunicWardMath.GainFraction(rank,
                PropertyManager.GetDouble("class_ability_runicward_gain_base").Item,
                PropertyManager.GetDouble("class_ability_runicward_gain_step").Item,
                attacker.GetClassAbilityAffinityMultiplier(Skill.ItemTinkering));

            attacker.InscribeRunicWard((uint)damageEvent.Damage, gainFraction);
        }

        /// <summary>
        /// Mirrors the inscribe half's terms exactly (x100 for display) - the two must stay in step. Reports
        /// the fraction of a landed weapon hit that goes into the ward, which is the one number a player can
        /// act on; the pool's own 15%-of-max-health ceiling is NOT a clamp on this value (it bounds the
        /// accumulated pool, not the per-hit share), so it never sets CapNote. The ability carries no
        /// equipment mod, so Gear is 0.
        ///
        /// Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the three terms
        /// share one unit and sum exactly to Effective.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var gainBase = PropertyManager.GetDouble("class_ability_runicward_gain_base").Item;
            var gainStep = PropertyManager.GetDouble("class_ability_runicward_gain_step").Item;

            var rankFraction = RunicWardMath.GainFraction(rank, gainBase, gainStep, 1.0);

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.ItemTinkering);

            var skill = rankFraction * 100.0;
            var affinity = (RunicWardMath.GainFraction(rank, gainBase, gainStep, multiplier) - rankFraction) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = skill + affinity,
                Unit = "%",
                Label = "ward",
                Per = "/hit",
                CapNote = null,
            };
        }
    }
}
