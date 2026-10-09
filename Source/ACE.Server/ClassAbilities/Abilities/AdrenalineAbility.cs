using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T1 splash: taking damage arms the next attack OR SPELL within 6 seconds for 2/4/6/8/10%
    /// more damage by rank. Covering spells as well as swings is deliberate - it keeps the entry from being
    /// dead weight on a Berserker who splashed a caster class - and the short window is what ties the payoff
    /// to actually standing in the fight, which is the class's whole premise.
    ///
    /// IT ARMS FROM THE PRE-WRITE HOOK AT THE Observe BAND, AND MITIGATES NOTHING. The obvious hook for
    /// "reacts to a landed hit on the player" is IIncomingDamageAbility, and that is the WRONG one here:
    /// that hook hangs off Player.TakeDamage, which is melee, missile and hotspots only - magic damage and
    /// DoT ticks write Health straight to the vital and never reach it (see its own doc comment, and the
    /// 2026-09-03 "Mana Barrier doesn't absorb magic damage" report for what that gap looks like in play).
    /// This ability's description promises the player it fires "when you take damage", full stop, so it
    /// rides the one dispatch that sees all five damage sites. <see cref="ModifyIncomingDamage"/> reads the
    /// context and writes nothing to it - that is the Observe band's contract.
    ///
    /// ARMING IS IDEMPOTENT, which is what makes an observer safe on this hook: a second hit inside the
    /// window simply re-stamps the same expiry, so nothing double-counts however many sites a single
    /// exchange passes through. Kinetic Charge, which COUNTS hits rather than stamping a time, needs its
    /// per-hit gates (no DoT ticks) for exactly that reason.
    ///
    /// PvE ONLY, enforced on both halves: the arming half requires the dispatch's filtered Attacker (a live
    /// non-player Creature other than the defender), so a hit from another player arms nothing; the
    /// consuming halves are reached only where the target is already known not to be a Player
    /// (ApplyOutgoingDamageClassAbilities excludes Player targets, and the war/void spell-damage site is
    /// inside its own `target is not Player` branch).
    ///
    /// THE WINDOW IS SPENT BY THE FIRST THING THAT QUALIFIES, weapon hit or spell, never both: the two
    /// consumers call the same Player.TryConsumeAdrenaline, which clears the window as it reports it.
    /// </summary>
    public class AdrenalineAbility : IClassAbility, IPreWriteDamageAbility, IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Adrenaline,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 1,
            Name = "adrenaline",
            DisplayName = "Adrenaline",
            Description = "When you take damage, your next attack or spell within 6 seconds deals " +
                          "2/4/6/8/10% more damage (by rank). Higher Recklessness multiplies the bonus.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Recklessness,
        };

        /// <summary>
        /// Observe band: this handler reacts to being hit and changes no damage at all, so it must not sit
        /// between two mitigations. See DamageMitigationOrder.Observe for why that band runs ahead of the
        /// absorb pools rather than after the death save.
        /// </summary>
        public int MitigationOrder => DamageMitigationOrder.Observe;

        /// <summary>
        /// FALSE. The window is a timestamp, not a pool, and it is worth nothing on its own: both consumers
        /// re-read the live rank before applying any bonus, so a player who unlearns mid-window gets no
        /// damage from a window still nominally open. Nothing can be stranded, so the rank filter on the
        /// per-player hook cache is exactly right - contrast Sanguine Ward, whose granted pool genuinely
        /// does outlive its rank.
        /// </summary>
        public bool RunsWithoutLearnedRank => false;

        /// <summary>
        /// RANK-GATED (reached only while a rank is held), and DAMAGE-NEUTRAL: it arms the window and
        /// returns. <paramref name="rank"/> is deliberately unused - the window records only WHEN the player
        /// was hit; how much the next strike gains is recomputed from the live rank when it is spent, so a
        /// rank bought or lost inside the window takes effect on the strike rather than being frozen here.
        ///
        /// The filtered Attacker is the PvE gate: null means PvP, self-damage, a dead attacker, or an
        /// unattributed DoT tick, and none of those arms anything.
        /// </summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            if (context.Attacker == null)
                return;

            defender.ArmAdrenaline(PropertyManager.GetDouble("class_ability_adrenaline_window_seconds").Item);
        }

        /// <summary>
        /// The weapon-hit half of the payoff: melee, missile and every multishot extra hit route through
        /// Player.DamageTarget, so all of them can spend the window. Spells never reach this hook - their
        /// half is <see cref="ConsumeSpellDamageMultiplier"/>.
        /// </summary>
        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || rank <= 0)
                return;

            if (!attacker.TryConsumeAdrenaline())
                return;

            var bonus = DamageBonus(rank,
                PropertyManager.GetDouble("class_ability_adrenaline_percent_per_rank").Item,
                attacker.GetClassAbilityAffinityMultiplier(Skill.Recklessness));

            if (bonus <= 0.0)
                return;

            damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// The spell half of the payoff, called from Player.GetClassAbilitySpellDamageMod (the one choke
        /// point every war and void projectile's damage passes through). Returns 1.0 - and spends nothing -
        /// for a player who has not learned the ability or whose window is not open.
        ///
        /// It does its own rank lookup rather than being handed one, because its caller is a plain damage
        /// getter rather than a hook dispatch and has no rank in hand.
        ///
        /// CONSUMED PER LANDED PROJECTILE, NOT PER CAST. A multi-projectile cast (a Ring, a Raven Fury)
        /// therefore spends the window on whichever of its bolts lands first and the rest are unbuffed. That
        /// is the honest reading of "your next attack or spell" for an ability whose window is spent by one
        /// strike, and it keeps this off the cast path entirely; the alternative (stamping the answer at
        /// cast time, as Blood Price does) would hand every bolt of a ring the bonus, which is a strictly
        /// bigger ability than the one that was signed off.
        /// </summary>
        public static float ConsumeSpellDamageMultiplier(Player caster)
        {
            if (caster == null || !caster.TryGetClassAbility(ClassAbilityId.Adrenaline, out var rank))
                return 1.0f;

            if (!caster.TryConsumeAdrenaline())
                return 1.0f;

            var bonus = DamageBonus(rank,
                PropertyManager.GetDouble("class_ability_adrenaline_percent_per_rank").Item,
                caster.GetClassAbilityAffinityMultiplier(Skill.Recklessness));

            return (float)(1.0 + bonus);
        }

        /// <summary>
        /// The armed strike's damage bonus as a fraction: rank * perRank, multiplied by the Recklessness
        /// affinity factor. Pure, so both consumers and the readout share one expression.
        ///
        /// Recklessness MULTIPLIES this ability's OWN rank bonus (the 2026-09-12 model) rather than riding
        /// additively beside it, so it is worth more the more ranks are bought and exactly nothing at rank
        /// 0. At zero effective Recklessness the multiplier is exactly 1.0 and the bonus is bit-identical to
        /// rank alone.
        /// </summary>
        public static double DamageBonus(int rank, double percentPerRank, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = rank * percentPerRank;

            return rankBonus * (affinityMultiplier < 1.0 ? 1.0 : affinityMultiplier);
        }

        /// <summary>
        /// Mirrors <see cref="DamageBonus"/> exactly (x100 for display) - the two must stay in step. Reports
        /// the bonus as if the window were open; this is a "how strong is the ability" readout, not a "is
        /// your window currently armed" simulation.
        ///
        /// Affinity is a MULTIPLIER on the rank term but is reported as the AMOUNT that multiplier adds, so
        /// the three displayed terms stay in one unit and sum exactly to Effective. No cap applies to this
        /// ability at any term, so CapNote is always null. It carries no equipment mod, so Gear is 0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_adrenaline_percent_per_rank").Item;

            var rankBonus = rank <= 0 ? 0.0 : rank * perRank;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Recklessness);

            var skill = rankBonus * 100.0;
            var affinity = (DamageBonus(rank, perRank, multiplier) - rankBonus) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = skill + affinity,
                Unit = "%",
                Label = "next hit dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
