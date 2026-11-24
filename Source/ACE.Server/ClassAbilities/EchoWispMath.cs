using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Entity.Enum;

namespace ACE.Server.ClassAbilities
{
    /// <summary>How an Echo Cast wisp decides whether to stay on its target (design "Targeting", rule 2).</summary>
    public enum EchoWispSpellKind
    {
        /// <summary>Damage spells and harmful vital drains or transfers: stay on the target until it dies.</summary>
        Damage,

        /// <summary>Harmful enchantments: stay on the target until it carries this debuff.</summary>
        Debuff,

        /// <summary>Dispels: one successful cast per target, then move on.</summary>
        Dispel,

        /// <summary>
        /// Heal and Revitalize (owner, 2026-11-23): a beneficial Boost of Health or Stamina, cast only at the caster or a
        /// member of the caster's fellowship. A Self spell stays on the caster and idles at full; an Other spell stays on
        /// its target until that target is full, then moves on to the most hurt legal target (<see cref="EchoWispMath.SelectRestoreTarget"/>).
        /// </summary>
        Restore,
    }

    /// <summary>
    /// Why a creature is not a legal Echo Cast wisp target (<see cref="EchoWispMath.IneligibleReason"/>), in the order
    /// the controller tests them. Teleporting and NoDebuffHolder are judged by the controller itself (EchoWisp.IsLegalTarget)
    /// from live state the pure rule does not take.
    /// </summary>
    public enum EchoWispIneligible
    {
        None,
        Player,
        Pet,
        Dead,
        Unattackable,
        Hidden,
        Done,
        Teleporting,
        SameFaction,

        /// <summary>
        /// Not attacking the caster, a pet, a fellow or a fellow's pet, and never damaged by the caster
        /// (EchoWispEngagement.IsEngaged), with /summonaggro off.
        /// </summary>
        NotInCastersFight,
        NoDebuffHolder,
        CarriesDebuff,

        /// <summary>A Restore wisp's candidate is not a player (a monster, a pet): heals never land on one.</summary>
        NotPlayer,

        /// <summary>A Restore wisp's candidate is a player who is neither the caster nor in the caster's fellowship.</summary>
        NotCasterOrFellow,

        /// <summary>
        /// A Restore wisp's candidate (a fellow, not the caster) is in a PvP state: not NPK, a running PK timer, in a PvP
        /// match, or under class-ability suppression. Class abilities never reach PvP (<see cref="EchoWispMath.IsPvpSafeRestoreTarget"/>).
        /// </summary>
        NotPvpSafe,
    }

    /// <summary>
    /// What one wisp target pick saw, for the per-cast DEBUG line (EchoWisp.CastOnOwnerThread), so a stage log answers
    /// "why didn't it cast" with one grep: the held target's fate, how many candidates the owner could see, how many were
    /// turned away and why, how many legal ones were out of range, and how many sight tests ran and failed. Filled by the
    /// delegates the controller hands HuntersEchoMath.SelectTarget, so it counts exactly what the pick did. One per pick,
    /// on the owner's thread.
    /// </summary>
    public sealed class EchoWispPickTally
    {
        // computed once: a tally is built on every wisp cast while DEBUG logging is on, which it is in the docker image
        private static readonly int ReasonCount = Enum.GetValues(typeof(EchoWispIneligible)).Length;

        private readonly int[] ineligible = new int[ReasonCount];

        /// <summary>Whether the pick started from a held target (the original cast's, or the one the last cast hit).</summary>
        public bool HadHeld { get; set; }

        /// <summary>Why the held target was dropped (None while it was kept, or when there was none).</summary>
        public EchoWispIneligible HeldIneligible { get; set; }

        public bool HeldOutOfRange { get; set; }

        public bool HeldSightFailed { get; set; }

        /// <summary>Creatures the owner could see (non-player), the held target included.</summary>
        public int Candidates { get; set; }

        /// <summary>Legal candidates (held target excluded) farther than <see cref="Range"/>.</summary>
        public int OutOfRange { get; set; }

        /// <summary>Legal, in-range candidates (held target excluded).</summary>
        public int InRange { get; set; }

        /// <summary>Sight tests run on candidates (held target excluded).</summary>
        public int SightTested { get; set; }

        public int SightFailed { get; set; }

