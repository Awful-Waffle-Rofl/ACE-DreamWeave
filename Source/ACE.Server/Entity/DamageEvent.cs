using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.DatLoader.Entity.AnimationHooks;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.Entity
{
    public class DamageEvent
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // factors:
        // - lifestone protection
        // - evade
        //   - offense mod (heart seeker)
        //      - accuracy mod (missile)
        //   - defense mod (defender)
        //      - stamina mod
        // - base damage / mod
        // - damage rating / mod
        //   - recklessness
        //   - sneak attack
        //   - heritage bonus
        // - damage resistance rating /mod
        // - power meter mod
        // - critical (chance % mod / critical damage mod)
        // - attribute mod
        // - armor / mod (base al, impen / bane, life armor / imperil)
        // - elemental damage bonus
        // - slayer mod
        // - resistance mod (natural, prot, vuln)
        //   - resistance cleaving
        // - shield mod
        // - rending mod

        public Creature Attacker;
        public Creature Defender;

        public CombatType CombatType;   // melee / missile / magic

        public WorldObject DamageSource;
        public DamageType DamageType;

        /// <summary>
        /// Volatile Elements (Spellsword T2): the hit's own type from GetBaseDamage when the ability re-typed
        /// this hit, null otherwise. Non-null exactly when the roll fired, including when it landed back on the
        /// same type. <see cref="DamageType"/> then holds the ROLLED type, which everything type-keyed follows;
        /// this field exists only so rend can look back - the hit's WeaponResistanceMod and the same hit's war
        /// procs (SpellProjectile.RendSourceDamageType) take the better of the original and rolled type's rend.
        /// </summary>
        public DamageType? OriginalDamageType;

        public WorldObject Weapon;      // the attacker's weapon. this can be different from DamageSource,
                                        // ie. for a missile attack, the missile would the DamageSource,
                                        // and the buffs would come from the Weapon

        public AttackType AttackType;   // slash / thrust / punch / kick / offhand / multistrike
        public AttackHeight AttackHeight;

        public bool LifestoneProtection;

        public float EvasionChance;
        public uint EffectiveAttackSkill;
        public uint EffectiveDefenseSkill;
        public float AccuracyMod;

        public bool Evaded;

        // class-ability avoidance outcomes (resolved before the evade roll). Blocked = Shield Block,
        // Parried = Parry. Both zero the damage like an evade, but unlike an evade they proc their
        // synergy (Thorns / Shield Check / Riposte) - handled at the roll site.
        public bool Blocked;
        public bool Parried;

        public BaseDamageMod BaseDamageMod;
        public float BaseDamage { get; set; }

        public float AttributeMod;
        public float PowerMod;

        /// <summary>
        /// TRUE only when this hit came from a real power-bar melee swing (Player.DamageTarget's default). A
        /// Riposte counter reaches DamageTarget without a swing and carries whatever PowerLevel the bar last
        /// held, so it is marked false; the Spellsword full-power upgrade and power multiplier read this
        /// (SpellswordSpellTables.SwingPowerLevel) and fire off nothing else.
        /// </summary>
        public bool PowerBarSwing;
        public float SlayerMod;

        public float DamageRatingBaseMod;
        public float RecklessnessMod;
        public float SneakAttackMod;
        public float HeritageMod;
        public float PkDamageMod;

        public float DamageRatingMod;

        public bool IsCritical;

        public float CriticalChance;
        public float CriticalDamageMod;

        public float CriticalDamageRatingMod;
        public float CriticalDamageResistanceRatingMod;

        public float DamageBeforeMitigation;

        public float ArmorMod;
        public float ResistanceMod;
        public float ShieldMod;
        public float WeaponResistanceMod;

        public float DamageResistanceRatingBaseMod;
        public float DamageResistanceRatingMod;
        public float PkDamageResistanceMod;

        public float DamageMitigated;

        // creature attacker
        public MotionCommand? AttackMotion;
        public AttackHook AttackHook;
        public KeyValuePair<CombatBodyPart, PropertiesBodyPart> AttackPart;      // the body part this monster is attacking with

        // creature defender
        public Quadrant Quadrant;

        // FORK CHANGE - hollow intensity: the resolved fraction 0-1 (HollowMath.Resolve), and the bools as "any hollow"
        public double IgnoreMagicArmorIntensity =>  HollowMath.Resolve(Weapon, Attacker, PropertyBool.IgnoreMagicArmor);
        public double IgnoreMagicResistIntensity => HollowMath.Resolve(Weapon, Attacker, PropertyBool.IgnoreMagicResist);

        public bool IgnoreMagicArmor =>  IgnoreMagicArmorIntensity > 0.0;      // ignores impen / banes

        public bool IgnoreMagicResist => IgnoreMagicResistIntensity > 0.0;    // ignores life armor / prots

        public bool Overpower;

        /// <summary>
        /// World Events Hold: a run monster hitting a knight of its own active run (WorldEventHooks.IsHoldKnightTargetFor).
        /// Such a hit always lands and skips the body part, armor, shield and resistance steps: the knight's TakeDamage
        /// replaces the amount with a time-based slice of its health anyway, so only "did it land" matters. Without this a
        /// knight copied from an NPC with no body parts (every hit read as evaded) or high defenses could not be hurt.
        /// </summary>
        public bool HoldKnightHit;


        // player defender
        public BodyPart BodyPart;
        public List<WorldObject> Armor;

        // creature defender
        public KeyValuePair<CombatBodyPart, PropertiesBodyPart> PropertiesBodyPart;
        public Creature_BodyPart CreaturePart;

        public float Damage;

        public bool GeneralFailure;

        public bool HasDamage => !Evaded && !Blocked && !Parried && !LifestoneProtection;

        public bool CriticalDefended;

        /// <summary>
        /// The multiplier a critical hit applies to this event's own damage, clamped to at least 1.0 - 1.0
        /// (no change) on a non-critical hit. CriticalDamageMod is a plain field with no initializer, so it
        /// is only meaningful when IsCritical is true; reading it unconditionally on a non-crit event would
        /// read a stale/default 0.0f, which is why this property gates on IsCritical first. Consumed by
        /// class abilities whose own proc damage should scale with the strike's own critical (Poison Weapon,
        /// Acid Proc) through <see cref="ProcDamageMultiplier"/>, which multiplies it by the strike's rating
        /// terms - read once DoCalculateDamage has finished, so it reflects the finished crit roll for
        /// melee, missile, multi-shot and Riposte hits alike (all of them resolve through DamageTarget after
        /// DamageEvent.CalculateDamage returns).
        /// </summary>
        public double CriticalDamageMultiplier => IsCritical ? Math.Max(1.0, CriticalDamageMod) : 1.0;

        /// <summary>
        /// The multiplier a flat on-hit PROC (Poison Weapon, the Venom gear mod, Acid Proc's wound) takes
        /// from the strike that triggered it, so the proc MIRRORS the strike's own rating math (owner
        /// ruling 2026-09-21). Two further factors were added by the 2026-09-22 ruling and are documented on
        /// their own fields: <see cref="ClassAbilityCritDamageBonus"/> (Killer Instinct, crits only) and
        /// <see cref="StrikeFractionProcMultiplier"/> (a partial-damage strike's fraction). The three rating factors below
        /// are values DoCalculateDamage already computed for THIS strike - nothing is recomputed and crit is
        /// never rolled a second time:
        ///
        ///  - <see cref="CriticalDamageMultiplier"/>: the weapon crit multiplier (CriticalMultiplier /
        ///    Crippling Blow, times the Execution weapon mod) on a crit, 1.0 otherwise. Unchanged meaning.
        ///  - <see cref="DamageRatingMod"/>: the attacker's damage rating exactly as the strike combined it.
        ///    On a crit that already includes CritDamageRating ADDED into the same pool (never multiplied on
        ///    top) and already drops Recklessness, because that is how the crit branch rebuilds it. Sneak
        ///    Attack, Heritage and (PvP only, unreachable here) PK damage rating ride inside it too.
        ///  - <see cref="DamageResistanceRatingMod"/>: the target's damage resistance rating, which on a crit
        ///    already has the target's crit damage resistance rating combined in - and ONLY on a crit, since
        ///    DoCalculateDamage adds that term under its own IsCritical test. Reading the finished value is
        ///    what keeps the crit-only rule in one place.
        ///
        /// Deliberately EXCLUDED: every term that scales the strike's BASE damage - attribute, power, slayer,
        /// armor, shield, elemental resistance, and the max-damage-on-crit substitution. The proc is flat,
        /// unresisted Base-type damage and stays that way.
        ///
        /// A rating mod of 0 means the field was never computed (DoCalculateDamage returned before the
        /// ratings block - an Invincible defender, a general failure) or the event was built by hand; a
        /// computed rating mod is always strictly positive (Creature.GetPositiveRatingMod /
        /// GetNegativeRatingMod never return 0). Such a factor reads as the neutral 1.0, which reproduces the
        /// proc exactly as it behaved before this property existed rather than zeroing it.
        ///
        /// The result may legitimately be below 1.0 - a resistant target, or a weakened attacker whose
        /// damage rating is negative - and that reduction is intended: it is the same reduction the strike
        /// itself took.
        /// </summary>
        public double ProcDamageMultiplier =>
            CriticalDamageMultiplier
            * (DamageRatingMod > 0.0f ? DamageRatingMod : 1.0)
            * (DamageResistanceRatingMod > 0.0f ? DamageResistanceRatingMod : 1.0)
            * (IsCritical ? 1.0 + Math.Max(0.0, ClassAbilityCritDamageBonus) : 1.0)
            * Math.Max(0.0, StrikeFractionProcMultiplier);

        /// <summary>
        /// The damage fraction of a partial-damage strike, as the procs see it: a Multishot / multi-shot-weapon
        /// EXTRA arrow (owner ruling 2026-09-22) and a Riposte counter (owner ruling 2026-10-15) scale their
        /// procs by the same fraction the strike's own damage does. 1.0 for every other hit - the primary arrow,
        /// a Double Volley re-fire at the primary target, ordinary melee and cleave hits - because
        /// Player.DamageTarget copies its damageMultiplier here and only Multishot's extra arrows and Riposte
        /// counters pass anything but the default 1.0.
        ///
        /// READ ONLY BY <see cref="ProcDamageMultiplier"/>, and that is what keeps it from applying twice: the
        /// strike's own copy of the fraction is the separate `Damage *= damageMultiplier` in DamageTarget,
        /// which no proc reads (procs read ProcDamageMultiplier and never Damage), and each arrow builds its
        /// own DamageEvent, so the field is written once per event.
        /// </summary>
        public float StrikeFractionProcMultiplier = 1.0f;

        /// <summary>
        /// A class ability's crit-damage bonus on THIS strike, as the fraction it multiplied Damage by - today
        /// only Killer Instinct (owner ruling 2026-09-22: its Openings bonus reaches the procs). Written by the
        /// ability at the moment it applies the bonus to Damage, so the strike gets it exactly once and the
        /// procs read the identical number rather than re-deriving it from stacks that the same handler then
        /// changes (it grants an Opening right after).
        ///
        /// Read by <see cref="ProcDamageMultiplier"/> on a crit only. Poison Weapon and Acid Proc see it because
        /// Killer Instinct dispatches in the earlier OutgoingDamageDispatchOrder.CritDamage band; the Venom gear
        /// proc sees it because ApplyEquipmentModOutgoingDamage runs after the whole class-ability loop.
        /// </summary>
        public double ClassAbilityCritDamageBonus;

        /// <summary>
        /// PvP rules debug record (Docs/Pvp/DESIGN.md "PvP rules (levers)"): the hit's Damage before the per-hit
        /// PvP damage cap bit it, and the choke point that capped it. 0 / null when the cap did not apply. Written
        /// only by <see cref="OnPvpDamageCapped"/>; read only by the debug-damage output.
        /// </summary>
        public float PvpDamageBeforeCap;
        public PvpChokePoint? PvpDamageCapPoint;

        /// <summary>
        /// PvP rules debug record: the rating scales a PvP rating lever applied to this hit (R1).
        /// PvpRatingScalesApplied is TRUE only when the lever ran (a PvP hit with pvp_rules_enabled on); the scales
        /// are printed exactly then, so a configured scale of 0 is still visible. False for PvE or the switch off.
        /// </summary>
        public bool PvpRatingScalesApplied;
        public float PvpDamageRatingScale;
        public float PvpCritDamageRatingScale;

        public static HashSet<uint> AllowDamageTypeUndef = new HashSet<uint>()
        {
            22545,  // Obsidian Spines
            35191,  // Thunder Chicken
            38406,  // Blessed Moar
            38587,  // Ardent Moar
            38588,  // Blessed Moar
            38586,  // Verdant Moar
            40298,  // Ardent Moar
            40300,  // Blessed Moar
            40301,  // Verdant Moar
        };

        /// <summary>
        /// <paramref name="deferOutgoingHitEffects"/>: when TRUE the monster-effect riders (and the debug
        /// logging that reports them) are NOT run here, and the caller must either call
        /// <see cref="ApplyDeferredOutgoingHitEffects"/> or deliberately drop them. Only the monster-missile
        /// site in ProjectileCollisionHelper passes it, so Reflect (Vanguard T3) can decide whether the shot
        /// bounces BEFORE its DoT / debuff / leech riders land on the player it was aimed at. Every other
        /// caller leaves it false and is unchanged.
        /// </summary>
        public static DamageEvent CalculateDamage(Creature attacker, Creature defender, WorldObject damageSource, MotionCommand? attackMotion = null, AttackHook attackHook = null, bool deferOutgoingHitEffects = false)
        {
            var damageEvent = new DamageEvent();
            damageEvent.AttackMotion = attackMotion;
            damageEvent.AttackHook = attackHook;
            if (damageSource == null)
                damageSource = attacker;

            var damage = damageEvent.DoCalculateDamage(attacker, defender, damageSource);

            if (!deferOutgoingHitEffects)
                FinishOutgoingHit(attacker, defender, damageEvent);

            return damageEvent;
        }

        /// <summary>
        /// Runs the riders and logging a <c>deferOutgoingHitEffects: true</c> CalculateDamage held back, for a
        /// hit that is going to land after all (Reflect did not bounce it).
        /// </summary>
        public void ApplyDeferredOutgoingHitEffects()
        {
            FinishOutgoingHit(Attacker, Defender, this);
        }

        private static void FinishOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent)
        {
            // monster combat effects: a non-player attacker's landed hit, after the calculation and before
            // the event is handed back, so the damage applied and the damage reported both include whatever
            // a rider changed. ONE site covers monster melee (Monster_Melee) and monster missile
            // (ProjectileCollisionHelper) - both resolve through this method, Creature-typed on both sides.
            if (attacker is not Player && damageEvent.HasDamage)
                attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            damageEvent.HandleLogging(attacker, defender);
        }

        /// <summary>
        /// Reflect (Vanguard T3): the damage a bounced missile deals to the creature that FIRED it. The attack
        /// half of <paramref name="incoming"/> (the firer's own rolled base damage, attribute / power / slayer
        /// mods, damage rating and critical) is kept exactly as it was rolled against the player; only the
        /// DEFENDER half is resolved again, against the firer: armor rending, body part, armor, resistance,
        /// damage resist rating, crit damage resist rating and shield, through the same
        /// <see cref="ResolveDefenderHalf"/> the normal path uses.
        ///
        /// DELIBERATELY NOT CalculateDamage(firer, firer): that would re-roll evade and the monster's avoidance
        /// effects against its own attack, fire their reactors (riposte with attacker == defender), and run the
        /// outgoing-hit riders a second time. A bounce runs none of those. The firer's Invincible flag is still
        /// honored.
        ///
        /// <paramref name="reflector"/> is the POSITIONAL attacker only - the hit quadrant and the shield's
        /// facing are measured from where the bounce comes from. Every attacker STAT the defender half reads
        /// (armor cleaving, hollow, weapon resistance cleaving) stays the firer's, because it is the firer's
        /// own shot.
        /// </summary>
        public static DamageEvent CalculateReflectedMissile(DamageEvent incoming, Creature reflector)
        {
            var reflected = new DamageEvent();

            var firer = incoming?.Attacker;

            if (firer == null)
                return reflected;

            reflected.Attacker = firer;
            reflected.Defender = firer;
            reflected.CombatType = incoming.CombatType;
            reflected.DamageSource = incoming.DamageSource ?? firer;
            reflected.DamageType = incoming.DamageType;
            reflected.OriginalDamageType = incoming.OriginalDamageType;
            reflected.Weapon = incoming.Weapon;
            reflected.AttackType = incoming.AttackType;
            reflected.AttackHeight = incoming.AttackHeight;
            reflected.BaseDamageMod = incoming.BaseDamageMod;
            reflected.BaseDamage = incoming.BaseDamage;
            reflected.AttributeMod = incoming.AttributeMod;
            reflected.PowerMod = incoming.PowerMod;
            reflected.PowerBarSwing = incoming.PowerBarSwing;
            reflected.SlayerMod = incoming.SlayerMod;
            reflected.DamageRatingBaseMod = incoming.DamageRatingBaseMod;
            reflected.DamageRatingMod = incoming.DamageRatingMod;
            reflected.IsCritical = incoming.IsCritical;
            reflected.CriticalDamageMod = incoming.CriticalDamageMod;
            reflected.CriticalDamageRatingMod = incoming.CriticalDamageRatingMod;
            reflected.DamageBeforeMitigation = incoming.DamageBeforeMitigation;

            if (firer.Invincible || reflected.DamageBeforeMitigation <= 0.0f)
                return reflected;

            // PvE only (Player.TryReflectProjectile refuses a Player firer), so this resolves to the neutral
            // identity; it is resolved anyway so the defender half reads every rating through a scaler,
            // exactly as the normal path does
            var reflectScales = PvpRatingScales.Resolve(PvpChokePoint.R1, firer, firer);

            var attackSkill = firer.GetCreatureSkill(firer.GetCurrentWeaponSkill());

            var positional = reflector ?? firer;

            reflected.ResolveDefenderHalf(firer, firer, firer as Player, attackSkill, reflectScales, false, positional, positional);

            return reflected;
        }

        private float DoCalculateDamage(Creature attacker, Creature defender, WorldObject damageSource)
        {
            var playerAttacker = attacker as Player;
            var playerDefender = defender as Player;

            var pkBattle = PvpClassifier.IsPlayerPairIncludingSelf(attacker, defender);

            Attacker = attacker;
            Defender = defender;

            CombatType = damageSource.ProjectileSource == null ? CombatType.Melee : CombatType.Missile;

            DamageSource = damageSource;

            Weapon = damageSource.ProjectileSource == null ? attacker.GetEquippedMeleeWeapon() : (damageSource.ProjectileLauncher ?? damageSource.ProjectileAmmo);

            AttackType = attacker.AttackType;
            AttackHeight = attacker.AttackHeight ?? AttackHeight.Medium;

            // check lifestone protection
            if (playerDefender != null && playerDefender.UnderLifestoneProtection)
            {
                LifestoneProtection = true;
                playerDefender.HandleLifestoneProtection();
                return 0.0f;
            }

            if (defender.Invincible)
                return 0.0f;

            // overpower
            if (attacker.Overpower != null)
                Overpower = Creature.GetOverpower(attacker, defender);

            // World Events Hold: a knight never avoids its own run's monsters (one field read for every other defender).
            HoldKnightHit = ACE.Server.WorldEvents.WorldEventHooks.IsHoldKnightTargetFor(attacker, defender);

            // class-ability avoidance (Shield Block / Parry) - resolved BEFORE the evade roll so a blocked
            // or parried hit can proc its synergy (Thorns / Shield Check / Riposte), whereas an evaded hit
            // procs nothing. PvE only (playerAttacker == null), matching every other class ability's PvP
            // exclusion; no-op unless the defender has learned one of those skills.
            if (!Overpower && playerDefender != null && playerAttacker == null)
            {
                var avoidance = playerDefender.RollClassAbilityAvoidance(attacker, CombatType);
                if (avoidance != ClassAbilityAvoidanceOutcome.None)
                {
                    Blocked = avoidance == ClassAbilityAvoidanceOutcome.Block;
                    Parried = avoidance == ClassAbilityAvoidanceOutcome.Parry;
                    playerDefender.OnClassAbilityAttackAvoided(attacker, avoidance, CombatType);
                    return 0.0f;
                }
            }

            // monster combat effects: the mirror of the block above for a NON-player defender, rolled from
            // the same slot - before the evade roll - so an authored avoid and the monster's own evade are
            // one decision rather than two chances to miss. Pooled across the monster's avoidance effects
            // and clamped by monster_effect_avoidance_cap inside; no-op unless its weenie authored one.
            // Reported as an evade because that is what it is from the attacker's side: the attack simply
            // did not land, and no block/parry synergy exists on the monster side to distinguish.
            // (A World Events Hold knight never avoids its own run's monsters: HoldKnightHit skips the roll.)
            if (!HoldKnightHit)
            {
                if (!Overpower && playerDefender == null && defender.RollMonsterEffectAvoidance(attacker, CombatType))
                {
                    Evaded = true;
                    return 0.0f;
                }
            }

            // evasion chance (a World Events Hold knight never dodges its own run's monsters)
            if (!Overpower && !HoldKnightHit)
            {
                EvasionChance = GetEvadeChance(attacker, defender);
                if (EvasionChance > ThreadSafeRandom.Next(0.0f, 1.0f))
                {
                    Evaded = true;
                    return 0.0f;
                }
            }

            // get base damage
            if (playerAttacker != null)
                GetBaseDamage(playerAttacker);
            else
                GetBaseDamage(attacker, AttackMotion ?? MotionCommand.Invalid, AttackHook);

            if (DamageType == DamageType.Undef)
            {
                if ((attacker?.Guid.IsPlayer() ?? false) || (damageSource?.Guid.IsPlayer() ?? false))
                {
                    log.Error($"DamageEvent.DoCalculateDamage({attacker?.Name} ({attacker?.Guid}), {defender?.Name} ({defender?.Guid}), {damageSource?.Name} ({damageSource?.Guid})) - DamageType == DamageType.Undef");
                    GeneralFailure = true;
                }
                else
                {
                    // Defensive tripwire: GetDamageType no longer returns Undef for a monster, so this should be
                    // unreachable. Monsters keep their behavior (the hit still lands); this is visibility only.
                    log.Warn($"DamageEvent.DoCalculateDamage({attacker?.Name} ({attacker?.Guid}, wcid {attacker?.WeenieClassId}), {defender?.Name} ({defender?.Guid}), {damageSource?.Name} ({damageSource?.Guid}), {CombatType}) - DamageType == DamageType.Undef");
                }
            }

            if (GeneralFailure) return 0.0f;

            // Volatile Elements (Spellsword T2, PvE only): re-type this weapon hit, one roll per DamageEvent.
            // AFTER GetBaseDamage, so the missile launcher's flat elemental bonus above was computed from the
            // weapon's own type and is kept; BEFORE ResolveDefenderHalf, so armor, rend, resistance, shields and
            // the procs that read DamageType after CalculateDamage all see the rolled type. The original is kept
            // in OriginalDamageType for the rend max only. Null (no roll, no change) unless the attacker owns the
            // ability and the defender is a non-player creature.
            if (playerAttacker != null && playerDefender == null)
            {
                var volatileType = playerAttacker.RollVolatileElement(defender, DamageType);

                if (volatileType != null)
                {
                    OriginalDamageType = DamageType;
                    DamageType = volatileType.Value;
                }
            }

            // get damage modifiers
            PowerMod = attacker.GetPowerMod(Weapon);
            AttributeMod = attacker.GetAttributeMod(Weapon);
            SlayerMod = WorldObject.GetWeaponCreatureSlayerModifier(Weapon, attacker, defender);

            // PvP rules choke point R1 (Docs/Pvp/DESIGN.md "PvP rules (levers)"): the rating-scale lever, resolved
            // once per hit. On a PvP hit it scales the summed damage / damage resist ratings by
            // pvp_damage_rating_scale and the crit damage / crit damage resist ratings by
            // pvp_crit_damage_rating_scale, signed, BEFORE each is turned into a mod. The PK ratings are not
            // scaled. Neutral (the identity, no setting read) for everything that is not two distinct players.
            var ratingScales = PvpRatingScales.Resolve(PvpChokePoint.R1, attacker, defender);

            if (ratingScales.Applied)
            {
                PvpRatingScalesApplied = true;
                PvpDamageRatingScale = (float)ratingScales.DamageRatingScale;
                PvpCritDamageRatingScale = (float)ratingScales.CritDamageRatingScale;
            }

            // ratings
            DamageRatingBaseMod = Creature.GetPositiveRatingMod(ratingScales.ScaleDamageRating(attacker.GetDamageRating()));
            RecklessnessMod = Creature.GetRecklessnessMod(attacker, defender);
            SneakAttackMod = attacker.GetSneakAttackMod(defender);
            HeritageMod = attacker.GetHeritageBonus(Weapon) ? 1.05f : 1.0f;

            DamageRatingMod = Creature.AdditiveCombine(DamageRatingBaseMod, RecklessnessMod, SneakAttackMod, HeritageMod);

            if (pkBattle)
            {
                PkDamageMod = Creature.GetPositiveRatingMod(attacker.GetPKDamageRating());
                DamageRatingMod = Creature.AdditiveCombine(DamageRatingMod, PkDamageMod);
            }

            // damage before mitigation
            DamageBeforeMitigation = BaseDamage * AttributeMod * PowerMod * SlayerMod * DamageRatingMod;

            // critical hit?
            var attackSkill = attacker.GetCreatureSkill(attacker.GetCurrentWeaponSkill());
            CriticalChance = WorldObject.GetWeaponCriticalChance(Weapon, attacker, attackSkill, defender);

            // PvP rules choke point CC1 (Docs/Pvp/DESIGN.md "PvP rules (levers)", context tuning): the arena / battleground
            // per-category crit chance multiplier, on the FINAL computed chance and before the logout always-crit
            // override below, so a logging-out target is still hit critically at 100%. A no-op unless this is a PvP
            // pair in a Live arena or battleground match.
            CriticalChance = PvpContextTuning.ApplyCritChance(PvpChokePoint.CC1, attacker, defender, PvpHitProfile.ForWeapon(Weapon, CombatType), CriticalChance);

            // https://asheron.fandom.com/wiki/Announcements_-_2002/08_-_Atonement
            // It should be noted that any time a character is logging off, PK or not, all physical attacks against them become automatically critical.
            // (Note that spells do not share this behavior.) We hope this will stress the need to log off in a safe place.

            if (playerDefender != null && (playerDefender.IsLoggingOut || playerDefender.PKLogout))
                CriticalChance = 1.0f;

            if (CriticalChance > ThreadSafeRandom.Next(0.0f, 1.0f))
            {
                if (playerDefender != null && playerDefender.AugmentationCriticalDefense > 0)
                {
                    var criticalDefenseMod = playerAttacker != null ? 0.05f : 0.25f;
                    var criticalDefenseChance = playerDefender.AugmentationCriticalDefense * criticalDefenseMod;

                    if (criticalDefenseChance > ThreadSafeRandom.Next(0.0f, 1.0f))
                        CriticalDefended = true;
                }

                if (!CriticalDefended)
                {
                    IsCritical = true;

                    // verify: CriticalMultiplier only applied to the additional crit damage,
                    // whereas CD/CDR applied to the total damage (base damage + additional crit damage)
                    CriticalDamageMod = 1.0f + WorldObject.GetWeaponCritDamageMod(Weapon, attacker, attackSkill, defender);

                    // Weapon mods v3 Tier B (2026-08-06): Execution MULTIPLIES CriticalDamageMod by
                    // (1 + magnitude), read directly off the weapon - see WeaponModRegistry.cs's Tier B v3
                    // remarks for why this is a multiplier rather than a write into GetWeaponCritDamageMod.
                    CriticalDamageMod *= 1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(Weapon, WeaponModId.Execution, attacker);

                    CriticalDamageRatingMod = Creature.GetPositiveRatingMod(ratingScales.ScaleCritDamageRating(attacker.GetCritDamageRating()));

                    // recklessness excluded from crits
                    RecklessnessMod = 1.0f;
                    DamageRatingMod = Creature.AdditiveCombine(DamageRatingBaseMod, CriticalDamageRatingMod, SneakAttackMod, HeritageMod);

                    if (pkBattle)
                        DamageRatingMod = Creature.AdditiveCombine(DamageRatingMod, PkDamageMod);

                    DamageBeforeMitigation = BaseDamageMod.MaxDamage * AttributeMod * PowerMod * SlayerMod * DamageRatingMod * CriticalDamageMod;
                }
            }

            return ResolveDefenderHalf(attacker, defender, playerDefender, attackSkill, ratingScales, pkBattle, Attacker, DamageSource);
        }

        /// <summary>
        /// The DEFENDER half of a physical hit: everything from armor rending to the final Damage, run against
        /// <paramref name="defender"/> once the attack half has set DamageBeforeMitigation. Extracted verbatim
        /// and in order from DoCalculateDamage so the Reflect bounce (<see cref="CalculateReflectedMissile"/>)
        /// resolves against the firer through the identical code.
        ///
        /// <paramref name="positionalAttacker"/> / <paramref name="positionalSource"/> are what the hit
        /// quadrant and the shield's facing are measured from. The normal path passes Attacker and
        /// DamageSource, which is exactly what this block read before it was extracted.
        /// </summary>
        private float ResolveDefenderHalf(Creature attacker, Creature defender, Player playerDefender, ACE.Server.WorldObjects.Entity.CreatureSkill attackSkill, PvpRatingScales ratingScales, bool pkBattle, Creature positionalAttacker, WorldObject positionalSource)
        {
            // World Events Hold: no body part, armor, shield or resistance for a knight hit by its own run's monster; at
            // least 1, so TakeDamage always sees a hit to turn into its time-based slice. False for every other hit
            // (HoldKnightHit is only ever set in DoCalculateDamage, never on a Reflect bounce).
            if (HoldKnightHit)
            {
                Damage = Math.Max(1.0f, DamageBeforeMitigation);
                DamageMitigated = 0.0f;
                return Damage;
            }

            // armor rending and cleaving
            var armorRendingMod = 1.0f;
            if (Weapon != null && Weapon.HasImbuedEffect(ImbuedEffectType.ArmorRending))
                armorRendingMod = WorldObject.GetArmorRendingMod(attackSkill);

            var armorCleavingMod = attacker.GetArmorCleavingMod(Weapon);

            var ignoreArmorMod = Math.Min(armorRendingMod, armorCleavingMod);

            // get body part / armor pieces / armor modifier
            if (playerDefender != null)
            {
                // select random body part @ current attack height
                GetBodyPart(AttackHeight);

                // get player armor pieces
                Armor = attacker.GetArmorLayers(playerDefender, BodyPart);

                // get armor modifiers
                ArmorMod = attacker.GetArmorMod(playerDefender, DamageType, Armor, Weapon, ignoreArmorMod);
            }
            else
            {
                // determine height quadrant
                Quadrant = GetQuadrant(Defender, positionalAttacker, AttackHeight, positionalSource);

                // select random body part @ current attack height
                GetBodyPart(Defender, Quadrant);
                if (Evaded)
                    return 0.0f;

                Armor = CreaturePart.GetArmorLayers(PropertiesBodyPart.Key);

                // get target armor
                ArmorMod = CreaturePart.GetArmorMod(DamageType, Armor, Attacker, Weapon, ignoreArmorMod);
            }

            if (Weapon != null && Weapon.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor))
                ArmorMod = 1.0f;

            // get resistance modifiers
            // Volatile Elements: when the hit was re-typed, the weapon keeps the better of its original type's rend
            // and the rolled type's (a fire-rend weapon rolling cold keeps its fire rend). The plain lookup otherwise.
            WeaponResistanceMod = VolatileElementsMath.ResolveWeaponResistanceMod(Weapon, attacker, attackSkill, OriginalDamageType, DamageType);

            if (playerDefender != null)
            {
                ResistanceMod = playerDefender.GetResistanceMod(DamageType, Attacker, Weapon, WeaponResistanceMod);
            }
            else
            {
                var resistanceType = Creature.GetResistanceType(DamageType);

                // Elemental Inversion (Archmage T1, PvE only - this branch is a non-player defender): a proc
                // replaces ONLY the defender's innate resist term for this melee/missile hit with its weakest innate
                // value among the seven types. The lookup stays on the hit's own type, so armor (above), shields
                // (below), rend/cleave and the type's vulnerabilities are untouched. magicDamage FALSE: Arcane
                // Hollow is magic damage only and never applies to a weapon hit.
                var inversionInnate = attacker is Player inversionAttacker
                    ? inversionAttacker.GetArchmageInnateOverride(defender, resistanceType, false)
                    : null;

                ResistanceMod = (float)Math.Max(0.0f, defender.GetResistanceMod(resistanceType, Attacker, Weapon, WeaponResistanceMod, inversionInnate));
            }

            // damage resistance rating
            DamageResistanceRatingMod = DamageResistanceRatingBaseMod = defender.GetDamageResistRatingMod(CombatType, true, Attacker, ratingScales.DamageRatingScale, out var rawDamageResistRating, out var scaledDamageResistRating);

            ratingScales.ReportIfChanged(rawDamageResistRating, scaledDamageResistRating);

            if (IsCritical)
            {
                CriticalDamageResistanceRatingMod = Creature.GetNegativeRatingMod(ratingScales.ScaleCritDamageResistRating(defender.GetCritDamageResistRating()));

                DamageResistanceRatingMod = Creature.AdditiveCombine(DamageResistanceRatingBaseMod, CriticalDamageResistanceRatingMod);
            }

            if (pkBattle)
            {
                PkDamageResistanceMod = Creature.GetNegativeRatingMod(defender.GetPKDamageResistRating());

                DamageResistanceRatingMod = Creature.AdditiveCombine(DamageResistanceRatingMod, PkDamageResistanceMod);
            }

            // get shield modifier
            ShieldMod = defender.GetShieldMod(attacker, DamageType, Weapon, positionalAttacker);

            // calculate final output damage
            Damage = DamageBeforeMitigation * ArmorMod * ShieldMod * ResistanceMod * DamageResistanceRatingMod;

            DamageMitigated = DamageBeforeMitigation - Damage;

            // World Events Hold, the last defender step: the ward on a warded ranged wave monster (missile cut while it is
            // up, melee shatters it; a Reflect bounce onto a warded firer is a missile like any other), and a run monster's
            // multiplier on a player. Both hand the damage back unchanged for every creature with no Hold tag, and Damage /
            // DamageMitigated are rewritten only when it changed.
            var held = playerDefender == null
                ? ACE.Server.WorldEvents.WorldEventHooks.HoldWardDamage(defender, CombatType == CombatType.Missile ? ACE.Server.WorldEvents.Defs.WorldEventCombatKinds.Missile : ACE.Server.WorldEvents.Defs.WorldEventCombatKinds.Melee, Damage)
                : Damage;

            if (playerDefender != null)
            {
                var holdMult = ACE.Server.WorldEvents.WorldEventHooks.HoldPlayerDamageMult(attacker);

                if (holdMult != 1.0)
                    held = (float)(Damage * holdMult);
            }

            if (held != Damage)
            {
                Damage = held;
                DamageMitigated = DamageBeforeMitigation - Damage;
            }

            return Damage;
        }

        public Quadrant GetQuadrant(Creature defender, Creature attacker, AttackHeight attackHeight, WorldObject damageSource)
        {
            var quadrant = attackHeight.ToQuadrant();

            var wo = damageSource.CurrentLandblock != null ? damageSource : attacker;

            quadrant |= wo.GetRelativeDir(defender);

            return quadrant;
        }

        /// <summary>
        /// Returns the chance for creature to avoid monster attack
        /// </summary>
        public float GetEvadeChance(Creature attacker, Creature defender)
        {
            AccuracyMod = attacker.GetAccuracyMod(Weapon);

            EffectiveAttackSkill = attacker.GetEffectiveAttackSkill();

            //var attackType = attacker.GetCombatType();

            EffectiveDefenseSkill = defender.GetEffectiveDefenseSkill(CombatType);

            // PvP rules choke point DF1 (Docs/Pvp/DESIGN.md "PvP rules (levers)"): pvp_melee_defense_mod / pvp_missile_defense_mod
            // scale the defender's skill for THIS roll only. EffectiveDefenseSkill (read by the debug dump) stays unscaled.
            var rollDefenseSkill = PvpRules.ApplyDefenseMod(attacker, defender, CombatType, EffectiveDefenseSkill);

            // Intimidate (Berserker T2, 2026-10-24): a reckless player attack lowers the MONSTER's defense skill for
            // this roll only, before the roll. PvE only - a Player defender is never reduced.
            if (attacker is Player intimidatingPlayer && defender is not Player)
                rollDefenseSkill = ACE.Server.ClassAbilities.Abilities.IntimidateAbility.ApplyToEvadeRoll(intimidatingPlayer, rollDefenseSkill);

            var evadeChance = 1.0f - SkillCheck.GetSkillChance(EffectiveAttackSkill, rollDefenseSkill);
            return (float)evadeChance;
        }

        /// <summary>
        /// Returns the base damage for a player attacker
        /// </summary>
        public void GetBaseDamage(Player attacker)
        {
            if (DamageSource.ItemType == ItemType.MissileWeapon)
            {
                DamageType = DamageSource.W_DamageType;

                // handle prismatic arrows
                if (DamageType == DamageType.Base)
                {
                    if (Weapon != null && Weapon.W_DamageType != DamageType.Undef)
                        DamageType = Weapon.W_DamageType;
                    else
                        DamageType = DamageType.Pierce;
                }
            }
            else
                DamageType = attacker.GetDamageType(false, CombatType.Melee);

            // TODO: combat maneuvers for player?
            BaseDamageMod = attacker.GetBaseDamageMod(DamageSource);

            // some quest bows can have built-in damage bonus
            if (Weapon?.WeenieType == WeenieType.MissileLauncher)
                BaseDamageMod.DamageBonus += Weapon.Damage ?? 0;

            if (DamageSource.ItemType == ItemType.MissileWeapon)
                BaseDamageMod.ElementalBonus = WorldObject.GetMissileElementalDamageBonus(Weapon, attacker, DamageType);

            // Weapon mods v3 Tier B: Heft into DamageBonus, Tension/Leverage into DamageMod - both inside the
            // "(base + bonus + elemental) * DamageMod" bracket. Kept out of BaseDamageMod's constructor so that
            // class stays byte-identical to upstream; see WeaponModCombat.ApplyBaseDamageMods for the reasoning.
            WeaponModCombat.ApplyBaseDamageMods(BaseDamageMod, Weapon, attacker);

            BaseDamage = (float)ThreadSafeRandom.Next(BaseDamageMod.MinDamage, BaseDamageMod.MaxDamage);
        }

        /// <summary>
        /// Returns the base damage for a non-player attacker
        /// </summary>
        public void GetBaseDamage(Creature attacker, MotionCommand motionCommand, AttackHook attackHook)
        {
            AttackPart = attacker.GetAttackPart(motionCommand, attackHook);
            if (AttackPart.Value == null)
            {
                GeneralFailure = true;
                return;
            }

            BaseDamageMod = attacker.GetBaseDamage(AttackPart.Value);
            BaseDamage = (float)ThreadSafeRandom.Next(BaseDamageMod.MinDamage, BaseDamageMod.MaxDamage);

            DamageType = attacker.GetDamageType(AttackPart.Value, CombatType);
        }

        /// <summary>
        /// Returns a body part for a player defender
        /// </summary>
        public void GetBodyPart(AttackHeight attackHeight)
        {
            // select random body part @ current attack height
            BodyPart = BodyParts.GetBodyPart(attackHeight);
        }

        public static readonly Quadrant LeftRight = Quadrant.Left | Quadrant.Right;
        public static readonly Quadrant FrontBack = Quadrant.Front | Quadrant.Back;

        /// <summary>
        /// Returns a body part for a creature defender
        /// </summary>
        public void GetBodyPart(Creature defender, Quadrant quadrant)
        {
            // get cached body parts table
            var bodyParts = Creature.GetBodyParts(defender.WeenieClassId);

            if (bodyParts == null)
            {
                Evaded = true;
                return;
            }

            // rng roll for body part
            var bodyPart = bodyParts.RollBodyPart(quadrant);

            if (bodyPart == CombatBodyPart.Undefined)
            {
                log.DebugFormat("DamageEvent.GetBodyPart({0} ({1}) ) - couldn't find body part for wcid {2}, Quadrant {3}", defender?.Name, defender?.Guid, defender.WeenieClassId, quadrant);
                Evaded = true;
                return;
            }

            //Console.WriteLine($"AttackHeight: {AttackHeight}, Quadrant: {quadrant & FrontBack}{quadrant & LeftRight}, AttackPart: {bodyPart}");

            defender.Biota.PropertiesBodyPart.TryGetValue(bodyPart, out var value);
            PropertiesBodyPart = new KeyValuePair<CombatBodyPart, PropertiesBodyPart>(bodyPart, value);

            // select random body part @ current attack height
            /*BiotaPropertiesBodyPart = BodyParts.GetBodyPart(defender, attackHeight);

            if (BiotaPropertiesBodyPart == null)
            {
                Evaded = true;
                return;
            }*/

            CreaturePart = new Creature_BodyPart(defender, PropertiesBodyPart);
        }

        public void ShowInfo(Creature creature)
        {
            var targetInfo = PlayerManager.GetOnlinePlayer(creature.DebugDamageTarget);
            if (targetInfo == null)
            {
                creature.DebugDamage = Creature.DebugDamageType.None;
                return;
            }

            // setup
            var info = $"Attacker: {Attacker.Name} ({Attacker.Guid})\n";
            info += $"Defender: {Defender.Name} ({Defender.Guid})\n";

            info += $"CombatType: {CombatType}\n";

            info += $"DamageSource: {DamageSource.Name} ({DamageSource.Guid})\n";
            info += $"DamageType: {DamageType}\n";

            if (OriginalDamageType != null)
                info += $"OriginalDamageType: {OriginalDamageType} (Volatile Elements)\n";

            var weaponName = Weapon != null ? $"{Weapon.Name} ({Weapon.Guid})" : "None\n";
            info += $"Weapon: {weaponName}\n";

            info += $"AttackType: {AttackType}\n";
            info += $"AttackHeight: {AttackHeight}\n";

            // lifestone protection
            if (LifestoneProtection)
                info += $"LifestoneProtection: {LifestoneProtection}\n";

            // evade
            if (AccuracyMod != 0.0f && AccuracyMod != 1.0f)
                info += $"AccuracyMod: {AccuracyMod}\n";

            info += $"EffectiveAttackSkill: {EffectiveAttackSkill}\n";
            info += $"EffectiveDefenseSkill: {EffectiveDefenseSkill}\n";

            if (Attacker.Overpower != null)
                info += $"Overpower: {Overpower} ({Creature.GetOverpowerChance(Attacker, Defender)})\n";

            info += $"EvasionChance: {EvasionChance}\n";
            info += $"Evaded: {Evaded}\n";

            if (!(Attacker is Player))
            {
                if (AttackMotion != null)
                    info += $"AttackMotion: {AttackMotion}\n";
                if (AttackPart.Value != null)
                    info += $"AttackPart: {AttackPart.Key}\n";
            }

            // base damage
            if (BaseDamageMod != null)
                info += $"BaseDamageRange: {BaseDamageMod.Range}\n";


            info += $"BaseDamage: {BaseDamage}\n";

            // damage modifiers
            info += $"AttributeMod: {AttributeMod}\n";

            if (PowerMod != 0.0f && PowerMod != 1.0f)
                info += $"PowerMod: {PowerMod}\n";

            if (SlayerMod != 0.0f && SlayerMod != 1.0f)
                info += $"SlayerMod: {SlayerMod}\n";

            if (BaseDamageMod != null)
            {
                if (BaseDamageMod.DamageBonus != 0)
                    info += $"DamageBonus: {BaseDamageMod.DamageBonus}\n";

                if (BaseDamageMod.DamageMod != 0.0f && BaseDamageMod.DamageMod != 1.0f)
                    info += $"DamageMod: {BaseDamageMod.DamageMod}\n";

                if (BaseDamageMod.ElementalBonus != 0)
                    info += $"ElementalDamageBonus: {BaseDamageMod.ElementalBonus}\n";
            }

            // critical hit
            info += $"CriticalChance: {CriticalChance}\n";
            info += $"CriticalHit: {IsCritical}\n";

            if (CriticalDefended)
                info += $"CriticalDefended: {CriticalDefended}\n";

            if (CriticalDamageMod != 0.0f && CriticalDamageMod != 1.0f)
                info += $"CriticalDamageMod: {CriticalDamageMod}\n";

            if (CriticalDamageRatingMod != 0.0f && CriticalDamageRatingMod != 1.0f)
                info += $"CriticalDamageRatingMod: {CriticalDamageRatingMod}\n";

            // damage ratings
            if (DamageRatingBaseMod != 0.0f && DamageRatingBaseMod != 1.0f)
                info += $"DamageRatingBaseMod: {DamageRatingBaseMod}\n";

            if (HeritageMod != 0.0f && HeritageMod != 1.0f)
                info += $"HeritageMod: {HeritageMod}\n";

            if (RecklessnessMod != 0.0f && RecklessnessMod != 1.0f)
                info += $"RecklessnessMod: {RecklessnessMod}\n";

            if (SneakAttackMod != 0.0f && SneakAttackMod != 1.0f)
                info += $"SneakAttackMod: {SneakAttackMod}\n";

            if (PkDamageMod != 0.0f && PkDamageMod != 1.0f)
                info += $"PkDamageMod: {PkDamageMod}\n";

            if (DamageRatingMod != 0.0f && DamageRatingMod != 1.0f)
                info += $"DamageRatingMod: {DamageRatingMod}\n";

            // PvP rules rating scales - printed whenever the PvP rating lever ran for this hit, including a scale of
            // exactly 0, which a non-zero test would hide
            if (PvpRatingScalesApplied)
            {
                info += $"PvpDamageRatingScale: {PvpDamageRatingScale}\n";
                info += $"PvpCritDamageRatingScale: {PvpCritDamageRatingScale}\n";
            }

            if (BodyPart != 0)
            {
                // player body part
                info += $"BodyPart: {BodyPart}\n";
            }
            if (Armor != null && Armor.Count > 0)
            {
                info += $"Armors: {string.Join(", ", Armor.Select(i => i.Name))}\n";
            }

            if (CreaturePart != null)
            {
                // creature body part
                info += $"BodyPart: {PropertiesBodyPart.Key}\n";
                info += $"BaseArmor: {CreaturePart.Biota.Value.BaseArmor}\n";
            }

            // damage mitigation
            if (ArmorMod != 0.0f && ArmorMod != 1.0f)
                info += $"ArmorMod: {ArmorMod}\n";

            if (ResistanceMod != 0.0f && ResistanceMod != 1.0f)
                info += $"ResistanceMod: {ResistanceMod}\n";

            if (ShieldMod != 0.0f && ShieldMod != 1.0f)
                info += $"ShieldMod: {ShieldMod}\n";

            if (WeaponResistanceMod != 0.0f && WeaponResistanceMod != 1.0f)
                info += $"WeaponResistanceMod: {WeaponResistanceMod}\n";

            if (DamageResistanceRatingBaseMod != 0.0f && DamageResistanceRatingBaseMod != 1.0f)
                info += $"DamageResistanceRatingBaseMod: {DamageResistanceRatingBaseMod}\n";

            if (CriticalDamageResistanceRatingMod != 0.0f && CriticalDamageResistanceRatingMod != 1.0f)
                info += $"CriticalDamageResistanceRatingMod: {CriticalDamageResistanceRatingMod}\n";

            if (PkDamageResistanceMod != 0.0f && PkDamageResistanceMod != 1.0f)
                info += $"PkDamageResistanceMod: {PkDamageResistanceMod}\n";

            if (DamageResistanceRatingMod != 0.0f && DamageResistanceRatingMod != 1.0f)
                info += $"DamageResistanceRatingMod: {DamageResistanceRatingMod}\n";

            if (IgnoreMagicArmor)
                info += $"IgnoreMagicArmor: {IgnoreMagicArmorIntensity:0.###}\n";
            if (IgnoreMagicResist)
                info += $"IgnoreMagicResist: {IgnoreMagicResistIntensity:0.###}\n";

            // final damage
            info += $"DamageBeforeMitigation: {DamageBeforeMitigation}\n";
            info += $"DamageMitigated: {DamageMitigated}\n";
            info += $"Damage: {Damage}\n";

            info += "----";

            targetInfo.Session.Network.EnqueueSend(new GameMessageSystemChat(info, ChatMessageType.Broadcast));
        }

        public void HandleLogging(Creature attacker, Creature defender)
        {
            if (attacker != null && (attacker.DebugDamage & Creature.DebugDamageType.Attacker) != 0)
            {
                ShowInfo(attacker);
            }
            if (defender != null && (defender.DebugDamage & Creature.DebugDamageType.Defender) != 0)
            {
                ShowInfo(defender);
            }
        }

        /// <summary>
        /// Records that the PvP damage cap bit this hit at <paramref name="point"/>, and - because the full
        /// ShowInfo dump has already been sent by CalculateDamage before the cap ran - sends each debug-damage
        /// listener a one-line follow-up naming the cap, so /debugdamage shows the number that actually landed.
        /// </summary>
        public void OnPvpDamageCapped(PvpChokePoint point, float damageBeforeCap)
        {
            PvpDamageBeforeCap = damageBeforeCap;
            PvpDamageCapPoint = point;

            if (Attacker != null && (Attacker.DebugDamage & Creature.DebugDamageType.Attacker) != 0)
                ShowPvpCapInfo(Attacker);

            if (Defender != null && (Defender.DebugDamage & Creature.DebugDamageType.Defender) != 0)
                ShowPvpCapInfo(Defender);
        }

        private void ShowPvpCapInfo(Creature creature)
        {
            var targetInfo = PlayerManager.GetOnlinePlayer(creature.DebugDamageTarget);

            if (targetInfo == null)
                return;

            targetInfo.Session.Network.EnqueueSend(new GameMessageSystemChat($"PvpDamageCap: {PvpDamageBeforeCap} -> {Damage} ({PvpDamageCapPoint})", ChatMessageType.Broadcast));
        }

        public AttackConditions AttackConditions
        {
            get
            {
                var attackConditions = new AttackConditions();

                if (CriticalDefended)
                    attackConditions |= AttackConditions.CriticalProtectionAugmentation;
                if (RecklessnessMod > 1.0f)
                    attackConditions |= AttackConditions.Recklessness;
                if (SneakAttackMod > 1.0f)
                    attackConditions |= AttackConditions.SneakAttack;
                if (Overpower)
                    attackConditions |= AttackConditions.Overpower;

                return attackConditions;
            }
        }
    }
}
