using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>The curated pool of group creature abilities (Docs/Threads/DAMAGE-CEILING-DESIGN.md "Ability pool").</summary>
    public enum CreatureAbilityKind
    {
        Execute,
        Leech,
        Frenzy,
        FrenzyCast,
        Speed,
        SpeedCast,
        Riposte,
        Draining,
        Scattering,
    }

    /// <summary>
    /// What the caller knows about one creature family: attack style flags, the effect kinds its weenies
    /// already author, how many effect records they author, and the band's spell tier.
    /// </summary>
    public sealed class CreatureAbilityFacts
    {
        /// <summary>Fights in melee only.</summary>
        public bool Melee { get; set; }

        /// <summary>Fights with missiles only.</summary>
        public bool Missile { get; set; }

        /// <summary>Casts spells only (has a spellbook, never melees).</summary>
        public bool Caster { get; set; }

        /// <summary>Melees AND casts.</summary>
        public bool Hybrid { get; set; }

        /// <summary>
        /// Effect KIND tokens, as the UNION across every family member (see <see cref="AuthoredRecordCount"/>) (the first token of a PropertyString 9015 record, e.g. "ramp", "avoid") the
        /// family's creatures already author. An ability is excluded when any record it would add has one of
        /// these kinds, so an authored kind is never doubled. Null = none.
        /// </summary>
        public IReadOnlyCollection<string> AuthoredEffectKinds { get; set; }

        /// <summary>
        /// How many effect records the family's creatures already author (the per-monster cap counts them). The caller
        /// passes the MAX across every family member (elites and boss included), and <see cref="AuthoredEffectKinds"/> the
        /// UNION across them, so the engine's tail drop at the per-monster cap can never split a riposte pair on any member.
        /// </summary>
        public int AuthoredRecordCount { get; set; }

        /// <summary>The band's spell tier (1-8) draining uses when its tunable is 0.</summary>
        public int BandSpellTier { get; set; }

        /// <summary>
        /// The attack style that WEIGHTS the draw (which kinds are favoured), when it differs from the one that decides
        /// ELIGIBILITY (the four flags above). Null = weigh by the eligibility flags. The family planner sets it to the
        /// budget-setting member's own style (orchestrator ruling 2026-10-26): the union of a family's styles decides what
        /// it may draw, the hardest hitter decides what it leans toward. Only its four style flags are read.
        /// </summary>
        public CreatureAbilityFacts Weighting { get; set; }

        internal bool HasSpellbook => Caster || Hybrid;

        internal bool Melees => Melee || Hybrid;

        /// <summary>The style the draw weights by: <see cref="Weighting"/> when set, else these facts.</summary>
        internal CreatureAbilityFacts WeightStyle => Weighting ?? this;
    }

    /// <summary>The composed overlay spec and the ability kinds it carries. <see cref="Empty"/> when nothing is attached.</summary>
    public sealed class CreatureAbilityResult
    {
        public static readonly CreatureAbilityResult Empty = new CreatureAbilityResult(string.Empty, new CreatureAbilityKind[0]);

        /// <summary>One PropertyString 9015 style string for a single ApplyMonsterEffectOverlay call ("" when empty).</summary>
        public string Spec { get; }

        /// <summary>The chosen kinds, in draw order.</summary>
        public IReadOnlyList<CreatureAbilityKind> Kinds { get; }

        public CreatureAbilityResult(string spec, IReadOnlyList<CreatureAbilityKind> kinds)
        {
            Spec = spec ?? string.Empty;
            Kinds = kinds ?? new CreatureAbilityKind[0];
        }
    }

    /// <summary>
    /// The tunables of the creature-ability layer, resolved once per plan. <see cref="Defaults"/> is the compiled
    /// default set; <see cref="Read"/> resolves the live PropertyManager values (do not call it from a unit test
    /// without seeding every key).
    /// </summary>
    public sealed class CreatureAbilitySettings
    {
        public const bool DefaultEnabled = true;
        public const string DefaultTiers = "15,45,90,150";
        public const string DefaultExecute = "execute hp=0.25 bonus=0.30";
        public const string DefaultLeech = "leech vital=health pct=0.20";
        public const string DefaultFrenzy = "ramp axis=attackspeed on=hit per=0.04 max=8 window=8";
        public const string DefaultFrenzyCast = "ramp axis=castspeed on=spellhit per=0.04 max=8 window=10";
        public const string DefaultSpeed = "speed axis=attack pct=0.15";
        public const string DefaultSpeedCast = "speed axis=cast pct=0.15";
        public const string DefaultRiposte = "avoid pct=0.15 type=melee; riposte pct=0.50 chance=0.20";
        public const double DefaultRiposteChanceCap = 0.20;
        public const string DefaultDraining = "castspell chance=0.10";
        public const long DefaultDrainingTier = 0;
        public const string DefaultScattering = "scatter pct=0.50";
        public const long DefaultMaxPerMonster = 12;

        public bool Enabled { get; set; } = DefaultEnabled;
        public string Tiers { get; set; } = DefaultTiers;
        public string Execute { get; set; } = DefaultExecute;
        public string Leech { get; set; } = DefaultLeech;
        public string Frenzy { get; set; } = DefaultFrenzy;
        public string FrenzyCast { get; set; } = DefaultFrenzyCast;
        public string Speed { get; set; } = DefaultSpeed;
        public string SpeedCast { get; set; } = DefaultSpeedCast;
        public string Riposte { get; set; } = DefaultRiposte;
        public double RiposteChanceCap { get; set; } = DefaultRiposteChanceCap;
        public string Draining { get; set; } = DefaultDraining;
        public long DrainingTier { get; set; } = DefaultDrainingTier;
        public string Scattering { get; set; } = DefaultScattering;
        public int MaxPerMonster { get; set; } = (int)DefaultMaxPerMonster;

        public static CreatureAbilitySettings Defaults => new CreatureAbilitySettings();

        /// <summary>
        /// The live values. Enabled folds in monster_effects_enabled, so switching the monster-effect system off
        /// makes the whole ability layer inert (design constraint) without touching anything else.
        /// </summary>
        public static CreatureAbilitySettings Read()
        {
            return new CreatureAbilitySettings
            {
                Enabled = PropertyManager.GetBool("dynamic_dungeons_creature_abilities", DefaultEnabled).Item
                          && MonsterEffectCaps.Enabled,
                Tiers = PropertyManager.GetString("dynamic_dungeons_creature_ability_tiers", DefaultTiers).Item,
                Execute = PropertyManager.GetString("dynamic_dungeons_creature_ability_execute", DefaultExecute).Item,
                Leech = PropertyManager.GetString("dynamic_dungeons_creature_ability_leech", DefaultLeech).Item,
                Frenzy = PropertyManager.GetString("dynamic_dungeons_creature_ability_frenzy", DefaultFrenzy).Item,
                FrenzyCast = PropertyManager.GetString("dynamic_dungeons_creature_ability_frenzy_cast", DefaultFrenzyCast).Item,
                Speed = PropertyManager.GetString("dynamic_dungeons_creature_ability_speed", DefaultSpeed).Item,
                SpeedCast = PropertyManager.GetString("dynamic_dungeons_creature_ability_speed_cast", DefaultSpeedCast).Item,
                Riposte = PropertyManager.GetString("dynamic_dungeons_creature_ability_riposte", DefaultRiposte).Item,
                RiposteChanceCap = PropertyManager.GetDouble("dynamic_dungeons_creature_ability_riposte_chance_cap", DefaultRiposteChanceCap).Item,
                Draining = PropertyManager.GetString("dynamic_dungeons_creature_ability_draining", DefaultDraining).Item,
                DrainingTier = PropertyManager.GetLong("dynamic_dungeons_creature_ability_draining_tier", DefaultDrainingTier).Item,
                Scattering = PropertyManager.GetString("dynamic_dungeons_creature_ability_scattering", DefaultScattering).Item,
                MaxPerMonster = MonsterEffectCaps.MaxPerMonster,
            };
        }
    }

    /// <summary>
    /// The Threads creature-ability catalog and its seeded selection (damage ceiling plan step 5). PURE: no world,
    /// database or PropertyManager access in <see cref="Select"/> - the tunables arrive as a
    /// <see cref="CreatureAbilitySettings"/>. Nothing here attaches an effect; the caller (spawn wiring) takes the
    /// returned <see cref="CreatureAbilityResult.Spec"/> and applies it with one overlay call.
    ///
    /// SELECTION. The family budget buys N abilities through the tier list; the N are drawn WITHOUT replacement
    /// from the eligible kinds, weighted by attack style, by a SplitMix64 stream seeded from an FNV-1a hash of
    /// (run seed, family key). Never string.GetHashCode (per-process), never the plan's shared Random: the same
    /// inputs give the same output in any process, and one family's draw cannot perturb another's.
    ///
    /// ATOMICITY. The riposte bundle is two records (avoid + riposte) bought and dropped as one ability. Dropping
    /// to fit monster_effect_max_per_monster removes whole abilities only.
    /// </summary>
    public static class ThreadCreatureAbilities
    {
        /// <summary>Weight of a kind the creature's attack style favours; every other kind weighs 1.</summary>
        public const int FavouredWeight = 3;

        private static readonly CreatureAbilityKind[] AllKinds = (CreatureAbilityKind[])Enum.GetValues(typeof(CreatureAbilityKind));

        /// <summary>The effect kinds each ability's records must have, in order. Also the exclusion keys.</summary>
        private static string[] RecordKinds(CreatureAbilityKind kind)
        {
            switch (kind)
            {
                case CreatureAbilityKind.Execute: return new[] { "execute" };
                case CreatureAbilityKind.Leech: return new[] { "leech" };
                case CreatureAbilityKind.Frenzy:
                case CreatureAbilityKind.FrenzyCast: return new[] { "ramp" };
                case CreatureAbilityKind.Speed:
                case CreatureAbilityKind.SpeedCast: return new[] { "speed" };
                case CreatureAbilityKind.Riposte: return new[] { "avoid", "riposte" };
                case CreatureAbilityKind.Draining: return new[] { "castspell" };
                case CreatureAbilityKind.Scattering: return new[] { "scatter" };
                default: return new string[0];
            }
        }

        private static string BaseSpec(CreatureAbilitySettings s, CreatureAbilityKind kind)
        {
            switch (kind)
            {
                case CreatureAbilityKind.Execute: return s.Execute;
                case CreatureAbilityKind.Leech: return s.Leech;
                case CreatureAbilityKind.Frenzy: return s.Frenzy;
                case CreatureAbilityKind.FrenzyCast: return s.FrenzyCast;
                case CreatureAbilityKind.Speed: return s.Speed;
                case CreatureAbilityKind.SpeedCast: return s.SpeedCast;
                case CreatureAbilityKind.Riposte: return s.Riposte;
                case CreatureAbilityKind.Draining: return s.Draining;
                case CreatureAbilityKind.Scattering: return s.Scattering;
                default: return string.Empty;
            }
        }

        private static bool IsCasterOnly(CreatureAbilityKind kind)
            => kind == CreatureAbilityKind.FrenzyCast || kind == CreatureAbilityKind.SpeedCast || kind == CreatureAbilityKind.Draining;

        private static bool FavouredBy(CreatureAbilityKind kind, CreatureAbilityFacts facts)
        {
            if (IsCasterOnly(kind))
                return facts.HasSpellbook;

            if (kind == CreatureAbilityKind.Riposte || kind == CreatureAbilityKind.Execute)
                return facts.Melees;

            return false;
        }

        /// <summary>
        /// Chooses the abilities for one creature family and composes their overlay spec.
        /// </summary>
        /// <param name="settings">The resolved tunables. Must come from <see cref="CreatureAbilitySettings.Read"/>, which folds in monster_effects_enabled; a settings with Enabled false returns Empty.</param>
        /// <param name="runSeed">The run's seed; with <paramref name="familyKey"/> it fixes the draw.</param>
        /// <param name="familyKey">A stable family identifier (never a hash code or an object identity).</param>
        /// <param name="budget">The family budget: the max withheld group rating among its members. 0 (solo) = nothing.</param>
        /// <param name="facts">Attack style, authored kinds and record count, band spell tier.</param>
        public static CreatureAbilityResult Select(CreatureAbilitySettings settings, long runSeed, string familyKey, int budget, CreatureAbilityFacts facts)
        {
            if (settings == null || facts == null || !settings.Enabled || budget <= 0)
                return CreatureAbilityResult.Empty;

            var count = AbilityCount(settings.Tiers, budget);
            if (count <= 0)
                return CreatureAbilityResult.Empty;

            var seed = StableSeed(runSeed, familyKey);
            var recordsOf = new Dictionary<CreatureAbilityKind, List<string>>();
            var candidates = new List<CreatureAbilityKind>();

            foreach (var kind in AllKinds)
            {
                if (IsCasterOnly(kind) && !facts.HasSpellbook)
                    continue;

                if (kind == CreatureAbilityKind.Riposte && !facts.Melees)
                    continue;

                if (AlreadyAuthored(kind, facts))
                    continue;

                var records = Compose(settings, kind, seed, facts.BandSpellTier);
                if (records == null)
                    continue;

                recordsOf[kind] = records;
                candidates.Add(kind);
            }

            var drawn = DrawWithoutReplacement(candidates, count, seed, facts);

            // Whole abilities only: one that does not fit under the per-monster cap is skipped (never trimmed),
            // and a later, smaller one may still fit.
            // monster_effect_max_per_monster <= 0 means no cap in the engine (Creature_MonsterEffects.cs)
            var room = settings.MaxPerMonster > 0 ? settings.MaxPerMonster - Math.Max(0, facts.AuthoredRecordCount) : int.MaxValue;
            var chosen = new List<CreatureAbilityKind>();

            foreach (var kind in drawn)
            {
                var records = recordsOf[kind];
                if (records.Count > room)
                    continue;

                room -= records.Count;
                chosen.Add(kind);
            }

            // The spec in CANONICAL order (enum order, each ability's records together, so riposte's avoid+riposte pair stays
            // adjacent): the same set of abilities is byte-identical whatever order it was drawn in, so the creature's
            // (wcid, composed spec) effect-set cache holds one entry per set, not one per draw order. Kinds keeps draw order.
            var spec = chosen.OrderBy(k => (int)k).SelectMany(k => recordsOf[k]);

            return chosen.Count == 0 ? CreatureAbilityResult.Empty : new CreatureAbilityResult(string.Join("; ", spec), chosen);
        }

        /// <summary>How many abilities a budget buys: the number of tiers it reaches. A malformed tier list reads as the default.</summary>
        public static int AbilityCount(string tiers, int budget)
        {
            var list = ParseTiers(tiers);
            var count = 0;

            foreach (var tier in list)
            {
                if (budget >= tier)
                    count++;
            }

            return count;
        }

        /// <summary>The tier thresholds: comma-separated, positive and strictly ascending, else the default list.</summary>
        public static IReadOnlyList<int> ParseTiers(string tiers)
        {
            var parsed = TryParseTiers(tiers);
            return parsed ?? TryParseTiers(CreatureAbilitySettings.DefaultTiers);
        }

        private static List<int> TryParseTiers(string tiers)
        {
            if (string.IsNullOrWhiteSpace(tiers))
                return null;

            var list = new List<int>();

            foreach (var token in tiers.Split(','))
            {
                if (!int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
                    return null;

                if (list.Count > 0 && value <= list[list.Count - 1])
                    return null;

                list.Add(value);
            }

            return list.Count == 0 ? null : list;
        }

        /// <summary>
        /// The effect records one ability adds, or null when its tunable is unusable (does not parse, has the wrong
        /// record kinds, or fails the real effect Validate). Riposte's chance is clamped to the cap here; draining's
        /// spell is chosen here.
        /// </summary>
        internal static List<string> Compose(CreatureAbilitySettings settings, CreatureAbilityKind kind, ulong seed, int bandSpellTier)
        {
            var raw = BaseSpec(settings, kind);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var records = raw.Split(';').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();

            if (kind == CreatureAbilityKind.Riposte)
                records = records.Select(r => ClampRiposteChance(r, settings.RiposteChanceCap)).ToList();
            else if (kind == CreatureAbilityKind.Draining)
                records = records.Select(r => AsDrainingRecord(r, ChooseDrainSpell(seed, settings.DrainingTier, bandSpellTier))).ToList();

            return IsValid(kind, records) ? records : null;
        }

        /// <summary>
        /// Clamps a riposte record's chance= to the cap (adding chance=cap when it is absent, since the effect's own
        /// default is 1.0). Any other record is returned untouched. The cap is sanitized into (0, 1].
        /// </summary>
        internal static string ClampRiposteChance(string record, double cap)
        {
            var tokens = record.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList();

            if (tokens.Count == 0 || !string.Equals(tokens[0], "riposte", StringComparison.OrdinalIgnoreCase))
                return record;

            if (double.IsNaN(cap) || double.IsInfinity(cap) || cap <= 0.0)
                cap = CreatureAbilitySettings.DefaultRiposteChanceCap;

            cap = Math.Min(cap, 1.0);

            var at = tokens.FindIndex(1, t => t.StartsWith("chance=", StringComparison.OrdinalIgnoreCase));
            var chance = 1.0;

            if (at >= 0)
            {
                if (!double.TryParse(tokens[at].Substring("chance=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out chance) || double.IsNaN(chance) || double.IsInfinity(chance))
                    chance = 1.0;
            }

            var clamped = Math.Min(chance, cap).ToString("R", CultureInfo.InvariantCulture);

            if (at >= 0)
                tokens[at] = "chance=" + clamped;
            else
                tokens.Add("chance=" + clamped);

            return string.Join(" ", tokens);
        }

        /// <summary>
        /// The drain-family spell for a seeded draw: DrainHealth, DrainStamina or DrainMana (a uniform draw), at the
        /// tunable tier when it is above 0, otherwise at the band's tier; the tier is clamped into the family's
        /// range (1-8) and a band tier of 0 reads as tier 1. The only spells this can return are the three
        /// SpellLevelProgression drain lists' members.
        /// </summary>
        public static SpellId ChooseDrainSpell(ulong seed, long tunableTier, int bandSpellTier)
        {
            var families = new[] { SpellLevelProgression.DrainHealth, SpellLevelProgression.DrainStamina, SpellLevelProgression.DrainMana };

            var family = families[(int)(SplitMix(seed ^ 0xD3A1A0F0C0FFEE11UL) % (ulong)families.Length)];

            long tier = tunableTier > 0 ? tunableTier : bandSpellTier;
            tier = Math.Max(1, Math.Min(tier, family.Count));

            return family[(int)tier - 1];
        }

        private static string AsDrainingRecord(string record, SpellId spell)
        {
            var tokens = record.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList();

            if (tokens.Count == 0 || !string.Equals(tokens[0], "castspell", StringComparison.OrdinalIgnoreCase))
                return record;

            // The spell is ours alone: an authored spell= in the tunable is dropped, not honoured.
            tokens.RemoveAll(t => t.StartsWith("spell=", StringComparison.OrdinalIgnoreCase));
            tokens.Add("spell=" + spell);

            // A caster's damage lands as spell hits, so fire on both unless the tunable chose its own trigger.
            if (!tokens.Any(t => t.StartsWith("on=", StringComparison.OrdinalIgnoreCase)))
                tokens.Add("on=hit,spellhit");

            return string.Join(" ", tokens);
        }

        /// <summary>True when the records parse cleanly, carry exactly the kind's record kinds, and every handler's Validate accepts them.</summary>
        internal static bool IsValid(CreatureAbilityKind kind, IReadOnlyList<string> records)
        {
            var expected = RecordKinds(kind);
            if (records == null || records.Count != expected.Length)
                return false;

            MonsterEffectParser.Parse(string.Join("; ", records), out var specs, out var errors);

            if (errors.Count != 0 || specs.Count != expected.Length)
                return false;

            for (var i = 0; i < expected.Length; i++)
            {
                if (specs[i].Kind != expected[i])
                    return false;

                if (!MonsterEffectRegistry.TryGetHandler(specs[i].Kind, out var handler))
                    return false;

                if (!handler.Validate(specs[i], out _))
                    return false;
            }

            return true;
        }

        private static bool AlreadyAuthored(CreatureAbilityKind kind, CreatureAbilityFacts facts)
        {
            if (facts.AuthoredEffectKinds == null || facts.AuthoredEffectKinds.Count == 0)
                return false;

            var authored = new HashSet<string>(facts.AuthoredEffectKinds.Where(k => k != null).Select(k => k.Trim().ToLowerInvariant()));

            return RecordKinds(kind).Any(authored.Contains);
        }

        private static List<CreatureAbilityKind> DrawWithoutReplacement(List<CreatureAbilityKind> candidates, int count, ulong seed, CreatureAbilityFacts facts)
        {
            var pool = new List<CreatureAbilityKind>(candidates);
            var drawn = new List<CreatureAbilityKind>();
            var state = seed;

            while (drawn.Count < count && pool.Count > 0)
            {
                state = SplitMix(state);

                var weights = pool.Select(k => FavouredBy(k, facts.WeightStyle) ? FavouredWeight : 1).ToList();
                var total = weights.Sum();

                // 53 high bits -> a uniform double in [0, 1)
                var u = (state >> 11) * (1.0 / 9007199254740992.0);
                var target = u * total;

                var pick = pool.Count - 1;
                var running = 0.0;

                for (var i = 0; i < pool.Count; i++)
                {
                    running += weights[i];
                    if (target < running)
                    {
                        pick = i;
                        break;
                    }
                }

                drawn.Add(pool[pick]);
                pool.RemoveAt(pick);
            }

            return drawn;
        }

        /// <summary>FNV-1a 64 over the UTF-8 bytes of runSeed + "|" + familyKey. Stable across processes and platforms.</summary>
        public static ulong StableSeed(long runSeed, string familyKey)
        {
            var bytes = Encoding.UTF8.GetBytes(runSeed.ToString(CultureInfo.InvariantCulture) + "|" + (familyKey ?? string.Empty));

            unchecked
            {
                var hash = 14695981039346656037UL;

                foreach (var b in bytes)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }

                return hash;
            }
        }

        private static ulong SplitMix(ulong x) => DungeonWeakPoint.SplitMix64(x);
    }
}
