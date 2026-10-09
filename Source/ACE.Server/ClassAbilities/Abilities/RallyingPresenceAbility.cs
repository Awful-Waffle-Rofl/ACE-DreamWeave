using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Vanguard T1 splash: fellows and combat pets within 15 metres take 1/2/3/4/5% less damage by rank, and
    /// the Vanguard takes HALF that reduction personally. The asymmetry is the design: the entry is an aura
    /// the class carries for the group, deliberately worth less to a solo Vanguard than to a grouped one,
    /// which is what keeps a defensive T1 from simply being a private mitigation stack.
    ///
    /// MECHANIC LIVE. Read at the shared incoming-damage rating choke point every attacker's damage
    /// calculation already passes through - Creature.GetDamageResistRatingMod, the SAME method
    /// Battle Hardened's self-only reduction already rides (Creature_Rating.cs). Unlike Battle Hardened this
    /// reads a DIFFERENT player's learned rank: <see cref="RallyingPresenceMath.FindBestProvider"/> walks the
    /// defender's own fellowship (or the defender's pet owner's fellowship) looking for the strongest
    /// Rallying Presence within radius, so it works identically whether the defender is a Player or a
    /// CombatPet. No enchantment is used - unlike Hunter's Mark, this is a continuously-live proximity
    /// condition (it must turn off the instant the Vanguard steps out of range or dies), not a timed mark,
    /// so a duration-based enchantment would be the wrong shape here.
    ///
    /// STACKING/CEILING: see the PR notes - damage reduction is a shared, additive axis with Battle
    /// Hardened, Soul Tether (combat pets only) and Mana Barrier. NO cap tunable is registered for this
    /// entry; a cross-class ceiling number was deliberately NOT invented here - see
    /// RallyingPresenceMath.DamageMultiplier's own clamp-at-zero note and the PR report for the arithmetic.
    ///
    /// PvE ONLY: a Player attacker never triggers this reduction, consistent with Battle Hardened/Soul
    /// Tether/Mana Barrier's own PvP exclusion at the same choke point. Benefiting another PLAYER (the
    /// fellow being defended) is explicitly fine - only a HOSTILE effect against a player is forbidden, and
    /// this is never hostile.
    ///
    /// AFFINITY: Assess Person, multiplying the reduction (rank * percent_per_rank * affinity, uncapped -
    /// no affinity-cap tunable is registered for this entry).
    /// </summary>
    public class RallyingPresenceAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.RallyingPresence,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 1,
            Name = "rallying_presence",
            DisplayName = "Rallying Presence",
            Description = "Fellows and combat pets within 15 metres take 1/2/3/4/5% less damage (by rank). " +
                          "You take half that reduction yourself. Higher Assess Person multiplies the " +
                          "reduction.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.AssessPerson,
        };

        /// <summary>
        /// Mirrors RallyingPresenceMath.ReductionFraction, reported as ability-level percentages (the FULL,
        /// ally-facing rate - the self-share halving is an application-time detail applied only when the
        /// Vanguard's own body is the one taking damage, not part of this readout). No gear term is
        /// registered for this entry.
        ///
        /// NO AFFINITY CAP is registered for this ability, but the delivered reduction still has an
        /// ARITHMETIC ceiling: RallyingPresenceMath.DamageMultiplier clamps 1.0 - reduction to [0, 1], so a
        /// reduction past 100% cannot be received - it would mean negative incoming damage. The readout
        /// therefore clamps the AFFINITY term (never the rank term, which cannot reach 100% at this
        /// ability's ranks and per-rank rate) so the three displayed values still sum to a reduction the
        /// player can actually get, and sets CapNote when that bites. Reporting a raw 120% that the
        /// mechanic silently delivers as 100% is the panel lying about what it grants; this is the same
        /// clamped-readout rule the capped abilities follow, applied to an arithmetic ceiling rather than a
        /// tunable one. The ceiling here is NOT a balance decision and is not the cross-class cap the
        /// overhaul's PR report raises as an open question - if a real cap tunable is signed off later, it
        /// belongs in ReductionFraction and this clamp stays underneath it as a floor.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_rallyingpresence_percent_per_rank").Item;

            var skillPercent = rank <= 0 ? 0.0 : rank * perRank;

            var affinityMultiplier = player.GetClassAbilityAffinityMultiplier(Skill.AssessPerson);
            var affinityAdded = skillPercent * affinityMultiplier - skillPercent;

            var skill = skillPercent * 100.0;
            var affinity = affinityAdded * 100.0;

            // 100% is where DamageMultiplier's clamp bites: beyond it the defender would take negative
            // damage. Clamp the affinity term so Skill + Affinity == Effective still holds.
            const double deliverableCeiling = 100.0;

            var capBites = skill + affinity > deliverableCeiling;

            if (capBites)
                affinity = Math.Max(0.0, deliverableCeiling - skill);

            var effective = skill + affinity;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = effective,
                Unit = "%",
                Label = "dmg reduction",
                Per = null,
                CapNote = capBites ? "100% reduction ceiling" : null,
            };
        }
    }

    /// <summary>
    /// Pure arithmetic + provider-selection helpers for Rallying Presence, split out from the ability class
    /// so the math is testable without a live Player (which the test host cannot construct - see
    /// BreakArmorAbility.Chance and its tests for the established shape).
    /// </summary>
    public static class RallyingPresenceMath
    {
        /// <summary>
        /// The FULL (ally-facing) reduction fraction at a given rank: rank * percentPerRank, multiplied by
        /// the Assess Person affinity factor. Pure for testability. Returns 0 for rank &lt;= 0.
        /// </summary>
        public static double ReductionFraction(int rank, double percentPerRank, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            return rank * percentPerRank * affinityMultiplier;
        }

        /// <summary>
        /// Applies the self-share halving: the Vanguard's own body takes only this fraction of the full
        /// reduction above. Never applied to the Vanguard's own combat pet or to any other fellow - only to
        /// the Vanguard's own person (see RallyingPresenceAbility.GetIncomingDamageMod's identity check).
        /// </summary>
        public static double SelfShareFraction(double fullReduction, double selfShare) => fullReduction * Math.Max(0.0, selfShare);

        /// <summary>
        /// The damage multiplier for a given (already self-share-adjusted, if applicable) reduction fraction:
        /// 1.0 - reduction, clamped to [0, 1] so an uncapped stack can never invert into bonus damage or a
        /// negative multiplier. This clamp is a safety floor, not the cross-class ceiling the PR report
        /// discusses - it only prevents an arithmetic absurdity, it does not bound the axis.
        /// </summary>
        public static float DamageMultiplier(double reductionFraction) => (float)Math.Clamp(1.0 - reductionFraction, 0.0, 1.0);
    }

    /// <summary>
    /// Cross-Player/CombatPet resolution for Rallying Presence, kept separate from the pure math above
    /// because it needs live Player/Fellowship state and so cannot be unit-tested the same way.
    /// </summary>
    public static class RallyingPresenceProvider
    {
        /// <summary>
        /// The incoming-damage multiplier Rallying Presence contributes for <paramref name="defender"/>
        /// against <paramref name="attacker"/> (1.0 = no reduction). Called from
        /// Creature.GetDamageResistRatingMod, so it runs on every hit against any Player or CombatPet,
        /// regardless of which player (if any) actually owns the ability.
        ///
        /// PvE ONLY, matching Battle Hardened/Soul Tether/Mana Barrier at the same choke point: a Player
        /// attacker (PvP) never triggers this.
        ///
        /// Resolves the "owner" whose fellowship is searched: the defender itself if it is a Player, or the
        /// defender's owner if it is a CombatPet (Pets that are not CombatPets - e.g. non-combat scout pets -
        /// are out of scope, matching the ability's own "combat pets" wording). A solo owner (no fellowship)
        /// still checks themselves, mirroring Player.GetFellowshipTargets' own solo fallback.
        ///
        /// Takes the STRONGEST qualifying Vanguard in range rather than stacking multiple - Rallying
        /// Presence does not stack with itself across two different Vanguards.
        /// </summary>
        public static float GetIncomingDamageMod(Creature defender, WorldObject attacker)
        {
            if (defender == null || attacker is Player)
                return 1.0f;

            Player owner = defender as Player;
            if (owner == null && defender is CombatPet pet)
                owner = pet.P_PetOwner;

            if (owner == null || defender.Location == null)
                return 1.0f;

            IEnumerable<Player> candidates = owner.Fellowship != null
                ? owner.Fellowship.GetFellowshipMembers().Values
                : new[] { owner };

            var radius = PropertyManager.GetDouble("class_ability_rallyingpresence_radius").Item;
            var perRank = PropertyManager.GetDouble("class_ability_rallyingpresence_percent_per_rank").Item;
            var selfShare = PropertyManager.GetDouble("class_ability_rallyingpresence_self_share").Item;

            var best = 0.0;

            foreach (var vanguard in candidates)
            {
                if (vanguard?.Location == null)
                    continue;

                if (!vanguard.TryGetClassAbility(ClassAbilityId.RallyingPresence, out var rank))
                    continue;

                if (vanguard.Location.DistanceTo(defender.Location) > radius)
                    continue;

                var affinity = vanguard.GetClassAbilityAffinityMultiplier(Skill.AssessPerson);
                var reduction = RallyingPresenceMath.ReductionFraction(rank, perRank, affinity);

                // Only the Vanguard's own BODY gets the halved self-share - their own combat pet (a
                // different WorldObject, same owner) still gets the full ally rate, exactly like every
                // other fellow's pet does.
                if (ReferenceEquals(defender, vanguard))
                    reduction = RallyingPresenceMath.SelfShareFraction(reduction, selfShare);

                if (reduction > best)
                    best = reduction;
            }

            return RallyingPresenceMath.DamageMultiplier(best);
        }
    }
}
