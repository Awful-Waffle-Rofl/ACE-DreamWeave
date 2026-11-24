# Resets ace_shard_loadtest to a known, reproducible baseline state, so `suite` runs are always apples-to-apples.
# Run this before every "before" measurement and every "after" measurement - not just once - since seed scenarios
# are additive and the table sizes they produce directly affect the benchmark numbers (landblock-population,
# character-list-scaling, and type-scan all depend on a specific seeded row count).
#
# Usage: .\reset-baseline.ps1
#
# Requires: MySQL Server running locally, Config.js in this directory pointed at ace_shard_loadtest,
# and the project already built (dotnet build -p:Platform=x64).

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 has no $IsWindows; it only runs on Windows.
$isWin = ($PSVersionTable.PSEdition -eq 'Desktop') -or $IsWindows

$mysql = if ($isWin) { "C:\Program Files\MySQL\MySQL Server 8.0\bin\mysql.exe" } else { "mysql" }
$mysqldump = if ($isWin) { "C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqldump.exe" } else { "mysqldump" }
$mysqlHost = "127.0.0.1"
$mysqlPort = 3306
$mysqlUser = "root"
$mysqlPassword = "WeakPassword1!"

$exeName = if ($isWin) { "ACE.Database.LoadTest.exe" } else { "ACE.Database.LoadTest" }
$exe = Join-Path $PSScriptRoot "bin/x64/Debug/net10.0/$exeName"
$dumpFile = Join-Path ([System.IO.Path]::GetTempPath()) "ace_shard_loadtest_reset_$PID.sql"

# The password is passed via the MYSQL_PWD environment variable, never as a -p"..." command-line argument.
# mysql.exe/mysqldump.exe both emit "[Warning] Using a password on the command line interface can be
# insecure" to stderr whenever -p is used on the command line, and with $ErrorActionPreference = "Stop"
# that stderr output can terminate the script mid-run - including in the window right after the DROP
# DATABASE below, which would leave ace_shard_loadtest gone. Do not "simplify" this back to -p"$mysqlPassword".
try
{
    $env:MYSQL_PWD = $mysqlPassword

    # Dump ace_shard to a temp file BEFORE touching ace_shard_loadtest at all. If the dump fails or comes
    # back empty/tiny, we bail out here with ace_shard_loadtest still intact. Only once we know we have a
    # good dump in hand do we drop and recreate the target database. Do not reorder this to dump-and-pipe
    # directly into a freshly-dropped database - that destroys the target before the dump is known-good.
    Write-Host "Dumping ace_shard to a temp file..."
    & $mysqldump -h $mysqlHost -P $mysqlPort -u $mysqlUser --routines --triggers --single-transaction --result-file=$dumpFile ace_shard
    if ($LASTEXITCODE -ne 0)
    {
        throw "mysqldump failed with exit code $LASTEXITCODE. ace_shard_loadtest has NOT been touched."
    }

    if (-not (Test-Path $dumpFile))
    {
        throw "mysqldump did not produce an output file at $dumpFile"
    }

    $dumpSize = (Get-Item $dumpFile).Length
    if ($dumpSize -lt 4096)
    {
        throw "Dump of ace_shard looks empty or truncated (only $dumpSize bytes) - refusing to proceed. ace_shard_loadtest has NOT been touched."
    }

    Write-Host "Dump OK ($dumpSize bytes). Recreating ace_shard_loadtest from the dump..."
    & $mysql -h $mysqlHost -P $mysqlPort -u $mysqlUser -e "DROP DATABASE IF EXISTS ace_shard_loadtest; CREATE DATABASE ace_shard_loadtest;"
    if ($LASTEXITCODE -ne 0) { throw "mysql failed (exit $LASTEXITCODE)" }
    # "source" reads the file inside the mysql client: no PowerShell pipeline re-encoding the dump. The client
    # wants forward slashes on both OSes.
    $dumpFileForMysql = $dumpFile -replace '\\', '/'
    & $mysql -h $mysqlHost -P $mysqlPort -u $mysqlUser ace_shard_loadtest -e "source $dumpFileForMysql"
    if ($LASTEXITCODE -ne 0) { throw "mysql failed (exit $LASTEXITCODE)" }

    Write-Host "Seeding fixed baseline data (landblock 0x7D64, 500 characters, 100k bulk rows)..."
    & $exe seed-landblock --landblock=0x7D64 --staticCount=200 --dynamicCount=300 --yes
    & $exe seed-characters --count=500 --yes
    & $exe seed-bulk --count=100000 --typeCount=5 --yes

    # The possession chain that `suite` reads back with possession-load --mode=read. Seeding it HERE, from this
    # separate process, is what makes that read genuinely cold: seeding it inside the suite process would populate
    # the in-memory biota cache and the read would be served from memory instead of the database. Do not "simplify"
    # this by moving it into the suite.
    Write-Host "Seeding the possession chain for possession-load (separate process, keeps the read cold)..."
    & $exe possession-load --mode=seed --yes

    Write-Host ""
    Write-Host "Baseline reset complete. Run 'suite --label=<name>' now to measure against it."
}
finally
{
    Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue
    if (Test-Path $dumpFile)
    {
        Remove-Item $dumpFile -ErrorAction SilentlyContinue
    }
}
