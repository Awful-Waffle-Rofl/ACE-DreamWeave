using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Frays
{
    /// <summary>A player's campfire mode (saved per character).</summary>
    /// <summary>The camp's spawn shape (version 1): the same health an hour in fewer, bigger or more, smaller creatures.</summary>
    public enum FraysShape { Medium, Heavy, Light }

    /// <summary>An outdoor spawn point's kind: what it fields and how long it rests once cleared.</summary>
    public enum FraysPointKind { Normal, Nest, Quick, Rich }

    public enum FraysMode
    {
        /// <summary>Heat rises as fast as you kill, up to the cap.</summary>
        Climb,

        /// <summary>Heat rises to at most 1 level above your Heat rating.</summary>
        Push,

        /// <summary>Heat freezes: no climbing, no decay.</summary>
        Hold,

        /// <summary>Heat rises normally but never above your limit.</summary>
        Limit,

        /// <summary>Climbs until you die, then holds 2 levels below where you died for a while (the caller applies the cap).</summary>
        Auto
    }

    /// <summary>One member's view of the fire's limit: who, their mode and numbers.</summary>
    public readonly struct FraysMemberSetting
    {
        public readonly string Name;
        public readonly FraysMode Mode;
        public readonly int Limit;
        public readonly int Rating;

        public FraysMemberSetting(string name, FraysMode mode, int limit, int rating)
        {
            Name = name;
            Mode = mode;
            Limit = limit;
            Rating = rating;
        }
    }

    /// <summary>The fire's effective limit, worked out from the members present.</summary>
    public readonly struct FraysLimit
    {
        /// <summary>The highest level the fire may climb to.</summary>
        public readonly int Level;

        /// <summary>True when a member is on Hold steady: no climbing and no decay.</summary>
        public readonly bool Frozen;

        /// <summary>Whose setting sets the limit ("cap" when nobody's does).</summary>
        public readonly string SetBy;

        public FraysLimit(int level, bool frozen, string setBy)
        {
            Level = level;
            Frozen = frozen;
            SetBy = setBy;
        }
    }

    /// <summary>
    /// The Frays' pure numbers: Heat, the limit, the rating protection and Embers. No game objects, so every rule
    /// here is unit tested. The live values (cap, decay, rates) come in as arguments from FraysSettings.
    /// </summary>
    public static class FraysRules
    {
        // ---- Heat ------------------------------------------------------------------------------------------

        /// <summary>Heat points level <paramref name="level"/> needs to reach the next level: base + perLevel x level.</summary>
        public static int PointsToNext(int level, int basePoints = 20, int perLevel = 5) => Math.Max(1, basePoints + perLevel * Math.Max(0, level));

        /// <summary>Seconds as a player reads them: "45 seconds", "3 minutes", "3 minutes 30 seconds".</summary>
        public static string Duration(double seconds)
        {
            var s = Math.Max(1, (int)Math.Ceiling(seconds));
            var m = s / 60;
            s %= 60;
            string Unit(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";
            return m == 0 ? Unit(s, "second") : s == 0 ? Unit(m, "minute") : $"{Unit(m, "minute")} {Unit(s, "second")}";
        }

        /// <summary>A text bar for chat, ASCII only: <c>[####------] 40%</c> for 0.4 (clamped to 0-100%).</summary>
        public static string Bar(double fraction, int width = 10)
        {
            var f = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
            var filled = (int)Math.Floor(f * width + 1e-9);
            return "[" + new string('#', filled) + new string('-', width - filled) + "] " + Math.Floor(f * 100 + 1e-9).ToString("0") + "%";
        }

        /// <summary>
        /// The step a bar is in, for "tell me every N percent": the count of whole <paramref name="stepPercent"/>s filled
        /// (-1 when steps are off), so a new step is a higher number.
        /// </summary>
        public static int BarStep(double fraction, int stepPercent) =>
            stepPercent <= 0 ? -1 : (int)Math.Floor(Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1) * 100 / stepPercent + 1e-9);

        /// <summary>
        /// The Great Snarl's bar size: a base plus a share for each camper in the Fray now (at least one), so a pair
        /// and a dozen fill it in about the same time.
        /// </summary>
        public static double GreatSnarlNeed(double baseWork, double perCamper, int campers) =>
            Math.Max(1, baseWork + Math.Max(0, perCamper) * Math.Max(1, campers));

        /// <summary>Pure: seconds before the next Great Snarl may come, at least <paramref name="gapHours"/> after the last was called (0 = now). Set for about 5 a day.</summary>
        public static double GreatGapLeft(double lastCalledAt, double gapHours, double now) =>
            gapHours <= 0 || lastCalledAt <= 0 ? 0 : Math.Max(0, lastCalledAt + gapHours * 3600 - now);

        /// <summary>Pure: has a Great Snarl that walked in at <paramref name="spawnedAt"/> stayed its <paramref name="timeoutMinutes"/> (0 = never leaves)?</summary>
        public static bool GreatTimedOut(double spawnedAt, double timeoutMinutes, double now) =>
            timeoutMinutes > 0 && spawnedAt > 0 && now - spawnedAt >= timeoutMinutes * 60;

        /// <summary>Pure: Lucid Night is on when the week's Great Snarls reached <paramref name="need"/> and the reset is at most <paramref name="windowHours"/> away (the final night only).</summary>
        public static bool LucidOn(double greatSnarls, int need, double hoursToReset, double windowHours) =>
            greatSnarls >= Math.Max(1, need) && hoursToReset >= 0 && hoursToReset <= windowHours;

        /// <summary>What the next Heat levels bring, for the level-up line: one more creature a spawn. Empty past it.</summary>
        public static string NextMilestone(int level, int cap, int packEvery)
        {
            for (var h = level + 1; h <= cap; h++)
            {
                if (packEvery > 0 && h % packEvery == 0)
                    return $"Heat {h}: one more creature a spawn.";
            }

            return "";
        }

        /// <summary>
        /// Adds <paramref name="points"/> (negative removes) to a Heat of <paramref name="level"/> plus <paramref name="progress"/>
        /// points, carrying over levels both ways. Never climbs past <paramref name="maxLevel"/> (the progress then stops just
        /// short of the next level) and never falls below level 0 with 0 points.
        /// </summary>
        public static (int Level, int Progress) AddPoints(int level, int progress, int points, int maxLevel, int basePoints = 20, int perLevel = 5)
        {
            maxLevel = Math.Max(0, maxLevel);
            level = Math.Max(0, level);
            progress = Math.Max(0, progress) + points;

            while (progress >= PointsToNext(level, basePoints, perLevel))
            {
                if (level >= maxLevel)
                {
                    progress = PointsToNext(level, basePoints, perLevel) - 1;
                    break;
                }

                progress -= PointsToNext(level, basePoints, perLevel);
                level++;
            }

            while (progress < 0)
            {
                if (level <= 0)
                {
                    progress = 0;
                    break;
                }

                level--;
                progress += PointsToNext(level, basePoints, perLevel);
            }

            return (level, progress);
        }

        /// <summary>A death: Heat drops <paramref name="levels"/> levels and the progress into the level is kept (clamped).</summary>
        public static (int Level, int Progress) DeathDrop(int level, int progress, int levels, int basePoints = 20, int perLevel = 5)
        {
            var newLevel = Math.Max(0, level - Math.Max(0, levels));
            var newProgress = Math.Min(Math.Max(0, progress), PointsToNext(newLevel, basePoints, perLevel) - 1);

            return (newLevel, newProgress);
        }

        /// <summary>A camper's death: true when a living fire creature should fade so the camp refills at the cooled Heat.
        /// Only creatures above the new Heat go; Snarls always stay.</summary>
        public static bool FadesOnDeath(int creatureHeat, int newHeat, bool snarl) =>
            !snarl && creatureHeat > newHeat;

        /// <summary>
        /// Pure: a Snarl creature dropped to a cooler Heat when its camper dies: max health times
        /// <paramref name="newMult"/> / <paramref name="oldMult"/> (each Heat's health multiplier), and current health keeping
        /// the share it had. Never raised, never below 1.
        /// </summary>
        public static (uint Max, uint Current) DownlevelHealth(uint max, uint current, double oldMult, double newMult)
        {
            if (max == 0 || oldMult <= 0 || newMult <= 0 || newMult >= oldMult)
                return (max, Math.Min(current, max));

            var newMax = (uint)Math.Max(1, Math.Round(max * newMult / oldMult));
            var share = (double)Math.Min(current, max) / max;
            return (newMax, (uint)Math.Clamp(Math.Round(newMax * share), current > 0 ? 1 : 0, newMax));
        }

        /// <summary>Pure: seconds left before a player whose camp ended at <paramref name="endedAt"/> may take or join one again (0 = now).</summary>
        public static double RetakeWait(double endedAt, double cooldown, double now) =>
            cooldown <= 0 ? 0 : Math.Max(0, endedAt + cooldown - now);

        /// <summary>Pure: a camp that ended for <paramref name="reason"/> starts the retake cooldown; staff putting the fire out or the dungeon unloading do not.</summary>
        public static bool CountsForRetake(string reason) =>
            reason != "put out by staff" && reason != "the dungeon unloaded";

        /// <summary>Pure: the Snarl bar speed every source together gives, at most 1 + <paramref name="cap"/> (a cap below 0 = none). Without it, Lure, Bait and Bane stacked to about 2.7x Snarls.</summary>
        public static double CapSnarlSpeed(double speed, double cap) => cap < 0 ? speed : Math.Min(speed, 1 + cap);

        /// <summary>An elite pack role's health share, for the pack templates' worth (no creature is an elite now, and the elite settings are gone).</summary>
        public const double EliteHealth = 2.5;

        /// <summary>Seconds between spawns at one spawn point: base x (1 - perLevel)^level (8 s, 4%: 5.3 s at 10, 3.5 s at 20).</summary>
        public static double SpawnIntervalSeconds(int level, double baseSeconds = 8, double perLevel = 0.04, double floorSeconds = 1)
        {
            var v = baseSeconds * Math.Pow(1 - Math.Clamp(perLevel, 0, 0.99), Math.Max(0, level));
            return Math.Max(floorSeconds, v);
        }

        /// <summary>Creatures per spawn: 1, plus 1 every <paramref name="levelsPerExtra"/> levels.</summary>
        /// <summary>Bellows: one more creature for a pack of <paramref name="count"/> with chance share x count (at most 1),
        /// so creatures an hour rise by about <paramref name="share"/> at any pack size. <paramref name="roll"/> is 0 to 1.</summary>
        public static int ShareExtra(int count, double share, double roll) => share > 0 && count > 0 && roll < Math.Min(1, share * count) ? 1 : 0;

        public static int PackSize(int level, int levelsPerExtra = 5) => 1 + (levelsPerExtra <= 0 ? 0 : Math.Max(0, level) / levelsPerExtra);

        /// <summary>
        /// Whether a creature at <paramref name="heat"/> may be a caster or cast at all (caster_min_heat,
        /// casters only from Heat 17). 0 or less = always.
        /// </summary>
        public static bool CastersAllowed(int heat, int minHeat) => minHeat <= 0 || heat >= minHeat;

        /// <summary>
        /// Empties a creature's spell book below caster_min_heat, so it never casts (retail maggots carry void spells in
        /// their own book and cast them on melee creatures' turns). The book is the creature's own fresh copy, never the
        /// cached weenie's. Returns how many spells came out.
        /// </summary>
        public static int StripSpellsBelowCasterHeat(IDictionary<int, float> book, int heat, int minHeat)
        {
            if (book == null || book.Count == 0 || CastersAllowed(heat, minHeat))
                return 0;

            var count = book.Count;
            book.Clear();
            return count;
        }

        /// <summary>Removes the spells <paramref name="drop"/> picks from a book. Pure, for tests. Returns how many went.</summary>
        public static int StripSpells(IDictionary<int, float> book, Func<int, bool> drop)
        {
            if (book == null || book.Count == 0 || drop == null)
                return 0;

            var gone = book.Keys.Where(drop).ToList();
            foreach (var id in gone)
                book.Remove(id);
            return gone.Count;
        }

        /// <summary>
        /// No Frays monster dispels or reflects. A monster effect record goes when it is a dispel or a
        /// reflect, or a castspell whose spell <paramref name="isDispelSpell"/> names a dispel. Pure, for tests.
        /// </summary>
        public static bool BannedMonsterEffect(string kind, string record, Func<string, bool> isDispelSpell)
        {
            if (kind is "dispel" or "reflect")
                return true;

            if (kind != "castspell" || isDispelSpell == null)
                return false;

            foreach (var token in record.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                if (token.StartsWith("spell=", StringComparison.OrdinalIgnoreCase) && isDispelSpell(token.Substring(6)))
                    return true;

            return false;
        }

        /// <summary>A per-level percentage multiplier: 1 + perLevel x level (HP and damage +4%, XP +5%, Embers +6%).</summary>
        public static double LevelMult(int level, double perLevel) => 1 + perLevel * Math.Max(0, level);

        /// <summary>
        /// Spawn points in use for a fellowship of <paramref name="owners"/> present (the design doc's table: 1-2 use 2,
        /// 3-4 use 3, 5-9 use 4, 10-14 use 6, 15+ use 8), never more than the room has. Spawns rotate across them.
        /// </summary>
        public static int ActiveSpawnPoints(int owners, int roomPoints)
        {
            int want;

            if (owners >= 15) want = 8;
            else if (owners >= 10) want = 6;
            else if (owners >= 5) want = 4;
            else if (owners >= 3) want = 3;
            else want = 2;

            return Math.Max(1, Math.Min(want, roomPoints));
        }

        // ---- limit ----------------------------------------------------------------------------------------

        /// <summary>
        /// The fire's limit from the members present: the lowest limit among them (Climb = the cap, Push = rating + 1,
        /// Limit N = N, Hold = the current level), never above the cap, and frozen when anyone is on Hold steady.
        /// With nobody present the cap applies (the fire then goes out anyway).
        /// </summary>
        public static FraysLimit ResolveLimit(IEnumerable<FraysMemberSetting> members, int currentLevel, int cap)
        {
            var level = cap;
            var setBy = "cap";
            var frozen = false;

            foreach (var m in members ?? Enumerable.Empty<FraysMemberSetting>())
            {
                int own;

                switch (m.Mode)
                {
                    case FraysMode.Push:
                        own = m.Rating + 1;
                        break;
                    case FraysMode.Limit:
                        own = m.Limit;
                        break;
                    case FraysMode.Hold:
                        own = currentLevel;
                        frozen = true;
                        break;
                    default:
                        own = cap;
                        break;
                }

                own = Math.Clamp(own, 0, cap);

                // A Hold member names the limit unless someone's own limit is lower still.
                if (own < level || (m.Mode == FraysMode.Hold && own <= level))
                {
                    level = own;
                    setBy = m.Name;
                }
            }

            return new FraysLimit(level, frozen, setBy);
        }



        /// <summary>The weekly Ember soft cap: what <paramref name="amount"/> pays when <paramref name="already"/> were earned this week.</summary>
        public static double CappedEmbers(double amount, double already, double cap, double overRate)
        {
            if (cap <= 0 || amount <= 0)
                return Math.Max(0, amount);

            var full = Math.Min(amount, Math.Max(0, cap - already));
            return full + (amount - full) * Math.Clamp(overRate, 0, 1);
        }

        // ---- version 1: pay by level, multiplied by Heat --------------------------------------------------

        /// <summary>
        /// XP an hour for a character whose level costs <paramref name="levelCost"/> XP to the next, when one at the
        /// reference level (costing <paramref name="referenceCost"/>) earns <paramref name="atReference"/>:
        /// atReference x (levelCost / referenceCost) ^ exponent. An exponent below 1 lets lower levels gain more levels
        /// an hour than higher ones, so they catch up.
        /// </summary>
        public static double LevelPay(double atReference, double levelCost, double referenceCost, double exponent)
        {
            if (atReference <= 0 || levelCost <= 0 || referenceCost <= 0)
                return Math.Max(0, atReference);

            return atReference * Math.Pow(levelCost / referenceCost, exponent);
        }

        /// <summary>
        /// Pure: a loot roll (chance in percent, min, max) that drops <paramref name="per"/> items on average. Under one item
        /// it is that chance of one; from one up the count runs evenly from about half of it to the matching top, so
        /// 2 is 1 to 3 and 5 is 2 to 8. 0 or less drops none.
        /// </summary>
        public static (int Chance, int Min, int Max) LootRoll(double per)
        {
            if (!(per > 0))
                return (0, 0, 0);

            if (per < 1)
                return ((int)Math.Round(per * 100), 1, 1);

            var min = Math.Max(1, (int)Math.Floor(per * 0.5));
            var max = Math.Max(min, (int)Math.Round(2 * per - min));
            return (100, min, max);
        }

        /// <summary>
        /// Pure: a loot roll that drops exactly <paramref name="per"/> items on average, so brute and swarm packs
        /// drop equal loot. LootRoll's spread rounds: a 1.2 share dropped 1, a 1.25 share 1.5, so a brute pack's
        /// few big corpses drifted up to 19% off a swarm pack's many small ones of the same health. Here the whole part always
        /// drops and the rest is that chance of one more, decided now with <paramref name="roll"/> (0 to 1).
        /// </summary>
        /// <summary>
        /// Magic, ordinary and mundane counts cut down to fit <paramref name="cap"/> items in one corpse, each in proportion
        /// (a count under 1 that rolled stays 1). Unchanged when they already fit.
        /// </summary>
        public static (int Magic, int Ordinary, int Mundane) FitCorpse(int magic, int ordinary, int mundane, int cap)
        {
            var total = magic + ordinary + mundane;

            if (cap <= 0 || total <= cap)
                return (magic, ordinary, mundane);

            var k = (double)cap / total;
            int Cut(int n) => n <= 0 ? 0 : Math.Max(1, (int)Math.Floor(n * k));
            return (Cut(magic), Cut(ordinary), Cut(mundane));
        }

        /// <summary>
        /// A corpse's average magic, ordinary and mundane items cut to fit <paramref name="cap"/> (keeping their mix), and how many
        /// items went past it: its owner gets those as Vault Key progress instead.
        /// </summary>
        public static (double Magic, double Ordinary, double Mundane, double Overflow) SplitCorpse(double magic, double ordinary, double mundane, int cap)
        {
            magic = Math.Max(0, magic);
            ordinary = Math.Max(0, ordinary);
            mundane = Math.Max(0, mundane);
            var total = magic + ordinary + mundane;

            if (cap <= 0 || total <= cap)
                return (magic, ordinary, mundane, 0);

            var k = cap / total;
            return (magic * k, ordinary * k, mundane * k, total - cap);
        }

        /// <summary>The share of a Vault Key that <paramref name="overflow"/> items past a corpse's cap make (0 when loot_overflow_per_key is 0).</summary>
        public static double OverflowKeys(double overflow, double itemsPerKey) => overflow > 0 && itemsPerKey > 0 ? overflow / itemsPerKey : 0;

        public static (int Chance, int Min, int Max) LootRollExact(double per, double roll)
        {
            if (!(per > 0))
                return (0, 0, 0);

            if (per < 1)
                return ((int)Math.Round(per * 100), 1, 1);

            var count = (int)Math.Floor(per) + (roll < per - Math.Floor(per) ? 1 : 0);
            return (100, count, count);
        }

        /// <summary>
        /// A rate from a "level:value,level:value" table at <paramref name="level"/>: log-linear between the two nearest
        /// points, the first value below the table, and the last value past it (no growth past the end).
        /// -1 when the table is empty or unreadable.
        /// </summary>
        /// <summary>
        /// The key and Snarl bars' shared soft ceiling (key_bar_knee): a kill's <paramref name="work"/> spends from
        /// a budget refilling <paramref name="kneePerHour"/> an hour (10 minutes bank). The share of the kill's normal bar fill it
        /// gives (1 within the budget, <paramref name="pastRate"/> past it), and the budget left. kneePerHour 0 or less = no knee.
        /// </summary>
        public static (double Factor, double Bucket) KneeFactor(double bucket, double seconds, double work, double kneePerHour, double pastRate, double bankHours = 1.0 / 6)
        {
            if (!(kneePerHour > 0) || work <= 0)
                return (1, bucket);

            var (within, left) = SpendKillBudget(RefillKillBudget(bucket, seconds, kneePerHour, bankHours), work, kneePerHour);
            return (within + (1 - within) * Math.Clamp(pastRate, 0, 1), left);
        }

        /// <summary>A creature's work under the week's affix health multiplier: lowered with it, never raised (affixes only make a week harder).</summary>
        public static double AffixWorkMult(double affixHealth) => affixHealth > 0 ? Math.Min(1, affixHealth) : 1;

        /// <summary>The hourly kill pay budget at a Heat, in hours of xp_kill_share: xp_kill_cap_by_heat, else xp_kill_cap (0 = no cap).</summary>
        public static double KillCapAt(int heat)
        {
            var byHeat = TableRate(FraysSettings.Text(FraysSettings.XpKillCapByHeat), Math.Max(0, heat));
            return byHeat > 0 ? byHeat : FraysSettings.Number(FraysSettings.XpKillCap);
        }

        public static double TableRate(string table, int level)
        {
            var pts = new List<(int Level, double Value)>();

            foreach (var part in (table ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');

                if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out var l) && double.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
                    pts.Add((l, v));
            }

            if (pts.Count == 0)
                return -1;

            pts = pts.OrderBy(p => p.Level).ToList();

            if (level <= pts[0].Level || pts.Count == 1)
                return pts[0].Value;

            for (var i = 1; i < pts.Count; i++)
            {
                if (level <= pts[i].Level)
                    return Lerp(pts[i - 1], pts[i], level);
            }

            // Past the table: its last value. It used to keep growing on the last stretch, and a level 999
            // character earned 42 billion XP in a few minutes at Heat 0 (about 600 billion an hour).
            return pts[^1].Value;
        }

        private static double Lerp((int Level, double Value) a, (int Level, double Value) b, int level)
        {
            if (b.Level == a.Level)
                return b.Value;

            var t = (double)(level - a.Level) / (b.Level - a.Level);
            return Math.Exp(Math.Log(a.Value) + t * (Math.Log(b.Value) - Math.Log(a.Value)));
        }

        /// <summary>The Heat income multiplier: 1 + perLevel x Heat, at most 1 + max.</summary>
        public static double HeatIncome(int heat, double perLevel, double max) => 1 + Math.Min(Math.Max(0, max), Math.Max(0, heat) * Math.Max(0, perLevel));

        /// <summary>A hit on a camper, held to <paramref name="share"/> of their max health (null or 0 or less = no cap).</summary>
        public static double CapHit(double damage, double maxHealth, double? share) =>
            share is double s && s > 0 && maxHealth > 0 ? Math.Min(damage, maxHealth * s) : damage;

        /// <summary>
        /// Pure: a creature spell or drain on a camper with hit_by_health (without it, bolts took 30 to 49% of health
        /// while weapon hits took 4%). <paramref name="hit"/> is the spell's hit as the armor-based rule left it (capped, cuts
        /// in); it is held to <paramref name="share"/> of max health, +/-20% by <paramref name="roll"/>, times the creature's
        /// elite and Snarl boosts, the cap, then the camper's cuts. Never more than <paramref name="hit"/>; share 0 = unchanged.
        /// </summary>
        public static double SpellHit(double hit, double maxHealth, double share, double creatureMult, double camperMult, double? cap, double roll)
        {
            if (share <= 0 || maxHealth <= 0)
                return hit;

            var sized = CapHit(maxHealth * share * (0.8 + 0.4 * Math.Clamp(roll, 0, 1)) * creatureMult, maxHealth, cap) * camperMult;
            return Math.Min(hit, sized);
        }

        /// <summary>
        /// A creature hit sized to the camper, so creatures stay dangerous (the log
        /// showed a level 444's armor letting through 0.4% of a level 410 creature's hit). The hit is a share of the camper's
        /// max health (heat_curve_hit_share), +/-20% by <paramref name="roll"/> in [0, 1). Armor and resists still count, but
        /// cut at most <paramref name="maxArmorCut"/> of it: <paramref name="keep"/> is the share they let through (0-1).
        /// </summary>
        public static double HealthHit(double maxHealth, double share, double keep, double maxArmorCut, double roll)
        {
            keep = Math.Clamp(double.IsNaN(keep) ? 1 : keep, 0, 1);
            maxArmorCut = Math.Clamp(maxArmorCut, 0, 1);
            return maxHealth * share * (1 - maxArmorCut * (1 - keep)) * (0.8 + 0.4 * Math.Clamp(roll, 0, 1));
        }

        /// <summary>
        /// A Frays creature's hit on a camper: the hit after armor and resists (<paramref name="mitigated"/>), times the
        /// creature's Heat and elite boosts, is held to the cap; the camper's own damage cuts then scale it down.
        /// </summary>
        public static double CapHitBeforeMitigation(double mitigated, double beforeMitigation, double creatureMult, double camperMult, double maxHealth, double? share)
        {
            // Capping the raw hit and then scaling it by the share
            // armor let through counted armor twice, so a well-armored camper took a few points from a Heat 17 hit. Now the
            // hit after armor and resists is capped, and the camper's own damage cuts (boons and the like) scale it after,
            // so they still count on a capped hit. beforeMitigation is kept for the call's shape and no longer used.
            if (mitigated <= 0)
                return mitigated;

            return CapHit(mitigated * creatureMult, maxHealth, share) * camperMult;
        }

        // ---- version 1: heavy / medium / light ------------------------------------------------------------

        /// <summary>Heavy: half as many spawns, each twice the health. Light: twice the pack, half the health. Same health an hour.</summary>
        public static double ShapeHealthMult(FraysShape s) => s == FraysShape.Heavy ? 2.0 : s == FraysShape.Light ? 0.5 : 1.0;

        public static double ShapeIntervalMult(FraysShape s) => s == FraysShape.Heavy ? 2.0 : 1.0;

        /// <summary>A group camp's pack shape at a point: medium, heavy and light in turn, so every group pull sees all three.</summary>
        public static FraysShape MixedShape(int index) => (FraysShape)(((index % 3) + 3) % 3);

        public static int ShapePackMult(FraysShape s) => s == FraysShape.Light ? 2 : 1;

        /// <summary>The share of pack creatures that are the swarm kind: none when heavy, most when light.</summary>
        public static double ShapeSwarmShare(FraysShape s, double medium) => s == FraysShape.Heavy ? 0 : s == FraysShape.Light ? Math.Max(medium, 0.8) : medium;

        public static string ShapeText(FraysShape s) => s switch
        {
            FraysShape.Heavy => "heavy: fewer, bigger creatures (single target)",
            FraysShape.Light => "light: swarms of smaller creatures (AoE)",
            _ => "medium: a mix",
        };

        // ---- outdoor zones ---------------------------------------------------------------------------------

        /// <summary>
        /// The cell a landblock-local point is in: outdoors the 24 m land cell (x / 24 x 8 + y / 24 + 1), so a pack member
        /// nudged across a cell edge is filed in the right cell; indoors the spawn point's own EnvCell.
        /// </summary>
        public static ushort OutdoorCell(bool outdoor, ushort cell, System.Numerics.Vector3 pos)
        {
            if (!outdoor)
                return cell;

            var cx = Math.Clamp((int)(pos.X / 24), 0, 7);
            var cy = Math.Clamp((int)(pos.Y / 24), 0, 7);
            return (ushort)(cx * 8 + cy + 1);
        }

        /// <summary>
        /// Outdoor zones: the spawn point to use next, or -1 when none is ready: a point within <paramref name="radius"/>
        /// metres of the camper whose rest has ended (<paramref name="readyAt"/>, absent = ready), taking them in turn
        /// from <paramref name="turn"/> so a camper standing in one place sees every nearby point before they rest.
        /// </summary>
        /// <summary>
        /// Pure: how far a zone's pull layout turns this week (0 to 29: the point kinds repeat every 5 and 3, swarms and hunters
        /// every 2). A stable hash of the fire and the week, so it is the same on every server start and for every player.
        /// </summary>
        public static int WeekShift(string fireId, int week)
        {
            unchecked
            {
                uint h = 2166136261;

                foreach (var ch in (fireId ?? "") + ":" + week)
                    h = (h ^ ch) * 16777619;

                return (int)(h % 30);
            }
        }

        /// <summary>
        /// Pure: map coordinates as the client shows them, rounded to the nearest tenth ("78.4N, 60.8E"). The
        /// server's GetMapCoordStr takes 0.05 off before rounding, so a fire at 78.35N, 60.75E read 78.3N, 60.7E.
        /// </summary>
        /// <summary>
        /// Pure: map coordinates (north-south, east-west) from global metres the way the client shows them: 240 m a map unit
        /// and 101.95 off the centre. The server's own -102 put Foam Steps (exactly 76.70N) at 76.6N.
        /// </summary>
        public static (double NS, double EW) MapFromGlobal(double globalX, double globalY) => (globalY / 240.0 - 101.95, globalX / 240.0 - 101.95);

        public static string MapCoords(double northSouth, double eastWest)
        {
            static string Tenth(double v) => (Math.Round(Math.Abs(v) + 1e-4, 1, MidpointRounding.AwayFromZero)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return $"{Tenth(northSouth)}{(northSouth >= 0 ? "N" : "S")}, {Tenth(eastWest)}{(eastWest >= 0 ? "E" : "W")}";
        }

        /// <summary>Pure: the compass way of an offset (+x east, +y north), as "north", "north-east" and so on.</summary>
        public static string Compass(double dx, double dy)
        {
            if (dx == 0 && dy == 0)
                return "here";

            var deg = (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;   // 0 = north, 90 = east
            string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return names[(int)Math.Round(deg / 45) % 8];
        }

        /// <summary>Pure: a climb from <paramref name="from"/> to <paramref name="to"/> crosses a multiple of 5 (the bigger Heat flash).</summary>
        public static bool HeatMilestone(int from, int to) => to > from && to / 5 > from / 5 && to >= 5;

        /// <summary>Pure: points marked waiting (double.MaxValue) with no living creature from them.</summary>
        /// <summary>
        /// Pure: the points to refill after a death fade: each faded creature's point once, by the camper it spawned for, valid
        /// points only (0 to <paramref name="pointCount"/> - 1).
        /// </summary>
        public static List<(uint Owner, int Index)> DeathRefillPoints(IEnumerable<(uint Owner, int Index)> faded, int pointCount) =>
            (faded ?? Enumerable.Empty<(uint, int)>()).Where(f => f.Index >= 0 && f.Index < pointCount).Distinct().OrderBy(f => f.Owner).ThenBy(f => f.Index).ToList();

        public static List<int> OrphanedPoints(IReadOnlyDictionary<int, double> readyAt, IEnumerable<int> livingFrom)
        {
            var living = new HashSet<int>(livingFrom ?? Enumerable.Empty<int>());
            return (readyAt ?? new Dictionary<int, double>()).Where(kv => kv.Value == double.MaxValue && !living.Contains(kv.Key)).Select(kv => kv.Key).ToList();
        }

        public static int PickRestedPoint(IReadOnlyList<System.Numerics.Vector3> points, System.Numerics.Vector3 camper,
            IReadOnlyDictionary<int, double> readyAt, double now, double radius, int turn)
        {
            if (points == null || points.Count == 0)
                return -1;

            var r2 = radius * radius;
            var anywhere = radius <= 0;

            for (var i = 0; i < points.Count; i++)
            {
                var index = ((turn + i) % points.Count + points.Count) % points.Count;
                var p = points[index];
                var dx = p.X - camper.X;
                var dy = p.Y - camper.Y;

                if (!anywhere && dx * dx + dy * dy > r2)
                    continue;

                if (readyAt != null && readyAt.TryGetValue(index, out var t) && t > now)
                    continue;

                return index;
            }

            return -1;
        }

        // ---- outdoor routing: nests, quick points, the hot quarter -----------------------------------------

        /// <summary>
        /// A spawn point's kind, fixed by its index (the points are spread by farthest-point sampling, so each kind is
        /// spread across the zone): every 5th a nest (bigger pack, long rest), every 3rd a quick point (one
        /// creature, short rest), the rest normal.
        /// </summary>
        public static FraysPointKind PointKind(int index) => index % 5 == 2 ? FraysPointKind.Nest : index % 3 == 0 ? FraysPointKind.Quick : FraysPointKind.Normal;


        /// <summary>
        /// Outdoors, the pack a spawn point fields, as roles (family creatures): a quick point a lone straggler; a normal
        /// point alternately a swarm (many weak creatures, made for chained AoE pulls) or a pair of hunters (for single
        /// target); a nest a standard creature with a swarm escort. Heat adds to each (<paramref name="extra"/> = pack
        /// growth), a flaring quarter adds one more. Heavy turns swarms into standard creatures, light turns hunters into
        /// swarms, so the shape changes the mix rather than stretching health.
        /// </summary>
        public static List<FraysRole> PackTemplate(FraysPointKind kind, int index, FraysShape shape, int extra, bool hot)
        {
            var roles = new List<FraysRole>();
            void Add(FraysRole r, int count) { for (var i = 0; i < count; i++) roles.Add(r); }

            var swarmy = index % 2 == 0;

            switch (kind)
            {
                case FraysPointKind.Quick:
                    Add(FraysRole.Standard, 1 + (hot ? 1 : 0));
                    break;

                case FraysPointKind.Nest:
                    Add(FraysRole.Standard, 1);   // no elites, a standard creature leads the nest

                    if (shape == FraysShape.Heavy)
                        Add(FraysRole.Standard, 1 + extra + (hot ? 1 : 0));
                    else
                        Add(FraysRole.Swarm, 3 + extra + (hot ? 1 : 0));
                    break;

                default:
                    if ((swarmy && shape != FraysShape.Heavy) || shape == FraysShape.Light)
                        Add(FraysRole.Swarm, 3 + 2 * extra + (hot ? 1 : 0) + (shape == FraysShape.Light && !swarmy ? 1 : 0));
                    else
                        Add(FraysRole.Standard, 2 + extra + (hot ? 1 : 0) - (shape == FraysShape.Heavy && swarmy ? 1 : 0));
                    break;
            }

            return roles;
        }

        /// <summary>
        /// A typed pack (pack_types): a swarm point's swarm creatures, or a brute point's tough ones holding the
        /// same total health. <paramref name="extraSwarm"/> adds swarm creatures' worth (a flaring quarter, Packmaster, the
        /// affixes); a brute pack takes it as health. <paramref name="each"/> is each creature's health times its role's.
        /// </summary>
        public static List<FraysRole> TypedPack(FraysPackType type, int heat, int extraSwarm, out double each)
        {
            var swarms = Math.Max(1, SwarmCount(heat) + extraSwarm);
            var roles = new List<FraysRole>();
            each = 1;

            if (type == FraysPackType.Swarm)
            {
                for (var i = 0; i < swarms; i++)
                    roles.Add(FraysRole.Swarm);

                return roles;
            }

            var brutes = BruteCount(heat);
            each = swarms * FraysSettings.Number(FraysSettings.SwarmHealthShare) / brutes;

            for (var i = 0; i < brutes; i++)
                roles.Add(FraysRole.Standard);

            return roles;
        }

        public static int SwarmCount(int heat) => Math.Max(1, TierForLevel(FraysSettings.Text(FraysSettings.SwarmPackCounts), Math.Max(0, heat)) is var n && n > 0 ? n : 6);
        public static int BruteCount(int heat) => Math.Max(1, TierForLevel(FraysSettings.Text(FraysSettings.BrutePackCounts), Math.Max(0, heat)));

        /// <summary>A typed pack's total worth in standard creatures (the same for brute and swarm).</summary>
        public static double TypedWorth(int heat, int extraSwarm) => Math.Max(1, SwarmCount(heat) + extraSwarm) * FraysSettings.Number(FraysSettings.SwarmHealthShare);

        /// <summary>
        /// The rest multiplier for <paramref name="packs"/> typed points (not Gilded) so they field <paramref name="target"/>
        /// standard creatures' worth an hour, as RestScale: with hot spots a quarter of them rest half as long with one more swarm creature's worth.
        /// </summary>
        public static double TypedRestScale(int packs, int heat, int extraSwarm, bool hotSpots, double target)
        {
            if (!(target > 0) || packs <= 0)
                return 1;

            var rest = Math.Max(1, PointRest(FraysPointKind.Normal, false));
            var cold = TypedWorth(heat, extraSwarm) / rest;
            var each = hotSpots ? 0.75 * cold + 0.25 * TypedWorth(heat, extraSwarm + 1) / (rest * 0.5) : cold;
            // pack_type_supply: each type's points alone field this share of the target (0.5 = all of them together field it).
            var share = Math.Max(0.05, FraysSettings.Number(FraysSettings.PackTypeSupply));
            return Math.Clamp(packs * each * 3600 / (target * 2 * share), 0.1, 20);
        }

        /// <summary>
        /// The rest multiplier for one pack type's <paramref name="packs"/> points so they alone field <paramref name="target"/>
        /// standard creatures' worth an hour (pack_type_follow, so brute points keep
        /// spawning after a few solo packs are cleared). At the floor, each type's target is pack_type_supply of the Heat's, which is
        /// what TypedRestScale gives; above it, each type keeps up with its own clears.
        /// </summary>
        public static double TypedRestScaleOne(int packs, int heat, int extraSwarm, bool hotSpots, double target)
        {
            if (!(target > 0) || packs <= 0)
                return 1;

            var rest = Math.Max(1, PointRest(FraysPointKind.Normal, false));
            var cold = TypedWorth(heat, extraSwarm) / rest;
            var each = hotSpots ? 0.75 * cold + 0.25 * TypedWorth(heat, extraSwarm + 1) / (rest * 0.5) : cold;
            return Math.Clamp(packs * each * 3600 / target, 0.1, 20);
        }

        /// <summary>
        /// One pack type's target (standard creatures an hour): pack_type_supply of the Heat's floor, or that type's own warmed
        /// clear rate plus pack_type_follow_extra of it when that is more (more than spawn_follow_extra: a single-target loop
        /// has only three or four points of its type, so each must be back before the camper comes round).
        /// </summary>
        public static double TypeTarget(double floor, double typeRate) =>
            Math.Max(floor * Math.Max(0.05, FraysSettings.Number(FraysSettings.PackTypeSupply)),
                Math.Max(0, typeRate) * (1 + Math.Max(0, FraysSettings.Number(FraysSettings.PackTypeFollowExtra))));
        /// <summary>A typed pack's weapon hit, times a normal creature's: swarm_hit_share for a swarm creature, a brute's health share with brute_hit_by_share.</summary>
        public static double HitShare(FraysRole role, double each, int heat = -1)
        {
            // brute_hit_by_pack (brutes and swarms take equal time to kill): a swarm pack's
            // damage falls with each small kill, a brute's stays whole until it dies, so at the same health a brute pull cost
            // about 1.3 to 1.7 times the damage (and the healing pauses). Killed one at a time, n swarm creatures deal
            // swarmHit x n(n+1)/2 hits' worth and m brutes of `each` deal hit x each x m(m+1)/2; equal when a brute hits
            // swarm_hit_share x (n+1)/(m+1).
            if (role != FraysRole.Swarm && each > 1 && heat >= 0 && FraysSettings.Toggle(FraysSettings.BruteHitByPack))
            {
                var swarmShare = Math.Max(0.01, FraysSettings.Number(FraysSettings.SwarmHealthShare));
                var m = BruteCount(heat);
                var n = each * m / swarmShare;
                return FraysSettings.Number(FraysSettings.SwarmHitShare) * (n + 1) / (m + 1);
            }

            return (role == FraysRole.Swarm ? FraysSettings.Number(FraysSettings.SwarmHitShare) : 1)
                * (role != FraysRole.Swarm && each > 0 && FraysSettings.Toggle(FraysSettings.BruteHitByShare) ? each : 1);
        }

        public static string TypedName(FraysPackType type) => type == FraysPackType.Brute ? "brute pack" : "swarm pack";

        /// <summary>The name of a point's pack.</summary>
        public static string PackName(FraysPointKind kind, int index, FraysShape shape) => kind switch
        {
            FraysPointKind.Quick => "straggler",
            FraysPointKind.Rich => "Gilded pack",
            FraysPointKind.Nest => shape == FraysShape.Heavy ? "nest (leader + guards)" : "nest (leader + swarm)",
            _ => (index % 2 == 0 && shape != FraysShape.Heavy) || shape == FraysShape.Light ? "swarm" : "hunters",
        };
        /// <summary>The centre of an outdoor zone's circle: the middle of its landblock.</summary>
        public const float ZoneCentre = 96;

        /// <summary>True when <paramref name="pos"/> is within <paramref name="radius"/> metres (flat) of the zone centre; always true for 0 or less.</summary>
        public static bool InCircle(System.Numerics.Vector3 pos, double radius)
        {
            if (radius <= 0)
                return true;

            var dx = pos.X - ZoneCentre;
            var dy = pos.Y - ZoneCentre;
            return dx * dx + dy * dy <= radius * radius;
        }

        /// <summary><paramref name="pos"/> pulled straight in towards the zone centre until it is within <paramref name="radius"/> metres.</summary>
        public static System.Numerics.Vector3 InsideCircle(System.Numerics.Vector3 pos, double radius)
        {
            if (radius <= 0)
                return pos;

            var dx = pos.X - ZoneCentre;
            var dy = pos.Y - ZoneCentre;
            var d = Math.Sqrt(dx * dx + dy * dy);

            if (d <= radius)
                return pos;

            var k = (float)(radius / d);
            return new System.Numerics.Vector3(ZoneCentre + dx * k, ZoneCentre + dy * k, pos.Z);
        }

        /// <summary>Evenly spaced spots round the zone's circle, about <paramref name="spacing"/> metres apart (at least 8).</summary>
        public static List<(float X, float Y)> RingSpots(double radius, double spacing)
        {
            var spots = new List<(float X, float Y)>();
            radius = Math.Clamp(radius, 1, 95);
            var count = Math.Max(8, (int)Math.Ceiling(2 * Math.PI * radius / Math.Max(1, spacing)));

            for (var i = 0; i < count; i++)
            {
                var a = 2 * Math.PI * i / count;
                spots.Add(((float)(ZoneCentre + radius * Math.Cos(a)), (float)(ZoneCentre + radius * Math.Sin(a))));
            }

            return spots;
        }

        /// <summary>A pack role's worth in standard creatures: its health, and so its Heat, Embers and loot.</summary>
        public static double RoleWorth(FraysRole role) => role switch
        {
            FraysRole.Swarm => FraysSettings.Number(FraysSettings.SwarmHealthShare),
            FraysRole.Elite => FraysRules.EliteHealth,
            _ => 1,
        };

        /// <summary>A pack's worth in standard creatures.</summary>
        public static double PackWorth(IEnumerable<FraysRole> pack) => pack.Sum(RoleWorth);

        /// <summary>
        /// Standard creatures' worth an hour a camper's zone fields with every point taken as soon as it is ready, at the
        /// base rests: each point's pack worth over its rest, with the flaring quarter (a quarter of the points at any
        /// time, half the rest, one more creature) when hot spots are on. Heavy, medium and light
        /// camps all get the same rates and rewards: counted in worth, not creatures, a light camp fields more swarm
        /// creatures and a heavy one fewer standard ones, for the same health, Heat, Embers and loot an hour.
        /// </summary>
        public static double SupplyPerHour(int points, FraysShape shape, int extra, bool hotSpots, int shift = 0) =>
            SupplyPerHour(Enumerable.Range(0, Math.Max(0, points)), shape, extra, hotSpots, shift);

        /// <summary>The same over the points that field packs (a fixed layout leaves out its shrine and switched-off points).</summary>
        public static double SupplyPerHour(IEnumerable<int> points, FraysShape shape, int extra, bool hotSpots, int shift = 0)
        {
            var total = 0.0;

            foreach (var i in points)
            {
                var kind = PointKind(i + shift);
                var cold = PackWorth(PackTemplate(kind, i + shift, shape, extra, false)) / Math.Max(1, PointRest(kind, false));

                if (hotSpots)
                {
                    var hot = PackWorth(PackTemplate(kind, i + shift, shape, extra, true)) / Math.Max(1, PointRest(kind, true));
                    total += 0.75 * cold + 0.25 * hot;
                }
                else
                    total += cold;
            }

            return total * 3600;
        }

        /// <summary>
        /// The multiplier on every point rest that makes the zone field <paramref name="target"/> standard creatures' worth an hour (1 when
        /// the target is 0 or the zone fields nothing). Bigger packs at higher Heat rest longer; the count stays the same.
        /// </summary>
        public static double RestScale(int points, FraysShape shape, int extra, bool hotSpots, double target, int shift = 0) =>
            RestScale(Enumerable.Range(0, Math.Max(0, points)).ToList(), shape, extra, hotSpots, target, shift);

        public static double RestScale(IReadOnlyCollection<int> points, FraysShape shape, int extra, bool hotSpots, double target, int shift = 0)
        {
            if (!(target > 0) || points.Count == 0)
                return 1;

            var supply = SupplyPerHour(points, shape, extra, hotSpots, shift);
            return supply > 0 ? Math.Clamp(supply / target, 0.1, 20) : 1;
        }

        /// <summary>Seconds a cleared point rests: by kind, halved while its quarter of the zone flares.</summary>
        /// <summary>A cleared Gilded point's rest: rich_rest_fight_mult times its pack's fight (first hit to last kill), at least rich_rest_seconds.</summary>
        public static double RichRest(double fightSeconds) =>
            Math.Max(FraysSettings.Number(FraysSettings.RichRestSeconds), FraysSettings.Number(FraysSettings.RichRestFightMult) * Math.Max(0, fightSeconds));

        /// <summary>
        /// Standard creatures' worth an hour a camper's plain points field: spawn_target_by_heat's Heat-weighted work over
        /// this Heat's health multiplier at the camper's level, or spawn_target_per_hour when that table is empty.
        /// </summary>
        public static double SpawnTargetAt(int heat, int playerLevel)
        {
            var weighted = FraysCurve.At(FraysSettings.Text(FraysSettings.SpawnTargetByHeat), Math.Max(0, heat));

            if (weighted is not double w)
                return FraysSettings.Number(FraysSettings.SpawnTargetPerHour);

            return Math.Max(0, w) / Math.Max(1, FraysLevelScale.HeatHealthMult(playerLevel, heat, false));
        }

        /// <summary>
        /// A running clear rate read <paramref name="age"/> seconds after its first kill, corrected for its start from zero: a
        /// steady pace r reads r(1 - e^(-age/window)), so it is divided by that (uncorrected, four minutes into a camp
        /// the rate read about half its pace, the supply stayed at the floor and every point rested at once for two minutes).
        /// At least WarmMinSeconds of age, so one early pack cannot read as a huge pace.
        /// </summary>
        public static double WarmRate(double rate, double age, double window)
        {
            window = Math.Max(1, window);
            var settled = 1 - Math.Exp(-Math.Max(WarmMinSeconds, age) / window);
            return Math.Max(0, rate) / settled;
        }

        /// <summary>The least age WarmRate corrects for (seconds): 90 s caps the boost near 3.9 at a 300 s window.</summary>
        public const double WarmMinSeconds = 90;

        /// <summary>A running clear rate (work an hour) averaged over <paramref name="window"/> seconds: the old rate decayed over <paramref name="seconds"/>, plus <paramref name="work"/> just cleared.</summary>
        public static double ClearRate(double rate, double seconds, double work, double window)
        {
            window = Math.Max(1, window);
            return Math.Max(0, rate) * Math.Exp(-Math.Max(0, seconds) / window) + Math.Max(0, work) * 3600 / window;
        }

        /// <summary>
        /// A resting point's ready time when its camper's supply rises by 1 / <paramref name="ratio"/> (old target / new, 0 to 1):
        /// a rest is the target's inverse, so the time left shrinks with it (so the follow supply speeds up;
        /// rests were fixed when a point was cleared, so a rising supply reached each point a whole rest late). Points ready,
        /// busy (MaxValue) or a ratio of 1 or more are left alone.
        /// </summary>
        public static double Sooner(double readyAt, double now, double ratio) =>
            readyAt <= now || readyAt == double.MaxValue || !(ratio < 1) ? readyAt : now + (readyAt - now) * Math.Max(0, ratio);

        /// <summary>
        /// The plain points' target (standard creatures an hour) for a camper clearing <paramref name="plainRate"/> of them and
        /// <paramref name="totalRate"/> in all (work an hour, divided by <paramref name="heatHealth"/>; FraysFire feeds standard creatures and 1): what it clears plus spawn_follow_extra of the total,
        /// never under <paramref name="floor"/> (supply follows each camper).
        /// </summary>
        public static double FollowTarget(double floor, double plainRate, double totalRate, double heatHealth)
        {
            var extra = FraysSettings.Number(FraysSettings.SpawnFollowExtra);

            if (!(extra > 0))
                return floor;

            return Math.Max(floor, (Math.Max(0, plainRate) + extra * Math.Max(0, totalRate)) / Math.Max(1, heatHealth));
        }

        /// <summary>Heat points a minute the top Heats burn down (heat_upkeep_by_heat; 0 below the table or when it is empty).</summary>
        public static double HeatUpkeep(int heat) =>
            heat <= 0 ? 0 : Math.Max(0, FraysCurve.At(FraysSettings.Text(FraysSettings.HeatUpkeepByHeat), heat) is double v && heat >= FirstUpkeepHeat() ? v : 0);

        private static double FirstUpkeepHeat()
        {
            var first = double.MaxValue;

            foreach (var part in (FraysSettings.Text(FraysSettings.HeatUpkeepByHeat) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (part.Split(':') is { Length: 2 } kv && double.TryParse(kv[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x))
                    first = Math.Min(first, x);

            return first;
        }

        /// <summary>The Snarl's health, times a normal creature's at this Heat: snarl_health_by_heat, or snarl_health_normal when it is empty.</summary>
        public static double SnarlHealthAt(int heat) =>
            Math.Max(1, FraysCurve.At(FraysSettings.Text(FraysSettings.SnarlHealthByHeat), Math.Max(0, heat)) ?? FraysSettings.Number(FraysSettings.SnarlHealthNormal));

        /// <summary>pay_per_creature_by_heat at this Heat: each creature's Embers, key bar, Snarl bar and corpse loot (1 when empty).</summary>
        public static double PayPerCreature(int heat) =>
            Math.Max(0, FraysCurve.At(FraysSettings.Text(FraysSettings.PayPerCreatureByHeat), Math.Max(0, heat)) ?? 1);

        public static double PointRest(FraysPointKind kind, bool hot)
        {
            var rest = kind switch
            {
                FraysPointKind.Nest => FraysSettings.Number(FraysSettings.NestRestSeconds),
                FraysPointKind.Quick => FraysSettings.Number(FraysSettings.QuickRestSeconds),
                FraysPointKind.Rich => FraysSettings.Number(FraysSettings.RichRestSeconds),
                _ => FraysSettings.Number(FraysSettings.SpawnRegrowSeconds),
            };

            return hot ? rest * 0.5 : rest;
        }

        /// <summary>The quarter of a landblock a point is in: 0 south-west, 1 south-east, 2 north-west, 3 north-east.</summary>
        public static int Quadrant(System.Numerics.Vector3 pos) => (pos.X >= 96 ? 1 : 0) + (pos.Y >= 96 ? 2 : 0);

        public static readonly string[] QuadrantNames = { "south-west", "south-east", "north-west", "north-east" };

        /// <summary>
        /// The quarter of a zone that flares now: it moves every <paramref name="minutes"/> minutes, in an order fixed per
        /// zone, never the same quarter twice running. -1 when hot spots are off (minutes 0).
        /// </summary>
        public static int HotQuadrant(double now, string zoneId, double minutes)
        {
            if (minutes <= 0)
                return -1;

            var step = (long)Math.Floor(now / (minutes * 60));
            var seed = 0;

            foreach (var ch in zoneId ?? "")
                seed = seed * 31 + ch;

            // 1, 2 or 3 quarters on each step: never the same twice running.
            var q = Math.Abs(seed) % 4;
            var rng = new Random(Math.Abs(seed));

            for (long i = 0; i < step % 4096; i++)
                q = (q + 1 + rng.Next(3)) % 4;

            return q;
        }
        /// <summary>A setup id from text: hex with or without 0x; 0 when it is not a 0x02 setup.</summary>
        public static uint ParseSetup(string text)
        {
            var t = (text ?? "").Trim();

            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(2);

            return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v) && (v >> 24) == 0x02 ? v : 0;
        }

        // ---- bands, steps, Bias ---------------------------------------------------------------------------

        /// <summary>The step at Heat 5, 10, 15 and 20 : 1 + step x (Heat / 5, at most 4).</summary>
        public static double StepMult(int heat, double step) => 1 + step * Math.Clamp(heat / 5, 0, 4);

        /// <summary>
        /// The loot Bias: with half the chance, a corpse's magic items come from the chosen kind's profile (one of the
        /// chosen kinds, picked by <paramref name="pick"/>); else the creature's own. 0 = no change. It never adds items.
        /// Profiles: 1 weapons, 2 armour, 5 jewelry, 7 art objects (salvage).
        /// </summary>
        public static int BiasProfile(IReadOnlyList<string> bias, double roll, int pick)
        {
            if (bias == null || bias.Count == 0 || roll >= 0.5)
                return 0;

            return BiasKindProfile(bias[Math.Abs(pick) % bias.Count]);
        }

        public static readonly string[] BiasKinds = { "weapons", "armor", "jewelry", "salvage" };

        public static int BiasKindProfile(string kind) => (kind ?? "").ToLowerInvariant() switch
        {
            "weapons" => 1,
            "armor" => 2,
            "jewelry" => 5,
            "salvage" => 7,
            _ => 0,
        };

        // ---- rating ---------------------------------------------------------------------------------------

        /// <summary>
        /// The fighting-only rule: a minute counts toward the Heat rating only when the player was not holding (Hold
        /// mode), was alive and at the fire, and removed at least <paramref name="minShare"/> of the health their own
        /// stream spawned that minute. A minute with nothing spawned counts only if nothing was left standing either.
        /// </summary>
        public static bool RatingMinuteCounts(bool holding, bool present, double spawnedHealth, double removedHealth, double minShare)
        {
            if (holding || !present)
                return false;

            if (spawnedHealth <= 0)
                return removedHealth > 0;

            return removedHealth >= spawnedHealth * minShare - 1e-9;
        }

        /// <summary>
        /// The Heat a player has held for the whole window: the lowest level the fire showed over the last
        /// <paramref name="windowSeconds"/>, or -1 when the player's streak (since they arrived or last died) is shorter
        /// than the window, or the samples do not cover it. <paramref name="samples"/> are (time, level), oldest first.
        /// </summary>
        public static int SustainedLevel(IReadOnlyList<(double Time, int Level)> samples, double now, double windowSeconds, double streakStart)
        {
            if (samples == null || samples.Count == 0 || now - streakStart < windowSeconds - 1e-9)
                return -1;

            var from = now - windowSeconds;

            // The window must be covered: the oldest sample at or before its start (2 s of slack for the tick).
            if (samples[0].Time > from + 2)
                return -1;

            var min = int.MaxValue;

            for (var i = samples.Count - 1; i >= 0; i--)
            {
                var s = samples[i];

                // The last sample before the window still holds at its start.
                if (s.Time < from)
                {
                    min = Math.Min(min, s.Level);
                    break;
                }

                min = Math.Min(min, s.Level);
            }

            return min == int.MaxValue ? -1 : min;
        }

        /// <summary>The new rating: it only ever rises, to a sustained level above it.</summary>
        public static int RaiseRating(int rating, int sustained) => sustained > rating ? sustained : rating;

        // ---- Embers ---------------------------------------------------------------------------------------

        // ---- ownership ------------------------------------------------------------------------------------

        /// <summary>
        /// When the fire goes out: nobody left holding a place in the camp (everyone left the fellowship) goes out at once;
        /// otherwise it goes out once no owner has been in the dungeon (or online) for <paramref name="awaySeconds"/>.
        /// </summary>
        public static bool ShouldGoOut(int ownersRemaining, int ownersInDungeon, double secondsSinceOwnerInDungeon, double awaySeconds)
        {
            if (ownersRemaining <= 0)
                return true;

            return ownersInDungeon <= 0 && secondsSinceOwnerInDungeon >= awaySeconds;
        }

        /// <summary>
        /// When the fire goes out because no owner has stood in its zone for <paramref name="limitSeconds"/>, wherever else in
        /// the Fray they are. 0 or less turns it off.
        /// </summary>
        public static bool AwayFromFireTooLong(double secondsSinceOwnerAtFire, double limitSeconds) =>
            limitSeconds > 0 && secondsSinceOwnerAtFire >= limitSeconds;

        // ---- personal Heat (each player's own Heat at a fire) -------------------------------------------------

        /// <summary>
        /// A player's own limit: their mode, with Auto turned into a Limit while its after-death cap lasts
        /// (<paramref name="autoCap"/> &gt;= 0), else a Climb.
        /// </summary>
        public static FraysLimit PersonalLimit(string name, FraysMode mode, int limit, int rating, int currentLevel, int cap, int autoCap)
        {
            var effective = mode;
            var lim = limit;

            if (mode == FraysMode.Auto)
            {
                effective = autoCap >= 0 ? FraysMode.Limit : FraysMode.Climb;
                lim = autoCap;
            }

            return ResolveLimit(new[] { new FraysMemberSetting(name, effective, lim, rating) }, currentLevel, cap);
        }

        /// <summary>The Heat a player starts at when they arrive at a lit fire: their chosen start, never above their rating or the cap.</summary>
        public static int StartLevel(int startHeat, int rating, int cap) => Math.Clamp(Math.Min(startHeat, rating), 0, Math.Max(0, cap));

        /// <summary>
        /// The Heat a player asks to start at: their own /frays start choice when they made one, else
        /// <paramref name="belowRating"/> under their rating (clicking the fire starts you
        /// near your rating).
        /// </summary>
        public static int WantedStart(bool chosen, int startHeat, int rating, int belowRating) =>
            chosen ? Math.Max(0, startHeat) : Math.Max(0, rating - Math.Max(0, belowRating));

        /// <summary>
        /// The Heat-point multiplier for a kill: <paramref name="gain"/> for a player with no rating yet, below Heat
        /// <paramref name="until"/>; 1 otherwise (a player with no rating yet climbs the first few Heats faster).
        /// </summary>
        public static double EarlyClimbGain(int rating, int level, int until, double gain) =>
            rating <= 0 && level < until && gain > 0 ? gain : 1.0;

        /// <summary>
        /// A kill's Heat credit: its work units (base health) times the Heat health multiplier it spawned with, so a
        /// tougher creature at a higher Heat pays for the health it really had (otherwise each
        /// level cost more while each kill paid the same). Heat points only; loot and XP keep work units.
        /// </summary>
        public static double HeatCredit(double work, double share, double heatHealth) =>
            Math.Max(0, work) * Math.Max(0, share) * Math.Max(1, heatHealth);

        /// <summary>Decay only runs once a player has gone <paramref name="graceSeconds"/> without a kill: a camp that keeps killing never cools.</summary>
        public static bool DecayRuns(double lastKillAt, double now, double graceSeconds) => now - lastKillAt >= graceSeconds;

        /// <summary>Auto mode after a death: hold this many levels below where you died.</summary>
        public static int AutoCapAfterDeath(int levelAtDeath, int drop = 2) => Math.Max(0, levelAtDeath - Math.Max(0, drop));

        /// <summary>
        /// The Heat a camper is at once a settle caps them at <paramref name="cap"/>: no higher (the cap alone only stops
        /// the climb, and AddPoints lets a camper already over it stay there, so a camper settled at 16 after a death fall to
        /// 17 could fight on at 17 and die again, over and over).
        /// </summary>
        public static int SettledLevel(int level, int cap) => cap >= 0 ? Math.Min(level, cap) : level;

        /// <summary>
        /// Climbing's settle (dying too often sets you at a Heat you can hold): with
        /// <paramref name="recentDeaths"/> (time, Heat died at) already pruned to the window, <paramref name="deathsToSettle"/> or
        /// more of them settle the camper at <paramref name="drop"/> below the lowest Heat died at, for
        /// <paramref name="holdMinutes"/> doubled per earlier settle (<paramref name="settlesBefore"/>), at most
        /// <paramref name="holdMaxMinutes"/>. Null when it does not settle (too few deaths, or the rule is off).
        /// </summary>
        public static (int Cap, double HoldSeconds)? Settle(IReadOnlyList<(double At, int Heat)> recentDeaths, int deathsToSettle,
            int drop, double holdMinutes, double holdMaxMinutes, int settlesBefore)
        {
            if (deathsToSettle <= 0 || recentDeaths == null || recentDeaths.Count < deathsToSettle)
                return null;

            var cap = AutoCapAfterDeath(recentDeaths.Min(d => d.Heat), drop);
            var minutes = Math.Min(Math.Max(1, holdMaxMinutes), Math.Max(1, holdMinutes) * Math.Pow(2, Math.Clamp(settlesBefore, 0, 10)));
            return (cap, minutes * 60);
        }

        /// <summary>
        /// The Heat a fire monster hits <paramref name="defender"/> at: the lower of the Heat it spawned at and the
        /// defender's own, so a weaker player in the fellowship is never hit at a stronger player's Heat.
        /// </summary>
        public static int HitHeat(int monsterHeat, int defenderHeat) => Math.Max(0, Math.Min(monsterHeat, defenderHeat));

        /// <summary>
        /// A Frays creature's attack against its camper: at least the camper's defense plus <paramref name="offset"/> and
        /// <paramref name="perHeat"/> a Heat (attack_vs_defense), never lowered. Offset below -199 = off.
        /// </summary>
        public static uint AttackVsDefense(uint attack, uint defense, int hitHeat, double offset, double perHeat)
        {
            if (offset < -199)
                return attack;

            var floor = defense + offset + Math.Max(0, perHeat) * Math.Max(0, hitHeat);
            return floor > attack ? (uint)Math.Round(floor) : attack;
        }

        // ---- health-based credit (single target and AoE earn alike) ------------------------------------------

        /// <summary>
        /// A kill's worth in work units: the creature's base health against the room's reference creature, times
        /// the elite health multiplier. A swarm of small creatures is worth what one big one is, so AoE builds gain
        /// nothing by killing many small things and single-target builds lose nothing on elites.
        /// </summary>
        public static double WorkUnits(double baseHealth, double referenceHealth, double eliteHealthMult = 1)
        {
            if (!(referenceHealth > 0) || !(baseHealth > 0))
                return 1 * Math.Max(1, eliteHealthMult);

            return baseHealth / referenceHealth * Math.Max(1, eliteHealthMult);
        }

        /// <summary>Each player's share of a kill's credit: their damage over all owners' damage (0 when nothing was recorded).</summary>
        public static double Share(double damage, double total) => total > 0 ? Math.Clamp(damage / total, 0, 1) : 0;

        // ---- loot ---------------------------------------------------------------------------------------------

        /// <summary>Treasure tier steps at <paramref name="heat"/>: +1 from <paramref name="first"/>, +2 from <paramref name="second"/>.</summary>
        public static int LootTierSteps(int heat, int first = 8, int second = 16) => heat >= second ? 2 : heat >= first ? 1 : 0;

        /// <summary>
        /// A treasure profile's tier raised by <paramref name="steps"/>, capped at <paramref name="maxTier"/>; steps
        /// past the cap become loot-quality steps instead (so Heat 16 still beats Heat 8 for a tier 7 creature).
        /// </summary>
        /// <summary>
        /// Health removed (in standard creatures) to fill the key bar at <paramref name="heat"/>, from a "heat:work,..." table
        /// (log-linear between steps, the last step past the end). 0 when the table is empty or unreadable (no key bar).
        /// </summary>
        public static double KeyBarNeed(string table, int heat)
        {
            var need = TableRate(table, Math.Max(0, heat));
            return need > 0 ? need : 0;
        }

        /// <summary>
        /// A Frays corpse's loot quality: the creature's own, plus the tier steps past 8, a base and a little per Heat, held
        /// under <paramref name="cap"/> (never under the creature's own). A Snarl or bonus cache is not capped.
        /// </summary>
        public static double LootQuality(double own, int qualitySteps, double perStep, double baseQuality, double perHeat, int heat, double cap, bool cache)
        {
            var q = own + Math.Max(0, qualitySteps) * perStep + Math.Max(0, baseQuality) + Math.Max(0, perHeat) * Math.Max(0, heat);
            var top = cache ? 1.0 : Math.Max(own, cap);
            return Math.Clamp(Math.Min(q, top), 0, 1);
        }

        /// <summary>
        /// The lowest treasure tier for a creature of <paramref name="level"/> from a "level:tier,..." table: the tier of the
        /// highest step at or under the level. 0 when the table is empty or unreadable (no floor).
        /// </summary>
        public static int TierForLevel(string table, int level)
        {
            var best = 0;
            var bestAt = int.MinValue;

            foreach (var part in (table ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');

                if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out var at) && int.TryParse(kv[1].Trim(), out var tier) && at <= level && at >= bestAt)
                {
                    best = tier;
                    bestAt = at;
                }
            }

            return Math.Clamp(best, 0, 8);
        }

        public static (int Tier, int QualitySteps) RaiseTier(int baseTier, int steps, int maxTier = 8)
        {
            var tier = Math.Min(maxTier, baseTier + Math.Max(0, steps));
            return (tier, Math.Max(0, baseTier + Math.Max(0, steps) - tier));
        }

        // ---- static XP ----------------------------------------------------------------------------------------

        /// <summary>XP (or luminance) for <paramref name="activeSeconds"/> of camping at <paramref name="perHour"/>.</summary>
        public static long ForSeconds(double perHour, double activeSeconds) => (long)Math.Round(Math.Max(0, perHour) * Math.Max(0, activeSeconds) / 3600.0);

        /// <summary>
        /// XP or lum for one kill: <paramref name="killShare"/> of the hourly rate, spread over
        /// <paramref name="pace"/> standard creatures an hour, times the creature's worth in standard creatures, the camper's
        /// share of its damage and <paramref name="mult"/> (a Gilded creature's rich_mult). A camper killing pace an hour earns the
        /// same hour as the per-minute rate alone paid.
        /// </summary>
        public static double KillPay(double perHour, double killShare, double pace, double work, double damageShare, double mult = 1) =>
            pace > 0 ? Math.Max(0, perHour) * Math.Clamp(killShare, 0, 1) / pace * Math.Max(0, work) * Math.Clamp(damageShare, 0, 1) * Math.Max(0, mult) : 0;

        /// <summary>
        /// A camper's per-kill pay budget (xp_kill_cap): in hours of xp_kill_share, it refills
        /// <paramref name="capPerHour"/> an hour and banks up to 10 minutes of it. 0 or less = no cap.
        /// </summary>
        public static double RefillKillBudget(double bucket, double seconds, double capPerHour, double bankHours = 1.0 / 6) =>
            capPerHour > 0 ? Math.Min(capPerHour * bankHours, Math.Max(0, bucket) + Math.Max(0, seconds) / 3600 * capPerHour) : bucket;

        /// <summary>A kill costing <paramref name="cost"/> hours of kill share against the budget: the share of it paid (0 to 1) and the budget left.</summary>
        public static (double Paid, double Bucket) SpendKillBudget(double bucket, double cost, double capPerHour)
        {
            if (!(capPerHour > 0) || cost <= 0)
                return (1, bucket);

            var take = Math.Min(cost, Math.Max(0, bucket));
            return (take / cost, bucket - take);
        }

        /// <summary>Active when the player fought, took a hit from the fire, or cast within <paramref name="windowSeconds"/>.</summary>
        public static bool IsActive(double lastActiveAt, double now, double windowSeconds) => lastActiveAt > 0 && now - lastActiveAt <= windowSeconds;

        /// <summary>The text name of a mode ("climb", "push", "hold", "limit N").</summary>
        public static string ModeText(FraysMode mode, int limit) => mode == FraysMode.Limit ? $"limit {limit}" : mode.ToString().ToLowerInvariant();

        /// <summary>Parses "climb", "push", "hold" or "limit" (with its number).</summary>
        public static bool TryParseMode(string word, out FraysMode mode)
        {
            switch ((word ?? "").Trim().ToLowerInvariant())
            {
                case "climb": mode = FraysMode.Climb; return true;
                case "push": mode = FraysMode.Push; return true;
                case "hold": case "steady": mode = FraysMode.Hold; return true;
                case "limit": mode = FraysMode.Limit; return true;
                case "auto": mode = FraysMode.Auto; return true;
                default: mode = FraysMode.Climb; return false;
            }
        }
    }
}
