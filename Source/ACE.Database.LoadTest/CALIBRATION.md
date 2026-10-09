# Noise calibration for `compare`

## The gate is two-tier, and its timing floors are coarse on purpose

Read this before reading a 30% floor as sloppiness. It is a measured property of the machine, and the split below
is the design rather than a compromise forced on it.

**Tier 1, coarse timing floors.** Every timing metric is judged against a floor sized from measured
identical-build variance: 30% by default, wider where the accumulated profile says so. These essentially never
false-positive, and in exchange they only detect order-of-magnitude changes.

**Tier 2, exact deterministic counters.** Statement counts, row counts, round trips - judged at 0%, so any change
at all is flagged. This is where precision lives.

The reason the timing tier can afford to be coarse is what this gate actually has to catch. Taking the real
changes this workstream produced: wins of 8x, 14x and 6x, a statement count collapsing from 2,727 to 13, and an
accidentally-shipped regression of 11-19x. Every one of those clears a 30% floor by an order of magnitude. The
15% floor this tool started with was not buying detection - measured, it produced one to three false regressions
on every identical-build validation pair, four pairs running, with a different metric set each time. There is no
20% regression in this codebase's history for a tighter floor to have caught.

So the honest statement of sensitivity is: **this gate detects order-of-magnitude timing regressions and does not
detect 20% ones.** If you need precision on a specific path, the answer is a deterministic counter on that path,
not a tighter timing floor. An N+1 regression shows up in a statement count with literally zero noise, where
wall-clock would need a 30% move before anyone could tell.

A few metrics end up with floors well past 30% because their measured variance demands it - `biota-roundtrip.Save`
p95 at 90.3% observed, `Save` p50 at 45.1%, `equip-buff-write-burst.Buff save latency cold ms` p95 at 46.0%,
`login-under-delete-load.Login latency under delete load ms` p95 at 43.1%. Those are order-of-magnitude detectors
and nothing finer. Treat them accordingly, and prefer a counter if a specific path needs real coverage.

`compare` used to judge every metric against one flat percentage (`--threshold`, default 15%). That number was
never measured against anything. Two `suite` runs of **byte-identical code**, each after its own
`reset-baseline.ps1`, routinely disagree by far more than 15% on some metrics and by well under 1% on others, so a
single threshold is simultaneously too tight (false regressions on the noisy metrics) and too loose (real
regressions hidden on the quiet ones).

`calibrate` measures the real number, per metric, on the machine you are actually running on.

## Regenerating the profile

Run this from the harness's build output directory (the same place you run `suite`), with `Config.js` pointed at
the disposable `ace_shard_loadtest` database:

```
ACE.Database.LoadTest.exe calibrate --runs=6 --label=<name> --yes
```

That runs `reset-baseline.ps1` + the standard suite `--runs` times in a row against the code you have built right
now, then writes:

- `results/calibration.json` - the profile `compare` reads.
- `results/calibration-runs/<label>-runN.json` - the individual suite reports it was derived from. Keep these:
  they are the evidence, and they let you recompute the spread over a different subset later.

Each suite run is spawned as a **fresh child process**, exactly as a person would run it by hand, so the measured
noise includes process startup, JIT, connection-pool warm-up and InnoDB buffer state. An in-process loop would be
quieter than the thing it is supposed to model.

`compare` picks up `results/calibration.json` automatically when it exists, and prints which profile it used.
Pass `--calibration=<path>` for a different one, or `--calibration=none` to fall back to the flat `--threshold`.

## What is in it

For each metric statistic (`value` for scalars, `p50` and `p95` for series) the profile records the min, median
and max seen across the runs, and:

```
ObservedSpreadPercent = (max - min) / median * 100
```

`compare` turns that into the threshold it applies:

```
threshold = max(ObservedSpreadPercent * --calibrationSafety, --threshold)
```

`--calibrationSafety` defaults to 1.5 because K runs observe a *sample* of the noise, not its true range - the
observed spread is a lower bound. A metric with no entry in the profile falls back to the flat `--threshold`.

### Calibration only widens, by default

