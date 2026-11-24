using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    public class Cloak
    {
        /// <summary>
        /// Opt out of the standarad ACE formula and proc at a custom rate
        /// This setting will affect cloaks with any proc, including -200
        /// </summary>
        private static bool UseCustomMath => PropertyManager.GetBool("use_cloak_proc_custom_scale", false).Item;

        /// <summary>
        /// The maximum frequency of cloak procs, in seconds
        /// </summary>
        private static double MinDelay
        {
            get
            {
                if (!UseCustomMath)
                {
                    return 5.0;
                }
                return PropertyManager.GetDouble("cloak_cooldown_seconds").Item;
            }
        }

        /// <summary>
        /// The base maximum percentage at which cloaks will proc
        /// </summary>
        private static float MaxProcBase
        {
            get
            {
                if (!UseCustomMath)
                {
                    return 0.25f;
                }
                return Convert.ToSingle(PropertyManager.GetDouble("cloak_max_proc_base").Item);
            }
        }

        /// <summary>
        /// The minimum proc chance of a cloak
        ///
        /// THE KEY IS <c>cloak_min_proc</c>, NOT <c>cloak_min_proc_base</c>. This read named the latter until
        /// 2026-09-29, and nothing was registered under that name (PropertyManager's DefaultDoubleProperties
        /// carries cloak_cooldown_seconds, cloak_max_proc_base, cloak_max_proc_damage_percentage and
        /// cloak_min_proc, and Source/config-defaults.tsv lists the same four). GetDouble falls back to the
        /// literal for an unregistered key, so the read silently returned 0 forever, while ModifyDouble
        /// refuses an unregistered key outright - so the lever could not be set in-game at all and the bug
        /// was invisible at the shipped default of 0.
        /// </summary>
        private static float MinProc
        {
            get
            {
                if (!UseCustomMath)
                {
                    return 0f;
                }
                return Convert.ToSingle(PropertyManager.GetDouble("cloak_min_proc", 0f).Item);
            }
        }

        /// <summary>
        /// The base maxiumum percentage at which cloaks with
        /// -200 will proc
        ///
        /// KNOWN DEFECT, DELIBERATELY NOT FIXED HERE (2026-09-29). Under custom scale this reads the SAME key
        /// as <see cref="MaxProcBase"/>, so enabling use_cloak_proc_custom_scale collapses the shipped
        /// 0.25-vs-0.15 distinction between a spell-proc cloak's cap and a damage-reduction cloak's. It is
        /// inert at shipped defaults (use_cloak_proc_custom_scale is false) but it is wrong.
        ///
        /// It was left alone because there is no coherent re-pointing available: all four registered cloak_*
        /// keys already key a distinct role - cloak_cooldown_seconds -> MinDelay, cloak_max_proc_base ->
        /// MaxProcBase, cloak_max_proc_damage_percentage -> the plateau damage percentage read in RollProc's
        /// custom-scale branch, cloak_min_proc -> MinProc - and NONE of them is the -200 cap. Fixing it
        /// coherently means registering a new key (cloak_max_proc_base_damage_reduction, default 0.15), which
        /// is a naming and scope decision rather than a bug fix. Do not "fix" this by pointing it at
        /// cloak_max_proc_damage_percentage; that key is already in use for the plateau and is a different
        /// quantity.
        /// </summary>
        private static float MaxProcBase200
        {
            get
            {
                if (!UseCustomMath)
                {
                    return 0.15f;
                }
                return Convert.ToSingle(PropertyManager.GetDouble("cloak_max_proc_base").Item);
            }
        }

        private const float TwoThirds = 2.0f / 3.0f;

        /// <summary>
        /// Rolls for a chance at procing a cloak spell
        /// If successful, casts the spell
        ///
        /// THE TWO-PHASE API FOR A CLOAK SPELL PROC, and deliberately the only way in. A proc is
        /// <see cref="RollSpellProc"/> (the chance roll, Cloaked in Power's floor, and the Critical Thorns window
        /// it opens) followed by <see cref="CompleteSpellProc"/> (the Taunt hook and the cast through
        /// HandleProcSpell). TryProcSpell runs the two back to back. Six sites proc a cloak: the Reflect bounce
        /// (Player.TryReflectProjectile) and the Shield Block (Player.OnClassAbilityAttackAvoided, which passes 0f
        /// because a block has no damage figure) split the phases around their Thorns reflect (roll, reflect,
        /// complete), as do Player.TakeDamage, SpellProjectile.DamageTarget and the Harm and Drain Health paths in
        /// WorldObject_Magic (roll ahead of Thorns, complete where the cast always was). Every one of them reaches
        /// HandleProcSpell only through CompleteSpellProc, so the floor, the window and the Taunt hook are applied
        /// once for every site and a SEVENTH site inherits them. CloakProcWiringSourceScanTests pins that as a
        /// source scan, including that each file completes exactly as many procs as it rolls.
        ///
        /// <paramref name="defender"/> is the cloak's WEARER at every call site (each one reads
        /// <c>defender.EquippedCloak</c>, or is the player in Player.TakeDamage), which is what makes reading
        /// the wearer's ability floor from here correct.
        /// </summary>
        public static bool TryProcSpell(Creature defender, WorldObject attacker, WorldObject cloak, float damage_percent)
        {
            if (!RollSpellProc(defender, cloak, damage_percent))
                return false;

            return CompleteSpellProc(defender, attacker, cloak);
        }

        /// <summary>
        /// PHASE ONE of a cloak spell proc: the roll. Split out of <see cref="TryProcSpell"/> (Vanguard pass
        /// 2026-11-06) so a call site whose Thorns reflect runs BEFORE the cast can learn the proc decision
        /// first: a successful roll opens Cloaked in Power's Critical Thorns window, and the reflect of the very
        /// hit that triggered the proc must see it open. Sites that have no earlier reflect call TryProcSpell,
        /// which runs both phases back to back. A site that calls this MUST call
        /// <see cref="CompleteSpellProc"/> on a true result (or deliberately skip the cast, as a killing blow
        /// does), and nothing else may reach HandleProcSpell.
        ///
        /// ACCEPTED EDGE: the cooldown stamp (cloak.UseTimestamp, set inside RollProc) and the Critical Thorns
        /// window are committed AT ROLL TIME. A wearer who dies between the roll and CompleteSpellProc (or a
        /// bad-body-part early return in Player.TakeDamage) loses that proc's cast but keeps the cooldown.
        /// </summary>
        public static bool RollSpellProc(Creature defender, WorldObject cloak, float damage_percent)
        {
            if (cloak == null) return false;

            // Cloaked in Power (Vanguard T2): raises the FLOOR of the roll below, nothing else. It cannot
            // bypass RollProc's cooldown or item-level gates, both of which return before the chance is
            // computed. 0 for every non-player and every player without the ability, which reproduces the
            // shipped cloak_min_proc default exactly.
            var abilityFloor = CloakedInPowerFloor(defender);

            if (!RollProc(cloak, damage_percent, abilityFloor))
                return false;

            // Cloaked in Power ranks 1-3: any successful cloak SPELL proc roll opens (or refreshes) the Critical
            // Thorns window. Not a separate roll, and the damage-reduction roll (CloakWeaveProc = 2, which calls
            // RollProc directly) never reaches here.
            if (defender is Player windowPlayer)
                windowPlayer.OpenCriticalThornsWindow();

            return true;
        }

        /// <summary>
        /// PHASE TWO of a cloak spell proc: the class-ability hook and the cast, after a successful
        /// <see cref="RollSpellProc"/>.
        /// </summary>
        public static bool CompleteSpellProc(Creature defender, WorldObject attacker, WorldObject cloak)
        {
            // Class abilities that ride along with a successful cloak proc ROLL (Taunt). Placed here, after
            // the roll and BEFORE the spell is constructed in HandleProcSpell, to match the aetheria
            // precedent at WorldObject.TryProcItem - "the roll succeeded" and "the spell landed" are
            // different events and the existing hook fires on the former, so this one does too.
            if (defender is Player defenderPlayer)
                defenderPlayer.OnClassAbilityItemProc(cloak);

            return HandleProcSpell(defender, attacker, cloak);
        }

        /// <summary>
        /// The Cloaked in Power floor for whoever is wearing this cloak, as a fraction, or 0 when the wearer
        /// is not a player. Separate and public so the wiring is one named expression rather than an inline
        /// type test, and so a test can assert the non-player case without a live Player.
        /// </summary>
        public static float CloakedInPowerFloor(Creature defender)
        {
            return defender is Player player ? (float)player.GetCloakedInPowerFloor() : 0f;
        }

        /// <summary>
        /// Rolls for a chance at procing a cloak spell
        /// </summary>
        /// <param name="damage_percent">The percent of MaxHealth inflicted by an enemy's hit</param>
        /// <param name="abilityFloor">
        /// An additional minimum chance contributed by a class ability (Cloaked in Power), as a fraction.
        /// DEFAULTS TO 0, which makes <c>Math.Max(MinProc, 0)</c> - and therefore the whole chance expression
        /// below - identical to the pre-ability behaviour for every caller that does not pass it, which is
        /// every damage-reduction (CloakWeaveProc = 2) call site. It is applied strictly AFTER the cooldown
        /// and item-level early returns, so no floor can bypass either gate, and it only ever enters through
        /// a Math.Max, so it can never LOWER a chance that is already above it.
        /// </param>
        /// <returns></returns>
        public static bool RollProc(WorldObject cloak, float damage_percent, float abilityFloor = 0f)
        {
            // TODO: find retail formula

            var currentTime = Time.GetUnixTime();

            if (currentTime - cloak.UseTimestamp < MinDelay)
                return false;

            var itemLevel = cloak.ItemLevel ?? 0;

            if (itemLevel < 1) return false;

            var maxProcBase = MaxProcBase;

            if (HasDamageProc(cloak))
            {
                maxProcBase = MaxProcBase200;
                damage_percent *= TwoThirds;
            }

            var maxProcRate = maxProcBase + (itemLevel - 1) * 0.0125f;

            if (UseCustomMath)
            {
                // The proc chance should only plateau for damage above a certain configured percentage
                var maxProcAtDamagePercent = Convert.ToSingle(PropertyManager.GetDouble("cloak_max_proc_damage_percentage", 30.0).Item);
                // Reduce the damage percent for the calculation if necessary to a fraction of the percentage based on the configuration
                damage_percent = maxProcRate * (damage_percent / maxProcAtDamagePercent);
            }

            // take the lowest of the chance between damage and proc rate, then override
            // with min proc if necessary - or with the class-ability floor when that is higher
            var chance = ProcChance(damage_percent, maxProcRate, MinProc, abilityFloor);

            var rng = ThreadSafeRandom.Next(0.0f, 1.0f);

            if (rng < chance)
            {
                cloak.UseTimestamp = currentTime;
                return true;
            }
            else
                return false;
        }

        /// <summary>
        /// The proc chance itself, factored out of <see cref="RollProc"/> on 2026-09-29 so the floor
        /// arithmetic is unit-testable without a live WorldObject and without an RNG draw. Byte-for-byte the
        /// expression RollProc always carried, with one extra term: the class-ability floor joins the existing
        /// min-proc through a second Math.Max.
        ///
        /// TWO PROPERTIES OF THE SHAPE MATTER AND ARE WORTH STATING, because both are what makes the ability
        /// safe rather than a rewrite of the cloak formula:
        ///  - the floor enters ONLY through a Math.Max, so it can never LOWER a chance already above it. A
        ///    big hit against a high-item-level cloak returns exactly what it returned before.
        ///  - <paramref name="minProc"/> and <paramref name="abilityFloor"/> are combined with each other
        ///    FIRST, so the higher of the two wins and an operator raising cloak_min_proc above the ability's
        ///    floor is not silently overridden by it.
        ///
        /// A FULLY AFFINITY-SCALED FLOOR CAN EXCEED <paramref name="maxProcRate"/>, and that is deliberate
        /// rather than an oversight: at the shipped defaults the floor tops out at 0.10 + the 0.20 affinity
        /// chance cap = 0.30, which is above an item-level-1 cloak's 0.25 cap. maxProcRate bounds what the
        /// HIT SIZE can buy, not what the floor is; the floor is the ability's whole point.
        /// </summary>
        public static float ProcChance(float damage_percent, float maxProcRate, float minProc, float abilityFloor)
        {
            return Math.Max(Math.Min(damage_percent, maxProcRate), Math.Max(minProc, abilityFloor));
        }

        /// <summary>
        /// Casts the cloak proc spell
        /// </summary>
        public static bool HandleProcSpell(Creature defender, WorldObject attacker, WorldObject cloak)
        {
            if (cloak.ProcSpell == null) return false;

            var spell = new Spell(cloak.ProcSpell.Value);

            if (spell.NotFound)
            {
                if (defender is Player player)
                {
                    if (spell._spellBase == null)
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"SpellId {cloak.ProcSpell} Invalid.", ChatMessageType.System));
                    else
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{spell.Name} spell not implemented, yet!", ChatMessageType.System));

                }
                return false;
            }

            var targetSelf = spell.Flags.HasFlag(SpellFlags.SelfTargeted);
            var untargeted = spell.NonComponentTargetType == ItemType.None;

            var target = attacker;
            if (untargeted)
                target = null;
            else if (targetSelf)
                target = defender;

            // cloak range?

            var msg = new GameMessageSystemChat($"The cloak of {defender.Name} weaves the magic of {spell.Name}!", ChatMessageType.Spellcasting);

            defender.EnqueueBroadcast(msg, WorldObject.LocalBroadcastRange, ChatMessageType.Spellcasting);

            defender.TryCastSpell(spell, target, cloak, cloak, true, true, false);

            return true;
        }

        /// <summary>
        /// Returns TRUE if object is cloak
        /// </summary>
        public static bool IsCloak(WorldObject wo)
        {
            return wo.ValidLocations == EquipMask.Cloak;
        }

        /// <summary>
        /// The amount of damage reduced by a cloak proced with PropertyInt.CloakWeaveProc=2
        /// </summary>
        public const int DamageReductionAmount = 200;

        /// <summary>
        /// The cloak proc's flat mitigation amount. <paramref name="defender"/> is the cloak's wearer, so PvP
        /// classification (source, defender) can gate the PvP rules lever pvp_cloak_damage_reduction (choke
        /// point CLK1, Docs/Pvp/DESIGN.md "PvP rules (levers)") - see <see cref="Pvp.Rules.PvpRules.ApplyCloakDamageReduction"/>.
        /// Outside classified PvP, the fork's old hardcoded behavior is reproduced exactly with no setting
        /// read at all: a Player source (self-cast Harm, or a player hitting a cloaked monster) still halves
        /// the base 200 to 100; any other source keeps the base 200 unchanged. Only a truly classified PvP
        /// pair reads pvp_cloak_damage_reduction.
        /// </summary>
        public static int GetDamageReductionAmount(WorldObject source, Creature defender)
        {
            var cloak = WornCloak(defender);

            // Proc Amplifier: scale the base BEFORE the PvP choke point. The pvp gate is explicit because, with the PvP
            // dials off, PvpRules passes the base through unchanged, so it would not strip the amplifier on its own.
            var amplified = CloakMantleMath.AmplifyReduction(DamageReductionBase(cloak), CloakMantleMath.AmplifierFactor(cloak, Pvp.Rules.PvpClassifier.IsPvp(source, defender)));

            return Pvp.Rules.PvpRules.ApplyCloakDamageReduction(source, defender, amplified);
        }

        /// <summary>
        /// The defender's equipped cloak, or null when there is no defender or it has no equipped-object list (a
        /// creature built outside the normal load path), so the base falls back to the retail 200 instead of throwing.
        /// </summary>
        private static WorldObject WornCloak(Creature defender)
        {
            return defender?.EquippedObjects == null ? null : defender.EquippedCloak;
        }

        /// <summary>
        /// The base flat mitigation of a damage-reduction cloak, before the PvP and player-source adjustments in
        /// <see cref="GetDamageReductionAmount"/>. A cloak stamped with PropertyInt.CloakDamageReductionAmount (the
        /// upgraded Knot cloak, ACE.Server.KnotLoot.UpgradedCloak) uses that value; a null cloak or an unstamped one
        /// returns <see cref="DamageReductionAmount"/>, so every retail cloak is unchanged.
        /// </summary>
        public static int DamageReductionBase(WorldObject cloak)
        {
            return cloak?.GetProperty(PropertyInt.CloakDamageReductionAmount) ?? DamageReductionAmount;
        }

        /// <summary>
        /// The StatModValue a cloak's own proc spell lands with. Returns PropertyFloat.CloakProcStatModValue only when
        /// <paramref name="weapon"/> is a cloak, <paramref name="spellId"/> is that cloak's ProcSpell, and the property
        /// is stamped (the upgraded Knot cloak's Cloaked in Skill); otherwise <paramref name="baseValue"/> unchanged,
        /// so a retail cloak, a non-cloak carrying the property, and any other spell are all untouched.
        /// </summary>
        public static float ProcStatModValue(WorldObject weapon, uint spellId, float baseValue, bool pvp = false, EnchantmentTypeFlags statModType = 0)
        {
            if (weapon == null || !IsCloak(weapon) || weapon.ProcSpell != spellId)
                return baseValue;

            var stamped = weapon.GetProperty(PropertyFloat.CloakProcStatModValue);

            // Proc Amplifier (PropertyFloat.CloakProcAmplifier): PvE only. The caller passes pvp = IsPvp(caster, target),
            // which is false for a self-cast, so the Cloaked in Skill self-buff stays amplified even when a PvP hit
            // triggered it, while a Shroud landing on another player is not. The refresh path recomputes from the spell value through
            // this method, so the factor is applied exactly once there too.
            var magnitude = stamped.HasValue ? (float)stamped.Value : baseValue;

            return AmplifyStatMod(magnitude, CloakMantleMath.AmplifierFactor(weapon, pvp), statModType);
        }

        /// <summary>
        /// Applies the Proc Amplifier factor (1 + CloakProcAmplifier) to a stat mod. An ADDITIVE mod (Cloaked in Skill)
        /// is v * factor. A MULTIPLICATIVE mod (EnchantmentTypeFlags.Multiplicative, e.g. Shroud of Darkness at 0.5, which
        /// scales its target's skill) is v ^ factor (owner ruling 2026-11-10): v * factor would turn a 0.5 debuff into
        /// 1.0 or more at +100%, a no-op or a buff, while the power keeps a debuff deeper (0.5 -> 0.25 -> 0.125) and
        /// applies the same way to a value above 1. A non-finite or non-positive v, or a factor at or below 1 (PvP,
        /// no amplifier), returns v unchanged.
        /// </summary>
        public static float AmplifyStatMod(float value, double factor, EnchantmentTypeFlags statModType)
        {
            if (!statModType.HasFlag(EnchantmentTypeFlags.Multiplicative))
                return value * (float)factor;

            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f || double.IsNaN(factor) || factor <= 1.0)
                return value;

            return (float)Math.Pow(value, factor);
        }

        /// <summary>
        /// The StatModValue a REFRESHED enchantment should carry. A refresh keys only on the caster guid, which is the
        /// wearer for every cloak proc, so without this a +40 Cloaked in Skill built by an upgraded cloak would be
        /// refreshed forever at +40 by a retail cloak (or a +20 one kept at +20 by an upgraded cloak).
        ///
        /// Narrow on purpose: only Cloaked in Skill (SpellId.CloakAllSkill), cast by a cloak whose ProcSpell it is, is
        /// recomputed - it is the only spell a cloak can stamp a magnitude for, and BuildEntry applies no other value
        /// transform to it (it is neither a DoT nor a Malediction target). Every other refresh, including a retail
        /// cloak's other procs, keeps <paramref name="existingValue"/>, so a value BuildEntry transformed (the DoT
        /// recompute, the Malediction boost) is never overwritten by the raw spell value.
        /// </summary>
        public static float RefreshedProcStatModValue(WorldObject weapon, uint spellId, float existingValue, float spellValue, bool pvp = false, EnchantmentTypeFlags statModType = 0)
        {
            if (spellId != (uint)SpellId.CloakAllSkill || weapon == null || !IsCloak(weapon) || weapon.ProcSpell != spellId)
                return existingValue;

            // same stat-mod type as the build path, so a refresh neither resets nor double-applies the amplified value
            return ProcStatModValue(weapon, spellId, spellValue, pvp, statModType);
        }

        /// <summary>
        /// Returns the reduced damage amount when a cloak procs
        /// with PropertyInt.CloakWeaveProc=2
        /// </summary>
        public static uint GetReducedAmount(WorldObject source, Creature defender, uint damage)
        {
            var damageReductionAmount = GetDamageReductionAmount(source, defender);

            if (damage > damageReductionAmount)
                return (uint)(damage - damageReductionAmount);
            else
                return 0;
        }

        public static int GetReducedAmount(WorldObject source, Creature defender, int damage)
        {
            var damageReductionAmount = GetDamageReductionAmount(source, defender);

            return Math.Max(0, damage - damageReductionAmount);
        }

        public static float GetReducedAmount(WorldObject source, Creature defender, float damage)
        {
            var damageReductionAmount = GetDamageReductionAmount(source, defender);

            return Math.Max(0, damage - damageReductionAmount);
        }

        /// <summary>
        /// Sends the message to attacker and defender when cloak is proced with PropertyInt.CloakWeaveProc=2
        /// </summary>
        public static void ShowMessage(Creature defender, WorldObject attacker, int origDamage, int reducedDamage)
        {
            var suffix = $"reduced the damage from {origDamage} down to {reducedDamage}!";

            if (defender is Player playerDefender)
                playerDefender.Session.Network.EnqueueSend(new GameMessageSystemChat($"Your cloak {suffix}", ChatMessageType.Magic));

            // send message to attacker?
            if (attacker is Player playerAttacker)
                playerAttacker.Session.Network.EnqueueSend(new GameMessageSystemChat($"The cloak of {defender.Name} {suffix}", ChatMessageType.Magic));
        }

        public static void ShowMessage(Creature defender, WorldObject attacker, float origDamage, float reducedDamage)
        {
            ShowMessage(defender, attacker, (int)Math.Round(origDamage), (int)Math.Round(reducedDamage));
        }

        /// <summary>
        /// Returns TRUE If cloak has a damage reduction proc
        /// Matches client logic
        /// </summary>
        public static bool HasDamageProc(WorldObject cloak)
        {
            return cloak?.CloakWeaveProc == 2;
        }

        /// <summary>
        /// Returns TRUE if cloak has a spell proc
        /// </summary>
        public static bool HasProcSpell(WorldObject cloak)
        {
            return cloak?.ProcSpell != null;
        }
    }
}
