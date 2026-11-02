using System;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /hazard: developer test bench for the hazard mechanics library (ACE.Server.HazardMechanics). Everything it
    /// places is transient and in memory: nothing is written to any database, and a restart or a landblock unload
    /// removes it. Every subcommand reaps dead runs first.
    /// </summary>
    public static class HazardCommands
    {
        private static string Usage =>
            "Usage:\n" +
            $"  /hazard <{string.Join("|", HazardMechanicRegistry.MechanicKinds)}> [pattern] [preset=<{string.Join("|", HazardRegionPresets.Names)}> | region=circle:<r> | region=rect:<w>x<h>] [hz=<m>] [seed=<n>] [key=value ...]\n" +
            "  /hazard balls [ring-out|ring-in|row|random|cross|fan|ring-alt]\n" +
            "      args: target=random|nearest spread=<deg> dir=n|s|e|w|random dirs=ne|nw|se|sw|random count=<n> speed=<m/s> gap=<m> lead=<s> waves=<n> wave_gap=<s> window=<s> ball_z=<m> ball_radius=<m> escape_speed=<m/s> marker=<bool> cx=<m> cy=<m> | at=me\n" +
            "      args+: speed_end=<m/s> lead_end=<s> wave_gap_end=<s> (ramp the start key to this over the waves) stream=<s> (random only: one wave of count launches over s seconds; speed ramps per launch)\n" +
            "      args+: ray_markers=1|0 (rings only, default 0: pillars along each ray during the lead)\n" +
            "      args+: wave_every=<s> wave_every_end=<s> (cadence: each wave launches s after the one before, never waiting for its balls to clear; waves overlap, wave_gap unused)\n" +
            "      args+: gaps=<n> (row, cross: n openings per wall, each gap wide, at random with a ball-width of wall between and at the ends; default 1)\n" +
            "      args+: max_in_flight=<n> max_in_flight_end=<n> (with wave_every: a wave waits while n earlier waves are still in flight; ramped, rounded)\n" +
            "      args+: damage_type=fire|acid|cold|electric|bludgeon (default fire; any other word is refused)\n" +
            "      args+: marker_script=<script id, decimal or 0x hex> (the look of every marker pillar; default the fire pillar)\n" +
            "      args+: max_balls=<n> (with wave_every: a due wave launches when nothing is in flight or the balls in flight plus its size fit n, else it waits)\n" +
            "      args+: fair_lead=1|0 (default 1; 0 drops the fair-lead floor and the fan's sidestep floor, lead may be 0: the balls move the moment they appear)\n" +
            "      args+: origin_clear=<m> (fan, rings, default 0: fan and ring-out balls start m out from the origin, ring-in balls stop m short of it)\n" +
            "      args+: no_overlap=1|0 (row, cross, default 0: wall pitch 2 x reach + 0.02, every wall stretch whole pitches, leftover span widens the openings)\n" +
            "      args+: distinct_dirs=1|0 (row dir=random max_in_flight<=3, cross dirs=random max_in_flight<=2, with wave_every; default 0: waves in flight never share a wall, a wave waits for a free one)\n" +
            "      args+: hit_lag=<s> (default 0.15, 0..0.5; latency compensation: a hit lands s after the server's ball, where the player sees it; 0 is off)\n" +
            "      args+: occupancy=<fraction> (random only, default 0.75, 0.25..2: the share of the narrowest side balls in flight may block; a higher one allows a denser stream)\n" +
            "      ring-out starburst from the centre | ring-in converge on it | row sweeps one gap across | random staggered launches (count over window) | cross two rows, safe where the gaps meet | fan count balls (3) aimed at a player, spread degrees (30);\n" +
            "      ring-alt one ring series whose waves alternate ring-out and ring-in, each with a new random rotation;\n" +
            "      defaults: ring-out, speed=3 lead=2 gap=6 waves=4 wave_gap=3 window=20 escape_speed=3, count 8 (rings) or 6 (random); lead is raised to the fair-lead floor for a player fleeing at escape_speed m/s (1..12); at=me/cx/cy set the ring centre\n" +
            "  /hazard wisps [scatter|row|column|diagonal|ring|sweep|mixed]\n" +
            "      args: n=<wisps> scale=<x> move=<s> hold=<s> burst=<s> rest=<s> cycles=<n> pillar_radius=<m> escape_speed=<m/s> fire=unison|random|sequence\n" +
            "      args+: seq_gap=<s> dir=n|s|e|w|random sweep_move=<s> steps=<n> ring_radius=<m> scatter_cover=<fraction> wisp_clear=<m> cx=<m> cy=<m>\n" +
            "      args+: move_end=<s> hold_end=<s> rest_end=<s> (ramp the start key to this over the cycles) hold_floor=1|0 (0 lifts the hold floor)\n" +
            "      args+: follow=1|0 (one extra wisp trails the first player and bursts a pillar on the crew's beat) follow_lag=<s>\n" +
            "      args+: mark_dest=1|0 (mark each wisp's destination on the ground from its glide until its burst) strike_delay=<s> (a pillar shows this long before it can hit, default 0.2)\n" +
            "      args+: ceiling_beam=<seconds 0..5, default 0 = off> (a narrow beam falls from the ceiling onto each wisp's pillar spot for this long before it bursts; visual only)\n" +
            "      args+: beam_script=<script id, decimal or 0x hex> (the look of the ceiling beam; default the beam script)\n" +
            "      args+: mark_script=<script id, decimal or 0x hex> (the look of the mark_dest ground mark; default the beam mark)\n" +
            "      args+: damage_type=fire|acid|cold|electric|bludgeon (default fire; any other word is refused)\n" +
            "      row runs west-east (dir n|s), column north-south (dir e|w), sweep takes any dir; hold = the telegraph (seconds the wisps sit still before bursting, at least 0.5 + (pillar_radius + 0.5) / escape_speed);\n" +
            "      defaults: scatter, n=8 move=2.5 hold=1.5 burst=1.5 rest=1 cycles=8 pillar_radius=2 escape_speed=3 fire=unison; pillar_radius is the DAMAGE radius, not the flames' size (the effect script fixes that); cx/cy = landblock-local centre of ring and diagonal\n" +
            "  /hazard memory   (no patterns)\n" +
            "      args: layout=quadrant|random len=<n> max_len=<n> beams_per_pulse=<n> beam_from=ceiling|floor beam_flip=1|0 beam_corners=1|0 pad_pillar=1|0 pct=<fraction> pulse_on=<s> pulse_off=<s> answer_timeout=<s> pad_radius=<m> pad_gap=<m> pad_spacing=<m>\n" +
            "      four coloured pads; the room is shown a fresh random sequence of beams each round (one colour longer per round) and answers by walking onto the pads; deals no damage unless pad_pillar=1, which raises a fire pillar on the shown colour's pad that hits for pct of max health (default 0.60); /hazard reveal shows the current round's sequence; ceiling beams need a region with a ceiling;\n" +
            "      defaults: quadrant, len=4 max_len=8 beams_per_pulse=6 beam_from=ceiling beam_flip=0 beam_corners=0 pad_pillar=0 pct=0.60 pulse_on=1 pulse_off=0.5 answer_timeout=20 pad_radius=1 pad_gap=6 pad_spacing=0.6\n" +
            "  /hazard zones [corners]   (rect regions only)\n" +
            "      args: radius=<m> tick=<s> pct=<fraction> pct_end=<fraction> ramp=<s> reset=<s>\n" +
            "      a fire pillar at each region corner; a player within radius of any pillar takes pct of max health on entering and every tick s after, rising to pct_end over ramp s of standing there; reset s outside starts the clock over;\n" +
            "      defaults: corners, radius=3 tick=1 pct=0.05 pct_end=0.35 ramp=5 reset=1; runs until /hazard stop\n" +
            "  /hazard rain   (no patterns; outdoor regions)\n" +
            "      args: speed=<m/s> lead=<s> burst_radius=<m> duration=<s> drop_z=<absolute z> max_falling=<n> marker_script=<id> burst_script=<id> ball_wcid=<wcid> damage_type=fire|acid|cold|electric|bludgeon hit_verb=<word> pct=<fraction> escape_speed=<m/s> player_radius=<m> edge_inset=<m> target_share=<0..1> target_spread=<m>\n" +
            "      balls fall out of the sky onto ground markers at random moments (about max_falling at once), burst on impact and vanish; target_share of the drops are aimed at a random player (landing within target_spread of them), the rest fall anywhere; the marker stands for the whole fall, lead is a short spawn-in raised so lead + fall >= 0.5 + (burst_radius + player_radius) / escape_speed;\n" +
            "      defaults: speed=10 lead=0.6 burst_radius=4 duration=60 drop_z=ground+30 max_falling=8 target_share=0.6 target_spread=5 escape_speed=3 pct=0.60 damage_type=fire\n" +
            "  /hazard fill   (no patterns; a rect room on a plane ground)\n" +
            "      args: element=acid|lava color=acid|lava|smoke|poison damage_type=fire|acid|cold|electric|bludgeon floor_z=<z> ground_z=<z> max_z=<z> start_z=<z> rate=<m/s> tick=<s> damage=<n> damage_per_m=<n> damage_max=<n> damage_scale=<x>\n" +
            "      args+: head_height=<m> panel_size=<m> opacity=<0..1> doubled=1|0 wisps=<n> wisp_scale=<x> resnap=<s> duration=<s> (0 = forever) sheet_margin=<m> under_margin=<m>\n" +
            "      args+: submerged_damage=<n> submerged_per_m=<n> submerged_max=<n> (optional tier at depth >= head_height; damage 0 = off; max 0 = damage_max)\n" +
            "      doubled=1 pairs every surface panel with a down-facing partner so the surface shows from below; at most 64 surface panels in all (doubled counts two per tile);\n" +
            "      a sheet of acid or lava rises from start_z at rate m/s (negative falls) to max_z; everyone below the surface takes damage + damage_per_m x depth (capped at damage_max, x damage_scale) every tick s, cut by their resistance;\n" +
            "      defaults: acid, rate=0.05 tick=2 damage=40 damage_per_m=25 damage_max=400 damage_scale=1 head_height=2 panel_size=10.4 opacity=0.5 doubled=1 wisps=16 wisp_scale=0.5 resnap=10 duration=0;\n" +
            "      floor_z = ground + 0.05, max_z = ceiling - 0.3; /hazard control <id> sets rate, target, set_z, damage_scale, stop\n" +
            "  /hazard fog   (no patterns; a rect room on a plane ground)\n" +
            "      args: color=smoke|acid|lava|poison opacity=<0..1> spacing=<m> height=<m> base_z=<z> doubled=1|0 max_panels=<n> radar=1|0 radar_scan=<s> duration=<s> (0 = forever)\n" +
            "      upright translucent panels in a lattice about spacing m apart, bottoms at base_z; doubled=1 adds a partner facing the other way to each; radar=1 hides every non-player creature inside from the radar, restored when the run ends;\n" +
            "      defaults: smoke, opacity=0.15 spacing=8 height=ceiling - ground max_panels=160 doubled=1 radar=1 radar_scan=1 duration=0; /hazard control <id> sets radar, stop\n" +
            "  /hazard control <id> key=value ...   (sends controls to a running fill or fog: fill takes rate, target, set_z, damage_scale, stop; fog takes radar, stop; repeated keys apply in order)\n" +
            "  /hazard beams   (not built yet: the stub takes no pattern and no args)\n" +
            "  balls and wisps also take pct=<fraction of max health per hit, default 0.60> player_radius=<m>; every mechanic takes edge_inset=<m> (memory deals no damage)\n" +
            "  /hazard move [preset=..|region=..] [case=1..5] [speed=4] [hold=2] [lead=1] [hit=0]   (movement gate: 5 announced cases, log tag move-case=N)\n" +
            "  /hazard show [preset=..|region=..] [seconds=10] [edge_inset=2]   (marks the region's corners/rim, inset, and centre)\n" +
            "  /hazard list\n" +
            "  /hazard stop [<id>|all]    (no id = every run on your landblock)\n" +
            "  /hazard reveal [<id>]      (no id = the newest run on your landblock)\n" +
            "No preset/region: this landblock's preset if it has one, else a 15 m circle around you.\n" +
            "Ad-hoc regions: dungeon ground = your z, outdoors = terrain; hazards fly at ground + hz.";

        [CommandHandler("hazard", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Hazard mechanics: start a hazard, move/show tests, list, stop, reveal.",
            "<wisps|balls|beams|memory|zones|rain|fill|fog|move|show> [pattern] [preset=..|region=..] [k=v ...] | list | stop [<id>|all] | reveal [<id>] | control <id> k=v ...\n\n" +
            "Starts a hazard run on your landblock. Damage is ceil(pct x MaxHealth) per hit (pct default 0.60); admins are\n" +
            "usually invincible, so test damage on a non-admin character.")]
        public static void HandleHazard(Session session, params string[] parameters)
        {
            var admin = session?.Player;

            if (admin == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "/hazard needs a player in the world.");
                return;
            }

            var reaped = HazardManager.Reap();

            if (reaped > 0)
                Write(session, $"Reaped {reaped} dead hazard run(s).");

            var sub = parameters[0].ToLowerInvariant();
            var rest = parameters.Skip(1).ToList();

            switch (sub)
            {
                case "list":
                    List(session);
                    return;

                case "stop":
                    Stop(session, admin, rest);
                    return;

                case "reveal":
                    Reveal(session, admin, rest);
                    return;

                case "control":
                    Control(session, rest);
                    return;

                case "help":
                case "?":
                    Write(session, Usage);
                    return;
            }

            if (!HazardMechanicRegistry.Kinds.Contains(sub, StringComparer.OrdinalIgnoreCase))
            {
                Write(session, $"/hazard: unknown subcommand '{sub}'.\n\n{Usage}");
                return;
            }

            if (!HazardCommandRequest.TryParse(parameters, out var request, out var error))
            {
                Write(session, $"/hazard {sub}: {error}\n\n{Usage}");
                return;
            }

            var landblock = admin.CurrentLandblock;
            var location = admin.Location;

            if (landblock == null || location == null)
            {
                Write(session, "/hazard needs you in the world.");
                return;
            }

            if (landblock.Id.Landblock != location.LandblockId.Landblock || landblock.Instance != location.Instance)
            {
                Write(session, "Your landblock is changing; try again in a moment.");
                return;
            }

            if (!request.TryResolveRegion(landblock.Id.Landblock, landblock.Instance, !landblock.IsDungeon, location.Pos, out var region, out error))
            {
                Write(session, $"/hazard {sub}: {error}");
                return;
            }

            var run = HazardManager.Start(region, request.Kind, request.ArgsFor(location.Pos), new AdminHazardHost(admin.Guid.Full), request.Seed, request.Pattern, landblock, out error);

            if (run == null)
            {
                Write(session, $"/hazard {sub}: refused - {error}");
                return;
            }

            Write(session, string.Format(CultureInfo.InvariantCulture, "Hazard #{0} {1}{2} queued seed={3} on {4}. args: {5}",
                run.Id, run.Kind, run.Pattern != null ? " " + run.Pattern : "", run.Seed, region.Describe(), run.Args.Count == 0 ? "(none)" : run.Args.ToString()));

            if (run.Mechanic is StubHazardMechanic)
                Write(session, $"NOTE: '{run.Kind}' is NOT BUILT YET - it spawns nothing. Only /hazard show and /hazard move are live right now.");
        }

        private static void List(Session session)
        {
            var runs = HazardManager.List();

            if (runs.Count == 0)
            {
                Write(session, "No hazard runs.");
                return;
            }

            foreach (var run in runs)
                Write(session, run.Summary());
        }

        private static void Stop(Session session, Player admin, System.Collections.Generic.List<string> rest)
        {
            if (rest.Count > 1)
            {
                Write(session, "/hazard stop: expected an id, 'all', or nothing.");
                return;
            }

            if (rest.Count == 1 && string.Equals(rest[0], "all", StringComparison.OrdinalIgnoreCase))
            {
                Write(session, $"Stopped {HazardManager.StopAll(null, HazardOutcome.Stopped, $"/hazard stop all by {admin.Name}")} hazard run(s).");
                return;
            }

            if (rest.Count == 1)
            {
                if (!int.TryParse(rest[0].TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    Write(session, $"/hazard stop: '{rest[0]}' is not an id.");
                    return;
                }

                var run = HazardManager.Find(id);

                if (run == null)
                {
                    Write(session, $"No hazard run #{id}. /hazard list shows them.");
                    return;
                }

                Write(session, run.Stop($"/hazard stop by {admin.Name}") ? $"Stopped hazard #{id}." : $"Hazard #{id} had already ended.");
                return;
            }

            var lb = admin.Location;
            var count = HazardManager.StopAll(r => r.Region.Landblock == lb.LandblockId.Landblock && r.Region.Instance == lb.Instance,
                HazardOutcome.Stopped, $"/hazard stop by {admin.Name}");

            Write(session, $"Stopped {count} hazard run(s) on this landblock.");
        }

        private static void Control(Session session, System.Collections.Generic.List<string> rest)
        {
            if (!HazardControlRequest.TryParse(rest, out var id, out var controls, out var error))
            {
                Write(session, $"/hazard control: {error}");
                return;
            }

            var run = HazardManager.Find(id);

            if (run == null)
            {
                Write(session, $"No hazard run #{id}. /hazard list shows them.");
                return;
            }

            if (!(run.Mechanic is IHazardControllable controllable))
            {
                Write(session, $"run #{id} ({run.Kind}) takes no controls");
                return;
            }

            foreach (var control in controls)
            {
                if (!controllable.ControlKeys.Contains(control.Key))
                {
                    Write(session, $"run #{id} ({run.Kind}) has no control '{control.Key}'; keys: {string.Join(", ", controllable.ControlKeys)}");
                    return;
                }
            }

            var sent = controls.Count(c => run.Control(c.Key, c.Value));

            Write(session, sent == controls.Count
                ? $"Sent {sent} control(s) to hazard #{id}."
                : $"Sent {sent} of {controls.Count} control(s) to hazard #{id}; the rest were refused (run ended or its inbox is full).");
        }

        private static void Reveal(Session session, Player admin, System.Collections.Generic.List<string> rest)
        {
            HazardRun run;

            if (rest.Count == 1)
            {
                if (!int.TryParse(rest[0].TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || (run = HazardManager.Find(id)) == null)
                {
                    Write(session, $"No hazard run '{rest[0]}'. /hazard list shows them.");
                    return;
                }
            }
            else
            {
                var lb = admin.Location;
                run = HazardManager.List().LastOrDefault(r => r.Region.Landblock == lb.LandblockId.Landblock && r.Region.Instance == lb.Instance);

                if (run == null)
                {
                    Write(session, "No hazard run on this landblock.");
                    return;
                }
            }

            string answer;

            try
            {
                answer = run.Mechanic.Reveal(run.Context);
            }
            catch (Exception ex)
            {
                answer = "reveal threw " + ex.GetType().Name;
            }

            Write(session, $"#{run.Id} {run.Kind}: {answer ?? "nothing hidden"}");
        }

        private static void Write(Session session, string text) => CommandHandlerHelper.WriteOutputInfo(session, text);
    }

    /// <summary>
    /// The /hazard command's host: tells the admin who started the run how it ended. Looks the admin up by guid at
    /// the moment of the message (they may have logged out); never holds a Player reference.
    /// </summary>
    internal sealed class AdminHazardHost : IHazardHost
    {
        private readonly uint adminGuid;

        public AdminHazardHost(uint adminGuid)
        {
            this.adminGuid = adminGuid;
        }

        public void OnEnded(HazardRun run, HazardOutcome outcome)
        {
            var admin = PlayerManager.GetOnlinePlayer(adminGuid);
            var reason = string.IsNullOrEmpty(run.EndReason) ? "" : $" ({run.EndReason})";

            admin?.Session?.Network.EnqueueSend(new GameMessageSystemChat($"Hazard #{run.Id} {run.Kind} ended: {outcome}{reason}.", ChatMessageType.Broadcast));
        }
    }
}
