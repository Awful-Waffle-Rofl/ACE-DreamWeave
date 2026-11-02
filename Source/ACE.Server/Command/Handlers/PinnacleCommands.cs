using System;
using System.Globalization;
using System.Linq;

using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Entity.Actions;
using ACE.Server.Network;
using ACE.Server.Pinnacles;

namespace ACE.Server.Command.Handlers
{
    public enum PinnacleCommandKind
    {
        Status,
        Phase,
        Reset,
        Enter,
    }

    /// <summary>The parsed /pinnacle arguments. Pure: no world access, so the parser is unit-testable.</summary>
    public sealed class PinnacleCommandRequest
    {
        public const int MaxPhase = 99;
        public const int MaxLevel = 999;

        public PinnacleCommandKind Kind { get; private set; }

        /// <summary>The target phase for <see cref="PinnacleCommandKind.Phase"/>, else 0.</summary>
        public int Phase { get; private set; }

        /// <summary>The destination kind for <see cref="PinnacleCommandKind.Enter"/>, lower-case, else null.</summary>
        public string PinnacleKind { get; private set; }

        /// <summary>The Knot level for <see cref="PinnacleCommandKind.Enter"/>, else 0.</summary>
        public int Level { get; private set; }

        /// <summary>The per-copy "key=value" overrides after the level on <see cref="PinnacleCommandKind.Enter"/>, else empty.</summary>
        public HazardArgs Overrides { get; private set; } = HazardArgs.Empty;

        /// <summary>Null when the arguments parsed; otherwise the reason they did not.</summary>
        public string Error { get; private set; }

        public bool IsValid => Error == null;

        public static PinnacleCommandRequest Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0 || string.IsNullOrWhiteSpace(parameters[0]))
                return Fail("a subcommand is required (status, phase <n>, reset or enter <kind> <level>)");

            var sub = parameters[0].Trim().ToLowerInvariant();

