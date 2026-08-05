using MySqlConnector;

namespace ACE.Dashboard;

public record RosterEntry(uint CharacterId, string Name, int Level, int Landblock);
public record BlockPop(int Landblock, int Players);
public record RateEntry(uint CharacterId, string Name, long Total, double Secs, double PerHour);
public record FlowEdge(uint FromId, string FromName, uint ToId, string ToName, long TotalValue, long TotalItems, int Transfers, bool OneDirectional);
public record BankTransfer(DateTime Ts, string FromName, string ToName, string Currency, long Amount);

/// <summary>
/// Read-only queries against the ace_analytics database (written by the game server's
/// AnalyticsManager). Aggregate columns (SUM/COUNT) come back as DECIMAL/BIGINT, so they're
/// read via Convert to stay type-agnostic.
/// </summary>
public sealed class AnalyticsQueries
{
    private readonly string _cs;

    public AnalyticsQueries(string connectionString) => _cs = connectionString;

    private MySqlConnection Open()
    {
        var c = new MySqlConnection(_cs);
        c.Open();
        return c;
    }

    public List<RosterEntry> GetRoster()
    {
        using var c = Open();
        using var cmd = new MySqlCommand("SELECT character_id,name,level,landblock FROM live_roster ORDER BY level DESC, name", c);
        using var r = cmd.ExecuteReader();
        var list = new List<RosterEntry>();
        while (r.Read())
            list.Add(new RosterEntry(r.GetUInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    public List<BlockPop> GetBlocks()
    {
        using var c = Open();
        using var cmd = new MySqlCommand("SELECT landblock,players FROM live_landblock_pop ORDER BY players DESC", c);
        using var r = cmd.ExecuteReader();
        var list = new List<BlockPop>();
        while (r.Read())
            list.Add(new BlockPop(r.GetInt32(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>
    /// Top earners in the trailing <paramref name="hours"/> window, ordered by RATE.
    ///
    /// Ordering by rate rather than by total is deliberate, and it used to be wrong here: the
    /// query ordered by SUM(gained) while the page displayed a per-hour column, so the list was
    /// a totals ranking wearing a rate label. Rate is the ordering that matters for spotting an
    /// exploit - a big total is usually just a long session.
    ///
    /// <paramref name="minEarnSecs"/> is what makes a rate ordering usable at all. A character
    /// only gets a row for a flush interval in which it actually earned, so a single large
    /// one-off grant (a quest turn-in) divided by one 60s interval reads as an astronomical
    /// hourly rate and takes the top slot. Observed on stage: the #1 slot by raw rate was a
    /// character with 0.77 accumulated earning hours. Floor it and the list becomes meaningful.
    /// Clamped to >= 1 so the ORDER BY cannot divide by a zero denominator.
    /// </summary>
    public List<RateEntry> GetRateLeaderboard(bool xp, int hours, int limit, int minEarnSecs = 600)
    {
        var col = xp ? "xp_gained" : "lum_gained";
        using var c = Open();
        using var cmd = new MySqlCommand(
            $"SELECT character_id,name,SUM({col}) t,SUM(secs) s FROM char_rate_interval " +
            "WHERE ts_utc > UTC_TIMESTAMP() - INTERVAL @h HOUR GROUP BY character_id,name " +
            "HAVING t > 0 AND s >= @m ORDER BY t / s DESC LIMIT @l", c);
        cmd.Parameters.AddWithValue("@h", hours);
        cmd.Parameters.AddWithValue("@m", Math.Max(minEarnSecs, 1));
        cmd.Parameters.AddWithValue("@l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<RateEntry>();
        while (r.Read())
        {
            var t = Convert.ToInt64(r.GetValue(2));
            var s = Convert.ToDouble(r.GetValue(3));
            var perHour = s > 0 ? t / s * 3600.0 : 0;
            list.Add(new RateEntry(r.GetUInt32(0), r.GetString(1), t, s, perHour));
        }
        return list;
    }

    public List<FlowEdge> GetTopItemEdges(int hours, int limit)
    {
        using var c = Open();
        // Directed from->to edges by value moved, with the reverse-edge value so we can flag
        // one-directional flow (the mule/RMT signature).
        using var cmd = new MySqlCommand(
            "SELECT a.from_id,a.from_name,a.to_id,a.to_name,SUM(a.value) v,SUM(a.stack_size) items,COUNT(*) n," +
            " COALESCE((SELECT SUM(b.value) FROM item_flow_event b" +
            "   WHERE b.from_id=a.to_id AND b.to_id=a.from_id AND b.to_is_player=1" +
            "     AND b.ts_utc > UTC_TIMESTAMP() - INTERVAL @h HOUR),0) rev" +
            " FROM item_flow_event a" +
            " WHERE a.to_is_player=1 AND a.ts_utc > UTC_TIMESTAMP() - INTERVAL @h HOUR" +
            " GROUP BY a.from_id,a.from_name,a.to_id,a.to_name ORDER BY v DESC LIMIT @l", c);
        cmd.Parameters.AddWithValue("@h", hours);
        cmd.Parameters.AddWithValue("@l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<FlowEdge>();
        while (r.Read())
        {
            var v = Convert.ToInt64(r.GetValue(4));
            var items = Convert.ToInt64(r.GetValue(5));
            var n = Convert.ToInt32(r.GetValue(6));
            var rev = Convert.ToInt64(r.GetValue(7));
            var oneWay = v > 0 && rev * 10 < v; // reverse flow < 10% of forward
            list.Add(new FlowEdge(r.GetUInt32(0), r.GetString(1), r.GetUInt32(2), r.GetString(3), v, items, n, oneWay));
        }
        return list;
    }

    public List<BankTransfer> GetBankTransfers(int limit)
    {
        using var c = Open();
        using var cmd = new MySqlCommand("SELECT ts_utc,from_name,to_name,currency,amount FROM currency_flow_event ORDER BY ts_utc DESC LIMIT @l", c);
        cmd.Parameters.AddWithValue("@l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<BankTransfer>();
        while (r.Read())
            list.Add(new BankTransfer(r.GetDateTime(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)));
        return list;
    }
}
