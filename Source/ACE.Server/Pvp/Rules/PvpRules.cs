using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// The architect's choke-point ids (Docs/Pvp/DESIGN.md "PvP rules (levers)"). Each names ONE call site a
    /// lever is applied at, so the observer, /pvprules and the log can say exactly where a lever bit.
    /// </summary>
    public enum PvpChokePoint
    {
        /// <summary>Player.DamageTarget (Player_Combat.cs): melee / missile / multi-shot / cleave / riposte hit, before it is applied.</summary>
        C1,
        /// <summary>SpellProjectile.DamageTarget: war, void and life health projectiles, after ratings and Soul Tether, before the cloak proc.</summary>
        C2,
        /// <summary>WorldObject_Magic HandleCastSpell_Boost: Harm (health), before the cloak proc.</summary>
        C3,
        /// <summary>WorldObject_Magic HandleCastSpell_Transfer: Drain Health, before the cloak proc; the caster's gain is recomputed.</summary>
        C4,
        /// <summary>DamageEvent rating mods (rating-scale lever).</summary>
        R1,
        /// <summary>SpellProjectile rating mods (rating-scale lever).</summary>
        R2,
        /// <summary>Healer completion, beside the PK movement cap (airborne / interval lever).</summary>
        H1,
        /// <summary>Player_Use.ApplyConsumable (airborne / interval lever).</summary>
        H2,
        /// <summary>EnchantmentManager.SelectDispel, beside the item-source exclusion (dispel vuln lock).</summary>
        D1,
        /// <summary>Player_WeaponMods.ApplyWeaponModCleanse, before its random harmful-enchantment pick (dispel vuln lock).</summary>
        D2,
        /// <summary>Cloak.GetDamageReductionAmount: a cloak proc's flat mitigation on a PvP hit (cloak damage reduction lever).</summary>
        CLK1,
        /// <summary>Creature_Melee.GetCleaveTarget: a cleave candidate that is a player (cleave-enabled lever).</summary>
        CLV1,
        /// <summary>Player_Combat.UpdatePKTimer, on the PK-timer inactive-to-active transition (strip rare buffs lever).</summary>
        RB1,
        /// <summary>DamageEvent / SpellProjectile, before the PvP damage cap (C1/C2): the arena 1v1 damage mod.</summary>
        AM1,
        /// <summary>Healer.DoSkillCheck: the arena 1v1 healing-kit skill bonus cap.</summary>
        HK1,
        /// <summary>Healer.GetHealAmount: the arena 1v1 healing-kit restoration bonus cap.</summary>
        HK2,
        /// <summary>Player.DamageTarget (Player_Combat.cs), after AM1 and before C1: the melee / missile / crit damage mods.</summary>
        M1,
        /// <summary>SpellProjectile.DamageTarget, before C2: the war magic / crit damage mods.</summary>
        M2,
        /// <summary>SpellProjectile.CalculateDamage, after the retail PvP 0.72 absorb block: the magic absorb mod.</summary>
        AB1,
        /// <summary>WorldObject_Magic HandleCastSpell_Boost: a heal spell or heal gem's positive Health boost, before the vital write (healing mod).</summary>
        HL1,
        /// <summary>Healer.GetHealAmount: a Health kit's heal, after the HK1/HK2 arena caps and the healing rating (healing mod).</summary>
        HL2,
        /// <summary>Food.BoostVital: a Health food or potion's positive boost (healing mod).</summary>
        HL3,
        /// <summary>EnchantmentManager.ApplyHealingTick: a positive heal-over-time tick, Aetheria included (healing mod).</summary>
        HL4,
        /// <summary>WorldObject_Magic HandleCastSpell_Transfer: the CASTER's share of a Drain / Transfer's Health gain, after C4, the cloak proc and the fellow split (healing mod).</summary>
        HL5,
        /// <summary>WorldObject_Magic HandleCastSpell_Transfer payout loop: ONE fellow's share of a Drain's Health surplus, keyed on that fellow (healing mod).</summary>
        HL5F,
        /// <summary>WorldObject_Weapon.GetWeaponCriticalChance: the physical Critical Strike imbue's crit chance bonus (cs crit mod).</summary>
        CS1,
        /// <summary>WorldObject_Weapon.GetWeaponMagicCritFrequency: the magic Critical Strike imbue's crit chance bonus (cs crit mod).</summary>
        CS2,
        /// <summary>WorldObject_Weapon.GetWeaponCritDamageMod: the Crippling Blow imbue's crit damage multiplier (cb crit mod).</summary>
        CB1,
        /// <summary>WorldObject_Magic HandleCastSpell_Boost, before C3: effective-HP normalization of a Harm.</summary>
        N3,
        /// <summary>WorldObject_Magic HandleCastSpell_Transfer, before C4: effective-HP normalization of a Drain Health's source loss.</summary>
        N4,
        /// <summary>Player.DamageTarget (Player_Combat.cs), directly after C1: the arena overtime damage ramp.</summary>
        OT1,
        /// <summary>SpellProjectile.DamageTarget, directly after C2 and before the cloak proc: the arena overtime damage ramp.</summary>
        OT2,
        /// <summary>WorldObject_Magic HandleCastSpell_Boost, directly after C3 and before the cloak proc: the arena overtime damage ramp on a Harm.</summary>
        OT3,
        /// <summary>WorldObject_Magic HandleCastSpell_Transfer, at the drained player's Health write (after C4, the cloak proc and every derivation of the caster's gain, before the pre-write mitigations): the arena overtime damage ramp on a Drain Health's source loss only.</summary>
        OT4,
        /// <summary>DamageEvent, after the physical crit chance is computed and before the logout always-crit override: the arena / battleground per-category crit chance multiplier.</summary>
        CC1,
        /// <summary>SpellProjectile.CalculateDamage, at the crit roll's call site (not DamageTarget, which is M2): the arena / battleground war magic crit chance multiplier.</summary>
        CC2,
        /// <summary>DamageEvent.GetEvadeChance: the defender's effective melee / missile defense skill in the evade roll (pvp_melee_defense_mod / pvp_missile_defense_mod).</summary>
        DF1,
        /// <summary>WorldObject.TryResistSpell: the target's effective magic defense in the spell resist roll (pvp_magic_defense_mod).</summary>
        DF2
    }

    /// <summary>
    /// The PvP balance levers. Every entry point here returns its input UNCHANGED unless the interaction is PvP
    /// (<see cref="PvpClassifier.Classify"/>: two distinct players - never self, never a pet) AND the master switch
    /// pvp_rules_enabled is on, and it classifies BEFORE it reads any setting, so a PvE interaction never reads a
    /// lever (proved by PvpRulesPveInvarianceTests with an instrumented <see cref="PvpRuleTunables.DialSource"/>).
    ///
    /// Two layers:
    ///  - pure functions (<see cref="CapDamage"/>, <see cref="RecomputeDrainGain"/>, and the overloads that take a
    ///    <see cref="PvpInteraction"/> and a <see cref="PvpRuleDials"/>) that read nothing global except the
    ///    max-health seam;
    ///  - choke-point entry points that take the raw (source, target) pair, classify, then read the dials once.
    ///
    /// A lever that changes a value reports it through <see cref="Report"/>, which counts it per choke point
    /// (shown by /pvprules) and hands (point, before, after) to <see cref="Observer"/>.
    /// </summary>
    public static partial class PvpRules
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpRules));

        /// <summary>
        /// Test / stats hook: (choke point, value before, value after), called once each time a lever changes a
        /// value. Null by default. A throwing observer is logged and swallowed - it can never fail a hit.
        /// </summary>
        public static Action<PvpChokePoint, double, double> Observer;

        private static readonly long[] appliedCounts = new long[Enum.GetValues(typeof(PvpChokePoint)).Length];

        /// <summary>
        /// Seam for the defender's max health, read only on a PvP interaction with a fraction cap configured.
        /// Tests swap it because a test Player built without the world database has no vitals.
        /// </summary>
        internal static Func<Creature, double> MaxHealthSource = DefaultMaxHealth;

        internal static double DefaultMaxHealth(Creature defender) => defender.Health.MaxValue;

        /// <summary>
        /// Seam for "now", read only by <see cref="TryBlockConsumable"/> to compare against a caller-supplied
        /// last-use timestamp. Tests swap it so the interval lever can be exercised without a real clock.
        /// </summary>
        internal static Func<DateTime> UtcNowSource = () => DateTime.UtcNow;

        // ================= dials and reporting =================

        /// <summary>
        /// The resolved levers: <see cref="PvpRuleTunables.DialSource"/>, or <see cref="PvpRuleTunables.Defaults"/>
        /// if the source is missing, returns null or throws. Call ONLY after classifying the interaction as PvP (or,
        /// for an arena-only lever, confirming the player is bound to a Live arena match) - see the isolation note
        /// on <see cref="PvpRuleTunables"/>.
        /// </summary>
        public static PvpRuleDials ReadDials()
        {
            try
            {
                return PvpRuleTunables.DialSource?.Invoke() ?? PvpRuleTunables.Defaults;
            }
            catch (Exception ex)
            {
                log.Error("[PVP] PvpRuleTunables.DialSource threw; using the built-in defaults", ex);
                return PvpRuleTunables.Defaults;
            }
        }

        /// <summary>
        /// Records that a lever changed a value at <paramref name="point"/>: bumps the per-point count /pvprules
        /// prints and calls <see cref="Observer"/>. Call it only when the value actually changed.
        /// </summary>
        public static void Report(PvpChokePoint point, double before, double after)
        {
            Interlocked.Increment(ref appliedCounts[(int)point]);

            var observer = Observer;

            if (observer == null)
                return;

            try
            {
                observer(point, before, after);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] PvpRules.Observer threw at {point}", ex);
            }
        }

        /// <summary>How many times a lever has changed a value at <paramref name="point"/> since boot.</summary>
        public static long GetAppliedCount(PvpChokePoint point) => Interlocked.Read(ref appliedCounts[(int)point]);

        /// <summary>Every choke point's count since boot, in enum order.</summary>
        public static IReadOnlyList<(PvpChokePoint Point, long Count)> GetAppliedCounts()
        {
            var rows = new List<(PvpChokePoint, long)>();

            foreach (PvpChokePoint point in Enum.GetValues(typeof(PvpChokePoint)))
                rows.Add((point, GetAppliedCount(point)));

            return rows;
        }

        // ================= damage cap (L1): C1-C4 =================

        /// <summary>
        /// PURE. The per-hit cap: min(hit, absoluteCap if &gt; 0, floor(maxHealthFraction * defenderMaxHealth) if
        /// both &gt; 0). A hit already at or under every active cap comes back unchanged, and with both caps off the
        /// input is returned as is.
        /// </summary>
        public static double CapDamage(double hit, long absoluteCap, double maxHealthFraction, double defenderMaxHealth)
        {
            var capped = hit;

            if (absoluteCap > 0 && capped > absoluteCap)
                capped = absoluteCap;

            if (maxHealthFraction > 0 && defenderMaxHealth > 0)
            {
                // floored to a whole point: C1/C2 later Math.Round the damage, so an unfloored 201.5 would
                // land as 202 - more than the fraction allows - while C3/C4 floor
                var fractionCap = Math.Floor(maxHealthFraction * defenderMaxHealth);

                if (capped > fractionCap)
                    capped = fractionCap;
            }

            return capped;
        }

        /// <summary>
        /// The damage cap for an already-classified interaction. Returns <paramref name="hit"/> unchanged for a
        /// non-PvP interaction, a null snapshot, the master switch off, both caps off, or a hit already under the
        /// cap; otherwise returns the capped hit and <see cref="Report"/>s it at <paramref name="point"/>.
        /// </summary>
        public static float ApplyDamageCap(PvpChokePoint point, PvpInteraction interaction, PvpRuleDials dials, float hit)
        {
            if (!interaction.IsPvp || dials == null || !dials.Enabled)
                return hit;

            var fractionOn = dials.DamageCapMaxHealthFraction > 0;

            if (dials.DamageCap <= 0 && !fractionOn)
                return hit;

            if (!(hit > 0))
                return hit;

            var maxHealth = fractionOn ? MaxHealthSource(interaction.Defender) : 0.0;

            var capped = (float)CapDamage(hit, dials.DamageCap, dials.DamageCapMaxHealthFraction, maxHealth);

            if (!(capped < hit))
                return hit;

            Report(point, hit, capped);

            return capped;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for C1-C3: classifies (<paramref name="source"/>, <paramref name="target"/>) first and
        /// reads the dials only for a PvP pair. The attacker's final number goes in, BEFORE the defender's own
        /// reductions (cloak proc, Sanguine Ward, Mana Barrier), so those reduce the capped hit.
        /// </summary>
        public static float ApplyDamageCap(PvpChokePoint point, WorldObject source, Creature target, float hit)
        {
            var interaction = PvpClassifier.Classify(source, target);

            if (!interaction.IsPvp)
                return hit;

            return ApplyDamageCap(point, interaction, ReadDials(), hit);
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for integer vital changes (C4 Drain Health's source loss): as the float overload, with a
        /// capped value floored to a whole point so the result never exceeds the cap. Unchanged input comes back
        /// exactly.
        /// </summary>
        public static uint ApplyDamageCap(PvpChokePoint point, WorldObject source, Creature target, uint hit)
        {
            var capped = ApplyDamageCap(point, source, target, (float)hit);

            return capped < hit ? (uint)Math.Floor(capped) : hit;
        }

        /// <summary>
        /// PURE. A Drain's caster gain after its source loss was capped: the gain the uncapped path would have paid
        /// for <paramref name="cappedSource"/> (source * (1 - lossPercent) * boostMod, the same formula
        /// HandleCastSpell_Transfer and its cloak block use), never above the gain already computed
        /// (<paramref name="priorGain"/>, which carries the caster's missing-health and TransferCap bounds).
        /// </summary>
        public static uint RecomputeDrainGain(uint cappedSource, uint priorGain, float lossPercent, float boostMod)
        {
            var recomputed = Math.Round(cappedSource * (1.0f - lossPercent) * boostMod);

            if (!(recomputed > 0))
                return 0;

            return recomputed < priorGain ? (uint)recomputed : priorGain;
        }

        /// <summary>
        /// PURE. A Drain's caster gain for a given source <paramref name="loss"/>: round(loss x (1 - lossPercent) x
        /// boostMod), the formula HandleCastSpell_Transfer's cloak-proc block recomputes the gain with. Extracted so
        /// the overtime tests can exercise the exact arithmetic that site runs: the loss handed to it is the
        /// UNRAMPED loss, because OT4 applies only at the Health write, after every gain derivation.
        /// </summary>
        public static uint DrainGainFromLoss(uint loss, float lossPercent, float boostMod)
            => (uint)Math.Round(loss * (1.0f - lossPercent) * boostMod);

        // ================= airborne / interval consumable block (L3): H1-H2 =================

        /// <summary>
        /// PURE. Which reason (if any) a consumable use that is about to complete must be refused for: the actor
        /// is airborne while <paramref name="dials"/>.BlockAirborneConsumables is on, or the elapsed time since
        /// <paramref name="lastUseUtc"/> is under <paramref name="dials"/>.ConsumableMinIntervalMs (a null
        /// <paramref name="lastUseUtc"/>, i.e. no prior use recorded, never triggers the interval). Airborne is
        /// checked first, so an airborne actor inside the interval too is reported as Airborne.
        /// </summary>
        public static PvpConsumableBlock ShouldBlockConsumable(PvpRuleDials dials, bool isJumping, DateTime? lastUseUtc, DateTime now)
        {
            if (dials.BlockAirborneConsumables && isJumping)
                return PvpConsumableBlock.Airborne;

            if (dials.ConsumableMinIntervalMs > 0 && lastUseUtc.HasValue)
            {
                var elapsedMs = (now - lastUseUtc.Value).TotalMilliseconds;

                if (elapsedMs < dials.ConsumableMinIntervalMs)
                    return PvpConsumableBlock.Interval;
            }

            return PvpConsumableBlock.None;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for H1 (Healer completion) and H2 (Player_Use.ApplyConsumable). ARENA ONLY (owner
        /// ruling 2026-09-27): the actor must be bound to a LIVE arena match (<see cref="PvpPlayerRules.InLiveMatch"/>,
        /// checked FIRST), and the dials are read only then, so any other consumable use never reads a lever. This
        /// is deliberately NOT a PK-timer gate (the old, deleted PvpClassifier.IsEngaged also counted a running PK timer),
        /// which would refuse a PK's potions for up to pk_timer seconds after a PvP hit while they fight monsters.
        /// In the open world a PK can therefore use consumables mid-jump and with no minimum interval. A non-None result means
        /// refuse: the caller must not decrement kit uses, consume the item, or cast its spell, and this reports
        /// the refusal at <paramref name="point"/>. <paramref name="isJumping"/> and <paramref name="lastUseUtc"/>
        /// are taken as parameters, supplied by the caller from the live actor, so this stays testable without a
        /// physics-backed Player (Player.IsJumping reads PhysicsObj).
        /// </summary>
        public static PvpConsumableBlock TryBlockConsumable(PvpChokePoint point, Player actor, bool isJumping, DateTime? lastUseUtc)
        {
            if (actor == null || !PvpPlayerRules.InLiveMatch(actor.PvpBinding))
                return PvpConsumableBlock.None;

            var dials = ReadDials();

            if (!dials.Enabled)
                return PvpConsumableBlock.None;

            var block = ShouldBlockConsumable(dials, isJumping, lastUseUtc, UtcNowSource());

            if (block == PvpConsumableBlock.None)
                return PvpConsumableBlock.None;

            Report(point, 1, 0);

            return block;
        }

        // ================= dispel vuln lock (L4): D1, D2 =================

        /// <summary>
        /// The SpellCategory values for a "vulnerability" debuff - a resistance, armor, or melee-defense
        /// lowering enchantment. Source of truth: the client dat via ACE.DatLoader, NOT ace_world.spell -
        /// that table has no category column at all (SpellCategory is a dat-only field, SpellBase.Category).
        /// Verified 2026-09-26 with a scratch ACE.DatLoader probe (DatManager.PortalDat.SpellTable.Spells,
        /// against C:\ACE\Dats\) dumping SpellBase.Category for the spell ids below (Docs/Pvp/DESIGN.md
        /// "PvP rules (levers)" carries the same table):
        ///
        ///   spell id(s)              name                              SpellCategory
        ///   15, 16                   Vulnerability Other/Self I         MeleeDefenseLowering
        ///   21, 22                   Fire Vulnerability Other/Self I    FireVulnerability
        ///   25, 1323-1327            Imperil Other I-VI                 ArmorLowering
        ///   521, 522                 Acid Vulnerability Other I/II      AcidVulnerability
        ///   1042, 1048               Bludgeoning Vulnerability S/O I    BludgeonVulnerability
        ///   1060                     Cold Vulnerability Other I         ColdVulnerability
        ///   1078, 1084               Lightning Vulnerability S/O I      ElectricVulnerability
        ///   1121, 1127               Blade Vulnerability Self/Other I   SlashVulnerability
        ///   1145, 1151               Piercing Vulnerability S/O I       PierceVulnerability
        ///
        /// NOT the same enum member as MonsterEffects/Effects/DebuffEffect.cs's synthetic monster Imperil,
        /// which tags itself SpellCategory.ArmorValueLowering (161) - a DIFFERENT value from ArmorLowering
        /// (116) above, so that monster-authored effect is outside this set by category alone, in addition
        /// to failing the player-caster check below. DebuffEffect.ApplyVuln DOES reuse the real per-element
        /// categories (e.g. SlashVulnerability) for its synthetic monster vulns, but always with a Creature
        /// (monster) as the caster, so <see cref="IsPlayerCastVulnerability"/> excludes them on caster guid.
        /// </summary>
        internal static readonly HashSet<SpellCategory> VulnerabilityCategories = new HashSet<SpellCategory>
        {
            SpellCategory.MeleeDefenseLowering,
            SpellCategory.ArmorLowering,
            SpellCategory.AcidVulnerability,
            SpellCategory.BludgeonVulnerability,
            SpellCategory.ColdVulnerability,
            SpellCategory.ElectricVulnerability,
            SpellCategory.FireVulnerability,
            SpellCategory.PierceVulnerability,
            SpellCategory.SlashVulnerability,
        };

        /// <summary>
        /// PURE. TRUE for a vulnerability debuff (<see cref="VulnerabilityCategories"/>) whose caster guid is
        /// a player's (<see cref="ObjectGuid.IsPlayer(uint)"/>) - a player-cast vulnerability. A monster-cast
        /// vulnerability, or any enchantment outside the category set, is never a player-cast vulnerability.
        /// Does NOT check the caster against the target - a self-cast is still "player-cast" by this
        /// predicate; <see cref="ApplyDispelVulnLock"/> is what excludes self from the lock, because the
        /// lock protects only against a vulnerability cast by ANOTHER player.
        /// </summary>
        public static bool IsPlayerCastVulnerability(SpellCategory category, uint casterObjectId)
            => VulnerabilityCategories.Contains(category) && ObjectGuid.IsPlayer(casterObjectId);

        /// <summary>
        /// CHOKE-POINT ENTRY for D1 (EnchantmentManager.SelectDispel) and D2 (Player_WeaponMods.
        /// ApplyWeaponModCleanse): while a player-cast vulnerability lock is active on <paramref name="target"/>
        /// - <paramref name="target"/> is a Player and their LastPkAttackTimestamp is within
        /// pvp_dispel_vuln_lock_seconds - excludes vulnerability enchantments cast by ANOTHER player from
        /// <paramref name="candidates"/>. A monster-cast vulnerability, a SELF-cast vulnerability (the
        /// enchantment's CasterObjectId equals <paramref name="target"/>'s own guid - the lock protects
        /// against another player's vuln, never the target's own), a non-vulnerability harmful enchantment,
        /// and every beneficial enchantment stay dispellable throughout.
        ///
        /// Classify before reading any setting: the target-state check (is <paramref name="target"/> a
        /// Player) happens first, so a non-player target never reads a setting; then the PvP-classification
        /// check (does <paramref name="candidates"/> hold at least one player-cast vulnerability from
        /// ANOTHER player) happens before the dials are read, so a Player target with no such vuln - a PvE
        /// interaction - never reads a setting either. Only once both checks pass are the dials read, exactly
        /// once. The master switch off, the lock seconds at 0 or less, the target outside the window, or
        /// nothing actually excluded all return <paramref name="candidates"/> unchanged, with no
        /// <see cref="Report"/>.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> ApplyDispelVulnLock(WorldObject target, List<PropertiesEnchantmentRegistry> candidates, PvpChokePoint point)
        {
            if (candidates == null || candidates.Count == 0)
                return candidates;

            if (!(target is Player targetPlayer))
                return candidates;

            var targetGuid = targetPlayer.Guid.Full;

            // PvP classification: only a vuln cast by ANOTHER player makes this a PvP question
            if (!candidates.Any(e => IsPlayerCastVulnerability(e.SpellCategory, e.CasterObjectId) && e.CasterObjectId != targetGuid))
                return candidates;

            var dials = ReadDials();

            if (dials == null || !dials.Enabled || dials.DispelVulnLockSeconds <= 0)
                return candidates;

            if (Time.GetUnixTime() - targetPlayer.LastPkAttackTimestamp >= dials.DispelVulnLockSeconds)
                return candidates;

            var before = candidates.Count;

            var filtered = candidates
                .Where(e => !(IsPlayerCastVulnerability(e.SpellCategory, e.CasterObjectId) && e.CasterObjectId != targetGuid))
                .ToList();

            if (filtered.Count != before)
                Report(point, before, filtered.Count);

            return filtered;
        }

        // ================= cloak damage reduction (L5): CLK1 =================

        /// <summary>
        /// CHOKE-POINT ENTRY for CLK1 (Cloak.GetDamageReductionAmount): classifies (<paramref name="source"/>,
        /// <paramref name="defender"/>) first and reads the dials only for a PvP pair - a PvE hit (including a
        /// self-cast, where source and defender are the same player) keeps <paramref name="baseAmount"/> (the
        /// fork's un-halved 200-point base) unchanged. A PvP pair, with the master switch on, returns
        /// pvp_cloak_damage_reduction rounded to the nearest int, defaulting to 100 - the same 100 the fork's old
        /// unconditional `source is Player` halving produced for a true PvP hit.
        /// </summary>
        public static int ApplyCloakDamageReduction(WorldObject source, Creature defender, int baseAmount)
        {
            var interaction = PvpClassifier.Classify(source, defender);

            if (!interaction.IsPvp)
            {
                // Outside classified PvP, reproduce the fork's old behavior EXACTLY, with no setting
                // read at all: source is Player (self-cast Harm, or a player hitting a cloaked monster)
                // keeps the old hardcoded halving; anything else (a monster source) keeps the un-halved
                // base. Only a truly classified PvP pair reads pvp_cloak_damage_reduction, below.
                return source is Player ? baseAmount / 2 : baseAmount;
            }

            var dials = ReadDials();

            if (dials == null || !dials.Enabled)
                return baseAmount;

            var reduced = Convert.ToInt32(dials.CloakDamageReduction);

            if (reduced != baseAmount)
                Report(PvpChokePoint.CLK1, baseAmount, reduced);

            return reduced;
        }

        // ================= cleave player-target gate (L6): CLV1 =================

        /// <summary>
        /// CHOKE-POINT ENTRY for CLV1 (Creature_Melee.GetCleaveTarget): TRUE when <paramref name="candidate"/>
        /// must be skipped as a cleave target. Classifies FIRST - <paramref name="candidate"/> must be a Player
        /// and <paramref name="attacker"/> vs <paramref name="candidate"/> must be PvP - so a non-player cleave
        /// candidate, or a candidate that fails PvP classification, never reads a setting. Mirrors Doctide's
        /// admin exemption: an admin attacker's cleave is never gated.
        /// </summary>
        public static bool ShouldSkipCleaveTarget(Player attacker, Creature candidate)
        {
            if (!(candidate is Player))
                return false;

            var interaction = PvpClassifier.Classify(attacker, candidate);

            if (!interaction.IsPvp)
                return false;

            if (attacker != null && attacker.IsAdmin)
                return false;

            var dials = ReadDials();

            if (dials == null || !dials.Enabled || dials.CleaveEnabled)
                return false;

            Report(PvpChokePoint.CLV1, 1, 0);

            return true;
        }

        // ================= strip rare buffs on every PvP hit (L7): RB1 =================

        /// <summary>
        /// CHOKE-POINT ENTRY for RB1 (Player_Combat.UpdatePKTimer, on EVERY successful PvP hit - mirroring
        /// Doctide's DispelPkRares, which runs from every UpdatePKTimer call, not only a PK-timer
        /// inactive-to-active transition, so a rare gem re-applied mid-engagement cannot survive the rest of
        /// the fight): TRUE when the caller must call <see cref="Player.StripRareGemBuffs"/>. The caller has
        /// already established PvP - UpdatePKTimers runs only for two non-Free-status players - so this reads
        /// the master switch and pvp_strip_rare_buffs directly, with no further classification. The caller
        /// (UpdatePKTimer) additionally gates the actual strip behind Player.HasAnyStrippableRareGemBuff, a
        /// no-allocation pre-check, so this being true does not by itself mean a clone-and-dispel ran.
        /// </summary>
        public static bool ShouldStripRareBuffs()
        {
            var dials = ReadDials();

            if (dials == null || !dials.Enabled || !dials.StripRareBuffs)
                return false;

            Report(PvpChokePoint.RB1, 0, 1);

            return true;
        }

        // ================= damage mods (L8): M1, M2 =================

        /// <summary>
        /// PURE. A damage-mod setting as it is actually applied: NaN, infinity or a negative value is treated as
        /// the identity 1.0 (the setting is not range-checked where it is read), anything else as is - including
        /// 0, which zeroes the hit.
        /// </summary>
        public static double SanitizeMod(double mod)
            => double.IsNaN(mod) || double.IsInfinity(mod) || mod < 0.0 ? 1.0 : mod;

        /// <summary>PURE. The <see cref="PvpDamageKind"/> of a weapon hit at M1: Melee and Missile map across, anything else is Other.</summary>
        public static PvpDamageKind WeaponDamageKind(CombatType combatType)
        {
            switch (combatType)
            {
                case CombatType.Melee:
                    return PvpDamageKind.Melee;
                case CombatType.Missile:
                    return PvpDamageKind.Missile;
                default:
                    return PvpDamageKind.Other;
            }
        }

        /// <summary>
        /// PURE. The <see cref="PvpDamageKind"/> of a health projectile at M2: WarMagic for a war magic spell, Other
        /// for everything else - void (void_pvp_modifier already covers it) and life projectiles are never WarMagic.
        /// </summary>
        public static PvpDamageKind ProjectileDamageKind(MagicSchool school)
            => school == MagicSchool.WarMagic ? PvpDamageKind.WarMagic : PvpDamageKind.Other;

        /// <summary>
        /// PURE. The multiplier the damage mods apply to one hit: the kind's own mod (pvp_melee_damage_mod,
        /// pvp_missile_damage_mod or pvp_war_magic_damage_mod; 1.0 for Other) times pvp_crit_damage_mod when
        /// <paramref name="isCritical"/>, each through <see cref="SanitizeMod"/>.
        /// </summary>
        public static double DamageModMultiplier(PvpRuleDials dials, PvpDamageKind kind, bool isCritical)
        {
            double kindMod;

            switch (kind)
            {
                case PvpDamageKind.Melee:
                    kindMod = dials.MeleeDamageMod;
                    break;
                case PvpDamageKind.Missile:
                    kindMod = dials.MissileDamageMod;
                    break;
                case PvpDamageKind.WarMagic:
                    kindMod = dials.WarMagicDamageMod;
                    break;
                default:
                    kindMod = 1.0;
                    break;
            }

            var multiplier = SanitizeMod(kindMod);

            if (isCritical)
                multiplier *= SanitizeMod(dials.CritDamageMod);

            return multiplier;
        }

        /// <summary>
        /// The damage mods for an already-classified interaction. Returns <paramref name="hit"/> unchanged for a
        /// non-PvP interaction, a null snapshot, the master switch off, a hit that is not positive, or a multiplier
        /// of exactly 1.0; otherwise returns the scaled hit and <see cref="Report"/>s it at <paramref name="point"/>.
        /// </summary>
        public static float ApplyDamageMods(PvpChokePoint point, PvpInteraction interaction, PvpRuleDials dials, float hit, PvpDamageKind kind, bool isCritical, PvpContextHit context = default)
        {
            if (!interaction.IsPvp || dials == null || !dials.Enabled)
                return hit;

            if (!(hit > 0))
                return hit;

            // effective-HP normalization (pvp_health_floor / pvp_health_ceiling) rides the same M1/M2 multiply,
            // so C1 and C2 cap the normalized hit. The context tuning (arena / battleground x category) is a third
            // factor in the same multiply: 1.0 for an inactive hit, so open-world PK and PvE are untouched.
            var multiplier = DamageModMultiplier(dials, kind, isCritical) * PvpContextTuning.DamageMultiplier(context, isCritical) * ResolveHealthNormalization(interaction.Defender, dials);

            if (multiplier == 1.0)
                return hit;

            var scaled = (float)(hit * multiplier);

            if (scaled == hit)
                return hit;

            Report(point, hit, scaled);

            return scaled;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for M1 (Player.DamageTarget, after AM1, before C1) and M2 (SpellProjectile.DamageTarget,
        /// before C2): classifies (<paramref name="source"/>, <paramref name="target"/>) first and reads the dials
        /// only for a PvP pair, then scales the hit by its kind's mod and, on a crit, by pvp_crit_damage_mod. Runs
        /// BEFORE the damage cap, so a raised mod can still be capped. Read per call, never cached.
        /// </summary>
        public static float ApplyDamageMods(PvpChokePoint point, WorldObject source, Creature target, float hit, PvpDamageKind kind, bool isCritical, PvpHitProfile profile)
        {
            var interaction = PvpClassifier.Classify(source, target);

            if (!interaction.IsPvp)
                return hit;

            var dials = ReadDials();

            // context tuning: resolved only for an Arena / Battleground pair with the master switch on (classify
            // first, read second); inactive otherwise
            return ApplyDamageMods(point, interaction, dials, hit, kind, isCritical, PvpContextTuning.Resolve(interaction, dials, profile));
        }

        // ================= defense mods (L8b): DF1, DF2 =================

        /// <summary>
        /// PURE. A defense skill after a defense-mod multiplier: exactly <paramref name="skill"/> at the identity 1.0,
        /// otherwise round(skill x mod) through <see cref="SanitizeMod"/>, saturating at int.MaxValue (the skill-check
        /// callers cast to int, so a larger value would wrap negative and make the defender trivially hittable).
        /// </summary>
        public static uint ScaleDefenseSkill(uint skill, double mod)
        {
            mod = SanitizeMod(mod);

            if (mod == 1.0)
                return skill;

            var scaled = Math.Round(skill * mod);

            return scaled >= int.MaxValue ? (uint)int.MaxValue : (uint)scaled;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for DF1 (DamageEvent.GetEvadeChance): classifies (<paramref name="source"/>,
        /// <paramref name="target"/>) first and reads the dials only for a PvP pair, then scales the defender's
        /// effective defense skill by pvp_melee_defense_mod (Melee) or pvp_missile_defense_mod (Missile). Any other
        /// combat type, a non-PvP pair, the master switch off or a mod of exactly 1.0 hands the skill back unchanged.
        /// The caller applies the result to the evade roll ONLY; it never replaces the stored skill.
        /// </summary>
        public static uint ApplyDefenseMod(WorldObject source, Creature target, CombatType combatType, uint defenseSkill)
        {
            if (combatType != CombatType.Melee && combatType != CombatType.Missile)
                return defenseSkill;

            var interaction = PvpClassifier.Classify(source, target);

            if (!interaction.IsPvp)
                return defenseSkill;

            var dials = ReadDials();

            if (dials == null || !dials.Enabled)
                return defenseSkill;

            var scaled = ScaleDefenseSkill(defenseSkill, combatType == CombatType.Melee ? dials.MeleeDefenseMod : dials.MissileDefenseMod);

            if (scaled == defenseSkill)
                return defenseSkill;

            Report(PvpChokePoint.DF1, defenseSkill, scaled);

            return scaled;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for DF2 (WorldObject.TryResistSpell): the magic counterpart of
        /// <see cref="ApplyDefenseMod(WorldObject, Creature, CombatType, uint)"/>, scaling the target's effective magic
        /// defense by pvp_magic_defense_mod for a PvP caster / target pair.
        /// </summary>
        public static uint ApplyMagicDefenseMod(WorldObject source, Creature target, uint magicDefense)
        {
            var interaction = PvpClassifier.Classify(source, target);

            if (!interaction.IsPvp)
                return magicDefense;

            var dials = ReadDials();

            if (dials == null || !dials.Enabled)
                return magicDefense;

            var scaled = ScaleDefenseSkill(magicDefense, dials.MagicDefenseMod);

            if (scaled == magicDefense)
                return magicDefense;

            Report(PvpChokePoint.DF2, magicDefense, scaled);

            return scaled;
        }

        // ================= magic absorb mod (L9): AB1 =================

        /// <summary>
        /// PURE. Scales the absorb REDUCTION fraction: 1 - (1 - <paramref name="absorbMod"/>) x mod, clamped to
        /// [0, 1], with the mod through <see cref="SanitizeMod"/>. A mod of exactly 1.0 returns
        /// <paramref name="absorbMod"/> bit for bit. 0 makes absorption do nothing (1.0); 2 absorbs twice the
        /// fraction, never past 100% (0.0).
        /// </summary>
        public static double ScaleAbsorb(double absorbMod, double mod)
        {
            mod = SanitizeMod(mod);

            if (mod == 1.0)
                return absorbMod;

            var scaled = 1.0 - (1.0 - absorbMod) * mod;

            if (scaled < 0.0)
                return 0.0;

            if (scaled > 1.0)
                return 1.0;

            return scaled;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for AB1 (SpellProjectile.CalculateDamage, just after the retail PvP 0.72 absorb block,
        /// so the 0.72 applies first): classifies (<paramref name="source"/>, <paramref name="target"/>) first and
        /// reads the dials only for a PvP pair that actually has something absorbing (<paramref name="absorbMod"/>
        /// under 1.0), then scales the reduction by pvp_magic_absorb_mod and, for an Arena / Battleground pair, then by
        /// that context's pvp_{arena|bg}_magic_absorb. Anything else hands
        /// <paramref name="absorbMod"/> back unchanged.
        /// </summary>
        public static float ApplyMagicAbsorbMod(WorldObject source, Creature target, float absorbMod)
        {
            var interaction = PvpClassifier.Classify(source, target);

            if (!interaction.IsPvp)
                return absorbMod;

            if (!(absorbMod < 1.0f))
                return absorbMod;

            var dials = ReadDials();

            if (dials == null || !dials.Enabled)
                return absorbMod;

            var scaled = (float)ScaleAbsorb(absorbMod, dials.MagicAbsorbMod);

            // per-context absorb key (pvp_arena_magic_absorb / pvp_bg_magic_absorb), AFTER the global mod: the two
            // compose. Read only for an Arena / Battleground pair; open-world PK reads no context key.
            var context = PvpContextTuning.ContextOf(interaction.Scope);

            if (context != null)
                scaled = (float)ScaleAbsorb(scaled, PvpContextTuning.MagicAbsorbMod(context.Value, PvpContextTunables.Read()));

            if (scaled == absorbMod)
                return absorbMod;

            Report(PvpChokePoint.AB1, absorbMod, scaled);

            return scaled;
        }

        // ================= healing mod (L10): HL1-HL5 =================

        /// <summary>PURE. TRUE for the Health vital - the only vital the healing mod scales (never Stamina or Mana).</summary>
        public static bool HealsHealth(PropertyAttribute2nd vital) => vital == PropertyAttribute2nd.Health;

        /// <summary>
        /// PURE. A heal of <paramref name="value"/> after a multiplier of <paramref name="multiplier"/>: exactly
        /// <paramref name="value"/> at the identity 1.0, otherwise floor(value x multiplier), so a scaled heal is a
        /// whole point and is never rounded UP past what the multiplier allows (the float call sites round their
        /// result afterwards, so an unfloored 61.5 would land as 62). 0 heals for 0.
        /// </summary>
        public static double ScaleHealing(double value, double multiplier)
        {
            if (multiplier == 1.0)
                return value;

            return Math.Floor(value * multiplier);
        }

        /// <summary>
        /// The multiplier every healing-mod site applies to a heal RECEIVED by <paramref name="healed"/>. ARENA ONLY
        /// (owner ruling 2026-09-27): the healed player must be bound to a LIVE arena match
        /// (<see cref="PvpPlayerRules.InLiveMatch"/>, checked FIRST), and the dials are read only then. This is
        /// deliberately NOT a PK-timer gate (the old, deleted PvpClassifier.IsEngaged also counted a running PK timer), which
        /// would scale a PK's heals for up to pk_timer seconds after a PvP hit while they fight monsters - an
        /// open-world PK's heals are never scaled. Gated on the master switch; pvp_healing_mod goes through
        /// <see cref="SanitizeMod"/>. THE ONE PLACE a further healing factor (for example an overtime ramp) should be
        /// multiplied in, so all healing sites pick it up together.
        ///
        /// The arena overtime healing factor (<see cref="OvertimeHealingFactor"/>) is multiplied in here. It comes
        /// from the healed player's own binding snapshot, never a setting, and it is an ARENA rule rather than a
        /// pvp_rules lever, so it applies with the pvp_rules_enabled master switch off too. Overtime implies
        /// <see cref="PvpPlayerRules.InLiveMatch"/>, so the arena-only gate above never hides it.
        /// </summary>
        public static double ResolveHealingMultiplier(Player healed)
        {
            if (healed == null || !PvpPlayerRules.InLiveMatch(healed.PvpBinding))
                return 1.0;

            var overtime = OvertimeHealingFactor(healed.PvpBinding);

            var dials = ReadDials();

            if (dials == null || !dials.Enabled)
                return overtime;

            return SanitizeMod(dials.HealingMod) * overtime;
        }

        // ================= arena overtime (Docs/Pvp/DESIGN.md "Overtime"): healing factor, H1/H2 refusal, OT1-OT4 ramp =================

        /// <summary>
        /// PURE. The healing factor arena overtime applies to a heal received by the holder of
        /// <paramref name="binding"/>: the binding's snapshotted OvertimeHealingMod through <see cref="SanitizeMod"/>
        /// while <see cref="PvpPlayerRules.InOvertime"/>, else exactly 1.0. Reads nothing but the binding.
        /// </summary>
        public static double OvertimeHealingFactor(PvpPlayerBinding binding)
            => PvpPlayerRules.InOvertime(binding) ? SanitizeMod(binding.OvertimeHealingMod) : 1.0;

        /// <summary>
        /// CHOKE-POINT ENTRY for the overtime half of H1 (Healer completion) and H2 (Player_Use.ApplyConsumable):
        /// <see cref="PvpConsumableBlock.Overtime"/> when <paramref name="healed"/> is in arena overtime with a
        /// healing factor of exactly 0, so a Health kit or Health potion that would heal for nothing is refused and
        /// KEPT instead of spent. Any other factor (including a reduced one) returns None: the heal goes ahead and
        /// is scaled at HL2/HL3. Reads only the binding; reports the refusal at <paramref name="point"/>.
        /// </summary>
        public static PvpConsumableBlock TryBlockOvertimeHeal(PvpChokePoint point, Player healed)
        {
            if (healed == null || OvertimeHealingFactor(healed.PvpBinding) != 0.0)
                return PvpConsumableBlock.None;

            Report(point, 1, 0);

            return PvpConsumableBlock.Overtime;
        }

        /// <summary>
        /// PURE. The overtime damage multiplier: 1 + ramp x floor(overtimeSeconds / 60), stepped per WHOLE minute of
        /// overtime. A ramp that is NaN, infinite, negative or 0 is off (exactly 1.0), a negative or non-finite
        /// elapsed time counts as 0 minutes, and a product that is not finite falls back to 1.0.
        /// </summary>
        public static double OvertimeRampMultiplier(double rampPerMinute, double overtimeSeconds)
        {
            if (double.IsNaN(rampPerMinute) || double.IsInfinity(rampPerMinute) || !(rampPerMinute > 0))
                return 1.0;

            if (double.IsNaN(overtimeSeconds) || double.IsInfinity(overtimeSeconds) || overtimeSeconds < 0)
                return 1.0;

            var multiplier = 1.0 + rampPerMinute * Math.Floor(overtimeSeconds / 60.0);

            return double.IsInfinity(multiplier) || double.IsNaN(multiplier) ? 1.0 : multiplier;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for OT1-OT4. OT1-OT3 sit directly AFTER C1-C3 (so the ramp multiplies the capped hit)
        /// and before the cloak proc; OT4 sits at the Drain's Health write, after the cloak proc, so the ramp never
        /// feeds the caster's gain. Arena-bound pairs only: the pair must classify as
        /// <see cref="PvpScope.Arena"/> and the defender's binding must be in overtime, so open-world PvP and PvE
        /// never ramp. With the snapshotted ramp at 0 it returns before reading the clock, so it reads nothing
        /// beyond the binding. Never reads a setting: the ramp is the binding's snapshot. Damage-over-time ticks
        /// do not route through here and are never ramped.
        /// </summary>
        public static float ApplyOvertimeRamp(PvpChokePoint point, WorldObject source, Creature target, float hit)
        {
            if (!(hit > 0))
                return hit;

            var interaction = PvpClassifier.Classify(source, target);

            // Arena OR Battleground: a battleground match classified as Arena before PvpScope.Battleground existed
            if (!interaction.IsMatchScope)
                return hit;

            var binding = interaction.Defender.PvpBinding;

            if (!PvpPlayerRules.InOvertime(binding) || !(binding.OvertimeRampPerMinute > 0))
                return hit;

            var multiplier = OvertimeRampMultiplier(binding.OvertimeRampPerMinute, (UtcNowSource() - binding.OvertimeSinceUtc.Value).TotalSeconds);

            if (multiplier == 1.0)
                return hit;

            var ramped = (float)(hit * multiplier);

            if (!(ramped > hit) || float.IsInfinity(ramped))
                return hit;

            Report(point, hit, ramped);

            return ramped;
        }

        /// <summary>OT4 (a Drain Health's integer source loss): as the float overload, floored to a whole point. Unchanged input comes back exactly.</summary>
        public static uint ApplyOvertimeRamp(PvpChokePoint point, WorldObject source, Creature target, uint hit)
        {
            var ramped = ApplyOvertimeRamp(point, source, target, (float)hit);

            if (!(ramped > hit))
                return hit;

            return ramped >= uint.MaxValue ? uint.MaxValue : (uint)Math.Floor(ramped);
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for HL1-HL5F - the one function every healing site routes through. A positive heal of
        /// <paramref name="value"/> received by <paramref name="healed"/> comes back scaled by
        /// <see cref="ResolveHealingMultiplier"/> and floored (<see cref="ScaleHealing"/>); a non-positive value, a
        /// null player or one not in a Live arena match (a PK-timer-only player included), the master switch off, or a
        /// multiplier of 1.0 comes back unchanged. Reported
        /// at <paramref name="point"/> only when the value changed. The typed overloads below all funnel here.
        /// </summary>
        public static double ApplyHealingMod(PvpChokePoint point, Player healed, double value)
        {
            if (!(value > 0))
                return value;

            var multiplier = ResolveHealingMultiplier(healed);

            if (multiplier == 1.0)
                return value;

            var scaled = ScaleHealing(value, multiplier);

            if (scaled == value)
                return value;

            Report(point, value, scaled);

            return scaled;
        }

        /// <summary>HL2 / HL4 (float heal amounts): <see cref="ApplyHealingMod(PvpChokePoint, Player, double)"/>, unchanged input returned exactly.</summary>
        public static float ApplyHealingMod(PvpChokePoint point, Player healed, float value)
        {
            var scaled = ApplyHealingMod(point, healed, (double)value);

            return scaled == value ? value : (float)scaled;
        }

        /// <summary>HL1 / HL3 (int boosts): <see cref="ApplyHealingMod(PvpChokePoint, Player, double)"/>, unchanged input returned exactly.</summary>
        public static int ApplyHealingMod(PvpChokePoint point, Player healed, int value)
        {
            var scaled = ApplyHealingMod(point, healed, (double)value);

            return scaled == value ? value : (int)scaled;
        }

        /// <summary>HL5 / HL5F (uint transfer gain): <see cref="ApplyHealingMod(PvpChokePoint, Player, double)"/>, unchanged input returned exactly.</summary>
        public static uint ApplyHealingMod(PvpChokePoint point, Player healed, uint value)
        {
            var scaled = ApplyHealingMod(point, healed, (double)value);

            return scaled == value ? value : (uint)scaled;
        }

        /// <summary>
        /// HL5 / HL5F: a Drain / Transfer's Health gain paid to ONE recipient (the caster, a fellow, or a summon),
        /// keyed on that recipient alone. A recipient that is not a Player (a summon, a monster caster) comes back
        /// unchanged and reads nothing; a Player goes through
        /// <see cref="ApplyHealingMod(PvpChokePoint, Player, uint)"/>, so a player outside a Live arena match reads
        /// nothing either.
        /// </summary>
        public static uint ApplyHealingModToRecipient(PvpChokePoint point, Creature recipient, uint value)
            => recipient is Player healed ? ApplyHealingMod(point, healed, value) : value;
    }

    /// <summary>What kind of hit <see cref="PvpRules.ApplyDamageMods(PvpChokePoint, WorldObject, Creature, float, PvpDamageKind, bool)"/> is scaling, which picks its per-kind mod.</summary>
    public enum PvpDamageKind
    {
        /// <summary>No per-kind mod (void or life projectile, or an unrecognised combat type); only the crit mod can apply.</summary>
        Other,
        Melee,
        Missile,
        WarMagic
    }

    /// <summary>
    /// Why <see cref="PvpRules.TryBlockConsumable"/> refused a consumable's completion. Airborne keeps the
    /// existing "while jumping" WeenieError; Interval is a distinct system-chat refusal, since telling a
    /// grounded player they can't act "while in the air" would be wrong.
    /// </summary>
    public enum PvpConsumableBlock
    {
        None,
        Airborne,
        Interval,
        /// <summary>Arena overtime with a healing factor of 0 (<see cref="PvpRules.TryBlockOvertimeHeal"/>): a Health kit or potion is refused and kept.</summary>
        Overtime
    }
}