        /// <summary>The cast range the pick used, in metres.</summary>
        public float Range { get; set; }

        public int Ineligible(EchoWispIneligible reason) => ineligible[(int)reason];

        public int IneligibleTotal
        {
            get
            {
                var total = 0;
                for (var i = 1; i < ineligible.Length; i++)
                    total += ineligible[i];
                return total;
            }
        }

        public void RecordIneligible(EchoWispIneligible reason)
        {
            if (reason != EchoWispIneligible.None)
                ineligible[(int)reason]++;
        }

        /// <summary>In-range legal candidates the sight budget never reached.</summary>
        public int SightUntested => Math.Max(0, InRange - SightTested);

        /// <summary>
        /// One line: "held: ...; candidates N: ineligible M (NotInCastersFight 3, Dead 1), out of range R (range 75.0 m), in range I,
        /// sight failed F of T tested, U untested".
        /// </summary>
        public string Describe()
        {
            string held;
            if (!HadHeld)
                held = "none";
            else if (HeldIneligible != EchoWispIneligible.None)
                held = $"dropped ({HeldIneligible})";
            else if (HeldOutOfRange)
                held = "dropped (out of range)";
            else if (HeldSightFailed)
                held = "dropped (no sight)";
            else
                held = "kept";

            var reasons = new List<string>();
            for (var i = 1; i < ineligible.Length; i++)
            {
                if (ineligible[i] > 0)
                    reasons.Add($"{(EchoWispIneligible)i} {ineligible[i]}");
            }

            var reasonText = reasons.Count > 0 ? $" ({string.Join(", ", reasons)})" : "";

            return $"held: {held}; candidates {Candidates}: ineligible {IneligibleTotal}{reasonText}, out of range {OutOfRange} (range {Range:0.0} m), in range {InRange}, sight failed {SightFailed} of {SightTested} tested, {SightUntested} untested";
        }
    }

    /// <summary>
    /// The pure rules of an Echo Cast wisp (Archmage T3 rework, Docs/ClassAbilities/ARCHMAGE-ECHO-CAST-WISP-DESIGN.md
    /// and -PLAN.md): which casts qualify, which body a school gets, where it spawns, how the tunables are read, when
    /// it ends, which creatures it may cast at and whether it stays on one. Everything here is a function of its
    /// arguments, so the controller (<see cref="EchoWisp"/>) holds only the wiring.
    /// </summary>
    public static class EchoWispMath
    {
        // ---- bodies --------------------------------------------------------------------------------------

        public const uint WarBody = 1008600;              // red
        public const uint LifeBody = 1008601;             // blue
        public const uint VoidBody = 1008602;             // purple
        public const uint CreatureBody = 1008603;         // green
        public const uint ItemEnchantmentBody = 1008604;  // white (owner, 2026-11-22)

        /// <summary>The body weenie for a wisp casting a spell of <paramref name="school"/> (owner: colour by school).</summary>
        public static uint BodyWcidFor(MagicSchool school)
        {
            switch (school)
            {
                case MagicSchool.LifeMagic: return LifeBody;
                case MagicSchool.VoidMagic: return VoidBody;
                case MagicSchool.CreatureEnchantment: return CreatureBody;
                case MagicSchool.ItemEnchantment: return ItemEnchantmentBody;
                default: return WarBody;
            }
        }

        // ---- qualifying casts ----------------------------------------------------------------------------

        /// <summary>
        /// Whether a cast may spawn a wisp. Two families:
        ///  - a harmful spell of an allowlisted MetaSpellType (IsHarmful alone is only !IsBeneficial and would let
        ///    portal, recall and summon spells through), aimed at a non-player, non-pet creature, or untargeted only for
        ///    a projectile spell (a ring or a wall). Never a harmful cast at a player: class abilities never reach PvP.
        ///  - a Restore spell (<see cref="IsRestore"/>: Heal or Revitalize, owner 2026-11-23) aimed at the caster or a
        ///    member of the caster's fellowship (<paramref name="targetIsCasterOrFellow"/>), and at nothing else. A Self
        ///    spell is aimed at the caster (the trigger passes the caster as its target).
        /// <paramref name="vital"/> is the spell's boosted vital (Spell.VitalDamageType); Undef for a spell that has none.
        /// </summary>
        public static bool Qualifies(SpellType metaSpellType, bool isHarmful, bool isProjectile, bool hasTarget, bool targetIsCreature, bool targetIsPlayer, bool targetIsPet,
            DamageType vital = DamageType.Undef, bool targetIsCasterOrFellow = false)
        {
            if (IsRestore(metaSpellType, !isHarmful, vital))
                return hasTarget && targetIsPlayer && targetIsCasterOrFellow;

            if (!isHarmful || !IsAllowedType(metaSpellType))
                return false;

            if (!hasTarget)
                return isProjectile;

            return targetIsCreature && !targetIsPlayer && !targetIsPet;
        }

