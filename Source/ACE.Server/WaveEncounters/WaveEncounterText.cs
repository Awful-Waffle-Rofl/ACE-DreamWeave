using System.Collections.Generic;

namespace ACE.Server.WaveEncounters
{
    /// <summary>
    /// The player-facing lines of one anchor's encounter. Placeholders: {n} the wave number, {total} the
    /// wave count, {m} the cooldown in words ("1 minute" / "4 minutes").
    ///
    /// Lines are keyed by the ANCHOR's wcid rather than carried as properties on it: the runner is generic,
    /// but its narration is content-specific, and a catalog here costs no property ids and keeps every line
    /// reviewable in one file. An anchor with no entry gets <see cref="Default"/>, which is deliberately
    /// plain so a new anchor still reads sensibly before its own lines are written.
    /// </summary>
    public sealed class WaveEncounterText
    {
        public string Start { get; private set; }
        public string Wave { get; private set; }
        public string FinalWave { get; private set; }
        public string RefuseRunning { get; private set; }
        public string RefuseCooldown { get; private set; }
        public string Win { get; private set; }
        public string Fail { get; private set; }

        public static readonly WaveEncounterText Default = new WaveEncounterText
        {
            Start = "The waves begin. {total} are coming.",
            Wave = "Wave {n} of {total}.",
            FinalWave = null,
            RefuseRunning = "It is already under way.",
            RefuseCooldown = "Nothing answers yet. Try again in {m}.",
            Win = "The last wave falls.",
            Fail = "It goes quiet, and what was called slips away.",
        };

        /// <summary>The Sounding Drum, Bluespire ladder rung 6 (weenies/1005611). Lines are the owner's, verbatim.</summary>
        public static readonly WaveEncounterText SoundingDrum = new WaveEncounterText
        {
            Start = "The Sounding Drum booms, and the deep answers. Ten waves come.",
            Wave = "Wave {n} of {total}.",
            FinalWave = "The Deepspeaker and the Drumbreaker come for you.",
            RefuseRunning = "The drum is already sounding.",
            RefuseCooldown = "The drum skin is still shaking. It will answer again in {m}.",
            Win = "The deep falls silent.",
            Fail = "The drum goes quiet, and what it called slips back into the dark.",
        };

        public const uint SoundingDrumWcid = 1005611;

        private static readonly Dictionary<uint, WaveEncounterText> byAnchorWcid = new Dictionary<uint, WaveEncounterText>
        {
            { SoundingDrumWcid, SoundingDrum },
        };

        public static WaveEncounterText For(uint anchorWcid)
            => byAnchorWcid.TryGetValue(anchorWcid, out var text) ? text : Default;

        public static string Format(string template, int wave = 0, int total = 0, int minutes = 0)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            return template
                .Replace("{n}", wave.ToString())
                .Replace("{total}", total.ToString())
                .Replace("{m}", WaveEncounterRules.MinutesText(minutes));
        }
    }
}