Note the `max(..., --threshold)`: a calibration entry can make a metric's band **wider** than `--threshold`, never
tighter. That default is what the calibration data itself said.

Measured here on 2026-07-29 over ten identical-build suite runs:
`character-list-scaling.GetCharacter (all calls)` p50 ranged 4.190-4.324ms across the six calibration runs - a
3.1% spread, which looks like a rock-solid metric that deserves a tight threshold. Across all ten runs of the
*same build* it ranged 4.100-4.680ms, a 13.8% spread, about 4.5x wider. Acting on the 6-run estimate produced a
`REGRESSIONS DETECTED` verdict on two identical builds - precisely the false positive this whole mechanism exists
to remove.

The observed range over K runs is a biased estimator of a distribution with occasional outliers, and it is biased
narrow, which is the dangerous direction. So calibration is used only to stop false regressions on noisy metrics,
which is the defect it was added for.

`--calibrationCanTighten` opts back in to letting a calibrated floor go below `--threshold` (clamped at
`--minCalibratedThreshold`, default 5%). Only turn it on with a K large enough that the widest per-metric spreads
have stopped growing between successive runs, and verify that against the retained per-run reports rather than
assuming it.

## Known weakness: calibration measures WITHIN-SITTING noise

`calibrate` runs its K runs back to back in a single invocation, on one machine state. That measures how much
consecutive runs disagree. It is not the same thing as how much a "before" run and an "after" run disagree in the
real workflow, where the two are separated by a code change, a rebuild, and minutes to hours of wall time.

The gap is large and it has been measured twice.

`save-batch-crossover`, all 12 metrics, six consecutive calibration runs against the true spread over ten runs
(the same six plus four validation runs taken later):

| metric | calibrated, 6 consecutive runs | true, 10 runs | ratio |
| --- | --- | --- | --- |
| `Warm burst ms N=1` | 3.4% | 17.4% | 5.2x |
| `Warm burst p50 ms N=100` | 2.8% | 14.3% | 5.1x |
| `Warm burst p50 ms N=12` | 3.2% | 15.8% | 4.9x |
| `Cold burst p50 ms N=12` | 4.6% | 21.2% | 4.6x |
| `Warm burst ms N=100` | 3.2% | 14.4% | 4.5x |
| `Cold burst p50 ms N=1` | 10.2% | 35.1% | 3.4x |
| `Cold burst ms N=12` | 8.8% | 29.9% | 3.4x |
| `Cold burst ms N=1` | 11.1% | 33.7% | 3.0x |
| `Warm burst ms N=12` | 6.6% | 17.0% | 2.6x |
| `Warm burst p50 ms N=1` | 4.2% | 10.5% | 2.5x |
| `Cold burst ms N=100` | 15.5% | 24.0% | 1.5x |
| `Cold burst p50 ms N=100` | 18.5% | 18.4% | 1.0x |

Median ratio about 3.2x. Independently, `character-list-scaling.GetCharacter` p50 measured 3.1% over six
consecutive runs and 13.8% over ten - 4.5x.

So `--calibrationSafety=1.5` is not enough on this evidence, and the "calibration only widens" rule is doing more
of the work than the safety factor is.

### It is NOT a first-run penalty, and that was tested

The obvious explanation is that the real workflow's "after" run happens immediately after a rebuild, so it is a
cold first run, while `calibrate` takes K runs back to back with binaries, page cache and buffer pool warm. If
that were the mechanism, the fix would be to insert a cold-start step (a rebuild, measured at 4.1s) before each
calibration run, and the profile would then capture the penalty.

The run-position data refutes it. Across three sittings, comparing every run against run 1 on the median
statistic:

| sitting | runs 2-6 relative to run 1 | run 1 was |
| --- | --- | --- |
| cal2 | -0.3% to -3.2% | slowest |
| cal3 | -4.4% to -10.8% | slowest, by a lot |
| cal5 | +1.4% to +2.5% | **fastest** |

There is no consistent position effect to reproduce. And cal5 - the sitting whose floors are currently too tight -
has no first-run penalty at all, so no cold-start emulation could have widened it.

