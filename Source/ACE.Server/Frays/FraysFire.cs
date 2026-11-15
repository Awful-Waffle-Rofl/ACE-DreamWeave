using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Frays
{
    /// <summary>What a fire's monster carries: its fire, who it spawned for, the Heat it spawned at, and leash state.</summary>
    public sealed class FraysCreatureTag
    {
        public FraysFire Fire { get; }

        /// <summary>The owner whose own Heat stream spawned it (it goes for them first).</summary>
        public uint ForPlayer { get; }

        /// <summary>The Heat it spawned at; a Snarl and its adds drop to the cooled Heat when their camper dies.</summary>
        public int Heat { get; set; }

        /// <summary>True for the Snarl, the camp guardian (its cache, its timer).</summary>
        public bool Snarl { get; init; }

        /// <summary>One of the Snarl's adds (snarl_adds): no tether or pack link, its own wave; it is hit as a pack creature (pack boons).</summary>
        public bool SnarlAdd { get; init; }

        /// <summary>The Snarl Brood: the add whose corpse holds the Snarl cache and extras.</summary>
        public bool SnarlCache { get; init; }

        /// <summary>The Snarl's damage multiplier (1 for everything else).</summary>
        public double DamageMult { get; init; } = 1;

        /// <summary>The week's number Shift on this creature's damage (Fortified, Tyrannical, Enraged), set at spawn.</summary>
        public double ShiftDamage = 1;

        /// <summary>Built by Threads at the run level (camper level plus the Heat's levels): its damage already carries the Heat.</summary>
        public bool ThreadsShaped;

        /// <summary>A cache: the best item in its corpse is attuned when the corpse fills.</summary>
        public bool AttuneBestLoot;

        /// <summary>Vault Keys (a share of one) for the items its corpse could not hold (loot_corpse_cap): its top damage dealer's key bar gets them.</summary>
        public double OverflowKeys;
        public int SpawnIndex { get; }

        /// <summary>How much tougher than a Heat 0 creature of its kind it is (FraysLevelScale.HeatHealthMult), set at spawn.</summary>
        public double HeatHealth = 1;

        /// <summary>
        /// Its worth for kill pay (XP and lum per kill, Embers, the key bar, the Snarl bar): health removed in Heat 0 standard
        /// creatures with kill_pay_by_health on, so a Heat 20 creature with 8 times the health pays 8 times;
        /// <see cref="Work"/> otherwise.
        /// </summary>
        public double PayWork => Work * (FraysSettings.Toggle(FraysSettings.KillPayByHealth) ? Math.Max(1, HeatHealth) : 1);

        /// <summary>Its worth in work units (its health against the room's reference creature): Heat points and Embers.</summary>
        public double Work { get; }

        /// <summary>When it first stood outside its zone (0 = inside).</summary>
        public double OutsideSince;

        /// <summary>When a camper first hit it (0 = not yet): a Gilded pack's fight is timed from its first hit (rich_rest_fight_mult).</summary>
        public double FirstHitAt;

        /// <summary>True while walking home on a leash return; it heals when it gets there.</summary>
        public bool Returning;
        /// <summary>A rich point's Gilded creature (FraysLayout): rich_mult times the Embers and key bar, a gold glow.</summary>
        public bool Rich;
        /// <summary>A group camp's creature: who loots its corpse (its top damage dealer; 0 = the player it spawned for).</summary>
        public uint LootOwner;
        /// <summary>Heat Shifts: whether Raging has flared on it.</summary>
        public bool Raged;

        public FraysCreatureTag(FraysFire fire, uint forPlayer, int heat, int spawnIndex, double work)
        {
            Fire = fire;
            ForPlayer = forPlayer;
            Heat = heat;
            SpawnIndex = spawnIndex;
            Work = work;
        }

        /// <summary>
        /// Whether this monster may target (or be woken by) <paramref name="creature"/>: an owner, or an owner's combat
        /// pet, standing inside the zone and not under Last Ember. Nobody else, ever.
        /// </summary>
        public bool Accepts(Creature creature)
        {
            if (creature == null || creature.IsDead || Fire == null || !Fire.Lit)
                return false;

            Player player = creature as Player;

            if (player == null && creature is CombatPet pet)
                player = pet.P_PetOwner;

            if (player == null || !Fire.IsOwner(player))
                return false;

            return Fire.InZone(creature.Location, Fire.Margin, Fire.MaxChase);
        }
    }

    /// <summary>One owner's own camp at a fire: their Heat, spawn stream, decay, rating streak and pay.</summary>
    public sealed class FraysCamper
    {
        public uint Guid;
        public int GrievousStacks, VenomStacks;
        public double NextGrievousAt, VenomUntil, NextVenomAt, NextQuakeAt;
        public string Name;
        public int Level;
        public int Progress;
        public double PointFraction;
        public double DecayAccumulator;
        public double LastKillAt;
        /// <summary>What the camper was last told about cooling: 0 nothing, 1 "cooling", 2 "dropping".</summary>
        public int CoolStage;
        /// <summary>Ember Boons held this camp (FraysBoons), and the open offer (empty: none) since OfferAt; OffersOwed queue up.</summary>
        public readonly List<string> Boons = new List<string>();

        /// <summary>What the camper did with their time, second by second (FraysSessions).</summary>
        public readonly FraysPlayLog Play = new FraysPlayLog();
        public List<string> Offer = new List<string>();
        public double OfferAt;
        public double OfferShoutAt;   // the last centre-screen reminder of an open boon offer
        public int OffersOwed;
        /// <summary>A group camp's boon vote: member guid to the offer number they voted for.</summary>
        public readonly Dictionary<uint, int> Votes = new Dictionary<uint, int>();
        public double NextSpawnAt;
        /// <summary>The camp has just been taken: every point fields its pack at once on the first spawn.</summary>
        public bool FillOnTake;
        /// <summary>Points whose packs faded when the camper died: refilled at once at the cooled Heat.</summary>
        public readonly HashSet<int> RefillPoints = new HashSet<int>();
        public double LastPresentAt;
        public bool PresentLastTick;
        public double LastActiveAt;
        public double ActiveSeconds;
        public double NextPayAt;
        /// <summary>Per-kill XP and lum budget left, in hours of xp_kill_share (xp_kill_cap), and when it was last refilled.</summary>
        public double KillBudget, KillBudgetAt;
        /// <summary>The key and Snarl bars' full-rate budget left (key_bar_knee, in Heat-weighted work) and when it was last refilled; -1 = not started.</summary>
        public double KeyKneeBudget = -1, KeyKneeAt;
        public double EarnKneeBudget = -1, EarnKneeAt;   // earn_knee: Embers and corpse loot
        public double CampEmbers;
        public int AutoCap = -1;
        public double AutoCapUntil;
        /// <summary>Climbing's settle: deaths (time, Heat) inside heat_settle_window_minutes, and how many settles this camp has had.</summary>
        public readonly List<(double At, int Heat)> RecentDeaths = new();
        public int Settles;
        public FraysLimit Limit = new FraysLimit(20, false, "cap");

        // rating: fighting minutes (see TickCamper)
        public double NextRatingAt;
        public int MinutePresentTicks;
        public double MinuteSpawnedWork;
        public double MinuteRemovedWork;
        public int MinuteMinLevel = int.MaxValue;
        public readonly Queue<int> RatingRun = new Queue<int>();

        public int RatingMinutes => RatingRun.Count;

        /// <summary>The Heat the current run would rate: the lowest over its counted minutes (the current Heat if none).</summary>
        public int RatingLevel => RatingRun.Count > 0 ? Math.Min(RatingRun.Min(), Level) : Level;

        // the camp loadout, locked when the camp starts
        public SnarlPact Snarl = SnarlPact.Normal;
        public List<string> Bias = new List<string>();
        public FraysShape Shape = FraysShape.Medium;
        public FraysCampMods Mods;

        // the Snarl
        /// <summary>The Snarl bar, as a share of a full bar (0 to 1; its need changes with Heat).</summary>
        public double SnarlBar;
        /// <summary>The key bar: health removed toward the next Ember Cache, and the caches it gave this camp.</summary>
        public double KeyBar;
        public int KeyCaches;
        public Creature SnarlAlive;
        public double SnarlSpawnedAt;
        /// <summary>The Snarl adds bar (snarl_adds): swarm pack kills fill it; full, the adds walk out.</summary>
        public double SnarlAddsBar;
        public List<Creature> SnarlAdds = new List<Creature>();
        public double SnarlAddsSpawnedAt;
        public int SnarlKills;
        public double BestSnarlSeconds;

        // Dream Surges, Deep Well
        public string Surge;
        public double SurgeUntil;
        public double NextSurgeAt;
        public double NextManaAt;
        public double NextMendAt;
        public int PlayerLevel;
        public int HotQuadrant = -1;
        /// <summary>Outdoor zones: when each spawn point is rested again for this camper (MaxValue while its creatures live).</summary>
        public readonly Dictionary<int, double> PointReadyAt = new Dictionary<int, double>();
        /// <summary>Gilded points: when the pack standing there was first hit (its fight, for rich_rest_fight_mult).</summary>
        public readonly Dictionary<int, double> RichFightFrom = new Dictionary<int, double>();
        /// <summary>Running clear rates (standard creatures an hour, spawn_follow_seconds): its plain points' and everything's, and when last fed.</summary>
        public double PlainClearRate, TotalClearRate, ClearRateAt;
        /// <summary>The same for each pack type's points alone (pack_type_follow).</summary>
        public double BruteClearRate, SwarmClearRate;
        /// <summary>When the clear rates were first fed (their warm-up, FraysRules.WarmRate); 0 before the first kill.</summary>
        public double ClearRateFrom;
        /// <summary>The top Heats' burn (heat_upkeep_by_heat): points owed, paid a whole point at a time.</summary>
        public double UpkeepOwed;

        // the Ember Shrine (FraysLayout): its buff, when it ends, and when the shrine works again
        public FraysLayout.ShrineBuff ShrineBuff;
        public double ShrineUntil, ShrineReadyAt;
        public bool ShrineToldReady = true;

        // the camp report
        public double StartedAt;
        public int StartLevel;
        public readonly List<(double Minute, int Level)> HeatLog = new List<(double, int)>();
        /// <summary>The camp minute each Heat was first reached (the climb score's time part).</summary>
        public readonly Dictionary<int, double> HeatReachedMinute = new Dictionary<int, double>();
        public int GildedKills;
        public readonly Dictionary<string, double> DamageTakenBy = new Dictionary<string, double>();
        public readonly List<string> DeathCauses = new List<string>();
        public readonly List<double> SnarlTimes = new List<double>();
        public int CachesEarned;
        public double HealthRemoved;

        // measurements
        public int Kills;
        public int Deaths;
        public int PeakLevel;
        public long XpPaid;
        public long LumPaid;
        public double EmbersEarned;
        /// <summary>The player's Heat rating when this camp began (no rating = faster early Heats).</summary>
        public int Rating;
        /// <summary>Items that dropped on this camper's corpses, by kind (what dropped, not just what was kept).</summary>
        public readonly Dictionary<string, int> LootDropped = new Dictionary<string, int>();
    }

    /// <summary>
    /// One campfire in one copy (instance) of a Frays landblock. Ownership is the fellowship's (or a solo player's);
    /// Heat is each owner's own: every owner present has their own Heat, their own spawn stream at that Heat, their own
    /// limit and decay, and earns loot, Embers and rating from it. A weaker owner only slows themselves. XP and lum are
    /// a fixed rate per active owner, paid each minute (fire monsters give no kill XP).
    ///
    /// Everything that changes the world runs on the landblock's own thread (the manager queues <see cref="Tick"/>
    /// there), and so do the deaths and hits that report in; the lock guards the commands that read it from elsewhere.
    /// </summary>
    public sealed partial class FraysFire
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(FraysFire));

        public FraysFireDef Def { get; }
        public Landblock Landblock { get; }
        public uint Instance => Landblock?.Instance ?? 0;

        public FraysLeashZone Zone { get; }
        public WorldObject FireObject { get; private set; }

        public readonly object Sync = new object();

        // ---- ownership ----
        public bool Lit { get; private set; }
        public Fellowship OwnerFellowship { get; private set; }
        public uint SoloOwner { get; private set; }
        public string OwnerName { get; private set; } = "";

        /// <summary>A Sky Banner flies over this group camp until then (FraysSky; reset when the fire is lit).</summary>
        public double SkyBannerUntil;

        /// <summary>The owner guids, replaced whole each tick (read by monster ticks without the lock).</summary>
        private volatile HashSet<uint> ownerGuids = new HashSet<uint>();

        public IReadOnlyCollection<uint> OwnerGuids => ownerGuids;

        // ---- campers, monsters, corpses ----
        private readonly Dictionary<uint, FraysCamper> campers = new Dictionary<uint, FraysCamper>();
        private readonly List<Creature> alive = new List<Creature>();
        private readonly LinkedList<Corpse> corpses = new LinkedList<Corpse>();
        private int rotation;

        // ---- measurements for the test ----
        public double LitAt { get; private set; }
        public int Kills { get; private set; }
        public int OwnerDeaths { get; private set; }
        public int LeashReturns { get; private set; }
        public int ZoneEscapes { get; private set; }
        public double FarthestFromFire { get; private set; }
        public int PresentCount { get; private set; }

        public double Margin => FraysSettings.Number(FraysSettings.LeashMargin, Def.Id);
        public double MaxChase => FraysSettings.Number(FraysSettings.LeashMaxChase, Def.Id);

        /// <summary>
        /// One work unit: the room's average creature health (weighted by its mix), so a typical kill is worth about one
        /// Heat point and smaller or bigger creatures less or more.
        /// </summary>
        public double ReferenceHealth { get; }

        public FraysFire(FraysFireDef def, Landblock landblock, FraysLeashZone zone)
        {
            Def = def;
            Landblock = landblock;
            Zone = zone;

            var weights = def.Mix.Where(m => m.Weight > 0).ToList();
            var total = weights.Sum(m => m.Weight);

            ReferenceHealth = total > 0 ? weights.Sum(m => BaseHealthOf(m.Wcid) * m.Weight) / total : 1;
        }

        /// <summary>A dispel spell (Nullify ... Other and the like, fellowship dispels too). Unknown ids are not.</summary>
        public static bool IsDispelSpell(int id)
        {
            var spell = new ACE.Server.Entity.Spell(id);
            return !spell.NotFound && spell.MetaSpellType is SpellType.Dispel or SpellType.FellowDispel;
        }

        /// <summary>The same for a castspell effect's spell= name (a SpellId name, or a number).</summary>
        public static bool IsDispelSpellName(string name)
        {
            if (int.TryParse(name, out var id))
                return IsDispelSpell(id);
            return Enum.TryParse<SpellId>(name, true, out var spellId) && IsDispelSpell((int)spellId);
        }

        /// <summary>A weenie's base max health (its retail value, before any Heat scaling).</summary>
        public static double BaseHealthOf(uint wcid)
        {
            try
            {
                var w = DatabaseManager.World.GetCachedWeenie(wcid);

                if (w?.PropertiesAttribute2nd != null && w.PropertiesAttribute2nd.TryGetValue(PropertyAttribute2nd.MaxHealth, out var v))
                    return Math.Max(1, v.InitLevel + (w.PropertiesAttribute != null && w.PropertiesAttribute.TryGetValue(PropertyAttribute.Endurance, out var e) ? e.InitLevel / 2.0 : 0));
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] could not read the health of weenie {wcid}", ex);
            }

            return 1;
        }

        public int AliveCount
        {
            get { lock (Sync) return alive.Count(c => c != null && !c.IsDead && !c.IsDestroyed); }
        }

        public int CorpseCount
        {
            get { lock (Sync) return corpses.Count(c => c != null && !c.IsDestroyed); }
        }

        public List<Creature> AliveSnapshot()
        {
            lock (Sync) return alive.Where(c => c != null && !c.IsDead && !c.IsDestroyed).ToList();
        }

        public List<FraysCamper> CampersSnapshot()
        {
            lock (Sync) return campers.Values.ToList();
        }

        /// <summary>For FraysMetrics' gauges: active campers here, the sum of their Heat, and the highest.</summary>
        public (int Count, int HeatSum, int Max) HeatSnapshot()
        {
            lock (Sync)
            {
                if (!Lit || campers.Count == 0)
                    return (0, 0, 0);

                int sum = 0, max = 0;

                foreach (var c in campers.Values)
                {
                    sum += c.Level;
                    max = Math.Max(max, c.Level);
                }

                return (campers.Count, sum, max);
            }
        }

        public FraysCamper CamperOf(Player player)
        {
            if (player == null)
                return null;

            lock (Sync) return campers.TryGetValue(player.Guid.Full, out var c) ? c : null;
        }

        /// <summary>A corpse spawned for <paramref name="forPlayer"/> has its loot: counted for that camper's report.</summary>
        public void NoteLoot(uint forPlayer, IEnumerable<string> kinds)
        {
            lock (Sync)
            {
                if (!campers.TryGetValue(forPlayer, out var c))
                    return;

                var items = 0;

                foreach (var k in kinds)
                {
                    c.LootDropped[k] = c.LootDropped.TryGetValue(k, out var n) ? n + 1 : 1;
                    items++;
                }

                FraysMetrics.AddPositive(FraysMetrics.LootItems, items);
            }
        }

        /// <summary>A player's own Heat here (0 when they have no camp at this fire).</summary>
        public int LevelOf(Player player) => CamperOf(player)?.Level ?? 0;

        /// <summary>The highest Heat any owner holds here (for listings).</summary>
        /// <summary>Outdoors: this camper's packs waiting in the zone, and points resting.</summary>
        /// <summary>
        /// The weekly pull map (like Mythic+, players learn the pulls each week and post routes): a
        /// point's place in the zone's layout this week. The kinds keep their mix (nests, quick points, swarms, hunters) but
        /// shift round the points each week, the same for every player all week.
        /// </summary>
        /// A solo outdoor camp with fixed_layout on (every camp has a set path) turns by its own
        /// fixed amount instead, the same every week.
        public int LayoutIndex(int index) => index + (FixedLayoutOn ? FraysLayout.FixedShift(Def.Id) : FraysRules.WeekShift(Def.Id, FraysWeek.Current));

        /// <summary>The shape of a point's pack: the camper's own, or at a group camp a mix of all three, point by point.</summary>
        public FraysShape PackShape(FraysCamper c, int index) => Def.Group ? FraysRules.MixedShape(LayoutIndex(index)) : c.Shape;

        /// <summary>This week's kind of spawn point <paramref name="index"/> (a fixed layout's rich points are Rich).</summary>
        public FraysPointKind KindOf(int index) => Layout is { } l && l.RoleOf(index) == FraysPointRole.Rich ? FraysPointKind.Rich
            : Layout?.TypeOf(index) is FraysPackType.Brute or FraysPackType.Swarm ? FraysPointKind.Normal : FraysRules.PointKind(LayoutIndex(index));

        /// <summary>True for a solo outdoor camp using its fixed layout (fixed_layout).</summary>
        public bool FixedLayoutOn => Def.Outdoor && !Def.Group && FraysSettings.Toggle(FraysSettings.FixedLayout);

        private FraysCampLayout layout;
        private string layoutKey;

        /// <summary>
        /// The camp's fixed layout (FraysLayout): every point's spot and role, spread layout_gap_aggro aggro ranges apart.
        /// Null for group and indoor camps, or with fixed_layout off. Rebuilt only when a setting it reads changes.
        /// </summary>
        public FraysCampLayout Layout
        {
            get
            {
                if (!FixedLayoutOn)
                    return null;

                var gap = FraysSettings.Number(FraysSettings.LayoutGapAggro) * FraysSettings.Number(FraysSettings.AggroRange);
                var radius = Def.ZoneRadius > 0 ? MyZoneRadius - 2 : ZoneRadius - 18;
                var rich = FraysSettings.Int(FraysSettings.RichPoints);
                var shrine = FraysSettings.Toggle(FraysSettings.ShrineEnabled);
                var keep = FraysSettings.Int(FraysSettings.LayoutPoints);
                var edges = FraysSettings.Toggle(FraysSettings.LayoutEdges);
                var types = FraysSettings.Toggle(FraysSettings.PackTypes);
                var key = $"{gap}|{radius}|{rich}|{shrine}|{keep}|{edges}|{types}";
                var built = layout;

                if (built != null && layoutKey == key)
                    return built;

                var points = Enumerable.Range(0, Def.SpawnPoints.Count).Select(BasePointPos).ToList();
                built = FraysLayout.Build(Def.Id, points, Def.FirePos, ZoneCentre, radius, gap, rich, shrine, keep, edges, types);
                layout = built;
                layoutKey = key;
                return built;
            }
        }

        /// <summary>This camper's creatures that are awake (fighting) (FraysReach).</summary>
        public int AwakeFor(uint camperGuid)
        {
            lock (Sync)
            {
                var n = 0;

                foreach (var m in alive)
                    if (m != null && !m.IsDead && m.AttackTarget != null && m.FraysTag != null && (Def.Group || m.FraysTag.ForPlayer == camperGuid))
                        n++;

                return n;
            }
        }

        /// <summary>A camper's point rests with the layout's spawnless points (the shrine, any switched off) never ready.</summary>
        private IReadOnlyDictionary<int, double> ReadyView(FraysCamper c)
        {
            var l = Layout;

            if (l == null)
                return c.PointReadyAt;

            var view = new Dictionary<int, double>(c.PointReadyAt);

            for (var i = 0; i < l.Roles.Count; i++)
                if (l.Spawnless(i))
                    view[i] = double.MaxValue;

            return view;
        }

        /// <summary>earn_knee: the share of a kill's Embers and corpse loot this camper gets, spending <paramref name="work"/> from their hourly budget.</summary>
        internal static double EarnFactor(FraysCamper c, double work, double now)
        {
            var knee = FraysSettings.Number(FraysSettings.EarnKnee);
            var bank = Math.Max(0.5, FraysSettings.Number(FraysSettings.EarnBankMinutes)) / 60;

            if (!(knee > 0) || c == null)
                return 1;

            if (c.EarnKneeBudget < 0)
                (c.EarnKneeBudget, c.EarnKneeAt) = (knee * bank, now);

            double factor;
            (factor, c.EarnKneeBudget) = FraysRules.KneeFactor(c.EarnKneeBudget, now - c.EarnKneeAt, work, knee, FraysSettings.Number(FraysSettings.EarnPastKnee), bank);
            c.EarnKneeAt = now;
            return factor;
        }

        /// <summary>A corpse's loot cut to <paramref name="factor"/> of its average (before the corpse is made), each kind of item on its own.</summary>
        internal static void ThinLoot(Creature creature, double factor)
        {
            var t = creature?.DeathTreasureOverride;

            if (t == null || factor >= 1)
                return;

            double Avg(int chance, int min, int max) => Math.Max(0, chance) / 100.0 * (min + max) / 2.0 * Math.Max(0, factor);
            int magicChance = t.MagicItemChance, magicMin = t.MagicItemMinAmount, magicMax = t.MagicItemMaxAmount;
            int itemChance = t.ItemChance, itemMin = t.ItemMinAmount, itemMax = t.ItemMaxAmount;
            int mundaneChance = t.MundaneItemChance, mundaneMin = t.MundaneItemMinAmount, mundaneMax = t.MundaneItemMaxAmount;
            (t.MagicItemChance, t.MagicItemMinAmount, t.MagicItemMaxAmount) = FraysRules.LootRollExact(Avg(magicChance, magicMin, magicMax), ThreadSafeRandom.Next(0.0f, 1.0f));
            (t.ItemChance, t.ItemMinAmount, t.ItemMaxAmount) = FraysRules.LootRollExact(Avg(itemChance, itemMin, itemMax), ThreadSafeRandom.Next(0.0f, 1.0f));
            (t.MundaneItemChance, t.MundaneItemMinAmount, t.MundaneItemMaxAmount) = FraysRules.LootRollExact(Avg(mundaneChance, mundaneMin, mundaneMax), ThreadSafeRandom.Next(0.0f, 1.0f));
        }

        /// <summary>What a camper hears when a Gilded pack falls: when it comes back.</summary>
        internal static string GildedClearedText(double seconds) => $"The Gilded pack falls. It returns in {FraysRules.Duration(seconds)}.";

        /// <summary>
        /// Seconds a camper's cleared point rests: its kind's rest (halved while its quarter flares), stretched or shrunk so
        /// the zone fields spawn_target_per_hour creatures an hour for this camper's Heat and shape.
        /// A rich point rests rich_rest_fight_mult times its pack's fight, at least rich_rest_seconds.
        /// </summary>
        public double PointRestFor(FraysCamper c, int index, double now)
        {
            if (KindOf(index) == FraysPointKind.Rich)
                return FraysRules.RichRest(c.RichFightFrom.TryGetValue(index, out var from) ? now - from : 0);

            var hotMinutes = FraysSettings.Number(FraysSettings.HotSpotMinutes);
            var hot = QuadrantOf(PointPos(index)) == FraysRules.HotQuadrant(now, Def.Id, hotMinutes);
            return FraysRules.PointRest(KindOf(index), hot) * RestScaleFor(c) * (FixedLayoutOn ? FraysSettings.Number(FraysSettings.LayoutRestMult) : 1) / GroupRateMult;
        }

        /// <summary>
        /// A kill feeds its point owner's running clear rates (spawn_follow_extra), from OnKill.
        /// Returns old target / new target (below 1 when the supply rose), for FraysRules.Sooner on the resting points.
        /// </summary>
        internal double FeedClearRate(FraysCamper c, FraysCreatureTag tag, double now)
        {
            var type = FollowType(tag);
            var before = type != FraysPackType.None ? TypeTargetFor(c, type) : SpawnTargetFor(c);
            var window = FraysSettings.Number(FraysSettings.SpawnFollowSeconds);
            var since = c.ClearRateAt > 0 ? now - c.ClearRateAt : 0;
            if (!(c.ClearRateFrom > 0)) c.ClearRateFrom = now;
            // Standard creatures (work units), the supply's own unit (feeding Heat-weighted pay and dividing by
            // HeatHealthMult(..., false) would mix two health multipliers and put the supply at about a third of the clear rate).
            c.TotalClearRate = FraysRules.ClearRate(c.TotalClearRate, since, tag.Work, window);
            c.PlainClearRate = FraysRules.ClearRate(c.PlainClearRate, since, tag.Rich ? 0 : tag.Work, window);
            var typeWindow = FraysSettings.Number(FraysSettings.PackTypeFollowSeconds);
            c.BruteClearRate = FraysRules.ClearRate(c.BruteClearRate, since, type == FraysPackType.Brute ? tag.Work : 0, typeWindow);
            c.SwarmClearRate = FraysRules.ClearRate(c.SwarmClearRate, since, type == FraysPackType.Swarm ? tag.Work : 0, typeWindow);
            c.ClearRateAt = now;

            var after = type != FraysPackType.None ? TypeTargetFor(c, type) : SpawnTargetFor(c);
            return before > 0 && after > before ? before / after : 1;
        }

        /// <summary>
        /// The pack type whose points a kill speeds up (pack_type_follow): a typed brute or swarm point's creature on a solo
        /// outdoor camp; None otherwise (Gilded, Snarls, group camps, the setting off), when every plain point follows as one.
        /// </summary>
        internal FraysPackType FollowType(FraysCreatureTag tag) =>
            tag == null || tag.Rich || tag.Snarl || tag.SnarlAdd || !FollowsByType ? FraysPackType.None
            : Layout.TypeOf(tag.SpawnIndex) is var t && t is FraysPackType.Brute or FraysPackType.Swarm ? t : FraysPackType.None;

        /// <summary>Solo outdoor camps with typed packs and pack_type_follow on.</summary>
        private bool FollowsByType => Def.Outdoor && !Def.Group && FraysSettings.Toggle(FraysSettings.PackTypeFollow)
            && Layout is { } l && l.Types.Any(t => t != FraysPackType.None);

        /// <summary>One pack type's target for a camper (FraysRules.TypeTarget on that type's warmed clear rate).</summary>
        public double TypeTargetFor(FraysCamper c, FraysPackType type)
        {
            var floor = FraysRules.SpawnTargetAt(c.Level, c.PlayerLevel);
            var age = c.ClearRateAt - c.ClearRateFrom;
            var window = FraysSettings.Number(FraysSettings.PackTypeFollowSeconds);
            var rate = type == FraysPackType.Brute ? c.BruteClearRate : c.SwarmClearRate;
            return FraysRules.TypeTarget(floor, FraysRules.WarmRate(rate, age, window));
        }

        /// <summary>The camper's resting plain points come back sooner when its supply rose (FraysRules.Sooner; Gilded keep their rest).</summary>
        internal void PullPointsForward(Dictionary<int, double> readyAt, double now, double ratio, FraysPackType only = FraysPackType.None)
        {
            if (!(ratio < 1))
                return;

            foreach (var i in readyAt.Keys.ToList())
                if (KindOf(i) != FraysPointKind.Rich && (only == FraysPackType.None || Layout?.TypeOf(i) == only))
                    readyAt[i] = FraysRules.Sooner(readyAt[i], now, ratio);
        }

        /// <summary>
        /// The plain points' target for a camper: spawn_target_by_heat, or more for a faster solo camper (spawn_follow_extra).
        /// Group camps keep the Heat's target (their members already scale it, GroupRateMult).
        /// </summary>
        public double SpawnTargetFor(FraysCamper c)
        {
            var floor = FraysRules.SpawnTargetAt(c.Level, c.PlayerLevel);

            if (Def.Group || !Def.Outdoor)
                return floor;

            // the rates are already standard creatures; warmed up, so a fresh camp reads its camper's real pace
            var age = c.ClearRateAt - c.ClearRateFrom;
            var window = FraysSettings.Number(FraysSettings.SpawnFollowSeconds);
            return FraysRules.FollowTarget(floor, FraysRules.WarmRate(c.PlainClearRate, age, window), FraysRules.WarmRate(c.TotalClearRate, age, window), 1);
        }

        /// <summary>The rest multiplier for a camper (FraysRules.RestScale at their Heat, shape and pack size).</summary>
        public double RestScaleFor(FraysCamper c) => RestScaleFor(c, -1);

        /// <summary>The same for one point: with pack_type_follow, a brute or swarm point follows its own type's clears.</summary>
        public double RestScaleFor(FraysCamper c, int index)
        {
            if (index >= 0 && FollowsByType && Layout.TypeOf(index) is (FraysPackType.Brute or FraysPackType.Swarm) and var type)
                return FraysRules.TypedRestScaleOne(Enumerable.Range(0, Layout.Roles.Count).Count(i => Layout.RoleOf(i) == FraysPointRole.Pack && Layout.TypeOf(i) == type),
                    c.Level, c.Mods?.PackDelta ?? 0, FraysSettings.Number(FraysSettings.HotSpotMinutes) > 0, TypeTargetFor(c, type));

            // Typed packs: the plain brute and swarm points field spawn_target_per_hour (the Gilded rest fixed).
            if (Layout is { } typed && typed.Types.Any(t => t != FraysPackType.None))
                return FraysRules.TypedRestScale(Enumerable.Range(0, typed.Roles.Count).Count(i => typed.RoleOf(i) == FraysPointRole.Pack), c.Level,
                    c.Mods?.PackDelta ?? 0, FraysSettings.Number(FraysSettings.HotSpotMinutes) > 0, SpawnTargetFor(c));   // sized to the Heat; affixes never pay more, so bigger packs rest longer

            var extra = Math.Max(0, FraysRules.PackSize(c.Level, FraysSettings.Int(FraysSettings.SpawnPackEvery)) - 1 + (c.Mods?.PackDelta ?? 0)) + LayoutPackExtra;   // bigger packs rest longer
            return FraysRules.RestScale(SupplyPoints(), c.Shape, extra, FraysSettings.Number(FraysSettings.HotSpotMinutes) > 0,
                FraysRules.SpawnTargetAt(c.Level, c.PlayerLevel), LayoutIndex(0));
        }

        /// <summary>The points that field packs: all of them, or with a fixed layout those not switched off or the shrine
        /// (counting spawnless points would make a camp field less than spawn_target_per_hour).</summary>
        public List<int> SupplyPoints()
        {
            var l = Layout;
            return Enumerable.Range(0, Def.SpawnPoints.Count).Where(i => l == null || !l.Spawnless(i)).ToList();
        }

        /// <summary>A solo fixed layout's extra creatures a pack (layout_pack_bonus): fewer points, bigger packs.</summary>
        public int LayoutPackExtra => FixedLayoutOn ? FraysSettings.Int(FraysSettings.LayoutPackBonus) : 0;

        public (int Waiting, int Resting) ZoneCounts(FraysCamper c, double now)
        {
            lock (Sync)
                return (alive.Count(m => m.FraysTag?.ForPlayer == c.Guid && !m.IsDead), c.PointReadyAt.Count(kv => kv.Value > now && kv.Value != double.MaxValue));
        }

        public int TopLevel
        {
            get { lock (Sync) return campers.Values.Select(c => c.Level).DefaultIfEmpty(0).Max(); }
        }

        public Position FirePosition => Grounded(new Position(((uint)Def.Landblock << 16) | Def.FireCell, Def.FirePos.X, Def.FirePos.Y, Def.FirePos.Z, 0, 0, 0, 1, Instance));

        /// <summary>Outdoors, a position put on the terrain (the generated heights are a cell's average); indoors, as it is.</summary>
        public Position Grounded(Position pos, float lift = 0f)
        {
            // On a raised floor (the Sky Fray's plateau) a position keeps its own height: the terrain is far below.
            if (Def.Outdoor && Def.FloorAbove == null)
            {
                try
                {
                    pos.PositionZ = ACE.Server.Entity.PositionExtensions.GetTerrainZ(pos) + lift;
                }
                catch (Exception ex)
                {
                    log.Warn($"[FRAYS] {Def.Id}: no terrain height at {pos}", ex);
                }
            }

            return pos;
        }

        public bool IsOwner(Player player) => player != null && Lit && ownerGuids.Contains(player.Guid.Full);

        public bool IsOwnerGuid(uint guid) => Lit && ownerGuids.Contains(guid);

        /// <summary>The zone's circle radius in metres (outdoor zones; 0 or less = the whole landblock).</summary>
        public static double ZoneRadius => FraysSettings.Number(FraysSettings.ZoneRadius);

        /// <summary>This fire's own zone radius (a plateau arm's, else zone_radius).</summary>
        public double MyZoneRadius => FraysGroup.RadiusOf(Def, ZoneRadius);

        /// <summary>This fire's zone centre (a plateau arm's, else the middle of the landblock).</summary>
        public Vector2 ZoneCentre => FraysGroup.CentreOf(Def);

        /// <summary>A spawn point's spot: outdoors, pulled in to sit at least 18 m inside the zone's circle (2 m on a small arm zone).</summary>
        public Vector3 PointPos(int index)
        {
            var l = Layout;

            if (l != null && index >= 0 && index < l.Positions.Count)
                return l.Positions[index];

            return BasePointPos(index);
        }

        /// <summary>A spawn point's spot before the fixed layout spreads it.</summary>
        private Vector3 BasePointPos(int index)
        {
            var p = Def.SpawnPoints[index].Pos;

            if (!Def.Outdoor)
                return p;

            return Def.ZoneRadius > 0 ? FraysGroup.InsideCircle(p, ZoneCentre, MyZoneRadius - 2) : FraysRules.InsideCircle(p, ZoneRadius - 18);
        }

        /// <summary>The quarter of this fire's zone a spot is in (round its own centre).</summary>
        public int QuadrantOf(Vector3 pos) => Def.ZoneCentre != null ? FraysGroup.Quadrant(pos, ZoneCentre) : FraysRules.Quadrant(pos);

        /// <summary>Is <paramref name="pos"/> in this fire's zone (same landblock copy, room cells or doorway margin, chase limit)?</summary>
        public bool InZone(Position pos, double margin, double maxChase)
        {
            if (pos == null || pos.Instance != Instance)
                return false;

            var at = pos.Pos;

            if ((pos.LandblockId.Raw >> 16) != Def.Landblock)
            {
                // The plateau runs past its landblock's edge (the north arm's tip, by the spire): a spot just over the line
                // is still on it (otherwise a player by the fire, over the edge, is dropped by every monster).
                if (Def.FloorAbove == null || !FraysGroup.TryLocal(Def.Landblock, (ushort)(pos.LandblockId.Raw >> 16), at, out at))
                    return false;
            }

            // Outdoors the zone is a circle round the landblock's centre (zone_radius), marked by the wisp ring; a plateau
            // arm's is everything past its wisp line.
            if (Def.Outdoor && (Def.WispLine is { } line ? !FraysGroup.BeyondLine(line, FraysDefs.PlateauMiddle, at)
                    : !FraysGroup.InCircle(at, ZoneCentre, MyZoneRadius)))
                return false;

            // The plateau: only what stands up on it, not the village and the sea below.
            if (!FraysGroup.OnFloor(Def.FloorAbove, pos.PositionZ))
                return false;

            return Zone.Contains((ushort)(pos.LandblockId.Raw & 0xFFFF), at, margin, maxChase);
        }

        public double DistanceToFire(Position pos)
        {
            if (pos == null)
                return double.MaxValue;

            // On the plateau, a spot just over the landblock's edge in the fire's own frame (see InZone).
            var at = pos.Pos;
            var lb = (ushort)(pos.LandblockId.Raw >> 16);

            if (Def.FloorAbove != null && lb != Def.Landblock)
                FraysGroup.TryLocal(Def.Landblock, lb, pos.Pos, out at);

            return Vector3.Distance(at, Def.FirePos);
        }

        // ---- placing the fire object ----------------------------------------------------------------------------

        /// <summary>The flames, there only while the fire is lit (they sit on the cold wood pile).</summary>
        public WorldObject FlameObject { get; private set; }

        /// <summary>True when lighting this fire made its landblock permanently loaded (undone when it goes out).</summary>
        private bool madePermaload;

        /// <summary>Matching flame colours: the sparkle this fire shares with one lit together with it (null = none).</summary>
        public ACE.Entity.Enum.PlayScript? KindredColour;
        public double NextSparkleAt;
        /// <summary>When the owner's Ascension sparks next rise from the flames (FraysLongTerm.AscensionSpark).</summary>
        public double NextAscendSparkAt;

        /// <summary>The solo owner's Ascensions while they are online (0 otherwise, and for a group fire's keeper).</summary>
        public int OwnerAscensions => Def.Group ? 0 : OwnerRecord()?.Ascensions ?? 0;

        /// <summary>The cold campfire (a wood pile) that always stands at the fire; the flames are added when it is lit.</summary>
        public void PlaceFireObject()
        {
            FireObject = PlaceObject(FraysDefs.ColdCampfireWcid, "2000033-hollow-firewood.sql", UnlitName);
            RefreshMarkers();
        }


        // ---- radar blip and beacon ---------------------------------------------------------------------------

        /// <summary>The beacon standing over this fire (null when beacons are off or this fire's state shows none).</summary>
        private WorldObject beacon;

        /// <summary>
        /// The fire's markers: its radar blip (one colour free, another lit) and the beacon over it, shown by
        /// beacon_mode (off, free, lit or all) with beacon_setup's model at beacon_scale. Called on placement, lighting
        /// and going out.
        /// </summary>
        public void RefreshMarkers()
        {
            try
            {
                if (FireObject != null && !FireObject.IsDestroyed)
                {
                    FireObject.RadarColor = (ACE.Entity.Enum.RadarColor)FraysSettings.Int(Lit ? FraysSettings.RadarLitColor : FraysSettings.RadarFreeColor);
                    FireObject.RadarBehavior = ACE.Entity.Enum.RadarBehavior.ShowAlways;
                    FireObject.EnqueueBroadcast(new GameMessageUpdateObject(FireObject));
                }

                var mode = FraysSettings.Text(FraysSettings.BeaconMode).ToLowerInvariant();
                var setup = FraysRules.ParseSetup(FraysSettings.Text(FraysSettings.BeaconSetup));
                var want = setup != 0 && (mode == "all" || (mode == "free" && !Lit) || (mode == "lit" && Lit));

                if (!want)
                {
                    RemoveBeacon();
                    return;
                }

                if (beacon != null && !beacon.IsDestroyed && beacon.SetupTableId == setup)
                    return;

                RemoveBeacon();
                beacon = MakeMarker(setup, (float)FraysSettings.Number(FraysSettings.BeaconScale), FirePosition, Lit ? $"{Def.RoomName} (lit)" : $"{Def.RoomName} (free campfire)");
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] {Def.Id}: markers threw", ex);
            }
        }

        // ---- the owner's wisps: a ring of fire wisps round the zone's edge, seen by the owner alone -------------

        private readonly List<WorldObject> wisps = new List<WorldObject>();
        private bool ownerInZone = true;

        private void PlaceWisps(Player owner)
        {
            RemoveWisps();

            if (!Def.Outdoor || owner == null || !FraysSettings.Toggle(FraysSettings.WispsEnabled))
                return;

            var setup = FraysRules.ParseSetup(FraysSettings.Text(FraysSettings.WispSetup));

            if (setup == 0)
                return;

            // One wisp every wisp_spacing metres round the zone's circle, close enough to read as a single burning line.
            var ringRadius = MyZoneRadius > 0 ? MyZoneRadius : 94;
            // A plateau arm's: one straight line across the arm instead.
            var ring = Def.WispLine is { } line ? FraysGroup.LineSpots(line.A, line.B, FraysSettings.Number(FraysSettings.WispSpacing))
                : Def.ZoneCentre != null ? FraysGroup.RingSpots(ZoneCentre, ringRadius, FraysSettings.Number(FraysSettings.WispSpacing))
                : FraysRules.RingSpots(ringRadius, FraysSettings.Number(FraysSettings.WispSpacing));

            foreach (var (x, y) in ring)
            {
                var at = new Position(((uint)Def.Landblock << 16) | FraysRules.OutdoorCell(true, 0, new Vector3(x, y, 0)), x, y, Def.FirePos.Z, 0, 0, 0, 1, Instance);
                at = Grounded(at, (float)FraysSettings.Number(FraysSettings.WispHeight));

                if (Def.FloorAbove != null)
                    at.PositionZ = Def.FirePos.Z + (float)FraysSettings.Number(FraysSettings.WispHeight);

                var wo = WorldObjectFactory.CreateNewWorldObject(FraysDefs.ColdCampfireWcid);

                if (wo == null)
                    break;

                wo.SetupTableId = setup;
                wo.ObjScale = (float)FraysSettings.Number(FraysSettings.WispScale);
                wo.Name = $"{Def.RoomName} (your zone)";
                wo.Location = at;
                wo.FraysTransient = true;
                wo.FraysOnlyFor = owner.Guid.Full;
                wo.TimeToRot = -1;
                wo.ItemUseable = ACE.Entity.Enum.Usable.No;
                wo.RadarBehavior = ACE.Entity.Enum.RadarBehavior.ShowNever;

                if (wo.EnterWorld())
                    wisps.Add(wo);
                else
                    wo.Destroy();
            }
        }

        private void RemoveWisps()
        {
            foreach (var w in wisps)
            {
                if (w != null && !w.IsDestroyed)
                    w.Destroy();
            }

            wisps.Clear();
        }

        private void RemoveBeacon()
        {
            if (beacon != null && !beacon.IsDestroyed)
                beacon.Destroy();

            beacon = null;
        }

        /// <summary>A display-only object with model <paramref name="setup"/> at <paramref name="at"/>: ethereal, not usable, never saved.</summary>
        public static WorldObject MakeMarker(uint setup, float scale, Position at, string name)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(FraysDefs.ColdCampfireWcid);

            if (wo == null)
                return null;

            wo.SetupTableId = setup;
            wo.ObjScale = scale;
            wo.Name = name;
            wo.Location = new Position(at);
            wo.FraysTransient = true;
            wo.TimeToRot = -1;
            wo.ItemUseable = ACE.Entity.Enum.Usable.No;
            wo.RadarBehavior = ACE.Entity.Enum.RadarBehavior.ShowNever;

            if (!wo.EnterWorld())
            {
                wo.Destroy();
                return null;
            }

            return wo;
        }

        /// <summary>
        /// A campfire's use line: a solo fire is
        /// one player's, a group fire its fellowship's.
        /// </summary>
        public static string FireUseText(bool group) => group
            ? "Double-click to light this group campfire for your fellowship."
            : "Double-click to light this campfire. It is yours alone: one player to a fire.";

        /// <summary>A campfire's description: what lighting it does (the creatures rise in its camp; they do not seek you out).</summary>
        public static string FireLongDesc(bool group) => group
            ? "A campfire in the Frays. Light it and the Drift's creatures rise in this camp for your fellowship to fight. Your Heat here is shared, and the longer you hold the fire the hotter it burns, for more experience, luminance, loot and Embers."
            : "A campfire in the Frays. Light it and the camp is yours: the Drift's creatures rise in this camp for you alone to fight and loot. Keep killing and your Heat climbs, for more experience, luminance, loot and Embers.";

        private WorldObject PlaceObject(uint wcid, string sql, string name, double scaleMult = 1, float dx = 0, float dy = 0)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (wo == null)
            {
                log.Error($"[FRAYS] {Def.Id}: weenie {wcid} is not in the world database; import C:\\dev\\dreamweave-content\\{sql}");
                return null;
            }

            wo.Location = FirePosition;
            wo.Location.PositionX += dx;
            wo.Location.PositionY += dy;
            wo.FraysTransient = true;
            wo.FraysFire = this;
            wo.TimeToRot = -1;
            wo.Name = name;
            wo.Use = FireUseText(Def.Group);
            wo.LongDesc = FireLongDesc(Def.Group);
            wo.ObjScale = (float)(FraysSettings.Number(FraysSettings.FireScale) * scaleMult);

            if (!wo.EnterWorld())
            {
                log.Error($"[FRAYS] {Def.Id}: {name} could not enter the world at {wo.Location.ToLOCString()}");
                wo.Destroy();
                return null;
            }

            log.Info($"[FRAYS] {Def.Id}: {name} entered the world at {wo.Location.ToLOCString()} ({FraysManager.MapCoordText(wo.Location) ?? "indoors"})");

            return wo;
        }

        private void LightFlames()
        {
            if (FlameObject != null && !FlameObject.IsDestroyed)
                return;

            FlameObject = PlaceObject(FraysDefs.CampfireWcid, "2000029-hollow-campfire.sql", LitName, FraysPerks.FlameScaleFor(SoloOwner));

            // A bonfire at high tiers: two side flames from tier 5, four from tier 10, smaller than the heart.
            var tier = OwnerRecord()?.FireTier ?? 0;
            var sides = tier >= 10 ? 4 : tier >= 5 ? 2 : 0;
            var spots = new[] { (1.3f, 0f), (-1.3f, 0f), (0f, 1.3f), (0f, -1.3f) };

            for (var i = 0; i < sides; i++)
            {
                var side = PlaceObject(FraysDefs.CampfireWcid, "2000029-hollow-campfire.sql", LitName, FraysPerks.FlameScaleFor(SoloOwner) * 0.55, spots[i].Item1, spots[i].Item2);

                if (side != null)
                    sideFlames.Add(side);
            }
        }

        private readonly List<WorldObject> sideFlames = new List<WorldObject>();

        private void PutOutFlames()
        {
            var flames = FlameObject;
            FlameObject = null;

            if (flames != null && !flames.IsDestroyed)
                flames.Destroy();

            foreach (var s in sideFlames)
                if (s != null && !s.IsDestroyed)
                    s.Destroy();

            sideFlames.Clear();
        }

        /// <summary>The owner's record while they are online (their tier, name for the fire, Epithet, sparks), else null.</summary>
        private FraysPlayerRecord OwnerRecord() => PlayerManager.GetOnlinePlayer(SoloOwner) is Player o ? FraysManager.Record(o) : null;

        /// <summary>
        /// The fire's look changed (a tier bought, a new name or Epithet): the flames swap at once, a flare covering the
        /// swap, so a bigger blaze appears seamlessly when unlocked.
        /// </summary>
        public void RefreshLook()
        {
            if (!Lit)
                return;

            // On the fire's own landblock (a command runs on the player's).
            Landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                if (!Lit)
                    return;

                PutOutFlames();
                LightFlames();
                Rename(LitName);
                FraysManager.Effect(FlameObject, PlayScript.EnchantUpOrange);
            }));
        }

        /// <summary>The fire's spark colour: a kindred pair's shared one, else the owner's choice (/fray sparks), else none.</summary>
        public PlayScript? SparkColour => KindredColour ?? (OwnerRecord() is FraysPlayerRecord r && FraysCodex.SparkColours.TryGetValue(r.Sparks ?? "", out var s) ? s : null);

        private string UnlitName => $"Frays Campfire - {Def.RoomName}";
        private string LitName => FraysCodex.LitName(Def.RoomName, OwnerName, OwnerRecord());

        private void Rename(string name)
        {
            foreach (var wo in new[] { FireObject, FlameObject })
            {
                if (wo == null || wo.IsDestroyed)
                    continue;

                wo.Name = name;
                wo.EnqueueBroadcast(new GameMessagePublicUpdatePropertyString(wo, PropertyString.Name, name));
            }
        }

        // ---- lighting and going out ----------------------------------------------------------------------------

        /// <summary>Why this player cannot light the fire now, or null when they can.</summary>
        public string RefuseLight(Player player)
        {
            if (Def.Group)
                return RefuseGroupLight(player);

            if (Lit)
                return IsOwner(player) || SoloOwner == player.Guid.Full
                    ? $"This is your fire. Your Heat here is {LevelOf(player)}."
                    : $"This fire belongs to {OwnerName}. Each camp holds one player; find a cold fire.";

            if (FraysManager.FireOwnedBySolo(player.Guid.Full) is FraysFire mine)
                return $"You already hold a fire ({mine.Def.RoomName}). One fire each.";

            if (RetakeRefusal(player, Time.GetUnixTime()) is string retake)
                return retake;

            var minLevel = FraysSettings.Int(FraysSettings.MinLevel);

            if ((player.Level ?? 1) < minLevel && player.Session?.AccessLevel < AccessLevel.Advocate)
                return $"The fire will not take for you yet. Campfires answer only to level {minLevel} and above.";

            if (FraysSettings.Toggle(FraysSettings.OnePerIp) && player.Session?.AccessLevel < AccessLevel.Advocate && FraysManager.FireHeldFromSameIp(player) is FraysFire sameIp)
                return $"Another character on your connection already holds a fire ({sameIp.Def.RoomName}). One fire per player.";

            return null;
        }

        /// <summary>A player lit the fire (after any confirmation). Returns the line to show them.</summary>
        public string TryLight(Player player, double now)
        {
            lock (Sync)
            {
                var refuse = RefuseLight(player);

                if (refuse != null)
                    return refuse;

                // One player per camp. Holding a fire puts you in the dungeon's fellowship (the camps share it); the
                // fellowship has no real leader, and leaving it gives up the fire. A group camp is held by the lighter's
                // own fellowship instead (the group part).
                if (!Def.Group && !FraysManager.JoinDungeonFellowship(player, this))
                    return $"You cannot join the Frays fellowship (it may be full), so you cannot hold a fire here.";

                Lit = true;
                OwnerFellowship = Def.Group ? player.Fellowship : null;
                SoloOwner = Def.Group ? 0 : player.Guid.Full;
                OwnerName = Def.Group ? GroupOwnerName(player) : player.Name;
                groupLead = Def.Group ? player.Guid.Full : 0;
                groupBelowMinSince = 0;
                rotation = ThreadSafeRandom.Next(0, Math.Max(0, Def.SpawnPoints.Count - 1));
                lastOwnerInDungeonAt = now;
                lastOwnerAtFireAt = now;
                LitAt = now;
                SkyBannerUntil = 0;
                Kills = OwnerDeaths = LeashReturns = ZoneEscapes = 0;
                FarthestFromFire = 0;
                campers.Clear();
                remembered.Clear();

                RefreshOwners(now);
            }

            // Keep the landblock loaded while the fire burns, so it waits out the owner's grace instead of unloading after 5 minutes.
            if (!Landblock.Permaload)
            {
                Landblock.Permaload = true;
                madePermaload = true;
            }

            LightFlames();
            Rename(LitName);
            RefreshMarkers();
            FraysFrayWide.OnLit(this, now);
            PlaceWisps(player);
            PlaceShrine(player);
            log.Info($"[FRAYS] {Def.Id} 0x{Instance:X8} lit by {player.Name} for {OwnerName}");
            FraysMetrics.FiresLit.Add(1);
            FraysLongTerm.GiveHearthstone(player);
            FraysLongTerm.FoldLooseKeys(player);   // keys held from before the Vault Keyring, or bought, go onto it

            var spentRec = FraysManager.Record(player);   // the perk preset spends any points waiting
            if (FraysPerks.AutoSpend(spentRec).Count > 0)
                FraysManager.SaveRecord(player, spentRec);

            if (Def.Group)
            {
                var groupLit = GroupLitLine(player);
                TellGroup(player, $"{player.Name} lights the group campfire at {Def.RoomName}. It belongs to your fellowship; your Heat here is shared.");
                return player.Attackable ? groupLit : groupLit + " You are not attackable, so its creatures will ignore you: use /attackable on.";
            }

            var rec = FraysManager.Record(player);
            var start = FraysRules.StartLevel(Math.Max(rec.WantedStart(false), FraysSettings.Int(FraysSettings.HeatStart)), Math.Max(rec.Rating, FraysSettings.Int(FraysSettings.HeatStart)), FraysPerks.CapFor(rec));
            var lit = $"You light the campfire. It is yours, and you are in the Frays fellowship. Your Heat starts at {start} ({rec.HeatText}; /frays stop or /frays go). Leaving the dungeon or logging off gives you {FraysSettings.Number(FraysSettings.AwaySeconds) / 60:0} minutes to come back."
                + (FraysSettings.Number(FraysSettings.FireAwaySeconds) > 0 ? $" Away from the fire for {FraysSettings.Number(FraysSettings.FireAwaySeconds) / 60:0} minutes, it goes out (a Campfire Hearthstone brings you back)." : "");

            // Staff characters are often not attackable, and monsters never target anything that is not.
            return player.Attackable ? lit : lit + " You are not attackable, so its creatures will ignore you: use /attackable on.";
        }

        /// <summary>Puts the fire out: its monsters despawn and everyone's Heat here resets. Safe to call more than once.</summary>
        public void Extinguish(string reason)
        {
            List<Creature> despawn;
            List<Player> tell;
            List<FraysCamper> report;

            lock (Sync)
            {
                if (!Lit)
                    return;

                tell = OnlineOwners();
                report = campers.Values.ToList();
                Lit = false;
                OwnerFellowship = null;
                SoloOwner = 0;
                groupLead = 0;
                groupBelowMinSince = 0;
                ownerGuids = new HashSet<uint>();
                remembered.Clear();
                despawn = alive.ToList();
                alive.Clear();
                campers.Clear();
                PresentCount = 0;
            }

            foreach (var c in despawn)
            {
                try
                {
                    if (c != null && !c.IsDestroyed && !c.IsDead)
                        c.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[FRAYS] {Def.Id}: despawn threw", ex);
                }
            }

            PutOutFlames();
            Rename(UnlitName);
            RemoveWisps();
            RemoveShrine();
            RefreshMarkers();

            if (madePermaload && !FraysManager.Fires.Any(f => f != this && f.Lit && f.Landblock == Landblock))
                Landblock.Permaload = false;

            madePermaload = false;
            KindredColour = null;

            var end = Time.GetUnixTime();

            foreach (var c in report)
                FinishCamp(c, end, reason);

            foreach (var p in tell)
            {
                FraysManager.Tell(p, $"The campfire in {Def.RoomName} goes out ({reason}). Your Heat here is gone.");

                // You are in the dungeon fellowship only while you hold a fire.
                if (p.Fellowship != null && FraysManager.IsDungeonFellowship(p.Fellowship) && FraysManager.FireOwnedBySolo(p.Guid.Full) == null)
                {
                    FraysManager.Tell(p, "Without a campfire you leave the Frays fellowship.");
                    p.FellowshipQuit(false, system: true);
                }
            }

            var people = string.Join("; ", report.Select(c => $"{c.Name} peak {c.PeakLevel}, {c.Kills} kills, {c.Deaths} deaths, {c.XpPaid:N0} xp, {c.LumPaid:N0} lum, {c.EmbersEarned:0} embers"));

            log.Info($"[FRAYS] {Def.Id} 0x{Instance:X8} out: {reason}; {Kills} kills, {OwnerDeaths} deaths, {LeashReturns} leash returns, {ZoneEscapes} zone escapes, farthest {FarthestFromFire:0.0} m. {people}");
        }

        // ---- the end of a camp: report and bests --------------------------------------------------------------

        /// <summary>A camper's camp is over: the camp report (saved for /frays report), the leaderboard bests.</summary>
        private void FinishCamp(FraysCamper c, double now, string reason)
        {
            try
            {
                // the retake cooldown counts from here (not when staff put the fire out or the dungeon unloaded)
                if (FraysRules.CountsForRetake(reason))
                    campEndedAt[c.Guid] = now;

                // Counted before the online check: a camp that ends while its player is offline still happened.
                FraysMetrics.RecordCampFinished((now - c.StartedAt) / 60.0, c.PeakLevel);

                // the time log (before the online check too)
                    FraysSessions.Save(this, c, now, reason);

                if (PlayerManager.GetOnlinePlayer(c.Guid) is not Player p)
                    return;

                if (p.FraysMods?.Fire == this)
                    p.FraysMods = null;

                var hours = Math.Max(1.0 / 60, (now - c.StartedAt) / 3600.0);
                var rec = FraysManager.Record(p);
                rec.RollWeek(FraysWeek.Current);

                // A group camp scores on the group boards only; the solo boards stay solo.
                if (Def.Group)
                {
                    rec.BestGroupHeat = Math.Max(rec.BestGroupHeat, c.PeakLevel);
                    rec.WeekBestGroupHeat = Math.Max(rec.WeekBestGroupHeat, c.PeakLevel);
                }
                else
                {
                    rec.BestHeat = Math.Max(rec.BestHeat, c.PeakLevel);
                    rec.WeekBestHeat = Math.Max(rec.WeekBestHeat, c.PeakLevel);
                }

                if (c.BestSnarlSeconds > 0 && !Def.Group)
                {
                    rec.BestSnarlSeconds = rec.BestSnarlSeconds <= 0 ? c.BestSnarlSeconds : Math.Min(rec.BestSnarlSeconds, c.BestSnarlSeconds);
                    rec.WeekBestSnarlSeconds = rec.WeekBestSnarlSeconds <= 0 ? c.BestSnarlSeconds : Math.Min(rec.WeekBestSnarlSeconds, c.BestSnarlSeconds);
                }

                // Run score: the weekly and all-time boards, and a line against the best run so far.
                var score = ClimbScore(c);
                var bestBefore = Def.Group ? rec.BestGroupRunScore : rec.BestRunScore;

                if (Def.Group)
                {
                    rec.BestGroupRunScore = Math.Max(rec.BestGroupRunScore, score);
                    rec.WeekBestGroupRunScore = Math.Max(rec.WeekBestGroupRunScore, score);
                    PayScoreTier(p, rec);   // the group score counts toward the same weekly tier
                }
                else
                {
                    rec.BestRunScore = Math.Max(rec.BestRunScore, score);
                    rec.WeekBestRunScore = Math.Max(rec.WeekBestRunScore, score);
                    PayScoreTier(p, rec);
                }

                if (Def.Group)
                    FraysGroupRuns.Note($"{Def.Id}:{LitAt:0}", Def.RoomName, p.Name, c.PeakLevel, score);

                rec.SnarlKills += c.SnarlKills;
                rec.LastReport = FraysReport.Build(Def.RoomName, c, now, hours, reason) + "\n" + FraysReport.RunLines(c, score, bestBefore);
                Achieve(p, rec);   // saves the record too (the run-score achievement)

                FraysManager.Tell(p, $"Your camp at {Def.RoomName} is over ({reason}): peak Heat {c.PeakLevel}, {c.Kills} kills, run score {score:N0}{(score > bestBefore && bestBefore > 0 ? " - a new best!" : "")}. /frays report shows the full camp report.");
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] {Def.Id}: camp report for {c.Name} threw", ex);
            }
        }

        /// <summary>A camper's climb score as it stands (FraysScore.ClimbScore with the score_* settings).</summary>
        public static long ClimbScore(FraysCamper c)
        {
            var steps = FraysScore.StepMinutes(c.HeatReachedMinute, c.StartLevel, c.PeakLevel);
            var time = FraysScore.TimeBonus(steps, FraysSettings.Number(FraysSettings.ScoreTimePoints), FraysSettings.Number(FraysSettings.ScoreParMinutes));

            return FraysScore.ClimbScore(c.PeakLevel, c.Kills, c.SnarlKills, c.GildedKills, c.Deaths, time,
                FraysSettings.Number(FraysSettings.ScoreHeatPoints), FraysSettings.Number(FraysSettings.ScoreKillPoints),
                FraysSettings.Number(FraysSettings.ScoreSnarlPoints), FraysSettings.Number(FraysSettings.ScoreGildedPoints),
                FraysSettings.Number(FraysSettings.ScoreDeathPoints));
        }

        /// <summary>
        /// The weekly climb reward: the week's best solo score earns Bronze, Silver or Gold, paid once in
        /// Vault Keys (no Embers); a higher tier later in the week pays the difference. One track: the
        /// week's best solo OR group run score, so at most the Gold keys a week. Titles stay on the solo board. The caller saves the record.
        /// </summary>
        private static void PayScoreTier(Player p, FraysPlayerRecord rec)
        {
            var best = Math.Max(rec.WeekBestRunScore, rec.WeekBestGroupRunScore);
            var paid = rec.WeekScoreTier;
            var tier = FraysScore.ScoreTier(best, FraysSettings.Number(FraysSettings.ScoreBronze),
                FraysSettings.Number(FraysSettings.ScoreSilver), FraysSettings.Number(FraysSettings.ScoreGold));

            if (tier <= paid)
                return;

            var keys = FraysScore.KeysOwed(paid, tier, FraysSettings.Int(FraysSettings.ScoreBronzeKeys),
                FraysSettings.Int(FraysSettings.ScoreSilverKeys), FraysSettings.Int(FraysSettings.ScoreGoldKeys));

            rec.WeekScoreTier = tier;

            if (keys > 0)
                FraysLongTerm.Give(p, FraysLongTerm.DreamKey, keys,
                    $"Weekly climb reward: {FraysScore.TierName(tier)} (best run score this week, solo or group, {best:N0}). {keys} Vault Key{(keys == 1 ? "" : "s")}.");
        }

        // ---- owners -------------------------------------------------------------------------------------------

        /// <summary>The camp's one owner, when they are online.</summary>
        private List<Player> OnlineOwners()
        {
            if (Def.Group)
                return OnlineGroupOwners();

            var list = new List<Player>();

            if (SoloOwner != 0 && PlayerManager.GetOnlinePlayer(SoloOwner) is Player owner)
                list.Add(owner);

            return list;
        }

        /// <summary>
        /// The camp's owner, by guid: true while they are online, false once they have logged off. Logging off takes a
        /// player out of their fellowship, so a logged-off owner keeps the fire for the grace time and is put back in the
        /// dungeon fellowship when they return; an owner who is online but has left the dungeon fellowship has given up
        /// the fire, at once.
        /// </summary>
        private readonly Dictionary<uint, bool> remembered = new Dictionary<uint, bool>();

        /// <summary>The last time the owner was in this dungeon (this landblock copy).</summary>
        private double lastOwnerInDungeonAt;

        /// <summary>The last time a living owner stood in this fire's zone.</summary>
        private double lastOwnerAtFireAt;

        /// <summary>Recomputes the owner set (the one owner, while online and in the dungeon fellowship). True when it changed.</summary>
        private bool RefreshOwners(double now)
        {
            if (Def.Group)
                return RefreshGroupOwners(now);

            var next = new HashSet<uint>();

            if (SoloOwner != 0)
            {
                var p = PlayerManager.GetOnlinePlayer(SoloOwner);
                var inFrays = FraysManager.InFraysFellowship(p, Instance);

                if (p == null)
                    remembered[SoloOwner] = false;  // logged off: keeps the fire for the grace time
                else if (inFrays)
                {
                    remembered[SoloOwner] = true;
                    next.Add(SoloOwner);
                }
                else if (remembered.TryGetValue(SoloOwner, out var wasOnline) && !wasOnline)
                {
                    // Back from a logoff (which took them out of the fellowship): put them back in it.
                    if (FraysManager.JoinDungeonFellowship(p, this))
                    {
                        remembered[SoloOwner] = true;
                        next.Add(SoloOwner);
                        FraysManager.Tell(p, $"You are back. The campfire in {Def.RoomName} is still yours.");
                    }
                    else
                        remembered.Remove(SoloOwner);
                }
                else if (p.Fellowship == null && !FraysManager.FraysFellowships(Instance).Any() && !remembered.ContainsKey(SoloOwner))
                {
                    // Just lit, and the dungeon fellowship could not be made: they still hold the fire, alone.
                    remembered[SoloOwner] = true;
                    next.Add(SoloOwner);
                }
                else
                {
                    // Online but out of the dungeon fellowship: they left it, and gave up the fire.
                    remembered.Remove(SoloOwner);
                    FraysManager.Tell(p, $"You left the Frays fellowship, so you give up your campfire in {Def.RoomName}.");
                }
            }

            if (next.SetEquals(ownerGuids))
                return false;

            ownerGuids = next;
            return true;
        }

        /// <summary>Owners who still hold a place in this camp (online members, and owners who logged off within the grace).</summary>
        public int RememberedCount
        {
            get { lock (Sync) return remembered.Count; }
        }

        // ---- the tick ----------------------------------------------------------------------------------------

        /// <summary>Once a second, on the landblock's thread.</summary>
        private double nextBossPulse;

        /// <summary>
        /// The bosses glow so they read at a glance (the Snarl, Great Snarl and gate creatures
        /// look different): every 4 seconds the Snarl pulses red and a Gilded creature sparkles gold.
        /// </summary>
        private void PulseBosses(double now)
        {
            if (now < nextBossPulse)
                return;

            nextBossPulse = now + 4;
            List<Creature> bosses;

            lock (Sync)
                bosses = alive.Where(m => m != null && !m.IsDead && !m.IsDestroyed && m.FraysTag != null && (m.FraysTag.Snarl || m.FraysTag.SnarlAdd || m.FraysTag.Rich)).ToList();

            // A Gilded creature (a rich point's) sparkles gold.
            foreach (var m in bosses)
                FraysManager.Effect(m, m.FraysTag.Snarl || m.FraysTag.SnarlAdd ? PlayScript.SpecialStateRed : m.FraysTag.Rich ? PlayScript.EnchantUpYellow
                    : PlayScript.ShieldUpYellow);

            // A rich point about to come back glows gold at its spot.
            foreach (var w in richWarnings.Values)
                if (w != null && !w.IsDestroyed)
                    FraysManager.Effect(w, PlayScript.EnchantUpYellow);
        }

        // ---- the fixed layout's markers: rich point warnings and the Ember Shrine (owner only) ----------------

        /// <summary>A gold glow at each rich point about to come back, by point index.</summary>
        private readonly Dictionary<int, WorldObject> richWarnings = new Dictionary<int, WorldObject>();

        private WorldObject shrine;

        /// <summary>The Ember Shrines standing now, by object guid, for a click on one (FraysManager.TryUseCampfire).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, FraysFire> shrines = new System.Collections.Concurrent.ConcurrentDictionary<uint, FraysFire>();

        /// <summary>An owner-only display object at a layout point, on the ground (or the plateau floor).</summary>
        private WorldObject PlaceMarker(Player owner, int index, uint setup, float scale, string name, float lift, bool usable = false) => PlaceAt(owner, PointPos(index), setup, scale, name, lift, usable);

        /// <summary>An owner-only display object at a spot, on the ground (or the plateau floor).</summary>
        private WorldObject PlaceAt(Player owner, Vector3 p, uint setup, float scale, string name, float lift, bool usable = false)
        {
            if (owner == null || setup == 0)
                return null;

            var at = new Position(((uint)Def.Landblock << 16) | FraysRules.OutdoorCell(true, 0, p), p.X, p.Y, Def.FirePos.Z, 0, 0, 0, 1, Instance);
            at = Grounded(at, lift);

            if (Def.FloorAbove != null)
                at.PositionZ = Def.FirePos.Z + lift;

            var wo = WorldObjectFactory.CreateNewWorldObject(FraysDefs.ColdCampfireWcid);

            if (wo == null)
                return null;

            wo.SetupTableId = setup;
            wo.ObjScale = scale;
            wo.Name = name;
            wo.Location = at;
            wo.FraysTransient = true;
            wo.FraysOnlyFor = owner.Guid.Full;
            wo.TimeToRot = -1;
            // Set before it enters the world: the client keeps what the create message said (a shrine made usable
            // just after entering stays "not usable" to the client, so a click on a cold one never reaches the server).
            wo.ItemUseable = usable ? ACE.Entity.Enum.Usable.Remote : ACE.Entity.Enum.Usable.No;
            wo.RadarBehavior = usable ? ACE.Entity.Enum.RadarBehavior.ShowAlways : ACE.Entity.Enum.RadarBehavior.ShowNever;

            if (wo.EnterWorld())
                return wo;

            wo.Destroy();
            return null;
        }

        // ---- /frays admin zone rings: each pack's aggro and tether rings, for the admin alone ----

        private readonly List<WorldObject> adminRings = new List<WorldObject>();

        /// <summary>Shows (or with <paramref name="on"/> false removes) a ring of wisps at aggro_range and pack_tether round every pack point. Returns what to tell the admin.</summary>
        public string AdminRings(Player admin, bool on)
        {
            foreach (var w in adminRings)
                if (w != null && !w.IsDestroyed)
                    w.Destroy();

            adminRings.Clear();
            var l = Layout;

            if (!on)
                return "Pack rings removed.";

            if (l == null)
                return "No fixed layout here: rings are for solo outdoor camps with fixed_layout on.";

            var setup = FraysRules.ParseSetup(FraysSettings.Text(FraysSettings.WispSetup));

            if (setup == 0)
                return "No wisp model is set (frays_wisp_setup).";

            var rings = new[] { (R: FraysSettings.Number(FraysSettings.AggroRange), Scale: 0.4f, What: "aggro"), (R: FraysSettings.Number(FraysSettings.PackTether), Scale: 0.8f, What: "tether") };

            for (var i = 0; i < l.Roles.Count; i++)
            {
                if (l.Spawnless(i))
                    continue;

                var p = PointPos(i);

                foreach (var ring in rings.Where(r => r.R > 0))
                    foreach (var (x, y) in FraysGroup.RingSpots(new Vector2(p.X, p.Y), ring.R, 4))
                        if (PlaceAt(admin, new Vector3(x, y, p.Z), setup, ring.Scale, $"Point {i} {ring.What} {ring.R:0} m", 0.3f) is { } wo)
                            adminRings.Add(wo);
            }

            return $"Placed {adminRings.Count} wisps: the small ring is each pack's aggro ({FraysSettings.Number(FraysSettings.AggroRange):0} m), the big one its tether ({FraysSettings.Number(FraysSettings.PackTether):0} m). Only you see them. /frays admin zone rings off removes them.";
        }

        private void PlaceShrine(Player owner)
        {
            RemoveShrine();
            var l = Layout;

            // the shrine stands fixed at its own point; the Gilded packs keep theirs
            if (l == null || l.Shrine < 0 || owner == null || !FraysSettings.Toggle(FraysSettings.ShrineEnabled))
                return;

            var point = l.Shrine;

            shrine = PlaceMarker(owner, point, FraysRules.ParseSetup(FraysSettings.Text(FraysSettings.ShrineSetup)),
                (float)FraysSettings.Number(FraysSettings.ShrineScale), "Ember Shrine", 0f, usable: true);

            if (shrine == null)
                return;

            shrines[shrine.Guid.Full] = this;
            log.Info($"[FRAYS] shrine {Def.Id}: \"{shrine.Name}\" 0x{shrine.Guid.Full:X8} at {shrine.Location} point {point}, only for {owner.Name}, setup 0x{shrine.SetupTableId:X8}, use Remote, walk-in {FraysSettings.Number(FraysSettings.ShrineRadius):0.#} m");
        }

        private void RemoveShrine()
        {
            if (shrine != null)
            {
                shrines.TryRemove(shrine.Guid.Full, out _);

                if (!shrine.IsDestroyed)
                    shrine.Destroy();
            }

            shrine = null;

            foreach (var w in richWarnings.Values)
                if (w != null && !w.IsDestroyed)
                    w.Destroy();

            richWarnings.Clear();
        }

        /// <summary>A click on an Ember Shrine: its owner takes the buff (or hears when it burns again).</summary>
        public static bool TryUseShrine(Player player, WorldObject item)
        {
            if (player == null || item == null || !shrines.TryGetValue(item.Guid.Full, out var fire))
                return false;

            var c = fire.CamperOf(player);

            if (c == null)
                FraysManager.Tell(player, "This Ember Shrine burns for its camp's owner.");
            else
                fire.TakeShrine(c, player, Time.GetUnixTime(), clicked: true);

            return true;
        }

        /// <summary>How far away the owner may use an Ember Shrine without walking to it.</summary>
        public const float ShrineUseRange = 15f;

        /// <summary>
        /// Player_Use, before the walk to the object: an Ember Shrine is used where the player stands, never walked to.
        /// It is owner-only, so there is no reason to make anyone walk. A walk that got cancelled or timed out used to drop
        /// the use with no reply. Every use gets an answer: the buff, "cold for N seconds", "not yours", or "too far".
        /// False when the item is not an Ember Shrine.
        /// </summary>
        public static bool TryUseShrineFromRange(Player player, WorldObject item)
        {
            if (player == null || item == null || !shrines.ContainsKey(item.Guid.Full))
                return false;

            if (item.FraysOnlyFor == player.Guid.Full && player.Location != null && item.Location != null
                && player.Location.Instance == item.Location.Instance)
            {
                var d = player.Location.DistanceTo(item.Location);

                if (d > ShrineUseRange)
                {
                    FraysManager.Tell(player, $"The Ember Shrine is too far away ({d:0} m). Come within {ShrineUseRange:0} m to use it.");
                    return true;
                }
            }

            TryUseShrine(player, item);
            return true;
        }

        /// <summary>The shrine's buff for this week, with its cooldown; a click on cooldown says when it burns again.</summary>
        private void TakeShrine(FraysCamper c, Player p, double now, bool clicked)
        {
            if (now < c.ShrineReadyAt)
            {
                if (clicked)
                    FraysManager.Tell(p, $"The Ember Shrine is cold. It burns again in {Math.Ceiling(c.ShrineReadyAt - now):0} seconds.");
                return;
            }

            var seconds = FraysSettings.Number(FraysSettings.ShrineBuffSeconds);
            c.ShrineBuff = FraysLayout.BuffForWeek(FraysWeek.Current);
            c.ShrineUntil = now + seconds;
            c.ShrineReadyAt = now + FraysSettings.Number(FraysSettings.ShrineCooldownSeconds);
            c.ShrineToldReady = false;

            var name = FraysLayout.BuffName(c.ShrineBuff);
            FraysManager.Tell(p, $"{name} for {seconds:0} seconds: {FraysLayout.BuffText(c.ShrineBuff)}. The shrine burns again in {FraysSettings.Number(FraysSettings.ShrineCooldownSeconds):0} seconds.");
            FraysManager.Shout(p, $"{name}!");
            log.Info($"[FRAYS] shrine {Def.Id}: {p.Name} took {name} ({(clicked ? "clicked" : "walked in")})");
            FraysManager.Effect(p, PlayScript.EnchantUpOrange);

            if (shrine != null && !shrine.IsDestroyed)
                FraysManager.Effect(shrine, PlayScript.EnchantUpOrange);
        }

        /// <summary>
        /// The camp tick's fixed layout part (solo outdoor camps): walking into the shrine, the shrine burning again, and
        /// a gold glow at each rich point in the last rich_warn_seconds before it comes back.
        /// </summary>
        private void TickLayout(FraysCamper c, Player p, double now)
        {
            var l = Layout;

            if (l == null)
                return;

            if (shrine != null && !shrine.IsDestroyed && shrine.FraysOnlyFor == p.Guid.Full && !p.IsDead && p.Location != null
                && shrine.Location != null && p.Location.Instance == shrine.Location.Instance && (p.Location.LandblockId.Raw >> 16) == (shrine.Location.LandblockId.Raw >> 16))
            {
                var dx = p.Location.PositionX - shrine.Location.PositionX;
                var dy = p.Location.PositionY - shrine.Location.PositionY;
                var radius = FraysSettings.Number(FraysSettings.ShrineRadius);

                if (dx * dx + dy * dy <= radius * radius)
                    TakeShrine(c, p, now, clicked: false);
            }

            if (!c.ShrineToldReady && now >= c.ShrineReadyAt)
            {
                c.ShrineToldReady = true;
                FraysManager.Tell(p, "The Ember Shrine burns again.");

                if (shrine != null && !shrine.IsDestroyed)
                    FraysManager.Effect(shrine, PlayScript.EnchantUpOrange);
            }

            var warn = FraysSettings.Number(FraysSettings.RichWarnSeconds);

            foreach (var r in l.Rich)
            {
                var due = c.PointReadyAt.TryGetValue(r, out var at) ? at : 0;
                var soon = warn > 0 && due != double.MaxValue && due > now && due - now <= warn;
                richWarnings.TryGetValue(r, out var marker);

                if (soon && (marker == null || marker.IsDestroyed))
                {
                    marker = PlaceMarker(p, r, FraysRules.ParseSetup(FraysSettings.Text(FraysSettings.WispSetup)),
                        (float)FraysSettings.Number(FraysSettings.WispScale), "Gilded pack returning", (float)FraysSettings.Number(FraysSettings.WispHeight));

                    if (marker != null)
                    {
                        richWarnings[r] = marker;
                        FraysManager.Effect(marker, PlayScript.EnchantUpYellow);
                    }
                }
                else if (!soon && marker != null)
                {
                    if (!marker.IsDestroyed)
                        marker.Destroy();

                    richWarnings.Remove(r);
                }
            }
        }

        public void Tick(double now)
        {
            if (!Lit)
                return;

            PulseBosses(now);

            string outReason = null;
            bool ownersChanged;
            List<Player> present;

            lock (Sync)
            {
                ownersChanged = RefreshOwners(now);

                var online = OnlineOwners();
                present = online.Where(p => !p.IsDead && InZone(p.Location, Margin, 0)).ToList();
                PresentCount = present.Count;

                // Anywhere in this dungeon (this landblock copy) keeps the fire; leaving it, or logging off, starts the grace.
                var inDungeon = online.Count(p => p.Location != null && p.Location.Instance == Instance && FraysDefs.FrayOfLandblock((ushort)(p.Location.LandblockId.Raw >> 16)) == Def.FrayKey);

                // The away clock stands still while the Fray's Great Snarl is due or up (and for the walk back), so going to fight it never costs the fire.

                var greatHolds = FraysFrayWide.GreatHoldsCamps(Def.FrayKey, now);

                if (inDungeon > 0 || greatHolds)
                    lastOwnerInDungeonAt = now;

                // Standing in the zone, not just somewhere in the Fray, keeps the fire, so a player
                // who walks away cannot hold a campfire for hours.
                if (present.Count > 0 || greatHolds)
                    lastOwnerAtFireAt = now;

                if (FraysRules.ShouldGoOut(remembered.Count, inDungeon, now - lastOwnerInDungeonAt, FraysSettings.Number(FraysSettings.AwaySeconds)))
                    outReason = remembered.Count == 0 ? "everyone has left the fellowship" : $"no owner has been in the Fray for {FraysSettings.Number(FraysSettings.AwaySeconds) / 60:0} minutes";
                else if (FraysRules.AwayFromFireTooLong(now - lastOwnerAtFireAt, FraysSettings.Number(FraysSettings.FireAwaySeconds)))
                    outReason = $"no owner has been at the fire for {FraysSettings.Number(FraysSettings.FireAwaySeconds) / 60:0} minutes";

                if (outReason == null && Def.Group)
                    outReason = GroupOutReason(now);
            }

            if (outReason != null)
            {
                Extinguish(outReason);
                return;
            }

            if (ownersChanged)
                RefreshViewers();

            // Outdoors: tell the owner when they step out of their zone (the camp pauses) and back in.
            if (Def.Outdoor && SoloOwner != 0 && PlayerManager.GetOnlinePlayer(SoloOwner) is Player zoneOwner)
            {
                var here = present.Any(p => p.Guid.Full == SoloOwner);

                if (here != ownerInZone)
                {
                    ownerInZone = here;
                    FraysManager.Tell(zoneOwner, here ? $"You are back in your zone, {Def.RoomName}." : $"You step out of your zone, {Def.RoomName}: your camp pauses until you return (the fire waits {FraysSettings.Number(FraysSettings.AwaySeconds) / 60:0} minutes).");
                }
            }

            var spawnFor = new List<FraysCamper>();

            lock (Sync)
            {
                alive.RemoveAll(c => c == null || c.IsDead || c.IsDestroyed);

                // Outdoors: a point waits while its pack lives. A pack gone some other way than a credited kill (despawned,
                // leashed off, killed by someone else) used to leave its point waiting for good; once every point was,
                // the camp never spawned again. Freed here to rest.
                if (Def.Outdoor)
                    foreach (var c in campers.Values)
                        foreach (var i in FraysRules.OrphanedPoints(c.PointReadyAt, alive.Where(m => m.FraysTag != null && (Def.Group || m.FraysTag.ForPlayer == c.Guid)).Select(m => m.FraysTag.SpawnIndex)))
                            c.PointReadyAt[i] = now + PointRestFor(c, i, now);

                var presentGuids = new HashSet<uint>(present.Select(p => p.Guid.Full));

                // A group camp: one member present keeps the shared Heat (and its creature stream) first.
                if (Def.Group)
                    EnsureGroupLead(present, now);

                foreach (var p in present)
                {
                    // just left a camp (retake_cooldown_seconds): no new camp here yet, so leaving is no way out of a hard one
                    if (!campers.ContainsKey(p.Guid.Full) && RetakeRefusal(p, now) is string wait)
                    {
                        if (!retakeToldAt.TryGetValue(p.Guid.Full, out var told) || now - told >= 30)
                        {
                            retakeToldAt[p.Guid.Full] = now;
                            FraysManager.Tell(p, wait);
                        }

                        continue;
                    }

                    var c = CamperFor(p, now);
                    TickCamper(c, p, now);

                    if ((now >= c.NextSpawnAt || DueRichPoint(c, p, now) >= 0) && !IsFollower(c))
                        spawnFor.Add(c);
                }

                if (Def.Group)
                    MirrorGroupHeat();

                // Away too long (or no longer an owner): that camp, and its Heat, end.
                foreach (var gone in campers.Values.Where(c => !presentGuids.Contains(c.Guid)).ToList())
                {
                    gone.PresentLastTick = false;

                    // Away from the fire: the camp's modifiers stop applying until they are back.
                    if (PlayerManager.GetOnlinePlayer(gone.Guid) is Player away && away.FraysMods?.Fire == this)
                        away.FraysMods = null;

                    if (!ownerGuids.Contains(gone.Guid) || (now - gone.LastPresentAt >= FraysSettings.Number(FraysSettings.AwaySeconds) && !FraysFrayWide.GreatHoldsCamps(Def.FrayKey, now)))
                    {
                        campers.Remove(gone.Guid);
                        FinishCamp(gone, now, "you were away too long");
                    }
                }
            }

            foreach (var c in spawnFor)
                SpawnFor(c, now, present);

            TickWake(present);
            FraysFrayWide.Sparkle(this, now);
            FraysLongTerm.AscensionSpark(this, now);
        }

        /// <summary>When each player's last camp ended, for retake_cooldown_seconds.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, double> campEndedAt = new System.Collections.Concurrent.ConcurrentDictionary<uint, double>();

        /// <summary>The last "you cannot take a camp yet" line each player was told, so it is said every 30 seconds, not every tick.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, double> retakeToldAt = new System.Collections.Concurrent.ConcurrentDictionary<uint, double>();

        /// <summary>
        /// Why <paramref name="p"/> cannot take or join a camp yet, or null: a camp of theirs ended under
        /// retake_cooldown_seconds ago (staff are never held).
        /// </summary>
        public static string RetakeRefusal(Player p, double now)
        {
            if (p == null || p.Session?.AccessLevel >= AccessLevel.Advocate || !campEndedAt.TryGetValue(p.Guid.Full, out var at))
                return null;

            var wait = FraysRules.RetakeWait(at, FraysSettings.Number(FraysSettings.RetakeCooldownSeconds), now);
            return wait > 0 ? $"You left a camp a short while ago. You can take or join a campfire again in {Math.Ceiling(wait):0} seconds." : null;
        }

        private FraysCamper CamperFor(Player p, double now)
        {
            if (campers.TryGetValue(p.Guid.Full, out var c))
                return c;

            var rec = FraysManager.Record(p);
            var floor = FraysSettings.Int(FraysSettings.HeatStart);
            var wanted = rec.WantedStart(Def.Group);
            var start = Math.Max(floor, FraysRules.StartLevel(wanted, rec.RatingFor(Def.Group), FraysPerks.CapFor(rec)));

            c = new FraysCamper
            {
                Guid = p.Guid.Full,
                Rating = rec.RatingFor(Def.Group),
                Name = p.Name,
                Level = Math.Min(start, FraysPerks.CapFor(rec)),
                LastKillAt = now,
                NextSpawnAt = now + 2,
                LastPresentAt = now,
                NextRatingAt = now + 60,
                NextPayAt = now + 60,
                StartedAt = now,
                NextSurgeAt = now + FraysSettings.Number(FraysSettings.SurgeEverySeconds),
                NextManaAt = now + 5,
            };

            // The Snarl is always on, every camp is the medium mix and loot is never targeted (no shape,
            // Snarl Pact or bias choices). A group camp's packs still mix heavy, medium
            // and light point by point (PackShape).
            c.Snarl = SnarlPact.Normal;
            c.Shape = FraysShape.Medium;
            c.Bias = new List<string>();
            // A group camp: the keeper starts at the lowest member's start; everyone else joins at the shared Heat.
            if (Def.Group)
                c.Level = GroupStartFor(p);

            c.StartLevel = c.Level;
            c.PeakLevel = c.Level;
            c.HeatLog.Add((0, c.Level));
            // A camp that starts high is owed the boons for the levels it skipped (so one started at Heat 9 does not
            // wait until 12). A group camp's boons are the keeper's, so only its first camper is owed them.
            if (!Def.Group || campers.Count == 0)
                c.OffersOwed += FraysBoons.StartOffers(c.Level, FraysSettings.Int(FraysSettings.BoonEvery));
            // the camp is being taken (its first camper): every point fields its pack on the first spawn
            c.FillOnTake = FraysSettings.Toggle(FraysSettings.FillOnTake) && campers.Count == 0;
            campers[c.Guid] = c;

            var collected = FraysBoons.All.Count(b => FraysCollection.Collected(rec, b.Key));
            var heatLine = $"Your Heat starts at {c.Level}. Boons collected: {collected} of {FraysBoons.All.Count} (/fray collection).";
            FraysManager.Tell(p, $"You join the camp at {Def.RoomName}. {heatLine}");
            FraysFrayWide.OnCamp(p, this);
            FraysManager.Tell(p, FraysWeek.Summary());
            return c;
        }

        private void TickCamper(FraysCamper c, Player p, double now)
        {
            var rec = FraysManager.Record(p);
            var cap = FraysPerks.CapFor(rec);

            if (c.AutoCap >= 0 && now >= c.AutoCapUntil)
                c.AutoCap = -1;

            c.Limit = FraysRules.PersonalLimit(p.Name, rec.HeatStopped ? FraysMode.Hold : FraysMode.Auto, rec.Limit, rec.RatingFor(Def.Group), c.Level, cap, c.AutoCap);

            if (c.Surge != null && now >= c.SurgeUntil)
                c.Surge = null;

            FraysHeatShifts.Tick(this, c, p, now);

            // The camp's modifiers: the week's affixes, Fray-only proof sets.
            c.Mods = FraysCampMods.Build(p, c.Snarl, FraysWeek.Shift, c.Surge);
            FraysLongTerm.Apply(p, rec, c.Mods, FraysFamilies.For(Def.FrayKey));
            FraysBoons.Apply(c.Boons, c.Mods, k => FraysCollection.Rank(rec, k), p.GetClassAbilityRank);
            FraysManager.ApplyBlessing(p, c.Mods);
            FraysSky.ApplyBanner(c.Mods, Def.Group, now, SkyBannerUntil);

            // The Ember Shrine's buff, and the fixed layout's shrine and rich point warnings (solo outdoor camps).
            if (now < c.ShrineUntil)
                FraysLayout.Apply(c.ShrineBuff, c.Mods);

            TickLayout(c, p, now);

                FraysSessions.Tick(c, p, now);

            // A group camp's boons belong to its shared Heat, kept by one member.
            if (!IsFollower(c))
                TickBoons(c, p, rec, now);
            c.Mods.Fire = this;
            p.FraysMods = c.Mods;

            // The mender companion: a share of max health every 10 seconds.
            if (c.Mods.HealPer10s > 0 && now >= c.NextMendAt && !p.IsDead)
            {
                c.NextMendAt = now + 10;
                var heal = (int)Math.Round(p.Health.MaxValue * c.Mods.HealPer10s);

                if (heal > 0 && p.Health.Current < p.Health.MaxValue)
                    p.UpdateVitalDelta(p.Health, heal);
            }

            // Deep Well: a share of max mana every 5 seconds.
            if (c.Mods.ManaPer5s > 0 && now >= c.NextManaAt)
            {
                c.NextManaAt = now + 5;
                var gain = (int)Math.Round(p.Mana.MaxValue * c.Mods.ManaPer5s);

                if (gain > 0 && p.Mana.Current < p.Mana.MaxValue)
                    p.UpdateVitalDelta(p.Mana, gain);
            }

            // Decay: only after a stretch without a kill, so a camp that keeps killing never cools. Slow Burn and
            // Smoulder change its pace; Still Night stops it.
            // A group camp cools once, on the shared Heat's keeper.
            var decay = IsFollower(c) || c.Mods.NoDecay || c.Mods.DecayMult <= 0 ? 0 : FraysSettings.Number(FraysSettings.HeatDecaySeconds) / c.Mods.DecayMult;
            var grace = FraysSettings.Number(FraysSettings.HeatDecayGraceSeconds);

            // Say so before the Heat starts to drop, and once when it does, so the camper knows where they stand.
            var hasHeat = c.Level > 0 || c.Progress > 0;
            var cool = c.Limit.Frozen || decay <= 0 || !hasHeat ? 0 : FraysRules.DecayRuns(c.LastKillAt, now, grace) ? 2
                : grace >= 20 && FraysRules.DecayRuns(c.LastKillAt, now, grace - 10) ? 1 : 0;

            if (cool > c.CoolStage)
            {
                var said = cool == 1 ? "Your fire is cooling: kill within 10 seconds or your Heat starts to drop." : "Your Heat is dropping. Kill to stop it.";
                TellCamp(p, said);
                ShoutCamp(p, cool == 1 ? "Your fire is cooling" : "Your Heat is dropping");
            }
            c.CoolStage = cool;

            // The top Heats burn down all the time (heat_upkeep_by_heat): kills must outpace it, Hold steady too.
            var upkeep = IsFollower(c) || p.IsDead ? 0 : FraysRules.HeatUpkeep(c.Level) * (Def.Group ? GroupPresent : 1);

            if (upkeep > 0)
            {
                c.UpkeepOwed += upkeep / 60;   // the camp tick runs once a second

                if (c.UpkeepOwed >= 1)
                {
                    var whole = (int)Math.Floor(c.UpkeepOwed);
                    c.UpkeepOwed -= whole;
                    AddPoints(c, p, -whole);
                }
            }
            else
                c.UpkeepOwed = 0;

            if (!c.Limit.Frozen && decay > 0 && FraysRules.DecayRuns(c.LastKillAt, now, grace))
            {
                c.DecayAccumulator += 1;

                while (c.DecayAccumulator >= decay)
                {
                    c.DecayAccumulator -= decay;
                    AddPoints(c, p, -1);
                }
            }
            else
                c.DecayAccumulator = 0;

            c.PresentLastTick = true;
            c.LastPresentAt = now;

            // Outdoors: tell the camper when a different quarter of their zone starts to flare.
            if (Def.Outdoor)
            {
                var hotNow = FraysRules.HotQuadrant(now, Def.Id, FraysSettings.Number(FraysSettings.HotSpotMinutes));

                if (hotNow >= 0 && hotNow != c.HotQuadrant)
                {
                    c.HotQuadrant = hotNow;
                    FraysManager.Tell(p, $"The {FraysRules.QuadrantNames[hotNow]} of {Def.RoomName} flares: its points rest half as long and field one more creature.");
                }
            }
            c.PlayerLevel = p.Level ?? 1;

            // Rating, fighting minutes only: each minute counts when the player was here and alive, not holding, and
            // cleared enough of what their stream spawned. The rating is the lowest Heat over the last N counted
            // minutes; a death clears the run. Idle minutes neither count nor clear it.
            if (!p.IsDead)
                c.MinutePresentTicks++;

            c.MinuteMinLevel = Math.Min(c.MinuteMinLevel, c.Level);

            if (now >= c.NextRatingAt)
            {
                c.NextRatingAt = now + 60;

                var counts = FraysRules.RatingMinuteCounts(rec.Mode == FraysMode.Hold, c.MinutePresentTicks >= 45,
                    c.MinuteSpawnedWork, c.MinuteRemovedWork, FraysSettings.Number(FraysSettings.RatingMinCleared));

                var need = Math.Max(1, (int)Math.Round(FraysSettings.Number(FraysSettings.RatingHoldMinutes)));

                if (counts)
                {
                    FraysLongTerm.Bounty(p, "w_heat", c.MinuteMinLevel != int.MaxValue && c.MinuteMinLevel >= 10 ? 1 : 0);
                    if (Def.Group)
                        FraysLongTerm.Bounty(p, "w_group", 1);
                    FraysLongTerm.Bounty(p, "heat12", c.MinuteMinLevel != int.MaxValue && c.MinuteMinLevel >= 12 ? 1 : 0);
                    FraysLongTerm.Bounty(p, "heat8", c.MinuteMinLevel != int.MaxValue && c.MinuteMinLevel >= 8 ? 1 : 0);
                    c.RatingRun.Enqueue(c.MinuteMinLevel == int.MaxValue ? c.Level : c.MinuteMinLevel);

                    while (c.RatingRun.Count > need)
                        c.RatingRun.Dequeue();

                    if (c.RatingRun.Count >= need)
                    {
                        // A group camp raises the group rating only; solo and group ratings are earned apart.
                        var was = rec.RatingFor(Def.Group);
                        var raised = FraysRules.RaiseRating(was, c.RatingRun.Min());

                        if (raised > was)
                        {
                            if (Def.Group)
                                rec.GroupRating = raised;
                            else
                                rec.Rating = raised;

                            FraysManager.SaveRecord(p, rec);
                            FraysManager.Tell(p, $"You fought at Heat {raised} for {need} minutes. Your {(Def.Group ? "group " : "")}Heat rating is now {raised}.");
                        }
                    }
                }

                c.MinutePresentTicks = 0;
                c.MinuteSpawnedWork = 0;
                c.MinuteRemovedWork = 0;
                c.MinuteMinLevel = int.MaxValue;
            }

            // Camp Embers, for standing by your lit fire.
            if (DistanceToFire(p.Location) <= FraysSettings.Number(FraysSettings.EmbersCampRadius))
            {
                c.CampEmbers += FraysSettings.Number(FraysSettings.EmbersCampPerMinute) / 60.0 * c.Mods.Embers;

                if (c.CampEmbers >= 1)
                {
                    var whole = Math.Floor(c.CampEmbers);
                    c.CampEmbers -= whole;
                    var campPaid = FraysManager.PayEmbers(p, whole);
                    c.EmbersEarned += campPaid;
                    FraysMetrics.AddPositive(FraysMetrics.EmbersCamp, campPaid);
                }
            }

            // Static XP: a fixed rate for each active second, paid once a minute straight to the player.
            if (FraysRules.IsActive(c.LastActiveAt, now, FraysSettings.Number(FraysSettings.ActivitySeconds)))
                c.ActiveSeconds += 1;

            if (now >= c.NextPayAt)
            {
                c.NextPayAt = now + 60;

                // The base rate.
                // Version 1: XP follows the character's level (by the XP curve), Heat multiplies XP and lum.
                var heatMult = FraysCurve.Income(c.Level, p.Level ?? 1);   // Threads' XP ladder at the Heat's creature level
                // xp_kill_share of it is paid per kill instead (KillPay); the rest stays per active minute.
                var minuteShare = 1 - Math.Clamp(FraysSettings.Number(FraysSettings.XpKillShare), 0, 1);
                var xp = FraysRules.ForSeconds(LevelXpPerHour(p.Level ?? 1) * heatMult * minuteShare, c.ActiveSeconds);
                var lum = FraysRules.ForSeconds(LevelLumPerHour(p.Level ?? 1) * heatMult * minuteShare, c.ActiveSeconds);
                c.ActiveSeconds = 0;

                // The camp report's Heat line: one sample a minute.
                c.HeatLog.Add(((now - c.StartedAt) / 60.0, c.Level));

                if (xp > 0)
                {
                    p.GrantXP(xp, XpType.Emote, ShareType.None);
                    c.XpPaid += xp;
                    FraysMetrics.XpGranted.Add(xp);
                }

                // No luminance flag here: earned lum banks for every player (Player_Luminance.GrantLuminance).
                if (lum > 0)
                {
                    p.GrantLuminance(lum, XpType.Emote, ShareType.None);
                    c.LumPaid += lum;
                    FraysMetrics.LumGranted.Add(lum);
                }
            }
        }

        /// <summary>The Heat-0 luminance an hour for a character of <paramref name="level"/> (lum_by_level; none when it is blank).</summary>
        public static double LevelLumPerHour(int level) => Math.Max(0, FraysRules.TableRate(FraysSettings.Text(FraysSettings.LumByLevel), level));

        /// <summary>
        /// The Heat-0 XP an hour for a character of <paramref name="level"/> (xp_by_level; none when it is blank).
        /// </summary>
        public static double LevelXpPerHour(int level) => Math.Max(0, FraysRules.TableRate(FraysSettings.Text(FraysSettings.XpByLevel), level));

        private void AddPoints(FraysCamper c, Player p, int points)
        {
            // Frozen (Hold steady): kills still fill the current level but never climb out of it.
            var max = c.Limit.Frozen ? c.Level : Math.Max(c.Limit.Level, 0);

            if (points > 0 && c.Level > max)
                max = c.Level;

            var basePoints = FraysSettings.Int(FraysSettings.HeatPointsBase);
            var perLevel = FraysSettings.Int(FraysSettings.HeatPointsPerLevel);
            var (lvl, prog) = FraysRules.AddPoints(c.Level, c.Progress, points, max, basePoints, perLevel);
            var need = FraysRules.PointsToNext(lvl, basePoints, perLevel);
            var step = FraysSettings.Int(FraysSettings.HeatProgressStep);

            if (lvl > c.Level)
            {
                var next = FraysRules.NextMilestone(lvl, Math.Max(c.Limit.Level, lvl), FraysSettings.Int(FraysSettings.SpawnPackEvery));

                TellCamp(p, $"Your campfire burns hotter: Heat {lvl} {FraysRules.Bar((double)prog / need)}.{(next.Length > 0 ? " Next - " + next : "")}");

                // Each Heat this camp has not reached before says once how much fiercer it is.
                for (var h = Math.Max(c.Level, c.PeakLevel) + 1; h <= lvl; h++)
                {
                    TellCamp(p, FraysCurve.FierceLine(h));
                    var shiftLine = FraysHeatShifts.OnLine(h);

                    if (shiftLine.Length > 0)
                    {
                        TellCamp(p, shiftLine);
                        ShoutCamp(p, shiftLine);
                    }
                }

                // Seen, not just read: center text, a flame swirl, and every 5th level a flash, the fire flaring
                // and a line in the Fray's feed.
                var milestone = FraysRules.HeatMilestone(c.Level, lvl);
                ShoutCamp(p, $"Heat {lvl}!{(next.Length > 0 ? " Next - " + next : "")}");
                if (milestone)
                {
                    FraysManager.Effect(FlameObject, PlayScript.EnchantUpOrange);
                    FraysManager.Feed(this, $"{(Def.Group ? OwnerName : p.Name)} reaches Heat {lvl}.");
                }

                // A group camp's climb is everyone's: each member present gets the effect, its Heat hunts and the achievements.
                foreach (var who in CampPlayers(p))
                {
                    FraysManager.HeatUpEffect(who, milestone);
                    FraysHunts.OnHeat(who, c.Level, lvl);

                    var arec = FraysManager.Record(who);   // the Heat achievements
                    if (Def.Group ? lvl > arec.BestGroupHeat : lvl > arec.BestHeat)
                    {
                        if (Def.Group)
                            arec.BestGroupHeat = lvl;
                        else
                            arec.BestHeat = lvl;
                        Achieve(who, arec);
                    }
                }

                // Sky Seals: a group camp that first reaches sky_seals_heat this camp, once a day for each member.
                if (FraysSky.HeatSealsDue(Def.Group, c.PeakLevel, c.Level, lvl, FraysSettings.Int(FraysSettings.SkySealsHeat)))
                    FraysSky.OnGroupHeat(CampPlayers(p));

                // Ember Boons: one owed for each boon level passed for the first time this camp (a new peak).
                c.OffersOwed += FraysBoons.OffersCrossed(c.PeakLevel, lvl, FraysSettings.Int(FraysSettings.BoonEvery));
            }
            else if (lvl < c.Level)
            {
                TellCamp(p, $"Your fire cools: Heat {lvl} {FraysRules.Bar((double)prog / need)}.");
                ShoutCamp(p, $"Your fire cools: Heat {lvl}");
                FraysManager.Effect(p, PlayScript.AttribDownOrange);
            }
            else if (points > 0 && step > 0 && FraysRules.BarStep((double)prog / need, step) > FraysRules.BarStep((double)c.Progress / need, step))
                TellCamp(p, $"Heat {lvl} {FraysRules.Bar((double)prog / need)}");

            c.Level = lvl;
            c.Progress = prog;

            if (lvl > c.PeakLevel)
            {
                var minute = (ACE.Common.Time.GetUnixTime() - c.StartedAt) / 60.0;

                for (var h = c.PeakLevel + 1; h <= lvl; h++)
                    c.HeatReachedMinute[h] = minute;
            }

            c.PeakLevel = Math.Max(c.PeakLevel, lvl);
        }

        // ---- the Codex and achievements ------------------------------------------------------

        /// <summary>Awards any achievement newly done and tells the player (center text for each). Saves the record.</summary>
        public static void Achieve(Player p, FraysPlayerRecord rec)
        {
            if (p == null || rec == null)
                return;

            foreach (var line in FraysCodex.Check(p, rec))
            {
                FraysManager.Tell(p, line);
                FraysManager.Shout(p, line.Split(" (")[0]);
                FraysManager.Effect(p, PlayScript.SpecialStateOrange);
            }

            FraysManager.SaveRecord(p, rec);
        }

        /// <summary>The top damager's kill: the Codex notes the family and the week's Gilded ward.</summary>
        private void OnCodexKill(Player p, Creature creature, FraysCreatureTag tag, bool gilded)
        {
            var rec = FraysManager.Record(p);
            var news = new List<string>();

            if (FraysFamilies.Of(creature.WeenieClassId) is FraysFamily fam && FraysCodex.Note(rec, FraysCodex.Family + fam.Key))
                news.Add($"the {fam.Name} family");

            if (gilded && FraysSettings.Toggle(FraysSettings.EliteAffixes) && FraysCodex.Note(rec, FraysCodex.Affix + FraysWards.ForWeek(FraysWeek.Current).Name))
                news.Add($"{FraysWards.ForWeek(FraysWeek.Current).Name} Gilded");

            if (news.Count > 0)
            {
                FraysManager.Tell(p, $"Fray Codex: {string.Join(", ", news)} added (/fray codex).");
                Achieve(p, rec);
            }
        }

        // ---- Ember Boons ---------------------------------------------------------------

        /// <summary>Each camp tick: open an owed boon offer, pick from the plan when time runs out.</summary>
        private void TickBoons(FraysCamper c, Player p, FraysPlayerRecord rec, double now)
        {
            if (c.Offer.Count == 0 && c.OffersOwed > 0)
            {
                c.OffersOwed--;
                c.Offer = FraysBoons.Offer(c.Boons, c.Level, FraysSettings.Int(FraysSettings.RareBoonHeat), new Random(), p.GetClassAbilityRank);

                if (c.Offer.Count > 0)
                {
                    c.OfferAt = now;
                    var auto = FraysSettings.Number(Def.Group ? FraysSettings.BoonVoteSeconds : FraysSettings.BoonAutoSeconds);
                    c.OfferShoutAt = now;
                    c.Votes.Clear();

                    if (Def.Group)
                        TellCamp(p, $"Ember Boon vote - members who fought in the last 2 minutes vote with /fp 1, 2 or 3 (in {auto:0} s the most votes win; a tie goes to the keeper's plan): {FraysBoons.OfferText(c.Offer, k => FraysCollection.Rank(rec, k), p.GetClassAbilityRank)}");
                    else
                        TellCamp(p, $"Ember Boon - pick with /fp 1, 2 or 3 (in {auto:0} s your boon plan, {FraysBoons.PickRuleText(rec.BoonOrder, rec.BoonPlan)}, picks): {FraysBoons.OfferText(c.Offer, k => FraysCollection.Rank(rec, k), p.GetClassAbilityRank)}");
                    ShoutCamp(p, FraysBoons.ShortOffer(c.Offer, auto));

                    // In combat the chat moves so fast the offer is missed, so a window stays up till the
                    // boon is taken (it closes itself on /fp, the boon plan's pick or the vote's end).
                    if (FraysSettings.Toggle(FraysSettings.BoonPopup))
                        foreach (var who in CampPlayers(p))
                            OpenBoonPrompt(who, Def.Group
                                ? $"An Ember Boon vote\n\n{FraysBoons.OfferText(c.Offer, k => FraysCollection.Rank(rec, k), p.GetClassAbilityRank).Replace("  ", "\n")}\n\nVote with /fp 1, /fp 2 or /fp 3 (members who fought in the last 2 minutes). In {auto:0} seconds the most votes win; a tie goes to the keeper's boon plan.\n\nThis window closes when you vote (either button also closes it)."
                                : $"An Ember Boon\n\n{FraysBoons.OfferText(c.Offer, k => FraysCollection.Rank(rec, k), p.GetClassAbilityRank).Replace("  ", "\n")}\n\nType /fp 1, /fp 2 or /fp 3. In {auto:0} seconds your boon plan ({FraysBoons.PickRuleText(rec.BoonOrder, rec.BoonPlan)}) picks for you.\n\nThis window closes when you pick (either button also closes it).");
                }
            }
            else if (c.Offer.Count > 0 && Def.Group && now - c.OfferAt >= FraysSettings.Number(FraysSettings.BoonVoteSeconds))
                CloseVote(c, p, now);
            else if (c.Offer.Count > 0 && !Def.Group && now - c.OfferAt >= FraysSettings.Number(FraysSettings.BoonAutoSeconds))
                TakeBoon(c, p, FraysBoons.AutoPick(c.Offer, rec.BoonOrder, rec.BoonPlan, p.GetClassAbilityRank), "your boon plan");
            else if (c.Offer.Count > 0 && now - c.OfferShoutAt >= 10)
            {
                // The centre-screen reminder every 10 seconds while the offer stands.
                c.OfferShoutAt = now;
                ShoutCamp(p, FraysBoons.ShortOffer(c.Offer, FraysSettings.Number(Def.Group ? FraysSettings.BoonVoteSeconds : FraysSettings.BoonAutoSeconds) - (now - c.OfferAt)));
            }
        }

        /// <summary>Emberblessed on a camp: one Heat at once and no cooling for 2 minutes.</summary>
        public void BlessCamp(Player p)
        {
            lock (Sync)
            {
                if (!campers.TryGetValue(p.Guid.Full, out var c))
                    return;

                c = HeatCamper(c);   // a group camp: its shared Heat

                var need = FraysRules.PointsToNext(c.Level, FraysSettings.Int(FraysSettings.HeatPointsBase), FraysSettings.Int(FraysSettings.HeatPointsPerLevel));
                AddPoints(c, p, Math.Max(1, need - c.Progress));
                c.LastKillAt = Time.GetUnixTime() + 120;
            }
        }

        /// <summary>/fp 1, 2 or 3: picks from the open offer. Returns what to tell the player.</summary>
        public string ChooseBoon(Player player, int choice)
        {
            lock (Sync)
            {
                if (!campers.TryGetValue(player.Guid.Full, out var c))
                    return "You have no camp here.";

                c = HeatCamper(c);   // a group camp: its shared Heat

                if (c.Offer.Count == 0)
                    return c.OffersOwed > 0 ? "Your next Ember Boon is coming." : "No Ember Boon is waiting. One comes every few Heat levels.";

                if (choice < 1 || choice > c.Offer.Count)
                    return $"Pick 1 to {c.Offer.Count}: {FraysBoons.OfferText(c.Offer, k => FraysCollection.Rank(FraysManager.Record(player), k), player.GetClassAbilityRank)}";

                if (Def.Group)
                {
                    var said = Vote(c, player, choice, Time.GetUnixTime());
                    CloseBoonPrompt(player);   // the voter's window goes; the others' wait for their own votes or the end
                    return string.IsNullOrEmpty(said) ? null : said;
                }

                TakeBoon(c, player, c.Offer[choice - 1], "your pick");
                return null;
            }
        }

        /// <summary>The Ember Boon window: a yes/no box the server can close later; a plain popup if one is already up.</summary>
        private static void OpenBoonPrompt(Player who, string text)
        {
            if (who?.Session == null)
                return;

            if (!who.ConfirmationManager.EnqueueSend(new Confirmation_FraysBoon(who.Guid), text))
                who.Session.Network.EnqueueSend(new ACE.Server.Network.GameEvent.Events.GameEventPopupString(who.Session, text));
        }

        /// <summary>Closes a player's Ember Boon window if it is still open.</summary>
        private static void CloseBoonPrompt(Player who)
        {
            if (who?.ConfirmationManager != null && who.ConfirmationManager.TryGetPending(ConfirmationType.Yes_No, out var open) && open is Confirmation_FraysBoon)
                who.ConfirmationManager.TryCancel(open);
        }

        private void TakeBoon(FraysCamper c, Player p, string key, string how)
        {
            foreach (var who in CampPlayers(p))
                CloseBoonPrompt(who);
            CloseBoonPrompt(p);

            var b = FraysBoons.Find(key);
            c.Offer = new List<string>();

            if (b == null)
                return;

            c.Boons.Add(b.Key);
            FraysMetrics.BoonsTaken.Add(1);
            var brec = FraysManager.Record(p);
            var isNew = FraysCodex.Note(brec, FraysCodex.Boon + b.Key);
            var rank = FraysBoons.RankFor(b, k => FraysCollection.Rank(brec, k), p.GetClassAbilityRank);
            TellCamp(p, $"Ember Boon ({how}): {b.Name}{(rank > 1 && !b.IsClass ? $" rank {rank}" : "")} - {(b.IsClass ? b.TextForAbilityRank(p.GetClassAbilityRank(b.Ability.Value)) : b.TextAt(rank))}. You hold {c.Boons.Count} this camp.");

            if (isNew)
            {
                FraysManager.SaveRecord(p, brec);
                FraysManager.Tell(p, $"{b.Name} joins your boon collection. Sigils from the Boonwright raise its rank (/fray collection).");
            }

            // A group camp's boon is everyone's: it joins each member's collection too.
            foreach (var who in CampPlayers(p))
            {
                if (who == p)
                    continue;

                var wrec = FraysManager.Record(who);

                if (FraysCodex.Note(wrec, FraysCodex.Boon + b.Key))
                {
                    FraysManager.SaveRecord(who, wrec);
                    FraysManager.Tell(who, $"{b.Name} joins your boon collection. Sigils from the Boonwright raise its rank (/fray collection).");
                }
            }
            ShoutCamp(p, $"Ember Boon: {b.Name}");
            FraysManager.Effect(p, b.Rare ? PlayScript.SpecialStateOrange : PlayScript.EnchantUpOrange);
        }

        // ---- spawning ----------------------------------------------------------------------------------------

        /// <summary>
        /// Outdoors, a rich point of this camper's that is due back (rested, in reach), or -1. A due rich point spawns on
        /// the dot: ahead of ordinary ready points, through the alive cap and without waiting on the spawn timer.
        /// </summary>
        private int DueRichPoint(FraysCamper c, Player near, double now)
        {
            if (!Def.Outdoor || near == null || Def.SpawnPoints.Count == 0)
                return -1;

            var view = new Dictionary<int, double>(ReadyView(c));
            var any = false;

            for (var i = 0; i < Def.SpawnPoints.Count; i++)
            {
                if (KindOf(i) == FraysPointKind.Rich)
                    any = true;
                else
                    view[i] = double.MaxValue;
            }

            if (!any)
                return -1;

            return FraysRules.PickRestedPoint(Enumerable.Range(0, Def.SpawnPoints.Count).Select(PointPos).ToList(), near.Location.Pos, view, now,
                FraysSettings.Number(FraysSettings.SpawnNearRadius), 0);
        }

        private void SpawnFor(FraysCamper c, double now, List<Player> present)
        {
            if (c.FillOnTake)
            {
                c.FillOnTake = false;
                FillAllPoints(c, now, present);
                return;
            }

            if (c.RefillPoints.Count > 0)
            {
                RefillAfterDeath(c, now, present);
                return;
            }

            int level;
            int rich;

            lock (Sync)
            {
                level = c.Level;

                // A due rich point spawns on time, through the alive cap and ahead of the ordinary ready points, and leaves
                // the ordinary spawn timer as it was (waiting behind them, it would come back 1-3 min after its gold glow said).
                rich = DueRichPoint(c, present.FirstOrDefault(p => p.Guid.Full == c.Guid), now);
                var timerDue = now >= c.NextSpawnAt;

                if (!timerDue && rich < 0)
                    return;

                if (timerDue)
                {
                    var perPoint = FraysRules.SpawnIntervalSeconds(level, FraysSettings.Number(FraysSettings.SpawnIntervalSeconds),
                        FraysSettings.Number(FraysSettings.SpawnIntervalPerLevel), FraysSettings.Number(FraysSettings.SpawnIntervalFloor));

                    // This player's stream uses spawn_points_per_player points' worth; the points take turns across the room.
                    var points = Math.Min(Def.SpawnPoints.Count, Math.Max(1, FraysSettings.Int(FraysSettings.SpawnPointsPerPlayer)));
                    c.NextSpawnAt = now + perPoint / points * (Def.Outdoor ? 1 : FraysRules.ShapeIntervalMult(c.Shape));
                }

                // A group camp's one stream counts every creature at the fire, and may field more as more members stand in it.
                if (rich < 0 && alive.Count(m => m.FraysTag != null && (Def.Group || m.FraysTag.ForPlayer == c.Guid))
                    >= FraysSettings.Int(FraysSettings.SpawnMaxAlive) * FraysRules.ShapePackMult(c.Shape) * (Def.Outdoor ? 2 : 1) * GroupRateMult)
                    return;
            }

            if (Def.SpawnPoints.Count == 0)
                return;

            int index;

            if (Def.Outdoor)
            {
                // Outdoors: packs wait at rested points across the zone (spawn_near_radius 0) or near the camper; the camper
                // routes between them and pulls. A point is busy while its pack lives and rests once cleared.
                var near = present.FirstOrDefault(p => p.Guid.Full == c.Guid);

                if (near == null)
                    return;

                index = rich >= 0 ? rich : FraysRules.PickRestedPoint(Enumerable.Range(0, Def.SpawnPoints.Count).Select(PointPos).ToList(), near.Location.Pos, ReadyView(c), now,
                    FraysSettings.Number(FraysSettings.SpawnNearRadius), 0);   // the lowest ready point first: a set order, the same every pull (the weekly pull map)

                if (index < 0)
                {
                    c.NextSpawnAt = now + 2;
                    return;
                }

                c.PointReadyAt[index] = double.MaxValue;
            }
            else
                index = rotation++ % Def.SpawnPoints.Count;

            SpawnPackAt(c, present.FirstOrDefault(p => p.Guid.Full == c.Guid), index, level, now);
        }

        /// <summary>
        /// A camp just taken (fill_on_take): every point that is ready fields its pack now, Gilded rich points too, through the spawn timer and
        /// the alive cap. The points then rest and come back as usual.
        /// </summary>
        private void FillAllPoints(FraysCamper c, double now, List<Player> present)
        {
            var owner = present.FirstOrDefault(p => p.Guid.Full == c.Guid);

            if (owner == null || Def.SpawnPoints.Count == 0)
                return;

            int level;
            var points = new List<int>();

            lock (Sync)
            {
                level = c.Level;
                var view = ReadyView(c);

                for (var i = 0; i < Def.SpawnPoints.Count; i++)
                {
                    if (Def.Outdoor)
                    {
                        if (view.TryGetValue(i, out var ready) && ready > now)
                            continue;

                        c.PointReadyAt[i] = double.MaxValue;
                    }

                    points.Add(i);
                }

                var perPoint = FraysRules.SpawnIntervalSeconds(level, FraysSettings.Number(FraysSettings.SpawnIntervalSeconds),
                    FraysSettings.Number(FraysSettings.SpawnIntervalPerLevel), FraysSettings.Number(FraysSettings.SpawnIntervalFloor));
                var per = Math.Min(Def.SpawnPoints.Count, Math.Max(1, FraysSettings.Int(FraysSettings.SpawnPointsPerPlayer)));
                c.NextSpawnAt = now + perPoint / per * (Def.Outdoor ? 1 : FraysRules.ShapeIntervalMult(c.Shape));
            }

            foreach (var i in points)
                SpawnPackAt(c, owner, i, level, now);

            log.Info($"[FRAYS] {Def.Id} {Instance:X8}: {c.Name} took the camp; {points.Count} packs up at once");
        }

        /// <summary>
        /// After a death (heat_death_clears_pack): the points whose packs faded field new packs at once, at the cooled Heat,
        /// through the spawn timer and the alive cap, as a camp just taken does. The ordinary stream then carries on.
        /// </summary>
        private void RefillAfterDeath(FraysCamper c, double now, List<Player> present)
        {
            var owner = present.FirstOrDefault(p => p.Guid.Full == c.Guid);

            if (owner == null || Def.SpawnPoints.Count == 0)
                return;   // kept for when the camper is back in the zone

            int level;
            var points = new List<int>();

            lock (Sync)
            {
                level = c.Level;
                var view = ReadyView(c);

                foreach (var i in c.RefillPoints.OrderBy(i => i))
                {
                    if (i < 0 || i >= Def.SpawnPoints.Count)
                        continue;

                    if (Def.Outdoor)
                    {
                        if (view.TryGetValue(i, out var ready) && ready > now)
                            continue;   // taken again since (a pack stands there)

                        c.PointReadyAt[i] = double.MaxValue;
                    }

                    points.Add(i);
                }

                c.RefillPoints.Clear();
            }

            foreach (var i in points)
                SpawnPackAt(c, owner, i, level, now);

            if (points.Count > 0)
                log.Info($"[FRAYS] {Def.Id} {Instance:X8}: {c.Name} died; {points.Count} faded points refilled at Heat {level}");
        }

        /// <summary>The pack one point fields for this camper at <paramref name="level"/>.</summary>
        private void SpawnPackAt(FraysCamper c, Player owner, int index, int level, double now)
        {
            var kind = Def.Outdoor ? KindOf(index) : FraysPointKind.Normal;
            c.RichFightFrom.Remove(index);   // a new pack here: its fight is timed afresh
            // the point's own spot (its layout position), as PointRestFor reads it (the def position would let a
            // moved point rest as hot and spawn as cold)
            var hot = Def.Outdoor && QuadrantOf(PointPos(index)) == FraysRules.HotQuadrant(now, Def.Id, FraysSettings.Number(FraysSettings.HotSpotMinutes));
            var mods = c.Mods ?? new FraysCampMods();
            var pack = Math.Max(1, FraysRules.PackSize(level, FraysSettings.Int(FraysSettings.SpawnPackEvery)) * FraysRules.ShapePackMult(c.Shape) + mods.PackDelta);

            // Nests field a bigger pack with more elites; quick points a single creature; a flaring quarter one more.
            if (kind == FraysPointKind.Nest)
                pack += FraysSettings.Int(FraysSettings.NestPackBonus);
            else if (kind == FraysPointKind.Quick)
                pack = 1;

            if (hot)
                pack += 1;


            if (TypedRoles(c, index, level, hot, out var each) is { } typed)
            {
                for (var i = 0; i < typed.Count; i++)
                    SpawnOne(c, owner, index, level, i, forceRole: typed[i], packShare: each);

                return;
            }

            if (Def.Outdoor)
            {
                // Outdoors every point fields a recognisable pack (FraysRules.PackTemplate): plan your pulls around them.
                var extra = FraysRules.PackSize(level, FraysSettings.Int(FraysSettings.SpawnPackEvery)) - 1 + (kind == FraysPointKind.Rich ? Math.Min(0, mods.PackDelta) : mods.PackDelta + LayoutPackExtra) + GroupPackExtra;   // Gilded keep their size: their rest is fixed (an affix never grows them)
                var roles = FraysRules.PackTemplate(kind, LayoutIndex(index), PackShape(c, index), Math.Max(0, extra), hot);

                if (kind != FraysPointKind.Rich && roles.Count > 0 && FraysRules.ShareExtra(roles.Count, mods.PackShare, ThreadSafeRandom.Next(0.0f, 1.0f)) > 0)
                    roles.Add(roles[roles.Count - 1]);   // Bellows: one more of the pack's last role

                for (var i = 0; i < roles.Count; i++)
                    SpawnOne(c, owner, index, level, i, forceRole: roles[i]);

                return;
            }

            pack += FraysRules.ShareExtra(pack, mods.PackShare, ThreadSafeRandom.Next(0.0f, 1.0f));   // Bellows

            for (var i = 0; i < pack; i++)
                SpawnOne(c, owner, index, level, i);
        }

        /// <summary>
        /// A typed point's pack (pack_types): its roles, and each creature's health times its role's (a brute's share).
        /// Null for a point with no type. A flaring quarter, Packmaster and the affixes add swarm worth; Bellows one more creature (not Gilded).
        /// </summary>
        internal List<FraysRole> TypedRoles(FraysCamper c, int index, int level, bool hot, out double each)
        {
            each = 1;
            var type = Layout?.TypeOf(index) ?? FraysPackType.None;

            if (type == FraysPackType.None)
                return null;

            var mods = c.Mods ?? new FraysCampMods();
            var delta = KindOf(index) == FraysPointKind.Rich ? Math.Min(0, mods.PackDelta) : mods.PackDelta;   // Gilded rests are fixed: an affix never grows them
            var roles = FraysRules.TypedPack(type, level, delta + (hot ? 1 : 0), out each);

            if (KindOf(index) != FraysPointKind.Rich && FraysRules.ShareExtra(roles.Count, mods.PackShare, ThreadSafeRandom.Next(0.0f, 1.0f)) > 0)
                roles.Add(roles[roles.Count - 1]);   // Bellows

            return roles;
        }

        /// <summary>True when a pack creature stands further than pack_tether from its own spawn point (solo fixed layouts; never the Snarl).</summary>
        public bool BeyondTether(FraysCreatureTag tag, Vector3 at)
        {
            var tether = FraysSettings.Number(FraysSettings.PackTether);

            if (tag == null || tag.Snarl || tag.SnarlAdd || tether <= 0 || Layout == null || tag.SpawnIndex < 0 || tag.SpawnIndex >= Def.SpawnPoints.Count)
                return false;

            var home = PointPos(tag.SpawnIndex);
            var dx = at.X - home.X;
            var dy = at.Y - home.Y;
            return dx * dx + dy * dy > tether * tether;
        }

        private Creature SpawnOne(FraysCamper c, Player owner, int index, int level, int packSlot, bool snarl = false, FraysRole? forceRole = null, double packShare = 1, int snarlAdd = 0)
        {
            // snarlAdd: 1 = the Snarl Brood (holds the cache), 2 = a plain Snarl add, 0 = neither.
            var boss = snarl || snarlAdd > 0;

            var mix = Def.MixAt(level).ToList();

            if (mix.Count == 0)
                return null;

            var roll = ThreadSafeRandom.Next(1, mix.Sum(m => m.Weight));
            var pick = mix[0];

            foreach (var m in mix)
            {
                roll -= m.Weight;

                if (roll <= 0)
                {
                    pick = m;
                    break;
                }
            }

            // The Snarl is the room's toughest creature, made big.
            if (snarl)
                pick = mix.OrderByDescending(m => BaseHealthOf(m.Wcid)).First();

            var mods = c.Mods ?? new FraysCampMods();
            // Gilded rich points are the high-value packs (no elites).
            var richPoint = !boss && Layout?.RoleOf(index) == FraysPointRole.Rich;
            var templated = forceRole != null;

            // The week's family (when families are on): swarm or standard in packs, its Snarl. Every role is
            // normalised to a share of the standard creature's health, so the week changes gear, not difficulty.
            var family = FraysFamilies.For(Def.FrayKey);
            var role = snarl ? FraysRole.Snarl : snarlAdd > 0 ? FraysRole.Swarm : FraysRole.Standard;
            var swarmShare = FraysSettings.Number(FraysSettings.SwarmHealthShare);

            // Casters only from caster_min_heat: below it no caster is picked and nothing casts.
            var casterMinHeat = FraysSettings.Int(FraysSettings.CasterMinHeat);
            var castersAllowed = FraysRules.CastersAllowed(level, casterMinHeat);

            if (family != null)
            {
                if (templated)
                    role = forceRole.Value == FraysRole.Elite ? FraysRole.Standard : forceRole.Value;
                else if (role == FraysRole.Standard && ThreadSafeRandom.Next(0.0f, 1.0f) < FraysRules.ShapeSwarmShare(c.Shape, FraysSettings.Number(FraysSettings.SwarmPackShare)))
                    role = FraysRole.Swarm;

                pick = new FraysMixEntry(family.WcidFor(role, castersAllowed), 1);
            }

            // Threads shaping (threads_shape): a Threads family's creature is drawn from a Threads plan for the role, at the
            // camper's own level (plus the Heat's levels only with threads_shape_heat_levels), and built the way a Thread builds it (FraysThreadsShape).
            var shapeLevel = owner?.Level ?? c.PlayerLevel;
            DungeonSpawnPlan threadsPlan = null;
            DungeonSpawnPlanEntry threadsEntry = null;

            if (family?.Table != null && shapeLevel > 0 && FraysSettings.Toggle(FraysSettings.ThreadsShape) && FraysSettings.Toggle(FraysSettings.LevelScaling))
            {
                threadsPlan = FraysThreadsShape.PlanFor(family.Table, FraysLevelScale.ShapedLevel(shapeLevel, level, FraysSettings.Toggle(FraysSettings.ThreadsShapeHeatLevels), FraysSettings.Int(FraysSettings.ThreadsShapeMaxLevel)));
                var (threadsWcid, entry) = FraysThreadsShape.Pick(threadsPlan, role, castersAllowed ? null : family.IsCaster);

                if (entry != null)
                {
                    pick = new FraysMixEntry(threadsWcid, 1);
                    threadsEntry = entry;
                }
                else
                    threadsEntry = FraysThreadsShape.EntryFor(threadsPlan, pick.Wcid, role, FraysThreadsShape.RunLevel(FraysLevelScale.ShapedLevel(shapeLevel, level, FraysSettings.Toggle(FraysSettings.ThreadsShapeHeatLevels), FraysSettings.Int(FraysSettings.ThreadsShapeMaxLevel))));   // the family's own pick, stamped at the run level
            }

            if (!(WorldObjectFactory.CreateNewWorldObject(pick.Wcid) is Creature creature))
            {
                log.Error($"[FRAYS] {Def.Id}: weenie {pick.Wcid} is not a creature");
                return null;
            }

            var point = Def.SpawnPoints[index];

            // A pack spreads a little around its point (never more than 1.5 m), the first one exactly on it.
            var offset = packSlot == 0 ? Vector3.Zero : new Vector3((float)ThreadSafeRandom.Next(-1.5f, 1.5f), (float)ThreadSafeRandom.Next(-1.5f, 1.5f), 0);
            var pos = PointPos(index) + offset;

            creature.Location = Grounded(new Position(((uint)Def.Landblock << 16) | FraysRules.OutdoorCell(Def.Outdoor, point.Cell, pos), pos.X, pos.Y, pos.Z + 0.05f, 0, 0, 0, 1, Instance), 0.05f);

            // Built as a Thread builds it (no run, no rewards), before any Frays name, health or multiplier goes on.
            var threadsShaped = threadsEntry != null && FraysThreadsShape.Shape(creature, threadsPlan, threadsEntry, Def.Id);

            // Below caster_min_heat nothing casts, whatever its own book holds (after Threads shaping, which can raise a
            // book's tier, and before the immobile release below, which reads the final book).
            FraysRules.StripSpellsBelowCasterHeat(creature.Biota?.PropertiesSpellBook, level, casterMinHeat);

            // No Frays monster dispels or reflects (a Snarl Virindi Cipher's Nullify Life Magic Other
            // would keep a camper rebuffing for minutes). Its book loses every dispel; its combat effects lose dispel,
            // reflect, and any castspell that casts a dispel.
            FraysRules.StripSpells(creature.Biota?.PropertiesSpellBook, IsDispelSpell);
            creature.RemoveMonsterEffects((kind, record) => FraysRules.BannedMonsterEffect(kind, record, IsDispelSpellName));

            // snarl_health_by_heat. A Snarl add: its share of snarl_adds_health Snarls, over the swarm share its role applies below.
            var eliteHealth = snarl ? FraysRules.SnarlHealthAt(level)
                : snarlAdd > 0 ? FraysRules.SnarlHealthAt(level) * FraysSettings.Number(FraysSettings.SnarlAddsHealth)
                    / Math.Max(1, FraysSettings.Int(FraysSettings.SnarlAddsCount)) / Math.Max(0.01, swarmShare)
                : 1;
            // packShare: a typed brute's health over its role's (pack_types), 1 for everything else.
            var shapeMult = (boss || templated ? 1 : FraysRules.ShapeHealthMult(c.Shape)) * packShare;
            var roleShare = (role == FraysRole.Swarm ? swarmShare : 1) * shapeMult;
            var work = family != null ? roleShare * eliteHealth : FraysRules.WorkUnits(BaseHealthOf(pick.Wcid), ReferenceHealth, eliteHealth) * shapeMult;
            // A weekly affix never pays more. One that lowers health (Brittle Dream, Frayed Masses) lowers
            // the creature's work with it, so a faster kill pays the same an hour; one that raises health pays as before.
            work *= FraysRules.AffixWorkMult(mods.MonsterHealth * (boss ? 1 : richPoint ? mods.GildedHealth : mods.PackHealth));
            var familyRatio = family != null ? (family.Table == null ? BaseHealthOf(family.Standard) / Math.Max(1, BaseHealthOf(pick.Wcid)) : 1.0) * roleShare : shapeMult;

            creature.FraysTag = new FraysCreatureTag(this, c.Guid, level, index, work)
            {
                Rich = richPoint,
                Snarl = snarl,
                SnarlAdd = snarlAdd > 0,
                SnarlCache = snarlAdd == 1,
                DamageMult = snarl ? FraysSettings.Number(FraysSettings.SnarlDamageNormal) : FraysRules.HitShare(role, packShare, level) * (snarlAdd > 0 ? FraysSettings.Number(FraysSettings.SnarlDamageNormal) : 1),   // swarm_hit_share, brute_hit_by_share
                ThreadsShaped = threadsShaped,
            };

            if (snarl)
            {
                creature.Name = $"{SnarlName(false)} {creature.Name}";
                creature.ObjScale = (creature.ObjScale ?? 1) * 1.4f;
            }
            else if (snarlAdd == 1)
            {
                creature.Name = $"Swarm Snarl Brood {creature.Name}";
                creature.ObjScale = (creature.ObjScale ?? 1) * 1.2f;
            }
            else if (snarlAdd > 1)
                creature.Name = $"Swarm Snarl {creature.Name}";
            else if (role == FraysRole.Standard && packShare > 1)
                creature.ObjScale = (creature.ObjScale ?? 1) * (float)FraysSettings.Number(FraysSettings.BruteScale);   // a brute point's creature
            creature.FraysTransient = true;
            creature.TimeToRot = -1;

            // A Threads-shaped creature has had Threads' own combat-trait rules; any other gets the World Events guard.
            if (!threadsShaped)
            {
                try
                {
                    CombatTraitGuard.Apply(creature, WorldEventCombatGuard.Current().ToCombatDials(), DungeonBandStandard.Empty, isBoss: false);
                }
                catch (Exception ex)
                {
                    log.Error($"[FRAYS] {Def.Id}: the combat guard threw for wcid {pick.Wcid}", ex);
                }
            }

            // A retail creature from a Threads family: hostile to every player whatever its faction, and never rooted.
            if (family?.Table != null)
            {
                SpawnedCreatureHostility.MakeHostileToPlayers(creature);
                SpawnedCreatureHostility.ReleaseImmobileWithoutRangedAttack(creature);

                // Its own weapons and armour never drop: Frays loot is the camp's loot (as in Threads).
                ThreadDungeonSpawner.StripCorpseTransfer(creature);
            }

            // The creature matches its camper's level, plus the Heat curve's extra levels (heat_curve_levels).
            var camperLevel = owner?.Level ?? c.PlayerLevel;

            // A Threads-shaped creature already stands at its standards; it only takes the level (health and loot read it): the camper's own, unless threads_shape_heat_levels.
            if (threadsShaped)
                creature.Level = FraysLevelScale.ShapedLevel(camperLevel, level, FraysSettings.Toggle(FraysSettings.ThreadsShapeHeatLevels), FraysSettings.Int(FraysSettings.ThreadsShapeMaxLevel));
            else if (FraysSettings.Toggle(FraysSettings.LevelScaling) && camperLevel > 0)
                FraysLevelScale.Apply(creature, FraysLevelScale.TargetLevel(camperLevel, level));

            // No Frays creature defends above the cap for its level (defense_cap), whatever it was authored with.
            FraysLevelScale.ApplyDefenseCap(creature);

            // Health is Threads' band-standard health curve at the creature's level (dynamic_dungeons_health_curve),
            // times its pack role's share, the Snarl multiplier and the week's affixes. heat_curve_health is
            // an optional extra on top (empty = none). With the Threads curve off, the old relative scaling applies.
            var extra = FraysCurve.Health(level) * eliteHealth
                * mods.MonsterHealth * (boss ? 1 : richPoint ? mods.GildedHealth : mods.PackHealth)
                * GroupHealthMult;   // a group camp: tougher for each member present past the first
            var curveHealth = FraysLevelScale.CurveHealth(FraysLevelScale.HealthLevel(creature.Level ?? camperLevel, camperLevel, level,
                FraysSettings.Int(FraysSettings.HealthCamperLevelCap)));

            if (curveHealth > 0)
                FraysLevelScale.SetMaxHealth(creature, curveHealth * (family != null ? roleShare : shapeMult) * extra);
            else
            {
                var scaled = WorldEventSpawner.ScaledStartingValue(creature.Health.StartingValue, creature.Health.MaxValue, extra * familyRatio);

                if (scaled != creature.Health.StartingValue)
                    creature.Health.StartingValue = scaled;
            }

            creature.Health.Current = creature.Health.MaxValue;

            if (threadsShaped)
                FraysThreadsShape.LogSample(creature, family?.Key, FraysThreadsShape.RunLevel(FraysLevelScale.ShapedLevel(camperLevel, level, FraysSettings.Toggle(FraysSettings.ThreadsShapeHeatLevels), FraysSettings.Int(FraysSettings.ThreadsShapeMaxLevel))), threadsEntry);

            // The week's number Shift (Savage, Fortified and the rest): ratings, health and damage.
            FraysHeatShifts.OnSpawn(creature, creature.FraysTag);

            // Kill pay counts health removed (kill_pay_by_health): how much tougher this Heat makes it than Heat 0.
            if (creature.FraysTag != null)
                creature.FraysTag.HeatHealth = FraysLevelScale.HeatHealthMult(camperLevel, level, threadsShaped);

            // Outdoors every Fray creature notices its owner at the same range, so pulls plan the same whatever the week's family.
            if (Def.Outdoor)
                creature.VisualAwarenessRange = FraysSettings.Number(FraysSettings.AggroRange);

            // XP and lum come from the static camping rate, never from kills.
            creature.XpOverride = 0;
            creature.LuminanceAward = 0;

            // No named rares: Gilded packs fill that role.

            // The week's affix (FraysWards): a test of the gear brought, on every Gilded creature.
            if (richPoint && !snarl && FraysSettings.Toggle(FraysSettings.EliteAffixes))
                FraysWards.ApplyTo(creature);

            // Some retail creatures have no loot table (Warrior Corthos, about a hundred more): they borrow their family's.
            if (creature.DeathTreasure == null)
                creature.DeathTreasureOverride = FallbackLoot(family);

            // A rich point's pack (FraysLayout): Gilded in its name, so a route's name priority finds it, and a gold glow
            // (PulseBosses). It pays rich_mult times the Embers and key bar, and rich_loot_mult times the loot.
            var rich = richPoint && creature.FraysTag != null;

            if (rich)
            {
                creature.FraysTag.Rich = true;
                creature.Name = $"Gilded {creature.Name}";
                log.Info($"[FRAYS] gilded {Def.Id}: \"{creature.Name}\" at point {index} {PointPos(index)}");
            }

            // A plain Snarl add drops a swarm creature's loot; the Snarl and the Snarl Brood drop the cache instead.
            var lootShare = family != null && !snarl && snarlAdd != 1 ? roleShare * (snarlAdd > 0 ? 1 : eliteHealth) : -1;
            var cache = snarl || snarlAdd == 1;

            // Heat-weighted, as the key bar: loot rises with the creature's Heat health.
            if (lootShare > 0 && creature.FraysTag != null && FraysSettings.Toggle(FraysSettings.LootByHeatHealth))
                lootShare *= Math.Max(1, creature.FraysTag.HeatHealth);

            if (rich && lootShare > 0)
                lootShare *= FraysSettings.Number(FraysSettings.RichLootMult);

            if (lootShare > 0)
                lootShare *= FraysRules.PayPerCreature(level);   // pay_per_creature_by_heat

            ApplyLoot(creature, level, mods, c.Bias, cache ? SnarlCacheItems(mods) : 0, attuneBest: cache,
                lootShare: lootShare);

            // A pack member pushed into a wall or a pillar: try once more exactly on the spawn point.
            if (!creature.EnterWorld())
            {
                if (offset == Vector3.Zero)
                {
                    creature.Destroy();
                    return null;
                }

                var exact = PointPos(index);
                creature.Location = Grounded(new Position(((uint)Def.Landblock << 16) | FraysRules.OutdoorCell(Def.Outdoor, point.Cell, exact), exact.X, exact.Y, exact.Z + 0.05f, 0, 0, 0, 1, Instance), 0.05f);

                if (!creature.EnterWorld())
                {
                    creature.Destroy();
                    return null;
                }
            }

            lock (Sync)
            {
                alive.Add(creature);

                // Outdoors packs wait to be pulled, so a minute counts what was killed rather than what spawned.
                if (!boss && !Def.Outdoor)
                    c.MinuteSpawnedWork += work;
            }

            // It goes for the player whose stream spawned it.
            // Indoors it comes for the player whose stream spawned it; outdoors it waits at its point to be pulled (the Snarl comes).
            if (owner != null && (!Def.Outdoor || boss))
                creature.TryFraysWake(new List<Player> { owner });

            return creature;
        }

        /// <summary>
        /// A creature's loot: one treasure tier higher at Heat 8 and two at 16 (past tier 8, better quality instead),
        /// Unyielding's quality step, Swarming's chance of one more item, the loot Bias (which only shifts the kind of
        /// item, never the count) and, for the Snarl or a bonus cache, a cache of <paramref name="cacheItems"/> items.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TreasureDeath> fallbackLoot = new System.Collections.Concurrent.ConcurrentDictionary<string, TreasureDeath>();

        /// <summary>The best loot table among a family's creatures (every family's, when it has none), for a creature with no loot table of its own.</summary>
        internal static TreasureDeath FallbackLoot(FraysFamily family)
        {
            static TreasureDeath Best(IEnumerable<FraysFamily> families)
            {
                TreasureDeath best = null;

                foreach (var wcid in families.Where(f => f != null).SelectMany(f => f.Trash.Concat(f.Elites).Select(m => m.Wcid)).Distinct())
                {
                    var w = DatabaseManager.World.GetCachedWeenie(wcid);
                    var did = w == null ? null : ACE.Entity.Models.WeenieExtensions.GetProperty(w, PropertyDataId.DeathTreasureType);
                    var t = did.HasValue ? DatabaseManager.World.GetCachedDeathTreasure(did.Value) : null;

                    if (t != null && (best == null || t.Tier > best.Tier))
                        best = t;
                }

                return best;
            }

            try
            {
                var own = family == null ? null : fallbackLoot.GetOrAdd("f:" + family.Key, _ => Best(new[] { family }));
                return own ?? fallbackLoot.GetOrAdd("all", _ => Best(FraysFamilies.All));
            }
            catch (Exception ex)
            {
                log.Error("[FRAYS] finding a fallback loot table threw", ex);
                return null;
            }
        }

        private static void ApplyLoot(Creature creature, int level, FraysCampMods mods, IReadOnlyList<string> bias, int cacheItems, bool attuneBest, double lootShare = -1)
        {
            var baseProfile = creature.DeathTreasure;

            if (baseProfile == null)
                return;

            var steps = FraysRules.LootTierSteps(level, FraysSettings.Int(FraysSettings.LootTierFirstHeat), FraysSettings.Int(FraysSettings.LootTierSecondHeat));


            // The creature's tier, but never under what its scaled level rolls (a level 115 Monouga lifted to a level
            // 440 camper would otherwise drop its own tier 4 loot).
            var floorTier = FraysRules.TierForLevel(FraysSettings.Text(FraysSettings.LootTierByLevel), creature.Level ?? 1);
            var startTier = Math.Max(baseProfile.Tier, floorTier);

            var (tier, qualitySteps) = FraysRules.RaiseTier(startTier, steps);

            // Quality grows with Heat from a base, so loot profiles find items worth keeping.
            var quality = FraysRules.LootQuality(baseProfile.LootQualityMod, qualitySteps, FraysSettings.Number(FraysSettings.LootQualityPerStep),
                FraysSettings.Number(FraysSettings.LootQualityBase), FraysSettings.Number(FraysSettings.LootQualityPerHeat), level,
                FraysSettings.Number(FraysSettings.LootQualityCap), cacheItems > 0);

            var magicProfile = baseProfile.MagicItemTreasureTypeSelectionChances;
            var biasProfile = FraysRules.BiasProfile(bias, ThreadSafeRandom.Next(0.0f, 1.0f), ThreadSafeRandom.Next(0, 1000));

            if (biasProfile > 0)
                magicProfile = biasProfile;

            var min = baseProfile.MagicItemMinAmount;
            var max = baseProfile.MagicItemMaxAmount;

            if (cacheItems > 0)
            {
                min = cacheItems;
                max = cacheItems;
            }

            // Health-sized loot (families): a standard creature drops loot_items_per_standard magic items,
            // loot_ordinary_per_standard ordinary ones and loot_mundane_per_standard mundane ones on average; a swarm one
            // a share of that and a Gilded one more, so AoE builds do not loot more than single-target ones. For
            // reference, Tou-Tou's Shadows drops about 1.5 / 0.5 / 2.5 and Threads
            // 1.5 / 1.5 / 1 plus 3 to 4 ten-item caches a run. Frays beat every tier 8 spot, so a
            // standard creature drops 15 / 6 / 5 and a swarm one more than a retail tier 8 mob; a brute pack drops what a swarm pack does.
            var magicChance = cacheItems > 0 ? 100 : baseProfile.MagicItemChance;
            var itemChance = baseProfile.ItemChance;
            var mundaneChance = baseProfile.MundaneItemChance;
            int itemMin = baseProfile.ItemMinAmount, itemMax = baseProfile.ItemMaxAmount;
            int mundaneMin = baseProfile.MundaneItemMinAmount, mundaneMax = baseProfile.MundaneItemMaxAmount;

            if (lootShare >= 0 && cacheItems <= 0)
            {
                // A corpse holds loot_corpse_cap items, keeping its mix; the items past it go to its owner's key bar at the kill
                // (fewer items a corpse, the rest as a key to a village chest). A brute pack is then worth a
                // swarm pack however big each brute's loot is.
                var cap = (int)FraysSettings.Number(FraysSettings.LootCorpseCap);
                var (magicAvg, itemAvg, mundaneAvg, overflow) = FraysRules.SplitCorpse(lootShare * FraysSettings.Number(FraysSettings.LootItemsPerStandard),
                    lootShare * FraysSettings.Number(FraysSettings.LootOrdinaryPerStandard), lootShare * FraysSettings.Number(FraysSettings.LootMundanePerStandard), cap);
                (magicChance, min, max) = FraysRules.LootRollExact(magicAvg, ThreadSafeRandom.Next(0.0f, 1.0f));
                (itemChance, itemMin, itemMax) = FraysRules.LootRollExact(itemAvg, ThreadSafeRandom.Next(0.0f, 1.0f));
                (mundaneChance, mundaneMin, mundaneMax) = FraysRules.LootRollExact(mundaneAvg, ThreadSafeRandom.Next(0.0f, 1.0f));

                if (creature.FraysTag != null)
                    creature.FraysTag.OverflowKeys = FraysRules.OverflowKeys(overflow, FraysSettings.Number(FraysSettings.LootOverflowPerKey));

                // Rounding up can pass the cap by an item or two: cut to fit.
                (min, itemMin, mundaneMin) = FraysRules.FitCorpse(min, itemMin, mundaneMin, cap);
                (max, itemMax, mundaneMax) = (min, itemMin, mundaneMin);
            }
            else
            {
                if (tier == baseProfile.Tier && steps <= 0 && Math.Abs(quality - baseProfile.LootQualityMod) < 0.0001 && biasProfile <= 0 && cacheItems <= 0)
                    return;
            }

            creature.DeathTreasureOverride = new TreasureDeath
            {
                Id = baseProfile.Id,
                TreasureType = baseProfile.TreasureType,
                Tier = tier,
                LootQualityMod = (float)quality,
                UnknownChances = baseProfile.UnknownChances,
                ItemChance = itemChance,
                ItemMinAmount = itemMin,
                ItemMaxAmount = itemMax,
                ItemTreasureTypeSelectionChances = baseProfile.ItemTreasureTypeSelectionChances > 0 ? baseProfile.ItemTreasureTypeSelectionChances : 8,
                MagicItemChance = magicChance,
                MagicItemMinAmount = min,
                MagicItemMaxAmount = max,
                MagicItemTreasureTypeSelectionChances = magicProfile,
                MundaneItemChance = mundaneChance,
                MundaneItemMinAmount = mundaneMin,
                MundaneItemMaxAmount = mundaneMax,
                MundaneItemTypeSelectionChances = baseProfile.MundaneItemTypeSelectionChances > 0 ? baseProfile.MundaneItemTypeSelectionChances : 7,
                LastModified = baseProfile.LastModified,
            };

            if (attuneBest && creature.FraysTag != null)
                creature.FraysTag.AttuneBestLoot = true;
        }

        /// <summary>Asleep monsters go for the player they spawned for if they can, else the nearest owner in the room.</summary>
        private void TickWake(List<Player> present)
        {
            // Outdoors packs wait at their points; their own awareness pulls them when the owner comes near.
            if (Def.Outdoor)
                return;

            if (present.Count == 0)
                return;

            foreach (var m in AliveSnapshot())
            {
                if (m.MonsterState != Creature.State.Idle || m.IsDead || m.PhysicsObj == null || m.FraysTag == null)
                    continue;

                var own = present.FirstOrDefault(p => p.Guid.Full == m.FraysTag.ForPlayer);

                if (own == null || !m.TryFraysWake(new List<Player> { own }))
                    m.TryFraysWake(present);
            }
        }

        /// <summary>Re-sends every live monster to the players who know it, so each sees it attackable or not afresh.</summary>
        public void RefreshViewers()
        {
            foreach (var c in AliveSnapshot())
            {
                if (c.PhysicsObj == null)
                    continue;

                foreach (var viewer in c.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
                    viewer.TrackObject(c, resend: true);
            }
        }

        // ---- the Snarl -----------------------------------------------------------------------------------------

        /// <summary>The Snarl's bar is full: it walks out of a spawn point.</summary>
        private void SpawnSnarl(FraysCamper c, Player p, double now)
        {
            if (c.SnarlAlive != null || Def.SpawnPoints.Count == 0)
                return;

            c.SnarlBar = 0;
            var index = ThreadSafeRandom.Next(0, Def.SpawnPoints.Count - 1);
            var snarl = SpawnOne(c, p, index, c.Level, 0, snarl: true);

            if (snarl == null)
                return;

            c.SnarlAlive = snarl;
            c.SnarlSpawnedAt = now;
            { var sk = TwoSnarls ? "brute" : "snarl"; c.Play.SnarlsSpawned[sk] = c.Play.SnarlsSpawned.GetValueOrDefault(sk) + 1; }
            TellCamp(p, $"The {SnarlName(false)} tears through: {snarl.Name} walks out of the dark.");
            ShoutCamp(p, $"The {SnarlName(false)} is here: {snarl.Name}!");
            FraysManager.Effect(snarl, PlayScript.SpecialStateRed);
        }

        /// <summary>
        /// This camp has both Snarls (snarl_adds on a typed camp): brute packs call the Brute Snarl, swarm packs the Swarm
        /// Snarl (named as equals). Elsewhere there is one, "the Snarl".
        /// </summary>
        public bool TwoSnarls => FraysSettings.Toggle(FraysSettings.SnarlAdds) && Layout?.Types.Any(t => t == FraysPackType.Swarm) == true;

        /// <summary>"Swarm Snarl" for the adds wave; "Brute Snarl" for the big one where both exist, else "Snarl".</summary>
        public string SnarlName(bool swarm) => swarm ? "Swarm Snarl" : TwoSnarls ? "Brute Snarl" : "Snarl";

        /// <summary>
        /// snarl_adds: a kill from a swarm point fills the adds bar instead of the Snarl bar (solo typed camps; the Snarl
        /// Pact on). Brute, untyped and Gilded-brute points keep filling the Snarl bar.
        /// </summary>
        private bool SwarmFillsAdds(FraysCamper hc, FraysCreatureTag tag) =>
            FraysSettings.Toggle(FraysSettings.SnarlAdds) && hc.Snarl != SnarlPact.Off && !tag.Snarl
            && Layout?.TypeOf(tag.SpawnIndex) == FraysPackType.Swarm;

        /// <summary>The adds bar is full: the Snarl's brood walks out of one spawn point, the Snarl Brood first (it holds the cache).</summary>
        private void SpawnSnarlAdds(FraysCamper c, Player p, double now)
        {
            c.SnarlAdds.RemoveAll(a => a == null || a.IsDestroyed);

            if (c.SnarlAdds.Count > 0 || Def.SpawnPoints.Count == 0)
                return;

            c.SnarlAddsBar = 0;
            var index = ThreadSafeRandom.Next(0, Def.SpawnPoints.Count - 1);
            var count = Math.Max(2, FraysSettings.Int(FraysSettings.SnarlAddsCount));
            var wave = new List<Creature>();

            for (var i = 0; i < count; i++)
            {
                var add = SpawnOne(c, p, index, c.Level, i, snarlAdd: i == 0 ? 1 : 2);

                if (add != null)
                    wave.Add(add);
            }

            if (wave.Count == 0)
                return;

            c.SnarlAdds = wave;
            c.SnarlAddsSpawnedAt = now;
            c.Play.SnarlsSpawned["swarm"] = c.Play.SnarlsSpawned.GetValueOrDefault("swarm") + 1;
            TellCamp(p, $"The Swarm Snarl tears through: {wave[0].Name} walks out of the dark with {wave.Count - 1} more.");
            ShoutCamp(p, $"The Swarm Snarl is here: {wave.Count} creatures!");

            foreach (var add in wave)
                FraysManager.Effect(add, PlayScript.SpecialStateRed);
        }

        /// <summary>Items in this Snarl's cache: cache_items_normal, plus Lucid Night's.</summary>
        private static int SnarlCacheItems(FraysCampMods mods)
        {
            var items = FraysSettings.Int(FraysSettings.CacheItemsNormal);

            if (FraysLongTerm.LucidNight)
                items += 2;

            return items;
        }

        /// <summary>Pure: the share of a Snarl bar a kill fills: Heat-weighted work over snarl_bar_need at this Heat (0 when the table is empty).</summary>
        public static double SnarlBarGain(double payWork, int heat, bool gilded, double speed)
        {
            var need = SnarlNeed(heat);
            return need > 0 ? payWork * speed * (gilded ? FraysSettings.Number(FraysSettings.SnarlBarGildedMult) : 1) / need : 0;
        }

        public static double SnarlNeed(int heat) => FraysRules.KeyBarNeed(FraysSettings.Text(FraysSettings.SnarlBarNeed), heat);

        private void OnSnarlKilled(FraysCamper c, Player p, double now, bool adds = false)
        {
            // A Fray Effigy of the week's family, now and then.
            if (ThreadSafeRandom.Next(0.0f, 1.0f) < FraysSettings.Number(FraysSettings.EffigySnarlChance) && FraysFamilies.For(Def.FrayKey) is FraysFamily ef)
                FraysEffigies.Give(p, ef, $"The {SnarlName(adds)} leaves something behind: a Fray Effigy of the {ef.Name}.");

            var seconds = now - (adds ? c.SnarlAddsSpawnedAt : c.SnarlSpawnedAt);
            if (!adds)
                c.SnarlAlive = null;
            c.SnarlKills++;
            c.CachesEarned++;
            FraysMetrics.SnarlsKilled.Add(1);
            FraysMetrics.CachesSnarl.Add(1);
            c.SnarlTimes.Add(seconds);
            c.Play.SnarlKills.Add((adds ? "swarm" : TwoSnarls ? "brute" : "snarl", seconds));
            c.BestSnarlSeconds = c.BestSnarlSeconds <= 0 ? seconds : Math.Min(c.BestSnarlSeconds, seconds);

            var line = adds
                ? $"The Swarm Snarl falls in {seconds:0} seconds. Its cache is in the Swarm Snarl Brood's corpse; its best item is attuned to you."
                : $"The {SnarlName(false)} falls in {seconds:0} seconds. Its cache is in the corpse; its best item is attuned to you.";

            // A Vault Key a Snarl.
            var keys = FraysSettings.Int(FraysSettings.SnarlKeys);
            if (keys > 0)
                FraysLongTerm.Give(p, FraysLongTerm.DreamKey, keys, $"The {SnarlName(adds)} drops {(keys == 1 ? "a Vault Key" : $"{keys} Vault Keys")} (it opens an Ember Vault by the Great Hall).");

            FraysLongTerm.Bounty(p, "snarls", 1);

            // A group camp's Snarl: Sky Seals to every member holding the camp (cosmetics only, no rate bonus).
            if (Def.Group)
                FraysSky.OnGroupSnarl(ownerGuids.Select(g => PlayerManager.GetOnlinePlayer(g)).Append(p));

            FraysManager.Tell(p, line);
            FraysManager.Feed(this, $"{p.Name} felled the {SnarlName(adds)} in {Def.RoomName} in {seconds:0} seconds.");
        }

        /// <summary>A cache corpse filled: its most valuable item becomes attuned (it cannot be traded).</summary>
        public static void AttuneBest(Corpse corpse)
        {
            var best = corpse?.Inventory?.Values.OrderByDescending(i => i.Value ?? 0).FirstOrDefault();

            if (best == null)
                return;

            best.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            best.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Bonded, (int)BondedStatus.Bonded);
        }

        // ---- reports in ----------------------------------------------------------------------------------------

        /// <summary>A fire monster died: Heat points and Embers to every owner who damaged it, by their share of the damage.</summary>
        public void OnKill(Creature creature)
        {
            var tag = creature.FraysTag;
            var payEmbers = new List<(Player Player, double Embers)>();
            var payKillXp = new List<(Player Player, FraysCamper Camper, double Xp, double Lum, bool Snarl)>();
            var now = Time.GetUnixTime();
            var payTo = new List<FraysCamper>();
            var snarlReady = new List<(FraysCamper, Player)>();
            var snarlKilled = new List<(FraysCamper, Player)>();
            var addsReady = new List<(FraysCamper, Player)>();
            var addsKilled = new List<(FraysCamper, Player)>();
            var keyCaches = new List<Player>();
            var longTerm = new List<(Player, bool)>();
            var burn = 0.0;
            var burnBy = new List<(uint Guid, string Name, double Work)>();

            lock (Sync)
            {
                alive.Remove(creature);

                if (!Lit)
                    return;

                Kills++;
                FraysMetrics.Kills.Add(1);

                // Each owner's damage, pets counted for their owner.
                var byOwner = new Dictionary<uint, double>();

                foreach (var d in creature.DamageHistory.TotalDamage.Values)
                {
                    var who = d.TryGetPetOwnerOrAttacker() as Player;

                    if (who == null || !IsOwner(who))
                        continue;

                    byOwner.TryGetValue(who.Guid.Full, out var sum);
                    byOwner[who.Guid.Full] = sum + d.TotalDamage;
                }

                var total = byOwner.Values.Sum();
                var topGuid = byOwner.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();

                // The point owner's clear rate (spawn_follow_extra): the whole creature, whoever hit it.
                if (Def.Outdoor && !tag.Snarl && !tag.SnarlAdd && campers.TryGetValue(tag.ForPlayer, out var feed))
                    PullPointsForward(feed.PointReadyAt, now, FeedClearRate(feed, tag, now), FollowType(tag));

                // A Gilded pack's fight runs from its first hit to its last kill (rich_rest_fight_mult).
                if (Def.Outdoor && tag.Rich && campers.TryGetValue(tag.ForPlayer, out var richOwner))
                    richOwner.RichFightFrom[tag.SpawnIndex] = Math.Min(richOwner.RichFightFrom.TryGetValue(tag.SpawnIndex, out var fightFrom) ? fightFrom : now,
                        tag.FirstHitAt > 0 ? tag.FirstHitAt : now);

                // A point is busy while its pack lives and rests once cleared (resting from each kill would bring
                // a long Gilded fight back on top of itself; at Heat 20 a swarm pack's first kill is minutes before its last).
                var packLives = Def.Outdoor && alive.Any(m => m != creature && !m.IsDead && m.FraysTag is { } mt && !mt.Snarl && !mt.SnarlAdd
                    && mt.SpawnIndex == tag.SpawnIndex && (Def.Group || mt.ForPlayer == tag.ForPlayer));

                // A group camp: the corpse is its top damage dealer's, and the point it came from rests once, on the shared stream.
                if (Def.Group && topGuid != 0)
                {
                    tag.LootOwner = topGuid;

                    if (Def.Outdoor && !packLives && !tag.SnarlAdd && campers.TryGetValue(tag.ForPlayer, out var stream))
                    {
                        stream.PointReadyAt[tag.SpawnIndex] = now + PointRestFor(stream, tag.SpawnIndex, now);

                        if (tag.Rich && PlayerManager.GetOnlinePlayer(tag.ForPlayer) is Player streamOwner)
                            TellCamp(streamOwner, GildedClearedText(stream.PointReadyAt[tag.SpawnIndex] - now));
                    }
                }

                foreach (var (guid, dmg) in byOwner)
                {
                    if (!campers.TryGetValue(guid, out var c) || PlayerManager.GetOnlinePlayer(guid) is not Player p)
                        continue;

                    var share = FraysRules.Share(dmg, total);

                    if (share <= 0)
                        continue;

                    // hc: where Heat and the Snarl bar live - the camper's own, or a group camp's shared keeper.
                    var hc = HeatCamper(c);
                    c.LastKillAt = now;
                    hc.LastKillAt = now;
                    c.MinuteRemovedWork += tag.Work * share;

                    if (Def.Outdoor)
                        c.MinuteSpawnedWork += tag.Work * share;

                    // Outdoors: the ground this creature came from rests before it spawns again.
                    if (Def.Outdoor && !Def.Group && guid == tag.ForPlayer && !packLives && !tag.SnarlAdd)
                    {
                        c.PointReadyAt[tag.SpawnIndex] = now + PointRestFor(c, tag.SpawnIndex, now);

                        // A cleared Gilded pack says when it respawns.
                        if (tag.Rich)
                            FraysManager.Tell(p, GildedClearedText(c.PointReadyAt[tag.SpawnIndex] - now));
                    }

                    if (guid == topGuid)
                    {
                        c.Kills++;
                        if (tag.Rich)
                            c.GildedKills++;
                        longTerm.Add((p, tag.Rich));   // Gilded: the high-value kills
                    }

                    FraysLongTerm.Bounty(p, "health", creature.Health.MaxValue * share);
                    FraysLongTerm.LucidKill(p);   // Lucid Night fills from Great Snarl kills; a kill only takes its cache
                    burn += tag.Work * share;
                    burnBy.Add((guid, p.Name, tag.Work * share));

                    var mods = c.Mods ?? new FraysCampMods();
                    c.HealthRemoved += tag.Work * share;

                    // the camp log (version 3): this camper's damage on a creature that died, and its health by share
                    c.Play.DamageOnKills += dmg;
                    c.Play.HealthKilled += creature.Health.MaxValue * share;
                    if (guid != topGuid)
                        c.Play.AssistKills++;
                    FraysMetrics.AddPositive(FraysMetrics.WorkRemoved, tag.Work * share);

                    // Heat points by health removed: work units x share x the Heat health it spawned with (x Quickening),
                    // whole points carried with the fraction kept.
                    hc.PointFraction += FraysSettings.Int(FraysSettings.HeatKillPoints) * FraysRules.HeatCredit(tag.Work, share, FraysCurve.Health(tag.Heat)) * mods.HeatGain
                        * FraysRules.EarlyClimbGain(hc.Rating, hc.Level, FraysSettings.Int(FraysSettings.HeatEarlyClimbUntil), FraysSettings.Number(FraysSettings.HeatEarlyClimbGain));
                    var whole = (int)Math.Floor(hc.PointFraction);

                    if (whole > 0)
                    {
                        hc.PointFraction -= whole;
                        AddPoints(hc, p, whole);

                        if (Def.Group)
                            MirrorGroupHeat();
                    }

                    var perCreature = FraysRules.PayPerCreature(hc.Level);   // pay_per_creature_by_heat: Embers, key and Snarl bars
                    var embers = FraysSettings.Number(FraysSettings.EmbersNormal) * tag.PayWork * share * perCreature
                        * FraysRules.LevelMult(c.Level, FraysSettings.Number(FraysSettings.EmbersPerLevel)) * mods.Embers
                        * (tag.Rich ? FraysSettings.Number(FraysSettings.RichMult) : 1);

                    // earn_knee (single-target and AoE builds earn the same): past it a kill pays fewer Embers and its corpse drops less.
                    var earn = EarnFactor(c, tag.Work * share, now);
                    embers *= earn;

                    if (guid == topGuid && earn < 1 && !tag.Snarl && !tag.SnarlCache)
                        ThinLoot(creature, earn);

                    payEmbers.Add((p, embers));

                    // XP and lum per kill (xp_kill_share of the hour, sized to xp_kill_pace standard creatures an hour), by the
                    // creature's worth and this camper's damage share, with the Heat multiplier; Gilded creatures rich_mult times.
                    var killShare = FraysSettings.Number(FraysSettings.XpKillShare);
                    var pace = FraysSettings.Number(FraysSettings.XpKillPace);
                    var richMult = tag.Rich ? FraysSettings.Number(FraysSettings.RichMult) : 1;
                    var richKill = tag.Rich ? FraysSettings.Number(FraysSettings.RichKillPayMult) : 1;   // Gilded XP and lum, apart from rich_mult

                    // Kill pay has a ceiling (without one, a camper killing 14 times the pace at Heat 17 would earn about
                    // 145 billion an hour, far above Threads). Each kill
                    // spends from an hourly budget of xp_kill_cap hours of kill share; past it the kill pays what is left.
                    // The key bar does not spend it (faster killers keep earning keys).
                    var killPaid = 1.0;
                    var cap = FraysRules.KillCapAt(hc.Level);

                    if (cap > 0 && pace > 0)
                    {
                        if (c.KillBudgetAt <= 0)
                            c.KillBudget = cap / 6;
                        else
                            c.KillBudget = FraysRules.RefillKillBudget(c.KillBudget, now - c.KillBudgetAt, cap);

                        c.KillBudgetAt = now;
                        (killPaid, c.KillBudget) = FraysRules.SpendKillBudget(c.KillBudget, tag.PayWork * share * richKill / pace, cap);
                    }

                    if (killShare > 0)
                    {
                        var killHeat = FraysCurve.Income(c.Level, p.Level ?? 1);
                        var killXp = FraysRules.KillPay(LevelXpPerHour(p.Level ?? 1) * killHeat, killShare, pace, tag.PayWork, share, richKill) * killPaid;
                        var killLum = FraysRules.KillPay(LevelLumPerHour(p.Level ?? 1) * killHeat, killShare, pace, tag.PayWork, share, richKill) * killPaid;
                        payKillXp.Add((p, c, killXp, killLum, tag.Snarl || tag.SnarlAdd));
                    }
                    payTo.Add(c);

                    // The key and Snarl bars' shared knee: past key_bar_knee an hour a kill fills both key_bar_past_knee as much.
                    var knee = FraysSettings.Number(FraysSettings.KeyBarKnee);
                    if (c.KeyKneeBudget < 0)
                        (c.KeyKneeBudget, c.KeyKneeAt) = (knee / 6, now);
                    double kneeFactor;
                    (kneeFactor, c.KeyKneeBudget) = FraysRules.KneeFactor(c.KeyKneeBudget, now - c.KeyKneeAt, tag.PayWork * share, knee, FraysSettings.Number(FraysSettings.KeyBarPastKnee));
                    c.KeyKneeAt = now;

                    // The Snarl: its kill ends its fight; otherwise health removed fills its bar.
                    // A group camp has one Snarl, on its shared keeper; its kill is credited once, to the top damage dealer.
                    if (tag.Snarl && hc.SnarlAlive == creature)
                    {
                        if (!Def.Group || guid == topGuid)
                            snarlKilled.Add((hc, p));
                    }
                    else if (tag.SnarlAdd)
                    {
                        // A Snarl add fills no bar; the last one down ends the wave (once, whoever's share it is).
                        if (hc.SnarlAdds.Remove(creature) && !hc.SnarlAdds.Any(a => a != null && !a.IsDead && !a.IsDestroyed))
                        {
                            hc.SnarlAdds.Clear();
                            addsKilled.Add((hc, p));
                        }
                    }
                    else if (SwarmFillsAdds(hc, tag))
                    {
                        // snarl_adds: brute packs fill the Snarl bar, swarm packs fill the Snarl adds bar.
                        hc.SnarlAdds.RemoveAll(a => a == null || a.IsDestroyed);   // a wave that faded without its kill ends unpaid

                        if (hc.SnarlAdds.Count == 0 && !addsReady.Any(s => s.Item1 == hc))
                        {
                            var before = hc.SnarlAddsBar;
                            hc.SnarlAddsBar += SnarlBarGain(tag.PayWork * share * kneeFactor * perCreature, hc.Level, tag.Rich, mods.SnarlSpeed)
                                * FraysSettings.Number(FraysSettings.SnarlAddsBarMult);
                            var after = hc.SnarlAddsBar;

                            if (after >= 1)
                                addsReady.Add((hc, p));
                            else if (FraysRules.BarStep(after, 10) > FraysRules.BarStep(before, 10))
                            {
                                TellCamp(p, $"Your Swarm Snarl stirs {FraysRules.Bar(after)}");
                                ShoutCamp(p, $"Your Swarm Snarl stirs {FraysRules.Bar(after)}");
                            }
                        }
                    }
                    else if (hc.Snarl != SnarlPact.Off && hc.SnarlAlive == null && !snarlReady.Any(s => s.Item1 == hc))
                    {
                        var before = hc.SnarlBar;
                        // Heat-weighted work (as the key bar), sized by Heat so a middling camper
                        // calls 2 / 3 / 4 an hour at Heat 0 / 10 / 20 and a faster one more. Never the hourly kill budget.
                        // A Gilded creature counts snarl_bar_gilded_mult times (x3).
                        hc.SnarlBar += SnarlBarGain(tag.PayWork * share * kneeFactor * perCreature, hc.Level, tag.Rich, mods.SnarlSpeed);
                        var after = hc.SnarlBar;

                        if (after >= 1)
                            snarlReady.Add((hc, p));
                        else if (FraysRules.BarStep(after, 10) > FraysRules.BarStep(before, 10))
                        {
                            TellCamp(p, $"Your {SnarlName(false)} stirs {FraysRules.Bar(after)}");
                            ShoutCamp(p, $"Your {SnarlName(false)} stirs {FraysRules.Bar(after)}");
                        }
                    }

                    // The key bar (chests follow Threads' 3 to 4 a 10 minute run): health
                    // removed fills it, faster at higher Heat; each full bar is a Vault Key (the Ember Vault
                    // is the one chest, no Ember Caches). Heat-weighted work, never the hourly kill budget.
                    var keyNeed = FraysRules.KeyBarNeed(FraysSettings.Text(FraysSettings.KeyBarWork), hc.Level);
                    if (keyNeed > 0)
                    {
                        c.KeyBar += tag.PayWork * share * richMult * kneeFactor * perCreature * mods.KeyBar;   // a Gilded creature fills it rich_mult times as fast; the shared knee

                        // The items its corpse could not hold, to the corpse's owner, thinned as the corpse is past earn_knee.
                        if (guid == topGuid && tag.OverflowKeys > 0)
                            c.KeyBar += tag.OverflowKeys * keyNeed * earn;

                        if (tag.Rich)
                            log.Info($"[FRAYS] gilded {Def.Id}: {p.Name} killed \"{creature.Name}\" (point {tag.SpawnIndex}, share {share:P0})");

                        while (c.KeyBar >= keyNeed)
                        {
                            c.KeyBar -= keyNeed;
                            c.KeyCaches++;
                            keyCaches.Add(p);
                            FraysMetrics.CachesKeyBar.Add(1);
                        }
                    }

                }
            }

            for (var i = 0; i < payEmbers.Count; i++)
            {
                var killPaid = FraysManager.PayEmbers(payEmbers[i].Player, payEmbers[i].Embers);
                payTo[i].EmbersEarned += killPaid;
                FraysMetrics.AddPositive(FraysMetrics.EmbersKill, killPaid);
            }

            foreach (var (p, c, xp, lum, fromSnarl) in payKillXp)
            {
                var wholeXp = (long)Math.Round(xp);
                var wholeLum = (long)Math.Round(lum);

                if (wholeXp > 0)
                {
                    p.GrantXP(wholeXp, XpType.Emote, ShareType.None);
                    c.XpPaid += wholeXp;
                    c.Play.XpFromKills += wholeXp;
                    if (fromSnarl)
                        c.Play.XpFromSnarls += wholeXp;
                    FraysMetrics.XpGranted.Add(wholeXp);
                }

                if (wholeLum > 0)
                {
                    p.GrantLuminance(wholeLum, XpType.Emote, ShareType.None);
                    c.LumPaid += wholeLum;
                    FraysMetrics.LumGranted.Add(wholeLum);
                }
            }

            FraysFrayWide.AddBurn(this, burn, burnBy);

            foreach (var (p, wasGilded) in longTerm)
            {
                FraysHunts.OnKill(p, creature.WeenieClassId, tag.Work, wasGilded);
                OnCodexKill(p, creature, tag, wasGilded);
                FraysLongTerm.Bounty(p, "kills", tag.Work);   // in standard creatures' worth, so every camp shape counts alike

                if (wasGilded)
                    FraysLongTerm.Bounty(p, "gilded", 1);
            }

            foreach (var p in keyCaches)
                FraysLongTerm.Give(p, FraysLongTerm.DreamKey, 1, "Your key bar is full: a Vault Key (it opens an Ember Vault by the Great Hall).");

            foreach (var (c, p) in snarlKilled)
                OnSnarlKilled(c, p, now);

            foreach (var (c, p) in snarlReady)
                SpawnSnarl(c, p, now);

            foreach (var (c, p) in addsKilled)
                OnSnarlKilled(c, p, now, adds: true);

            foreach (var (c, p) in addsReady)
                SpawnSnarlAdds(c, p, now);
        }

        /// <summary>An owner died: their own Heat drops (Auto mode then holds 2 below where they died for a while).</summary>
        public void OnOwnerDied(Player player, string cause = null)
        {
            int level;
            string settleText = null;
            var fade = new List<Creature>();
            var downleveled = 0;

            lock (Sync)
            {
                if (!Lit || !campers.TryGetValue(player.Guid.Full, out var c))
                    return;

                OwnerDeaths++;
                c.Deaths++;
                FraysMetrics.Deaths.Add(1);
                c.RatingRun.Clear();
                c.MinuteMinLevel = int.MaxValue;
                c.DeathCauses.Add(string.IsNullOrWhiteSpace(cause) ? "unknown" : cause);

                // A group camp: a death costs the shared Heat group_death_drop levels, for everyone.
                if (Def.Group)
                {
                    var hc = HeatCamper(c);
                    var (glvl, gprog) = FraysRules.DeathDrop(hc.Level, hc.Progress, FraysGroup.DeathDrop(FraysSettings.Int(FraysSettings.GroupDeathDrop)),
                        FraysSettings.Int(FraysSettings.HeatPointsBase), FraysSettings.Int(FraysSettings.HeatPointsPerLevel));
                    hc.Level = glvl;
                    hc.Progress = gprog;
                    hc.PointFraction = 0;
                    MirrorGroupHeat();
                    level = glvl;
                }
                else
                {
                    var died = c.Level;

                    // Brittle Camp: a death costs 3 levels instead of 2.
                    var drop = Math.Max(0, FraysSettings.Int(FraysSettings.HeatDeathDropLevels) + (c.Mods?.DeathDropLevels ?? 2) - 2);   // Phoenix Coal lowers it

                    var (lvl, prog) = FraysRules.DeathDrop(c.Level, c.Progress, drop,
                        FraysSettings.Int(FraysSettings.HeatPointsBase), FraysSettings.Int(FraysSettings.HeatPointsPerLevel));

                    c.Level = lvl;
                    c.Progress = prog;
                    c.PointFraction = 0;

                    level = lvl;

                    // Climbing's settle: dying too often holds the Heat below where the deaths come (FraysRules.Settle).
                    {
                        var at = Time.GetUnixTime();
                        c.RecentDeaths.Add((at, died));
                        c.RecentDeaths.RemoveAll(d => at - d.At > FraysSettings.Number(FraysSettings.HeatSettleWindowMinutes) * 60);
                        var settle = FraysRules.Settle(c.RecentDeaths, FraysSettings.Int(FraysSettings.HeatSettleDeaths), FraysSettings.Int(FraysSettings.HeatSettleDrop),
                            FraysSettings.Number(FraysSettings.HeatSettleHoldMinutes), FraysSettings.Number(FraysSettings.HeatSettleHoldMaxMinutes), c.Settles);
                        if (settle is var (settleCap, holdSeconds))
                        {
                            c.AutoCap = settleCap;
                            c.AutoCapUntil = at + holdSeconds;
                            if (c.Level > settleCap)
                            {
                                // Down to the cap now, not just no higher (FraysRules.SettledLevel): the creatures over it fade below.
                                c.Level = FraysRules.SettledLevel(c.Level, settleCap);
                                c.Progress = 0;
                                c.PointFraction = 0;
                                level = c.Level;
                            }
                            c.Settles++;
                            c.RecentDeaths.Clear();
                            settleText = $"You keep falling around Heat {died}. Your Heat holds at {settleCap} or below for {holdSeconds / 60:0} minutes, then climbs again (/fray setup).";
                            log.Info($"[FRAYS] {Def.Id} {Instance:X8}: {player.Name} settles at Heat {settleCap} for {holdSeconds / 60:0} min (settle {c.Settles})");
                        }
                    }
                }

                // The pack built at the old Heat would still be standing over the camper's respawn (and could kill
                // them again and again). Those above the cooled Heat fade; their points refill.
                if (FraysSettings.Toggle(FraysSettings.HeatDeathClearsPack))
                {
                    fade = alive.Where(m => m.FraysTag != null && (Def.Group || m.FraysTag.ForPlayer == c.Guid)
                        && FraysRules.FadesOnDeath(m.FraysTag.Heat, level, m.FraysTag.Snarl || m.FraysTag.SnarlAdd)).ToList();
                    alive.RemoveAll(fade.Contains);

                    // The Snarl and its adds stay (their bar and cache are the camper's), but drop to the cooled Heat (so a camper
                    // whose Heat fell does not die again to the same high-Heat creatures): their health
                    // shrinks by the Heat's health multiplier, keeping the share they had left. Hits and attack already
                    // count the lower of the creature's and the camper's Heat (HitHeat).
                    downleveled = DownlevelOnDeath(alive.Where(m => m.FraysTag != null && (Def.Group || m.FraysTag.ForPlayer == c.Guid)
                        && (m.FraysTag.Snarl || m.FraysTag.SnarlAdd) && m.FraysTag.Heat > level).ToList(), level, player.Level ?? 1);

                    // Their points refill at once at the cooled Heat (otherwise the faded camp
                    // comes back a pack a minute and the camper chases single creatures for minutes). Asleep until pulled.
                    var now = Time.GetUnixTime();
                    foreach (var (owner, index) in FraysRules.DeathRefillPoints(fade.Select(m => (m.FraysTag.ForPlayer, m.FraysTag.SpawnIndex)), Def.SpawnPoints.Count))
                    {
                        var stream = campers.TryGetValue(owner, out var oc) ? oc : c;
                        stream.RefillPoints.Add(index);
                        stream.PointReadyAt[index] = now;
                        stream.NextSpawnAt = Math.Min(stream.NextSpawnAt, now);
                    }
                }
            }

            foreach (var m in fade)
            {
                try
                {
                    if (!m.IsDestroyed && !m.IsDead)
                        m.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[FRAYS] {Def.Id}: death fade threw", ex);
                }
            }

            if (fade.Count > 0)
                log.Info($"[FRAYS] {Def.Id} {Instance:X8}: {player.Name} died; {fade.Count} creatures above Heat {level} faded");

            if (downleveled > 0)
                log.Info($"[FRAYS] {Def.Id} {Instance:X8}: {player.Name} died; {downleveled} Snarl creatures dropped to Heat {level}");

            var snarlText = downleveled > 0 ? " The Snarl's brood weakens to match." : "";

            if (Def.Group)
            {
                FraysManager.Tell(player, $"You have fallen. The group's Heat cools to {level}.{(fade.Count > 0 ? $" {fade.Count} of its fiercer creatures fade." : "")}{snarlText}");
                TellGroup(player, $"{player.Name} has fallen. The group's Heat cools to {level}.");
            }
            else
                FraysManager.Tell(player, $"You have fallen. Your Heat cools to {level}.{(fade.Count > 0 ? $" {fade.Count} of its fiercer creatures fade." : "")}{snarlText}");

            if (settleText != null)
                FraysManager.Tell(player, settleText);
        }

        /// <summary>
        /// A camper's death: their Snarl and its adds above the cooled Heat drop to it. Health shrinks by the
        /// Heat's health multiplier (FraysLevelScale.HeatHealthMult) and keeps the share it had; kill pay follows the new Heat.
        /// Returns how many dropped.
        /// </summary>
        private static int DownlevelOnDeath(List<Creature> snarls, int level, int camperLevel)
        {
            var n = 0;

            foreach (var m in snarls)
            {
                try
                {
                    if (m.IsDestroyed || m.IsDead || m.FraysTag == null)
                        continue;

                    var tag = m.FraysTag;
                    var oldMult = FraysLevelScale.HeatHealthMult(camperLevel, tag.Heat, tag.ThreadsShaped);
                    var newMult = FraysLevelScale.HeatHealthMult(camperLevel, level, tag.ThreadsShaped);
                    var (max, current) = FraysRules.DownlevelHealth(m.Health.MaxValue, m.Health.Current, oldMult, newMult);

                    FraysLevelScale.SetMaxHealth(m, max);
                    m.Health.Current = (uint)Math.Min(current, m.Health.MaxValue);
                    tag.Heat = level;
                    tag.HeatHealth = newMult;
                    n++;
                }
                catch (Exception ex)
                {
                    log.Error("[FRAYS] Snarl down-level threw", ex);
                }
            }

            return n;
        }

        /// <summary>A hit from this fire's creature on a camper, for the camp report's damage-by-element line.</summary>
        public void OnDamageTaken(Player player, DamageType type, float amount)
        {
            lock (Sync)
            {
                if (campers.TryGetValue(player.Guid.Full, out var c))
                {
                    var key = type == DamageType.Undef ? "other" : type.GetName().ToLowerInvariant();
                    c.DamageTakenBy.TryGetValue(key, out var sum);
                    c.DamageTakenBy[key] = sum + amount;
                }
            }
        }

        /// <summary>An owner hit, or was hit by, one of this fire's monsters: they count as active.</summary>
        [ThreadStatic]
        private static bool linking;

        /// <summary>True when two creatures belong to the same pack: one camper's stream, one spawn point (a point fields one pack at a time).</summary>
        public static bool SamePack(FraysCreatureTag a, FraysCreatureTag b) =>
            a != null && b != null && a.Fire == b.Fire && a.ForPlayer == b.ForPlayer && a.SpawnIndex == b.SpawnIndex
            && !a.Snarl && !b.Snarl && !a.SnarlAdd && !b.SnarlAdd;

        /// <summary>
        /// Pack linking (pack_link): outdoors, when one creature of a pack is hit
        /// or wakes on its owner, the rest of that pack comes for <paramref name="target"/> too. Neighbouring packs never
        /// do (Monster_Awareness.AlertFriendly asks SamePack). Snarls keep their own rules.
        /// </summary>
        public void LinkPack(Creature woke, Creature target, bool fromWake = false)
        {
            var tag = woke?.FraysTag;

            if (linking || tag == null || tag.Snarl || tag.SnarlAdd || !Def.Outdoor || target == null)
                return;

            if (!tag.Accepts(target))
                return;

            var link = FraysSettings.Toggle(FraysSettings.PackLink);
            List<Creature> mates;
            List<int> awakePoints;

            lock (Sync)
            {
                mates = link ? alive.Where(m => m != null && m != woke && !m.IsDead && !m.IsDestroyed && !m.IsAwake && SamePack(m.FraysTag, tag)).ToList() : new List<Creature>();
                awakePoints = fromWake
                    ? alive.Where(m => m != null && m != woke && !m.IsDead && m.IsAwake && m.FraysTag != null && !m.FraysTag.Snarl && m.FraysTag.ForPlayer == tag.ForPlayer && m.FraysTag.SpawnIndex != tag.SpawnIndex)
                        .Select(m => m.FraysTag.SpawnIndex).Distinct().OrderBy(i => i).ToList()
                    : null;
            }

            // One line per pack waking, so a big pull can be read back: which point, how far its owner was, what else was already awake.
            if (fromWake && log.IsInfoEnabled)
            {
                var pt = PointPos(tag.SpawnIndex);
                var tp = target.Location?.Pos ?? System.Numerics.Vector3.Zero;
                var dist = Math.Sqrt((pt.X - tp.X) * (pt.X - tp.X) + (pt.Y - tp.Y) * (pt.Y - tp.Y));
                var near = woke.PhysicsObj != null && target.PhysicsObj != null ? Math.Sqrt(woke.PhysicsObj.get_distance_sq_to_object(target.PhysicsObj, true)) : -1;
                log.Info($"[FRAYS] pull {Def.Id}: point {tag.SpawnIndex} ({KindOf(tag.SpawnIndex)}) woke on {target.Name} {near:F1} m from {woke.Name} ({dist:F1} m from the point), {mates.Count} mates linked; other packs already awake: [{string.Join(",", awakePoints)}]");
            }

            if (mates.Count == 0)
                return;

            linking = true;

            try
            {
                foreach (var m in mates)
                {
                    m.AttackTarget = target;
                    m.WakeUp(false);
                }
            }
            finally
            {
                linking = false;
            }
        }

        /// <summary>FraysSessions.OnDamage: damage a camper dealt to, or took from, this fire's creatures.</summary>
        internal void NoteDamage(Player player, uint amount, bool dealt, string weapon = null)
        {
            lock (Sync)
            {
                if (!campers.TryGetValue(player.Guid.Full, out var c))
                    return;

                var now = Time.GetUnixTime();

                if (dealt)
                {
                    c.Play.DamageDealt += amount;
                    c.Play.LastDealtAt = now;
                    if (weapon != null && (c.Play.DamageByWeapon.Count < 32 || c.Play.DamageByWeapon.ContainsKey(weapon)))
                        c.Play.DamageByWeapon[weapon] = c.Play.DamageByWeapon.GetValueOrDefault(weapon) + amount;
                }
                else
                    c.Play.LastHurtAt = now;
            }
        }

        /// <summary>FraysSessions.OnSourcedHit: damage a camper dealt to this fire's creatures, by source.</summary>
        internal void NoteSource(Player player, string kind, uint amount, bool? cleave = null)
        {
            lock (Sync)
            {
                if (campers.TryGetValue(player.Guid.Full, out var c))
                {
                    FraysSessions.Add(c.Play.DamageBySource, kind, amount, (a, b) => a + b);

                    // the camper's own melee hit: on the creature struck, or a cleave's extra one
                    if (cleave == true)
                        c.Play.CleaveHits++;
                    else if (cleave == false)
                        c.Play.MeleeStrikes++;
                }
            }
        }

        /// <summary>FraysSessions.OnCast: a camper finished casting a spell.</summary>
        /// <summary>FraysSessions.OnShot: a camper fired a bow shot.</summary>
        internal void NoteShot(Player player)
        {
            lock (Sync)
            {
                if (campers.TryGetValue(player.Guid.Full, out var c))
                    c.Play.MissileShots++;
            }
        }

        internal void NoteCast(Player player, string spell)
        {
            lock (Sync)
            {
                if (campers.TryGetValue(player.Guid.Full, out var c))
                    FraysSessions.Add(c.Play.Casts, spell, 1, (a, b) => a + b);
            }
        }

        public void OnCombat(Player player)
        {
            lock (Sync)
            {
                if (campers.TryGetValue(player.Guid.Full, out var c))
                    c.LastActiveAt = Time.GetUnixTime();
            }
        }

        /// <summary>A fire monster's corpse: short rot while it holds loot, and the oldest goes quickly past the cap.</summary>
        public void OnCorpse(Corpse corpse)
        {
            corpse.TimeToRot = FraysSettings.Number(FraysSettings.CorpseRotSeconds);

            lock (Sync)
            {
                corpses.AddLast(corpse);

                while (corpses.First != null && (corpses.First.Value == null || corpses.First.Value.IsDestroyed))
                    corpses.RemoveFirst();

                var cap = FraysSettings.Int(FraysSettings.CorpseCap);
                var live = corpses.Where(c => c != null && !c.IsDestroyed).ToList();

                for (var i = 0; i < live.Count - cap; i++)
                {
                    if ((live[i].TimeToRot ?? 0) > Corpse.EmptyDecayTime)
                        live[i].TimeToRot = Corpse.EmptyDecayTime;
                }

                if (corpses.Count > cap * 4)
                {
                    foreach (var dead in corpses.Where(c => c == null || c.IsDestroyed).ToList())
                        corpses.Remove(dead);
                }
            }
        }

        /// <summary>
        /// A monster turned home: <paramref name="escaped"/> when it had stood outside its zone past the grace time (the
        /// number the 2-hour leash test watches), else an ordinary walk home from somewhere other than its spawn point.
        /// </summary>
        public void OnLeashReturn(Creature creature, bool escaped)
        {
            lock (Sync)
            {
                if (escaped)
                    ZoneEscapes++;
                else
                    LeashReturns++;
            }
        }

        public void RecordDistance(double metres)
        {
            if (metres > FarthestFromFire)
                FarthestFromFire = metres;
        }

        /// <summary>Staff test aid: sets a player's own Heat here (clamped to the cap). False when they have no camp here.</summary>
        public bool AdminSetHeat(Player player, int level)
        {
            lock (Sync)
            {
                if (!Lit || !campers.TryGetValue(player.Guid.Full, out var c))
                    return false;

                c = HeatCamper(c);   // a group camp: its shared Heat

                c.Level = Math.Clamp(level, 0, FraysSettings.Int(FraysSettings.HeatCap));
                c.Progress = 0;
                c.PeakLevel = Math.Max(c.PeakLevel, c.Level);
                return true;
            }
        }

        /// <summary>Staff testing aid: calls the player's Snarl now, as if its bar had filled. Run on the landblock.</summary>
        public string AdminSnarl(Player player)
        {
            FraysCamper c;

            lock (Sync)
            {
                if (!Lit || !campers.TryGetValue(player.Guid.Full, out c))
                    return "You have no camp at that fire (light it and stand at it first).";

                c = HeatCamper(c);   // a group camp: its shared Heat

                if (c.SnarlAlive != null && !c.SnarlAlive.IsDead && !c.SnarlAlive.IsDestroyed)
                    return "Your Snarl is already out.";
            }

            Landblock.EnqueueAction(new ActionEventDelegate(() => SpawnSnarl(c, player, Time.GetUnixTime())));
            return "Calling your Snarl.";
        }

        /// <summary>Staff testing aid: an Ember Boon offer owed now (the camp tick shows it with its popup).</summary>
        public string AdminBoon(Player player)
        {
            lock (Sync)
            {
                if (!Lit || !campers.TryGetValue(player.Guid.Full, out var c))
                    return "You have no camp at that fire (light it and stand at it first).";

                c = HeatCamper(c);   // a group camp: its shared Heat

                c.OffersOwed++;
                return "An Ember Boon offer comes in a moment.";
            }
        }

        /// <summary>Points into a level and the points it needs.</summary>
        public static (int Have, int Need) ProgressOf(FraysCamper c) =>
            (c.Progress, FraysRules.PointsToNext(c.Level, FraysSettings.Int(FraysSettings.HeatPointsBase), FraysSettings.Int(FraysSettings.HeatPointsPerLevel)));
    }
}
