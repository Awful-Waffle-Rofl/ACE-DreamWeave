using System;
using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for WEAKENED BLOOD (Blood Mage T2 game-changer): a damaging Life Magic spell the Blood Mage
    /// lands (Harm, every Drain strike, and the Health-damage life projectiles) has a per-target chance
    /// (<see cref="ProcChance"/>) to leave the TARGET carrying a life-magic vulnerability of 2.00 / 2.50 /
    /// 3.10 by the applier's rank, plus an attack/cast slow (<see cref="SpeedFactor"/>), for 20 seconds, and
    /// applies the retail FESTER enchantment on that target so the mark is something the player can
    /// actually see land (<see cref="FesterSpell"/>).
    ///
    /// The state this backs lives on the TARGET (Creature_ClassAbilityDebuffs.cs), not on the caster,
    /// precisely so a second life caster in the fellowship reads the same debuff - BLOOD-MAGE-DESIGN sec 3.
    /// The value is fed into Creature.GetLifeVulnerabilityMod, which takes MAX against weapon rending, so
    /// life vulnerability stays one axis rather than becoming a product.
    ///
    /// THE FESTER ENCHANTMENT IS NOT WHERE THE VULNERABILITY LIVES. The two are deliberately separate: the
    /// vulnerability is still the transient target-side mark, and Fester is a real Life Magic enchantment
    /// with its own retail effect (a health-regeneration penalty) and its own visual. Reading the
    /// vulnerability out of the enchantment registry instead would put it at the mercy of
    /// EnchantmentManager.GetEnchantmentsTopLayer, which selects the surviving layer by PowerLevel and never
    /// by StatModValue - so a higher-tier Fester from any other source would silently take over a Blood
    /// Mage's mark. Keeping them apart is what makes that impossible.
    /// </summary>
    public static class WeakenedBloodMath
    {
        /// <summary>
        /// The life resistance modifier a given rank applies: 2.00 / 2.50 / 3.10 at ranks 1-3, the top three
        /// rungs of the retail vulnerability ladder (Vulnerability V, VI, and the Incantation). Rank 0 or
        /// below returns 1.0 (no vulnerability); a rank above 3 is clamped to rank 3 rather than rejected,
        /// matching how the other class abilities treat an out-of-range persisted rank.
        ///
        /// A configured value below 1.0 would be a RESISTANCE rather than a vulnerability, which this axis
        /// must never produce, so the result is floored at 1.0.
        ///
        /// <paramref name="gear"/> is the HEMORRHAGE equipment mod (EquipmentModId.Hemorrhage), added to
        /// the rank's table value on the same additive axis (DESIGN.md 3.3). WATCH THE UNIT: this method
        /// returns a BARE MULTIPLIER (2.00/2.50/3.10), not a fraction, so the gear term is in that same
        /// bare-multiplier unit - Hemorrhage's MaxMagnitude of 0.05 means +0.05 ON the multiplier, taking a
        /// rank-3 mark from 3.10 to 3.15. It defaults to 0 and adding 0.0 is exact, so an unmodded mark is
        /// bit-identical.
        ///
        /// The rank &lt;= 0 early return runs BEFORE the gear term, so a caster without Weakened Blood can
        /// never produce a vulnerability from gear alone - the mod is machinery.
        /// </summary>
        public static double ResistanceMod(int rank, double modR1, double modR2, double modR3, double gear = 0.0)
        {
            if (rank <= 0)
                return 1.0;

            var mod = (rank switch
            {
                1 => modR1,
                2 => modR2,
                _ => modR3,
            }) + gear;

            return mod < 1.0 ? 1.0 : mod;
        }

        /// <summary>
        /// The retail Fester Other tier a given rank applies, alongside the transient mark. Rank 0 or below
        /// returns <see cref="SpellId.Undef"/> (apply nothing); a rank above 3 is clamped to rank 3, matching
        /// how <see cref="ResistanceMod"/> treats an out-of-range persisted rank.
        ///
        /// HOW THE TIERS WERE CHOSEN: each rank's Fester tier sits on the SAME rung of the retail ladder as
        /// that rank's vulnerability magnitude already does. ResistanceMod's 2.00 / 2.50 / 3.10 are exactly
        /// the elemental Vulnerability Other V, VI and Incantation values (verified in ace_world.spell:
        /// Acid Vulnerability Other V = 2.0, VI = 2.5, and the Incantation = 3.1), so the mark's spell is
        /// Fester Other V, Fester Other VI, and the Incantation of Fester Other in turn. Rank 3 lands on the
        /// Incantation exactly, which is the same ceiling the resistance mod already states.
        ///
        /// Ids are the enum's, verified against ace_world.spell at write time: FesterOther5 = 175
        /// "Fester Other V", FesterOther6 = 176 "Fester Other VI", FesterOther8 = 4489 "Incantation of
        /// Fester Other". Note the rung between them is NOT named Fester: FesterOther7 = 2178 is
        /// "Decrepitude's Grasp", the retail tier-VII flavour name.
        /// </summary>
        public static SpellId FesterSpell(int rank) => rank switch
        {
            <= 0 => SpellId.Undef,
            1 => SpellId.FesterOther5,
            2 => SpellId.FesterOther6,
            _ => SpellId.FesterOther8,
        };

        /// <summary>
        /// TRUE while a debuff applied with the given expiry is still holding.
        /// </summary>
        public static bool IsActive(double now, double expireTime) => now < expireTime;

        /// <summary>
        /// Combines an incoming application with whatever the target already carries, returning the values
        /// the target should store.
        ///
        /// BOTH terms take the MAXIMUM, independently. A rank-1 blood mage refreshing a rank-3 debuff must
        /// not downgrade it to 2.00, and a rank-3 application must not shorten a longer-lived one. The
        /// existing values are ignored entirely once they have expired, so a lapsed rank-3 debuff cannot
        /// resurrect itself through a later rank-1 hit.
        /// </summary>
        public static (double Mod, double ExpireTime) Refresh(double now, double existingMod, double existingExpire, double incomingMod, double incomingExpire)
        {
            if (!IsActive(now, existingExpire))
                return (incomingMod, incomingExpire);

            return (existingMod > incomingMod ? existingMod : incomingMod,
                    existingExpire > incomingExpire ? existingExpire : incomingExpire);
        }

        /// <summary>
        /// Whether a life spell's DAMAGE reads the Weakened Blood mark. This is the single place the
        /// beneficiary ruling is encoded.
        ///
        /// APPLYING THE MARK AND BENEFITING FROM IT ARE DIFFERENT QUESTIONS. A landed Drain applies and
        /// refreshes Weakened Blood - that is unchanged and is not what this decides. What this decides is
        /// who the resulting vulnerability multiplies for.
        ///
        /// THE RULE (Blood Mage buff pass, 2026-10-17): every spell that deals HEALTH damage benefits, except
        /// Drain. That widens the 2026-08-02 list ("Blood rend/vuln should apply to harm, heca, raven") to
        /// cover the Health-damage projectiles that are not LifeProjectile-typed, such as Bloodstone Bolt
        /// (a Life Magic <see cref="SpellType.Projectile"/>, damage type Health). The Drain half of that
        /// ruling stands unchanged:
        ///
        ///  - <see cref="SpellType.Boost"/> / <see cref="SpellType.FellowBoost"/> - HARM - benefits. Harm's
        ///    damage is identified by the vital it lowers, not by a damage type: the retail Harm and Drain
        ///    rows carry no e_Type at all (ace_world.spell ids 7, 1176, 4308: e_Type NULL), so an Undef
        ///    damage type is accepted here alongside an explicit Health one;
        ///  - <see cref="SpellType.LifeProjectile"/> / <see cref="SpellType.Projectile"/> carrying Health -
        ///    MARTYR'S HECATOMB, CURSE OF RAVEN FURY, BLOODSTONE BOLT - benefit;
        ///  - a projectile of any other damage type (a war bolt, Martyr's Blight on Mana) does NOT - it is on
        ///    its own element's axis, not this one;
        ///  - <see cref="SpellType.Transfer"/> - DRAIN - does NOT, whatever its damage type.
        ///
        /// The mechanical reason Drain is the odd one out is spell.TransferCap: the cap binds against
        /// anything worth draining, so a vulnerability there is either invisible (scaling the roll) or turns
        /// a filler spell into a primary nuke that also heals (scaling the cap). Making Drain better is a
        /// design decision the user deliberately deferred, not an engineering gap to close here.
        ///
        /// Only the Drain answer is a live branch (WorldObject_Magic.HandleCastSpell_Transfer selects a
        /// resistance accessor on it). The others are stated rather than gated, because Harm and the
        /// Health-damage projectiles read the axis inside Creature.GetResistanceMod(HealthDrain) with no
        /// branch to take - they are here so the full beneficiary list is one readable, testable thing.
        /// </summary>
        public static bool DamageBenefits(SpellType metaSpellType, DamageType damageType)
        {
            var health = (damageType & DamageType.Health) != 0;

            switch (metaSpellType)
            {
                case SpellType.Transfer:
                    return false;

                case SpellType.Boost:
                case SpellType.FellowBoost:
                    return health || damageType == DamageType.Undef;

                case SpellType.LifeProjectile:
                case SpellType.Projectile:
                    return health;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The one-argument form the Drain gate in WorldObject_Magic.HandleCastSpell_Transfer calls, which
        /// asks about a HEALTH drain specifically (it is reached only under spell.Source == Health). Kept as a
        /// forwarder so that call site, owned elsewhere, needs no edit; it answers exactly what the two-
        /// argument form answers for a Health spell of the given type.
        /// </summary>
        public static bool DamageBenefits(SpellType metaSpellType) => DamageBenefits(metaSpellType, DamageType.Health);

        /// <summary>
        /// Whether a landed spell PROJECTILE may apply the mark (WeakenedBloodAbility.OnSpellHit). Gate, in
        /// full: a Life Magic spell, whose damage type includes Health, of type LifeProjectile (Martyr's
        /// Hecatomb, Curse of Raven Fury) or Projectile (Bloodstone Bolt). A war or void bolt fails the school
        /// test; a Mana-damage life projectile (Martyr's Blight) fails the damage-type test. The non-player-
        /// target half of the gate lives in the handler, which has the target.
        ///
        /// Harm and Drain never come through here - they produce no projectile and apply the mark from their
        /// own landed-spell sites in WorldObject_Magic. DoT ticks never apply it from anywhere.
        /// </summary>
        public static bool AppliesFromSpellProjectile(MagicSchool school, SpellType metaSpellType, DamageType damageType)
        {
            if (school != MagicSchool.LifeMagic)
                return false;

            if ((damageType & DamageType.Health) == 0)
                return false;

            return metaSpellType == SpellType.LifeProjectile || metaSpellType == SpellType.Projectile;
        }

        /// <summary>
        /// The chance, per target per cast, that a landed damaging Life Magic spell applies the mark at a given
        /// rank: 0.25 / 0.35 / 0.50 by default (class_ability_weakenedblood_proc_chance_r1..r3). Rank 0 or
        /// below is 0 (never); a rank above 3 clamps to rank 3. The result is clamped into [0, 1] so a
        /// misconfigured tunable cannot produce a chance the roll cannot mean.
        /// </summary>
        public static double ProcChance(int rank, double chanceR1, double chanceR2, double chanceR3)
        {
            if (rank <= 0)
                return 0.0;

            var chance = rank switch
            {
                1 => chanceR1,
                2 => chanceR2,
                _ => chanceR3,
            };

            return Math.Clamp(chance, 0.0, 1.0);
        }

        /// <summary>
        /// The proc test: STRICTLY less than. A roll drawn from ThreadSafeRandom.Next(0f, 1f) that equals the
        /// chance does not proc, so a chance of 0 can never fire and a chance of 1 always does.
        /// </summary>
        public static bool Procs(double roll, double chance) => roll < chance;

        /// <summary>
        /// The rank's raw attack/cast SLOW fraction before affinity: 0.20 / 0.35 / 0.50 by default
        /// (class_ability_weakenedblood_slow_r1..r3). Rank 0 or below is 0; a rank above 3 clamps to rank 3;
        /// a negative configured value is clamped to 0 (a slow can never become a haste).
        /// </summary>
        public static double RankSlow(int rank, double slowR1, double slowR2, double slowR3)
        {
            if (rank <= 0)
                return 0.0;

            var slow = rank switch
            {
                1 => slowR1,
                2 => slowR2,
                _ => slowR3,
            };

            return Math.Max(0.0, slow);
        }

        /// <summary>
        /// Hard lower bound on the speed factor, whatever the configured floor says. The factor divides the
        /// monster's cadence delays and cast times, so it must never reach zero; 0.10 (ten times slower) is
        /// far below any sensible floor and exists only so a misconfigured class_ability_weakenedblood_slow_floor
        /// of 0 or less cannot produce a division by zero.
        /// </summary>
        public const double MinSpeedFactor = 0.10;

        /// <summary>
        /// The speed factor a slow leaves a monster at: max(1 - slow, floor), where floor is
        /// class_ability_weakenedblood_slow_floor (0.50 by default) clamped into [<see cref="MinSpeedFactor"/>, 1].
        /// 1.0 means no slow. The factor multiplies animation speed and divides durations.
        ///
        /// THE FLOOR IS RELATIVE TO THE MONSTER'S OWN SPEED, not an absolute anim speed: it is applied after
        /// the monster-effect speed multiplier and its cap, so an authored-slow monster is still slowed by up
        /// to the same fraction instead of being exempt because it already sits low.
        /// </summary>
        public static double SpeedFactor(double slow, double floor)
        {
            var clampedFloor = Math.Clamp(floor, MinSpeedFactor, 1.0);

            return Math.Max(1.0 - Math.Max(0.0, slow), clampedFloor);
        }

        /// <summary>
        /// Merges an incoming speed factor with the one the target already carries. The slow SHARES THE
        /// MARK'S EXPIRY, so <paramref name="existingExpire"/> is the mark's expiry before this application.
        /// While that is still running the STRONGER slow wins (the smaller factor, i.e. the max slow), so a
        /// low-rank blood mage cannot weaken a high-rank slow; once it has lapsed the old factor is ignored.
        /// </summary>
        public static double RefreshSpeedFactor(double now, double existingFactor, double existingExpire, double incomingFactor)
        {
            if (!IsActive(now, existingExpire))
                return incomingFactor;

            return Math.Min(existingFactor, incomingFactor);
        }

        /// <summary>
        /// Whether the caster should see a "takes hold" line for this application: only when the mark freshly
        /// took hold on the target AND nothing has been announced for this cast yet. Keyed on the caster's
        /// cast number (MagicState.CastNum), so a Curse of Raven Fury ring, a Spell AOE splash, an Echo Cast
        /// recast or a Crimson Harvest fan that marks five creatures in one cast produces one line, not five.
        /// </summary>
        public static bool ShouldAnnounce(bool tookHold, int castKey, int lastAnnouncedCastKey) =>
            tookHold && castKey != lastAnnouncedCastKey;

        /// <summary>
        /// The single life-vulnerability axis, as arithmetic: the MAXIMUM of the weapon rend/cleave term and
        /// any cast mark, floored at 1.0 so a rend below 1.0 (a resistance, not a vulnerability) is clamped
        /// out. Creature.GetLifeVulnerabilityMod is a one-line call to this.
        ///
        /// MAX, NEVER A PRODUCT. That is the whole guarantee the axis exists to provide - a blood mage's
        /// mark and a rending wand do not multiply, exactly as a war caster's rending wand and a cast Fire
        /// Vulnerability do not. A new contributor belongs in this call, not as a separate multiplier at a
        /// damage site.
        /// </summary>
        public static float VulnerabilityMod(double weaponResistanceMod, double castVulnerability)
        {
            return (float)Math.Max(1.0, Math.Max(weaponResistanceMod, castVulnerability));
        }
    }

    /// <summary>
    /// Enforces ONE Weakened Blood roll per TARGET per CAST (owner intent, code review 2026-10-17). Without
    /// it the spell-hit dispatch rolls once per projectile COLLISION, so three Curse of Raven Fury
    /// projectiles striking one large monster would roll three times - 1 - (1 - 0.5)^3 = 87.5% at rank 3
    /// instead of 50%.
    ///
    /// Keyed on the caster's cast number (MagicState.CastNum): the set of targets already rolled is cleared
    /// whenever the cast number changes, so every projectile, Spell AOE child, Cascade child, Echo Cast
    /// recast and Crimson Harvest secondary of one cast shares one set. A projectile still in flight when
    /// the next cast starts counts against that next cast; that can never grant more than one roll per
    /// target per cast number.
    ///
    /// One instance per Player, mutated on that player's landblock thread like the rest of its class-ability
    /// state. A plain class rather than a Player method so the rule is unit-testable without a live Player.
    /// </summary>
    public sealed class WeakenedBloodCastRollLedger
    {
        private int castNum = int.MinValue;

        private readonly HashSet<uint> rolledTargets = new HashSet<uint>();

        /// <summary>
        /// TRUE when <paramref name="targetGuid"/> has not yet rolled during cast <paramref name="castNumber"/>,
        /// and records that it now has. FALSE for a repeat - the caller then draws no roll at all.
        /// </summary>
        public bool TryClaimRoll(int castNumber, uint targetGuid)
        {
            if (castNumber != castNum)
            {
                castNum = castNumber;
                rolledTargets.Clear();
            }

            return rolledTargets.Add(targetGuid);
        }
    }
}
