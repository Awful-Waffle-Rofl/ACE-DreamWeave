using System;
using System.Diagnostics.Metrics;
using System.Linq;

namespace ACE.Server.Frays
{
    /// <summary>
    /// Frays numbers for the "ACE Frays" Grafana dashboard (deploy/monitoring). Same meter name as
    /// ServerMetrics ("ACE.Server"), so the dotnet-monitor sidecar collects these with no config change and
    /// they export as aceserver_ace_frays_*.
    ///
    /// The ServerMetrics rules apply here too:
    ///  - UNTAGGED, one instrument per thing: dotnet-monitor keys its store by instrument name alone, so tag
    ///    sets overwrite each other.
    ///  - Counters export as "amount in the last 5 s" gauges, not running totals. Query them with
    ///    avg_over_time(x[5m]) / 5 (per second), never rate().
    ///  - Each histogram counts against the sidecar's MaxHistograms; this file adds two.
    ///
    /// Recording is cheap (a Counter.Add on a disabled instrument is one Enabled check) and thread-safe, which
    /// matters because fire ticks and kills run on landblock threads.
    /// </summary>
    public static class FraysMetrics
    {
        private static readonly Meter Meter = new Meter(ACE.Server.Managers.ServerMetrics.MeterName);

        // ---- camps --------------------------------------------------------------------------------------------

        public static readonly Counter<long> FiresLit =
            Meter.CreateCounter<long>("ace.frays.fires.lit", "fires", "Frays: campfires lit");

        public static readonly Counter<long> CampsFinished =
            Meter.CreateCounter<long>("ace.frays.camps.finished", "camps", "Frays: per-player camps that ended (fire out or player away too long)");

        private static readonly Histogram<double> CampMinutes =
            Meter.CreateHistogram<double>("ace.frays.camp.duration", "min", "Frays: length of a finished per-player camp");

        private static readonly Histogram<double> CampPeakHeat =
            Meter.CreateHistogram<double>("ace.frays.camp.peak_heat", "heat", "Frays: peak Heat a finished per-player camp reached");

        // ---- rewards ------------------------------------------------------------------------------------------

        /// <summary>Static XP paid by camps (the per-minute payout). Already inside ace.xp.granted too.</summary>
        public static readonly Counter<long> XpGranted =
            Meter.CreateCounter<long>("ace.frays.xp.granted", "xp", "Frays: static XP paid to campers");

        public static readonly Counter<long> LumGranted =
            Meter.CreateCounter<long>("ace.frays.lum.granted", "lum", "Frays: static Luminance paid to campers");

        public static readonly Counter<double> EmbersKill =
            Meter.CreateCounter<double>("ace.frays.embers.kill", "embers", "Frays: Embers from kills, after the weekly cap");

        public static readonly Counter<double> EmbersCamp =
            Meter.CreateCounter<double>("ace.frays.embers.camp", "embers", "Frays: Embers for standing by a lit fire, after the weekly cap");

        public static readonly Counter<double> EmbersTask =
            Meter.CreateCounter<double>("ace.frays.embers.task", "embers", "Frays: Embers from daily/weekly tasks and hunts (not weekly-capped)");

        public static readonly Counter<double> EmbersVault =
            Meter.CreateCounter<double>("ace.frays.embers.vault", "embers", "Frays: Embers put in Ember Vaults as they open (not weekly-capped)");

        /// <summary>Embers the weekly soft cap took away (asked for minus paid).</summary>
        public static readonly Counter<double> EmbersCapLost =
            Meter.CreateCounter<double>("ace.frays.embers.cap_lost", "embers", "Frays: Embers removed by the weekly soft cap");

        public static readonly Counter<long> TasksCompleted =
            Meter.CreateCounter<long>("ace.frays.tasks.completed", "tasks", "Frays: daily/weekly tasks and hunts completed");

        // ---- combat -------------------------------------------------------------------------------------------

        public static readonly Counter<long> Kills =
            Meter.CreateCounter<long>("ace.frays.kills", "creatures", "Frays: fire creatures killed while the fire burned");

        public static readonly Counter<double> WorkRemoved =
            Meter.CreateCounter<double>("ace.frays.work", "standards", "Frays: health removed by campers, in standard creatures' worth");

        public static readonly Counter<long> Deaths =
            Meter.CreateCounter<long>("ace.frays.deaths", "deaths", "Frays: camper deaths at a lit fire");

        public static readonly Counter<long> SnarlsKilled =
            Meter.CreateCounter<long>("ace.frays.snarls.killed", "snarls", "Frays: Snarl bosses killed");

        public static readonly Counter<long> BoonsTaken =
            Meter.CreateCounter<long>("ace.frays.boons.taken", "boons", "Frays: Ember Boons picked");

        // ---- loot ---------------------------------------------------------------------------------------------

        public static readonly Counter<long> CachesKeyBar =
            Meter.CreateCounter<long>("ace.frays.caches.key_bar", "keys", "Frays: Vault Keys from a full key bar");

        public static readonly Counter<long> CachesSnarl =
            Meter.CreateCounter<long>("ace.frays.caches.snarl", "caches", "Frays: Snarl caches");

        public static readonly Counter<long> LootItems =
            Meter.CreateCounter<long>("ace.frays.loot.items", "items", "Frays: items rolled onto fire-creature corpses");

        // ---- recording helpers --------------------------------------------------------------------------------

        public static void RecordCampFinished(double minutes, int peakHeat)
        {
            CampsFinished.Add(1);

            if (minutes > 0)
                CampMinutes.Record(minutes);

            CampPeakHeat.Record(peakHeat);
        }

        public static void AddPositive(Counter<double> counter, double value)
        {
            if (value > 0)
                counter.Add(value);
        }

        public static void AddPositive(Counter<long> counter, long value)
        {
            if (value > 0)
                counter.Add(value);
        }

        // ---- gauges -------------------------------------------------------------------------------------------

        private static bool initialized;

        /// <summary>
        /// Registers the live gauges. Call once at startup (ServerMetrics.Initialize does). The callbacks run on
        /// the metrics scrape thread, so they only read each fire through its own lock and never throw.
        /// </summary>
        public static void Initialize()
        {
            if (initialized)
                return;

            initialized = true;

            Meter.CreateObservableGauge("ace.frays.fires.burning", () => Safe(() => FraysManager.Fires.Count(f => f.Lit)),
                "fires", "Frays: campfires lit right now");

            Meter.CreateObservableGauge("ace.frays.campers.present", () => Safe(() => FraysManager.Fires.Where(f => f.Lit).Sum(f => f.PresentCount)),
                "players", "Frays: campers present at lit fires right now");

            Meter.CreateObservableGauge("ace.frays.heat.average", () => SafeDouble(() =>
                {
                    long sum = 0, count = 0;

                    foreach (var f in FraysManager.Fires)
                    {
                        var (n, heatSum, _) = f.HeatSnapshot();
                        count += n;
                        sum += heatSum;
                    }

                    return count == 0 ? 0 : (double)sum / count;
                }),
                "heat", "Frays: average Heat across active campers right now");

            Meter.CreateObservableGauge("ace.frays.heat.max", () => Safe(() => FraysManager.Fires.Select(f => f.HeatSnapshot().Max).DefaultIfEmpty(0).Max()),
                "heat", "Frays: highest Heat any active camper has right now");
        }

        private static int Safe(Func<int> read)
        {
            try { return read(); }
            catch { return 0; }
        }

        private static double SafeDouble(Func<double> read)
        {
            try { return read(); }
            catch { return 0; }
        }
    }
}
