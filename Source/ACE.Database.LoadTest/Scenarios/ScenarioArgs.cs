using System.Collections.Generic;
using System.Globalization;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// Parses "--key=value" tokens (anything after the scenario name on the command line).
    /// </summary>
    public class ScenarioArgs
    {
        private readonly Dictionary<string, string> _values = new();

        public ScenarioArgs(string[] tokens)
        {
            foreach (var token in tokens)
            {
                if (!token.StartsWith("--"))
                    continue;

                var trimmed = token[2..];
                var separatorIndex = trimmed.IndexOf('=');

                if (separatorIndex < 0)
                    _values[trimmed] = "true";
                else
                    _values[trimmed[..separatorIndex]] = trimmed[(separatorIndex + 1)..];
            }
        }

        public int GetInt(string key, int defaultValue) =>
            _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;

        public uint GetUInt(string key, uint defaultValue)
        {
            if (!_values.TryGetValue(key, out var value))
                return defaultValue;

            if (value.StartsWith("0x") && uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexParsed))
                return hexParsed;

            return uint.TryParse(value, out var parsed) ? parsed : defaultValue;
        }

        public double GetDouble(string key, double defaultValue) =>
            _values.TryGetValue(key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;

        public bool GetBool(string key, bool defaultValue) =>
            _values.TryGetValue(key, out var value) ? value == "true" : defaultValue;

        public string GetString(string key, string defaultValue) =>
            _values.TryGetValue(key, out var value) ? value : defaultValue;
    }
}
