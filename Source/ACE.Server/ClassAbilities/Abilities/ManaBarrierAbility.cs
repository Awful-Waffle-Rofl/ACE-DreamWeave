using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T2: a share of every incoming hit is paid out of Mana instead of Health - 6/12/18% by
    /// rank, plus a Magic Defense rider, capped by a server tunable. Implemented as a REDUCTION applied
    /// BEFORE the health write, exactly like Sanguine Ward: the barrier spends Mana and hands the caller a
    /// smaller damage figure, and the caller writes that smaller figure to the vital. It never calls
    /// UpdateVitalDelta(Health, +x) and never puts health back.
    ///
    /// IT WAS A REFUND UNTIL 2026-09-08 AND THAT WAS THE BUG. Every health write clamps at zero, so the
    /// number the old refund was handed was the victim's CURRENT HEALTH on any overkill hit, never the hit
    /// that was actually thrown. A player with 477 Health took a 942-damage critical, the write clamped
    /// `damageTaken` to 477, the barrier refunded its share of 477, and the player survived above zero.
    /// clamp(H - amount, 0) + share*amount is strictly greater than H - amount*(1 - share) whenever
    /// amount > H, so a refund applied after a clamped write can never be correct: with any Mana at all,
    /// NO single hit of any size could kill a barrier carrier. Feeding the refund the true pre-clamp
    /// amount would have made it worse, not better - the defect was the convention, not the number. Do not
    /// reintroduce a refund shape here for any reason.
    ///
    /// COVERAGE IS FIVE CALL SITES, and none of them is redundant. There is no single choke point through
    /// which a player loses health. Player.TakeDamage covers melee, missile and hotspots; the four magic
    /// paths write Health straight to the vital and never pass through it. All five call
    /// Player.ApplyPreWriteDamageClassAbilities and assign the result back over their own damage figure;
    /// that dispatch reaches this handler, which still resolves the absorb through
    /// Player.AbsorbWithManaBarrier (or AbsorbWithManaBarrierDot for the tick path, which carries no
    /// attacker):
    ///
    ///   - Player.TakeDamage                          - melee, missile, hotspots
    ///   - SpellProjectile.DamageTarget               - war, void and life bolts
    ///   - WorldObject_Magic.HandleCastSpell_Boost    - Harm
    ///   - WorldObject_Magic.HandleCastSpell_Transfer - Drain Health
    ///   - EnchantmentManager.ApplyDamageTick         - DoT ticks
    ///
    /// The DoT site is the ACCUMULATION point, not Player.TakeDamageOverTime where the barrier first
    /// landed. ApplyDamageTick used to cap its accumulated tick total to the victim's current Health before
    /// calling TakeDamageOverTime, which defeated a pre-write absorb just as thoroughly as the old
    /// post-write refund did: an absorb applied to a figure already capped at current health always leaves
    /// the victim strictly alive. The cap now happens after the absorb, and only for the per-damager credit
    /// accounting. Never call the barrier from TakeDamageOverTime again.
    ///
    /// The magic sites dispatch the PRE-WRITE hook, never the post-write IIncomingDamageAbility one, on
    /// purpose: Vengeance rides that other hook and must not begin tracking magic damage. Thorns rides it
    /// too, but now also reacts to direct magic hits (user ruling 2026-10-07) through its own by-name call
    /// at the three direct magic sites (Player.ApplyThornsOnMagicHit), never through the whole post-write
    /// dispatch. Keeping the two hooks separate is what lets the barrier cover all five sites while each
    /// post-write handler keeps exactly the damage paths it chose. Do not
    /// "simplify" any of the four away - deleting one silently restores the physical-only behaviour a
    /// player reported as a bug on 2026-09-03 ("Mana Barrier doesn't absorb magic damage; only physical").
    ///
    /// THE BARRIER NO LONGER RIDES IIncomingDamageAbility AT ALL, and must never ride it again: that hook
    /// fires after the health write, which is the entire defect above. It rides IPreWriteDamageAbility
    /// instead - the pre-write mitigation hook built for exactly this shape - so the five sites reach it
    /// through Player.ApplyPreWriteDamageClassAbilities rather than naming it by hand. It was an
    /// IPassiveStatAbility marker with five hand-written call sites until that hook existed.
    ///
    /// Sanguine Ward (Blood Mage T3) sits immediately above each of these calls and the two now share one
    /// convention. They still GATE differently and that part must not be unified: the barrier reproduces
    /// the old hook's preconditions (enabled, learned, live non-player attacker other than the victim), the
    /// ward is called unconditionally. Order at every site is: cloak proc, ward, barrier, health write.
    ///
    /// If Mana cannot pay the whole diverted share, it pays what it can (partial) and never goes negative.
    /// </summary>
    public class ManaBarrierAbility : IClassAbility, IPreWriteDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ManaBarrier,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 3,
            Name = "manabarrier",
            DisplayName = "Mana Barrier",
            Description = "6/12/18% of the damage you take is paid out of your Mana instead of your Health " +
                          "(25% cap). Higher Magic Defense increases the share. If your Mana runs short it pays " +
                          "what it can.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },
            Implemented = true,
            AffinitySkill = Skill.MagicDefense,
        };

        /// <summary>
        /// Divert band: the barrier pays a SHARE of whatever is still coming, so it runs AFTER any finite
        /// absorb pool (Sanguine Ward) has eaten what it can. That reproduces the hand-wired order at all
        /// five sites exactly, and it is also the cheaper order for the player - the Mana price is charged
        /// against the damage that was genuinely still going to land, not against damage a pool had already
        /// stopped.
        /// </summary>
        public int MitigationOrder => DamageMitigationOrder.Divert;

        /// <summary>
        /// FALSE: the barrier holds no pool and nothing of it can outlive a rank change, so the rank filter
        /// on the per-player hook cache is exactly right for it. Every hit recomputes the share from the
        /// live rank (Player.GetManaBarrierDivertShare, which calls TryGetClassAbility each time) and
        /// AbsorbWithManaBarrier re-checks the learned rank before spending any Mana, so an unlearn stops
        /// the barrier on the very next hit - which is precisely what it did before it was dispatched from a
        /// hook. Contrast Sanguine Ward, whose granted ward genuinely does outlive its rank.
        /// </summary>
        public bool RunsWithoutLearnedRank => false;

        /// <summary>
        /// RANK-GATED. Reached only while the player currently holds a rank, because
        /// <see cref="RunsWithoutLearnedRank"/> is false and the hook cache filters on rank; that matches
        /// the barrier's own behaviour exactly, since it recomputes its share from rank on every hit and
        /// carries no state that could be stranded. <paramref name="rank"/> is therefore always 1 or more
        /// here, and is unused only because the entry points below re-read it themselves.
        ///
        /// Routes the hit through the barrier's own entry points, which still own the gating and the Mana
        /// spend (Player_ClassAbilityBuffs). The DoT tick takes the sourceless entry point; every other site
        /// requires the dispatch's filtered attacker, which is where the barrier DIVERGES from Sanguine Ward
        /// and must keep diverging - the barrier never absorbs PvP or self-damage, the ward always does.
        /// Both entry points re-apply their own gates, which is redundant with the dispatch and deliberately
        /// kept: it means neither method becomes unsafe if anything ever calls it by name again.
        /// </summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            if (context.IsDamageOverTime)
                context.Damage = defender.AbsorbWithManaBarrierDot(context.Damage);

            else if (context.Attacker != null)
                context.Damage = defender.AbsorbWithManaBarrier(context.Attacker, context.Damage);
        }

        /// <summary>
        /// The fraction of incoming damage diverted to Mana at a given rank: base + (rank-1)*step, plus the
        /// Magic Defense rider, plus the Mana Barrier equipment mod, clamped to <paramref name="cap"/>.
        /// <paramref name="gearModFraction"/> is the Mana Barrier equipment mod, a STANDALONE mod: same axis,
        /// additive, NOT rank-gated, so at rank 0 this degenerates to just the (capped) gear share. Defaults
        /// to 0, which reproduces the pre-equipment-mod behavior exactly. Pure for testability.
        /// </summary>
        public static double DivertShare(int rank, double baseShare, double stepPerRank, double magicDefFraction, double cap, double gearModFraction = 0.0)
        {
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (rank <= 0 && gearBonus <= 0.0)
                return 0.0;

            var abilityShare = rank <= 0 ? 0.0 : baseShare + (rank - 1) * stepPerRank + Math.Max(0.0, magicDefFraction);

            return Math.Clamp(abilityShare + gearBonus, 0.0, Math.Max(0.0, cap));
        }

        /// <summary>
        /// How much of the hit the barrier soaks up and how much Mana that costs, given a diverted share
        /// and the Mana actually on hand. Pure for testability.
        ///
        /// The field is DamageAbsorbed, not "HealthRestored": nothing restores Health anywhere any more.
        /// Every consumer subtracts this from the incoming damage BEFORE the vital write.
        /// </summary>
        public readonly struct Divert
        {
            public uint DamageAbsorbed { get; init; }
            public uint ManaSpent { get; init; }
        }

        /// <summary>
        /// Resolves the diverted share against the Mana available: the full amount when it is affordable,
        /// otherwise as much as <paramref name="currentMana"/> can buy at <paramref name="manaPerHealth"/>
        /// mana per point of health. Never spends more Mana than is present, and never returns a negative
        /// or unaffordable result. Pure.
        /// </summary>
        public static Divert Resolve(uint damageTaken, double share, uint currentMana, double manaPerHealth)
        {
            if (damageTaken == 0 || share <= 0.0 || currentMana == 0)
                return default;

            var rate = manaPerHealth > 0.0 ? manaPerHealth : 1.0;

            var wantedHealth = (uint)Math.Floor(damageTaken * Math.Max(0.0, share));
            if (wantedHealth == 0)
                return default;

            var manaNeeded = (uint)Math.Ceiling(wantedHealth * rate);

            if (manaNeeded <= currentMana)
                return new Divert { DamageAbsorbed = wantedHealth, ManaSpent = manaNeeded };

            // partial: buy as much absorb as the remaining mana covers
            var affordableHealth = (uint)Math.Floor(currentMana / rate);
            if (affordableHealth == 0)
                return default;

            var manaSpent = (uint)Math.Ceiling(affordableHealth * rate);
            if (manaSpent > currentMana)
                manaSpent = currentMana;

            return new Divert { DamageAbsorbed = affordableHealth, ManaSpent = manaSpent };
        }

        /// <summary>
        /// What the barrier leaves of an incoming hit, and what that cost: DamageAfter is the number the
        /// caller writes to Health, DamageAbsorbed is the number reported in the "absorbs N points" line,
        /// ManaSpent is what leaves the Mana pool. DamageAfter + DamageAbsorbed == incomingDamage always.
        /// </summary>
        public readonly struct Absorbed
        {
            public uint DamageAfter { get; init; }
            public uint DamageAbsorbed { get; init; }
            public uint ManaSpent { get; init; }
        }

        /// <summary>
        /// Resolves an incoming hit against the barrier and returns the damage that should still reach
        /// Health. Wraps <see cref="Resolve"/>; the caller spends ManaSpent and applies DamageAfter.
        ///
        /// THIS FUNCTION TAKES NO HEALTH INPUT, BY DESIGN. That is the core of the 2026-09-08 overkill fix
        /// and it is structural, not a tuning change. The barrier used to run as a refund AFTER the health
        /// write, which meant it was handed a damage figure that had already been clamped against the
        /// victim's remaining health - so a 942-damage hit on a 477-health player arrived here as 477, the
        /// barrier refunded a share of 477, and the player survived a hit that was twice their health bar.
        ///
        /// TAKING NO HEALTH INPUT IS NECESSARY AND NOT SUFFICIENT, and the first version of this comment
        /// overclaimed by saying the error class "cannot recur". It can, from any CALLER that clamps before
        /// it calls: a figure already capped at current health leaves the victim strictly alive after any
        /// nonzero absorb, wherever the capping happened. Exactly one caller did that -
        /// EnchantmentManager.ApplyDamageTick capped its accumulated DoT total to current Health before
        /// handing it over - and it was fixed the same day by moving the absorb above the cap. The honest
        /// statement is the pair: this function reads no health-clamped figure at any of the five sites,
        /// AND the one upstream caller that used to hand it one no longer does. No residual path is known
        /// as of 2026-09-08. A new caller must feed this the damage as thrown, never a health-capped share
        /// of it.
        ///
        /// THE CALLER MUST APPLY THE RESULT BEFORE ITS VITAL WRITE. Applying it afterwards reintroduces
        /// exactly the bug this shape exists to prevent.
        ///
        /// <paramref name="share"/> is clamped into [0, 1] here: the barrier can absorb all of a hit but
        /// never more than was thrown, whatever an out-of-range tunable says. In normal operation the
        /// share arrives already capped by <see cref="DivertShare"/>, so this clamp is inert.
        /// </summary>
        public static Absorbed AbsorbDamage(uint incomingDamage, double share, uint currentMana, double manaPerHealth)
        {
            var divert = Resolve(incomingDamage, Math.Clamp(share, 0.0, 1.0), currentMana, manaPerHealth);

            return new Absorbed
            {
                DamageAfter = incomingDamage - divert.DamageAbsorbed,
                DamageAbsorbed = divert.DamageAbsorbed,
                ManaSpent = divert.ManaSpent,
            };
        }

        /// <summary>
        /// Mirrors the terms fed into DivertShare above (x100 for display, "%" since it's a share of
        /// damage) - the two must stay in step. Affinity is a MULTIPLIER on the rank share (migrated off the
        /// legacy additive Magic Defense rider, matching Soul Tether's 2026-09-12 migration), reported as the
        /// RAW (unclamped) AMOUNT it adds so the three displayed terms stay in one unit; Effective applies
        /// the same Math.Clamp(abilityShare + gearBonus, 0.0, cap) DivertShare uses. CapNote is set only when
        /// the clamp actually reduced this call's value.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseShare = PropertyManager.GetDouble("class_ability_manabarrier_base").Item;
            var stepPerRank = PropertyManager.GetDouble("class_ability_manabarrier_step").Item;
            var cap = PropertyManager.GetDouble("class_ability_manabarrier_max_share").Item;

            var skillShare = rank <= 0 ? 0.0 : baseShare + (rank - 1) * stepPerRank;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.MagicDefense);

            var rawAffinity = Math.Max(0.0, skillShare * multiplier - skillShare);

            var gearBonus = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.ManaBarrier));

            var abilityShare = skillShare + rawAffinity;
            var uncapped = abilityShare + gearBonus;
            var clamped = Math.Clamp(uncapped, 0.0, Math.Max(0.0, cap));

            var skill = skillShare * 100.0;
            var affinity = rawAffinity * 100.0;
            var gear = gearBonus * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = clamped * 100.0,
                Unit = "%",
                Label = "dmg to mana",
                Per = null,
                CapNote = clamped < uncapped - 0.0000001 ? "barrier cap" : null,
            };
        }
    }
}
