using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Server.WorldObjects;

namespace ACE.Server.KnotLoot
{
    /// <summary>
    /// Appraisal text for rolled Knot traits. Item-centric (no wielder), like the equipment-mod lines: the values are
    /// resolved at read time from the item's stored position, so a retuned setting shows on existing items.
    /// </summary>
    public static class KnotTraitDisplay
    {
        private static string Whole(double x) => Math.Round(x, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

        private static string Pct(double fraction) => (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture);

        public static string Describe(KnotTraitDefinition d, KnotTraitEffect e)
        {
            var pct = Math.Clamp(Math.Round(e.P * 100, MidpointRounding.AwayFromZero), 0, 100).ToString("0", CultureInfo.InvariantCulture);

            string effect;

            switch (d.Id)
            {
                case KnotTraitId.CastSpeed:
                    effect = $"+{Pct(e.Primary)}% buff cast speed";
                    break;
                case KnotTraitId.ShieldTrade:
                    effect = $"+{Whole(e.Primary)} shield armor, -{Whole(e.Secondary)} maximum health";
                    break;
                case KnotTraitId.FrugalHand:
                    effect = $"-{Pct(e.Primary)}% stamina and mana usage";
                    break;
                case KnotTraitId.WristTrade:
                    var g = Whole(e.Primary);
                    var c = Whole(e.Secondary);
                    effect = $"left wrist +{g} damage resist rating, -{c} damage rating; right wrist +{g} damage rating, -{c} damage resist rating";
                    break;
                case KnotTraitId.OverpoweringIntent:
                    effect = $"+{Pct(e.Primary)}% War and Void Magic skill against monster resistance";
                    break;
                default:
                    effect = string.Empty;
                    break;
            }

            return $"{d.DisplayName} [{pct}%]: {effect}";
        }

        public static List<string> GetAppraisalLines(WorldObject item)
        {
            var lines = new List<string>();

            if (item == null)
                return lines;

            foreach (var d in KnotTraitRegistry.All)
            {
                var e = KnotTraitValue.ResolveOn(item, d.Id, null);

                if (e.Active)
                    lines.Add("- " + Describe(d, e));
            }

            return lines;
        }
    }
}