            switch (sub)
            {
                case "status":
                    return parameters.Length == 1 ? new PinnacleCommandRequest { Kind = PinnacleCommandKind.Status } : Fail("status takes no arguments");

                case "reset":
                    return parameters.Length == 1 ? new PinnacleCommandRequest { Kind = PinnacleCommandKind.Reset } : Fail("reset takes no arguments");

                case "phase":
                    if (parameters.Length != 2)
                        return Fail("phase needs exactly one number, e.g. phase 3");

                    if (!int.TryParse(parameters[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > MaxPhase)
                        return Fail($"'{parameters[1]}' is not a phase number (1 to {MaxPhase})");

                    return new PinnacleCommandRequest { Kind = PinnacleCommandKind.Phase, Phase = n };

                case "enter":
                    if (parameters.Length < 3)
                        return Fail("enter needs a kind and a level, e.g. enter matron 300");

                    var kind = parameters[1].Trim().ToLowerInvariant();

                    if (kind.Length == 0 || !kind.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                        return Fail($"'{parameters[1]}' is not a pinnacle kind");

                    if (!int.TryParse(parameters[2], NumberStyles.None, CultureInfo.InvariantCulture, out var level) || level < 1 || level > MaxLevel)
                        return Fail($"'{parameters[2]}' is not a level (1 to {MaxLevel})");

                    var overrideErrors = new List<string>();
                    var overrides = HazardArgs.FromTokens(parameters.Skip(3), overrideErrors);

                    if (overrideErrors.Count > 0)
                        return Fail(overrideErrors[0]);

                    return new PinnacleCommandRequest { Kind = PinnacleCommandKind.Enter, PinnacleKind = kind, Level = level, Overrides = overrides };

                default:
                    return Fail($"unknown subcommand '{parameters[0]}'");
            }
        }

        private static PinnacleCommandRequest Fail(string error) => new PinnacleCommandRequest { Error = error };
    }

    /// <summary>
    /// /pinnacle: developer control of the Pinnacle fight in the caller's own copy (found by the caller's realm
    /// instance). The fight core is single-threaded on its landblock, so every call is marshalled there.
    /// </summary>
    public static class PinnacleCommands
    {
        public const string Usage = "Usage: /pinnacle status | phase <n> | reset | enter <kind> <level> [key=value ...]  (enter opens a fresh admin copy, or returns you to your held one; the rest act on the copy you stand in)";

        [CommandHandler("pinnacle", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Pinnacle fight admin: status, jump to a phase, reset.",
            "status | phase <n> | reset | enter <kind> <level> [key=value ...]\n\n" +
            "status, phase and reset act on the Pinnacle fight in the copy you stand in. status describes it, phase <n> jumps to that phase\n" +
            "(begins the fight free if it has not begun), reset ends the attempt as lost. enter opens a fresh admin copy of that\n" +
            "kind at that level (or returns you to your held one); it ignores the row's enabled flag, charges no Knot and\n" +
            "records no clear. It is refused while you stand in an instance. Trailing key=value pairs tune that one copy (matron only):\n" +
            "flow_rate finale_rate drain_rate lava_clear_rate flow_escalate_seconds flow_rate_cap damage damage_per_m damage_max\n" +
            "submerged_damage submerged_per_m submerged_max (the tier once acid is head_height, 0.5-10 m, deep; submerged_damage 0 turns it off)\n" +
            "finale_damage_scale max_z fill_floor_z hazard_pct_min hazard_pct_max wall_fx (0 panels, 1 fog, 2 both) fog_acid (0-2) fog_lava (0-3)\n" +
            "flow_delay (seconds from a vent opening to its acid rising, 0-30) beacon_depth (m of acid at which the unused drain shows its beam, 0.25-20) beacon_fx (0-2) return_max_depth (most m of acid a death and return restarts at, 0-20).\n" +
            "An unknown key refuses the entry.")]
        public static void HandlePinnacle(Session session, params string[] parameters)
        {
            var admin = session?.Player;

            if (admin == null || admin.Location == null)
            {
                Write(session, "/pinnacle needs a player in the world.");
                return;
            }

            var request = PinnacleCommandRequest.Parse(parameters);

            if (!request.IsValid)
            {
                Write(session, $"/pinnacle: {request.Error}. {Usage}");
                return;
            }

            if (request.Kind == PinnacleCommandKind.Enter)
            {
                // Like the Knot seam, the entry runs on the admin's own thread (it creates a copy and teleports).
                admin.EnqueueAction(new ActionEventDelegate(() =>
                {
                    string reply;

                    try
                    {
                        reply = PinnacleEncounter.TryEnterAdmin(admin, request.PinnacleKind, request.Level, request.Overrides, out var refusal)
                            ? $"Entering an admin {request.PinnacleKind} copy at level {request.Level}." + (request.Overrides.Count > 0 ? $" ({request.Overrides})" : "")
                            : $"/pinnacle enter: {refusal}";
                    }
                    catch (Exception ex)
                    {
                        reply = "/pinnacle enter failed: " + ex.GetType().Name;
                    }

                    Write(session, reply);
                }));
                return;
            }

            if (!PinnacleFightManager.TryGetFightByInstance(admin.Location.Instance, out var lf))
            {
                Write(session, "/pinnacle: no Pinnacle fight is running in your copy.");
                return;
            }

            lf.Landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                string reply;

                try
                {
                    var now = PinnacleFightManager.Now();

                    switch (request.Kind)
                    {
                        case PinnacleCommandKind.Phase:
                            lf.Fight.JumpToPhase(request.Phase, now);
                            reply = "Jumped: " + lf.Fight.Describe(now);
                            break;

                        case PinnacleCommandKind.Reset:
                            lf.Fight.Reset("admin " + admin.Name);
                            reply = "Reset: " + lf.Fight.Describe(now);
                            break;

                        default:
                            reply = lf.Fight.Describe(now);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    reply = "/pinnacle failed: " + ex.GetType().Name;
                }

                Write(session, reply);
            }));
        }

        private static void Write(Session session, string text) => CommandHandlerHelper.WriteOutputInfo(session, text);
    }
}