What cal5 does show, in chronological order (each run about a minute apart):

| run | t+ | median delta vs run 1 | `Cold burst ms N=1` |
| --- | --- | --- | --- |
| cal5-run1 | 0.0min | 0.0% | 4.26 |
| cal5-run2 | 1.0min | +2.5% | 4.25 |
| cal5-run3 | 1.9min | +2.1% | 4.32 |
| cal5-run4 | 2.9min | +2.2% | 4.15 |
| cal5-run5 | 3.9min | +1.4% | 4.32 |
| cal5-run6 | 4.8min | +2.1% | 4.07 |
| validation 1A | 5.8min | +2.1% | 4.19 |
| validation 1B | 6.8min | +3.5% | 4.41 |
| validation 2A | 7.8min | +5.3% | 4.47 |
| validation 2B | 8.7min | +5.7% | **5.34** |

Flat for the first seven runs, then climbing. The whole calibration window is 4.8 minutes; the reading that breaks
the threshold appears at 8.7 minutes. The bias is not a cold start, it is **machine-state drift on a timescale
longer than the calibration window**, and it is uncorrelated with run position.

That also bounds what a cold-start step could achieve: cal5's six runs spanned 4.07-4.32, a 5.8% range, and the
acceptance target is 29.4%. Adding 4.1s of rebuild per run extends the window from 4.8 to about 5.2 minutes. It
cannot manufacture a 5.34 reading that only appeared at 8.7 minutes, so it cannot close a 5x gap.

The estimator is measuring a slowly-varying process over too short a window. The only fix that makes
`ObservedSpreadPercent` mean what it claims is to sample across a window comparable to the gap between a real
"before" and "after" - separated sittings, or many more runs - and both cost far more than a `calibrate`
invocation is worth.

### What was actually done about it

Neither a multiplier nor a cold-start step. The estimator now samples a wider window by **accumulating runs across
sittings**: each `calibrate` invocation writes its runs to `results/calibration-runs/<label>/`, nothing is
overwritten, and the profile is built from every accumulated run under that root. `calibrate --aggregate` rebuilds
the profile from what is already there without running anything.

That directly attacks the measured cause. A single sitting samples about five minutes; the profile now spans every
sitting ever recorded on this machine, so drift that only shows up over tens of minutes is inside the sample. The
first profile built this way used 14 runs across 2 sittings and produced floors of 90.3% for
`biota-roundtrip.Save` p95 and 40.3% for `save-batch-crossover.Cold burst ms N=12`, against 39.2% and 6.7% from a
single sitting.

Two consequences to know about:

- Runs taken under different scenario arguments are not comparable. Changing a scenario's suite arguments means
  clearing `results/calibration-runs/` before recalibrating, not adding to it.
- `--calibrationSafety` is still 1.5 and is now applied on top of an already-wide multi-sitting spread, so it
  somewhat double-counts. It has deliberately been left alone rather than retuned in the same change that altered
  what it multiplies; the effect is conservative (wider floors, fewer false positives), which is the direction
  this gate wants.

### If you ever do need a multiplier: the derivation

Given that, the defensible fallback is an explicit multiplier that is honest about being a fudge. Its derivation,
from four independent calibrations measured against the true spread over all runs available at the time:

| calibration | metric | within-sitting | true | ratio |
| --- | --- | --- | --- | --- |
| cal2 | `character-list-scaling.GetCharacter` p50 | 3.1% | 13.8% | 4.5x |
| cal2 | `save-batch-crossover`, 12 metrics | 2.8-18.5% | 14.3-35.1% | 1.0x-5.2x, median 3.2x |
| cal5 | `save-batch-crossover.Cold burst ms N=1` | 5.8% | 29.4% | 5.1x |
| cal5 | `save-batch-crossover.Cold burst p50 ms N=1` | 7.1% | 31.6% | 4.5x |
| cal5 | `queue-saturation.Queue wait time` p50 | 18.6% | 25.7% | 1.4x |

The ratios cluster between about 3x and 5x, with the narrowest metrics understated worst - which follows, since a
metric with little within-sitting jitter has nothing but the drift left to surprise it.

