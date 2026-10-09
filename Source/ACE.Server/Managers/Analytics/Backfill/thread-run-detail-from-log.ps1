<#
.SYNOPSIS
    Reconstructs dungeon_run_detail rows for Thread runs that predate the live feature, by reading a
    saved server log and emitting an INSERT IGNORE .sql file. NEVER connects to a database itself.

.DESCRIPTION
    dungeon_run_detail (ace_analytics.sql) records the plan-time facts a Thread run's population
    actually drew - which monster family, and the tuning knobs that plan carried. It shipped with a
    live writer (AnalyticsDatabase.WriteDungeonRuns, source='live'), but every run that finished
    before that writer existed has no row. This script recovers what it can for those older runs from
    the server's own log output, which already printed every fact ThreadDungeonSpawner's plan log
    line carries (see ThreadDungeonSpawner.cs, the log.Info call right after DungeonPopulationBuilder.Build).

    It reads three log line shapes, all tagged [DYNDUNGEON]:
      - "started run 0x<hex> ... owner=<name> state=..."                              (ThreadDungeonManager)
      - "run 0x<hex> ... owner=<name> state=... plan: family=... entries=... ..."      (ThreadDungeonSpawner)
      - "cleared run 0x<hex> ... owner=<name> state=Cleared ..."                       (ThreadDungeonManager)

    A row is emitted only for a run that has BOTH a started line and a plan line. A started line with
    no matching plan line (population threw before a plan was built - see ThreadDungeonSpawner.cs's
    catch block around DungeonPopulationBuilder.Build) is counted and reported but produces NO row:
    dungeon_run_detail.plan_built would have to be 0 for it, and this backfill only ever writes
    plan_built = 1 rows (source='log' rows are always plan-derived, by construction - there is nothing
    in the log for a run whose plan never built, because the log line that carries all these facts is
    the plan line itself). A plan line with no matching started line is likewise counted and skipped
    (it should not happen - every plan line is logged from inside the same TryPopulate call a started
    run already registered - and is reported so a real occurrence is visible rather than silently eaten).

    RUN ID RECYCLING. run_id (ThreadDungeonRun.RunId, the ephemeral instance id embedded in the log's
    0x<hex>) recycles across restarts, so the same hex value can legitimately identify two unrelated
    runs within one log file, or across two files fed to this script on separate occasions. Events are
    matched by ORDINAL POSITION within each hex value's own event stream - the Nth "started" line for
    a given hex is paired with the Nth "plan" line and the Nth "cleared" line for that same hex, never
    by nearest-neighbour distance or timestamp proximity - which stays correct even if the id recycles
    within the file, as long as one run's own started/plan/cleared lines appear in that order relative
    to each other (they always do: a run cannot clear before it starts, and cannot log a plan before
    TryPopulate runs).

    JOIN KEY. The generated SQL never carries a run_fk directly - it resolves one server-side, inside
    each generated statement, from three independent facts that together are extremely unlikely to
    collide: the numeric run_id, the owner name (dungeon_run.name), and a 3-minute window around the
    log's own started timestamp (converted from host-local UTC-4 to UTC) compared against
    dungeon_run.started_utc. A run matching none of the three combined produces zero affected rows for
    that statement rather than a wrong row, because run_id + name + a tight time window is the actual
    identity of one dungeon_run row for any realistic collision rate.

    IDEMPOTENCY / DUPLICATES. This script does NOT deduplicate anything itself - the same plan line
    fed to it twice (a second run over an overlapping log window, or a log that was reprocessed)
    produces two textually-identical INSERT IGNORE statements. This is deliberately cheap rather than
    clever: dungeon_run_detail's primary key is run_fk, so the second statement's row either already
    exists (ignored, 0 rows affected) or the join failed identically both times (0 rows affected
    either way). Running this backfill's output against the same database twice therefore always
    inserts the same total row count as running it once - that is the whole idempotency contract, and
    it needs no PowerShell-side bookkeeping to hold.

    TIMEZONE. Log timestamps are host-local UTC-4 (fixed offset, not DST-aware - see CLAUDE.md's "Log
    TZ = host local UTC-4" note); dungeon_run.started_utc is stored in true UTC. Every log timestamp
    this script reads is therefore converted by adding exactly 4 hours before it is compared against
    or written into the generated SQL.

    FIELDS WITH NO LOG TOKEN. Several dungeon_run_detail columns have no corresponding token anywhere
    in ThreadDungeonSpawner's plan log line and are therefore ALWAYS NULL in a 'log' row:
    trash_planned, elite_planned (only the combined entries= total is logged, not the role split),
    band_standard_samples, run_speed_mult, ignore_shield, hollow_intensity, and loot_quantity_mult.
    This is not a script limitation to fix later - the log line itself never carried these values, so
    there is nothing on disk to recover them from for any run recorded before the live writer existed.

.PARAMETER LogPath
    Path to a saved server log file (plain text). Required.

.PARAMETER OutputPath
    Where to write the generated .sql file. Required. This script is meant to be re-run against
    different log snapshots over time, so there is no repo-relative default - the caller always names
    the output explicitly.

.EXAMPLE
    .\thread-run-detail-from-log.ps1 -LogPath C:\logs\ace-server-2026-09-15.log -OutputPath C:\scratch\backfill.sql
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

# ---- regexes -----------------------------------------------------------------------------------
# All three line shapes share a timestamp prefix and are tagged [DYNDUNGEON]. Owner names can
# contain spaces (e.g. "owner=Thickity Thwack state="), so the owner capture is lazy up to the
# literal " state=" terminator rather than a single non-space token.

$tsPattern = '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}),(?<ms>\d{3})'

$startedRe = [regex]::new($tsPattern + '.*\[DYNDUNGEON\] started run 0x(?<runid>[0-9A-Fa-f]+) \S+ owner=(?<owner>.+?) state=')
$clearedRe = [regex]::new($tsPattern + '.*\[DYNDUNGEON\] cleared run 0x(?<runid>[0-9A-Fa-f]+) \S+ owner=(?<owner>.+?) state=')

# The plan line is logged by ThreadDungeonSpawner (not ThreadDungeonManager, unlike the other two),
# from the exact same "run 0x<hex> ... owner=... state=... plan: ..." shape ThreadDungeonSpawner.cs
# builds around line 264-269. Matched on "ThreadDungeonSpawner) [DYNDUNGEON] run 0x" rather than just
# "run 0x" so it is never confused with the started/cleared lines, which come from ThreadDungeonManager.
$planRe = [regex]::new($tsPattern + '.*ThreadDungeonSpawner\) \[DYNDUNGEON\] run 0x(?<runid>[0-9A-Fa-f]+) \S+ owner=(?<owner>.+?) state=\S+ .*plan: (?<body>family=.+)$')

# Plan-body token regexes. Case-sensitive and matched against the literal token text, which is what
# keeps e.g. "xp x2" (xp_multiplier) from ever matching inside "bossXp x1.37" or "xpScale x1.57" -
# those are spelled "Xp x" and "xpScale x" respectively, neither of which is the literal "xp x" this
# script looks for.
function Get-Token {
    param([string]$Body, [string]$Pattern)
    $m = [regex]::Match($Body, $Pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

# ---- pass 1: collect ordered events per hex run id ---------------------------------------------

$startedByHex = @{}
$planByHex = @{}
$clearedByHex = @{}

$lineCount = 0

foreach ($line in Get-Content -LiteralPath $LogPath) {
    $lineCount++

    $m = $startedRe.Match($line)
    if ($m.Success) {
        $hex = $m.Groups['runid'].Value.ToUpperInvariant()
        if (-not $startedByHex.ContainsKey($hex)) { $startedByHex[$hex] = New-Object System.Collections.Generic.List[object] }
        $startedByHex[$hex].Add(@{ Ts = $m.Groups['ts'].Value; Ms = $m.Groups['ms'].Value; Owner = $m.Groups['owner'].Value })
        continue
    }

    $m = $planRe.Match($line)
    if ($m.Success) {
        $hex = $m.Groups['runid'].Value.ToUpperInvariant()
        if (-not $planByHex.ContainsKey($hex)) { $planByHex[$hex] = New-Object System.Collections.Generic.List[object] }
        $planByHex[$hex].Add(@{ Ts = $m.Groups['ts'].Value; Ms = $m.Groups['ms'].Value; Owner = $m.Groups['owner'].Value; Body = $m.Groups['body'].Value })
        continue
    }

    $m = $clearedRe.Match($line)
    if ($m.Success) {
        $hex = $m.Groups['runid'].Value.ToUpperInvariant()
        if (-not $clearedByHex.ContainsKey($hex)) { $clearedByHex[$hex] = New-Object System.Collections.Generic.List[object] }
        $clearedByHex[$hex].Add(@{ Ts = $m.Groups['ts'].Value; Ms = $m.Groups['ms'].Value; Owner = $m.Groups['owner'].Value })
        continue
    }
}

# ---- pass 2: pair by ordinal position within each hex's own event stream -----------------------

$rows = New-Object System.Collections.Generic.List[object]
$startedWithoutPlan = 0
$planWithoutStarted = 0

foreach ($hex in $startedByHex.Keys) {
    $starts = $startedByHex[$hex]

    # Deliberately PLAIN assignment statements, never `$x = if (...) {...} else {...}`. That form
    # sends the chosen branch's value through PowerShell's pipeline, and a List<object> (or a
    # Hashtable, which is also IEnumerable) gets UNROLLED into its elements on the way through -
    # silently, with no error - which for a one-element list collapses $plans into that single
    # element itself rather than a list containing it, corrupting every $plans.Count and $plans[$i]
    # access downstream. A plain `if (...) { $x = ... } else { $x = ... }` assigns directly and never
    # touches the pipeline, so the reference survives intact regardless of element count.
    if ($planByHex.ContainsKey($hex)) { $plans = $planByHex[$hex] } else { $plans = New-Object System.Collections.Generic.List[object] }
    if ($clearedByHex.ContainsKey($hex)) { $cleareds = $clearedByHex[$hex] } else { $cleareds = New-Object System.Collections.Generic.List[object] }

    for ($i = 0; $i -lt $starts.Count; $i++) {
        if ($i -ge $plans.Count) {
            $startedWithoutPlan++
            continue
        }

        # Same reasoning applies to a single hashtable value: resolve it with a plain if/else into
        # its own variable first, never inline as a hashtable-literal value expression.
        if ($i -lt $cleareds.Count) { $clearedEvent = $cleareds[$i] } else { $clearedEvent = $null }

        $rows.Add(@{
            Hex = $hex
            Started = $starts[$i]
            Plan = $plans[$i]
            Cleared = $clearedEvent
        })
    }
}

foreach ($hex in $planByHex.Keys) {
    $planCount = $planByHex[$hex].Count
    if ($startedByHex.ContainsKey($hex)) { $startCount = $startedByHex[$hex].Count } else { $startCount = 0 }
    if ($planCount -gt $startCount) {
        $planWithoutStarted += ($planCount - $startCount)
    }
}

# ---- pass 3: build one INSERT IGNORE statement per paired run -----------------------------------

function ConvertTo-Utc {
    param([string]$LogTimestamp, [string]$LogMillis)
    # Log timestamps are host-local UTC-4, fixed offset (not DST-aware). See CLAUDE.md. The
    # milliseconds component (the ",mmm" suffix log4net appends) is captured separately by
    # $tsPattern's ms group and added back in here - dropping it quantizes cleared_secs to whole
    # seconds, which is wrong for a value that is supposed to be a precise duration.
    $local = [datetime]::ParseExact($LogTimestamp, 'yyyy-MM-dd HH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture)
    $ms = 0
    if (-not [string]::IsNullOrEmpty($LogMillis)) { $ms = [int]$LogMillis }
    $local = $local.AddMilliseconds($ms)
    return $local.AddHours(4)
}

function Format-SqlDateTime {
    param([datetime]$Value)
    return $Value.ToString('yyyy-MM-dd HH:mm:ss')
}

function Format-SqlString {
    param([string]$Value)
    # MySQL string escaping: backslash and single quote are the two characters that matter here.
    # Owner names are free text (a player can name a character almost anything short of the client's
    # own reserved characters), so this is not defensive paranoia - it is required correctness.
    if ($null -eq $Value) { return "''" }
    $escaped = $Value.Replace('\', '\\').Replace("'", "''")
    return "'$escaped'"
}

function Format-SqlStringOrNull {
    # Deliberately UNTYPED $Value parameter, not [string]$Value: PowerShell coerces a $null argument
    # to an empty string when the parameter IS typed [string], which silently defeats the null check
    # below (a caller passing $null would see $Value come in as '' instead, and fall through to the
    # empty-string-literal branch every time). Leaving the parameter untyped is what lets $null survive
    # the call intact.
    param($Value)
    # Same escaping as Format-SqlString, but a null value becomes the SQL keyword NULL rather than an
    # empty-string literal. Used for family_id: "no family" must be a real NULL (matching the live
    # writer's plan.FamilyId == null -> DBNull.Value path), never the empty string ''.
    if ($null -eq $Value) { return 'NULL' }
    $escaped = $Value.Replace('\', '\\').Replace("'", "''")
    return "'$escaped'"
}

function Format-SqlNumberOrNull {
    param([string]$Token)
    if ([string]::IsNullOrEmpty($Token)) { return 'NULL' }
    return $Token
}

function Format-SqlBoolOrNull {
    param([string]$Token)
    if ([string]::IsNullOrEmpty($Token)) { return 'NULL' }
    if ($Token -eq 'True') { return '1' }
    if ($Token -eq 'False') { return '0' }
    return 'NULL'
}

$sqlStatements = New-Object System.Collections.Generic.List[string]

foreach ($row in $rows) {
    $body = $row.Plan.Body

    # ThreadDungeonSpawner.cs:264 logs "family={plan.FamilyId ?? "none"}" - the literal token 'none'
    # means the plan drew no family, and must round-trip to a real SQL NULL (matching the live
    # writer's plan.FamilyId == null -> DBNull.Value path), never the string 'none'.
    $family = Get-Token $body 'family=(\S+)'
    if ($family -eq 'none') { $family = $null }
    $entries = Get-Token $body 'entries=(\d+)'
    $bossLevel = Get-Token $body 'bossLevel=(\d+)'
    $bandLowMatch = [regex]::Match($body, 'bandLow=(?<eff>\d+)/natural (?<nat>\d+)')
    # Plain if/else assignment statements, same reasoning as the block above: never
    # `$x = if (...) {...} else {...}`, which routes the chosen branch through the pipeline.
    if ($bandLowMatch.Success) { $effectiveBandLow = $bandLowMatch.Groups['eff'].Value } else { $effectiveBandLow = $null }
    if ($bandLowMatch.Success) { $naturalBandLow = $bandLowMatch.Groups['nat'].Value } else { $naturalBandLow = $null }
    $uplifted = Get-Token $body 'uplifted=(\d+)'
    $bossNormalize = Get-Token $body 'bossNormalize=(True|False)'
    $bossBase = Get-Token $body 'bossBase=([\d.]+)'
    $poolMax = Get-Token $body 'poolMax (\d+)\)'
    $stripTraits = Get-Token $body 'stripTraits=(True|False)'
    $hp = Get-Token $body '(?<!boss)(?<!\S)hp x([\d.]+)'
    $bossHp = Get-Token $body 'bossHp x([\d.]+)'
    $trashHpFloor = Get-Token $body 'trashHpFloor=(\d+)'
    $hpCurve = Get-Token $body 'hpCurve=([\d.]+)'
    $hpNorm = Get-Token $body 'hpNorm=x([\d.]+)'
    $xp = Get-Token $body '(?<!\S)xp x([\d.]+)'
    $bossXp = Get-Token $body 'bossXp x([\d.]+)'
    $lum = Get-Token $body '(?<!\S)lum x([\d.]+)'
    $xpScale = Get-Token $body 'xpScale x([\d.]+)'
    $lumScale = Get-Token $body 'lumScale x([\d.]+)'
    # The six rating fields (dr/bossDr/cr/cdr/drr/bossDrr) are DungeonRewardMath.RatingTotal sums of
    # signed per-modifier magnitudes (see DungeonRewardMath.cs's RatingTotal), so a negative total is a
    # real possibility, not just a defensive allowance - these tokens accept an optional leading '-'.
    # Every other numeric token below (entries/bossLevel/bandLow/uplifted/bossBase/poolMax/hp/xp/lum/
    # tier/etc.) comes from a count, a level, a health value, or a multiplier that is never negative in
    # DungeonSpawnPlan/DungeonPopulationBuilder, so those keep the plain \d+ / [\d.]+ forms.
    $dr = Get-Token $body '(?<!\S)dr=(-?\d+)'
    $bossDr = Get-Token $body 'bossDr=(-?\d+)'
    $cr = Get-Token $body '(?<!\S)cr=(-?\d+)'
    $cdr = Get-Token $body 'cdr=(-?\d+)'
    $drr = Get-Token $body '(?<!\S)drr=(-?\d+)'
    $bossDrr = Get-Token $body 'bossDrr=(-?\d+)'
    $tier = Get-Token $body 'tier=(\d+)'

    $startedUtc = ConvertTo-Utc $row.Started.Ts $row.Started.Ms
    $lowerBound = $startedUtc.AddMinutes(-3)
    $upperBound = $startedUtc.AddMinutes(3)

    $clearedSecs = 'NULL'
    if ($row.Cleared) {
        $clearedUtc = ConvertTo-Utc $row.Cleared.Ts $row.Cleared.Ms
        $secs = ($clearedUtc - $startedUtc).TotalSeconds
        $clearedSecs = $secs.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture)
    }

    $runIdDecimal = [Convert]::ToUInt32($row.Hex, 16)
    $ownerName = $row.Started.Owner

    # Column and table names are written WITHOUT backtick quoting here, deliberately: none of them are
    # MySQL reserved words, and a backtick inside a PowerShell DOUBLE-quoted here-string is an escape
    # character (see CLAUDE.md's here-string note) - `t becomes a literal tab, `r a literal CR, and so
    # on, silently corrupting every column name that happened to contain one of those letters right
    # after a backtick. Quoting is not needed for correctness here, so the simplest fix is to not fight
    # the escaping at all.
    $columnList = 'run_fk,source,plan_built,cleared_secs,family_id,trash_planned,elite_planned,entries_planned,uplifted_planned,' +
        'natural_band_low,effective_band_low,boss_level,boss_normalize,boss_health_base,pool_max_base,health_multiplier,' +
        'boss_health_multiplier,health_curve_target,health_normalize_ratio,trash_health_floor,band_standard_samples,' +
        'damage_rating,crit_rating,crit_damage_rating,damage_resist_rating,boss_damage_rating,boss_damage_resist_rating,' +
        'run_speed_mult,ignore_shield,hollow_intensity,strip_combat_traits,xp_multiplier,boss_xp_multiplier,lum_multiplier,' +
        'xp_scale,lum_scale,loot_tier,loot_quantity_mult'

    $selectValues = @(
        "r.id", "'log'", "1", $clearedSecs, (Format-SqlStringOrNull $family), 'NULL', 'NULL',
        (Format-SqlNumberOrNull $entries), (Format-SqlNumberOrNull $uplifted),
        (Format-SqlNumberOrNull $naturalBandLow), (Format-SqlNumberOrNull $effectiveBandLow),
        (Format-SqlNumberOrNull $bossLevel), (Format-SqlBoolOrNull $bossNormalize),
        (Format-SqlNumberOrNull $bossBase), (Format-SqlNumberOrNull $poolMax),
        (Format-SqlNumberOrNull $hp), (Format-SqlNumberOrNull $bossHp),
        (Format-SqlNumberOrNull $hpCurve), (Format-SqlNumberOrNull $hpNorm),
        (Format-SqlNumberOrNull $trashHpFloor), 'NULL',
        (Format-SqlNumberOrNull $dr), (Format-SqlNumberOrNull $cr), (Format-SqlNumberOrNull $cdr),
        (Format-SqlNumberOrNull $drr), (Format-SqlNumberOrNull $bossDr), (Format-SqlNumberOrNull $bossDrr),
        'NULL', 'NULL', 'NULL', (Format-SqlBoolOrNull $stripTraits),
        (Format-SqlNumberOrNull $xp), (Format-SqlNumberOrNull $bossXp), (Format-SqlNumberOrNull $lum),
        (Format-SqlNumberOrNull $xpScale), (Format-SqlNumberOrNull $lumScale),
        (Format-SqlNumberOrNull $tier), 'NULL'
    )

    $lowerBoundSql = "'" + (Format-SqlDateTime $lowerBound) + "'"
    $upperBoundSql = "'" + (Format-SqlDateTime $upperBound) + "'"

    $sql = "INSERT IGNORE INTO dungeon_run_detail ($columnList)`n" +
        "SELECT " + ($selectValues -join ', ') + "`n" +
        "FROM dungeon_run r`n" +
        "WHERE r.run_id = $runIdDecimal AND r.name = $(Format-SqlString $ownerName) AND r.started_utc BETWEEN $lowerBoundSql AND $upperBoundSql;"

    $sqlStatements.Add($sql)
}

# ---- write output --------------------------------------------------------------------------------

$header = @"
-- Generated by thread-run-detail-from-log.ps1 from $LogPath
-- $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') (local)
-- $($rows.Count) run(s) matched a started line and a plan line.
-- Every statement is INSERT IGNORE against the primary key (run_fk), so re-running this same file,
-- or a file covering an overlapping window, against the same database is always safe: a row that
-- already exists is skipped, never duplicated or overwritten.
BEGIN;
"@

$footer = "COMMIT;`n"

$content = @($header) + $sqlStatements + @($footer)
[System.IO.File]::WriteAllText($OutputPath, ($content -join "`n"), (New-Object System.Text.UTF8Encoding($false)))

# ---- summary ---------------------------------------------------------------------------------

Write-Host "lines read: $lineCount"
Write-Host "runs emitted (started + plan both present): $($rows.Count)"
Write-Host "runs with a started line but no plan line: $startedWithoutPlan"
Write-Host "runs with a plan line but no started line: $planWithoutStarted"
Write-Host "total runs matched (emitted statements): $($rows.Count)"
Write-Host "output written to: $OutputPath"
