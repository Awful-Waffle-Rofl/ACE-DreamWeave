using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Admin command family for World Events (TECH-DESIGN 2.14, 5.3). One dispatcher, one switch over
    /// parameters[0], matching the ClassAbilityCommands / ProvingGroundsAdminCommands precedent. Every
    /// mutating subcommand (start/stop/reload) writes an audit line via
    /// PlayerManager.BroadcastToAuditChannel. Nothing here ever throws out of the handler - the outer
    /// try/catch turns any escaping exception into a chat reply.
    ///
    /// WP-04 owns WorldEventAnnouncer.Audit(Player, string); this file predates it and must not depend on
    /// it, so audit lines go straight through PlayerManager.BroadcastToAuditChannel.
    /// </summary>
    public static class WorldEventCommands
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // TECH-DESIGN 5.3, one line per subcommand collapsed onto one usage string per CommandHandlerAttribute.
        private const string Usage =
            "start random [--here | --anchor <name>] [--source <id>] [--family <id>[,<id2>]] [--goal <id>] [--boss <id>] [--dry] [--teaser <seconds>] (rolls whichever axes are not pinned by a flag; an unpinned family rolls a PAIR) | " +
            "start <preset> [--here | --anchor <name>] [--source <id>] [--family <id>[,<id2>]] [--boss <id> (required for kill_boss; auto = a named boss bound to either composed family, random named boss if none bound)] [--goal <id>] [--reward <id>] [--min-duration <seconds>] [--teaser <seconds>] [--dry] | " +
            "stop [fail|abort] | " +
            "status | " +
            "list [axes|families|presets] | " +
            "reload | " +
            "properties [substring] (every world_events_ server property, one chat line each; '*' marks a non-default value) | " +
            "simulate [--players <n>] [--level <L>] [--source <id>] [--family <id>[,<id2>]] [--goal <id>] | " +
            "scene <sourceId> [--players <n>] [--yaw <deg>] | scene clear [<n>] | scene list";

        private const string DisabledMessage = "World events are disabled (world_events_enabled).";

        [CommandHandler("worldevent", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Start, stop and inspect World Events", Usage)]
        public static void HandleWorldEvent(Session session, params string[] parameters)
        {
            try
            {
                if (parameters == null || parameters.Length == 0)
                {
                    Reply(session, Usage);
                    return;
                }

                var subcommand = parameters[0].ToLowerInvariant();
                var rest = parameters.Skip(1).ToArray();

                switch (subcommand)
                {
                    case "start":
                        HandleStart(session, rest);
                        break;

                    case "stop":
                        HandleStop(session, rest);
                        break;

                    case "status":
                        Reply(session, WorldEventManager.StatusText());
                        break;

                    case "list":
                        HandleList(session, rest);
                        break;

                    case "reload":
                        HandleReload(session);
                        break;

                    case "properties":
                        HandleProperties(session, rest);
                        break;

                    case "simulate":
                        HandleSimulate(session, rest);
                        break;

                    case "scene":
                        HandleScene(session, rest);
                        break;

                    default:
                        Reply(session, Usage);
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] worldevent command threw", ex);
                Reply(session, $"worldevent: {ex.Message}");
            }
        }

        private static void HandleStart(Session session, string[] args)
        {
            if (!WorldEventManager.Enabled)
            {
                Reply(session, DisabledMessage);
                return;
            }

            if (!TryParseStartArgs(args, out var parsed, out var parseError))
            {
                Reply(session, $"worldevent start: {parseError}");
                return;
            }

            if (!string.IsNullOrEmpty(parsed.Preset))
            {
                Reply(session, "presets arrive with P2; pass every axis explicitly (--source --family --goal [--boss] [--reward]), or use \"start random\"");
                return;
            }

            if (session?.Player == null)
            {
                Reply(session, "this subcommand needs an in-game session");
                return;
            }

            string anchorId = null;
            Position anchorPosition = null;
            string anchorLabel = null;

            if (parsed.Here)
            {
                // A NEW Position copy (keeps Instance) - the composition must not alias the live player's
                // mutable Location.
                anchorPosition = new Position(session.Player.Location);
                // Nearest Town Network town plus map coordinates, e.g. "Holtburg (43.2N, 34.0E)":
                // players navigate by town, not by coordinate.
                anchorLabel = WorldEventTownIndex.Describe(session.Player.Location);
            }
            else
            {
                // Mutual exclusivity with --here is already enforced by TryParseStartArgs; reaching here
                // means parsed.AnchorId is set. Named anchors are a P2 feature (anchors.json ships empty in
                // v1), so this only succeeds if a later WP or a hand-authored anchors.json populated one.
                if (!WorldEventManager.Store.Anchors.ContainsKey(parsed.AnchorId))
                {
                    Reply(session, "named anchors arrive with P2; pass --here instead");
                    return;
                }

                anchorId = parsed.AnchorId;
            }

            var request = new WorldEventRequest
            {
                SourceId = parsed.SourceId,
                FamilyIds = new List<string>(parsed.FamilyIds),
                BossId = parsed.BossId,
                GoalId = parsed.GoalId,
                RewardId = parsed.RewardId,
                AnchorId = anchorId,
                AnchorPosition = anchorPosition,
                AnchorLabel = anchorLabel,
                Invoker = session.Player.Name,
                DryRun = parsed.Dry,
                MinDurationSeconds = parsed.MinDurationSeconds,
                Random = parsed.Random,
                TeaserLeadSeconds = parsed.TeaserLeadSeconds
            };

            // Identifies a randomly composed run in the reply/audit text - "[WORLDEVENT] compose" log
            // lines and ToAxisSummary() stay a fixed format (TECH-DESIGN 5.2, monitoring reads them), so
            // the tag is added here rather than to the composition itself.
            var randomTag = parsed.Random ? "[random] " : string.Empty;

            // Checked here as well as in TryStart, because "--dry" returns before TryStart ever runs and a
            // dry run that silently accepted a rejected minimum would be worse than useless.
            if (!WorldEvent.ValidateDurations(request.MinDurationSeconds, request.MaxDurationSeconds, out var durationError))
            {
                Reply(session, $"worldevent start: {durationError}");
                return;
            }

            if (parsed.Dry)
            {
                if (!WorldEventManager.TryCompose(request, out var composition, out var composeError))
                {
                    Reply(session, composeError);
                    return;
                }

                Reply(session, $"{randomTag}{composition.ToAxisSummary()}, dry run, nothing started");
                return;
            }

            if (!WorldEventManager.TryStart(request, out var evt, out var startError))
            {
                Reply(session, startError);
                return;
            }

            var began = evt.TeaserLeadSeconds > 0
                ? $"teaser out, staging in {evt.TeaserLeadSeconds}s"
                : "started";

            Reply(session, $"{randomTag}World event run {evt.RunId} {began}: {evt.Composition.ToAxisSummary()}");
            Audit(session, $"{randomTag}{session.Player.Name} started world event run {evt.RunId} ({evt.Composition.ToAxisSummary()})");
        }

        private static void HandleStop(Session session, string[] args)
        {
            if (!WorldEventManager.Enabled)
            {
                Reply(session, DisabledMessage);
                return;
            }

            var mode = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "abort";

            if (mode != "abort" && mode != "fail")
            {
                Reply(session, Usage);
                return;
            }

            if (session?.Player == null)
            {
                Reply(session, "this subcommand needs an in-game session");
                return;
            }

            var current = WorldEventManager.Current;

            if (current == null || current.State == WorldEventState.Done)
            {
                Reply(session, "no world event is running");
                return;
            }

            var runId = current.RunId;
            var invoker = session.Player.Name;

            if (mode == "fail")
            {
                // There is no admin-specific "Failed" outcome in the fixed WorldEventOutcome enum
                // (TECH-DESIGN 5.1), so "fail" reuses FailedTimeout. That is deliberate: FailedTimeout is
                // the outcome that pays the CONSOLATION reward instead of destroying everything with
                // nothing paid out, which is the whole reason "fail" exists as distinct from "abort".
                WorldEventManager.StopCurrent(WorldEventOutcome.FailedTimeout, invoker);

                Reply(session, $"world event run {runId} stopped as FAILED (outcome=FailedTimeout) - this pays the consolation reward rather than aborting with nothing.");
                Audit(session, $"{invoker} stopped world event run {runId} as FAILED (consolation payout, outcome=FailedTimeout)");
            }
            else
            {
                WorldEventManager.StopCurrent(WorldEventOutcome.AbortedAdmin, invoker);

                Reply(session, $"world event run {runId} aborted (outcome=AbortedAdmin).");
                Audit(session, $"{invoker} aborted world event run {runId} (outcome=AbortedAdmin)");
            }
        }

        private static void HandleList(Session session, string[] args)
        {
            var mode = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "axes";

            switch (mode)
            {
                case "axes":
                    Reply(session, BuildAxesListing());
                    break;

                case "families":
                    Reply(session, BuildFamiliesListing());
                    break;

                case "presets":
                    Reply(session, "no presets in P1");
                    break;

                default:
                    Reply(session, Usage);
                    break;
            }
        }

        private static void HandleReload(Session session)
        {
            var summary = WorldEventManager.Reload();

            Reply(session, summary);
            Audit(session, $"{session?.Player?.Name ?? "console"} reloaded World Events axes: {summary}");
        }

        /// <summary>
        /// "/worldevent properties [substring]" - every world_events_ server property (TECH-DESIGN "Live
        /// overrides"), one chat line each so the client does not truncate a combined block. Sorted by key,
        /// with a leading '*' on any line whose current value differs from its default - the live-override
        /// dials default to 0 ("use the JSON value"), so a '*' line is exactly one that is currently
        /// overriding a source/boss.
        /// </summary>
        private static void HandleProperties(Session session, string[] args)
        {
            var filter = args != null && args.Length > 0 ? args[0] : null;

            var rows = PropertyManager.EnumerateWithPrefix("world_events_").ToList();

            if (!string.IsNullOrEmpty(filter))
                rows = rows.Where(r => r.key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (rows.Count == 0)
            {
                Reply(session, string.IsNullOrEmpty(filter) ? "no world_events_ properties found" : $"no world_events_ properties matching '{filter}'");
                return;
            }

            foreach (var row in rows)
            {
                var changed = !string.Equals(row.current, row.@default, StringComparison.Ordinal);
                var mark = changed ? "* " : "";

                Reply(session, $"{mark}{row.key} = {row.current} (default {row.@default}) - {row.description}");
            }
        }

        private static void HandleSimulate(Session session, string[] args)
        {
            if (!TryParseSimulateArgs(args, out var parsed, out var parseError))
            {
                Reply(session, $"worldevent simulate: {parseError}");
                return;
            }

            if (string.IsNullOrEmpty(parsed.SourceId) || parsed.FamilyIds.Count == 0 || string.IsNullOrEmpty(parsed.GoalId))
            {
                Reply(session, "worldevent simulate: --source, --family and --goal are required");
                return;
            }

            if (session?.Player == null)
            {
                Reply(session, "this subcommand needs an in-game session");
                return;
            }

            var levels = Enumerable.Repeat(parsed.Level, parsed.Players).ToList();
            var estimate = WorldEventRosterSelector.EstimateFromLevels(levels);

            var request = new WorldEventRequest
            {
                SourceId = parsed.SourceId,
                FamilyIds = new List<string>(parsed.FamilyIds),
                GoalId = parsed.GoalId,
                BossId = "none",
                RewardId = "standard",
                AnchorPosition = new Position(session.Player.Location),
                AnchorLabel = "simulation",
                Invoker = session.Player.Name,
                DryRun = true
            };

            if (!WorldEventManager.TryCompose(request, out var composition, out var composeError))
            {
                Reply(session, composeError);
                return;
            }

            var lines = new List<string>
            {
                composition.ToAxisSummary(),
                $"estimate {estimate}"
            };

            var theme = composition.Source;

            // Every preview draw uses the UNION roster, exactly like a live run's SpawnWave/PickChampion do
            // (two-family composition, 2026-08-29). For a one-family simulate this is that family's roster.
            var family = composition.Roster;

            var trashBand = WorldEventRosterSelector.TrashBand(estimate.MedianLevel);
            var eliteBand = WorldEventRosterSelector.EliteBand(estimate.P90Level);

            var catalog = WorldEventManager.Catalog;

            // One line per composed family with its pairing profile, so a two-family preview shows WHY the
            // pair is legal (which half reaches the floor, which half brings the casters) rather than only
            // the merged result.
            foreach (var composedFamily in composition.Families)
            {
                var profile = catalog.Profile(composedFamily.Id);

                lines.Add($"family: {composedFamily.Id} \"{composedFamily.DisplayName}\" " +
                          $"members {profile.MemberCount} levels {profile.MinLevel}-{profile.MaxLevel} " +
                          $"casters {profile.CasterCount}");
            }

            var bandDials = WorldEventRosterSelector.DialSource();
            lines.Add($"band dials: trash x{bandDials.TrashLow:F2}-x{bandDials.TrashHigh:F2}, " +
                      $"elite x{bandDials.EliteLow:F2}-x{bandDials.EliteHigh:F2}, " +
                      $"family floor: {WorldEventFamilyPairing.FloorLevel()}");

            // TECH-DESIGN 2.15: the CROWD half of the health multiplier only. The pace half is a runtime
            // measurement of how fast this particular group is actually clearing waves, so it has no
            // honest value to preview - a simulate line that showed one would be inventing it.
            var crowd = theme.CrowdHealth ?? new CrowdHealthDef();
            var crowdMult = crowd.Resolve(estimate.Count);

            lines.Add($"crowd health: x{crowdMult:F2} at {estimate.Count} participants " +
                      $"(startAt {crowd.EffectiveStartAt}, cap x{crowd.EffectiveCap:F2}); pace multiplier is runtime-only");

            lines.Add(WorldEventOverrides.ActiveOverridesSummary());

            // WP-24: legend for the '*' marker the wave lines below use.
            lines.Add("* = synthetic promotion (role-0 member renamed and health-buffed to fill an empty elite/champion slot)");

            for (var waveIndex = 0; waveIndex < 3; waveIndex++)
            {
                // currentAlive 0 and quantityBonus 0: this previews the pick for a fixed cohort on an EMPTY
                // field, which is the only state a preview can honestly assume.
                var wave = WorldEventRosterSelector.PickWave(family, estimate, theme, waveIndex, currentAlive: 0);

                if (wave.IsEmpty)
                {
                    lines.Add($"wave {waveIndex}: empty");
                    continue;
                }

                // WP-24: a '*' marks a synthetic elite/champion - a role-0 member promoted (renamed, extra
                // health) because the band had no real one - see the legend line appended below.
                var trashDescriptions = wave.Trash.Select((wcid, i) =>
                    ((i == wave.EliteIndex && wave.EliteSynthetic) ? "*" : "") +
                    DescribeMember(family, wcid, trashBand, eliteBand));

                var championDescriptions = wave.Champions.Select((wcid, i) =>
                    $"OVERFLOW {((i < wave.ChampionsSynthetic.Count && wave.ChampionsSynthetic[i]) ? "*" : "")}" +
                    DescribeMember(family, wcid, trashBand, eliteBand));

                var descriptions = trashDescriptions.Concat(championDescriptions);

                // world_events_band_uplift: how many of this wave's slots are drawn from BELOW the natural
                // band and would be normalized up at spawn. Shown only when there are any.
                var uplifted = wave.TrashUplift.Count(u => u > 0) + wave.ChampionsUplift.Count(u => u > 0);
                var upliftNote = uplifted > 0 ? $" [band uplift: {uplifted} of {wave.Total} slot(s)]" : "";

                lines.Add($"wave {waveIndex}: trash {wave.Trash.Count}, champions {wave.Champions.Count}, " +
                          $"health x{crowdMult:F2} (raw {wave.Sizing.Raw}, ceiling {wave.Sizing.Ceiling}, " +
                          $"overflow {wave.Sizing.Overflow}): {string.Join(", ", descriptions)}{upliftNote}");
            }

            var championPick = WorldEventRosterSelector.PickChampion(family, estimate);

            if (championPick.IsNone)
            {
                lines.Add("champion: none - nothing in the elite/champion band and no role-0 member to promote");
            }
            else
            {
                lines.Add($"champion: {(championPick.Synthetic ? "*" : "")}{DescribeMember(family, championPick.Wcid, trashBand, eliteBand)} " +
                          $"- arrives at the minimum duration ({WorldEvent.DefaultMinDurationSeconds}s by default, " +
                          $"--min-duration overrides), not at a fixed wave index" +
                          (championPick.UpliftLevel > 0 ? $" [band uplift to level {championPick.UpliftLevel}]" : ""));
            }

            // WP-14 decor: what the theme would hang over the anchor. One line per entry, in file order,
            // which is the order the layers are placed.
            foreach (var decor in theme.Decor ?? new List<DecorDef>())
            {
                lines.Add($"decor: wcid {decor.Wcid} scale {decor.Scale} speed {decor.Speed} dz {decor.Dz} " +
                          $"yaw {decor.Yaw} {(decor.Inverted ? "inverted" : "upright")}");
            }

            // WP-25 decor styles: how many color variants the theme could pick from at spawn time.
            if (theme.DecorStyles != null && theme.DecorStyles.Count > 0)
                lines.Add($"decorStyles: {theme.DecorStyles.Count}");

            // WP-15 npcs: the attendants the theme would stand around the anchor, in file order. Offsets are
            // metres from the geometry centre (x east, y north, z up) and yaw is the heading they face.
            foreach (var npc in theme.Npcs ?? new List<NpcDef>())
                lines.Add($"npc: wcid {npc.Wcid} at ({npc.Dx}, {npc.Dy}, {npc.Dz}) yaw {npc.Yaw}");

            // Band coverage gaps: detected by comparing every picked member's actual level against the
            // band it should have been drawn from, per TECH-DESIGN 2.14 - not just family membership, since
            // a family can have members outside both bands and still resolve fine via the nearest-member
            // fallthrough in WorldEventRosterSelector.SelectPool.
            //
            // Reported per composed family AND for the union: a gap in one half of a pair is worth seeing
            // even when the other half covers it (that is the half whose members will never be drawn at
            // this audience level), while the union line is what actually decides whether the run has a
            // coverage problem at all.
            foreach (var composedFamily in composition.Families)
            {
                if (!composedFamily.Members.Any(m => m.Role == 0 && trashBand.Contains(m.Level)))
                    lines.Add($"gap: {composedFamily.Id} has no member in trash band [{trashBand.Low}-{trashBand.High}]");

                if (!composedFamily.Members.Any(m => m.Role != 0 && eliteBand.Contains(m.Level)))
                    lines.Add($"gap: {composedFamily.Id} has no member in elite band [{eliteBand.Low}-{eliteBand.High}]");
            }

            if (!family.Members.Any(m => m.Role == 0 && trashBand.Contains(m.Level)))
                lines.Add($"gap: no member in trash band [{trashBand.Low}-{trashBand.High}]");

            if (!family.Members.Any(m => m.Role != 0 && eliteBand.Contains(m.Level)))
                lines.Add($"gap: no member in elite band [{eliteBand.Low}-{eliteBand.High}]");

            Reply(session, string.Join("\n", lines));
        }

        /// <summary>
        /// WP-22: "worldevent scene &lt;sourceId&gt; [--players n] [--yaw deg]" places one theme's decor,
        /// npcs and objectives at the admin's feet without running an event - see WorldEventScenePreview's
        /// class remarks. "worldevent scene clear [n]" and "worldevent scene list" are routed here too since
        /// they share the "scene" subcommand token.
        /// </summary>
        private static void HandleScene(Session session, string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Reply(session, "worldevent scene: a source theme id, \"clear\" or \"list\" is required. " + Usage);
                return;
            }

            var mode = args[0].ToLowerInvariant();

            if (mode == "clear")
            {
                HandleSceneClear(session, args.Skip(1).ToArray());
                return;
            }

            if (mode == "list")
            {
                Reply(session, WorldEventScenePreview.List());
                return;
            }

            if (session?.Player == null)
            {
                Reply(session, "this subcommand needs an in-game session");
                return;
            }

            if (!TryParseSceneArgs(args, out var parsed, out var parseError))
            {
                Reply(session, $"worldevent scene: {parseError}");
                return;
            }

            if (!WorldEventManager.Store.Sources.TryGetValue(parsed.SourceId, out var theme))
            {
                Reply(session, $"worldevent scene: unknown source id \"{parsed.SourceId}\"; known ids: {string.Join(", ", WorldEventManager.Store.Sources.Keys)}");
                return;
            }

            var landblock = session.Player.CurrentLandblock;

            if (landblock == null)
            {
                Reply(session, "worldevent scene: no current landblock");
                return;
            }

            // A NEW Position copy, the same reason HandleStart's --here does it: the placement must not
            // alias the live player's mutable Location.
            var centre = new Position(session.Player.Location);

            var summary = WorldEventScenePreview.Spawn(theme, centre, parsed.Players, parsed.Yaw, landblock);

            Reply(session, summary);
            Audit(session, $"{session.Player.Name} placed {summary}");
        }

        private static void HandleSceneClear(Session session, string[] args)
        {
            int? sceneNumber = null;

            if (args != null && args.Length > 0)
            {
                if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    Reply(session, "worldevent scene clear: expected a scene number");
                    return;
                }

                sceneNumber = n;
            }

            var summary = WorldEventScenePreview.Clear(sceneNumber);

            Reply(session, summary);
            Audit(session, $"{session?.Player?.Name ?? "console"} {summary}");
        }

        private static string DescribeMember(FamilyDef family, uint wcid, LevelBand trashBand, LevelBand eliteBand)
        {
            var member = family?.Members?.FirstOrDefault(m => m.Wcid == wcid);

            if (member == null)
                return $"wcid={wcid}";

            var roleName = member.Role switch
            {
                0 => "trash",
                1 => "elite",
                2 => "champion",
                _ => $"role{member.Role}"
            };

            var band = member.Role == 0 ? trashBand : eliteBand;
            var fallthrough = band.Contains(member.Level) ? "" : " (fallthrough)";

            // Which of the composed families this member came from (two-family composition, 2026-08-29).
            // Without it a two-family wave line is unreadable: the wcids are merged and there is no other
            // way to see which half of the pair a slot was filled from. Blank only for a hand-built member
            // that predates the catalog carrying it.
            var familyTag = string.IsNullOrEmpty(member.FamilyId) ? "" : $" family={member.FamilyId}";

            return $"wcid={member.Wcid} name=\"{member.Name}\" level={member.Level} role={roleName}{fallthrough}{familyTag}";
        }

        private static string BuildAxesListing()
        {
            var store = WorldEventManager.Store;
            var lines = new List<string> { $"axis folder: {store.ResolvedFolder ?? "none"}" };

            foreach (var source in store.Sources.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                lines.Add($"source {source.Id}: {source.DisplayName}, geometry={source.GeometryKind} " +
                          $"interval={source.EffectiveWaveIntervalSeconds}s maxAlive={source.EffectiveMaxAlive} " +
                          $"compatibleGoals={JoinOrNone(source.CompatibleGoals)}");
            }

            foreach (var boss in store.Bosses.Values.OrderBy(b => b.Id, StringComparer.Ordinal))
                lines.Add($"boss {boss.Id}: {boss.DisplayName}");

            foreach (var goal in store.Goals.Values.OrderBy(g => g.Id, StringComparer.Ordinal))
            {
                lines.Add($"goal {goal.Id}: {goal.DisplayName}, type={goal.TypeKind} " +
                          $"count={FormatCount(goal.Count)} mvpRule={goal.RuleKind}");
            }

            foreach (var reward in store.Rewards.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                var poolSuffix = reward.CacheWcids != null && reward.CacheWcids.Count > 0
                    ? $" cachePoolOnSuccess=[{string.Join(",", reward.CacheWcids)}]"
                    : string.Empty;

                // coalWcid is OPTIONAL, so report the inert case in words rather than printing a bare 0 an
                // operator would have to know how to read. This is the only place the participation payout
                // is visible without reading the axis file off disk.
                var coalSuffix = reward.CoalWcid != 0
                    ? $" coal={reward.CoalWcid}"
                    : " coal=none (participation payout inert for this axis)";

                lines.Add($"reward {reward.Id}: {reward.DisplayName}, successCrate={reward.SuccessCrateWcid} " +
                          $"consolationCrate={reward.ConsolationCrateWcid} cacheOnFailure={reward.CacheWcid}{poolSuffix}{coalSuffix}, " +
                          $"gates: char={reward.GateByCharacter} account={reward.GateByAccount} ip={reward.GateByIp}");
            }

            foreach (var anchor in store.Anchors.Values.OrderBy(a => a.Id, StringComparer.Ordinal))
                lines.Add($"anchor {anchor.Id}: {anchor.DisplayName}");

            foreach (var diagnostic in store.Diagnostics)
                lines.Add($"diagnostic: {diagnostic}");

            return string.Join("\n", lines);
        }

        private static string BuildFamiliesListing()
        {
            var store = WorldEventManager.Store;
            var catalog = WorldEventManager.Catalog;

            var ids = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var id in catalog.Families.Keys)
                ids.Add(id);

            foreach (var id in store.Families.Keys)
                ids.Add(id);

            var lines = ids.Select(id => catalog.DescribeCoverage(id)).ToList();

            foreach (var diagnostic in catalog.Diagnostics)
                lines.Add($"diagnostic: {diagnostic}");

            return lines.Count == 0 ? "no families" : string.Join("\n", lines);
        }

        private static string FormatCount(ScaledCount count)
        {
            return count == null ? "n/a" : $"base={count.Base} perParticipant={count.PerParticipant} cap={count.Cap}";
        }

        private static string JoinOrNone(List<string> ids)
        {
            return ids == null || ids.Count == 0 ? "none" : string.Join(",", ids);
        }

        /// <summary>
        /// Null-safe: CommandHandlerFlag.None (deliberately - status/list/reload/simulate must work from
        /// the console per TECH-DESIGN 2.14/5.3) means CommandManager both permits and actually invokes
        /// this handler with session == null for console dispatch. CommandHandlerHelper.WriteOutputInfo
        /// already routes a null session to log.Info instead of a network send, so every reply - success,
        /// error, or the outer catch's "worldevent: &lt;message&gt;" - reaches the console operator instead
        /// of NREing silently.
        /// </summary>
        private static void Reply(Session session, string message)
        {
            CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
        }

        private static void Audit(Session session, string message)
        {
            PlayerManager.BroadcastToAuditChannel(session?.Player, $"[WORLDEVENT] {message}");
        }

        // ---------------------------------------------------------------------------------------------
        // Parser (TECH-DESIGN D6 / 2.14) - pure static, public, no reflection needed for tests.
        // ---------------------------------------------------------------------------------------------

        public class WorldEventStartArgs
        {
            public string Preset { get; set; }

            /// <summary>The leading "random" token (case-insensitive). Mutually exclusive with Preset.</summary>
            public bool Random { get; set; }

            public bool Here { get; set; }
            public string AnchorId { get; set; }
            public string SourceId { get; set; }

            /// <summary>
            /// "--family a" or "--family a,b" (two-family composition, 2026-08-29). Empty when the flag was
            /// not passed, which on "start random" is the signal to ROLL a pair.
            /// </summary>
            public List<string> FamilyIds { get; set; } = new List<string>();

            /// <summary>The first parsed family id, or null. Convenience for callers and tests that only ever deal in one.</summary>
            public string FamilyId => FamilyIds != null && FamilyIds.Count > 0 ? FamilyIds[0] : null;

            public string BossId { get; set; }
            public string GoalId { get; set; }
            public string RewardId { get; set; }
            public bool Dry { get; set; }

            /// <summary>
            /// "--min-duration &lt;seconds&gt;" (TECH-DESIGN 2.15). Defaults to the run default rather than
            /// to 0, so omitting the flag is not the same as asking for no minimum.
            /// </summary>
            public int MinDurationSeconds { get; set; } = WorldEvent.DefaultMinDurationSeconds;

            /// <summary>
            /// "--teaser &lt;seconds&gt;". Null when the flag was not passed, which is the signal to use the
            /// world_events_teaser_lead_seconds tunable (WorldEventRequest.TeaserLeadSeconds).
            /// </summary>
            public int? TeaserLeadSeconds { get; set; }
        }

        public class WorldEventSimulateArgs
        {
            public int Players { get; set; } = 8;
            public int Level { get; set; } = 100;
            public string SourceId { get; set; }

            /// <summary>"--family a" or "--family a,b" (two-family composition, 2026-08-29).</summary>
            public List<string> FamilyIds { get; set; } = new List<string>();

            /// <summary>The first parsed family id, or null. Convenience for callers and tests that only ever deal in one.</summary>
            public string FamilyId => FamilyIds != null && FamilyIds.Count > 0 ? FamilyIds[0] : null;

            public string GoalId { get; set; }
        }

        public class WorldEventSceneArgs
        {
            public string SourceId { get; set; }
            public int Players { get; set; } = 1;
            public float Yaw { get; set; }
        }

        /// <summary>
        /// Parses the arguments to "/worldevent start" (everything after the "start" token itself). Pure,
        /// no engine dependency (D6).
        /// </summary>
        public static bool TryParseStartArgs(string[] args, out WorldEventStartArgs parsed, out string error)
        {
            parsed = null;
            error = null;

            args ??= Array.Empty<string>();

            var result = new WorldEventStartArgs();

            var i = 0;

            if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                if (string.Equals(args[0], "random", StringComparison.OrdinalIgnoreCase))
                    result.Random = true;
                else
                    result.Preset = args[0];

                i = 1;
            }

            var hereSeen = false;
            var anchorSeen = false;

            while (i < args.Length)
            {
                var flag = args[i].ToLowerInvariant();

                switch (flag)
                {
                    case "--here":
                        result.Here = true;
                        hereSeen = true;
                        i++;
                        break;

                    case "--dry":
                        result.Dry = true;
                        i++;
                        break;

                    case "--anchor":
                        if (!TryTakeValue(args, ref i, flag, out var anchorId, out error))
                            return false;
                        result.AnchorId = anchorId.ToLowerInvariant();
                        anchorSeen = true;
                        break;

                    case "--source":
                        if (!TryTakeValue(args, ref i, flag, out var sourceId, out error))
                            return false;
                        result.SourceId = sourceId.ToLowerInvariant();
                        break;

                    case "--family":
                        if (!TryTakeValue(args, ref i, flag, out var familyId, out error))
                            return false;
                        if (!TrySplitFamilyIds(familyId, out var startFamilyIds, out error))
                            return false;
                        result.FamilyIds = startFamilyIds;
                        break;

                    case "--boss":
                        if (!TryTakeValue(args, ref i, flag, out var bossId, out error))
                            return false;
                        result.BossId = bossId.ToLowerInvariant();
                        break;

                    case "--goal":
                        if (!TryTakeValue(args, ref i, flag, out var goalId, out error))
                            return false;
                        result.GoalId = goalId.ToLowerInvariant();
                        break;

                    case "--reward":
                        if (!TryTakeValue(args, ref i, flag, out var rewardId, out error))
                            return false;
                        result.RewardId = rewardId.ToLowerInvariant();
                        break;

                    case "--min-duration":
                        if (!TryTakeValue(args, ref i, flag, out var minDurationRaw, out error))
                            return false;

                        if (!int.TryParse(minDurationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minDuration)
                            || minDuration < 0)
                        {
                            error = "--min-duration needs a whole number of seconds, 0 or more";
                            return false;
                        }

                        // The upper bound is checked against the run's MAXIMUM duration, which lives on the
                        // request rather than in the arg list, so WorldEvent.ValidateDurations owns it and
                        // WorldEventManager.TryStart is where it fires (TECH-DESIGN 2.15).
                        result.MinDurationSeconds = minDuration;
                        break;

                    case "--teaser":
                        if (!TryTakeValue(args, ref i, flag, out var teaserRaw, out error))
                            return false;

                        if (!int.TryParse(teaserRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var teaserLead)
                            || teaserLead < 0)
                        {
                            error = "--teaser needs a whole number of seconds, 0 or more";
                            return false;
                        }

                        if (teaserLead > WorldEvent.MaxTeaserLeadSeconds)
                        {
                            error = $"--teaser cannot exceed {WorldEvent.MaxTeaserLeadSeconds} seconds";
                            return false;
                        }

                        result.TeaserLeadSeconds = teaserLead;
                        break;

                    default:
                        error = $"unknown flag {args[i]}";
                        return false;
                }
            }

            if (hereSeen && anchorSeen)
            {
                error = "use --here or --anchor, not both";
                return false;
            }

            if (!hereSeen && !anchorSeen)
            {
                error = "one of --here or --anchor is required";
                return false;
            }

            parsed = result;
            return true;
        }

        /// <summary>
        /// Parses the arguments to "/worldevent simulate" (everything after the "simulate" token itself).
        /// Pure, no engine dependency (D6).
        /// </summary>
        public static bool TryParseSimulateArgs(string[] args, out WorldEventSimulateArgs parsed, out string error)
        {
            parsed = null;
            error = null;

            args ??= Array.Empty<string>();

            var result = new WorldEventSimulateArgs();

            var i = 0;

            while (i < args.Length)
            {
                var flag = args[i].ToLowerInvariant();

                switch (flag)
                {
                    case "--players":
                        if (!TryTakeValue(args, ref i, flag, out var playersRaw, out error))
                            return false;

                        if (!int.TryParse(playersRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var players) || players <= 0)
                        {
                            error = "--players needs a positive integer";
                            return false;
                        }

                        result.Players = players;
                        break;

                    case "--level":
                        if (!TryTakeValue(args, ref i, flag, out var levelRaw, out error))
                            return false;

                        if (!int.TryParse(levelRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level <= 0)
                        {
                            error = "--level needs a positive integer";
                            return false;
                        }

                        result.Level = level;
                        break;

                    case "--source":
                        if (!TryTakeValue(args, ref i, flag, out var sourceId, out error))
                            return false;
                        result.SourceId = sourceId.ToLowerInvariant();
                        break;

                    case "--family":
                        if (!TryTakeValue(args, ref i, flag, out var familyId, out error))
                            return false;
                        if (!TrySplitFamilyIds(familyId, out var simulateFamilyIds, out error))
                            return false;
                        result.FamilyIds = simulateFamilyIds;
                        break;

                    case "--goal":
                        if (!TryTakeValue(args, ref i, flag, out var goalId, out error))
                            return false;
                        result.GoalId = goalId.ToLowerInvariant();
                        break;

                    default:
                        error = $"unknown flag {args[i]}";
                        return false;
                }
            }

            parsed = result;
            return true;
        }

        /// <summary>
        /// Splits a "--family" value into one or two lowercased ids (two-family composition, 2026-08-29).
        /// Shared by start and simulate so the two can never disagree about what is accepted.
        ///
        /// Refusals, each with its own message: an empty element ("a,", ",b", "a,,b"), the same id twice,
        /// and more than <see cref="WorldEventComposer.MaxComposedFamilies"/> ids. Refused HERE rather than
        /// left to the composer so an admin gets the mistake back before anything is composed, but the
        /// composer checks the same three things anyway - it is reachable from paths that never parse a
        /// command line.
        /// </summary>
        public static bool TrySplitFamilyIds(string raw, out List<string> familyIds, out string error)
        {
            familyIds = new List<string>();
            error = null;

            var parts = (raw ?? "").Split(',');

            foreach (var part in parts)
            {
                var id = part.Trim().ToLowerInvariant();

                if (id.Length == 0)
                {
                    error = "--family takes one or two family ids separated by a comma, with no empty entries";
                    return false;
                }

                if (familyIds.Contains(id, StringComparer.Ordinal))
                {
                    error = $"--family lists '{id}' twice - a run's two families must be different";
                    return false;
                }

                familyIds.Add(id);
            }

            if (familyIds.Count > WorldEventComposer.MaxComposedFamilies)
            {
                error = $"--family takes at most {WorldEventComposer.MaxComposedFamilies} family ids, got {familyIds.Count}";
                familyIds = new List<string>();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parses the arguments to "/worldevent scene &lt;sourceId&gt;" (everything after "scene" itself,
        /// including the source id token - "clear"/"list" are intercepted by HandleScene before this is
        /// called). Pure, no engine dependency (D6).
        /// </summary>
        public static bool TryParseSceneArgs(string[] args, out WorldEventSceneArgs parsed, out string error)
        {
            parsed = null;
            error = null;

            args ??= Array.Empty<string>();

            if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            {
                error = "a source theme id is required";
                return false;
            }

            var result = new WorldEventSceneArgs { SourceId = args[0].ToLowerInvariant() };

            var i = 1;

            while (i < args.Length)
            {
                var flag = args[i].ToLowerInvariant();

                switch (flag)
                {
                    case "--players":
                        if (!TryTakeValue(args, ref i, flag, out var playersRaw, out error))
                            return false;

                        if (!int.TryParse(playersRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var players) || players < 0)
                        {
                            error = "--players needs a non-negative integer";
                            return false;
                        }

                        result.Players = players;
                        break;

                    case "--yaw":
                        if (!TryTakeValue(args, ref i, flag, out var yawRaw, out error))
                            return false;

                        if (!float.TryParse(yawRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw) || !float.IsFinite(yaw))
                        {
                            error = "--yaw needs a finite number";
                            return false;
                        }

                        result.Yaw = yaw;
                        break;

                    default:
                        error = $"unknown flag {args[i]}";
                        return false;
                }
            }

            parsed = result;
            return true;
        }

        private static bool TryTakeValue(string[] args, ref int i, string flag, out string value, out string error)
        {
            value = null;
            error = null;

            if (i + 1 >= args.Length)
            {
                error = $"{flag} needs a value";
                return false;
            }

            value = args[i + 1];
            i += 2;
            return true;
        }
    }
}
