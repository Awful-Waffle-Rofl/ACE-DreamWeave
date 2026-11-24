using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The one rule for which sources a named city anchor can host: a source is offered at an anchor only when the anchor's
    /// maxRadius is above 0 and the source's ClearanceRadius is at most that radius (the market admin page's rule). Shared by
    /// the daily scheduler and the in-game "--anchor" start so the two cannot drift.
    /// </summary>
    public static class WorldEventAnchorRules
    {
        /// <summary>True when <paramref name="anchor"/> can host <paramref name="source"/>.</summary>
        public static bool Hosts(AnchorDef anchor, SourceThemeDef source)
            => anchor != null && source != null && anchor.MaxRadius > 0 && source.ClearanceRadius <= anchor.MaxRadius;

        /// <summary>Why <paramref name="anchor"/> hosts nothing at all, or null when it hosts something.</summary>
        public static string AnchorRefusal(AnchorDef anchor)
            => anchor != null && anchor.MaxRadius <= 0
                ? $"anchor '{anchor.Id}' hosts no World Event (its maxRadius is 0)"
                : null;

        /// <summary>Why <paramref name="anchor"/> cannot host <paramref name="source"/>, or null when it can.</summary>
        public static string Refusal(AnchorDef anchor, SourceThemeDef source)
        {
            var none = AnchorRefusal(anchor);

            if (none != null)
                return none;

            return Hosts(anchor, source)
                ? null
                : $"source '{source?.Id}' needs {source?.ClearanceRadius:0.##} m of clearance but anchor '{anchor?.Id}' only hosts up to {anchor?.MaxRadius:0.##} m";
        }

        /// <summary>The ids in <paramref name="allowed"/> whose ClearanceRadius is at most <paramref name="maxRadius"/> (above 0); an unknown id is dropped.</summary>
        public static List<string> NarrowSources(IEnumerable<string> allowed, float maxRadius, Func<string, float?> clearanceOf)
        {
            if (maxRadius <= 0)
                return new List<string>();

            return (allowed ?? Enumerable.Empty<string>())
                .Where(id =>
                {
                    var clearance = clearanceOf(id);
                    return clearance != null && clearance.Value <= maxRadius;
                })
                .ToList();
        }
    }
}
