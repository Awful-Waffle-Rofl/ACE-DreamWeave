using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// WaffleACE fork: authoring loop for server-generated sky decor.
    ///
    /// A sky_decor_region row describes a rectangle of landblocks whose outdoor sky is dressed with
    /// spinning Sky Rift discs. Nothing is placed by hand and nothing is persisted, so the whole
    /// iteration loop happens in game:
    ///
    ///   /sky-decor set scale_max 180   tune a value in memory and see it immediately
    ///   /sky-decor show                what is set now, and which values are overrides
    ///   /sky-decor sql                 one UPDATE carrying the finished values, to paste into the
    ///                                  content unit when the look is right
    ///   /sky-decor reload              throw the overrides away and go back to the database
    ///
    /// `set` NEVER writes the database. That is deliberate and is the reason this command family can be
    /// used freely while tuning: nothing a session does here can leave a half-tuned value in ace_world,
    /// and a restart returns the sky to exactly what the content unit says.
    /// </summary>
    public static class SkyDecorCommands
    {
        private const string Usage =
            "Usage:\n" +
            "  /sky-decor info                          what is spawned where you stand\n" +
            "  /sky-decor show [<region>]               every tunable and its effective value\n" +
            "  /sky-decor set [<region>] <col> <value>  override a value in memory and respawn\n" +
            "  /sky-decor sql [<region>]                one UPDATE with the current values, to paste\n" +
            "  /sky-decor colours                       the colour names a palette accepts\n" +
            "  /sky-decor reload                        re-read the table (DROPS all overrides)\n" +
            "\n" +
            "<region> may be omitted when exactly one region covers the landblock you are standing on.\n" +
            "Settable columns: ";

        [CommandHandler("sky-decor", AccessLevel.Developer, CommandHandlerFlag.None, 1,
            "Server-generated sky decor (sky_decor_region).",
            "info | show | set | sql | colours | reload\n\n" +
            "info   - the regions covering your current landblock and realm, and what is spawned there.\n" +
            "show   - every tunable column and its effective value; overrides are marked with *.\n" +
            "set    - [<region>] <column> <value>: override a value IN MEMORY ONLY and respawn at once.\n" +
            "         Never writes the database; lost on /sky-decor reload and on restart.\n" +
            "sql    - a ready-to-paste UPDATE carrying the current effective values.\n" +
            "colours- the colour names a palette may use, and the wcid behind each one.\n" +
            "reload - re-read sky_decor_region and rebuild every loaded outdoor landblock. Drops overrides.")]
        public static void HandleSkyDecor(Session session, params string[] parameters)
        {
            switch (parameters[0].ToLowerInvariant())
            {
                case "reload":
                    HandleReload(session);
                    break;

                case "info":
                    HandleInfo(session);
                    break;

                case "show":
                    HandleShow(session, parameters);
                    break;

                case "set":
                    HandleSet(session, parameters);
                    break;

                case "sql":
                    HandleSql(session, parameters);
                    break;

                case "colours":
                case "colors":
                    HandleColours(session);
                    break;

                default:
                    // An unknown subcommand used to fall through to a one-line usage string that did not
                    // mention it was unknown, so `/sky-decor min_separation 0.5` looked like it had worked.
                    // Naming the token back is what makes a typo visible.
                    CommandHandlerHelper.WriteOutputInfo(session, $"Unknown /sky-decor subcommand '{parameters[0]}'.\n\n{UsageText()}");
                    break;
            }
        }

        private static string UsageText()
        {
            return Usage + string.Join(", ", SkyDecorOverrides.Columns);
        }

        // ===========================================================================================
        // Region resolution
        // ===========================================================================================

        /// <summary>
        /// Works out which region a subcommand means. An explicit name always wins; otherwise the region
        /// is inferred from where the player stands, and ONLY when that is unambiguous.
        ///
        /// Ambiguity is reported rather than guessed: regions are deliberately additive (two may dress the
        /// same sky), so silently picking the first would edit whichever one happened to sort lower and
        /// leave the owner watching the wrong half of the sky fail to change.
        /// </summary>
        private static bool TryResolveRegion(Session session, string explicitName, out SkyDecorRegion region)
        {
            region = null;

            var regions = SkyDecorOverrides.EffectiveRegions();

            if (regions.Count == 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "No enabled sky_decor_region rows are loaded. Apply the migration and the content unit, then /sky-decor reload.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                region = regions.FirstOrDefault(r => string.Equals(r.Name, explicitName, StringComparison.OrdinalIgnoreCase));

                if (region == null)
                {
                    CommandHandlerHelper.WriteOutputInfo(session, $"No enabled region named '{explicitName}'. Loaded: {string.Join(", ", regions.Select(r => r.Name))}");
                    return false;
                }

                return true;
            }

            var player = session?.Player;

            if (player?.CurrentLandblock == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Name a region: {string.Join(", ", regions.Select(r => r.Name))}");
                return false;
            }

            Position.ParseInstanceID(player.CurrentLandblock.Instance, out _, out var realmId, out _);

            var covering = regions.Where(r => r.Covers(player.CurrentLandblock.Id.Landblock, realmId)).ToList();

            if (covering.Count == 1)
            {
                region = covering[0];
                return true;
            }

            if (covering.Count == 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"No region covers landblock 0x{player.CurrentLandblock.Id.Landblock:X4} in realm {realmId}. Name one: {string.Join(", ", regions.Select(r => r.Name))}");
                return false;
            }

            CommandHandlerHelper.WriteOutputInfo(session, $"{covering.Count} regions cover this landblock - name one: {string.Join(", ", covering.Select(r => r.Name))}");

            return false;
        }

        // ===========================================================================================
        // set
        // ===========================================================================================

        private static void HandleSet(Session session, string[] parameters)
        {
            // `set <column> <value>` and `set <region> <column> <value>` are told apart by looking the
            // second token up in the tunable table rather than by counting arguments, so a value that
            // itself contains spaces cannot make the parse ambiguous.
            string regionName = null;
            string column;
            string value;

            if (parameters.Length >= 3 && SkyDecorOverrides.IsColumn(parameters[1]))
            {
                column = parameters[1];
                value = string.Join(" ", parameters.Skip(2));
            }
            else if (parameters.Length >= 4)
            {
                regionName = parameters[1];
                column = parameters[2];
                value = string.Join(" ", parameters.Skip(3));
            }
            else
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"/sky-decor set needs a column and a value.\n\n{UsageText()}");
                return;
            }

            if (!SkyDecorOverrides.IsColumn(column))
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Unknown column '{column}'.\nSettable: {string.Join(", ", SkyDecorOverrides.Columns)}");
                return;
            }

            if (!TryResolveRegion(session, regionName, out var region))
                return;

            var before = SkyDecorOverrides.Read(region, column);

            if (!SkyDecorOverrides.TryApply(region, column, value, out var updated, out var error, out var warnings))
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"/sky-decor set {region.Name} {column}: {error}");
                return;
            }

            SkyDecorOverrides.Store(updated, column);

            // No cache invalidation here, unlike reload: the point of set is that the OTHER overrides
            // survive, and dropping the cache would re-read the table and take them all with it.
            var queued = QueueRebuildOnLoadedLandblocks();

            var sb = new StringBuilder();

            sb.Append($"{updated.Name}.{column}: {before} -> {SkyDecorOverrides.Read(updated, column)}  (runtime override, NOT written to the database)\n");

            // Advisories, not failures: the edit is already applied. Printed after the old -> new line so
            // the reply never reads as though the change was refused.
            foreach (var warning in warnings)
                sb.Append($"  NOTE: {warning}\n");
            sb.Append($"Rebuild queued on {queued} loaded outdoor landblock(s).\n");
            sb.Append(ProjectCounts(updated));

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
        }

        /// <summary>
        /// What the region will hold once the queued rebuilds run, over the loaded landblocks it covers.
        ///
        /// PROJECTED, not read back: the rebuilds run asynchronously on each landblock's own action queue,
        /// so there is nothing true to read at the moment this reply is written. It is computed by calling
        /// the same pure layout functions the spawner will call, which makes it exact for everything
        /// except the water test - that one needs the landblock's terrain and so is left out, which can
        /// only matter while over_water is 0. `/sky-decor info` reports the real numbers afterwards.
        /// </summary>
        private static string ProjectCounts(SkyDecorRegion region)
        {
            var blocks = 0;
            var candidates = 0;
            var clouds = 0;

            foreach (var landblock in LandblockManager.GetLoadedLandblocks())
            {
                if (landblock.IsDungeon)
                    continue;

                Position.ParseInstanceID(landblock.Instance, out _, out var realmId, out _);

                if (!region.Covers(landblock.Id.Landblock, realmId))
                    continue;

                var own = SkyDecorLayout.PlanCandidates(region, landblock.Id.Landblock);

                blocks++;
                candidates += own.Count;
                clouds += SkyDecorLayout.Cull(region, landblock.Id.Landblock, own).Count;
            }

            if (blocks == 0)
                return "No loaded landblock is covered by this region, so nothing visible changes until one loads.";

            var line = $"Projected: {clouds} cloud(s) of {candidates} candidate(s) over {blocks} loaded block(s).";

            if (!region.OverWater)
                line += " (water cull not included in this projection)";

            return line;
        }

        // ===========================================================================================
        // show
        // ===========================================================================================

        private static void HandleShow(Session session, string[] parameters)
        {
            var regionName = parameters.Length >= 2 ? parameters[1] : null;

            if (!TryResolveRegion(session, regionName, out var region))
                return;

            var fromDatabase = SkyDecorOverrides.DatabaseRegion(region.Name);

            var sb = new StringBuilder();

            var overriddenColumns = SkyDecorOverrides.OverriddenColumns(region.Name);

            sb.Append($"{region.Name} - realm {region.RealmId}, blocks 0x{region.LbXMin:X2}-0x{region.LbXMax:X2} x 0x{region.LbYMin:X2}-0x{region.LbYMax:X2}\n");
            sb.Append(overriddenColumns.Count > 0
                ? $"{overriddenColumns.Count} runtime override(s) active - marked *, database value in parentheses. /sky-decor reload drops them.\n"
                : "No runtime overrides; every value below is the database row.\n");

            foreach (var column in SkyDecorOverrides.Columns)
            {
                var effective = SkyDecorOverrides.Read(region, column);

                if (overriddenColumns.Contains(column) && fromDatabase != null)
                    sb.Append($"  * {column} = {effective}   (db: {SkyDecorOverrides.Read(fromDatabase, column)})\n");
                else
                    sb.Append($"    {column} = {effective}\n");
            }

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
        }

        // ===========================================================================================
        // sql
        // ===========================================================================================

        private static void HandleSql(Session session, string[] parameters)
        {
            var regionName = parameters.Length >= 2 ? parameters[1] : null;

            if (!TryResolveRegion(session, regionName, out var region))
                return;

            var sb = new StringBuilder();

            sb.Append($"Current effective values for {region.Name}, as one statement. Paste it into\n");
            sb.Append("Content/realms/<unit>.sql (or run it against ace_world) to make the tuning permanent:\n\n");
            sb.Append(SkyDecorOverrides.BuildUpdateSql(region));

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
        }

        // ===========================================================================================
        // colours
        // ===========================================================================================

        /// <summary>
        /// The colour names a palette accepts, and the wcid behind each. Exists so the owner never has to
        /// look a wcid up: `/sky-decor set palette purple,red,orange` is the whole workflow.
        /// </summary>
        private static void HandleColours(Session session)
        {
            var sb = new StringBuilder();

            sb.Append("Sky Rift colours - use these names in a palette, with an optional :weight\n");
            sb.Append("(e.g. /sky-decor set palette purple,red:2,orange). Raw wcids still work.\n\n");

            foreach (var colour in SkyDecorColours.All)
            {
                var name = colour.Alias == null ? colour.Name : $"{colour.Name} / {colour.Alias}";

                sb.Append($"  {name,-16} {colour.WeenieClassId}   {(colour.Retired ? "RETIRED - " + colour.Note : "ok")}\n");
            }

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
        }

        // ===========================================================================================
        // reload
        // ===========================================================================================

        private static void HandleReload(Session session)
        {
            var dropped = SkyDecorOverrides.ClearAll();

            DatabaseManager.World.ClearSkyDecorRegionCache();

            var regions = SkyDecorOverrides.EffectiveRegions();

            var queued = QueueRebuildOnLoadedLandblocks();

            var sb = new StringBuilder();

            sb.Append($"Sky decor reloaded: {regions.Count} enabled region(s), rebuild queued on {queued} loaded outdoor landblock(s).");

            if (dropped > 0)
                sb.Append($"\nDropped runtime overrides on {dropped} region(s) - the sky is back to what sky_decor_region says.");

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
        }

        /// <summary>
        /// Queues the idempotent destroy-and-respawn on every loaded outdoor landblock.
        ///
        /// EVERY outdoor block, not only the ones a given region covers, because a coverage-changing edit
        /// (or a region switched off) has to be able to clear clouds out of a block that is no longer
        /// covered - and that block would be invisible to a covered-only sweep.
        /// </summary>
        private static int QueueRebuildOnLoadedLandblocks()
        {
            var queued = 0;

            foreach (var landblock in LandblockManager.GetLoadedLandblocks())
            {
                if (landblock.IsDungeon)
                    continue;

                // Queued onto each landblock's own action queue: the destroy and the respawn both run on
                // the thread that owns those objects, and the respawn reads the region set AFTER the
                // destroy, so a block can never end up holding two generations of the same region's clouds.
                landblock.ReloadSkyDecor();

                queued++;
            }

            return queued;
        }

        // ===========================================================================================
        // info
        // ===========================================================================================

        private static void HandleInfo(Session session)
        {
            var player = session?.Player;

            if (player?.CurrentLandblock == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "/sky-decor info needs a player in the world.");
                return;
            }

            var landblock = player.CurrentLandblock;

            Position.ParseInstanceID(landblock.Instance, out _, out var realmId, out _);

            var sb = new StringBuilder();

            sb.Append($"Landblock 0x{landblock.Id.Landblock:X4} realm {realmId} - {(landblock.IsDungeon ? "DUNGEON (sky decor never spawns here)" : "outdoors")}");
            sb.Append(SkyDecorOverrides.AnyActive ? " (overrides active)\n" : "\n");

            var counts = landblock.GetSkyDecorCounts();

            sb.Append($"Sky decor here: {counts.Live} live object(s); last rebuild planned {counts.PlannedClouds} cloud(s) of {counts.PlannedCandidates} candidate(s) -> {counts.PlannedObjects} object(s)\n");

            // Candidates minus clouds is what min_separation and the water test removed between them,
            // summed over every covering region. Worth printing because the two dials that decide it -
            // density and min_separation - pull against each other, and the only way to see where they
            // have landed is the gap.
            if (counts.PlannedCandidates > counts.PlannedClouds)
                sb.Append($"  {counts.PlannedCandidates - counts.PlannedClouds} candidate(s) culled (min_separation and/or water)\n");

            if (counts.Live != counts.PlannedObjects)
                sb.Append($"  NOTE: {counts.PlannedObjects - counts.Live} planned object(s) are not live - check the log for spawn failures\n");

            var covering = SkyDecorOverrides.EffectiveRegions()
                .Where(r => r.Covers(landblock.Id.Landblock, realmId))
                .ToList();

            if (covering.Count == 0)
            {
                sb.Append("No enabled sky_decor_region covers this landblock in this realm.");
            }
            else
            {
                foreach (var region in covering)
                {
                    var overriddenColumns = SkyDecorOverrides.OverriddenColumns(region.Name);

                    sb.Append($"\n[{region.Id}] {region.Name} - realm {region.RealmId}, blocks 0x{region.LbXMin:X2}-0x{region.LbXMax:X2} x 0x{region.LbYMin:X2}-0x{region.LbYMax:X2}");
                    sb.Append(overriddenColumns.Count > 0 ? $" (overrides: {string.Join(", ", overriddenColumns)})\n" : "\n");
                    sb.Append($"  seed {region.Seed} v{region.Version} -> block hash 0x{SkyDecorLayout.StableHash(region.Seed, region.Version, region.RealmId, landblock.Id.Landblock):X8}\n");
                    sb.Append($"  density {region.Density:0.###} cloud(s)/block, scale {region.ScaleMin:0.##}-{region.ScaleMax:0.##}, height {region.HeightMin:0.##}-{region.HeightMax:0.##} m\n");
                    sb.Append($"  palette {SkyDecorColours.Format(region.Palette)}\n");
                    sb.Append($"  tilt max {region.TiltMaxDeg:0.##} deg, spin {region.SpinMin:0.##}-{region.SpinMax:0.##}, part offset {region.PartOffset:0.###}, over water {(region.OverWater ? "yes" : "no")}\n");
                    sb.Append($"  pairing {(region.PairChance > 0 ? $"{region.PairChance:0.##} chance, partner scale x{region.PairScaleRatio:0.##}, gap {region.PairGap:0.##} m, speed x{region.PairSpeedRatio:0.####}" : "off")}\n");
                    sb.Append($"  rig {(SkyDecorLayout.IsMast(region.Rig) ? "mast (disc above origin, origin 1 m over ground)" : "inverted (disc below origin)")}\n");
                    sb.Append($"  min separation {(region.MinSeparation > 0 ? $"x{region.MinSeparation:0.##} of summed disc radii, searching {SkyDecorLayout.NeighbourBlockRadius(region)} block(s) out" : "off")}\n");
                }
            }

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
        }
    }
}
