using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.MonsterEffects;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>The bank of eight wisp buffs, in bank order (wisp wcids 1007972 to 1007979 follow this order).</summary>
    public enum GolemBuff
    {
        Might = 0,
        KeenEdge = 1,
        Frenzy = 2,
        Vigor = 3,
        Arcana = 4,
        Ruin = 5,
        Bulwark = 6,
        Mending = 7,
    }

    /// <summary>The encounter's phase. Dormant until the first accepted use; Shaping through the rounds; Awakened once the monster stands; Done after its death (or a failed awakening).</summary>
    public enum GolemPhase { Dormant, Shaping, Awakened, Done }

    /// <summary>
    /// The golem's plate skin: the metal palette laid over the Celdon plate texture the weenies swap in. CopperBronze is
    /// what the weenies ship; Silver and Gold are selected by the row's config "skin" key.
    /// </summary>
    public enum GolemSkin { CopperBronze, Silver, Gold }

    /// <summary>What a use of the dormant golem did.</summary>
    public enum GolemUseOutcome { RunOver, NotInside, Shaping, Spent, Begun }

    /// <summary>One validated wisp position: its candidate index (0 to 8 are the round-set slots on the configured ring, see GolemEncounterType.CandidateSpots), compass bearing, landblock-local x, y and probed ground z, and its cell.</summary>
    public readonly record struct GolemWispSlot(int Index, float BearingDegrees, float X, float Y, float Z, uint Cell);

    /// <summary>One wisp spawn attempt: it stands on its spot; the slot refused it (it did not enter, or entered off its spot); or its wisp could not be built at all.</summary>
    public enum GolemWispTry { Placed, SlotFailed, BuffFailed }

    /// <summary>A ground probe hit: the probed ground height and the cell it is in.</summary>
    public readonly record struct GolemGroundHit(float Z, uint Cell);

    /// <summary>
    /// What a set of picks does to the awakened golem (Docs/Threads/ENCOUNTERS.md "Craftwork Golem"): the four ratings
    /// add, the five factors multiply. Every per-pick size is scaled by dynamic_dungeons_golem_pick_scale.
    /// </summary>
    public readonly record struct GolemBuffEffect(int DamageRating, int CritRating, int CritDamageRating, int DamageResistRating,
        double AttackSpeedMult, double HealthMult, double SpellChanceMult, double CastSpeedMult, double HealthRateMult);

    /// <summary>A golem row's parsed config (encounters.json "config"). Defaults when a key is absent.</summary>
    public sealed class GolemConfig
    {
        public uint DormantWcid { get; init; } = GolemEncounterType.DefaultDormantWcid;
        public uint GolemWcid { get; init; } = GolemEncounterType.DefaultGolemWcid;

        /// <summary>The eight wisp wcids in bank order.</summary>
        public IReadOnlyList<uint> WispWcids { get; init; } = GolemEncounterType.DefaultWispWcids;

        public int Rounds { get; init; } = 3;
        public double RoundSeconds { get; init; } = 30.0;

        /// <summary>The plate skin's metal. CopperBronze is baked into the weenies; another value replaces the palette rows at creation.</summary>
        public GolemSkin Skin { get; init; } = GolemSkin.CopperBronze;

        /// <summary>The wisp ring's radius, metres.</summary>
        public float RingRadius { get; init; } = 3.0f;

        /// <summary>The most a wisp position's ground may differ in height from the golem's ground, metres (the flat-ground rule).</summary>
        public float HeightTolerance { get; init; } = 0.5f;

        public const int MinRounds = 1, MaxRounds = 6;
        public const double MinRoundSeconds = 5.0, MaxRoundSeconds = 300.0;
        /// <summary>
        /// The ring radius bounds. The floor is 1.85 m (code review 2026-11-11): adjacent round-set slots 40 degrees apart
        /// sit 2 r sin(20) apart, which is below MinWispSpacing (1.25 m) under about 1.83 m, so a smaller ring would lose slots.
        /// </summary>
        public const float MinRingRadius = 1.85f, MaxRingRadius = 6.0f;
        public const float MinHeightTolerance = 0.1f, MaxHeightTolerance = 3.0f;
    }

    /// <summary>
    /// One golem encounter's state. The phase, the round, the offered wisps and the pick counts change only under the
    /// lock; the world objects are touched only on the golem's landblock queue. Player-free, so the tests drive it
    /// with guids.
    /// </summary>
    public sealed class GolemState
    {
        private readonly object gate = new object();

        private GolemPhase phase = GolemPhase.Dormant;
        private int round;
        private int generation;
        private bool roundOpen;
        private readonly int[] counts = new int[GolemEncounterType.BankSize];
        private readonly List<GolemBuff> picked = new List<GolemBuff>();
        private readonly Dictionary<uint, GolemBuff> wispBuffs = new Dictionary<uint, GolemBuff>();
        private GolemBuff[] offered = Array.Empty<GolemBuff>();
        private uint awakenedGuid;
        private bool dead;

        public GolemState(ThreadEncounterPlaceRequest request, GolemConfig config, IReadOnlyList<GolemWispSlot> slots)
        {
            Request = request;
            Config = config ?? new GolemConfig();
            Slots = slots ?? Array.Empty<GolemWispSlot>();
        }

        public GolemConfig Config { get; }

        /// <summary>The validated wisp positions (two to nine; the nine round-set slots on a flat ring), checked at placement.</summary>
        public IReadOnlyList<GolemWispSlot> Slots { get; }

        /// <summary>The Place request (run, the golem's landblock, context, point, seed). Null once the run ended.</summary>
        public volatile ThreadEncounterPlaceRequest Request;

        public uint DormantGuid;
        public WorldObject Dormant;

        /// <summary>The awakened golem (golem landblock queue writes it; read by the aura and the death hook).</summary>
        public volatile Creature Awakened;

        /// <summary>The current round's wisp objects (golem landblock queue only).</summary>
        internal readonly List<WorldObject> Wisps = new List<WorldObject>();

        public GolemPhase Phase { get { lock (gate) return phase; } }

        /// <summary>The 0-based round now open (or next to open).</summary>
        public int Round { get { lock (gate) return round; } }

        public int Generation { get { lock (gate) return generation; } }

        public bool RoundOpen { get { lock (gate) return roundOpen; } }

        public IReadOnlyList<GolemBuff> Offered { get { lock (gate) return offered.ToList(); } }

        /// <summary>Every accepted pick, in order.</summary>
        public IReadOnlyList<GolemBuff> Picked { get { lock (gate) return picked.ToList(); } }

        /// <summary>The pick count per buff, bank order.</summary>
        public int[] Counts { get { lock (gate) return (int[])counts.Clone(); } }

        public uint AwakenedGuid { get { lock (gate) return awakenedGuid; } }

        /// <summary>
        /// A use of the dormant golem. Order: the run is over (silent), the user is not a member inside, the golem is
        /// being shaped, it is spent. Only from Dormant is the encounter claimed (Dormant to Shaping), so two uses in one
        /// tick begin one shaping.
        /// </summary>
        public GolemUseOutcome TryBegin(bool runOver, bool userInside)
        {
            if (runOver)
                return GolemUseOutcome.RunOver;

            if (!userInside)
                return GolemUseOutcome.NotInside;

            lock (gate)
            {
                switch (phase)
                {
                    case GolemPhase.Shaping:
                        return GolemUseOutcome.Shaping;
                    case GolemPhase.Awakened:
                    case GolemPhase.Done:
                        return GolemUseOutcome.Spent;
                }

                phase = GolemPhase.Shaping;
                round = 0;
                roundOpen = false;
                return GolemUseOutcome.Begun;
            }
        }

        /// <summary>
        /// Opens round <paramref name="forRound"/> with <paramref name="buffs"/> offered (one per wisp guid in
        /// <paramref name="wispGuids"/>, the same order; a buff with no wisp is still offered to the timeout). Returns the
        /// round's generation, or 0 when the encounter is not shaping this round any more.
        /// </summary>
        public int OpenRound(int forRound, IReadOnlyList<GolemBuff> buffs, IReadOnlyList<uint> wispGuids)
        {
            lock (gate)
            {
                if (phase != GolemPhase.Shaping || round != forRound || round >= Config.Rounds || roundOpen || buffs == null || buffs.Count == 0)
                    return 0;

                offered = buffs.ToArray();
                wispBuffs.Clear();

                for (var i = 0; i < (wispGuids?.Count ?? 0) && i < buffs.Count; i++)
                    if (wispGuids[i] != 0)
                        wispBuffs[wispGuids[i]] = buffs[i];

                roundOpen = true;
                return ++generation;
            }
        }

        /// <summary>The buff a wisp of the open round carries; false for any other object.</summary>
        public bool TryGetWispBuff(uint guid, out GolemBuff buff)
        {
            lock (gate)
            {
                buff = default;
                return roundOpen && wispBuffs.TryGetValue(guid, out buff);
            }
        }

        /// <summary>
        /// A wisp use: the lookup of the wisp's buff and the pick latch under ONE lock, so the buff and the generation it
        /// is picked for are read together. False for an object that is not a wisp of the open round, or a round already
        /// spent. <paramref name="buff"/> is the bound buff on true.
        /// </summary>
        public bool TryPickWisp(uint guid, out GolemBuff buff, out int pickedRound, out bool wasLast)
        {
            lock (gate)
            {
                buff = default;
                pickedRound = round;
                wasLast = false;

                if (!roundOpen || !wispBuffs.TryGetValue(guid, out buff))
                    return false;

                return PickLocked(generation, buff, out pickedRound, out wasLast);
            }
        }

        /// <summary>
        /// THE pick latch: true exactly once per round, for the first pick (a wisp use or the timeout) of generation
        /// <paramref name="gen"/> whose buff is one the round offered. The round closes, the pick is counted, and
        /// <paramref name="pickedRound"/> (0-based) and <paramref name="wasLast"/> say what was spent.
        /// </summary>
        public bool TryPick(int gen, GolemBuff buff, out int pickedRound, out bool wasLast)
        {
            lock (gate)
                return PickLocked(gen, buff, out pickedRound, out wasLast);
        }

        /// <summary>The latch body; the caller holds the gate.</summary>
        private bool PickLocked(int gen, GolemBuff buff, out int pickedRound, out bool wasLast)
        {
            pickedRound = round;
            wasLast = false;

            if (phase != GolemPhase.Shaping || !roundOpen || gen != generation || !offered.Contains(buff))
                return false;

            roundOpen = false;
            counts[(int)buff]++;
            picked.Add(buff);
            wispBuffs.Clear();
            pickedRound = round;
            round++;
            wasLast = round >= Config.Rounds;
            return true;
        }

        /// <summary>The swap is done: Shaping to Awakened, with the monster's guid. False when the encounter is not waiting to awaken.</summary>
        public bool MarkAwakened(uint guid)
        {
            lock (gate)
            {
                if (phase != GolemPhase.Shaping || roundOpen || round < Config.Rounds)
                    return false;

                phase = GolemPhase.Awakened;
                awakenedGuid = guid;
                return true;
            }
        }

        /// <summary>The awakening failed: the encounter is spent with no golem.</summary>
        public void MarkFailed()
        {
            lock (gate)
                phase = GolemPhase.Done;
        }

        /// <summary>THE death latch: true exactly once, for the awakened golem's own guid.</summary>
        public bool TryMarkDead(uint guid)
        {
            lock (gate)
            {
                if (phase != GolemPhase.Awakened || dead || guid == 0 || guid != awakenedGuid)
                    return false;

                dead = true;
                phase = GolemPhase.Done;
                return true;
            }
        }
    }

    /// <summary>
    /// The Craftwork Golem (Docs/Threads/ENCOUNTERS.md "Craftwork Golem"), dungeon and outdoor runs alike. A dormant golem
    /// waits at the picked point. A member inside the run uses it and the shaping begins: each round three wisps (three
    /// distinct buffs of the bank of eight) stand on a ring around it, and the first member inside to use one binds that
    /// buff into the golem and spends the round (or, after roundSeconds, the golem draws one of the three itself). Repeat
    /// picks stack. After the last round the dormant golem is swapped for the awakened monster with every buff written
    /// before EnterWorld; its death (a bonus kill, never the clear) grants the shared encounter prize to every online
    /// member inside the run.
    ///
    /// Threading. Place runs on the point's landblock queue: it validates the whole wisp ring there (the flat-ground rule)
    /// and enters the dormant golem. OnUse runs on the user's thread: every phase change is one locked call on
    /// <see cref="GolemState"/>, and every world-object change (wisps, the swap, the aura) is queued onto the golem's
    /// landblock (the aura onto the awakened golem's own). Every queued action does nothing once the run or its encounters
    /// ended. OnRunEnded releases in-memory state only.
    /// </summary>
    public sealed class GolemEncounterType : IThreadEncounterType, IThreadEncounterUsable, IThreadEncounterDeathAware
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindName = "golem";

        public const int BankSize = 8;

        /// <summary>Wisps offered per round.</summary>
        public const int WispsPerRound = 3;

        /// <summary>Round sets of fixed bearings: set s uses s x 40 + k x 120 degrees (k = 0, 1, 2), so the nine slots are 40 degrees apart.</summary>
        public const int BearingSets = 3;

        /// <summary>A wisp that entered the world further than this from its probed spot was moved: it is taken down and the next validated slot is tried.</summary>
        public const float PlacementTolerance = 0.05f;

        /// <summary>The fewest wisps a round ever shows (owner ruling 2026-11-11). A round that cannot place this many shows none and the golem draws one itself.</summary>
        public const int MinWispsPerRound = 2;

        /// <summary>The most validated wisp spots a placement keeps (a flat ring keeps exactly the nine round-set slots).</summary>
        public const int MaxPoolSlots = BearingSets * WispsPerRound;

        /// <summary>The flat-ground rule's widened search: the step between the extra rings tried in and out of the configured one, metres.</summary>
        public const float ExtraRadiusStep = 0.75f;

        /// <summary>No extra ring comes closer to the golem than this, metres (its body radius is about 1.33 m at DefaultScale 1.25: Setup 0x02000055 radius 1.06 x 1.25).</summary>
        public const float MinWispGolemDistance = 2.0f;

        /// <summary>Two kept wisp spots are at least this far apart, metres (adjacent round-set slots on a 3 m ring are about 2.05 m apart).</summary>
        public const float MinWispSpacing = 1.25f;

        /// <summary>While fewer than three spots qualify, the height tolerance relaxes by this per step, up to <see cref="MaxRelaxedTolerance"/>.</summary>
        public const float ToleranceRelaxStep = 0.25f;
        public const float MaxRelaxedTolerance = 1.5f;

        /// <summary>The spawn's tries per slot (pending buffs) before the slot is given up, and the slots a buff may fail on before it leaves the round.</summary>
        public const int MaxBuffTriesPerSlot = 2;
        public const int MaxSlotFailuresPerBuff = 3;

        /// <summary>A dungeon wisp spot is reached when the probe body's horizontal walk from the golem ends within this of it (a wall stops it short).</summary>
        public const float DungeonReachTolerance = 0.25f;

        /// <summary>Seconds between a pick (the round's wisps vanish) and the next round's wisps.</summary>
        public const double NextRoundDelaySeconds = 1.0;

        /// <summary>The landblock-local margin RejectPoint keeps around the ring on an outdoor point.</summary>
        public const float LandblockMargin = 0.5f;
        public const float LandblockEdge = 192f;

        public const uint DefaultDormantWcid = 1007970;
        public const uint DefaultGolemWcid = 1007971;

        /// <summary>
        /// The ObjDesc PaletteBase both golem weenies carry: 0x0400007E, the DefaultPaletteId of SurfaceTexture 0x0500009E
        /// (the Celdon plate texture; read from client_portal.dat 2026-11-10). Raw palette rows only apply to textures whose
        /// default palette equals it, so the plate (swapped in on the 12 body parts) takes the tint and the red eye texture
        /// 0x05000410, whose own default is 0x04000B9D, keeps its native colour.
        /// </summary>
        public const uint SkinPaletteBase = 0x0400007E;

        /// <summary>The metal palettes (Source/.claude/skills/weenie-generator/references/wiki/palette_catalog.tsv): copper-bronze, silver, gold.</summary>
        public const uint CopperBronzePaletteId = 0x0400068C, SilverPaletteId = 0x04000487, GoldPaletteId = 0x0400044F;

        /// <summary>The skin config names, in enum order, as written in encounters.json.</summary>
        public static readonly IReadOnlyList<string> SkinNames = new[] { "copperBronze", "silver", "gold" };

        /// <summary>The palette a skin lays over the plate texture.</summary>
        public static uint SkinPaletteId(GolemSkin skin) => skin switch
        {
            GolemSkin.Silver => SilverPaletteId,
            GolemSkin.Gold => GoldPaletteId,
            _ => CopperBronzePaletteId,
        };

        /// <summary>The eight wisps in bank order (Content/sql/weenies/1007972 to 1007979; wcid-registry.d/wcid-1007970.tsv).</summary>
        public static readonly IReadOnlyList<uint> DefaultWispWcids = new uint[] { 1007972, 1007973, 1007974, 1007975, 1007976, 1007977, 1007978, 1007979 };

        /// <summary>The wisp names in bank order (the weenies' Name rows).</summary>
        public static readonly IReadOnlyList<string> WispNames = new[]
        {
            "Wisp of Might", "Wisp of the Keen Edge", "Wisp of Frenzy", "Wisp of Vigor",
            "Wisp of Arcana", "Wisp of Ruin", "Wisp of the Bulwark", "Wisp of Mending",
        };

        /// <summary>
        /// The aura colour per buff, bank order. All are present in the golem's PhysicsEffectTable 0x34000060 except
        /// EnchantUpGrey, so Bulwark uses ShieldUpGrey (read from client_portal.dat, 2026-11-10).
        /// </summary>
        public static readonly IReadOnlyList<PlayScript> AuraScripts = new[]
        {
            PlayScript.EnchantUpRed, PlayScript.EnchantUpOrange, PlayScript.EnchantUpYellow, PlayScript.EnchantUpGreen,
            PlayScript.EnchantUpBlue, PlayScript.EnchantUpPurple, PlayScript.ShieldUpGrey, PlayScript.EnchantUpWhite,
        };

        // Per-pick sizes at pick_scale 1.
        public const int MightDamageRating = 10;
        public const int KeenEdgeCritRating = 10;
        public const int RuinCritDamageRating = 15;
        public const int BulwarkDamageResistRating = 10;
        public const double FrenzyAttackSpeed = 0.20;
        public const double VigorHealth = 0.25;
        public const double ArcanaSpellChance = 0.50;
        public const double ArcanaCastSpeed = 0.15;
        public const double MendingHealthRate = 1.00;

        public const string RefusedLine = "Only members of this thread can shape the golem.";
        /// <summary>The first round's line, sent once its wisps stand: the count spelled out (owner ruling 2026-11-11). Pure.</summary>
        public static string BeginLine(int wisps) => $"The golem's core flickers. {CountWord(wisps)} wisps drift free - choose one to bind into it.";
        public const string ShapingLine = "The golem is already being shaped.";
        /// <summary>Every later round's line, sent once its wisps stand: the count spelled out. Pure.</summary>
        public static string NextRoundLine(int wisps) => $"{CountWord(wisps)} more wisps drift free.";

        /// <summary>A round that could not stand two wisps shows none, and the golem picks at once with this line (owner ruling 2026-11-11). Pure.</summary>
        public static string NoWispLine(GolemBuff buff) => $"No wisp can settle here. The golem chooses for itself: {WispNames[(int)buff]}.";

        /// <summary>A round's wisp count as a word: Two or Three (the only counts a shown round has); digits otherwise. Pure.</summary>
        public static string CountWord(int n) => n switch
        {
            2 => "Two",
            3 => "Three",
            _ => n.ToString(CultureInfo.InvariantCulture),
        };
        public const string AwakenLine = "The Craftwork Golem awakens!";
        public const string DeathLine = "The Craftwork Golem falls apart.";

        public static string PickLine(string name, GolemBuff buff, int roundNumber, int total)
            => $"{name} binds the {WispNames[(int)buff]} into the golem. (Round {roundNumber.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)})";

        /// <summary>
        /// The pick announcement: <paramref name="line"/> when the caller supplied one (a short round's NoWispLine), else
        /// the player's PickLine, else (no picker) the TimeoutLine. Pure.
        /// </summary>
        public static string AnnounceText(string line, string pickerName, GolemBuff buff, int roundNumber, int total)
            => line ?? (pickerName == null ? TimeoutLine(buff, roundNumber, total) : PickLine(pickerName, buff, roundNumber, total));

        public static string TimeoutLine(GolemBuff buff, int roundNumber, int total)
            => $"The golem draws the {WispNames[(int)buff]} into itself. (Round {roundNumber.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)})";

        public string Kind => KindName;

        /// <summary>Test seam: the object factory. Production is WorldObjectFactory.</summary>
        internal static Func<uint, WorldObject> CreateObject = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);

        /// <summary>
        /// Test seam: the ground under the golem and its ring for a request: the golem's own ground hit, and a probe for
        /// any (x, y) on its landblock. Production probes the live landblock (outdoor terrain, or the dungeon floor).
        /// </summary>
        internal static Func<ThreadEncounterPlaceRequest, GolemConfig, (GolemGroundHit? Golem, Func<float, float, GolemGroundHit?> At, Action Release)> GroundFor = ProductionGround;

        /// <summary>Test seam: the online roster members inside the run. Production is ThreadRunPresence.OnlineMembersInside.</summary>
        internal static Func<ThreadDungeonRun, List<Player>> MembersInside = run => ThreadRunPresence.OnlineMembersInside(run);

        /// <summary>Test seam: the prize grant. Production is ThreadEncounterPrize.Grant.</summary>
        internal static Func<ThreadDungeonRun, Player, string, bool> GrantPrize = ThreadEncounterPrize.Grant;

        // ---- skin --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Lays a skin's palette over a golem's biota: when the skin is not the weenie default (CopperBronze), every palette
        /// row is replaced by the chosen palette across the full 0-256 span as two rows ((0,128) and (128,128), the shape the
        /// client needs; no length may reach past 255 per byte). The texture swap stays from the weenie. Returns whether the
        /// rows were replaced. Call before the object enters the world. Guarded: an object whose PaletteBase is not
        /// <see cref="SkinPaletteBase"/> (a weenie without the plate swap) is left untouched, because the rows would tint
        /// nothing or the wrong texture; that logs one warning per process and is not a failure.
        /// </summary>
        internal static bool ApplySkinRows(ACE.Entity.Models.Biota biota, System.Threading.ReaderWriterLockSlim biotaLock, GolemSkin skin)
        {
            if (biota == null || skin == GolemSkin.CopperBronze)
                return false;

            if (biota.PropertiesDID == null || !biota.PropertiesDID.TryGetValue(ACE.Entity.Enum.Properties.PropertyDataId.PaletteBase, out var paletteBase) || paletteBase != SkinPaletteBase)
            {
                if (!skinGuardWarned)
                {
                    skinGuardWarned = true;
                    log.Warn($"[DYNDUNGEON] golem skin {skin} not applied: the object's PaletteBase is not 0x{SkinPaletteBase:X8} (a golem weenie without the plate swap); its palette rows are left as authored");
                }

                return false;
            }

            var id = SkinPaletteId(skin);

            biotaLock?.EnterWriteLock();
            try
            {
                biota.PropertiesPalette = new List<ACE.Entity.Models.PropertiesPalette>
                {
                    new ACE.Entity.Models.PropertiesPalette { SubPaletteId = id, Offset = 0, Length = 128 },
                    new ACE.Entity.Models.PropertiesPalette { SubPaletteId = id, Offset = 128, Length = 128 },
                };
            }
            finally
            {
                if (biotaLock != null && biotaLock.IsWriteLockHeld)
                    biotaLock.ExitWriteLock();
            }

            return true;
        }

        private static bool skinGuardWarned;

        /// <summary>Creates a golem-family object (CreateObject seam) and lays the configured skin over it before it can enter the world.</summary>
        internal static WorldObject CreateSkinned(uint wcid, GolemConfig config)
        {
            var wo = CreateObject(wcid);

            if (wo != null)
                ApplySkinRows(wo.Biota, wo.BiotaDatabaseLock, config?.Skin ?? GolemSkin.CopperBronze);

            return wo;
        }

        // ---- config ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Parses a golem row's config. Absent, null or {} yields the defaults. A config that is not an object, a bad wcid,
        /// a wisp list that is not eight positive wcids, or a key of the wrong type (rounds not a whole number, roundSeconds
        /// / ringRadius / heightTolerance not a finite number, skin not one of copperBronze, silver, gold) is an error (null with <paramref name="error"/>). A number out
        /// of its range is clamped and noted in <paramref name="problems"/>. Pure.
        /// </summary>
        public static GolemConfig ParseConfig(JsonElement config, List<string> problems, out string error)
        {
            error = null;
            problems ??= new List<string>();

            if (config.ValueKind == JsonValueKind.Undefined || config.ValueKind == JsonValueKind.Null)
                return new GolemConfig();

            if (config.ValueKind != JsonValueKind.Object)
            {
                error = $"config must be an object, not {config.ValueKind}";
                return null;
            }

            var d = new GolemConfig();

            if (!TryWcid(config, "dormantWcid", d.DormantWcid, out var dormant, out error) || !TryWcid(config, "golemWcid", d.GolemWcid, out var golem, out error))
                return null;

            IReadOnlyList<uint> wisps = d.WispWcids;

            if (config.TryGetProperty("wispWcids", out var w))
            {
                var parsed = new List<uint>();

                if (w.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in w.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Number || !e.TryGetUInt32(out var wcid) || wcid == 0)
                        {
                            parsed = null;
                            break;
                        }

                        parsed.Add(wcid);
                    }
                }

                if (parsed == null || parsed.Count != BankSize || w.ValueKind != JsonValueKind.Array)
                {
                    error = $"config.wispWcids must be {BankSize} positive whole numbers, in bank order";
                    return null;
                }

                wisps = parsed;
            }

            var skin = d.Skin;

            if (config.TryGetProperty("skin", out var sk))
            {
                var index = sk.ValueKind == JsonValueKind.String ? SkinNames.ToList().IndexOf(sk.GetString()) : -1;

                if (index < 0)
                {
                    error = $"config.skin must be one of {string.Join(", ", SkinNames)}";
                    return null;
                }

                skin = (GolemSkin)index;
            }

            var rounds = d.Rounds;

            if (config.TryGetProperty("rounds", out var r))
            {
                if (r.ValueKind != JsonValueKind.Number || !r.TryGetInt32(out rounds))
                {
                    error = "config.rounds must be a whole number";
                    return null;
                }

                var clamped = Math.Clamp(rounds, GolemConfig.MinRounds, GolemConfig.MaxRounds);

                if (clamped != rounds)
                    problems.Add($"config.rounds {rounds.ToString(CultureInfo.InvariantCulture)} clamped to {clamped.ToString(CultureInfo.InvariantCulture)}");

                rounds = clamped;
            }

            if (!TryClampedNumber(config, "roundSeconds", d.RoundSeconds, GolemConfig.MinRoundSeconds, GolemConfig.MaxRoundSeconds, problems, out var roundSeconds, out error)
                || !TryClampedNumber(config, "ringRadius", d.RingRadius, GolemConfig.MinRingRadius, GolemConfig.MaxRingRadius, problems, out var ringRadius, out error)
                || !TryClampedNumber(config, "heightTolerance", d.HeightTolerance, GolemConfig.MinHeightTolerance, GolemConfig.MaxHeightTolerance, problems, out var tolerance, out error))
                return null;

            return new GolemConfig
            {
                DormantWcid = dormant,
                GolemWcid = golem,
                WispWcids = wisps,
                Skin = skin,
                Rounds = rounds,
                RoundSeconds = roundSeconds,
                RingRadius = (float)ringRadius,
                HeightTolerance = (float)tolerance,
            };
        }

        private static bool TryWcid(JsonElement obj, string name, uint fallback, out uint value, out string error)
        {
            value = fallback;
            error = null;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out value) && value != 0)
                return true;

            error = $"config.{name} must be a positive whole number";
            return false;
        }

        private static bool TryClampedNumber(JsonElement obj, string name, double fallback, double min, double max, List<string> problems, out double value, out string error)
        {
            value = fallback;
            error = null;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out value) || !double.IsFinite(value))
            {
                error = $"config.{name} must be a number";
                return false;
            }

            var clamped = Math.Clamp(value, min, max);

            if (clamped != value)
                problems.Add($"config.{name} {value.ToString(CultureInfo.InvariantCulture)} clamped to {clamped.ToString(CultureInfo.InvariantCulture)}");

            value = clamped;
            return true;
        }

        // ---- pure planning ---------------------------------------------------------------------------------------

        /// <summary>
        /// The three buffs round <paramref name="round"/> (0-based) offers: three distinct buffs drawn uniformly from the
        /// bank of eight, deterministic for (<paramref name="seed"/>, <paramref name="round"/>). Rounds are independent, so
        /// a buff offered (and picked) before may be offered again. Pure.
        /// </summary>
        public static GolemBuff[] DrawRound(int seed, int round)
            => RoundBuffOrder(seed, round).Take(WispsPerRound).ToArray();

        /// <summary>The index (into the offered list) the timeout picks for round <paramref name="round"/>: uniform, deterministic for the seed. Pure.</summary>
        public static int TimeoutChoice(int seed, int round, int offeredCount)
            => offeredCount <= 1 ? 0 : new Random(unchecked(seed * 17 + 0x71E0 + round * 104729)).Next(offeredCount);

        /// <summary>The nine fixed slot bearings, slot index order: set s (0 to 2), then k (0 to 2), at s x 40 + k x 120 degrees. Pure.</summary>
        public static IReadOnlyList<float> SlotBearings()
        {
            var result = new List<float>();

            for (var s = 0; s < BearingSets; s++)
                for (var k = 0; k < WispsPerRound; k++)
                    result.Add(s * 40f + k * 120f);

            return result;
        }

        /// <summary>
        /// The slot order round <paramref name="round"/> tries: its own set's three slots (set round mod 3) first, then the
        /// other six validated slots in index order, the fallback when one of its own fails at spawn. Pure.
        /// </summary>
        public static IReadOnlyList<int> FallbackOrder(int round, int slotCount = BearingSets * WispsPerRound)
        {
            var set = ((round % BearingSets) + BearingSets) % BearingSets;
            var own = Enumerable.Range(set * WispsPerRound, WispsPerRound).Where(i => i < slotCount).ToList();
            return own.Concat(Enumerable.Range(0, slotCount).Where(i => !own.Contains(i))).ToList();
        }

        /// <summary>
        /// Every candidate wisp spot around the golem, in the order the flat-ground rule tries them: first the nine
        /// round-set slots on the configured ring (indices 0 to 8, <see cref="SlotBearings"/>), then the nine bearings
        /// between them on the same ring (20, 60, ... 340), then rings stepped <see cref="ExtraRadiusStep"/> in and out of
        /// it (nearest first, outward before inward), 18 bearings each (every 20 degrees). Extra rings stay within the
        /// config's ring bounds and never closer to the golem than <see cref="MinWispGolemDistance"/>. Pure.
        /// </summary>
        public static IReadOnlyList<(float Bearing, float Radius)> CandidateSpots(float radius)
        {
            var spots = new List<(float, float)>();

            foreach (var b in SlotBearings())
                spots.Add((b, radius));

            for (var k = 0; k < 9; k++)
                spots.Add((20f + k * 40f, radius));

            var lo = Math.Max(GolemConfig.MinRingRadius, MinWispGolemDistance);
            var hi = GolemConfig.MaxRingRadius;

            for (var step = 1; step <= 8; step++)
            {
                foreach (var r in new[] { radius + step * ExtraRadiusStep, radius - step * ExtraRadiusStep })
                {
                    if (r < lo - 1e-4f || r > hi + 1e-4f)
                        continue;

                    for (var k = 0; k < 18; k++)
                        spots.Add((k * 20f, r));
                }
            }

            return spots;
        }

        /// <summary>
        /// THE flat-ground rule (owner ruling 2026-11-11: a round never shows fewer than two wisps). Probes the candidate
        /// spots (<see cref="CandidateSpots"/>) around (<paramref name="x"/>, <paramref name="y"/>) and keeps, in candidate
        /// order, each spot with ground (<paramref name="groundAt"/> non-null: walkable, no building, reachable) within the
        /// height tolerance of the golem's ground <paramref name="golemZ"/> and at least <see cref="MinWispSpacing"/> from
        /// every spot already kept, up to <see cref="MaxPoolSlots"/>. While fewer than <see cref="WispsPerRound"/> are kept,
        /// the tolerance relaxes by <see cref="ToleranceRelaxStep"/> up to <see cref="MaxRelaxedTolerance"/> (never below
        /// the configured value). Returns the slots, each at ITS OWN probed ground height, with the tolerance that produced
        /// them; null with <paramref name="refusal"/> when fewer than <see cref="MinWispsPerRound"/> spots qualify even
        /// then. On a flat ring this is exactly the nine round-set slots at the configured tolerance. Pure over
        /// <paramref name="groundAt"/>, which is called at most once per candidate.
        /// </summary>
        public static List<GolemWispSlot> BuildRing(float x, float y, float golemZ, float radius, float tolerance,
            Func<float, float, GolemGroundHit?> groundAt, out string refusal)
            => BuildRing(x, y, golemZ, radius, tolerance, groundAt, out refusal, out _);

        public static List<GolemWispSlot> BuildRing(float x, float y, float golemZ, float radius, float tolerance,
            Func<float, float, GolemGroundHit?> groundAt, out string refusal, out float usedTolerance)
        {
            refusal = null;
            usedTolerance = tolerance;

            if (groundAt == null || !float.IsFinite(golemZ))
            {
                refusal = "no ground under the golem";
                return null;
            }

            var spots = CandidateSpots(radius);
            var probed = new (float X, float Y, GolemGroundHit? Hit)?[spots.Count];

            (float X, float Y, GolemGroundHit? Hit) Probe(int i)
            {
                if (probed[i] == null)
                {
                    var rad = spots[i].Bearing * Math.PI / 180.0;
                    var sx = x + (float)(spots[i].Radius * Math.Sin(rad));
                    var sy = y + (float)(spots[i].Radius * Math.Cos(rad));
                    var hit = groundAt(sx, sy);
                    probed[i] = (sx, sy, hit != null && float.IsFinite(hit.Value.Z) ? hit : null);
                }

                return probed[i].Value;
            }

            List<GolemWispSlot> Pool(float tol)
            {
                var pool = new List<GolemWispSlot>();

                for (var i = 0; i < spots.Count && pool.Count < MaxPoolSlots; i++)
                {
                    var (sx, sy, hit) = Probe(i);

                    if (hit == null || Math.Abs(hit.Value.Z - golemZ) > tol)
                        continue;

                    if (pool.Any(s => new Vector2(s.X - sx, s.Y - sy).Length() < MinWispSpacing))
                        continue;

                    pool.Add(new GolemWispSlot(i, spots[i].Bearing, sx, sy, hit.Value.Z, hit.Value.Cell));
                }

                return pool;
            }

            var slots = Pool(tolerance);

            // Each step keeps the best pool seen (code review 2026-11-11): a looser tolerance admits an earlier candidate,
            // and the greedy spacing can then reject two later ones, so a relaxed pool may be smaller than a tighter one.
            for (var tol = tolerance + ToleranceRelaxStep; slots.Count < WispsPerRound && tol <= MaxRelaxedTolerance + 1e-4f; tol += ToleranceRelaxStep)
            {
                var step = Math.Min(tol, MaxRelaxedTolerance);
                var relaxed = Pool(step);

                if (relaxed.Count > slots.Count)
                {
                    slots = relaxed;
                    usedTolerance = step;
                }
                else if (slots.Count < MinWispsPerRound)
                    usedTolerance = step;
            }

            if (slots.Count < MinWispsPerRound)
            {
                var dry = probed.Count(p => p != null && p.Value.Hit == null);
                refusal = string.Format(CultureInfo.InvariantCulture,
                    "only {0} wisp spot(s) within {1:0.00} m of the golem's ground (need {2}; {3} of {4} candidate spot(s) had no walkable ground)",
                    slots.Count, usedTolerance, MinWispsPerRound, dry, spots.Count);
                return null;
            }

            return slots;
        }

        /// <summary>
        /// The slot order round <paramref name="round"/> tries over a validated pool: the pool positions holding its own
        /// round set's slots (indices set x 3 to set x 3 + 2, set = round mod 3) first, then every other pool position in
        /// pool order. On a full nine-slot pool this is <see cref="FallbackOrder(int, int)"/>. Pure.
        /// </summary>
        public static IReadOnlyList<int> RoundSlotOrder(int round, IReadOnlyList<GolemWispSlot> slots)
        {
            var set = ((round % BearingSets) + BearingSets) % BearingSets;
            var count = slots?.Count ?? 0;
            bool Own(int pos) => slots[pos].Index >= set * WispsPerRound && slots[pos].Index < (set + 1) * WispsPerRound;
            var own = Enumerable.Range(0, count).Where(Own).ToList();
            return own.Concat(Enumerable.Range(0, count).Where(p => !Own(p))).ToList();
        }

        /// <summary>
        /// Round <paramref name="round"/>'s buffs in the order the spawn tries them: the whole bank of eight, shuffled
        /// deterministically for (<paramref name="seed"/>, <paramref name="round"/>). The first three are exactly
        /// <see cref="DrawRound"/>; the other five stand in for a drawn buff whose wisp cannot be built. Pure.
        /// </summary>
        public static GolemBuff[] RoundBuffOrder(int seed, int round)
        {
            var rng = new Random(unchecked(seed * 31 + 0x601E + round * 7919));
            var bank = Enumerable.Range(0, BankSize).ToArray();

            for (var i = 0; i < BankSize; i++)
            {
                var j = i + rng.Next(BankSize - i);
                (bank[i], bank[j]) = (bank[j], bank[i]);
            }

            return bank.Select(b => (GolemBuff)b).ToArray();
        }

        /// <summary>Does a round with <paramref name="placed"/> wisps standing show none (fewer than <see cref="MinWispsPerRound"/>)? Pure.</summary>
        internal static bool IsShortRound(int placed) => placed < MinWispsPerRound;

        /// <summary>
        /// The spawn plan for one round's wisps, pure over <paramref name="tryPlace"/> (pool position, buff -> result).
        /// Walks the pool in <paramref name="slotOrder"/>; at each slot it tries the pending buffs in
        /// <paramref name="buffOrder"/> order. A buff whose wisp cannot be built (<see cref="GolemWispTry.BuffFailed"/>)
        /// leaves the round without costing the slot; a wisp that entered off its spot (<see cref="GolemWispTry.SlotFailed"/>)
        /// costs that slot one of its <see cref="MaxBuffTriesPerSlot"/> tries, and a buff that failed on
        /// <see cref="MaxSlotFailuresPerBuff"/> slots leaves the round. So one bad wisp weenie can never eat the whole pool
        /// (the 2026-11-11 one-wisp round). Stops at <paramref name="want"/> placed. Returns (pool position, buff) in placement order.
        /// </summary>
        internal static List<(int SlotPos, GolemBuff Buff)> PlanWisps(IReadOnlyList<int> slotOrder, IReadOnlyList<GolemBuff> buffOrder, int want,
            Func<int, GolemBuff, GolemWispTry> tryPlace)
        {
            var placed = new List<(int, GolemBuff)>();
            var pending = (buffOrder ?? Array.Empty<GolemBuff>()).Distinct().ToList();
            var slotFailures = new Dictionary<GolemBuff, int>();

            foreach (var pos in slotOrder ?? Array.Empty<int>())
            {
                if (placed.Count >= want || pending.Count == 0)
                    break;

                var tries = 0;
                var i = 0;

                while (i < pending.Count && tries < MaxBuffTriesPerSlot)
                {
                    var buff = pending[i];
                    var result = tryPlace(pos, buff);

                    if (result == GolemWispTry.Placed)
                    {
                        placed.Add((pos, buff));
                        pending.RemoveAt(i);
                        break;
                    }

                    if (result == GolemWispTry.BuffFailed)
                    {
                        pending.RemoveAt(i);
                        continue;
                    }

                    tries++;
                    slotFailures[buff] = slotFailures.GetValueOrDefault(buff) + 1;

                    if (slotFailures[buff] >= MaxSlotFailuresPerBuff)
                        pending.RemoveAt(i);
                    else
                        i++;
                }
            }

            return placed;
        }

        /// <summary>
        /// The stacking math. Ratings add (per pick x pick scale, rounded); factors multiply (per pick 1 + (f - 1) x scale,
        /// raised to the pick count). <paramref name="counts"/> is per buff in bank order. Pure.
        /// </summary>
        public static GolemBuffEffect Compute(IReadOnlyList<int> counts, double pickScale)
        {
            var s = ThreadDungeonRun.ClampGolemPickScale(pickScale);
            int N(GolemBuff b) => counts != null && (int)b < counts.Count ? Math.Max(0, counts[(int)b]) : 0;
            int Rating(int per, GolemBuff b) => (int)Math.Round(per * s * N(b), MidpointRounding.AwayFromZero);
            double Factor(double per, GolemBuff b) => Math.Pow(1.0 + per * s, N(b));

            return new GolemBuffEffect(
                Rating(MightDamageRating, GolemBuff.Might),
                Rating(KeenEdgeCritRating, GolemBuff.KeenEdge),
                Rating(RuinCritDamageRating, GolemBuff.Ruin),
                Rating(BulwarkDamageResistRating, GolemBuff.Bulwark),
                Factor(FrenzyAttackSpeed, GolemBuff.Frenzy),
                Factor(VigorHealth, GolemBuff.Vigor),
                Factor(ArcanaSpellChance, GolemBuff.Arcana),
                Factor(ArcanaCastSpeed, GolemBuff.Arcana),
                Factor(MendingHealthRate, GolemBuff.Mending));
        }

        /// <summary>
        /// The awakened golem's max health: max(the shaped value, observed max non-boss x health mult), then Vigor's factor
        /// on top. Clamped to [1, int.MaxValue]. Pure.
        /// </summary>
        public static int HealthTarget(uint shaped, uint observedMaxNonBoss, double healthMult, double vigorMult)
        {
            var floor = observedMaxNonBoss * ThreadDungeonRun.ClampGolemHealthMult(healthMult);
            var baseHp = Math.Max(shaped, floor);
            var target = baseHp * (double.IsFinite(vigorMult) && vigorMult > 0 ? vigorMult : 1.0);
            return (int)Math.Clamp(Math.Round(target), 1.0, int.MaxValue);
        }

        /// <summary>
        /// One spell-book probability scaled by <paramref name="factor"/>, in the encoding Monster_Magic reads (a value
        /// above 2 is 2 + the chance; otherwise the value is a percent). The chance is capped at 1. Pure.
        /// </summary>
        public static float ScaleSpellProbability(float value, double factor)
        {
            if (!double.IsFinite(factor) || factor <= 0 || factor == 1.0)
                return value;

            var chance = value > 2.0f ? value - 2.0 : value / 100.0;
            var scaled = Math.Clamp(chance * factor, 0.0, 1.0);

            // A value already in the 2 + chance form stays in it; a percent-form value stays a percent while it still fits.
            return value > 2.0f || scaled * 100.0 > 2.0 ? (float)(2.0 + scaled) : (float)(scaled * 100.0);
        }

        /// <summary>The monster-effect overlay for the speed factors ("" when both are 1): PropertyString 9015 grammar. Pure.</summary>
        public static string SpeedOverlay(GolemBuffEffect effect)
        {
            var parts = new List<string>();

            if (Math.Abs(effect.AttackSpeedMult - 1.0) > 1e-9)
                parts.Add("speed axis=attack pct=" + (effect.AttackSpeedMult - 1.0).ToString("0.######", CultureInfo.InvariantCulture));

            if (Math.Abs(effect.CastSpeedMult - 1.0) > 1e-9)
                parts.Add("speed axis=cast pct=" + (effect.CastSpeedMult - 1.0).ToString("0.######", CultureInfo.InvariantCulture));

            return string.Join("; ", parts);
        }

        /// <summary>The distinct picked buffs in bank order: the aura cycles through their colours. Pure.</summary>
        public static IReadOnlyList<GolemBuff> AuraOrder(IReadOnlyList<int> counts)
            => Enumerable.Range(0, BankSize).Where(i => counts != null && i < counts.Count && counts[i] > 0).Select(i => (GolemBuff)i).ToList();

        /// <summary>Grants once per winner in <paramref name="winners"/>; returns how many grants were accepted. Pure over <paramref name="grant"/>.</summary>
        internal static int GrantToAll<T>(IEnumerable<T> winners, Func<T, bool> grant)
        {
            var granted = 0;

            foreach (var w in winners ?? Enumerable.Empty<T>())
                if (grant(w))
                    granted++;

            return granted;
        }

        /// <summary>Is the run (or its encounters) over, or the request released? Then every use and queued action is silent.</summary>
        internal static bool RunOver(ThreadDungeonRun run, GolemState state)
            => run == null || state?.Request == null || run.State == ThreadDungeonRunState.Ended || run.EncountersEnded;

        // ---- IThreadEncounterType -------------------------------------------------------------------------------

        /// <summary>A bad config refuses; outdoors the whole ring (plus a margin) must fit on the point's landblock, so every wisp is created on the golem's own queue. Pure.</summary>
        public string RejectPoint(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, bool isOutdoor)
        {
            var config = ParseConfig(def?.Config ?? default, null, out var error);

            if (config == null)
                return "golem: " + error;

            if (isOutdoor && !RingFitsLandblock(candidate.Point.X, candidate.Point.Y, config.RingRadius))
                return "golem: the wisp ring would leave the point's landblock";

            return null;
        }

        /// <summary>The outdoor bounds as a pure check: local x and y in [R + 0.5, 192 - R - 0.5].</summary>
        internal static bool RingFitsLandblock(float x, float y, float radius)
        {
            var lo = radius + LandblockMargin;
            var hi = LandblockEdge - radius - LandblockMargin;
            return x >= lo && x <= hi && y >= lo && y <= hi;
        }

        public bool Place(ThreadEncounterPlaceRequest request, out string failure)
        {
            failure = null;

            var run = request.Run;
            var problems = new List<string>();
            var config = ParseConfig(request.Def?.Config ?? default, problems, out var error);

            foreach (var problem in problems)
                run.RecordEncounterOutcome($"{request.Def?.Id}: {problem}");

            if (config == null)
            {
                failure = error;
                return false;
            }

            var isOutdoor = run.Dungeon?.IsOutdoor == true;
            var point = request.Point.Point;

            if (isOutdoor && !RingFitsLandblock(point.X, point.Y, config.RingRadius))
            {
                failure = "the wisp ring would leave the point's landblock";
                return false;
            }

            // The flat-ground rule, at placement: every wisp slot of every round is probed now, so a shaping never finds
            // a hill or a wall mid-encounter.
            List<GolemWispSlot> slots = null;
            var refusal = "no walkable ground under the golem";
            var usedTolerance = config.HeightTolerance;
            var ground = GroundFor(request, config);

            try
            {
                if (ground.Golem != null)
                    slots = BuildRing(point.X, point.Y, ground.Golem.Value.Z, config.RingRadius, config.HeightTolerance, ground.At, out refusal, out usedTolerance);
            }
            finally
            {
                ground.Release?.Invoke();
            }

            if (slots == null)
            {
                failure = "the wisp ring is not flat: " + refusal;
                return false;
            }

            var dormant = CreateSkinned(config.DormantWcid, config);

            if (dormant == null)
            {
                failure = $"wcid {config.DormantWcid} did not create - is its weenie in the world database?";
                return false;
            }

            dormant.Location = request.PositionOf(point);
            dormant.TimeToRot = -1;
            dormant.IsTransientSpawn = true;

            var state = new GolemState(request, config, slots) { DormantGuid = dormant.Guid.Full, Dormant = dormant };
            request.Instance.State = state;

            // Before EnterWorld: a use can arrive the moment it is visible.
            request.Instance.Track(dormant);

            bool entered;

            try
            {
                entered = dormant.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} golem EnterWorld threw at {dormant.Location?.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                dormant.P_ThreadEncounter = null;
                try { dormant.Destroy(); } catch { }
                request.Instance.State = null;
                failure = $"wcid {config.DormantWcid} failed to enter the world at {dormant.Location?.ToLOCString()}";
                return false;
            }

            run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "{0}: dormant golem {1}, {2} round(s) of {3:0} s, ring {4:0.0} m, {5} wisp slot(s) within {6:0.00} m of its ground",
                request.Def?.Id, config.DormantWcid, config.Rounds, config.RoundSeconds, config.RingRadius, slots.Count, usedTolerance));

            return true;
        }

        public void OnRunEnded(ThreadEncounterInstance instance, string why)
        {
            if (instance?.State is GolemState state)
                state.Request = null;
        }

        // ---- IThreadEncounterUsable ------------------------------------------------------------------------------

        public void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player)
        {
            if (!(instance?.State is GolemState state) || used == null || player == null)
                return;

            if (state.DormantGuid != 0 && used.Guid.Full == state.DormantGuid)
                UseDormant(instance, state, player);
            else
                UseWisp(instance, state, player, used.Guid.Full);
        }

        private static void UseDormant(ThreadEncounterInstance instance, GolemState state, Player player)
        {
            var run = instance.Run;
            var outcome = state.TryBegin(RunOver(run, state), ThreadRunPresence.IsMemberInside(run, player));

            switch (outcome)
            {
                case GolemUseOutcome.NotInside:
                    Send(player, RefusedLine);
                    return;
                case GolemUseOutcome.Shaping:
                    Send(player, ShapingLine);
                    return;
                case GolemUseOutcome.Spent:
                case GolemUseOutcome.RunOver:
                    return;
            }

            // The begin line waits for round 1's wisps (StartRound), so it can say how many stand.
            run.RecordEncounterOutcome($"{instance}: shaping begun by {player.Name}");
            log.Info($"[DYNDUNGEON] {run} {instance} golem shaping begun by {player.Name}");

            OnGolemQueue(state, 0, () => StartRound(instance, state, 0));
        }

        private static void UseWisp(ThreadEncounterInstance instance, GolemState state, Player player, uint wispGuid)
        {
            var run = instance.Run;

            if (RunOver(run, state))
                return;

            if (!ThreadRunPresence.IsMemberInside(run, player))
            {
                // The refusal only answers a live wisp; any other tracked object stays silent.
                if (state.TryGetWispBuff(wispGuid, out _))
                    Send(player, RefusedLine);
                return;
            }

            if (state.TryPickWisp(wispGuid, out var buff, out var pickedRound, out var wasLast))
                Announce(instance, state, buff, pickedRound, wasLast, player.Name);
        }

        /// <summary>
        /// The Player-free core of a timeout pick (a null <paramref name="pickerName"/>) or a pick by buff: the locked
        /// latch, then the line and the next step queued on the golem's landblock. True for the one pick that spent the round.
        /// </summary>
        internal static bool Resolve(ThreadEncounterInstance instance, GolemState state, int generation, GolemBuff buff, string pickerName, string line = null)
        {
            if (!state.TryPick(generation, buff, out var pickedRound, out var wasLast))
                return false;

            Announce(instance, state, buff, pickedRound, wasLast, pickerName, line);
            return true;
        }

        /// <summary>After a won latch: the pick line, the outcome record, and the next step (the next round, or the awakening) queued on the golem's landblock.</summary>
        private static void Announce(ThreadEncounterInstance instance, GolemState state, GolemBuff buff, int pickedRound, bool wasLast, string pickerName, string line = null)
        {
            var run = instance.Run;
            var total = state.Config.Rounds;
            SendAll(run, AnnounceText(line, pickerName, buff, pickedRound + 1, total));
            run.RecordEncounterOutcome($"{instance}: round {pickedRound + 1} {buff} ({pickerName ?? "timeout"})");

            OnGolemQueue(state, 0, () =>
            {
                ClearWisps(state);

                if (wasLast)
                    Awaken(instance, state);
                else
                    OnGolemQueue(state, NextRoundDelaySeconds, () => StartRound(instance, state, pickedRound + 1));
            });
        }

        // ---- the shaping, on the golem's landblock queue ---------------------------------------------------------

        /// <summary>Queues <paramref name="action"/> onto the golem's landblock (the request's), after <paramref name="delay"/> seconds, behind the run guard.</summary>
        private static void OnGolemQueue(GolemState state, double delay, Action action)
        {
            var landblock = state.Request?.Landblock;

            if (landblock == null)
                return;

            var chain = new ActionChain();

            if (delay > 0)
                chain.AddDelaySeconds(delay);

            chain.AddAction(landblock, () =>
            {
                var request = state.Request;

                if (request == null || RunOver(request.Run, state))
                    return;

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {request.Run} golem action threw", ex);
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>Golem landblock queue: draws the round's three buffs, places their wisps (falling back to the other validated slots), opens the round and starts its timer.</summary>
        private static void StartRound(ThreadEncounterInstance instance, GolemState state, int round)
        {
            var request = state.Request;
            var run = instance.Run;

            if (request == null || state.Phase != GolemPhase.Shaping || state.Round != round || state.RoundOpen)
                return;

            var buffOrder = RoundBuffOrder(request.Seed, round);
            var drawn = buffOrder.Take(WispsPerRound).ToArray();
            var placedBuffs = new List<GolemBuff>();
            var guids = new List<uint>();
            var order = RoundSlotOrder(round, state.Slots);
            var ownPositions = order.Take(WispsPerRound).ToHashSet();
            var fallbacks = 0;

            // A throw while placing must not strand the encounter in Shaping: the round still opens with whatever
            // placed (or, short of two, with none and the golem's own draw).
            try
            {
                PlanWisps(order, buffOrder, WispsPerRound, (pos, buff) =>
                {
                    var result = TryPlaceWisp(instance, state, request, state.Slots[pos], buff, out var wisp);

                    if (result == GolemWispTry.Placed)
                    {
                        if (!ownPositions.Contains(pos))
                            fallbacks++;

                        placedBuffs.Add(buff);
                        guids.Add(wisp.Guid.Full);
                    }

                    return result;
                });
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} {instance} golem round {round + 1} wisp placement threw; {placedBuffs.Count} wisp(s) placed", ex);
            }

            // Owner ruling 2026-11-11: a round never shows fewer than two wisps. Short of two, the placed one (if any) is
            // taken down and the round opens with the drawn three and no wisps, and the golem draws one of them at once
            // (the timeout's seeded pick, with NoWispLine), so the shaping goes on and is never stuck.
            var shortRound = IsShortRound(placedBuffs.Count);

            if (shortRound)
            {
                ClearWisps(state);
                guids.Clear();
            }

            var offered = shortRound ? drawn.ToList() : placedBuffs;

            if (placedBuffs.Count < WispsPerRound)
            {
                log.Warn($"[DYNDUNGEON] {run} {instance} golem round {round + 1}: {placedBuffs.Count} of {WispsPerRound} wisp(s) placed{(shortRound ? "; fewer than " + MinWispsPerRound + ", so none are shown and the golem draws one itself" : "")}");
                run.RecordEncounterOutcome($"{instance}: round {round + 1} placed {placedBuffs.Count} of {WispsPerRound} wisp(s){(shortRound ? " (none shown, the golem draws)" : "")}");
            }
            else if (fallbacks > 0)
                run.RecordEncounterOutcome($"{instance}: round {round + 1} used {fallbacks} fallback wisp slot(s)");

            var generation = state.OpenRound(round, offered, guids);

            if (generation == 0)
            {
                ClearWisps(state);
                return;
            }

            if (shortRound)
            {
                var auto = offered[TimeoutChoice(request.Seed, round, offered.Count)];
                Resolve(instance, state, generation, auto, null, NoWispLine(auto));
                return;
            }

            // The timer first, so nothing after the round opened can leave it without a timeout.
            var seconds = state.Config.RoundSeconds;

            OnGolemQueue(state, seconds, () =>
            {
                var choices = state.Offered;

                if (state.Generation != generation || !state.RoundOpen || choices.Count == 0)
                    return;

                Resolve(instance, state, generation, choices[TimeoutChoice(request.Seed, round, choices.Count)], null);
            });

            SendAll(run, round == 0 ? BeginLine(guids.Count) : NextRoundLine(guids.Count));
        }

        /// <summary>
        /// Golem landblock queue: one wisp on one validated slot, at that slot's probed ground height. BuffFailed when the
        /// wisp could not be built; SlotFailed (nothing left behind) when it did not enter or landed off its spot.
        /// </summary>
        private static GolemWispTry TryPlaceWisp(ThreadEncounterInstance instance, GolemState state, ThreadEncounterPlaceRequest request, GolemWispSlot slot, GolemBuff buff, out WorldObject placed)
        {
            placed = null;
            var wcid = state.Config.WispWcids[(int)buff];
            var wo = CreateObject(wcid);

            if (wo == null)
            {
                log.Warn($"[DYNDUNGEON] {request.Run} {instance} golem wisp {wcid} ({buff}) did not create; the round tries another buff");
                return GolemWispTry.BuffFailed;
            }

            var p = request.Point.Point;
            var intended = new Position(slot.Cell != 0 ? slot.Cell : p.Cell, slot.X, slot.Y, slot.Z, 0f, 0f, 0f, 1f, request.Run.Instance);

            if (request.Run.Dungeon?.IsOutdoor == true)
                intended.SetLandCell();

            // The threading rule: only ever onto the landblock this queue belongs to.
            if (request.Landblock != null && intended.LandblockId.Landblock != request.Landblock.Id.Landblock)
            {
                try { wo.Destroy(); } catch { }
                return GolemWispTry.SlotFailed;
            }

            wo.Location = new Position(intended);
            wo.TimeToRot = -1;
            wo.IsTransientSpawn = true;
            instance.Track(wo);

            bool entered;

            try
            {
                entered = wo.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {request.Run} golem wisp {wo.WeenieClassId} EnterWorld threw at {intended.ToLOCString()}", ex);
                entered = false;
            }

            var at = wo.Location;
            var landed = entered && at != null && at.LandblockId.Landblock == intended.LandblockId.Landblock && Vector3.Distance(at.Pos, intended.Pos) <= PlacementTolerance;

            if (!landed)
            {
                log.Warn($"[DYNDUNGEON] {request.Run} {instance} golem wisp slot {slot.Index} {(entered ? $"landed at {at?.ToLOCString()}, not {intended.ToLOCString()}" : "did not enter")}; trying the next slot");
                wo.P_ThreadEncounter = null;
                try { if (!wo.IsDestroyed) wo.Destroy(); } catch { }
                return GolemWispTry.SlotFailed;
            }

            state.Wisps.Add(wo);
            placed = wo;
            return GolemWispTry.Placed;
        }

        /// <summary>Golem landblock queue: destroys the current round's wisps. Idempotent.</summary>
        private static void ClearWisps(GolemState state)
        {
            foreach (var wisp in state.Wisps)
            {
                wisp.P_ThreadEncounter = null;
                try { if (!wisp.IsDestroyed) wisp.Destroy(); } catch { }
            }

            state.Wisps.Clear();
        }

        /// <summary>
        /// Golem landblock queue: the swap. The awakened monster is created and EVERY buff written before EnterWorld, the
        /// dormant golem is destroyed, the monster enters at the same spot under a Create burst, and the aura starts.
        /// </summary>
        internal static void Awaken(ThreadEncounterInstance instance, GolemState state)
        {
            Creature created = null;

            try
            {
                AwakenCore(instance, state, golem => created = golem);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance?.Run} {instance} golem awakening threw", ex);

                // Never stranded in Shaping: a throw before the swap finished spends the encounter.
                if (state.Phase == GolemPhase.Shaping)
                {
                    if (created != null)
                    {
                        created.P_ThreadEncounter = null;
                        try { if (!created.IsDestroyed) created.Destroy(); } catch { }
                    }

                    Fail(instance, state, "the awakening threw: " + ex.Message);
                }
            }
        }

        private static void AwakenCore(ThreadEncounterInstance instance, GolemState state, Action<Creature> onCreated)
        {
            var request = state.Request;
            var run = instance.Run;
            var wo = CreateSkinned(state.Config.GolemWcid, state.Config);

            if (!(wo is Creature golem))
            {
                try { wo?.Destroy(); } catch { }
                Fail(instance, state, $"wcid {state.Config.GolemWcid} is not a creature");
                return;
            }

            onCreated(golem);

            var point = request.Point.Point;
            golem.Location = request.PositionOf(point);

            var entry = new DungeonSpawnPlanEntry(state.Config.GolemWcid, DungeonRole.Elite, point, run.Spec?.Level ?? 0);
            request.ShapeCreature(golem, entry, new CreatureShapeOptions
            {
                Run = run,
                Rewards = true,
                GroupEffort = run.Group.Effort,
                ExcludedFromClear = true,
            });

            var effect = Compute(state.Counts, run.GolemPickScale);
            ApplyBuffs(golem, effect, request.Context?.ObservedMaxNonBoss ?? 0u, run.GolemHealthMult);

            request.BindToRunPerimeter(golem, point);
            request.Instance.Track(golem);

            var dormant = state.Dormant;

            if (dormant != null)
            {
                dormant.P_ThreadEncounter = null;
                try { if (!dormant.IsDestroyed) dormant.Destroy(); } catch { }
            }

            bool entered;

            try
            {
                entered = golem.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} awakened golem EnterWorld threw at {golem.Location?.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                golem.P_ThreadEncounter = null;
                try { golem.Destroy(); } catch { }
                Fail(instance, state, "the awakened golem failed to enter the world");
                return;
            }

            state.Awakened = golem;
            state.MarkAwakened(golem.Guid.Full);

            golem.PlayParticleEffect(PlayScript.Create, golem.Guid);
            SendAll(run, AwakenLine);

            run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture,
                "{0}: awakened ({1}); dr +{2}, crit +{3}, crit dmg +{4}, dmg resist +{5}, attack x{6:0.##}, health {7} (x{8:0.##}), spell chance x{9:0.##}, cast x{10:0.##}, regen x{11:0.##}",
                instance, string.Join(" ", state.Picked), effect.DamageRating, effect.CritRating, effect.CritDamageRating, effect.DamageResistRating,
                effect.AttackSpeedMult, golem.Health.MaxValue, effect.HealthMult, effect.SpellChanceMult, effect.CastSpeedMult, effect.HealthRateMult));
            log.Info($"[DYNDUNGEON] {run} {instance} golem awakened with {string.Join(" ", state.Picked)}");

            ScheduleAura(instance, state, golem, 0);
        }

        private static void Fail(ThreadEncounterInstance instance, GolemState state, string why)
        {
            state.MarkFailed();
            instance.Run.RecordEncounterOutcome($"{instance}: awakening failed ({why}); spent");
            log.Warn($"[DYNDUNGEON] {instance.Run} {instance} golem awakening failed: {why}");
        }

        /// <summary>Writes every buff onto the not-yet-entered golem: the ratings, the health rule, the regen, the spell book, the speed overlay.</summary>
        internal static void ApplyBuffs(Creature golem, GolemBuffEffect effect, uint observedMaxNonBoss, double healthMult)
        {
            void AddRating(PropertyInt key, int bonus)
            {
                if (bonus != 0)
                    golem.SetProperty(key, (golem.GetProperty(key) ?? 0) + bonus);
            }

            AddRating(PropertyInt.DamageRating, effect.DamageRating);
            AddRating(PropertyInt.CritRating, effect.CritRating);
            AddRating(PropertyInt.CritDamageRating, effect.CritDamageRating);
            AddRating(PropertyInt.DamageResistRating, effect.DamageResistRating);

            var target = HealthTarget(golem.Health.MaxValue, observedMaxNonBoss, healthMult, effect.HealthMult);

            if (target != golem.Health.MaxValue)
                WorldEventSpawner.ApplyHealth(golem, target);

            if (Math.Abs(effect.HealthRateMult - 1.0) > 1e-9)
            {
                var rate = golem.GetProperty(PropertyFloat.HealthRate) ?? 0.0;

                // BOTH: CreatureVital copies HealthRate into Health.RegenRate once at construction, and regen reads only
                // the vital's RegenRate (the same pair DungeonCreatureNormalizer writes).
                if (rate > 0)
                {
                    var scaled = rate * effect.HealthRateMult;
                    golem.SetProperty(PropertyFloat.HealthRate, scaled);
                    golem.Health.RegenRate = scaled;
                }
            }

            // Safe to mutate in place before EnterWorld: PropertiesSpellBook is a fresh copy out of the weenie.
            var book = golem.Biota.PropertiesSpellBook;

            if (book != null && Math.Abs(effect.SpellChanceMult - 1.0) > 1e-9)
                foreach (var spell in book.Keys.ToList())
                    book[spell] = ScaleSpellProbability(book[spell], effect.SpellChanceMult);

            var overlay = SpeedOverlay(effect);

            // Frenzy and Arcana's cast speed ride the monster-effect system: inert while monster_effects_enabled is
            // false, and subject to monster_effect_max_per_monster.
            if (overlay.Length > 0 && !MonsterEffectCaps.Enabled)
                log.Warn($"[DYNDUNGEON] golem speed overlay '{overlay}' requested but monster_effects_enabled is false; the speed buffs are inert");
            else if (overlay.Length > 0 && !golem.ApplyMonsterEffectOverlay(overlay))
                log.Warn($"[DYNDUNGEON] golem speed overlay '{overlay}' did not attach");
        }

        /// <summary>The awakened golem's own landblock: every aura_seconds, the next picked colour. Stops at its death, its destroy, or the run's end.</summary>
        private static void ScheduleAura(ThreadEncounterInstance instance, GolemState state, Creature golem, int tick)
        {
            var colours = AuraOrder(state.Counts);

            if (colours.Count == 0)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(ThreadDungeonRun.ClampGolemAuraSeconds(instance.Run.GolemAuraSeconds));
            chain.AddAction(golem, () =>
            {
                if (RunOver(instance.Run, state) || golem.IsDead || golem.IsDestroyed)
                    return;

                golem.PlayParticleEffect(AuraScripts[(int)colours[tick % colours.Count]], golem.Guid);
                ScheduleAura(instance, state, golem, tick + 1);
            });
            chain.EnqueueChain();
        }

        // ---- IThreadEncounterDeathAware -------------------------------------------------------------------------

        /// <summary>
        /// THE reward hook, from the run's kill hook (ThreadEncounterPass.OnTrackedCreatureDied) on the dying golem's
        /// thread: once, for the awakened golem, the death line and one shared encounter prize to every online member
        /// inside the run at that moment (the Defense rule). The count goes to /dd encounter list.
        /// </summary>
        public void OnTrackedCreatureDied(ThreadEncounterInstance instance, Creature creature)
        {
            if (!(instance?.State is GolemState state) || creature == null)
                return;

            var run = instance.Run;

            if (!state.TryMarkDead(creature.Guid.Full))
                return;

            SendAll(run, DeathLine);

            var granted = GrantToAll(MembersInside(run), winner => GrantPrize(run, winner, KindName));

            run.RecordEncounterOutcome($"{instance}: the golem fell; {granted} prize(s) granted");
            log.Info($"[DYNDUNGEON] {run} {instance} golem fell; {granted} prize(s) granted");
        }

        // ---- messages and ground --------------------------------------------------------------------------------

        private static void Send(Player player, string line)
        {
            if (!string.IsNullOrEmpty(line))
                player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        private static void SendAll(ThreadDungeonRun run, string line)
        {
            foreach (var recipient in ThreadRunPresence.MessageRecipients(run))
                Send(recipient, line);
        }

        /// <summary>
        /// The production ground on the point's landblock (Place runs on its queue). Outdoors: the overflow scatter's
        /// checks (ThreadDungeonSpawner.ProbeOutdoorGround: no building in the cell, walkable) at the golem's spot and each
        /// slot, the slot's terrain height returned for the tolerance check. A slot off the point's landblock is no ground.
        /// In a dungeon: a player-sized physics body (PuzzleWalkProbe) settled onto the golem's floor walks straight to the
        /// slot (a wall stops it short, which is no ground) and settles there; the slot's floor must be walkable. The body
        /// passes through creatures (<see cref="DungeonGround"/>).
        /// </summary>
        private static (GolemGroundHit? Golem, Func<float, float, GolemGroundHit?> At, Action Release) ProductionGround(ThreadEncounterPlaceRequest request, GolemConfig config)
        {
            var p = request.Point.Point;
            var landblock = (ushort)(p.Cell >> 16);
            var instanceId = request.Run.Instance;

            if (request.Run.Dungeon?.IsOutdoor == true)
            {
                GolemGroundHit? Outdoor(float x, float y, float sourceZ, float maxDrop)
                {
                    var pos = new Position(p.Cell, x, y, sourceZ, 0f, 0f, 0f, 1f, instanceId);

                    if (pos.Indoors)
                        return null;

                    pos.SetLandCell();

                    if (pos.LandblockId.Landblock != landblock)
                        return null;

                    return ThreadDungeonSpawner.ProbeOutdoorGround(pos, sourceZ, maxDrop, out var terrainZ)
                        ? new GolemGroundHit(terrainZ, pos.Cell)
                        : (GolemGroundHit?)null;
                }

                var golem = Outdoor(p.X, p.Y, p.Z, ThreadEncounterPass.ProbeMaxDropZ(config.RingRadius));
                var golemZ = golem?.Z ?? p.Z;
                return (golem, (x, y) => Outdoor(x, y, golemZ, ThreadDungeonSpawner.ScatterMaxDropZ), null);
            }

            return DungeonGround(instanceId, p.Cell, new Vector3(p.X, p.Y, p.Z));
        }

        /// <summary>
        /// What the dungeon ground probe walks through: any creature (a player or a monster). The run's trash stands on
        /// most curated points (DungeonPopulationBuilder deals its slots over the curated points round-robin) and the
        /// encounter pass runs after that population has entered the world, so the golem's own point usually has a monster
        /// on it at Place. Without this the body settled onto that monster instead of the floor, the settle found no contact, and
        /// every dungeon golem was refused as "no walkable ground under the golem" (prod 2026-11-21: 21 of 21 indoor
        /// golems). A creature moves; it is no part of the floor's flatness. Static scenery still blocks. Pure.
        /// </summary>
        internal static bool GroundIgnores(WorldObject wo) => wo is Creature;

        /// <summary>The <see cref="GroundIgnores"/> predicate over a physics object (its WorldObject, when it has one).</summary>
        internal static bool GroundIgnoresPhysics(ACE.Server.Physics.PhysicsObj obj) => GroundIgnores(obj?.WeenieObj?.WorldObject);

        /// <summary>
        /// The dungeon half of the production ground, on its own so a dat test can run it: a player-sized body settled
        /// onto the floor at (<paramref name="cell"/>, <paramref name="point"/>) in <paramref name="instanceId"/>, then for
        /// each slot a straight walk there and a settle; every Transition passes through creatures (<see cref="GroundIgnores"/>).
        /// The returned Release disposes the probe. Runs on the point's landblock thread (or offline in a test).
        /// </summary>
        internal static (GolemGroundHit? Golem, Func<float, float, GolemGroundHit?> At, Action Release) DungeonGround(uint instanceId, uint cell, Vector3 point)
        {
            var landblock = (ushort)(cell >> 16);
            var probe = new PuzzleWalkProbe(instanceId);
            Func<ACE.Server.Physics.PhysicsObj, bool> ignore = GroundIgnoresPhysics;

            if (!probe.Ready)
            {
                probe.Dispose();
                return (null, null, null);
            }

            var settled = probe.Settle(cell, point, ignore);

            if (settled == null || !settled.Value.OnWalkable)
            {
                probe.Dispose();
                return (null, null, null);
            }

            var start = settled.Value;

            GolemGroundHit? Dungeon(float x, float y)
            {
                var offset = new Vector3(x - start.Pos.X, y - start.Pos.Y, 0f);
                var walked = probe.Step(start, offset, ignore, out _);

                if (walked == null)
                    return null;

                var end = walked.Value;

                if (new Vector2(end.Pos.X - x, end.Pos.Y - y).Length() > DungeonReachTolerance || (ushort)(end.Cell >> 16) != landblock)
                    return null;

                var floor = probe.Settle(end.Cell, end.Pos, ignore);

                if (floor == null || !floor.Value.OnWalkable || (ushort)(floor.Value.Cell >> 16) != landblock)
                    return null;

                return new GolemGroundHit(floor.Value.Pos.Z, floor.Value.Cell);
            }

            return (new GolemGroundHit(start.Pos.Z, start.Cell), Dungeon, probe.Dispose);
        }
    }
}
