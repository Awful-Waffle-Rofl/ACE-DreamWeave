using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Database;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// "/frays admin ...": the Frays settings (show, set, reset; the HoldSettingsAdmin pattern), the per-fire leash
    /// overrides, a fire's zone and numbers, the placement check and a few test aids. Every write goes through
    /// PropertyAdminService.TryModify, so it is audited exactly like @modifystring.
    /// </summary>
    public static class FraysAdmin
    {
        /// <summary>Test seam: writes one string property; null on success, else why not.</summary>
        internal static Func<string, string, Session, string> Writer = WriteProperty;

        public const string Usage =
            "/frays admin settings [group|all] | set <name> <value> | reset <name> | fire <id> [set <key> <value> | reset <key>] | zone [id] [rings [off]] | check [gap] | fires | goto <id> | heat <id> <level> | snarl [id] | greatsnarl [id] | boon [id] | firetier <n> | piece (the village tool; piece help) | out <id> | rating <n> | heatfx [n] | ascfx <1-5>";

        private static string WriteProperty(string property, string value, Session session)
        {
            var type = FraysSettings.TryFind(property, out var setting) ? FraysSettings.PropertyType(setting) : PropertyAdminService.TypeString;
            var result = PropertyAdminService.TryModify(type, property, value, PropertyAdminActor.InGame(session));

            switch (result?.Outcome)
            {
                case PropertyModifyOutcome.Updated:
                case PropertyModifyOutcome.Unchanged:
                    return null;
                case PropertyModifyOutcome.UnknownKey:
                    return $"{property} is not a server property";
                default:
                    return $"{property} was not written ({result?.Outcome})";
            }
        }

        // ---- settings ----------------------------------------------------------------------------------------

        public static string Settings(string which)
        {
            var sb = new StringBuilder();
            var w = (which ?? "").Trim().ToLowerInvariant();

            IEnumerable<FraysSetting> chosen;

            if (w.Length == 0)
            {
                sb.AppendLine($"Frays settings (the Frays are {(FraysManager.Enabled ? "on" : "off")}). Groups: {string.Join(", ", FraysSettings.Groups)}.");
                chosen = FraysSettings.All.Where(FraysSettings.IsSet);
                sb.AppendLine(chosen.Any() ? "Set:" : "Nothing is set: every value is the code default.");
            }
            else if (w == "all")
                chosen = FraysSettings.All;
            else
            {
                chosen = FraysSettings.All.Where(s => s.Group == w).ToList();

                if (!chosen.Any())
                    return $"No group '{which}'. Groups: {string.Join(", ", FraysSettings.Groups)}.";
            }

            foreach (var s in chosen)
                sb.AppendLine($"  {s.Key} = {FraysSettings.Display(s)}  [{s.RangeText}]{(FraysSettings.IsSet(s) ? "  *set" : "")}{(s.PerFire ? "  per-fire" : "")}  - {s.Description}");

            sb.Append("Use: /frays admin set <name> <value>, /frays admin reset <name>.");
            return sb.ToString();
        }

        public static string Set(string name, string value, Session session)
        {
            if (!FraysSettings.TryFind(name, out var s))
                return $"No Frays setting '{name}'. See /frays admin settings all.";

            if (s.Key == FraysSettings.FireOverrides)
                return "Use /frays admin fire <id> set <key> <value> for per-fire values.";

            if (string.IsNullOrWhiteSpace(value))
                return $"Give a value, or use /frays admin reset {s.Key}.";

            string encoded;

            if (s.Kind == FraysSettingKind.Text)
                encoded = value.Trim();
            else
            {
                if (!FraysSettings.TryParse(s, value, out var v, out var warning))
                    return $"Not set: {warning}.";

                encoded = FraysSettings.Encode(s, v);
            }

            var error = Writer(s.PropertyName, encoded, session);

            return error ?? $"{s.Key} = {FraysSettings.Display(s)} (live).{(s.Key == FraysSettings.Enabled || s.Key == FraysSettings.Landblocks ? " Applies when the dungeon next loads (restart the server or let it unload)." : "")}";
        }

        /// <summary>Writes a setting's raw text (the village layout's JSON). Null when written, else why not.</summary>
        public static string SetRaw(string name, string value, Session session) =>
            FraysSettings.TryFind(name, out var s) ? Writer(s.PropertyName, value, session) : $"No Frays setting '{name}'.";

        public static string Reset(string name, Session session)
        {
            if (!FraysSettings.TryFind(name, out var s))
                return $"No Frays setting '{name}'.";

            var error = Writer(s.PropertyName, FraysSettings.EncodedDefault(s), session);
            return error ?? $"{s.Key} reset to its default ({FraysSettings.Display(s)}).";
        }

        // ---- per-fire overrides ------------------------------------------------------------------------------

        public static string FireOverride(string fireId, string action, string key, string value, Session session)
        {
            var def = FraysDefs.Find(fireId);

            if (def == null)
                return $"No fire '{fireId}'. Fires: {string.Join(", ", FraysDefs.All.Select(f => f.Id))}.";

            var overrides = FraysSettings.ParseOverrides(FraysSettings.Text(FraysSettings.FireOverrides));

            if (string.IsNullOrEmpty(action))
                return FireSettings(def);

            if (!FraysSettings.TryFind(key, out var s) || !s.PerFire)
                return $"'{key}' cannot be set per fire. Per-fire keys: {string.Join(", ", FraysSettings.All.Where(x => x.PerFire).Select(x => x.Key))}.";

            if (!overrides.TryGetValue(def.Id, out var map))
                overrides[def.Id] = map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            if (action == "reset")
                map.Remove(s.Key);
            else if (action == "set")
            {
                if (!FraysSettings.TryParse(s, value, out var v, out var warning))
                    return $"Not set: {warning}.";

                map[s.Key] = v;
            }
            else
                return Usage;

            var error = Writer(FraysSettings.Prefix + FraysSettings.FireOverrides, FraysSettings.FormatOverrides(overrides), session);

            return error ?? FireSettings(def);
        }

        private static string FireSettings(FraysFireDef def)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Leash settings for {def.Id} ({def.RoomName}):");

            foreach (var s in FraysSettings.All.Where(x => x.PerFire))
            {
                var own = FraysSettings.FireOverride(def.Id, s.Key);
                sb.AppendLine($"  {s.Key} = {FraysSettings.Format(FraysSettings.Number(s.Key, def.Id))}{(own != null ? "  (this fire)" : "  (global)")}");
            }

            sb.Append($"Use: /frays admin fire {def.Id} set <key> <value> | reset <key>.");
            return sb.ToString();
        }

        // ---- a fire's zone and numbers ----------------------------------------------------------------------

        public static string Zone(Player admin, string fireId, string option = null)
        {
            // /frays admin zone [fire] rings [off]: each pack's aggro and tether rings.
            var rings = string.Equals(fireId, "rings", StringComparison.OrdinalIgnoreCase) || string.Equals(option, "rings", StringComparison.OrdinalIgnoreCase);
            var off = string.Equals(option, "off", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(fireId, "rings", StringComparison.OrdinalIgnoreCase))
                fireId = null;

            var fire = string.IsNullOrEmpty(fireId) ? FraysManager.NearestFire(admin) : FraysManager.FindFire(admin, fireId);

            if (fire == null)
                return "No Frays fire is loaded here. Go to a Fray campfire with frays_enabled on, or name a fire.";

            var id = fire.Def.Id;
            var sb = new StringBuilder();
            var margin = fire.Margin;
            var chase = fire.MaxChase;

            sb.AppendLine($"Fire {id} - {fire.Def.RoomName} (landblock 0x{fire.Def.Landblock:X4}, instance 0x{fire.Instance:X8}), at cell 0x{fire.Def.FireCell:X3} {fire.Def.FirePos.X:0.0}, {fire.Def.FirePos.Y:0.0}, {fire.Def.FirePos.Z:0.0}.");
            sb.AppendLine($"Leash: margin {FraysSettings.Format(margin)} m, max chase {FraysSettings.Format(chase)} m, grace {FraysSettings.Format(FraysSettings.Number(FraysSettings.LeashGraceSeconds, id))} s, return speed x{FraysSettings.Format(FraysSettings.Number(FraysSettings.LeashReturnSpeed, id))}, heal {FraysSettings.Format(FraysSettings.Number(FraysSettings.LeashReturnHeal, id) * 100)}% on return.");
            sb.AppendLine($"Room cells ({fire.Zone.Cells.Count}): {string.Join(" ", fire.Zone.Cells.OrderBy(c => c).Select(c => c.ToString("X3")))}");
            sb.AppendLine($"Doorways ({fire.Zone.Doorways.Count}): {string.Join("; ", fire.Zone.Doorways.Select(d => $"{d.Inside:X3}->{d.Outside:X3} at {d.Centre.X:0},{d.Centre.Y:0},{d.Centre.Z:0}"))}");
            sb.AppendLine($"Spawn points ({fire.Def.SpawnPoints.Count}): {string.Join("; ", fire.Def.SpawnPoints.Select(p => $"{p.Cell:X3} {p.Pos.X:0.0},{p.Pos.Y:0.0}"))}");

            if (admin?.Location != null)
            {
                var inside = fire.InZone(admin.Location, margin, chase);
                sb.AppendLine($"You: cell 0x{admin.Location.LandblockId.Raw & 0xFFFF:X3}, {fire.DistanceToFire(admin.Location):0.0} m from the fire, {(inside ? "INSIDE" : "outside")} the zone.");
            }

            sb.Append(PackLanes(fire));

            if (rings)
                sb.AppendLine(fire.AdminRings(admin, !off));

            sb.Append(FireNumbers(fire));
            return sb.ToString();
        }

        /// <summary>Each pack point's type, nearest neighbour, the clear lane between their aggro circles and the room past the tether.</summary>
        public static string PackLanes(FraysFire fire)
        {
            var l = fire.Layout;

            if (l == null)
                return "";

            var aggro = FraysSettings.Number(FraysSettings.AggroRange);
            var tether = FraysSettings.Number(FraysSettings.PackTether);
            var packs = Enumerable.Range(0, l.Roles.Count).Where(i => !l.Spawnless(i)).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Packs ({packs.Count}, aggro {aggro:0} m, tether {tether:0} m; lane = gap less both aggro circles, room = gap less the tether and the neighbour's aggro):");

            foreach (var i in packs)
            {
                var p = fire.PointPos(i);
                var near = packs.Where(j => j != i).Select(j => (J: j, D: Math.Sqrt(Math.Pow(p.X - fire.PointPos(j).X, 2) + Math.Pow(p.Y - fire.PointPos(j).Y, 2)))).OrderBy(x => x.D).FirstOrDefault();
                var type = l.TypeOf(i) == FraysPackType.None ? FraysRules.PackName(fire.KindOf(i), fire.LayoutIndex(i), FraysShape.Medium) : FraysRules.TypedName(l.TypeOf(i));
                sb.AppendLine($"  {i}: {type}{(l.RoleOf(i) == FraysPointRole.Rich ? " (Gilded)" : "")} at {p.X:0},{p.Y:0}, nearest pack {near.D:0.0} m (point {near.J}), lane {near.D - 2 * aggro:0.0} m, room {near.D - tether - aggro:0.0} m.");
            }

            if (l.Shrine >= 0)
                sb.AppendLine($"  Shrine: point {l.Shrine} at {fire.PointPos(l.Shrine).X:0},{fire.PointPos(l.Shrine).Y:0}.");

            return sb.ToString();
        }

        public static string FireNumbers(FraysFire fire)
        {
            var totals = $"{fire.Kills} kills, {fire.OwnerDeaths} owner deaths, {fire.LeashReturns} leash returns, {fire.ZoneEscapes} zone escapes (outside over the grace time), farthest any monster got from the fire {fire.FarthestFromFire:0.0} m.";

            if (!fire.Lit)
                return $"Unlit. Last camp: {totals}";

            var minutes = (ACE.Common.Time.GetUnixTime() - fire.LitAt) / 60;
            var sb = new StringBuilder();

            sb.AppendLine($"Lit by {fire.OwnerName} for {minutes:0} min: {fire.AliveCount} monsters alive, {fire.CorpseCount} corpses, {fire.PresentCount} owners in the room. {totals}");

            foreach (var c in fire.CampersSnapshot())
            {
                var (have, need) = FraysFire.ProgressOf(c);
                sb.AppendLine($"  {c.Name}: Heat {c.Level} ({have}/{need}), peak {c.PeakLevel}, limit {c.Limit.Level}{(c.Limit.Frozen ? " held" : "")}, {c.Kills} kills, {c.Deaths} deaths, {c.XpPaid:N0} xp, {c.LumPaid:N0} lum, {Math.Floor(c.EmbersEarned):0} Embers.");
            }

            return sb.ToString().TrimEnd();
        }

        public static string Fires()
        {
            var list = FraysManager.Fires.OrderBy(f => f.Instance).ThenBy(f => f.Def.Id).ToList();

            if (list.Count == 0)
                return $"No Frays fires are loaded (frays_enabled is {(FraysManager.Enabled ? "on" : "off")}; they appear when the dungeon loads).";

            return string.Join("\n", list.Select(f => $"{f.Def.Id} {f.Def.RoomName} (0x{f.Instance:X8}): {(f.Lit ? $"lit by {f.OwnerName}, top Heat {f.TopLevel}, {f.AliveCount} alive, {f.CorpseCount} corpses" : "unlit")}; wood {(f.FireObject == null ? "MISSING" : f.FireObject.IsDestroyed ? "destroyed" : (f.FireObject.Location?.ToLOCString() ?? "no location") + (f.FireObject.CurrentLandblock == null ? " (not on a landblock)" : ""))}"));
        }

        // ---- placement check ---------------------------------------------------------------------------------

        /// <summary>Every pair of fires in one dungeon whose leash zones are closer than the gap.</summary>
        public static string Check(string gapText)
        {
            var sb = new StringBuilder();
            var margin = FraysSettings.Number(FraysSettings.LeashMargin);

            foreach (var group in FraysDefs.All.GroupBy(f => f.Landblock))
            {
                var boxes = FraysGeometry.CellBoxes(group.Key, out var error);

                if (boxes == null)
                {
                    sb.AppendLine($"0x{group.Key:X4}: cannot read the cells ({error}).");
                    continue;
                }

                double gap;

                if (!string.IsNullOrWhiteSpace(gapText) && double.TryParse(gapText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var given))
                    gap = given;
                else if (FraysSettings.Number(FraysSettings.PlacementGap) > 0)
                    gap = FraysSettings.Number(FraysSettings.PlacementGap);
                else
                    gap = DetectionRange(group);

                var defs = group.ToList();
                var bad = 0;

                sb.AppendLine($"0x{group.Key:X4}: {defs.Count} fires, zones grown by {FraysSettings.Format(margin)} m (doorway margin), required gap {gap:0.0} m.");

                for (var i = 0; i < defs.Count; i++)
                {
                    for (var j = i + 1; j < defs.Count; j++)
                    {
                        var a = defs[i];
                        var b = defs[j];
                        var marginA = FraysSettings.Number(FraysSettings.LeashMargin, a.Id);
                        var marginB = FraysSettings.Number(FraysSettings.LeashMargin, b.Id);
                        var d = FraysLeashZone.Gap(FraysGeometry.RoomBoxes(a, boxes), FraysGeometry.RoomBoxes(b, boxes), Math.Max(marginA, marginB));

                        if (d < gap)
                        {
                            bad++;
                            sb.AppendLine($"  TOO CLOSE: {a.Id} ({a.RoomName}) and {b.Id} ({b.RoomName}): {d:0.0} m apart.");
                        }
                        else
                            sb.AppendLine($"  ok: {a.Id} - {b.Id}: {d:0.0} m.");
                    }
                }

                sb.AppendLine(bad == 0 ? "  Every pair is far enough apart." : $"  {bad} pair(s) too close.");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>The largest detection range (VisualAwarenessRange) among the creatures the fires can spawn.</summary>
        private static double DetectionRange(IEnumerable<FraysFireDef> defs)
        {
            var best = 0.0;

            foreach (var wcid in defs.SelectMany(d => d.Mix).Select(m => m.Wcid).Distinct())
            {
                var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

                if (weenie?.PropertiesFloat != null && weenie.PropertiesFloat.TryGetValue(ACE.Entity.Enum.Properties.PropertyFloat.VisualAwarenessRange, out var v))
                    best = Math.Max(best, v);
                else
                    best = Math.Max(best, Creature.VisualAwarenessRange_Default);
            }

            return best * PropertyManager.GetDouble("mob_awareness_range").Item;
        }
    }
}
