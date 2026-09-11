using System.Text;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.MonsterEffects;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Read-only admin diagnostics for the monster combat effect system (PropertyString 9015
    /// MonsterCombatEffects - see Docs/MonsterEffects/DESIGN.md and CATALOG.md). This is the only in-game
    /// window onto whether an authored effect string actually resolved to anything: a malformed record is
    /// dropped whole with a logged warning (Creature_MonsterEffects.BuildMonsterEffects /
    /// ResolveMonsterEffects), so a monster that "should" carry four effects and only shows three has an
    /// authoring bug, not a code bug - and there is otherwise no way for a designer to tell the difference
    /// short of combing the server log for the wcid.
    ///
    /// Never mutates anything: reads Creature.MonsterEffects (the shared, immutable resolved set) and
    /// Creature.MonsterEffectStates (this creature's own per-effect mutable state array, internal within
    /// ACE.Server - see Creature_MonsterEffects.cs) and formats them. No player-facing surface at all.
    /// </summary>
    public static class MonsterEffectAdminCommands
    {
        [CommandHandler("monstereffects", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Dumps the parsed monster combat effects and live per-effect state for the last appraised creature.",
            "Select/appraise a creature first, then run this with no arguments.")]
        public static void HandleMonsterEffects(Session session, params string[] parameters)
        {
            var target = CommandHandlerHelper.GetLastAppraisedObject(session);

            if (target == null)
                return;

            if (!(target is Creature creature))
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"{target.Name} (0x{target.Guid}) is not a creature - monster combat effects only apply to Creature weenies.");
                return;
            }

            var sb = new StringBuilder();

            sb.AppendLine($"Monster combat effects for {creature.Name} (0x{creature.Guid}), wcid {creature.WeenieClassId}:");

            var authored = creature.GetProperty(PropertyString.MonsterCombatEffects);

            if (string.IsNullOrWhiteSpace(authored))
            {
                sb.AppendLine("  PropertyString 9015 MonsterCombatEffects is not set on this weenie - nothing authored.");
                CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
                return;
            }

            sb.AppendLine($"  Authored string: {authored}");

            if (creature.WeenieType == WeenieType.GamePiece)
                sb.AppendLine("  WeenieType is GamePiece - its attacks bypass the damage pipeline entirely, so this string is IGNORED (see BuildMonsterEffects). No effects are attached regardless of what is authored above.");

            sb.AppendLine($"  monster_effects_enabled: {MonsterEffectCaps.Enabled} (master switch, read live at dispatch - effects below still parse/attach either way)");

            var set = creature.MonsterEffects;

            if (set == null)
            {
                sb.AppendLine("  Resolved effect set: NONE - either every record was rejected (check the server log for 'Monster effects: wcid " + creature.WeenieClassId + "'), or this is a GamePiece as noted above.");
                CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
                return;
            }

            sb.AppendLine($"  Resolved entries: {set.Count} ({set.ActiveCount} with a shipped handler)");

            var state = creature.MonsterEffectStates;
            var now = Time.GetUnixTime();

            for (var i = 0; i < set.Count; i++)
            {
                var entry = set[i];
                var spec = entry.Spec;

                sb.AppendLine();
                sb.AppendLine($"  [{i}] {spec}");

                if (entry.Handler == null)
                {
                    sb.AppendLine("      handler: NOT SHIPPED - this kind is reserved but has no implementation yet, so this entry is inert.");
                    continue;
                }

                sb.AppendLine($"      handler: {entry.Handler.GetType().Name}");

                if (state == null || i >= state.Length)
                {
                    sb.AppendLine("      state: unavailable");
                    continue;
                }

                ref var s = ref state[i];

                sb.AppendLine($"      stacks: {s.Stacks}");

                if (s.LastTime > 0)
                    sb.AppendLine($"      last triggered: {now - s.LastTime:F1}s ago");

                if (s.WardAmount > 0)
                {
                    var remaining = s.WardExpire - now;
                    sb.AppendLine($"      ward pool: {s.WardAmount} points, expires in {(remaining > 0 ? remaining.ToString("F1") : "0.0")}s");
                }

                if (s.Carry != 0)
                    sb.AppendLine($"      fractional carry: {s.Carry:F3}");

                sb.AppendLine($"      announced: {s.Announced}");
            }

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
        }
    }
}
