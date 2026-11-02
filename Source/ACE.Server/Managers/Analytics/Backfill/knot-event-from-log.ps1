<#
.SYNOPSIS
    Reconstructs knot_event fiber_roll / pattern_roll rows for Knot rolls that predate the live
    telemetry, by reading a saved server log and emitting a .sql file. NEVER connects to a database itself.

.DESCRIPTION
    knot_event (ace_analytics.sql) is written live by KnotTelemetry. Rolls made before that writer
    shipped exist only as the summary line KnotDrops.RollSeat logs per rolling seat of a cleared run (one line per seat;
    before per-seat rolling, one line per run). Both formats are accepted. The older owner-only shape:

      2026-10-21 12:16:00,437 [World Manager] INFO  (ACE.Server.Knots.KnotDrops) [KNOTS] run 0x8000FC92 tiny_hive
          owner=Billdozer state=Cleared trash=66/75 (need 66) boss=dead progress=90%/90% rolled: level=364
          misses=0 fiber=dry missesNow=1 pattern=none enabledPinnacles=1

    The thread field is either a number ([55]) or a name ([World Manager]); the pattern accepts both.

    For every such line the generated SQL writes one fiber_roll and, when enabledPinnacles > 0, one
    pattern_roll (the Pattern roll never ran with none enabled, exactly as the live writer behaves).
    Pattern: none -> result 'none'; LOST -> 'lost'; anything else is the pinnacle key -> 'dropped'.
    Fiber: dropped / dry / LOST -> 'dropped' / 'dry' / 'lost'.

    WHAT A 'log' ROW CANNOT KNOW. item_guid and char_level are NULL (the log never carried them).
    For an old-format line character_id and account_id come from the newest char_bank_snapshot row with the same
    name, and are 0 when none matches (a renamed or deleted character); see TWO LINE FORMATS for the seat format.
    start_group comes from the dungeon_run
    row with the same run_id and RUN-owner name whose [started_utc, ended_utc] contains the roll, else NULL.
    run_id recycles, so the name and time window together are the match, never the run id alone.

    TWO LINE FORMATS. Before per-seat rolling the line read "... owner=<Name> state=... rolled: level=..." and
    only the run's owner rolled; that format is still in older logs and is accepted. Since per-seat rolling each
    rolling seat logs "... state=... seat 0x<hex> (<Name>) rolled: level=...", one line per seat. For that format
    character_id is the seat hex itself and the name is the seat's; for the old format both come from the owner and
    the char_bank_snapshot lookup. account_id comes from that lookup: by character_id for the seat format (a
    rename or a reused name cannot mislead it), by name for the old format. The seat format keeps the seat's name
    exactly as logged, so an empty "()" stays an empty name. start_group is matched on the
    RUN's owner either way, since dungeon_run.name is the run owner.

    TIMEZONE. Log timestamps are host-local UTC-4 (fixed offset); every one gets exactly 4 hours added
    before it is written.

    IDEMPOTENCY / OVERLAP. The script deletes every source='log' row and re-inserts, so running it
    twice leaves the same rows. It only inserts rows older than the first source='live' row, so it can
    never double-count a roll the live writer already recorded. Duplicate identical lines (a log fed
    twice) collapse through SELECT DISTINCT.

    The emitted file ends with a validation SELECT of row counts by kind and result.

.PARAMETER LogPath
    Path to a saved server log file (plain text). Required.

.PARAMETER OutputPath
    Where to write the generated .sql file. Required.

.EXAMPLE
    .\knot-event-from-log.ps1 -LogPath C:\logs\ace-server-2026-10-21.log -OutputPath C:\scratch\knot-backfill.sql
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogPath)) {
    throw "Log file not found: $LogPath"
}

$rollRe = [regex]::new('^(?<ts>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d),\d{3} \[[^\]]+\]\s+INFO\s+\(ACE\.Server\.Knots\.KnotDrops\) \[KNOTS\] run 0x(?<run>[0-9A-Fa-f]{8}) (?<dungeon>\S*) owner=(?<owner>.+?) state=\S+ .*?(?:seat 0x(?<seat>[0-9A-Fa-f]{8}) \((?<sname>.*?)\) )?rolled: level=(?<level>\d+) misses=(?<misses>\d+) fiber=(?<fiber>dropped|dry|LOST) missesNow=(?<now>\d+) pattern=(?<pattern>\S+) enabledPinnacles=(?<en>\d+)')

