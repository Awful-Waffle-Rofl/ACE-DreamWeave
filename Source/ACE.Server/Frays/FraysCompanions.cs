using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>One collected form's companion: its locked style and its own Kindling experience.</summary>
    public class FraysCompanionForm
    {
        [JsonPropertyName("r")] public string Role { get; set; } = "";
        [JsonPropertyName("x")] public double Xp { get; set; }
    }

    /// <summary>
    /// Effigy companions: loot an Effigy, choose its style, level it with Kindling, and respec the style with an
    /// item bought for Embers. Each companion has its own level. Every collected Effigy form can be a companion. Its style (warder, mender or striker) is
    /// chosen once and then locked; a Rebinding Ember (sold by the Boonwright for Embers) changes it and keeps the level.
    /// Kindling levels the active companion. Only the active companion's bonus applies, whether or not it is summoned
    /// with /fray pet (a summoned pet takes the pet slot, so a combat-pet user can keep the bonus without it).
    /// The old single companion (CompanionRole and CompanionXp) carries its experience to the first form given a style.
    /// </summary>
    public static class FraysCompanions
    {
        /// <summary>The style respec item (dreamweave-content 2000174).</summary>
        public const uint RebindingEmber = 2000174;

        public static readonly string[] Roles = { "warder", "mender", "striker" };

        public static int Level(double xp) => FraysLongTerm.CompanionLevel(xp);

        public static bool Owns(FraysPlayerRecord rec, string key) => rec.Codex.Contains(FraysCodex.Effigy + key);

        /// <summary>The active companion's form key and record, or null when none is set.</summary>
        public static (string Key, FraysCompanionForm Form)? Active(FraysPlayerRecord rec)
        {
            if (!string.IsNullOrEmpty(rec.ActiveCompanion) && rec.Companions.TryGetValue(rec.ActiveCompanion, out var form) && !string.IsNullOrEmpty(form.Role))
                return (rec.ActiveCompanion, form);

            return null;
        }

        /// <summary>The style and level whose bonus applies in a camp (the old single companion until a form has a style).</summary>
        public static (string Role, int Level) Bonus(FraysPlayerRecord rec)
        {
            if (Active(rec) is { } act)
                return (act.Form.Role, Level(act.Form.Xp));

            return rec.Companions.Count == 0 ? (rec.CompanionRole, Level(rec.CompanionXp)) : ("", 0);
        }

        public static string FamilyName(string key) => FraysFamilies.All.FirstOrDefault(f => f.Key == key)?.Name ?? key;

        /// <summary>
        /// Pure: gives a collected form its style, or changes it with a Rebinding Ember (<paramref name="spendRebinding"/>
        /// spends one and says whether it could). The form becomes the active companion.
        /// </summary>
        public static string SetStyle(FraysPlayerRecord rec, string key, string role, Func<bool> spendRebinding)
        {
            role = (role ?? "").ToLowerInvariant();

            if (!Roles.Contains(role))
                return "Companion styles, the same three for every family: warder (less damage taken), mender (heals you) or striker (more damage).";

            if (!Owns(rec, key))
                return "You have not collected that form. Use its Fray Effigy first.";

            var name = FamilyName(key);

            if (rec.Companions.TryGetValue(key, out var form) && !string.IsNullOrEmpty(form.Role))
            {
                if (form.Role == role)
                {
                    rec.ActiveCompanion = key;
                    return $"Your {name} is already a {role}. It is your companion now.";
                }

                if (!spendRebinding())
                    return $"Your {name} is locked as a {form.Role}. Changing its style takes a Rebinding Ember (the Boonwright sells them for Embers).";

                var was = form.Role;
                form.Role = role;
                rec.ActiveCompanion = key;
                return $"The Rebinding Ember burns away. Your {name} was a {was} and is now a {role}, still level {Level(form.Xp)} ({StyleText(role, Level(form.Xp))}).";
            }

            form ??= new FraysCompanionForm();

            // The old single companion's experience goes to the first form that gets a style.
            if (rec.Companions.Values.All(f => string.IsNullOrEmpty(f.Role)) && rec.CompanionXp > 0)
            {
                form.Xp += rec.CompanionXp;
                rec.CompanionXp = 0;
            }

            rec.CompanionRole = "";
            form.Role = role;
            rec.Companions[key] = form;
            rec.ActiveCompanion = key;
            return $"Your {name} is now a {role} companion, level {Level(form.Xp)} ({StyleText(role, Level(form.Xp))}). Its style is locked; a Rebinding Ember changes it. Feed it Kindling to level it.";
        }

        /// <summary>Pure: makes a styled form the active companion.</summary>
        public static string SetActive(FraysPlayerRecord rec, string key)
        {
            if (!Owns(rec, key))
                return "You have not collected that form. Use its Fray Effigy first.";

            if (!rec.Companions.TryGetValue(key, out var form) || string.IsNullOrEmpty(form.Role))
                return $"Your {FamilyName(key)} has no style yet. Choose one: /frays companion {key} warder, mender or striker. The style is locked once chosen.";

            rec.ActiveCompanion = key;
            return $"Your {FamilyName(key)} ({form.Role}, level {Level(form.Xp)}) is your companion now: {StyleText(form.Role, Level(form.Xp))}.";
        }

        /// <summary>Pure: adds one Kindling's experience to the active companion. False when it took none; <paramref name="message"/> says why.</summary>
        public static bool Feed(FraysPlayerRecord rec, double xp, out string message)
        {
            if (Active(rec) is not { } act)
            {
                message = rec.Companions.Count == 0
                    ? "You have no companion yet. Collect a form with a Fray Effigy, then choose its style: /frays companion <form> warder, mender or striker."
                    : "You have no active companion. /frays companion <form> picks one.";
                return false;
            }

            var (key, form) = act;
            var name = FamilyName(key);

            if (Level(form.Xp) >= 10)
            {
                message = $"Your {name} is already at its full strength (level 10).";
                return false;
            }

            var before = Level(form.Xp);
            form.Xp += xp;
            var after = Level(form.Xp);
            message = after > before ? $"Your {name} grows to level {after} ({StyleText(form.Role, after)})." : $"Your {name} takes the Kindling ({form.Xp:0} experience, level {after}).";
            return true;
        }

        public static string StyleText(string role, int level) => FraysLongTerm.CompanionText(role, level);

        /// <summary>The companion readout: every collected form with its style and level, the active one marked.</summary>
        public static IEnumerable<string> Lines(FraysPlayerRecord rec)
        {
            var owned = FraysFamilies.All.Where(f => Owns(rec, f.Key)).ToList();

            if (owned.Count == 0)
            {
                yield return "No companions yet. Every Fray Effigy you use adds a form; give it a style with /frays companion <form> warder, mender or striker (every family has the same three styles).";
                yield break;
            }

            yield return "Your companions (only the active one's bonus applies; each has its own level):";

            foreach (var f in owned)
            {
                rec.Companions.TryGetValue(f.Key, out var form);
                var mark = rec.ActiveCompanion == f.Key && !string.IsNullOrEmpty(form?.Role) ? " - ACTIVE" : "";

                yield return string.IsNullOrEmpty(form?.Role)
                    ? $"  {f.Name} ({f.Key}): no style yet"
                    : $"  {f.Name} ({f.Key}): {form.Role}, level {Level(form.Xp)}, {StyleText(form.Role, Level(form.Xp))}{mark}";
            }

            if (rec.Companions.Count == 0 && !string.IsNullOrEmpty(rec.CompanionRole))
                yield return $"  Your old {rec.CompanionRole} companion (level {Level(rec.CompanionXp)}) still applies until you style a form; its experience moves to that form.";

            yield return "/frays companion <form> <style> sets a style (locked), /frays companion <form> picks the active one, Kindling levels it, a Rebinding Ember changes a style.";
        }

        /// <summary>/frays companion [form] [style].</summary>
        public static string Command(Player player, string[] args)
        {
            var rec = FraysManager.Record(player);

            if (args.Length == 0)
                return string.Join("\n", Lines(rec));

            var owned = FraysFamilies.All.Where(f => Owns(rec, f.Key)).ToList();
            var arg = args[0].ToLowerInvariant();

            if (Roles.Contains(arg))
                return "Name the form first: /frays companion <form> " + arg + ". /frays companion lists yours.";

            var family = owned.FirstOrDefault(f => f.Key.Equals(arg, StringComparison.OrdinalIgnoreCase)) ?? owned.FirstOrDefault(f => f.Name.ToLowerInvariant().Contains(arg));

            if (family == null)
                return $"You have not collected that form. Yours: {(owned.Count == 0 ? "none yet" : string.Join(", ", owned.Select(f => f.Key)))}.";

            var text = args.Length > 1
                ? SetStyle(rec, family.Key, args[1], () => SpendRebinding(player))
                : SetActive(rec, family.Key);

            FraysManager.SaveRecord(player, rec);
            return text;
        }

        private static bool SpendRebinding(Player player)
        {
            var ember = player.GetInventoryItemsOfWCID(RebindingEmber).FirstOrDefault();
            return ember != null && player.TryConsumeFromInventoryWithNetworking(ember, 1);
        }
    }
}