`--calibrationSafety` has deliberately NOT been changed from 1.5. Picking a number here would mean picking it
against the run that happens to be failing today, and the whole point of this tool is to stop that. The choice of
multiplier is a judgement about how coarse the gate should be, and it belongs to whoever owns the gate, not to the
measurement.

Until it is set, treat a single flagged metric as a prompt to re-run the pair rather than a verdict, and note that
a pair run late in a long session is more likely to flag than one run early.

## Deterministic metrics

Some numbers are not measurements, they are discrete repeatable facts: a SQL statement count, a row count, a
number of round trips. For those, `n` says nothing about reliability and any threshold is too loose - if the
number changed, something changed.

`Metrics.Record(..., deterministic: true)` marks one. `compare` then judges it at a 0% threshold, exempt from the
sample gate, the millisecond floor and calibration entirely.

The case it was added for: `possession-load`'s `Possession read SQL statements` read **exactly 68 on all six**
identical-build calibration runs, a 0.0% observed spread. It is the sharpest detector there is of an N+1
regression in the batched possession read - a change that broke the batching would push it from 68 into the
hundreds - and yet at `--samples=1` the minimum-sample gate refused to judge it, so the whole scenario gated
nothing. With the flag, that regression is caught on the first comparison.

Two guards, both deliberate. It is never inferred, because wrongly marking a noisy metric deterministic turns
ordinary jitter into a permanent false regression. And it requires BOTH reports to declare it, so a report
written before the field existed cannot have determinism assumed on its behalf.

### When a metric qualifies

Both conditions, not either:

1. **It must be a count or an invariant, never a timing.** SQL statements issued, rows returned, round trips made,
   items loaded. A duration never qualifies, however stable it looks - a timing is a sample from a distribution
   and the machine decides its value, so the right tool for a stable timing is a tight calibrated floor, not a 0%
   one. `Possession read ms` sits directly next to `Possession read SQL statements` in the same scenario and does
   NOT qualify, which is the clearest illustration of the line: same read, same code path, one is a fact about
   what the code did and the other is a measurement of how long the machine took to do it.
2. **It must be observed stable across the whole calibration set before being declared.** Run `calibrate` first
   and confirm the metric's `ObservedSpreadPercent` is exactly 0.0 over all K runs. If it is 0.3%, it is not
   deterministic, it is quiet - and a 0% threshold will flag it on the first run that disagrees.

If either condition fails, leave it as an ordinary metric. A wrongly-declared deterministic metric fails on every
comparison forever, which trains people to ignore the tool - a worse outcome than a metric that is merely loose.

## Discarded warm-up

The first few iterations of a measured loop are systematically slower than the rest - JIT of the path under test,
EF model and query-plan construction, connection-pool growth. That does not just add noise, it biases small-N
measurements upward and does so inconsistently between runs.

`MeasuredLoop.Run(iterations, warmup, measureOne)` runs `warmup` extra passes and discards their timings. It
hands `measureOne` the absolute pass index, so a scenario that needs distinct ids per pass can derive them
without a warm-up pass ever handing a cached id to a measured one - and a scenario adopting it must budget id
space for `MeasuredLoop.TotalPasses(iterations, warmup)`, not just `iterations`.

Only `save-batch-crossover` uses it so far, at `--warmup=3`. That is deliberate: adopting it in a scenario
changes that scenario's numbers and invalidates its calibration entries, so each adoption carries a
re-calibration. Do not roll it out broadly without one.

## Whole-run drift

Per-metric noise floors assume metrics drift independently. They do not always. A whole suite run can land on a
machine that is uniformly busier, and then many unrelated metrics cross their thresholds at once.

Measured here: across eight identical-build runs, seven clustered within 3.2% of each other on the median
statistic and one came in 12.8% slower, with 34 of 40 statistics moving the same way. Compared against its
neighbour, that single run produced five "regressions".

`compare` now prints a `Whole-run drift:` line on every comparison, and adds a NOTE when the movement is lopsided
enough to look like a whole-run effect (by default at least 75% of statistics moving one way AND a median move of
at least 5%). It is a diagnostic only. It never suppresses a verdict, because a genuine across-the-board
regression looks exactly the same, and hiding that would be far worse than the false positive it explains.

