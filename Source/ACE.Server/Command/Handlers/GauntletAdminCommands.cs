using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>One loaded object, as the arena census sees it. Built at the world edge from a WorldObject.</summary>
    public readonly record struct GauntletCensusItem(uint Wcid, string Name, bool IsCreature, bool IsGenerator, bool IsPortal);

    /// <summary>
    /// The verdict of a Gauntlet arena copy's content: no creature, no generator, and exactly one portal, which is the arena
    /// exit (the content-filter check of Task 5, kept as @gauntlet census). Pure.
    /// </summary>
    public sealed class GauntletArenaCensus
    {
        public int Creatures { get; private init; }
        public int Generators { get; private init; }
        public int Portals { get; private init; }
        public int ExitPortals { get; private init; }
        public int Objects { get; private init; }

        /// <summary>"wcid name (what)" for every object that breaks the rule.</summary>
        public IReadOnlyList<string> Offenders { get; private init; } = Array.Empty<string>();

        public bool Clean => Creatures == 0 && Generators == 0 && Portals == 1 && ExitPortals == 1;

        public static GauntletArenaCensus Count(IEnumerable<GauntletCensusItem> items, uint exitWcid)
        {
            int creatures = 0, generators = 0, portals = 0, exits = 0, objects = 0;
            var offenders = new List<string>();

            foreach (var i in items ?? Array.Empty<GauntletCensusItem>())
            {
                objects++;

                if (i.IsCreature) { creatures++; offenders.Add($"{i.Wcid} {i.Name} (creature)"); }
                if (i.IsGenerator) { generators++; offenders.Add($"{i.Wcid} {i.Name} (generator)"); }

                if (i.IsPortal)
                {
                    portals++;

                    if (i.Wcid == exitWcid)
                        exits++;
                    else
                        offenders.Add($"{i.Wcid} {i.Name} (portal that is not the exit)");
                }
            }

            return new GauntletArenaCensus { Creatures = creatures, Generators = generators, Portals = portals, ExitPortals = exits, Objects = objects, Offenders = offenders };
        }

        public string Summary() =>
            $"{(Clean ? "CLEAN" : "NOT CLEAN")}: {Objects} objects, {Creatures} creatures, {Generators} generators, {Portals} portals ({ExitPortals} exit)";
    }

    internal enum GauntletAdminVerb
    {
        List,
        Abort,
        Census,
    }

    /// <summary>
    /// @gauntlet list | abort &lt;avatarId&gt; [refund] | census. Admin only. list shows every live run; abort ends one (before wave 1
    /// the reservation always goes back; after it, "refund" also returns the committed price through AvatarManager.RefundFavor,
    /// which respects the Favor cap); census counts the creatures, generators and exits loaded in the caller's own arena copy.
    /// </summary>
    public static class GauntletAdminCommands
    {
        internal const string UsageText = "Usage: @gauntlet list | abort <avatarId> [refund] | census";

        /// <summary>Pure parse. Error is the usage or the argument fault (null when the line is valid).</summary>
        internal static (GauntletAdminVerb Verb, uint AvatarId, bool Refund, string Error) Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return (GauntletAdminVerb.List, 0, false, UsageText);

            switch (parameters[0].ToLowerInvariant())
            {
                case "list":
                    return (GauntletAdminVerb.List, 0u, false, parameters.Length == 1 ? null : UsageText);

                case "census":
                    return (GauntletAdminVerb.Census, 0u, false, parameters.Length == 1 ? null : UsageText);

                case "abort":
                    if (parameters.Length < 2 || parameters.Length > 3
                        || !uint.TryParse(parameters[1], NumberStyles.None, CultureInfo.InvariantCulture, out var avatarId) || avatarId == 0)
                        return (GauntletAdminVerb.Abort, 0, false, UsageText);

                    if (parameters.Length == 3)
                    {
                        if (!string.Equals(parameters[2], "refund", StringComparison.OrdinalIgnoreCase))
                            return (GauntletAdminVerb.Abort, 0, false, UsageText);

                        return (GauntletAdminVerb.Abort, avatarId, true, null);
                    }

                    return (GauntletAdminVerb.Abort, avatarId, false, null);

                default:
                    return (GauntletAdminVerb.List, 0, false, UsageText);
            }
        }

        /// <summary>One run per line: key, Avatar, site, state, wave and level, cleared, seconds left, headcount, fought, instance.</summary>
        internal static string ListLine(GauntletRunInfo r) =>
            string.Format(CultureInfo.InvariantCulture,
                "run {0} (id {1}): Avatar {2}, {3}, {4}, wave {5} L{6}, {7} cleared, {8} s left, {9} fighting, {10} fought, instance 0x{11:X8}{12}",
                r.RunKey, r.RunId, r.AvatarId, r.SiteId, r.State, r.Wave, r.Level, r.WavesCleared, r.SecondsLeft, r.Headcount, r.Fought, r.Instance,
                string.IsNullOrEmpty(r.LastRefusal) ? "" : $" [last refusal: {r.LastRefusal}]");

        /// <summary>Test seam: the admin list.</summary>
        internal static Func<IReadOnlyList<GauntletRunInfo>> ListRuns = GauntletManager.List;

        /// <summary>Test seam: the admin abort (GauntletManager.Abort).</summary>
        internal static Func<uint, bool, string> AbortRun = GauntletManager.Abort;

        /// <summary>Test seam: where a reply line goes (the caller's chat, or the console for a null session).</summary>
        internal static Action<Session, string> Output = (session, text) => CommandHandlerHelper.WriteOutputInfo(session, text);

        internal static void ResetSeams()
        {
            ListRuns = GauntletManager.List;
            AbortRun = GauntletManager.Abort;
            Output = (session, text) => CommandHandlerHelper.WriteOutputInfo(session, text);
        }

        [CommandHandler("gauntlet", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Allegiance Gauntlet admin: list the live runs, abort one (optionally refunding its committed Favor), or census the arena you stand in.",
            "list | abort <avatarId> [refund] | census")]
        public static void HandleGauntlet(Session session, params string[] parameters)
        {
            var (verb, avatarId, refund, error) = Parse(parameters);

            if (error != null)
            {
                Output(session, error);
                return;
            }

            switch (verb)
            {
                case GauntletAdminVerb.List:
                    var runs = ListRuns();

                    if (runs.Count == 0)
                    {
                        Output(session, "No Allegiance Gauntlet runs are live.");
                        return;
                    }

                    foreach (var r in runs)
                        Output(session, ListLine(r));
                    return;

                case GauntletAdminVerb.Abort:
                    Output(session, AbortRun(avatarId, refund));
                    return;

                case GauntletAdminVerb.Census:
                    var player = session?.Player;

                    if (player == null)
                    {
                        Output(session, "@gauntlet census needs a character standing in an arena.");
                        return;
                    }

                    Census(session, player);
                    return;
            }
        }

        private static void Census(Session session, Player player)
        {
            var instance = player.Location.Instance;
            var run = GauntletManager.ForInstance(instance);

            if (run == null)
            {
                Output(session, "You are not inside a live Allegiance Gauntlet arena.");
                return;
            }

            var outdoor = run.Site.Outdoor.Outdoor;
            var items = new List<GauntletCensusItem>();
            var missing = new List<string>();

            foreach (var lb in outdoor.Footprint.Append(outdoor.Primary).Distinct().OrderBy(x => x))
            {
                var landblock = LandblockManager.GetLoadedLandblock(new LandblockId((uint)lb << 16), instance);

                if (landblock == null)
                {
                    missing.Add($"0x{lb:X4}");
                    continue;
                }

                items.AddRange(landblock.GetAllWorldObjectsForDiagnostics()
                    .Where(wo => !(wo is Player))
                    .Select(wo => new GauntletCensusItem(wo.WeenieClassId, wo.Name, wo is Creature, wo.IsGenerator, wo is Portal)));
            }

            var census = GauntletArenaCensus.Count(items, RealmManager.GauntletExitPortalWcid);

            Output(session, $"Gauntlet arena {run.Site.Id} (run {run.RunKey}): {census.Summary()}.");

            if (missing.Count > 0)
                Output(session, $"Footprint landblocks not loaded: {string.Join(", ", missing)}");

            foreach (var offender in census.Offenders.Take(15))
                Output(session, $"  {offender}");
        }
    }
}