        /// <summary>
        /// Why Player.CastAsEchoWisp refuses a wisp cast at its target, or null when the target is allowed. A harmful
        /// spell never lands on a player ("player target": class abilities never reach PvP), fellow or not. A Restore
        /// spell (<see cref="IsRestore"/>) lands ONLY on a player, and only on the caster or a member of the caster's
        /// fellowship: never on a monster, a pet or a stranger, and never on a fellow in a PvP state
        /// (<paramref name="targetIsPvpSafe"/>, <see cref="IsPvpSafeRestoreTarget"/>: class abilities never reach PvP).
        /// </summary>
        public static string CastTargetRefusal(bool isRestore, bool hasTarget, bool targetIsPlayer, bool targetIsCasterOrFellow, bool targetIsPvpSafe)
        {
            if (isRestore)
            {
                if (!hasTarget || !targetIsPlayer)
                    return "restore target is not a player";

                if (!targetIsCasterOrFellow)
                    return "restore target is not the caster or a fellow";

                return targetIsPvpSafe ? null : "restore target is not PvP-safe";
            }

            return targetIsPlayer ? "player target" : null;
        }

        public static bool IsAllowedType(SpellType metaSpellType)
        {
            switch (metaSpellType)
            {
                case SpellType.Projectile:
                case SpellType.LifeProjectile:
                case SpellType.EnchantmentProjectile:
                case SpellType.Enchantment:
                case SpellType.Boost:
                case SpellType.Transfer:
                case SpellType.Dispel:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether a spell is a Restore spell (owner, 2026-11-23): the Heal and Revitalize families, Self and Other, told
        /// apart by spell data, never by name - a beneficial Boost (MetaSpellType Boost, not FellowBoost) whose boosted
        /// vital (Spell.VitalDamageType) is Health or Stamina. Mana Boost, buffs, portals and every other beneficial
        /// spell stay excluded; a harmful Boost (Harm, Enfeeble) stays a Damage spell.
        /// </summary>
        public static bool IsRestore(SpellType metaSpellType, bool isBeneficial, DamageType vital) =>
            metaSpellType == SpellType.Boost && isBeneficial && (vital == DamageType.Health || vital == DamageType.Stamina);

        /// <summary>The stay-or-move-on family of a qualifying spell: <see cref="EchoWispSpellKind.Restore"/> for a Restore spell, else by type.</summary>
        public static EchoWispSpellKind KindOf(SpellType metaSpellType, bool isBeneficial, DamageType vital) =>
            IsRestore(metaSpellType, isBeneficial, vital) ? EchoWispSpellKind.Restore : KindOf(metaSpellType);

        /// <summary>The stay-or-move-on family of an allowlisted harmful spell type.</summary>
        public static EchoWispSpellKind KindOf(SpellType metaSpellType)
        {
            switch (metaSpellType)
            {
                case SpellType.Enchantment:
                case SpellType.EnchantmentProjectile:
                    return EchoWispSpellKind.Debuff;
                case SpellType.Dispel:
                    return EchoWispSpellKind.Dispel;
                default:
                    return EchoWispSpellKind.Damage;
            }
        }

        // ---- tunables, read defensively -------------------------------------------------------------------

        /// <summary>The shortest gap the interval pair may give: the controller's watch tick.</summary>
        public const double WatchTickSeconds = 0.5;

        /// <summary>
        /// The interval pair, clamped so min is at least <see cref="WatchTickSeconds"/> (NaN reads as that floor) and
        /// max is at least min (NaN reads as min).
        /// </summary>
        public static (double Min, double Max) ClampIntervals(double min, double max)
        {
            var lo = double.IsNaN(min) || double.IsInfinity(min) ? WatchTickSeconds : Math.Max(WatchTickSeconds, min);
            var hi = double.IsNaN(max) || double.IsInfinity(max) ? lo : Math.Max(lo, max);
            return (lo, hi);
        }

        /// <summary>One gap drawn uniformly from [min, max] by <paramref name="unit"/> in [0, 1] (clamped).</summary>
        public static double Interval(double min, double max, double unit)
        {
            var (lo, hi) = ClampIntervals(min, max);
            var u = double.IsNaN(unit) ? 0.0 : Math.Clamp(unit, 0.0, 1.0);
            return lo + (hi - lo) * u;
        }

        /// <summary>The cap on live wisps per caster, never below 0.</summary>
        public static int ClampMaxActive(long maxActive) => (int)Math.Clamp(maxActive, 0L, int.MaxValue);

        /// <summary>The wisp's life in seconds, never negative; NaN or infinity reads as 0 (no wisp).</summary>
        public static double ClampDuration(double seconds) => double.IsNaN(seconds) || double.IsInfinity(seconds) ? 0.0 : Math.Max(0.0, seconds);

        /// <summary>How long an orphaned body lasts past the wisp's own life before it rots on its own (plan ruling).</summary>
        public const double RotGraceSeconds = 30.0;

        /// <summary>The body's TimeToRot: the wisp's duration plus <see cref="RotGraceSeconds"/>.</summary>
        public static double TimeToRot(double durationSeconds) => ClampDuration(durationSeconds) + RotGraceSeconds;

        /// <summary>Whether one more wisp may spawn for a caster who already has <paramref name="active"/> out.</summary>
        public static bool UnderCap(int active, long maxActive) => active < ClampMaxActive(maxActive);

        // ---- placement -----------------------------------------------------------------------------------

        /// <summary>How far to the caster's side the wisp appears (owner, 2026-11-22: about 1.5 m, never on the caster).</summary>
        public const float SideOffset = 1.5f;

        /// <summary>
        /// A spawn spot is taken when one of this caster's live wisps is within this distance of it (owner, 2026-11-23).
        /// </summary>
        public const float SpotTakenRadius = 1.0f;

        /// <summary>
        /// The points the wisp tries to spawn at, in order (owner, 2026-11-23): the caster's right, left, behind and in
        /// front, then the four diagonals front-right, front-left, back-right and back-left. Every one is
        /// <see cref="SideOffset"/> from the caster in the horizontal plane (the diagonals too) and at the caster's own
        /// height (Z unchanged). Never the caster's own spot: a wisp there would lose its bolts to the caster (plan
        /// section 5, first risk).
        /// </summary>
        public static IReadOnlyList<Vector3> SpawnCandidates(Vector3 casterOrigin, Quaternion casterRotation)
        {
            // AC headings: +Y is forward, +X is the right hand (WorldObject_Magic.CalculateProjectileVelocity launches
            // an untargeted spell along Transform(UnitY, rotation))
            var forward = Flatten(Vector3.Transform(Vector3.UnitY, casterRotation));
            var right = new Vector3(forward.Y, -forward.X, 0.0f);

            var diagonal = SideOffset / MathF.Sqrt(2.0f);

            return new[]
            {
                casterOrigin + right * SideOffset,
                casterOrigin - right * SideOffset,
                casterOrigin - forward * SideOffset,
                casterOrigin + forward * SideOffset,
                casterOrigin + (forward + right) * diagonal,
                casterOrigin + (forward - right) * diagonal,
                casterOrigin + (-forward + right) * diagonal,
                casterOrigin + (-forward - right) * diagonal,
            };
        }

        /// <summary>
        /// Whether <paramref name="spot"/> is taken: one of <paramref name="liveWisps"/> (the caster's live wisp bodies, in
        /// the same frame as the spot) is within <see cref="SpotTakenRadius"/> of it, measured in the horizontal plane only
        /// (Z ignored), so a wisp left behind at another height still holds the spot above or below it. Null or empty:
        /// nothing is taken.
        /// </summary>
        public static bool IsSpotTaken(Vector3 spot, IEnumerable<Vector3> liveWisps)
        {
            if (liveWisps == null)
                return false;

            foreach (var wisp in liveWisps)
            {
                var dx = spot.X - wisp.X;
                var dy = spot.Y - wisp.Y;

                if (dx * dx + dy * dy <= SpotTakenRadius * SpotTakenRadius)
                    return true;
            }

            return false;
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.Z = 0.0f;
            return v.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(v);
        }

        /// <summary>
        /// The heading that faces <paramref name="to"/> from <paramref name="from"/>, the same yaw LaunchSpellProjectiles
        /// computes for a targeted projectile (Atan2(-dx, dy) about Z); null when the two are on top of each other.
        /// </summary>
        public static Quaternion? FacingToward(Vector3 from, Vector3 to)
        {
            var d = to - from;
            d.Z = 0.0f;

            if (d.LengthSquared() < 1e-6f)
                return null;

            return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.Atan2(-d.X, d.Y));
        }

        // ---- launcher -------------------------------------------------------------------------------------

        /// <summary>
        /// The launcher a wisp cast hands to HandleCastSpell (and so to LaunchSpellProjectiles' ProjectileLauncher): the
        /// proc weapon of a Spellsword proc wisp while its owner still holds it, else what a hand-cast wisp uses (the
        /// owner's equipped wand) - the Hunter's Echo fallback for a weapon that left the owner.
        /// </summary>
        public static T Launcher<T>(T procWeapon, bool procWeaponStillOwned, T handCastLauncher) where T : class =>
            procWeapon != null && procWeaponStillOwned ? procWeapon : handCastLauncher;

        // ---- end conditions ------------------------------------------------------------------------------

        /// <summary>A wisp ends once its caster is more than this far away (design: fixed, not a tunable).</summary>
        public const float LeashDistance = 40.0f;

        /// <summary>
        /// Why the wisp must end now, or null while it may stay. Modelled on CombatPet.HuntersEchoEndReason: its
        /// time is up, its body is gone, its caster is gone, dead or portaling, its caster's class abilities are
        /// suppressed ("suppressed": class abilities never reach PvP), on another landblock or instance, or more than
        /// <see cref="LeashDistance"/> away.
        /// </summary>
        public static string EndReason(double now, double expiresAt, bool bodyPresent, bool ownerPresent, bool ownerDead,
            bool ownerTeleporting, bool ownerSuppressed, bool sameLandblock, bool sameInstance, float distance)
        {
            if (now >= expiresAt)
                return "expire";

            if (!bodyPresent)
                return "bodylost";

            if (!ownerPresent)
                return "ownerlost";

            if (ownerDead)
                return "ownerdeath";

            if (ownerTeleporting)
                return "ownerportal";

            // class abilities never reach PvP: a caster whose class abilities turn suppressed (the PK facet rule, an arena
            // match's mask; Player.ClassAbilitySuppressed) loses every wisp, of any kind
            if (ownerSuppressed)
                return "suppressed";

            if (!sameLandblock)
                return "ownerlandblock";

            if (!sameInstance || float.IsNaN(distance) || distance > LeashDistance)
                return "leash";

            return null;
        }

        // ---- targeting -----------------------------------------------------------------------------------

        /// <summary>
        /// The reach a harmful spell with no range data gets (0 / 0 in the dat): the caster-pet precedent,
        /// PetCasterBrain.UnboundedHarmfulReach.
        /// </summary>
        public const float UnboundedReach = 4.5f;

        /// <summary>The wisp's cast range for a spell: the player formula (Player.MaxSpellRange), or <see cref="UnboundedReach"/> when that is 0.</summary>
        public static float CastRange(float formulaRange) => formulaRange > 0.0f ? formulaRange : UnboundedReach;

        /// <summary>
        /// The cheap half of <see cref="IsEligible"/>, over fields already on the creature: never a player or a pet,
        /// alive, attackable, not hidden (Visibility, the IsPetTargetCandidate rule), and not already done (debuffed,
        /// dispelled, immune or refused). The controller tests this first so the engagement, faction and enchantment
        /// reads run only for a creature that passes it.
        /// </summary>
        public static bool PassesCheapChecks(bool isPlayer, bool isPet, bool isDead, bool attackable, bool hidden, bool done)
        {
            return !isPlayer && !isPet && !isDead && attackable && !hidden && !done;
        }

        /// <summary>
        /// Whether a creature is a legal target for the wisp: <see cref="PassesCheapChecks"/>; not of the caster's own
        /// faction unless it is retaliating against the caster (the combat-pet rule, CombatPet.IsPetTargetCandidate);
        /// engaged with the caster's side unless the caster has /summonaggro on; and for a debuff, not already carrying it.
        /// </summary>
        public static bool IsEligible(EchoWispSpellKind kind, bool isPlayer, bool isPet, bool isDead, bool attackable, bool hidden,
            bool sameFactionNotRetaliating, bool engagedWithOwner, bool summonAggro, bool carriesDebuff, bool done)
        {
            if (!PassesCheapChecks(isPlayer, isPet, isDead, attackable, hidden, done))
                return false;

            if (sameFactionNotRetaliating)
                return false;

            if (!engagedWithOwner && !summonAggro)
                return false;

            return kind != EchoWispSpellKind.Debuff || !carriesDebuff;
        }

        /// <summary>
        /// Why a creature is NOT a legal wisp target, or <see cref="EchoWispIneligible.None"/> when it is: the reason
        /// form of <see cref="IsEligible"/>, tested in the same order, so a wisp that idles can say why each candidate
        /// was turned away (the per-cast DEBUG line, EchoWisp.CastOnOwnerThread). None exactly when IsEligible is true
        /// for the same arguments (EchoWispControllerTests pins that over the whole truth table).
        /// </summary>
        public static EchoWispIneligible IneligibleReason(EchoWispSpellKind kind, bool isPlayer, bool isPet, bool isDead, bool attackable, bool hidden,
            bool sameFactionNotRetaliating, bool engagedWithOwner, bool summonAggro, bool carriesDebuff, bool done)
        {
            if (isPlayer)
                return EchoWispIneligible.Player;
            if (isPet)
                return EchoWispIneligible.Pet;
            if (isDead)
                return EchoWispIneligible.Dead;
            if (!attackable)
                return EchoWispIneligible.Unattackable;
            if (hidden)
                return EchoWispIneligible.Hidden;
            if (done)
                return EchoWispIneligible.Done;
            if (sameFactionNotRetaliating)
                return EchoWispIneligible.SameFaction;
            if (!engagedWithOwner && !summonAggro)
                return EchoWispIneligible.NotInCastersFight;
            if (kind == EchoWispSpellKind.Debuff && carriesDebuff)
                return EchoWispIneligible.CarriesDebuff;

            return EchoWispIneligible.None;
        }

        /// <summary>
        /// Whether one active enchantment of <paramref name="activePowers"/> (the target's registry entries in the
        /// spell's category) already holds the debuff: one at equal or higher power, the test the enchantment manager
        /// uses to decide that a new cast would not surpass it.
        /// </summary>
        public static bool CarriesDebuff(IEnumerable<uint> activePowers, uint spellPower)
        {
            if (activePowers == null)
                return false;

            foreach (var power in activePowers)
            {
                if (power >= spellPower)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether, after one cast at a target, the wisp is done with it for good: immune or refused (it cannot take the
        /// spell from this caster at all), or a dispel that landed (one per target). A resisted debuff is NOT done (the
        /// wisp tries again); a landed debuff is judged by <see cref="CarriesDebuff"/> on the next pick, and a damage
        /// target stays until it dies.
        /// </summary>
        public static bool IsDoneAfter(EchoWispSpellKind kind, EchoWispCastOutcome outcome)
        {
            switch (outcome)
            {
                case EchoWispCastOutcome.Immune:
                case EchoWispCastOutcome.Refused:
                    return true;
                case EchoWispCastOutcome.Cast:
                    return kind == EchoWispSpellKind.Dispel;
                default:
                    return false;
            }
        }

        // ---- Restore targeting (owner, 2026-11-23) ---------------------------------------------------------

        /// <summary>Why a Restore wisp idles: nobody it may heal is missing any of the vital.</summary>
        public const string IdleNobodyHurt = "nobody hurt";

        /// <summary>Why a Restore wisp idles: someone is hurt, but no hurt legal target is in range and sight of the wisp.</summary>
        public const string IdleHurtOutOfReach = "no hurt target in range and sight";

        /// <summary>
        /// Why <paramref name="isPlayer"/>-and-friends is not a legal Restore target, or None when it is: players only
        /// (never a monster or a pet), and only the caster or a member of the caster's fellowship; never a fellow in a PvP
        /// state (<paramref name="pvpSafe"/>, <see cref="IsPvpSafeRestoreTarget"/>); then alive, visible, not portaling and
        /// not done (a refused or immune cast). Tested in that order, so the cheap type, fellowship and PvP tests turn a
        /// monster, a stranger or a PK away before any vital is read.
        /// </summary>
        public static EchoWispIneligible RestoreIneligible(bool isPlayer, bool isCasterOrFellow, bool pvpSafe, bool isDead, bool hidden, bool teleporting, bool done)
        {
            if (!isPlayer)
                return EchoWispIneligible.NotPlayer;
            if (!isCasterOrFellow)
                return EchoWispIneligible.NotCasterOrFellow;
            if (!pvpSafe)
                return EchoWispIneligible.NotPvpSafe;
            if (done)
                return EchoWispIneligible.Done;
            if (isDead)
                return EchoWispIneligible.Dead;
            if (hidden)
                return EchoWispIneligible.Hidden;
            if (teleporting)
                return EchoWispIneligible.Teleporting;

            return EchoWispIneligible.None;
        }

        /// <summary>
        /// Whether a Restore wisp may heal a player (class abilities never reach PvP, a fixed owner rule): always the caster
        /// itself; any other player only while NPK, with no running PK timer, outside every PvP match, and with its class
        /// abilities not suppressed (Player.ClassAbilitySuppressed, which covers the PK facet rule and an arena match's
        /// mask). The ONE rule both the pick (EchoWisp.RestoreVerdict) and the cast guard (Player.CastAsEchoWisp) read,
        /// through EchoWispEngagement.IsPvpSafeRestoreTarget.
        /// </summary>
        public static bool IsPvpSafeRestoreTarget(bool isCaster, bool isNpk, bool pkTimerActive, bool inPvpMatch, bool classAbilitySuppressed) =>
            isCaster || (isNpk && !pkTimerActive && !inPvpMatch && !classAbilitySuppressed);

        /// <summary>current / max of a vital; 1 (full) for a max of 0, so a vital-less target never reads as hurt.</summary>
        public static float VitalFraction(uint current, uint max) => max == 0 ? 1.0f : Math.Min(1.0f, (float)current / max);

        /// <summary>Whether a vital fraction from <see cref="VitalFraction"/> is below full.</summary>
        public static bool IsHurt(float fraction) => fraction < 1.0f;

        /// <summary>
        /// The Restore wisp's target for this cast, or null with <paramref name="idleReason"/> set (owner, 2026-11-23):
        ///  - keep <paramref name="current"/> while it is legal, hurt (below max of the restored vital) and in reach;
        ///  - else the MOST hurt legal candidate (lowest <paramref name="fraction"/>) that is in reach, the caster included
        ///    when the caller lists it; ties keep the candidates' order;
        ///  - else nobody: <see cref="IdleNobodyHurt"/> when no legal candidate is hurt, <see cref="IdleHurtOutOfReach"/>
        ///    when one is but none is in reach. The wisp checks again at its next cast.
        /// <paramref name="inReach"/> is the spell range and line of sight from the wisp (always true for a Self spell,
        /// which lands on the caster). Pure: everything it decides with comes in through its delegates.
        /// </summary>
        public static T SelectRestoreTarget<T>(T current, IEnumerable<T> candidates, Func<T, bool> isLegal, Func<T, float> fraction, Func<T, bool> inReach,
            out string idleReason) where T : class
        {
            idleReason = null;

            var anyHurt = false;

            if (current != null && isLegal(current) && IsHurt(fraction(current)))
            {
                anyHurt = true;

                if (inReach(current))
                    return current;
            }

            var hurt = new List<KeyValuePair<T, float>>();

            if (candidates != null)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate == null || ReferenceEquals(candidate, current) || hurt.Exists(h => ReferenceEquals(h.Key, candidate)) || !isLegal(candidate))
                        continue;

                    var f = fraction(candidate);

                    if (IsHurt(f))
                        hurt.Add(new KeyValuePair<T, float>(candidate, f));
                }
            }

            if (hurt.Count > 0)
                anyHurt = true;

            // stable: equal fractions keep the candidates' order
            foreach (var entry in hurt.OrderBy(h => h.Value))
            {
                if (inReach(entry.Key))
                    return entry.Key;
            }

            idleReason = anyHurt ? IdleHurtOutOfReach : IdleNobodyHurt;
            return null;
        }
    }
}