function ConvertTo-Utc {
    param([string]$Local)
    $dt = [datetime]::ParseExact($Local, 'yyyy-MM-dd HH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture)
    return $dt.AddHours(4)
}

function Format-SqlString {
    param([string]$Value)
    return "'" + $Value.Replace('\', '\\').Replace("'", "''") + "'"
}

# ---- parse ---------------------------------------------------------------------------------------

$rows = New-Object System.Collections.Generic.List[object]
$lineCount = 0

foreach ($line in Get-Content -LiteralPath $LogPath) {
    $lineCount++
    $m = $rollRe.Match($line)
    if (-not $m.Success) { continue }

    $rows.Add(@{
        Ts      = (ConvertTo-Utc $m.Groups['ts'].Value).ToString('yyyy-MM-dd HH:mm:ss')
        Run     = [Convert]::ToUInt32($m.Groups['run'].Value, 16)
        Dungeon = $m.Groups['dungeon'].Value
        RunOwner = $m.Groups['owner'].Value
        Seat    = $(if ($m.Groups['seat'].Success) { [Convert]::ToUInt32($m.Groups['seat'].Value, 16) } else { $null })
        Owner   = $(if ($m.Groups['seat'].Success) { $m.Groups['sname'].Value } else { $m.Groups['owner'].Value })
        Level   = [int]$m.Groups['level'].Value
        Misses  = [int]$m.Groups['misses'].Value
        Fiber   = $m.Groups['fiber'].Value
        Now     = [int]$m.Groups['now'].Value
        Pattern = $m.Groups['pattern'].Value
        En      = [int]$m.Groups['en'].Value
    })
}

if ($rows.Count -eq 0) {
    throw "No Knot roll lines found in $LogPath ($lineCount lines read). Nothing written."
}

# ---- emit ----------------------------------------------------------------------------------------

$sql = New-Object System.Collections.Generic.List[string]
$sql.Add("-- knot_event backfill from $([System.IO.Path]::GetFileName($LogPath)): $($rows.Count) roll lines parsed from $lineCount lines.")
$sql.Add("-- Generated by knot-event-from-log.ps1. Log timestamps are UTC-4; +4h applied.")
$sql.Add("")
$sql.Add("DROP TEMPORARY TABLE IF EXISTS ``knot_log_stage``;")
$sql.Add("CREATE TEMPORARY TABLE ``knot_log_stage`` (")
$sql.Add("    ``ts_utc``  DATETIME     NOT NULL,")
$sql.Add("    ``run_hex`` INT UNSIGNED NOT NULL,")
$sql.Add("    ``dungeon`` VARCHAR(16)  NOT NULL,")
$sql.Add("    ``run_owner`` VARCHAR(64) NOT NULL,")
$sql.Add("    ``seat``    INT UNSIGNED NULL,")
$sql.Add("    ``owner``   VARCHAR(64)  NOT NULL,")
$sql.Add("    ``level``   INT          NOT NULL,")
$sql.Add("    ``misses``  INT          NOT NULL,")
$sql.Add("    ``fiber``   VARCHAR(8)   NOT NULL,")
$sql.Add("    ``now``     INT          NOT NULL,")
$sql.Add("    ``pattern`` VARCHAR(32)  NOT NULL,")
$sql.Add("    ``en``      INT          NOT NULL")
$sql.Add(") DEFAULT CHARSET=utf8mb4;")
$sql.Add("")

$chunk = 500
for ($i = 0; $i -lt $rows.Count; $i += $chunk) {
    $end = [Math]::Min($i + $chunk, $rows.Count) - 1
    $values = New-Object System.Collections.Generic.List[string]
    for ($j = $i; $j -le $end; $j++) {
        $r = $rows[$j]
        $values.Add("('$($r.Ts)',$($r.Run),$(Format-SqlString $r.Dungeon),$(Format-SqlString $r.RunOwner),$(if ($null -ne $r.Seat) { $r.Seat } else { 'NULL' }),$(Format-SqlString $r.Owner),$($r.Level),$($r.Misses),$(Format-SqlString $r.Fiber),$($r.Now),$(Format-SqlString $r.Pattern),$($r.En))")
    }
    $sql.Add("INSERT INTO ``knot_log_stage`` (``ts_utc``,``run_hex``,``dungeon``,``run_owner``,``seat``,``owner``,``level``,``misses``,``fiber``,``now``,``pattern``,``en``) VALUES")
    $sql.Add(($values -join ",`n") + ";")
}

$sql.Add("")
$sql.Add("-- Two small lookups, each read from char_bank_snapshot once (a correlated subquery per roll line would scan the whole")
$sql.Add("-- bank table per line). A new-format line names its character by id, so its account is the newest snapshot for that")
$sql.Add("-- character_id (a rename or a reused name cannot mislead it); an old-format line only has a name, so it takes the")
$sql.Add("-- newest snapshot for that name.")
$sql.Add("DROP TEMPORARY TABLE IF EXISTS ``knot_log_char_id``;")
$sql.Add("CREATE TEMPORARY TABLE ``knot_log_char_id`` AS")
$sql.Add("SELECT b.``character_id`` AS ``character_id``, b.``account_id`` AS ``account_id``")
$sql.Add("FROM ``char_bank_snapshot`` b")
$sql.Add("JOIN (SELECT ``character_id``, MAX(``id``) AS ``mid`` FROM ``char_bank_snapshot`` WHERE ``character_id`` IN (SELECT DISTINCT ``seat`` FROM ``knot_log_stage`` WHERE ``seat`` IS NOT NULL) GROUP BY ``character_id``) m ON b.``id`` = m.``mid``;")
$sql.Add("")
$sql.Add("DROP TEMPORARY TABLE IF EXISTS ``knot_log_char``;")
$sql.Add("CREATE TEMPORARY TABLE ``knot_log_char`` AS")
$sql.Add("SELECT b.``name`` AS ``name``, b.``character_id`` AS ``character_id``, b.``account_id`` AS ``account_id``")
$sql.Add("FROM ``char_bank_snapshot`` b")
$sql.Add("JOIN (SELECT ``name``, MAX(``id``) AS ``mid`` FROM ``char_bank_snapshot`` WHERE ``name`` IN (SELECT DISTINCT ``owner`` FROM ``knot_log_stage`` WHERE ``seat`` IS NULL) GROUP BY ``name``) m ON b.``id`` = m.``mid``;")
$sql.Add("")
$sql.Add("DELETE FROM ``knot_event`` WHERE ``source`` = 'log';")
$sql.Add("")

$cols = "``ts_utc``,``kind``,``result``,``source``,``character_id``,``name``,``account_id``,``char_level``,``knot_level``,``qty``," +
        "``knot_guid``,``item_guid``,``pinnacle_key``,``entries_left``,``entries_max``,``misses_before``,``misses_after``,``enabled_pinnacles``," +
        "``fiber_levels``,``phase_reached``,``phase_count``,``charged``,``duration_secs``,``reason``,``dungeon_id``,``instance_id``,``start_group``,``attempt_id``"

$charId = "COALESCE(s.``seat``, c.``character_id``, 0)"
$acctId = "COALESCE(i.``account_id``, c.``account_id``, 0)"
$fromJoin = "FROM ``knot_log_stage`` s LEFT JOIN ``knot_log_char_id`` i ON i.``character_id`` = s.``seat`` LEFT JOIN ``knot_log_char`` c ON s.``seat`` IS NULL AND c.``name`` = s.``owner``"
$group  = "(SELECT d.``start_group`` FROM ``dungeon_run`` d WHERE d.``run_id`` = s.``run_hex`` AND d.``name`` = s.``run_owner`` AND s.``ts_utc`` BETWEEN d.``started_utc`` AND d.``ended_utc`` ORDER BY d.``id`` LIMIT 1)"
$cutoff = "s.``ts_utc`` < COALESCE((SELECT MIN(l.``ts_utc``) FROM ``knot_event`` l WHERE l.``source`` = 'live'), '9999-12-31')"

$fiberResult   = "CASE s.``fiber`` WHEN 'dropped' THEN 'dropped' WHEN 'LOST' THEN 'lost' ELSE 'dry' END"
$patternResult = "CASE s.``pattern`` WHEN 'none' THEN 'none' WHEN 'LOST' THEN 'lost' ELSE 'dropped' END"
$patternKey    = "CASE WHEN s.``pattern`` IN ('none','LOST') THEN NULL ELSE s.``pattern`` END"

$sql.Add("-- one fiber_roll per line")
$sql.Add("INSERT INTO ``knot_event`` ($cols)")
$sql.Add("SELECT DISTINCT s.``ts_utc``, 'fiber_roll', $fiberResult, 'log', $charId, s.``owner``, $acctId, NULL, s.``level``, 1,")
$sql.Add("       NULL, NULL, NULL, NULL, NULL, s.``misses``, s.``now``, NULL,")
$sql.Add("       NULL, NULL, NULL, NULL, NULL, NULL, NULLIF(s.``dungeon``, ''), s.``run_hex``, $group, NULL")
$sql.Add($fromJoin)
$sql.Add("WHERE $cutoff;")
$sql.Add("")
$sql.Add("-- a pattern_roll too, only where a pinnacle was enabled (the roll did not run otherwise)")
$sql.Add("INSERT INTO ``knot_event`` ($cols)")
$sql.Add("SELECT DISTINCT s.``ts_utc``, 'pattern_roll', $patternResult, 'log', $charId, s.``owner``, $acctId, NULL, s.``level``, 1,")
$sql.Add("       NULL, NULL, $patternKey, NULL, NULL, NULL, NULL, s.``en``,")
$sql.Add("       NULL, NULL, NULL, NULL, NULL, NULL, NULLIF(s.``dungeon``, ''), s.``run_hex``, $group, NULL")
$sql.Add($fromJoin)
$sql.Add("WHERE s.``en`` > 0 AND $cutoff;")
$sql.Add("")
$sql.Add("DROP TEMPORARY TABLE ``knot_log_char_id``;")
$sql.Add("DROP TEMPORARY TABLE ``knot_log_char``;")
$sql.Add("DROP TEMPORARY TABLE ``knot_log_stage``;")
$sql.Add("")
$sql.Add("-- validation: row counts by kind and result for the backfilled rows")
$sql.Add("SELECT ``kind``, ``result``, COUNT(*) AS ``rows`` FROM ``knot_event`` WHERE ``source`` = 'log' GROUP BY ``kind``, ``result`` ORDER BY ``kind``, ``result``;")

[System.IO.File]::WriteAllText($OutputPath, ($sql -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Parsed $($rows.Count) roll lines from $lineCount lines; wrote $OutputPath"
