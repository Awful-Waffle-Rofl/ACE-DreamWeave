using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>One row of the Allegiance Gauntlet board, already ordered and ranked.</summary>
public readonly record struct GauntletBoardRow(int Rank, uint AvatarId, string Name, long BestCenti, int Participants, DateTime? BestAt);

/// <summary>
/// Allegiance Gauntlet: the in-memory record cache, the per-Avatar board behind /top gauntlet, and the record
/// announcement rules (design spec, "Record, leaderboard, announcement").
///
/// The cache is loaded once from allegiance_gauntlet_record at startup and updated on every Bank; each change enqueues
/// one absolute-value UpsertGauntletRecord. The board is one row per Avatar, ordered by best score descending, then by
/// the earlier best_At. Every run counts, including one in which an exempt (admin/trial/leaderboard-exempt) account had contact: the /top exemption does not apply to this board.
///
/// If the startup read FAILED (the DAO returns null, which is not the same as an empty table) the cache is marked
/// unavailable and Bank refuses to write: an empty cache would otherwise let the next run overwrite a stored best.
/// </summary>
public static class GauntletRecords
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The longest the board prints, the same cut every other /top board uses.</summary>
    public const int BoardSize = 20;

    private static readonly object gate = new();
    private static Dictionary<uint, AllegianceGauntletRecord> cache = new();
    private static readonly HashSet<uint> announcedRuns = new();
    private static bool available = true;

    // ---- collaborators (test seams) ----------------------------------------------------------------------------

    /// <summary>The startup read. Null means the read failed.</summary>
    internal static Func<List<AllegianceGauntletRecord>> Loader = () => DatabaseManager.Shard.BaseDatabase.GetAllGauntletRecords();

    /// <summary>Enqueues one absolute record write. Receives a private copy.</summary>
    internal static Action<AllegianceGauntletRecord> Persist = row => DatabaseManager.Shard.UpsertGauntletRecord(row, ok =>
    {
        if (!ok)
            log.Error($"[GAUNTLET] record write for Avatar {row.AvatarId} failed");
    });

    /// <summary>Resolves an Avatar's allegiance name: (allegianceName, monarchName), either may be null.</summary>
    internal static Func<uint, (string AllegianceName, string MonarchName)> NameSource = LiveNames;

    /// <summary>Sends a world-wide line. Replaced by tests.</summary>
    internal static Action<string> Broadcast = text =>
        PlayerManager.BroadcastToAll(new GameMessageSystemChat(text, ChatMessageType.WorldBroadcast));

    /// <summary>Sends a line to one allegiance (by monarch guid). Replaced by tests.</summary>
    internal static Action<uint, string> ToAllegiance = (monarchGuid, text) => AvatarAnnouncer.ToAllegiance(monarchGuid, text);

    // ---- lifecycle ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the cache from the database. Call once at startup, before the world ticks. A failed read leaves the
    /// cache empty and unavailable (see the class comment); it never throws.
    /// </summary>
    public static void Initialize()
    {
        List<AllegianceGauntletRecord> rows = null;

        try
        {
            rows = Loader();
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] record load threw: {ex.Message}");
        }

        lock (gate)
        {
            cache = new Dictionary<uint, AllegianceGauntletRecord>();
            announcedRuns.Clear();
            available = rows != null;

            if (rows == null)
            {
                log.Error("[GAUNTLET] records unavailable: Bank is disabled for this session so a stored best cannot be overwritten.");
                return;
            }

            foreach (var row in rows)
                cache[row.AvatarId] = Copy(row);
        }
    }

    /// <summary>Test seam: drops every row and re-arms the cache as available.</summary>
    internal static void ResetForTests()
    {
        lock (gate)
        {
            cache = new Dictionary<uint, AllegianceGauntletRecord>();
            announcedRuns.Clear();
            available = true;
        }
    }

    // ---- API for the run manager ----------------------------------------------------------------------------------

    /// <summary>
    /// The server #1 score, in hundredths of a wave, across every Avatar on the board (0 when the board is empty). The run
    /// manager captures it once when a run goes Active(1) and passes it to <see cref="TryAnnounce"/> at Ending: the
    /// Proving Grounds snapshot rule, so a record set by another allegiance mid-run does not hide this one.
    /// </summary>
    public static long SnapshotTop()
    {
        lock (gate)
            return cache.Values.Select(r => (long)r.BestCenti).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// Banks a run's score for an Avatar: best = max(best, score). Called at every clear and again at Ending with the
    /// partial. Updates the cache and enqueues one absolute upsert, but only when the best actually improved (a tie
    /// keeps the earlier best_At and its run). A zero score, an unknown Avatar id or an unavailable
    /// cache changes nothing. The Avatar's cooldown and run count are carried over from the cached row.
    /// </summary>
    /// <returns>true when the stored best changed.</returns>
    public static bool Bank(uint avatarId, long scoreCenti, int participants, uint? runId, DateTime nowUtc)
    {
        if (avatarId == 0 || scoreCenti <= 0)
            return false;

        AllegianceGauntletRecord toWrite;

        lock (gate)
        {
            if (!available)
                return false;

            cache.TryGetValue(avatarId, out var row);

            if (row != null && scoreCenti <= row.BestCenti)
                return false;

            row ??= new AllegianceGauntletRecord { AvatarId = avatarId };
            row.BestCenti = (uint)Math.Min(scoreCenti, uint.MaxValue);
            row.BestParticipants = (ushort)Math.Max(0, Math.Min(participants, ushort.MaxValue));
            row.BestRunId = runId;
            row.BestAt = nowUtc;
            cache[avatarId] = row;
            toWrite = Copy(row);
        }

        Persist(toWrite);
        return true;
    }

    /// <summary>
    /// Records that an Avatar's run was committed (the cooldown and the run count), through the same cache so a later
    /// <see cref="Bank"/> carries them instead of overwriting them with stale values. Enqueues one absolute upsert.
    /// </summary>
    public static void NoteRunCommitted(uint avatarId, DateTime nowUtc)
    {
        if (avatarId == 0)
            return;

        AllegianceGauntletRecord toWrite;

        lock (gate)
        {
            if (!available)
                return;

            if (!cache.TryGetValue(avatarId, out var row))
                cache[avatarId] = row = new AllegianceGauntletRecord { AvatarId = avatarId };

            row.LastRunAt = nowUtc;
            row.RunCount++;
            toWrite = Copy(row);
        }

        Persist(toWrite);
    }

    /// <summary>
    /// The Avatar's cooldown start (last_Run_At: the commit time of its last run), from the cache, or null. Read by the run
    /// manager's start gate; written only through <see cref="NoteRunCommitted"/>.
    /// </summary>
    public static DateTime? LastRunAt(uint avatarId)
    {
        lock (gate)
            return cache.TryGetValue(avatarId, out var row) ? row.LastRunAt : null;
    }

    /// <summary>The Avatar's banked best score in hundredths of a wave, from the cache (0 when it has none). Read by /avatar gauntlet.</summary>
    public static long BestCentiOf(uint avatarId)
    {
        lock (gate)
            return cache.TryGetValue(avatarId, out var row) ? row.BestCenti : 0;
    }

    /// <summary>
    /// Evaluated once at Ending. Sends the allegiance its result line ("Your Allegiance Gauntlet ended at wave 13.47.
    /// 24 fought.") and, when <paramref name="scoreCenti"/> is STRICTLY above
    /// <paramref name="snapshotTop"/> (the <see cref="SnapshotTop"/> value captured at Active(1)), broadcasts
    /// "[Allegiance Gauntlet] NAME has fought through wave 13.47 - a new record!" to the whole server. At most once per
    /// <paramref name="runId"/>: a second call for the same run sends nothing and returns false.
    /// </summary>
    /// <returns>true when the world-wide record line was broadcast.</returns>
    public static bool TryAnnounce(uint runId, uint avatarId, uint? monarchGuid, long scoreCenti, long snapshotTop, int fought)
    {
        lock (gate)
        {
            if (!announcedRuns.Add(runId))
                return false;
        }

        if (monarchGuid.HasValue)
            ToAllegiance(monarchGuid.Value, ResultLine(scoreCenti, fought));

        if (!GauntletScore.IsNewRecord(scoreCenti, snapshotTop))
            return false;

        Broadcast(RecordLine(AllegianceLabel(avatarId), scoreCenti));
        return true;
    }

    // ---- board ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The ordered board: best score descending, then the earlier best_At (a missing best_At sorts last), then avatar id
    /// so the order is total. Avatars with no banked score are not listed.
    /// </summary>
    public static IReadOnlyList<GauntletBoardRow> Board(int count = BoardSize)
    {
        List<AllegianceGauntletRecord> rows;

        lock (gate)
            rows = cache.Values.Where(r => r.BestCenti > 0).Select(Copy).ToList();

        return Order(rows).Take(count)
            .Select((r, i) => new GauntletBoardRow(i + 1, r.AvatarId, AllegianceLabel(r.AvatarId), r.BestCenti, r.BestParticipants, r.BestAt))
            .ToList();
    }

    /// <summary>The ordering rule on its own: score descending, earlier best_At first, then avatar id.</summary>
    internal static IEnumerable<AllegianceGauntletRecord> Order(IEnumerable<AllegianceGauntletRecord> rows) =>
        rows.OrderByDescending(r => r.BestCenti)
            .ThenBy(r => r.BestAt ?? DateTime.MaxValue)
            .ThenBy(r => r.AvatarId);

    /// <summary>The text /top gauntlet sends.</summary>
    public static string RenderBoard(IReadOnlyList<GauntletBoardRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append($"=== Allegiance Gauntlet Leaderboard (Top {BoardSize}) ===\n");

        if (rows.Count == 0)
        {
            sb.Append("No Allegiance Gauntlet runs have been recorded yet.\n");
            return sb.ToString();
        }

        foreach (var row in rows)
            sb.Append($"{row.Rank}. {RowText(row.Name, row.BestCenti, row.Participants)}\n");

        return sb.ToString();
    }

    // ---- text -------------------------------------------------------------------------------------------------

    /// <summary>"Name - Wave 13.47 (24 fought)".</summary>
    public static string RowText(string name, long centi, int participants) =>
        $"{name} - {Player.FormatWaveScore(centi)} ({participants} fought)";

    /// <summary>"[Allegiance Gauntlet] Name has fought through wave 13.47 - a new record!"</summary>
    public static string RecordLine(string name, long centi) =>
        $"[Allegiance Gauntlet] {name} has fought through wave {centi / 100}.{centi % 100:D2} - a new record!";

    /// <summary>"Your Allegiance Gauntlet ended at wave 13.47. 24 fought."</summary>
    public static string ResultLine(long centi, int fought) =>
        $"Your Allegiance Gauntlet ended at wave {centi / 100}.{centi % 100:D2}. {fought} fought.";

    /// <summary>
    /// The allegiance's display name: its own name, else "MONARCH's allegiance", else "a former allegiance".
    /// Pure, so the fallback order is testable.
    /// </summary>
    public static string ChooseLabel(string allegianceName, string monarchName)
    {
        if (!string.IsNullOrWhiteSpace(allegianceName))
            return allegianceName.Trim();

        if (!string.IsNullOrWhiteSpace(monarchName))
            return $"{monarchName.Trim()}'s allegiance";

        return "a former allegiance";
    }

    private static string AllegianceLabel(uint avatarId)
    {
        try
        {
            var (allegiance, monarch) = NameSource(avatarId);
            return ChooseLabel(allegiance, monarch);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] allegiance name lookup for Avatar {avatarId} threw: {ex.Message}");
            return ChooseLabel(null, null);
        }
    }

    /// <summary>
    /// Live lookup. In-memory only: the allegiance is read from the loaded set and the monarch's name from
    /// PlayerManager, so rendering a board never touches the database. A retired or absorbed Avatar has no monarch.
    /// </summary>
    private static (string, string) LiveNames(uint avatarId)
    {
        var monarchGuid = AvatarManager.Get(avatarId)?.MonarchGuid;

        if (monarchGuid == null)
            return (null, null);

        string allegianceName = null;

        if (AllegianceManager.Allegiances.TryGetValue(new ObjectGuid(monarchGuid.Value), out var allegiance))
            allegianceName = allegiance.AllegianceName;

        return (allegianceName, PlayerManager.FindByGuid(monarchGuid.Value)?.Name);
    }

    private static AllegianceGauntletRecord Copy(AllegianceGauntletRecord r) => new()
    {
        AvatarId = r.AvatarId,
        BestCenti = r.BestCenti,
        BestParticipants = r.BestParticipants,
        BestRunId = r.BestRunId,
        BestAt = r.BestAt,
        LastRunAt = r.LastRunAt,
        RunCount = r.RunCount,
        UpdatedAt = r.UpdatedAt,
    };
}
