using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// GET /v1/accounts/me/characters/{guid}/details (Docs/Market/market-api-v1.yaml, CharacterDetails). Every string is
    /// written here; the client renders it verbatim. Values are player-side percentages; the tooltips name the calculation
    /// inputs, including the baseline monster's average skills (owner ruling, round 4).
    /// </summary>
    public sealed class CharacterDetails
    {
        public long CharacterGuid { get; set; }
        public int Level { get; set; }
        public bool BaselineAvailable { get; set; }
        public string BaselineNote { get; set; }
        public List<CharacterDetailsSection> Sections { get; set; } = new List<CharacterDetailsSection>();
    }

    public sealed class CharacterDetailsSection
    {
        public string Key { get; set; }
        public string Title { get; set; }

        /// <summary>"list", "table" or "tables".</summary>
        public string Kind { get; set; }

        /// <summary>Table sections only: text for the header cell above the row labels.</summary>
        public string Corner { get; set; }

        /// <summary>Table sections only: the DATA column headers (no header for the row-label column).</summary>
        public List<string> Columns { get; set; }

        /// <summary>List and table sections; always serialized, and empty on a tables section.</summary>
        public List<CharacterDetailsRow> Rows { get; set; } = new List<CharacterDetailsRow>();

        /// <summary>Tables sections only: each drawn like a table section under the one section title, in display order.</summary>
        public List<CharacterDetailsTable> Tables { get; set; }

        /// <summary>True: the section is drawn collapsed by default and the reader can expand it. Null (omitted) otherwise.</summary>
        public bool? Collapsed { get; set; }
    }

    /// <summary>One table inside a "tables" section: the same shape as a table section, minus the section title.</summary>
    public sealed class CharacterDetailsTable
    {
        public string Key { get; set; }

        /// <summary>Text for the header cell above the row labels.</summary>
        public string Corner { get; set; }

        public List<string> Columns { get; set; }
        public List<CharacterDetailsRow> Rows { get; set; } = new List<CharacterDetailsRow>();
    }

    public sealed class CharacterDetailsRow
    {
        public string Key { get; set; }
        public string Label { get; set; }

        /// <summary>List sections: the preformatted value.</summary>
        public string Value { get; set; }

        /// <summary>Table sections: one preformatted cell per data column; the label is the row header, never cells[0].</summary>
        public List<string> Cells { get; set; }

        public string Tooltip { get; set; }

        /// <summary>Table sections only: sub-rows with the same columns, shown when the parent row is expanded. No nesting.</summary>
        public List<CharacterDetailsRow> Children { get; set; }
    }

    /// <summary>
    /// Everything the projector needs, read off a live Player on the world thread (<see cref="CharacterDetailsReader"/>).
    /// Plain values, so the projector is testable without a Player (none can be constructed in the test harness).
    /// </summary>
    public sealed class CharacterDetailsInputs
    {
        public uint CharacterGuid { get; set; }
        public int Level { get; set; }

        /// <summary>The combat stance every value is read in (ProfileBuilder.DefenderCombatStance), never NonCombat.</summary>
        public CombatMode Stance { get; set; }

        /// <summary>The skill <see cref="WeaponAttackSkill"/> is read from (Player.GetCurrentWeaponSkill(mainHandOnly: true)); named in the tooltips.</summary>
        public Skill WeaponSkill { get; set; }

        /// <summary>Player.GetEffectiveAttackSkill in <see cref="Stance"/>, main hand, at a FULL accuracy bar. 0 in Magic.</summary>
        public uint WeaponAttackSkill { get; set; }

        /// <summary>
        /// Dual wielding two weapons only: the off-hand swing's effective attack skill (Player.GetEffectiveOffHandAttackSkill: the off
        /// weapon's skill capped at Dual Wield, times the better-of-two-weapons offense modifier) and the skill it came from. Null / None otherwise.
        /// </summary>
        public uint? OffWeaponAttackSkill { get; set; }
        public Skill OffWeaponSkill { get; set; }

        /// <summary>
        /// Missile stance only: GetEffectiveAttackSkill at accuracy bar 0%, 1%, ... 100% (101 entries), through the same
        /// GetAccuracyMod(weapon, bar) path as <see cref="WeaponAttackSkill"/>. Null otherwise.
        /// </summary>
        public uint[] MissileAttackSkillByBarPercent { get; set; }

        /// <summary>The trained magic schools (War, Void, Life, Creature) and their current skill.</summary>
        public IReadOnlyDictionary<Skill, uint> MagicSkills { get; set; } = new Dictionary<Skill, uint>();

        public uint MeleeDefense { get; set; }
        public uint MissileDefense { get; set; }
        public uint MagicDefense { get; set; }

        /// <summary>
        /// The held weapon's attacker-side crit chance before any target's crit resist: WorldObject.GetWeaponCriticalChancePreResist
        /// for a melee or missile weapon (or empty hands), GetWeaponMagicCritFrequencyPreResist for the held caster in Magic.
        /// </summary>
        public float HeldCritPreResist { get; set; }

        /// <summary>The same for the off-hand weapon when dual wielding; null otherwise.</summary>
        public float? OffCritPreResist { get; set; }

        /// <summary>
        /// The held weapon's crit damage multiplier before the crit damage rating: AttackerTerms.PhysicalCritDamageMod
        /// (DamageEvent: (1 + CriticalMultiplier) x (1 + Execution)) in a melee or missile stance, AttackerTerms.MagicCritDamageMod
        /// (SpellProjectile: CriticalMultiplier x (1 + Execution), the multiple of half the spell's maximum damage it adds) in Magic.
        /// </summary>
        public float HeldCritDamageMod { get; set; }

        /// <summary>The same for the off-hand weapon when dual wielding; null otherwise.</summary>
        public float? OffCritDamageMod { get; set; }

        /// <summary>The Killer Instinct share of the physical crit chance right now (stateful: held Openings).</summary>
        public float KillerInstinctCritBonus { get; set; }

        /// <summary>ProfileBuilder.ReadDefenderProfile in <see cref="Stance"/>.</summary>
        public DefenderProfile Profile { get; set; }

        /// <summary>
        /// Spell projectiles (SpellProjectile.CalculateDamage): the resistance term each damage type takes,
        /// Math.Max(0, GetResistanceMod(Creature.GetResistanceType(type), ...)) with no hollow and no rend - innate ResistX,
        /// protections or natural resistance, augmentations, vulnerabilities. Health is the life projectile's (HealthDrain).
        /// </summary>
        public IReadOnlyDictionary<DamageType, float> SpellResistanceMod { get; set; } = new Dictionary<DamageType, float>();

        /// <summary>SpellProjectile.AbsorbModFor in <see cref="Stance"/>: a bolt inside the frontal arc, and one outside it.</summary>
        public float SpellAbsorbFront { get; set; } = 1.0f;
        public float SpellAbsorbBack { get; set; } = 1.0f;

        /// <summary>Harm's resistance term (HandleCastSpell_Boost): GetResistanceMod(HealthDrain, ...), unclamped.</summary>
        public float HarmResistanceMod { get; set; } = 1.0f;

        /// <summary>Drain Health's resistance term (HandleCastSpell_Transfer): GetHealthDrainResistanceOnly.</summary>
        public float DrainResistanceMod { get; set; } = 1.0f;

        /// <summary>A DoT tick's resistance term (EnchantmentManager.ApplyDamageTick): GetResistanceMod(Nether / Undef, ...).</summary>
        public float DotNetherResistanceMod { get; set; } = 1.0f;
        public float DotUntypedResistanceMod { get; set; } = 1.0f;

        /// <summary>Creature.GetDotResistanceRating, which only a DoT tick reads.</summary>
        public int DotResistRating { get; set; }

        /// <summary>Player.GetFortitude for a PvE source: the fraction of a hit worn Defense aspects remove (every damage kind).</summary>
        public double Fortitude { get; set; }

        /// <summary>The damage rating, and the crit damage rating that a crit adds into the same pool (DamageEvent, additive).</summary>
        public int DamageRating { get; set; }
        public int CritDamageRating { get; set; }

        /// <summary>The crit rating, which adds 1% of crit chance per point (GetWeaponCriticalChancePreResist).</summary>
        public int CritRating { get; set; }

        /// <summary>Player.GetBurdenMod: below 1.0 when overburdened (melee and missile defense).</summary>
        public float BurdenMod { get; set; }
    }

    /// <summary>The pure projection of <see cref="CharacterDetailsInputs"/> against a Thread baseline into the response.</summary>
    public static class CharacterDetailsProjector
    {
        /// <summary>The response's baseline_note when a baseline exists. Owner text, verbatim.</summary>
        public const string BaselineTooltip = "Against an average Thread monster of your level; specialized monsters may be stronger.";

        /// <summary>Dual wield: the weapon modifiers (offense and melee defense) are each the better of the two weapons (WorldObject.GetWeaponOffenseModifier / GetWeaponMeleeDefenseModifier).</summary>
        public const string BetterOfTwo = "Weapon modifier: the better of your two weapons.";

        /// <summary>The last sentence of every baseline-derived tooltip.</summary>
        public const string SpecializedSuffix = " Specialized monsters may be stronger.";

        /// <summary>
        /// The accuracy bar every missile hit chance assumes: a FULL bar, the combat simulator's default
        /// (PlayerAttackerSpecs.FromPlayer bar = 1.0). One constant so the owner can move it.
        /// </summary>
        public const float MissileAccuracyBar = 1.0f;

        /// <summary>The hit chance the accuracy-bar row searches for (owner ruling: 99%).</summary>
        public const double AccuracyBarTargetChance = 0.99;

        public static string BaselineUnavailableNote(int level)
            => $"No Thread monster baseline exists for level {level.ToString(CultureInfo.InvariantCulture)} right now, so hit, evade and resist chances are not shown.";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly (BodyPart Part, string Label)[] BodyPartRows =
        {
            (BodyPart.Head, "Head"),
            (BodyPart.Chest, "Chest"),
            (BodyPart.Abdomen, "Abdomen"),
            (BodyPart.UpperArm, "U Arm"),
            (BodyPart.LowerArm, "L Arm"),
            (BodyPart.Hand, "Hands"),
            (BodyPart.UpperLeg, "U Leg"),
            (BodyPart.LowerLeg, "L Leg"),
            (BodyPart.Foot, "Feet"),
        };

        private static readonly (DamageType Type, string Label)[] DamageColumns =
        {
            (DamageType.Slash, "Slash"), (DamageType.Pierce, "Pierce"), (DamageType.Bludgeon, "Bludgeon"), (DamageType.Fire, "Fire"),
            (DamageType.Cold, "Cold"), (DamageType.Acid, "Acid"), (DamageType.Electric, "Lightning"), (DamageType.Nether, "Nether"),
        };

        // the collapsed Front/Back cells show the worst of these: Nether is left out (it stays in the expanded children)
        private static readonly (DamageType Type, string Label)[] SummaryColumns = DamageColumns.Where(c => c.Type != DamageType.Nether).ToArray();

        private static readonly (Skill Skill, string Key, string Label)[] SpellSchools =
        {
            (Skill.WarMagic, "spell_war", "War Magic spells land"),
            (Skill.VoidMagic, "spell_void", "Void Magic spells land"),
            (Skill.LifeMagic, "spell_life", "Life Magic spells land"),
            (Skill.CreatureEnchantment, "spell_creature", "Creature Enchantment spells land"),
        };

        public static CharacterDetails Build(CharacterDetailsInputs input, ThreadBaselineCreature baseline)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            var details = new CharacterDetails
            {
                CharacterGuid = input.CharacterGuid,
                Level = input.Level,
                BaselineAvailable = baseline != null,
                BaselineNote = baseline != null ? BaselineTooltip : BaselineUnavailableNote(input.Level),
            };

            details.Sections.Add(Offense(input, baseline));
            details.Sections.Add(Defense(input, baseline));
            details.Sections.Add(DamageTaken(input));

            details.Sections.RemoveAll(s => s.Tables != null ? s.Tables.All(t => t.Rows.Count == 0) : s.Rows.Count == 0);

            return details;
        }

        /// <summary>"HeavyWeapons" -> "Heavy Weapons".</summary>
        public static string SkillName(Skill skill)
        {
            var text = skill.ToString();
            var sb = new System.Text.StringBuilder();

            for (var i = 0; i < text.Length; i++)
            {
                if (i > 0 && char.IsUpper(text[i]) && !char.IsUpper(text[i - 1]))
                    sb.Append(' ');

                sb.Append(text[i]);
            }

            return sb.ToString();
        }

        private static string Num(uint value) => value.ToString(Inv);

        private static string Lvl(ThreadBaselineCreature baseline) => baseline.Level.ToString(Inv);

        // ---- offense ----

        private static CharacterDetailsSection Offense(CharacterDetailsInputs input, ThreadBaselineCreature baseline)
        {
            var section = List("offense", "Offense");

            if (baseline != null)
            {
                var level = Lvl(baseline);

                if (input.Stance == CombatMode.Melee && baseline.MeleeDefense > 0)
                {
                    var main = Pct(HitChance.Calculate(input.WeaponAttackSkill, baseline.MeleeDefense));

                    if (input.OffWeaponAttackSkill.HasValue)
                    {
                        // two weapons: swings alternate, each at its own skill; the off hand is capped at Dual Wield
                        var off = Pct(HitChance.Calculate(input.OffWeaponAttackSkill.Value, baseline.MeleeDefense));
                        var capped = input.OffWeaponSkill == Skill.DualWield ? " (capped by Dual Wield)" : "";

                        section.Rows.Add(Baseline("hit_melee", "Melee hit chance", off == main ? main : $"{main} / {off}",
                            $"Main hand {Num(input.WeaponAttackSkill)} effective {SkillName(input.WeaponSkill)}, off hand {Num(input.OffWeaponAttackSkill.Value)} effective {SkillName(input.OffWeaponSkill)}{capped} vs {Num(baseline.MeleeDefense)} average Melee Defense of level {level} Thread monsters. {BetterOfTwo}"));
                    }
                    else
                    {
                        section.Rows.Add(Baseline("hit_melee", "Melee hit chance", main,
                            $"{Num(input.WeaponAttackSkill)} effective {SkillName(input.WeaponSkill)} (buffed skill with weapon and stance modifiers) vs {Num(baseline.MeleeDefense)} average Melee Defense of level {level} Thread monsters."));
                    }
                }

                if (input.Stance == CombatMode.Missile && baseline.MissileDefense > 0)
                {
                    section.Rows.Add(Baseline("hit_missile", "Missile hit chance", Pct(HitChance.Calculate(input.WeaponAttackSkill, baseline.MissileDefense)),
                        $"{Num(input.WeaponAttackSkill)} effective {SkillName(input.WeaponSkill)} at a full accuracy bar (buffed skill with weapon, stance and accuracy modifiers) vs {Num(baseline.MissileDefense)} average Missile Defense of level {level} Thread monsters."));

                    if (input.MissileAttackSkillByBarPercent != null && input.MissileAttackSkillByBarPercent.Length == 101)
                        section.Rows.Add(Baseline("accuracy_bar_99", "Accuracy bar for 99% hit",
                            AccuracyBarFor99(input.MissileAttackSkillByBarPercent, baseline.MissileDefense),
                            $"The lowest accuracy bar at which your effective {SkillName(input.WeaponSkill)} reaches a 99% hit chance vs {Num(baseline.MissileDefense)} average Missile Defense of level {level} Thread monsters. A fuller bar raises the skill."));
                }

                // only a held caster casts: the schools it can use, each at least trained
                if (input.Stance == CombatMode.Magic && baseline.MagicDefense > 0)
                {
                    foreach (var (skill, key, label) in SpellSchools)
                    {
                        if (input.MagicSkills != null && input.MagicSkills.TryGetValue(skill, out var current))
                            section.Rows.Add(Baseline(key, label, Pct(HitChance.Calculate(current, baseline.MagicDefense)),
                                $"{Num(current)} effective {SkillName(skill)} (buffed skill) vs {Num(baseline.MagicDefense)} average Magic Defense of level {level} Thread monsters."));
                    }
                }
            }

            section.Rows.Add(CritChanceRow(input, baseline));
            section.Rows.Add(CritDamageRow(input));

            return section;
        }

        private static CharacterDetailsRow CritChanceRow(CharacterDetailsInputs input, ThreadBaselineCreature baseline)
        {
            var magic = input.Stance == CombatMode.Magic;

            float Chance(float pre)
                => Math.Clamp(baseline != null ? WorldObject.ApplyCritResist(pre, baseline.CritResistRating) : pre, 0.0f, 1.0f);

            var main = Pct(Chance(input.HeldCritPreResist));
            var value = main;

            if (input.OffCritPreResist.HasValue)
            {
                var off = Pct(Chance(input.OffCritPreResist.Value));
                value = off == main ? main : $"{main} / {off}";
            }

            var ki = magic ? 0.0f : input.KillerInstinctCritBonus;
            var ratingShare = input.CritRating * 0.01f;
            var weaponBase = Math.Max(0.0f, input.HeldCritPreResist - ratingShare - ki);

            var text = $"{(magic ? "Caster" : "Weapon")} base {PctPoints(weaponBase)} + crit rating {input.CritRating.ToString(Inv)} ({PctPoints(ratingShare)})";

            if (ki > 0)
                text += $" + Killer Instinct Openings {PctPoints(ki)}, which changes as you fight";

            var dual = input.OffCritPreResist.HasValue ? " Main hand shown first, then the off hand; the base listed is the main hand's." : "";

            if (baseline == null)
                return Row("crit_chance", "Crit chance", value, text + "." + dual);

            var resist = baseline.CritResistRating;

            return Baseline("crit_chance", "Crit chance", value,
                text + $", times x{Mult(Creature.GetNegativeRatingMod(resist))} for the {resist.ToString(Inv)} average crit resist rating of level {Lvl(baseline)} Thread monsters." + dual);
        }

        /// <summary>
        /// A crit hit as a multiple of a maximum-damage non-crit hit. Physical (DamageEvent, the crit branch): the base is
        /// CriticalDamageMod = (1 + CriticalMultiplier) x (1 + Execution), and the crit damage rating is ADDED into the damage
        /// rating pool (Creature.AdditiveCombine), never multiplied on top. Magic (SpellProjectile.CalculateDamage): a crit adds
        /// half the spell's maximum damage times CriticalMultiplier x (1 + Execution), so the base is 1 + 0.5 x that; DamageTarget
        /// then adds the crit damage rating into the damage rating pool the same way.
        /// </summary>
        public static float CritDamageMultiplier(CombatMode stance, float critDamageMod, int damageRating, int critDamageRating)
        {
            var baseMult = stance == CombatMode.Magic ? 1.0f + 0.5f * critDamageMod : critDamageMod;
            var damage = Creature.GetPositiveRatingMod(damageRating);
            var combined = Creature.AdditiveCombine(damage, Creature.GetPositiveRatingMod(critDamageRating));

            return baseMult * combined / damage;
        }

        private static CharacterDetailsRow CritDamageRow(CharacterDetailsInputs input)
        {
            var magic = input.Stance == CombatMode.Magic;

            string Mul(float mod) => "x" + Mult(CritDamageMultiplier(input.Stance, mod, input.DamageRating, input.CritDamageRating));

            var main = Mul(input.HeldCritDamageMod);
            var value = main;

            if (input.OffCritDamageMod.HasValue)
            {
                var off = Mul(input.OffCritDamageMod.Value);
                value = off == main ? main : $"{main} / {off}";
            }

            var ratingStep = Creature.AdditiveCombine(Creature.GetPositiveRatingMod(input.DamageRating), Creature.GetPositiveRatingMod(input.CritDamageRating))
                / Creature.GetPositiveRatingMod(input.DamageRating);

            var text = magic
                ? $"A crit adds half the spell's maximum damage times your caster's crit multiplier x{Mult(input.HeldCritDamageMod)}, so x{Mult(1.0f + 0.5f * input.HeldCritDamageMod)} of a maximum-damage cast."
                : $"A crit hits for the weapon's maximum damage times x{Mult(input.HeldCritDamageMod)} (1 + its crit multiplier, times Execution if any).";

            text += $" Crit damage rating {input.CritDamageRating.ToString(Inv)} adds to your damage rating {input.DamageRating.ToString(Inv)}, a further x{Mult(ratingStep)}."
                + " Shown against a maximum-damage hit that is not a crit; Killer Instinct's crit damage is not counted.";

            if (input.OffCritDamageMod.HasValue)
                text += " Main hand shown first, then the off hand.";

            return Row("crit_damage", "Crit damage", value, text);
        }

        /// <summary>
        /// The accuracy-bar row: the lowest bar percent whose missile hit chance against <paramref name="defense"/> is at
        /// least <see cref="AccuracyBarTargetChance"/>, searched over the engine's own attack skill at each percent
        /// (<paramref name="skillByBarPercent"/>, 101 entries) - nothing here re-derives the accuracy formula. "Any bar" when
        /// 0% already reaches it; "Not reachable - X% at full bar" when even 100% does not.
        /// </summary>
        public static string AccuracyBarFor99(uint[] skillByBarPercent, uint defense)
        {
            if (skillByBarPercent == null || skillByBarPercent.Length != 101)
                throw new ArgumentException("101 entries, bar 0% to 100%.", nameof(skillByBarPercent));

            for (var percent = 0; percent <= 100; percent++)
            {
                if (HitChance.Calculate(skillByBarPercent[percent], defense) >= AccuracyBarTargetChance)
                    return percent == 0 ? "Any bar" : $"{percent.ToString(Inv)}%";
            }

            return $"Not reachable - {Pct(HitChance.Calculate(skillByBarPercent[100], defense))} at full bar";
        }

        // ---- defense ----

        private static CharacterDetailsSection Defense(CharacterDetailsInputs input, ThreadBaselineCreature baseline)
        {
            var section = List("defense", "Defense");

            if (baseline != null)
            {
                var level = Lvl(baseline);
                var burden = input.BurdenMod < 1.0f ? $" Overburdened: melee and missile defense x{Mult(input.BurdenMod)}." : "";

                // The monster's attack lands with HitChance(attack, defense); the evade is the rest. The same
                // closed form as the spell resist roll (WorldObject.MagicDefenseCheck: resisted = 1 - chance).
                if (baseline.MeleeAttack > 0)
                    section.Rows.Add(Baseline("evade_melee", "Melee evade chance", Pct(1.0f - HitChance.Calculate(baseline.MeleeAttack, input.MeleeDefense)),
                        $"{Num(input.MeleeDefense)} effective Melee Defense (buffed skill with weapon and stance modifiers) vs {Num(baseline.MeleeAttack)} average melee attack of level {level} Thread monsters.{burden}{(input.OffWeaponAttackSkill.HasValue ? " " + BetterOfTwo : "")}"));

                if (baseline.MissileAttack > 0)
                    section.Rows.Add(Baseline("evade_missile", "Missile evade chance", Pct(1.0f - HitChance.Calculate(baseline.MissileAttack, input.MissileDefense)),
                        $"{Num(input.MissileDefense)} effective Missile Defense (buffed skill with weapon and stance modifiers) vs {Num(baseline.MissileAttack)} average missile attack of level {level} Thread monsters.{burden}"));

                if (baseline.MagicAttack > 0)
                    section.Rows.Add(Baseline("resist_spells", "Spell resist chance", Pct(1.0f - HitChance.Calculate(baseline.MagicAttack, input.MagicDefense)),
                        $"{Num(input.MagicDefense)} effective Magic Defense (buffed skill with weapon and stance modifiers) vs {Num(baseline.MagicAttack)} average magic attack of level {level} Thread monsters."));
            }

            return section;
        }

        // ---- damage taken (tables) ----

        /// <summary>
        /// A neutral PvE attacker: no rending, no hollow, no imbue, no ignore-shield - what the defender's own gear does.
        /// <paramref name="withinShieldArc"/> is the facing, passed explicitly: the frontal arc, or behind.
        /// </summary>
        public static AttackerSpec NeutralPhysicalSpec(DamageType type, bool withinShieldArc, CombatType combatType = CombatType.Melee)
            => new AttackerSpec { DamageType = type, CombatType = combatType, AttackerIsPlayer = false, WithinShieldArc = withinShieldArc };

        /// <summary>
        /// One body part's physical armor, shield and resistance layers for one facing: DamageEvent's ArmorMod x ShieldMod x
        /// ResistanceMod (the final multiply), through MitigationMath. ShieldMod carries the facing: in front, the shield
        /// or Defensive Weapon (Creature.GetShieldMod's frontal cone); behind, the Mantle rear guard (Player.GetCloakRearGuardMod).
        /// </summary>
        public static float PhysicalTaken(DefenderProfile profile, BodyPart part, DamageType type, bool front)
        {
            var spec = NeutralPhysicalSpec(type, front);
            return MitigationMath.ArmorMod(profile, spec, part) * MitigationMath.ShieldMod(profile, spec) * MitigationMath.ResistanceMod(profile, spec);
        }

        /// <summary>
        /// <see cref="PhysicalTaken"/> with the rating and gear layers a monster's hit also meets: the damage resist rating with
        /// its specialized-defense bonus and Battle Hardened (MitigationMath.DamageResistRatingMod, the melee combat type), then
        /// Jewelry Aspect Fortitude (Player.ApplyGearFortitude, which every damage site's pre-write dispatch runs).
        /// </summary>
        public static float PhysicalTakenWithLayers(DefenderProfile profile, BodyPart part, DamageType type, bool front, double fortitude)
            => PhysicalTaken(profile, part, type, front)
               * MitigationMath.DamageResistRatingMod(profile, NeutralPhysicalSpec(type, front))
               * (float)JewelryAspectMath.FortitudeMultiplier(fortitude);

        /// <summary>The one "Damage taken" section: the physical table (body parts) and the magic table (spell kinds) under one title.</summary>
        private static CharacterDetailsSection DamageTaken(CharacterDetailsInputs input)
            => new CharacterDetailsSection
            {
                Key = "damage_taken",
                Title = "Damage taken",
                Kind = "tables",
                Collapsed = true,
                Tables = new List<CharacterDetailsTable> { PhysicalTable(input), MagicTable(input) },
            };

        private static string ArmorTypeKey(DamageType type)
            => type == DamageType.Electric ? "lightning" : type.ToString().ToLowerInvariant();

        /// <summary>A value, or "lo-hi" when the types differ.</summary>
        private static string Spread(IEnumerable<float> values)
        {
            var list = values.ToList();
            var lo = list.Min();
            var hi = list.Max();

            return Mult(lo) == Mult(hi) ? "x" + Mult(lo) : $"x{Mult(lo)}-{Mult(hi)}";
        }

        private static string PhysicalFacingTooltip(CharacterDetailsInputs input, bool front)
        {
            var profile = input.Profile;

            var facing = Spread(DamageColumns.Select(c => MitigationMath.ShieldMod(profile, NeutralPhysicalSpec(c.Type, front))));
            var melee = MitigationMath.DamageResistRatingMod(profile, NeutralPhysicalSpec(DamageType.Slash, front, CombatType.Melee));
            var missile = MitigationMath.DamageResistRatingMod(profile, NeutralPhysicalSpec(DamageType.Slash, front, CombatType.Missile));
            var rating = profile.DamageResistRatingBase != null && profile.DamageResistRatingBase.TryGetValue(CombatType.Melee, out var r) ? r : 1.0f;
            var fortitude = (float)JewelryAspectMath.FortitudeMultiplier(input.Fortitude);

            var text = "Each body part shows its worst damage type other than Nether; expand for all eight. Includes "
                + (front
                    ? $"your shield or Defensive Weapon ({facing}, front only)"
                    : $"a Mantle cloak's rear guard ({facing}; a shield does not cover the back)")
                + $", damage resist rating with its specialized-defense bonus (x{Mult(rating)}), Battle Hardened (x{Mult(profile.BattleHardenedMultiplier)}) and Jewelry Aspect Fortitude (x{Mult(fortitude)}), against a monster's melee hit.";

            if (Mult(melee) != Mult(missile))
                text += $" Against a monster's missile the damage resist rating layers are x{Mult(missile)} instead of x{Mult(melee)}.";

            return text;
        }

        private static CharacterDetailsTable PhysicalTable(CharacterDetailsInputs input)
        {
            var table = new CharacterDetailsTable
            {
                Key = "physical",
                Corner = "Physical",
                Columns = BodyPartRows.Select(p => p.Label).ToList(),
            };

            if (input.Profile == null)
                return table;

            CharacterDetailsRow Facing(bool front)
            {
                var prefix = front ? "armor_front" : "armor_back";

                return new CharacterDetailsRow
                {
                    Key = prefix,
                    Label = front ? "Front" : "Back",
                    Cells = BodyPartRows.Select(p => Pct(SummaryColumns.Max(c => PhysicalTakenWithLayers(input.Profile, p.Part, c.Type, front, input.Fortitude)))).ToList(),
                    Tooltip = PhysicalFacingTooltip(input, front),
                    Children = DamageColumns.Select(c => new CharacterDetailsRow
                    {
                        Key = $"{prefix}_{ArmorTypeKey(c.Type)}",
                        Label = c.Label,
                        Cells = BodyPartRows.Select(p => Pct(PhysicalTakenWithLayers(input.Profile, p.Part, c.Type, front, input.Fortitude))).ToList(),
                        Tooltip = $"{c.Label} resistance from protections, natural resistance and augmentations: x{Mult(MitigationMath.ResistanceMod(input.Profile, NeutralPhysicalSpec(c.Type, front)))}. Armor varies per body part.",
                    }).ToList(),
                };
            }

            table.Rows.Add(Facing(front: true));
            table.Rows.Add(Facing(front: false));

            return table;
        }

        // ---- spell damage ----

        // column headers only: the short forms; the physical table's child rows keep the full type names
        private static readonly (DamageType Type, string Label)[] SpellColumns =
        {
            (DamageType.Slash, "Slash"), (DamageType.Pierce, "Pierce"), (DamageType.Bludgeon, "Bludg"), (DamageType.Fire, "Fire"),
            (DamageType.Cold, "Cold"), (DamageType.Acid, "Acid"), (DamageType.Electric, "Light"), (DamageType.Nether, "Nether"),
        };

        private static float SpellResist(CharacterDetailsInputs input, DamageType type)
            => input.SpellResistanceMod != null && input.SpellResistanceMod.TryGetValue(type, out var mod) ? mod : 1.0f;

        /// <summary>
        /// A non-critical spell bolt from a monster (SpellProjectile.CalculateDamage, then DamageTarget): the resistance term x
        /// the magic absorb for the facing x the magic damage resist rating (with Battle Hardened, MitigationMath) x Fortitude.
        /// </summary>
        public static float SpellBoltTaken(float resistanceMod, float absorbMod, float magicDamageResistMod, double fortitude)
            => resistanceMod * absorbMod * magicDamageResistMod * (float)JewelryAspectMath.FortitudeMultiplier(fortitude);

        /// <summary>A DoT tick (EnchantmentManager.ApplyDamageTick): resistance x magic damage resist rating x DoT resist rating x Fortitude.</summary>
        public static float DotTickTaken(float resistanceMod, float magicDamageResistMod, int dotResistRating, double fortitude)
            => resistanceMod * magicDamageResistMod * Creature.GetNegativeRatingMod(dotResistRating) * (float)JewelryAspectMath.FortitudeMultiplier(fortitude);

        private static CharacterDetailsTable MagicTable(CharacterDetailsInputs input)
        {
            var section = new CharacterDetailsTable
            {
                Key = "magic",
                Corner = "Magic",
                Columns = SpellColumns.Select(c => c.Label).Concat(new[] { "Harm", "Drain", "DoT" }).ToList(),
            };

            if (input.Profile == null)
                return section;

            var drr = MitigationMath.DamageResistRatingMod(input.Profile, new AttackerSpec { CombatType = CombatType.Magic, AttackerIsPlayer = false });
            var fortitude = (float)JewelryAspectMath.FortitudeMultiplier(input.Fortitude);

            // Harm, Drain Health and the DoT tick take no magic absorb, so they are the same in both rows
            var tail = new[]
            {
                Pct(input.HarmResistanceMod * fortitude),
                Pct(input.DrainResistanceMod * fortitude),
                Pct(DotTickTaken(input.DotNetherResistanceMod, drr, input.DotResistRating, input.Fortitude)),
            };

            List<string> Cells(float absorb)
                => SpellColumns.Select(c => Pct(SpellBoltTaken(SpellResist(input, c.Type), absorb, drr, input.Fortitude))).Concat(tail).ToList();

            var tooltip = "A non-critical hit. Harm and Drain Health take no magic absorb or damage resist rating; Drain Health is of the amount drained, before the spell's cap. "
                + $"DoT is per Nether tick; other damage over time ticks at {Pct(DotTickTaken(input.DotUntypedResistanceMod, drr, input.DotResistRating, input.Fortitude))}. "
                + "Mana Barrier, wards and cloak procs are not counted.";

            if (input.SpellAbsorbFront != input.SpellAbsorbBack)
            {
                // a shield's frontal magic absorb applies: the two facings differ
                section.Rows.Add(new CharacterDetailsRow { Key = "spell_taken_front", Label = "Front", Cells = Cells(input.SpellAbsorbFront), Tooltip = tooltip });
                section.Rows.Add(new CharacterDetailsRow { Key = "spell_taken_back", Label = "Back", Cells = Cells(input.SpellAbsorbBack), Tooltip = "A shield's magic absorb covers the front only." });
            }
            else
            {
                section.Rows.Add(new CharacterDetailsRow { Key = "spell_taken", Label = "Front & back", Cells = Cells(input.SpellAbsorbFront), Tooltip = tooltip });
            }

            return section;
        }

        // ---- helpers ----

        private static CharacterDetailsSection List(string key, string title)
            => new CharacterDetailsSection { Key = key, Title = title, Kind = "list" };

        private static CharacterDetailsRow Row(string key, string label, string value, string tooltip = null)
            => new CharacterDetailsRow { Key = key, Label = label, Value = value, Tooltip = tooltip };

        /// <summary>A baseline-derived row: the calculation in the tooltip, ending with the specialized-monsters caveat.</summary>
        private static CharacterDetailsRow Baseline(string key, string label, string value, string tooltip)
            => Row(key, label, value, tooltip + SpecializedSuffix);

        /// <summary>A probability or multiplier as a percent with one decimal, e.g. 0.8734 -> "87.3%".</summary>
        public static string Pct(double value) => (value * 100.0).ToString("0.0", Inv) + "%";

        /// <summary>A fraction as percentage points with at most one decimal; never "-0".</summary>
        internal static string PctPoints(double fraction)
        {
            var text = (fraction * 100.0).ToString("0.#", Inv);
            return (text == "-0" ? "0" : text) + "%";
        }

        private static string Mult(double value) => value.ToString("0.00", Inv);
    }
}
