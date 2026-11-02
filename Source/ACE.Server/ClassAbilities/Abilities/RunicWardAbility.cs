using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T2: each landed weapon hit inscribes a ward, adding 3/6/9% of that hit's damage by rank to
    /// the pool, capped at 10/20/30% of the Spellsword's maximum health by rank. The ward absorbs incoming
    /// damage and lapses after 12 seconds without a weapon hit. While it is up it ALSO acts as a shield
    /// against frontal physical blows from monsters (Player.GetWardShieldLevel, read in
    /// Creature.GetShieldMod). It used to be spendable into a war spell; the 2026-10-24 Spellsword pass
    /// removed that path entirely, so the pool now drains ONLY by absorbing damage.
    ///
    /// TEMPORARY HP, NOT DAMAGE REDUCTION, and the distinction is the same one BLOOD-MAGE-DESIGN.md settled
    /// for Sanguine Ward: damage reduction is a SHARED ADDITIVE AXIS (POWER-LEDGER lists Mana Barrier and
    /// Battle Hardened on it) that would need a cross-class ceiling check, while an absorb pool touches
    /// neither the mitigation nor the avoidance budget. Do not "improve" this into DR later. THE ONE
    /// EXCEPTION, by owner ruling 2026-10-24, is the ward SHIELD: a flat shield armor level that rides the
    /// existing shield factor (frontal cone, physical only, PvE only) for as long as the pool is up. It is
    /// a shield, not a percentage DR term, and it drops the moment the pool empties.
    ///
    /// TWO HALVES, TWO SITES (plus the shield read), and none of them is optional:
    ///  - INSCRIBE rides IOutgoingDamageAbility purely as a "landed weapon hit vs a monster" trigger and
    ///    changes the strike's damage by nothing, the same way the Spellsword proc handlers use that hook.
    ///  - ABSORB rides IPreWriteDamageAbility at the AbsorbPool band, which is what covers all five sites a
    ///    player can lose health through in one dispatch. A pool wired only into Player.TakeDamage would be
    ///    physical-only - the gap both Mana Barrier and Sanguine Ward were reported for on 2026-09-03.
    ///
    /// WHY THE AbsorbPool BAND AND NOT ANOTHER. A finite pool that eats damage outright must be spent
    /// against the full hit, ahead of any proportional diversion (Mana Barrier), or the two do not commute:
    /// the same hit leaves different damage on Health and spends a different amount of Mana depending on the
    /// order. Sanguine Ward sits at the same band; two finite pools DO commute with each other, so the
    /// stable sort's fallback to registration order between them is correct rather than merely tolerable.
    ///
    /// PvE ONLY. The inscribe half is reached only through ApplyOutgoingDamageClassAbilities (Player targets
    /// excluded), the absorb half requires the dispatch's filtered Attacker, and the shield requires an
    /// enemy-creature attacker. Sanguine Ward now gates its absorb the same way (2026-10-24, all class
    /// abilities are PvE only), so a ward does nothing against another player.
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
            Description = "Each landed weapon hit inscribes a ward, adding 3/6/9% of that hit's damage (by " +
                          "rank) to the ward, up to 10/20/30% of your maximum health. The ward absorbs incoming " +
                          "damage from monsters and lapses after 12 seconds without a weapon hit. While it " +
                          "holds, it also acts as a shield against monsters' physical " +
                          "attacks from the front. Higher Item Tinkering multiplies the ward.",
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
            // a hazard-library hit is not absorbed (owner ruling 2026-10-19: hazards bypass every ward): the pool is
            // left untouched, not spent
            if (context.IsHazard)
                return;

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
            // bit-identical to rank alone.
            var gainFraction = RunicWardMath.GainFraction(rank,
                PropertyManager.GetDouble("class_ability_runicward_gain_base").Item,
                PropertyManager.GetDouble("class_ability_runicward_gain_step").Item,
                attacker.GetClassAbilityAffinityMultiplier(Skill.ItemTinkering));

            // The pool's ceiling is a per-rank fraction of max health (10/20/30% since 2026-10-24); the
            // affinity rides the per-hit gain only, never the ceiling.
            var capFraction = RunicWardMath.CapFraction(rank,
                PropertyManager.GetDouble("class_ability_runicward_cap_fraction_r1").Item,
                PropertyManager.GetDouble("class_ability_runicward_cap_fraction_r2").Item,
                PropertyManager.GetDouble("class_ability_runicward_cap_fraction_r3").Item);

            attacker.InscribeRunicWard((uint)damageEvent.Damage, gainFraction, capFraction);
        }

        /// <summary>
        /// Mirrors the inscribe half's terms exactly (x100 for display) - the two must stay in step. Reports
        /// the fraction of a landed weapon hit that goes into the ward, which is the one number a player can
        /// act on; the pool's own 10/20/30%-of-max-health ceiling is NOT a clamp on this value (it bounds the
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
