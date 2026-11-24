using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.MonsterEffects;
using ACE.Server.Physics.Animation;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// The dat-dependent half of summon-duel: turns a prepared Creature and the analytic simulator's numbers into
    /// DuelAttackModes with the engine's own cadence. Needs the portal dat (motion tables, combat maneuver tables) and
    /// a creature that is outside the world but fully built; the pure model it feeds is SummonDuel.
    ///
    /// CADENCE, mode by mode, mirrors what the live monster code does:
    /// - Melee (Monster_Melee.MeleeAttack, GetCombatManeuver): the attack height is drawn uniformly from the heights
    ///   the stance's maneuver table has, the attack type follows the wielded weapon (Punch / Kick without one), a
    ///   maneuver is drawn uniformly from those listed, the animation length is MotionTable.GetAnimationLength at the
    ///   creature's GetAnimSpeed (dual wield 20 percent faster), each attack frame of the motion is a strike, and the
    ///   next attack opens after the animation plus Uniform(0, PowerupTime) (default 1.0).
    /// - Missile (Monster_Missile.LaunchMissile): aim motion + Reload + the Reload-to-Ready link, plus the fixed
    ///   MissileDelay; the one strike lands when the aim motion ends. Only the reload runs at the attack speed.
    /// - Magic (Monster_Magic.MagicAttack): the pre-cast motion at 2.0, the post-cast motion, the recovery, and the
    ///   AiUseMagicDelay gate measured from the end of the pre-cast.
    ///
    /// NOT MODELLED: the time to walk into range or turn, projectile and spell flight time, the AI tick granularity
    /// (an attack opens on the first tick at or after its gate), human-magic windup gestures (the plain cast motion is
    /// used). A dual wielder's off-hand weapon IS modelled: MeleeCycles reads each swing from the hand the engine's
    /// DualWieldAlternate flag names.
    /// </summary>
    public static class SummonDuelBuilder
    {
        /// <summary>Monster.MissileDelay (a public const on the monster partial), repeated so this class names it once.</summary>
        private const float MissileDelay = 1.0f;

        private const float PreCastSpeed = 2.0f;
        private const float PostCastSpeed = 1.0f;
        private const float PostCastSpeedRanged = 1.66f;

        // ---- Summoner casters (Docs/SummonerCasters/DESIGN.md) ----

        /// <summary>
        /// Scales a pet combatant by a Summoner focus: every mode's NormalDamage and CritDamage by <paramref name="dealtMultiplier"/>,
        /// and the combatant's IncomingMultiplier by <paramref name="takenMultiplier"/>.
        /// </summary>
        public static void ApplySummonerFocus(DuelCombatant pet, double dealtMultiplier, double takenMultiplier)
        {
            foreach (var mode in pet.Modes)
            {
                mode.NormalDamage *= dealtMultiplier;
                mode.CritDamage *= dealtMultiplier;
            }

            pet.IncomingMultiplier *= takenMultiplier;
        }

        /// <summary>
        /// Parses <c>--summoner-mods=&lt;mod&gt;=&lt;fraction&gt;,...</c>. A mod is a WeaponModId name (case-insensitive) that reaches a
        /// Summoner focus's pets: any route but Wielder (SummonerFocus.ReachesPets: PetsOnly or Shared), or Attunement, which
        /// feeds the elemental damage mod. The fraction is the roll fraction in (0, 1].
        /// </summary>
        public static List<(WeaponModId Id, double Fraction)> ParseSummonerMods(string text)
        {
            var result = new List<(WeaponModId, double)>();

            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', StringSplitOptions.TrimEntries);

                if (pair.Length != 2 || !Enum.TryParse<WeaponModId>(pair[0], true, out var id) || !Enum.IsDefined(typeof(WeaponModId), id)
                    || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) || double.IsNaN(fraction) || fraction <= 0.0 || fraction > 1.0)
                    throw new ArgumentException($"summon-duel: --summoner-mods entry '{part}' must be <mod>=<fraction in (0,1]>.");

                if (!WeaponModRegistry.TryGet(id, out var definition) || !(SummonerFocus.ReachesPets(definition) || id == WeaponModId.Attunement))
                    throw new ArgumentException($"summon-duel: --summoner-mods names '{pair[0]}', which a Summoner focus does not send to its pets (a PetsOnly or Shared mod, or Attunement).");

                if (result.Any(r => r.Item1 == id))
                    throw new ArgumentException($"summon-duel: --summoner-mods names '{pair[0]}' twice.");

                result.Add((id, fraction));
            }

            return result;
        }

        /// <summary>
        /// Parses <c>--summoner-imbue=&lt;ImbuedEffectType&gt;</c> (case-insensitive): one imbue from SummonerFocus.PetImbues, which
        /// BuildSummonerFocus stamps on the offline focus (stat 179) so the pets read it through the live hooks. Undef for none.
        /// </summary>
        public static ImbuedEffectType ParseSummonerImbue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ImbuedEffectType.Undef;

            if (!Enum.TryParse<ImbuedEffectType>(text.Trim(), true, out var imbue) || Array.IndexOf(SummonerFocus.PetImbues, imbue) < 0)
                throw new ArgumentException($"summon-duel: --summoner-imbue='{text}' must be one imbue a focus sends to its pets: {string.Join(", ", SummonerFocus.PetImbues)}.");

            return imbue;
        }

        private static uint offlineFocusWcid = 996900;
        private static uint offlineFocusGuid = 0x7E900000;

        /// <summary>
        /// An offline Summoner focus: a Caster carrying the SummonerFocus marker, the stored ElementalDamageMod
        /// <paramref name="elementalDamageMod"/>, and each mod written through the applicator
        /// (WeaponModTinkerSet.ApplySpecialAtFraction: the record, plus the native a Tier A row adds).
        /// </summary>
        public static Caster BuildSummonerFocus(double elementalDamageMod, IEnumerable<(WeaponModId Id, double Fraction)> mods, ImbuedEffectType imbue = ImbuedEffectType.Undef)
        {
            var weenie = new Weenie
            {
                WeenieClassId = offlineFocusWcid++,
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Caster } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.ElementalDamageMod, elementalDamageMod } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.SummonerFocus, true } },
            };

            var focus = new Caster(weenie, new ObjectGuid(offlineFocusGuid++));

            foreach (var (id, fraction) in mods)
                WeaponModTinkerSet.ApplySpecialAtFraction(focus, WeaponModRegistry.Get(id), fraction);

            // PropertyInt 179, the one slot combat reads (WorldObject.HasImbuedEffect); tinkering allows one imbue per item
            if (imbue != ImbuedEffectType.Undef)
                focus.ImbuedEffect = imbue;

            return focus;
        }

        // ---- target debuffs (harness-only, default off) ----

        /// <summary>
        /// HYPOTHETICAL (--target-spells): puts one player debuff on a monster before its defender profile is built, so the
        /// profile's own reads (GetBodyArmorMod for Imperil, GetVulnerabilityResistanceMod for a Vulnerability) see it. The
        /// entry is what EnchantmentManager.Add writes for a first cast in a category (BuildEntry, LayerId 1; the spell's
        /// StatModType / StatModKey / StatModVal); a monster already carrying an enchantment in that category is refused,
        /// because that would need Add's surpass/refresh stacking. Assumes the cast lands (no resist roll modelled).
        /// </summary>
        public static void ApplyTargetDebuff(Creature monster, int spellId, SpellCategory category, uint power, EnchantmentTypeFlags statModType, uint statModKey, float statModValue)
        {
            if (monster == null)
                throw new ArgumentNullException(nameof(monster));

            if (monster.EnchantmentManager.GetEnchantments(category).Count > 0)
                throw new InvalidOperationException($"summon-duel: {monster.Name} (wcid {monster.WeenieClassId}) already carries an enchantment in spell category {category}; --target-spells models a first cast only.");

            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = spellId,
                SpellCategory = category,
                PowerLevel = power,
                LayerId = 1,
                Duration = -1.0,
                CasterObjectId = monster.Guid.Full,
                StatModType = statModType,
                StatModKey = statModKey,
                StatModValue = statModValue,
            };

            monster.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, monster.BiotaDatabaseLock);
        }

        /// <summary><see cref="ApplyTargetDebuff(Creature, int, SpellCategory, uint, EnchantmentTypeFlags, uint, float)"/> from a loaded Spell.</summary>
        public static void ApplyTargetDebuff(Creature monster, Spell spell) =>
            ApplyTargetDebuff(monster, (int)spell.Id, spell.Category, spell.Power, spell.StatModType, spell.StatModKey, spell.StatModVal);

        /// <summary>
        /// The pet dealt and taken multipliers of a focus, from the same sum combat uses (SumCasterElementalDamageMod, so Attunement counts)
        /// and the live scales: the numbers ApplySummonerFocus takes. The offline focus has no wielder, so no wielder enchantments.
        /// </summary>
        public static (double Edm, double Dealt, double Taken) FocusMultipliers(WorldObject focus)
        {
            var edm = (double)WorldObject.SumCasterElementalDamageMod(focus, null);

            return (edm, SummonerFocusMath.DealtMultiplier(edm, SummonerFocus.PetDamageScale()),
                SummonerFocusMath.TakenMultiplier(edm, SummonerFocus.PetDefenseScale(), SummonerFocus.PetDefenseFloor()));
        }

        /// <summary>Makes every ownerless non-Echo CombatPet resolve <paramref name="focus"/> until the result is disposed, which resets the seam.</summary>
        public static IDisposable SimulateFocus(WorldObject focus) => new FocusSeam(focus);

        private sealed class FocusSeam : IDisposable
        {
            private readonly Func<WorldObject, WorldObject> previous;

            public FocusSeam(WorldObject focus)
            {
                previous = SummonerFocus.SimulatedPetFocusSource;
                SummonerFocus.SimulatedPetFocusSource = _ => focus;
            }

            public void Dispose() => SummonerFocus.SimulatedPetFocusSource = previous;
        }

        /// <summary>
        /// A copy of a spec with its crit chance replaced. The analytic mean is linear in the crit chance, so the
        /// copies at 0 and at 1 give the damage of a normal and of a critical landed hit.
        /// </summary>
        internal static AttackerSpec WithCrit(AttackerSpec s, float critChance)
        {
            return new AttackerSpec
            {
                Label = s.Label,
                CombatType = s.CombatType,
                DamageType = s.DamageType,
                AttackHeight = s.AttackHeight,
                AttackSkill = s.AttackSkill,
                BaseDamageMin = s.BaseDamageMin,
                BaseDamageMax = s.BaseDamageMax,
                CritChance = critChance,
                CritDamageMod = s.CritDamageMod,
                MagicCritDamageMod = s.MagicCritDamageMod,
                MagicSkillBonus = s.MagicSkillBonus,
                PreMitigationMod = s.PreMitigationMod,
                ArmorRendingMod = s.ArmorRendingMod,
                IgnoreShieldMod = s.IgnoreShieldMod,
                IgnoreMagicArmor = s.IgnoreMagicArmor,
                IgnoreMagicResist = s.IgnoreMagicResist,
                WeaponResistanceMod = s.WeaponResistanceMod,
                IgnoreAllArmor = s.IgnoreAllArmor,
                WithinShieldArc = s.WithinShieldArc,
                AttackerIsPlayer = s.AttackerIsPlayer,
            };
        }

        /// <summary>
        /// The per-strike numbers of one spec against one creature defender, split into a normal and a critical landed
        /// hit (MonsterCombatSimulator.Analytic at crit 0 and crit 1), with the spec's own clamped crit chance.
        /// </summary>
        public static DuelAttackMode AnalyticMode(string name, double mix, AttackerSpec spec, MonsterDefenderProfile defender, double damagingFraction = 1.0)
        {
            var normal = MonsterCombatSimulator.Analytic(WithCrit(spec, 0.0f), defender);
            var crit = MonsterCombatSimulator.Analytic(WithCrit(spec, 1.0f), defender);

            return new DuelAttackMode
            {
                Name = name,
                Mix = mix,
                HitChance = Math.Clamp(normal.HitChance * damagingFraction, 0.0, 1.0),
                CritChance = Math.Clamp(spec.CritChance, 0.0f, 1.0f),
                NormalDamage = normal.MeanDamagePerLandedHit,
                CritDamage = crit.MeanDamagePerLandedHit,
                IsMissile = spec.CombatType == CombatType.Missile,
            };
        }

        // ---- melee ----

        /// <summary>
        /// The melee cycles of a creature already in its melee stance: one per (offhand, height, maneuver) the engine can
        /// roll, weighted by the chance it rolls them. Returns an empty list when the combat table or motion table is
        /// missing, which the caller must treat as "no cadence" and not guess.
        /// </summary>
        public static List<DuelAttackCycle> MeleeCycles(Creature c)
        {
            var cycles = new List<DuelAttackCycle>();

            if (c.CombatTable == null)
                c.GetCombatTable();

            var table = c.CombatTable;

            if (table == null || c.MotionTableId == 0)
                return cycles;

            var stance = c.CurrentMotionState.Stance;

            if (!table.Stances.TryGetValue(stance, out var stanceManeuvers))
                return cycles;

            var motionTable = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.MotionTable>(c.MotionTableId);

            var stanceKey = (uint)stance << 16 | ((uint)MotionCommand.Ready & 0xFFFFFF);

            if (motionTable == null || !motionTable.Links.TryGetValue(stanceKey, out var motions) || motions == null)
                return cycles;

            // GetCombatManeuver: AttackHeight = Next(startHeight, 3), inclusive - every listed height is equally likely
            var startHeight = stanceManeuvers.Table.Count == 3 ? 1 : 2;
            var heights = Enumerable.Range(startHeight, 3 - startHeight + 1).Select(i => (AttackHeight)i).ToList();

            var dual = stance == MotionStance.DualWieldCombat;
            // dual wield alternates hands every swing, so half the swings are off-hand ones
            var hands = dual ? new[] { false, true } : new[] { false };

            var baseSpeed = c.GetAnimSpeed();
            var animSpeed = baseSpeed * (dual ? 1.2f : 1.0f);

            foreach (var offhand in hands)
            {
                foreach (var height in heights)
                {
                    if (!stanceManeuvers.Table.TryGetValue(height, out var attackTypes))
                        continue;

                    // GetCombatManeuver reads the weapon after toggling the hand: the off-hand swing reads the off-hand weapon
                    var weapon = offhand ? c.GetDualWieldWeapon() : c.GetEquippedMeleeWeapon(forceMainHand: true);

                    AttackType attackType;

                    if (weapon != null)
                        attackType = weapon.GetAttackType(stance, 0.5f, offhand);
                    else
                        attackType = height != AttackHeight.Low ? AttackType.Punch : AttackType.Kick;

                    List<MotionCommand> maneuvers = null;

                    if (!attackTypes.Table.TryGetValue(attackType, out maneuvers) || maneuvers.Count == 0)
                    {
                        if (attackType == AttackType.Punch && height == AttackHeight.Low || attackType == AttackType.Kick)
                        {
                            attackType = attackType == AttackType.Punch ? AttackType.Kick : AttackType.Punch;

                            if (!attackTypes.Table.TryGetValue(attackType, out maneuvers) || maneuvers.Count == 0)
                                continue;
                        }
                        else if (attackType.IsMultiStrike())
                        {
                            if (!attackTypes.Table.TryGetValue(attackType.ReduceMultiStrike(), out maneuvers) || maneuvers.Count == 0)
                                continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    foreach (var rolled in maneuvers)
                    {
                        var command = rolled;

                        if (!motions.ContainsKey((uint)command))
                        {
                            if (command.IsMultiStrike() && motions.ContainsKey((uint)command.ReduceMultiStrike()))
                                command = command.ReduceMultiStrike();
                            else if (command.IsSubsequent() && motions.ContainsKey((uint)command.ReduceSubsequent()))
                                command = command.ReduceSubsequent();
                            else
                                continue;
                        }

                        var length = MotionTable.GetAnimationLength(c.MotionTableId, stance, command, animSpeed);
                        var frames = MotionTable.GetAttackFrames(c.MotionTableId, stance, command).Select(f => (double)f.time).ToArray();

                        if (frames.Length == 0)
                            frames = new[] { 1.0 / 3.0 };

                        cycles.Add(new DuelAttackCycle
                        {
                            Weight = 1.0 / (hands.Length * heights.Count * maneuvers.Count),
                            AnimSeconds = length,
                            StrikeFractions = frames,
                        });
                    }
                }
            }

            return cycles;
        }

        /// <summary>The recovery ceiling after a melee or cast animation: the weenie's PowerupTime, 1.0 when unset.</summary>
        public static double RecoveryMax(Creature c) => c.PowerupTime ?? 1.0;

        // ---- missile ----

        /// <summary>The one missile cycle of a creature in its missile stance, or null when the dat has no aim motion for it.</summary>
        public static DuelAttackCycle MissileCycle(Creature c)
        {
            if (c.MotionTableId == 0)
                return null;

            var stance = c.CurrentMotionState.Stance;

            var launch = MotionTable.GetAnimationLength(c.MotionTableId, stance, MotionCommand.AimLevel);
            var reload = MotionTable.GetAnimationLength(c.MotionTableId, stance, MotionCommand.Reload, c.GetAnimSpeed());

            var linkFrom = reload > 0 ? MotionCommand.Reload : MotionCommand.AimLevel;
            var link = MotionTable.GetAnimationLength(c.MotionTableId, stance, linkFrom, MotionCommand.Ready);

            if (launch + reload + link <= 0.0f)
                return null;

            // Monster_Missile.LaunchMissile: the aim motion and the Reload-to-Ready link play at 1.0; only the reload runs at
            // GetAnimSpeed (already inside `reload`), so a Frenzy ramp shortens the reload alone. The arrow leaves when the aim ends.
            return new DuelAttackCycle
            {
                AnimSeconds = reload,
                LeadSeconds = launch,
                FixedSeconds = link,
                StrikeFractions = new[] { 0.0 },
            };
        }

        public static double MissileFixedDelay => MissileDelay;

        // ---- magic ----

        /// <summary>
        /// The cast cycle of a creature in its magic stance: pre-cast + post-cast, the strike after the pre-cast, and the
        /// extra wait the AiUseMagicDelay gate adds beyond the post-cast and the mean recovery. Null without a motion table.
        /// </summary>
        public static (DuelAttackCycle Cycle, double FixedDelay) MagicCycle(Creature c)
        {
            if (c.MotionTableId == 0)
                return (null, 0.0);

            var stance = c.CurrentMotionState.Stance;

            var pre = MotionTable.GetAnimationLength(c.MotionTableId, stance, MotionCommand.CastSpell, PreCastSpeed);
            var post = MotionTable.GetAnimationLength(c.MotionTableId, stance, MotionCommand.CastSpell, MotionCommand.Ready, c.IsRanged ? PostCastSpeedRanged : PostCastSpeed);

            if (pre + post <= 0.0f)
                return (null, 0.0);

            var magicDelay = c.GetProperty(PropertyFloat.AiUseMagicDelay) ?? 3.0;

            // the next attack opens at post-cast + U(0, powerup) after the pre-cast; the magic gate opens magicDelay after it
            var ordinaryGap = post + RecoveryMax(c) / 2.0;
            var extra = Math.Max(0.0, magicDelay - ordinaryGap);

            return (new DuelAttackCycle { AnimSeconds = pre + post, StrikeFractions = new[] { (double)pre / (pre + post) } }, extra);
        }

        /// <summary>
        /// The cast cycle of a CASTER PET (CombatPet_Caster through Monster_Magic.TryStartExplicitCast), in the stance the
        /// pet actually casts from: with no wand SetCombatMode(Magic) leaves its motion stance where it was (NonCombat), so
        /// CurrentMotionState.Stance is read as the engine reads it. Pre-cast = CastSpell at 2.0, post-cast = CastSpell to
        /// Ready at 1.0 (1.66 ranged), the strike when the pre-cast ends, and the magic-delay gate measured from that
        /// landing (CombatPet.CasterCastReady). The returned mode carries the cadence only; the caller adds the numbers.
        /// Null when the motion table yields no cast animation at all.
        /// </summary>
        public static (DuelAttackCycle Cycle, double Post, double MagicDelay, double Recovery)? PetCastCycle(Creature c)
        {
            if (c.MotionTableId == 0)
                return null;

            var stance = c.CurrentMotionState.Stance;

            var pre = MotionTable.GetAnimationLength(c.MotionTableId, stance, MotionCommand.CastSpell, PreCastSpeed);
            var post = MotionTable.GetAnimationLength(c.MotionTableId, stance, MotionCommand.CastSpell, MotionCommand.Ready, c.IsRanged ? PostCastSpeedRanged : PostCastSpeed);

            if (pre + post <= 0.0f)
                return null;

            // the unscaled gate (no effect stacks yet); the duel divides it by the cast-axis ramp itself
            var magicDelay = c.GetWeakenedBloodScaledMagicDelay();

            return (new DuelAttackCycle { AnimSeconds = pre + post, StrikeFractions = new[] { (double)pre / (pre + post) } }, post, magicDelay, RecoveryMax(c));
        }

        // ---- Frenzy ----

        /// <summary>
        /// The attack-speed ramp a pet's MonsterCombatEffects string authors ("ramp axis=attackspeed on=hit per=.. max=..
        /// window=.."), through the engine's own parser; null when there is none. PerStack, MaxStacks (clamped to
        /// monster_effect_ramp_stack_cap) and the speed cap are the values RampEffect and GetMonsterEffectSpeedMultiplier read.
        /// </summary>
        public static DuelRamp RampFromEffects(string monsterCombatEffects, int stackCap, double speedCap, out string note)
        {
            note = null;

            MonsterEffectParser.Parse(monsterCombatEffects, out var specs, out var errors);

            if (errors.Count > 0)
                note = "MonsterCombatEffects parse errors: " + string.Join(" | ", errors);

            foreach (var spec in specs)
            {
                if (spec.Kind != "ramp")
                    continue;

                // Mana Rush: castspeed accruing on=cast (RampEffect.OnCastComplete) scales a caster pet's gated cast cycle
                if (string.Equals(spec.GetString("axis"), "castspeed", StringComparison.OrdinalIgnoreCase))
                {
                    if (!spec.GetFlags("on", MonsterEffectTrigger.Hit).HasFlag(MonsterEffectTrigger.Cast))
                    {
                        note = (note == null ? "" : note + "; ") + $"ramp axis=castspeed on={spec.GetString("on")} does not accrue from a cast: ignored";
                        continue;
                    }

                    return new DuelRamp
                    {
                        PerStack = spec.GetDouble("per", 0.0),
                        MaxStacks = Math.Min(Math.Max(1, spec.GetInt("max", 1)), Math.Max(1, stackCap)),
                        WindowSeconds = spec.GetDouble("window", 0.0),
                        SpeedCap = speedCap,
                        CastAxis = true,
                    };
                }

                if (!string.Equals(spec.GetString("axis"), "attackspeed", StringComparison.OrdinalIgnoreCase))
                    continue;

                // RampEffect.OnOutgoingHit accrues only when on= carries Hit; on=spellhit / avoid never accrue from a landed
                // melee or missile hit, so such a record is no Frenzy for a duel (parsed the way the engine parses it)
                var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);

                if (!trigger.HasFlag(MonsterEffectTrigger.Hit))
                {
                    note = (note == null ? "" : note + "; ") + $"ramp on={spec.GetString("on")} does not accrue from a landed hit: ignored";
                    continue;
                }

                return new DuelRamp
                {
                    PerStack = spec.GetDouble("per", 0.0),
                    MaxStacks = Math.Min(Math.Max(1, spec.GetInt("max", 1)), Math.Max(1, stackCap)),
                    WindowSeconds = spec.GetDouble("window", 0.0),
                    SpeedCap = speedCap,
                };
            }

            return null;
        }
    }
}