## This profile is machine specific, and it goes stale

Disk, CPU, MySQL version, other processes, power plan - all of it moves these numbers. A profile is only valid on
the machine that produced it. `compare` prints a warning if the profile's recorded machine name does not match the
current one, but it cannot detect the machine changing underneath it.

Regenerate it:

- after any hardware, MySQL or OS change on the benchmark machine;
- after changing a scenario's suite arguments, since sample counts change the noise floor;
- after adding or removing a metric;
- whenever the flat-threshold verdicts and the calibrated verdicts start disagreeing in ways you cannot explain.

## Choosing `--runs`

More runs is a strictly better estimate and a linearly longer wait. The useful question is where the observed
spread stops growing: recompute the spread over the first 3, 4, 5 ... of the retained per-run reports and pick the
smallest `--runs` past the point where it flattens for the metrics the gate can actually judge.

`--runs=6` was chosen here from that curve. On this machine a full reset + suite cycle is about 50 seconds, so six
runs cost roughly six minutes. Between the 5th and 6th run the observed spread moved by under ~1 point for every
judgeable metric (`Throughput saves per sec` 21.6% -> 21.1%, `Deletes per sec` 21.3% -> 20.5%, `Wall time ms`
22.1% -> 22.6%, `Queue wait time` p95 26.2% -> 25.5%, `Warm reload` p95 19.4% -> 19.4%). Three runs would have
been badly wrong: `queue-saturation.Execution time` p95 read 30.4% at K=3 against 45.6% at K=6, and
`landblock-population.Warm reload` p50 read 0.9% at K=3 against 18.5% at K=6.

The metrics whose spread was still climbing at K=6 are all ones the gate already excludes for other reasons -
`Login latency idle ms` p95 (n=5, so its "p95" is the max), `Login latency cold first read ms` (n=1), and the
sub-millisecond cached-read percentiles that the `--minMs` floor covers. Extra runs would only have refined
numbers nothing acts on.

## What it does not fix

A noise floor makes a noisy metric honest, not useful. A metric whose identical-build spread is so wide that no
threshold below it could ever catch a real regression is not being rescued by calibration - it is being labelled.
Those metrics need more samples (a scenario-side change) or removal.

This was measured before `login-under-delete-load` was raised to `--samples=30`. At its former `--samples=5` none
of its latency metrics could discriminate anything, which left the login/possession batch-read path with no
working coverage at all:

| metric | n | identical-build spread over 6 runs |
| --- | --- | --- |
| `Login latency idle ms` p95 | 5 | 504% |
| `Login latency cold first read ms` | 1 | 128% |
| `Login latency under delete load ms` p95 | 5 | 62% |
| `Login latency under delete load ms` p50 | 5 | 36% |
| `Login latency idle ms` p50 | 5 | 29% |

`--samples=30` fixes five of those six. `Login latency cold first read ms` is n=1 by construction and no argument
changes it, so it is now recorded as `Informational`: printed, never gated.

## What this suite does NOT cover

Read this before assuming a green `compare` means a change is safe.

**`ACE.Server` is never loaded.** This harness links `ACE.Common`, `ACE.Database` and `ACE.Entity` only. Anything
that lives in the server - `Player_Inventory.DeepSave`, landblock ticking, the world-object layer - is invisible
to it. `DeepSave` in particular collects dirty biotas and hands them to `SaveBiotasInParallel`, and this suite
measures what happens *below* that call, not the batching decision `DeepSave` itself makes. If `DeepSave`
regressed to saving one item per call, every metric here would stay flat. That is a structural limit of a
database-layer harness, not a gap to be papered over: the coverage for `DeepSave` is `ACE.Server.Tests` plus
in-game verification.

**`SaveBiotasInParallel` is only correctness-gated.** `integrity-check` exercises it, but records pass/fail rather
than timing. `save-batch-crossover` is the scenario that times it.

