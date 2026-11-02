using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// World Events Hold H6: the pure rules behind the run's own cadence and every line it says (ported from DreamWeave #17's
    /// WorldEventHoldRules and WorldEventAnnouncer). No clock, no world, no property reads. Every player-facing string here is
    /// ASCII and keeps the contributor's wording.
    /// </summary>
    public static partial class WorldEventHoldRules
    {
        // ---- picking and filling lines ----------------------------------------------------------------------------------

        /// <summary>One line from <paramref name="pool"/> at random, skipping blanks; null when there is none.</summary>
        public static string PickLine(IReadOnlyList<string> pool, Random rng)
        {
            if (pool == null || pool.Count == 0)
                return null;

            var usable = pool.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (usable.Count == 0)
                return null;

            rng ??= new Random();

            return usable[rng.Next(usable.Count)];
        }

        /// <summary>Fills a special wave line's {side}, {champion} and {target} tokens; the rest are left for FillLine.</summary>
        public static string FillWaveTokens(string template, string side, string champion, string target)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            return template
                .Replace("{target}", string.IsNullOrWhiteSpace(target) ? "one of us" : target)
                .Replace("{side}", string.IsNullOrWhiteSpace(side) ? "nearest" : side)
                .Replace("{champion}", string.IsNullOrWhiteSpace(champion) ? "Their champion" : champion);
        }

        /// <summary>The call-out when an assault monster is sent: {monster} and {target} filled; the default when the pool is empty.</summary>
        public static string AssaultLine(string template, string monster, string target)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "A {monster} marches on {target}!" : template;

            return line.Replace("{monster}", string.IsNullOrWhiteSpace(monster) ? "corrupted foe" : monster.Trim())
                       .Replace("{target}", string.IsNullOrWhiteSpace(target) ? "the envoys" : target.Trim());
        }

        /// <summary>The label a phase broadcast starts with: "Final wave" for the finale, else "Wave 4 of 7".</summary>
        public static string PhaseLabel(int waveNumber, int targetWaves, bool isFinale)
        {
            return isFinale ? "Final wave" : WaveProgress(waveNumber, targetWaves);
        }

        /// <summary>
        /// Every standing knight's health, for the {targetHealth} progress token: "Envoy Draithon 80%, Envoy Donovan 64%".
        /// Empty when none stand.
        /// </summary>
        public static string TargetHealthSummary(IEnumerable<(string Name, uint Current, uint Max)> targets)
        {
            if (targets == null)
                return "";

            return string.Join(", ", targets.Where(t => t.Current > 0)
                .Select(t => $"{(string.IsNullOrWhiteSpace(t.Name) ? "?" : t.Name)} {HealthPercent(t.Current, t.Max)}%"));
        }

        /// <summary>
        /// A Hold outcome line's count tokens: {alive}, {total} and {lost} as they stood when the run finished, so the success
        /// and fail broadcasts can say "2 of 3 envoys survived".
        /// </summary>
        public static string FillCounts(string template, int alive, int total)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            return template
                .Replace("{alive}", alive.ToString())
                .Replace("{total}", total.ToString())
                .Replace("{lost}", Math.Max(0, total - alive).ToString());
        }

        /// <summary>The Hold success line a goal with no successTemplate gets. ASCII only.</summary>
        public const string DefaultSuccessTemplate = "{goal} at {anchor} succeeded! {alive} of {total} {nounPlural} survived.";

        /// <summary>
        /// A Hold run's outcome template with the final counts filled in: the goal's template, or
        /// <see cref="DefaultSuccessTemplate"/> for a success with none; {family}/{Family} too. A failure with no template
        /// stays null (the announcer's own fixed line).
        /// </summary>
        public static string OutcomeTemplate(string template, bool success, int alive, int total, string family)
        {
            if (success && string.IsNullOrWhiteSpace(template))
                template = DefaultSuccessTemplate;

            if (string.IsNullOrEmpty(template))
                return template;

            var familyText = string.IsNullOrWhiteSpace(family) ? DefaultFamilyText : family.Trim();

            return FillCounts(template, alive, total)
                .Replace("{Family}", CapitalizeFirst(familyText))
                .Replace("{family}", familyText);
        }

        /// <summary>
        /// A PlayScript by name (case-insensitive), or none: "none", null and blank read as <see cref="PlayScript.Invalid"/>
        /// with true. False for anything that is not a PlayScript.
        /// </summary>
        public static bool TryParseEffect(string name, out PlayScript script)
        {
            script = PlayScript.Invalid;

            if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                return true;

            var trimmed = name.Trim();

            if (!trimmed.All(char.IsLetterOrDigit) || !Enum.TryParse(trimmed, true, out script))
                return false;

            return Enum.IsDefined(typeof(PlayScript), script) && script != PlayScript.Invalid;
        }

        // ---- the world lines ---------------------------------------------------------------------------------------------

        /// <summary>A knight crossed a health milestone: "[World Event] An envoy at Holtburg is at 50% health!".</summary>
        public static string KnightHealthMilestoneLine(string noun, string anchorName, int pct)
        {
            var trimmed = (string.IsNullOrWhiteSpace(noun) ? ProtectDef.DefaultNoun : noun).Trim();

            // "An envoy", "A ward": the article follows the noun's first letter.
            var article = trimmed.Length > 0 && "aeiou".IndexOf(char.ToLowerInvariant(trimmed[0])) >= 0 ? "An" : "A";

            return WorldEventAnnouncer.Global($"{article} {trimmed} at {anchorName} is at {pct}% health!");
        }

        /// <summary>The world copy of a knight's spoken line: "[World Event] Envoy Draithon: "We made it!"".</summary>
        public static string KnightSpeechLine(string speakerName, string line)
        {
            var name = string.IsNullOrWhiteSpace(speakerName) ? CapitalizeFirst(ProtectDef.DefaultNoun) : speakerName;

            return WorldEventAnnouncer.Global($"{name}: \"{line}\"");
        }

        /// <summary>A Hold phase (a special wave or the finale): "[World Event] Wave 4 of 7: They are massing on the east flank!".</summary>
        public static string PhaseLine(string label, string announce)
        {
            return WorldEventAnnouncer.Global($"{(string.IsNullOrWhiteSpace(label) ? "Next wave" : label)}: {announce ?? ""}");
        }

        /// <summary>The finale boss enrages: "[World Event] Warlord Grub flies into a rage!".</summary>
        public static string EnrageLine(string bossName) =>
            WorldEventAnnouncer.Global($"{(string.IsNullOrWhiteSpace(bossName) ? "The champion" : bossName)} flies into a rage!");

        /// <summary>
        /// What a player who has not taken part in the run is told at a shrine (H6: the shrine is the defenders' own). New
        /// wording - the contributor's shrine had no such gate.
        /// </summary>
        public const string ShrineNotParticipantLine = "The shrine answers only those who stand in the defense.";

        // ---- the cadence ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// The Hold wave cadence: the next wave is due once <paramref name="interval"/> seconds have passed since the last, or
        /// earlier once no more than <paramref name="aliveShare"/> of the last wave's size is still alive. Never sooner than
        /// <paramref name="minGap"/> after the last wave. With <paramref name="requireBoth"/>, BOTH the interval and most of
        /// the field being dead are needed (whichever comes later), and <paramref name="maxInterval"/> (0 = none) sends it
        /// anyway so a run where nobody kills never stalls.
        /// </summary>
        public static bool HoldNextWaveDue(double now, double lastWaveAt, double interval, double minGap,
            int liveCount, int lastWaveSize, double aliveShare, bool requireBoth, double maxInterval)
        {
            var since = now - lastWaveAt;

            if (since < Math.Max(0, minGap))
                return false;

            var mostDead = lastWaveSize <= 0 ? liveCount <= 0 : liveCount <= Math.Floor(Math.Max(0, aliveShare) * lastWaveSize);

            if (!requireBoth)
                return (interval > 0 && since >= interval) || mostDead;

            if (maxInterval > 0 && since >= maxInterval)
                return true;

            var intervalDone = !(interval > 0) || since >= interval;

            return intervalDone && mostDead;
        }

        /// <summary>
        /// How many started waves count as resolved: what the spawner reported, or every started wave once
        /// <paramref name="graceSeconds"/> have passed since the last one started (a batch whose landblock never ran it cannot
        /// hold a won run open). While the finale's escort has not been sent, the finale is not resolved.
        /// </summary>
        public static int ResolvedWaves(int started, int spawnerResolved, double now, double lastStartedAt, double graceSeconds,
            bool finaleEscortPending)
        {
            int resolved;

            if (spawnerResolved >= started)
                resolved = started;
            else if (lastStartedAt > 0 && now - lastStartedAt >= graceSeconds)
                resolved = started;
            else
                resolved = Math.Max(0, spawnerResolved);

            return finaleEscortPending ? Math.Min(resolved, Math.Max(0, started - 1)) : resolved;
        }

        /// <summary>
        /// Whether the first wave may go out: once the knights' batch has resolved on its landblock, or after
        /// <paramref name="graceSeconds"/> regardless, so a batch that never reports back cannot hold the run without waves.
        /// </summary>
        public static bool FirstWaveMayStart(bool placementResolved, double secondsSinceActive, double graceSeconds)
        {
            return placementResolved || secondsSinceActive >= graceSeconds;
        }

        /// <summary>
        /// Whether the counted waves still want another one: the finale has not started yet. Once every counted wave has
        /// started, the field is left to clear and the hold is won when its last monster dies.
        /// </summary>
        public static bool WantsAnotherWave(int started, int targetWaves) => targetWaves > 0 && started < targetWaves;

        /// <summary>How many more creatures the final counted wave gets for a wave of <paramref name="baseSize"/>: <paramref name="share"/> more, at least one (0 = none).</summary>
        public static int FinalWaveBonus(int baseSize, double share)
        {
            if (!(share > 0) || !double.IsFinite(share))
                return 0;

            return Math.Max(1, (int)Math.Ceiling(Math.Max(0, baseSize) * share));
        }

        // ---- the run ticks --------------------------------------------------------------------------------------------------

        /// <summary>
        /// A knight's current health after its max moves from <paramref name="oldMax"/> to <paramref name="newMax"/>: the same
        /// percentage, rounded, never 0 for a knight that was alive, never above the new max.
        /// </summary>
        public static uint RescaledHealth(uint current, uint oldMax, uint newMax)
        {
            if (newMax == 0)
                return 0;

            if (oldMax == 0)
                return newMax;

            var scaled = (uint)Math.Round((double)current / oldMax * newMax, MidpointRounding.AwayFromZero);

            if (current > 0 && scaled == 0)
                scaled = 1;

            return Math.Min(newMax, scaled);
        }

        /// <summary>Whether a knight is clear enough to regenerate: no living wave monster within <paramref name="radius"/> metres (null = none alive).</summary>
        public static bool IsClear(double? nearestMonster, double radius)
        {
            return nearestMonster == null || !(radius > 0) || nearestMonster.Value > radius;
        }

        /// <summary>Whether a kill counts as a rescue: a player's kill, or a pet's (it has an owner). Never a monster's.</summary>
        public static bool IsRescueKill(bool killerIsPlayer, bool killerHasPetOwner) => killerIsPlayer || killerHasPetOwner;

        /// <summary>
        /// Whether a ready shrine plays its ready effect now: ready (<paramref name="now"/> past <paramref name="readyAt"/>, 0 =
        /// never used), and either it has not pulsed since it became ready or the last pulse is <paramref name="period"/>
        /// seconds old. A period of 0 means no repeat: once per time it becomes ready.
        /// </summary>
        public static bool ShrineReadyPulseDue(double now, double readyAt, double pulsedAt, double period)
        {
            if (now < readyAt)
                return false;

            if (!(period > 0))
                return pulsedAt <= 0 || pulsedAt < readyAt;

            return pulsedAt < readyAt || pulsedAt <= 0 || now - pulsedAt >= period;
        }

        /// <summary>
        /// The milestones due on this sample: every tier of <paramref name="tiers"/> crossed (health at or below that percent
        /// of max) and not yet in <paramref name="fired"/>. MUTATES <paramref name="fired"/>, so a tier is said once per knight
        /// even when a heal or a rescale lifts it back above.
        /// </summary>
        public static List<int> DueMilestones(uint current, uint max, HashSet<int> fired, IReadOnlyList<int> tiers)
        {
            var due = new List<int>();

            if (max == 0 || fired == null || tiers == null)
                return due;

            foreach (var pct in tiers)
            {
                if (fired.Contains(pct) || current * 100.0 > (double)max * pct)
                    continue;

                fired.Add(pct);
                due.Add(pct);
            }

            return due;
        }
    }
}
