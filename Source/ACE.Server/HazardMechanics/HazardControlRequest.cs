using System.Collections.Generic;
using System.Globalization;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The pure parser of <c>/hazard control &lt;id&gt; key=value [key=value ...]</c>: the run id (a leading '#' is
    /// allowed) and at least one control, in order. Keys are lower-cased; repeats are kept in order (a rate then a
    /// target is a sequence, not a mistake). Every refusal is ONE line.
    /// </summary>
    public static class HazardControlRequest
    {
        public static bool TryParse(IReadOnlyList<string> rest, out int runId, out List<HazardControl> controls, out string error)
        {
            runId = 0;
            controls = new List<HazardControl>();
            error = null;

            if (rest == null || rest.Count == 0)
            {
                error = "expected <id> key=value [key=value ...]";
                return false;
            }

            var idText = rest[0].TrimStart('#');

            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out runId))
            {
                error = $"'{rest[0]}' is not a run id";
                return false;
            }

            if (rest.Count < 2)
            {
                error = "expected at least one key=value after the run id";
                return false;
            }

            for (var i = 1; i < rest.Count; i++)
            {
                var token = rest[i];
                var equals = token.IndexOf('=');

                if (equals <= 0 || equals == token.Length - 1)
                {
                    error = $"token '{token}' is not key=value";
                    return false;
                }

                var key = token.Substring(0, equals).Trim().ToLowerInvariant();
                var text = token.Substring(equals + 1).Trim();

                if (key.Length == 0 || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                {
                    error = $"{(key.Length == 0 ? token : key)}='{text}' is not a number";
                    return false;
                }

                controls.Add(new HazardControl(key, value));
            }

            return true;
        }
    }
}