**A scenario in the suite must earn its wall-clock in judgeable statistics.** Two scenarios on master are
deliberately excluded, both measured rather than assumed:

- `deepsave-burst` runs 74.7s at its defaults, three times the rest of the suite, and reports five metrics for
  each of 28 size/arm/cache combinations at `--samples=3` - almost none of it over the sample gate. It is a
  crossover-finding diagnostic, like `retry-cost`.
- `account-character-list` covers `GetCharacters(accountId)`, which nothing else here calls and which is NOT
  `character-list-scaling`'s `GetCharacter(characterId)`. But against this baseline's fixture, `seed-characters`
  gives each of its 500 accounts a single character, so the call returns one row: p50 lands at 0.485ms, under the
  `--minMs` floor and therefore never flagged, and its p95 has a 223.8% identical-build spread. Worth adding only
  with a fixture that puts many characters on one account.

**`biota-roundtrip` does not measure the database read path at all.** Verified two ways on 2026-07-29.

By source: `ShardDatabaseWithCaching.GetBiota(context, id, doNotAddToCache)` consults the biota cache and returns
a hit *before* it ever looks at `doNotAddToCache`; that flag only suppresses *populating* the cache, it does not
bypass it. `SaveBiota` populates the cache on success. `biota-roundtrip` saves an id and then immediately "cold"
reads the same id, so it hits its own cache entry every time.

By probe, 50 iterations each:

| arm | what it reads | p50 |
| --- | --- | --- |
| ids this process never touched (seeded by a prior process) | a genuine database read | 1.3ms |
| save-then-read, exactly `biota-roundtrip`'s pattern | a cache hit | under 0.05ms |
| documented warm read of the same ids | a cache hit | under 0.05ms |

The second and third arms are indistinguishable; the first is 30x slower. So `biota-roundtrip` measures the
cache-hit path twice and the database read path zero times.

Those two metrics have been renamed to `Cached read after save ms` and `Cached read after priming ms` and marked
`Informational`, so they print but cannot gate. The names now describe what is measured. Renaming resets their
history: they show as MISSING against any report taken before 2026-07-29, which is correct. **The single-biota
database read path still has no performance coverage** - giving it some means either saving with
`doNotAddToCache` or reading from another process, both of which change what the scenario measures, and neither
has been done.

### The same trap made a correctness gate tautological

`integrity-check`'s phase 1 had the identical save-then-read shape, and it was not merely imprecise - it could
not fail. Probed directly: save a biota, corrupt its persisted `Name` row through a fresh `ShardDbContext`, then
read it back the way phase 1 did. The read returned the ORIGINAL value and the phase passed.

Phase by phase, as of this change:

| phase | asserts against | status |
| --- | --- | --- |
| 1, save/read round trip | `GetBiota` from a genuine cache miss | **fixed** - was tautological |
| 2, batched delete cascade | `CountRowsFor`, direct `ShardDbContext` | sound, always was |
| 3, batched update round trip | `CompareAgainstDatabase`, direct `ShardDbContext` | sound, always was |

Only phase 1 was affected. Phases 2 and 3 already read persisted rows directly, and phase 3's doc comment had
already recorded this exact trap - the knowledge existed in the file and phase 1 had simply never been revisited.

Phase 1 now saves with `doNotAddToCache` so the read-back is a real cache miss through `GetBiota`, which is what
it always claimed to test, and it asserts that the id is absent from the biota cache before reading. That
self-guard was verified by flipping the save back to cache-populating: the phase fails with
`0x... is in the biota cache before the read-back`. A gate that cannot be made to fail is not a gate, so this one
was made to fail on purpose before being trusted.

The related guard is the sample-count gate: `compare` refuses to flag any statistic backed by fewer than
`--minSamples` (default 10) observations, and refuses to flag a `p95` backed by fewer than `--minSamplesForP95`
(default 20), because `Metrics.RecordSeries` computes a percentile as `sorted[ceil(0.95*n)-1]`, which is the
single largest sample for every n <= 19. Those metrics are still printed, marked
`insufficient samples to judge (n=N)` - they are blind spots, and a blind spot you can see is not a pass.
