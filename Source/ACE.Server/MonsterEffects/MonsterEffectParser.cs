using System;
using System.Collections.Generic;

namespace ACE.Server.MonsterEffects
{
    /// <summary>
    /// Turns a weenie's PropertyString 9015 MonsterCombatEffects value into effect specs.
    ///
    /// FORMAT. Records are separated by ';'. Within a record the tokens are whitespace-separated: the FIRST
    /// token is the effect kind, every remaining token is key=value. Kinds and keys are case-insensitive and
    /// are lowercased on the way in; values are kept verbatim. Empty records are skipped, so trailing and
    /// doubled separators are harmless.
    ///
    ///     flatdamage type=fire amount=30 chance=0.35; leech vital=health pct=0.12
    ///
    /// NOTHING IS SKIPPED SILENTLY. An unknown kind, a token with no '=', an empty key and an empty value
    /// each append a description to <c>errors</c> naming the offending text. This is deliberately unlike
    /// ClassAbilityTrainer.ParseTrainerAbilities, whose silent skip of a typo'd token needed a hand-written
    /// unit test per trainer to catch a mis-authored weenie
    /// (Source/ACE.Server.Tests/ClassAbilityTrainerParseTests.cs). Here a typo is a logged warning naming
    /// the wcid at the moment the first monster of that wcid is built.
    ///
    /// Pure and static: no PropertyManager reads, no logging, no registry mutation. The caller
    /// (Creature.BuildMonsterEffects) owns logging, the per-monster cap and the wcid context.
    /// </summary>
    public static class MonsterEffectParser
    {
        private static readonly char[] RecordSeparators = { ';' };

        public static void Parse(string raw, out List<MonsterEffectSpec> specs, out List<string> errors)
        {
            specs = new List<MonsterEffectSpec>();
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(raw))
                return;

            foreach (var rawRecord in raw.Split(RecordSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var record = rawRecord.Trim();
                if (record.Length == 0)
                    continue;

                var tokens = record.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                var kind = tokens[0].ToLowerInvariant();

                if (kind.IndexOf('=') >= 0)
                {
                    errors.Add($"record '{record}' starts with the arg '{tokens[0]}' - the first token of a record must be the effect kind.");
                    continue;
                }

                if (!MonsterEffectRegistry.IsKnownKind(kind))
                {
                    errors.Add($"unknown effect kind '{tokens[0]}' in record '{record}'.");
                    continue;
                }

                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var recordFailed = false;

                for (var i = 1; i < tokens.Length; i++)
                {
                    var token = tokens[i];
                    var split = token.IndexOf('=');

                    if (split < 0)
                    {
                        errors.Add($"malformed token '{token}' in record '{record}' - args are key=value (a flag is authored as key=true).");
                        recordFailed = true;
                        continue;
                    }

                    var key = token.Substring(0, split).Trim().ToLowerInvariant();
                    var value = token.Substring(split + 1).Trim();

                    if (key.Length == 0)
                    {
                        errors.Add($"malformed token '{token}' in record '{record}' - the key is empty.");
                        recordFailed = true;
                        continue;
                    }

                    if (value.Length == 0)
                    {
                        errors.Add($"malformed token '{token}' in record '{record}' - '{key}' has no value.");
                        recordFailed = true;
                        continue;
                    }

                    if (args.ContainsKey(key))
                    {
                        errors.Add($"duplicate arg '{key}' in record '{record}' - the later value is ignored.");
                        continue;
                    }

                    args[key] = value;
                }

                // A record with a bad token is dropped whole rather than run with a hole in its args: a
                // half-authored effect that silently uses defaults is harder to notice in play than one that
                // does not appear at all, and the error above already names it.
                if (recordFailed)
                    continue;

                specs.Add(new MonsterEffectSpec(kind, args));
            }
        }
    }
}
