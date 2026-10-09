using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Read-only admin view of the PvP rules levers (Docs/Pvp/DESIGN.md "PvP rules (levers)"). Changing a lever
    /// is /modifybool, /modifylong or /modifydouble on its pvp_ key; this command only shows what the server has
    /// resolved and how often each choke point has bitten since boot. Same access level as /modifydouble.
    /// </summary>
    public static class PvpRulesCommands
    {
        [CommandHandler("pvprules", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Shows every PvP rules lever's resolved value (non-defaults marked) and the per-choke-point lever counts since boot.",
            "pvprules")]
        public static void HandlePvpRules(Session session, params string[] parameters)
        {
            CommandHandlerHelper.WriteOutputInfo(session, BuildReport(PvpRules.ReadDials(), PvpContextTunables.Read()));
        }

        /// <summary>The /pvprules text for one resolved snapshot. Pure apart from the since-boot counters.</summary>
        /// <param name="dials">The rules levers.</param>
        /// <param name="contextDials">The context tuning keys (pvp_{arena|bg}_{category}_{stat}); null = all neutral.</param>
        public static string BuildReport(PvpRuleDials dials, PvpContextDials contextDials = null)
        {
            var sb = new StringBuilder();

            sb.AppendLine("PvP rules levers (resolved; * = differs from the registered default):");

            foreach (var (key, value) in PvpRuleTunables.Describe(dials))
            {
                var defaultText = PvpRuleTunables.RegisteredDefaultText(key);
                var changed = defaultText != null && defaultText != value;

                sb.AppendLine(changed
                    ? $"  * {key} = {value} (default {defaultText})"
                    : $"    {key} = {value}");
            }

            if (dials != null && !dials.Enabled)
                sb.AppendLine("  pvp_rules_enabled is OFF: every lever hands its input back unchanged.");

            // the context tuning keys: only those off neutral are listed (marked), the rest are summarised
            var contextRows = (contextDials ?? PvpContextDials.Neutral).NonDefault();

            sb.AppendLine($"Context tuning (pvp_arena_* / pvp_bg_* per weapon category; arena and battleground only, never open-world): {PvpContextTuning.Keys.Count - contextRows.Count} of {PvpContextTuning.Keys.Count} keys at the default 1");

            foreach (var (key, value) in contextRows)
                sb.AppendLine($"  * {key} = {value} (default {PvpContextTuning.NeutralValue:0})");

            sb.AppendLine("Lever applications since boot (C1-C4 = damage cap bites; R1/R2 = ratings rescaled, up to 4 per hit; H1/H2 = consumable refusals; D1/D2 = dispel candidate lists narrowed):");

            var counts = new StringBuilder("  ");

            foreach (var (point, count) in PvpRules.GetAppliedCounts())
                counts.Append($"{point}={count} ");

            sb.Append(counts.ToString().TrimEnd());

            return sb.ToString();
        }
    }
}
