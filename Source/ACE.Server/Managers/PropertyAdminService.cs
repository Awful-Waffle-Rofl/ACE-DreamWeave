using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers
{
    public enum PropertyModifyOutcome
    {
        Updated,
        Unchanged,
        UnknownKey,
        InvalidValue,
        Conflict,
        Faulted
    }

    public sealed class PropertyModifyResult
    {
        public PropertyModifyOutcome Outcome { get; init; }

        /// <summary>bool|long|double|string; null for UnknownKey.</summary>
        public string Type { get; init; }

        /// <summary>GET-formatter text before the call.</summary>
        public string PreviousText { get; init; }

        /// <summary>GET-formatter text after the call.</summary>
        public string CurrentText { get; init; }
    }

    /// <summary>
    /// Who is making a PropertyAdminService.TryModify call - an in-game/console session, or a web
    /// admin account with no live character behind it (PLAN-P2.md section 2).
    /// </summary>
    public sealed class PropertyAdminActor
    {
        private PropertyAdminActor(Session session, string accountName, bool isWeb)
        {
            Session = session;
            AccountName = accountName;
            IsWeb = isWeb;
        }

        /// <summary>session may be null (console).</summary>
        public static PropertyAdminActor InGame(Session session) => new PropertyAdminActor(session, null, false);

        public static PropertyAdminActor Web(string accountName) => new PropertyAdminActor(null, accountName, true);

        public bool IsWeb { get; }

        internal Session Session { get; }

        internal string AccountName { get; }
    }

    /// <summary>
    /// Shared body of /modifybool, /modifylong, /modifydouble, /modifystring (PLAN-P2.md section 2),
    /// used by both the in-game commands (thin wrappers, byte-identical output) and
    /// POST /v1/admin/settings/{key}. In-game and web share the same underlying ModifyX/GetX calls
    /// and the same pk_server/pkl_server side effect, but web adds a staleness check (expected_current)
    /// and a same-value no-op that in-game must never adopt (invariant 8).
    /// </summary>
    public static class PropertyAdminService
    {
        public const string TypeBool = "bool";
        public const string TypeLong = "long";
        public const string TypeDouble = "double";
        public const string TypeString = "string";

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Held across read, compare, modify, audit and side effect in TryModify for both actors.
        /// In-game commands run on the world thread; console commands run on CommandThread - without
        /// this lock a console edit could interleave with a world-thread edit.
        /// </summary>
        private static readonly object Sync = new object();

        // Test seams, restored by tests in finally.
        internal static Action<string, bool> WorldTypeChanged = PlayerManager.UpdatePKStatusForAllPlayers;
        internal static Action<Player, string> AuditInGame = PlayerManager.BroadcastToAuditChannel;
        internal static Action<string, string> AuditAs = PlayerManager.BroadcastToAuditChannel;

        public static bool TryResolveType(string key, out string type)
        {
            if (DefaultPropertyManager.DefaultBooleanProperties.ContainsKey(key))
            {
                type = TypeBool;
                return true;
            }

            if (DefaultPropertyManager.DefaultLongProperties.ContainsKey(key))
            {
                type = TypeLong;
                return true;
            }

            if (DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key))
            {
                type = TypeDouble;
                return true;
            }

            if (DefaultPropertyManager.DefaultStringProperties.ContainsKey(key))
            {
                type = TypeString;
                return true;
            }

            type = null;
            return false;
        }

        /// <summary>
        /// Parses raw web (or, for long/double, in-game) input into the GET-formatter's own text, so a
        /// caller never needs a second formatting step. bool: bool.TryParse (case-insensitive). long:
        /// NumberStyles.Integer, invariant. double: NumberStyles.Float (no thousands separators),
        /// invariant, finite only. string: verbatim, refused if it contains a control character.
        /// </summary>
        public static bool TryParseWeb(string type, string raw, out string normalizedText)
        {
            switch (type)
            {
                case TypeBool:
                    if (bool.TryParse(raw, out var boolVal))
                    {
                        normalizedText = PropertyManager.FormatBool(boolVal);
                        return true;
                    }
                    break;

                case TypeLong:
                    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
                    {
                        normalizedText = PropertyManager.FormatLong(longVal);
                        return true;
                    }
                    break;

                case TypeDouble:
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleVal) && double.IsFinite(doubleVal))
                    {
                        normalizedText = PropertyManager.FormatDouble(doubleVal);
                        return true;
                    }
                    break;

                case TypeString:
                    if (raw != null && !ContainsControlChar(raw))
                    {
                        normalizedText = raw;
                        return true;
                    }
                    break;
            }

            normalizedText = null;
            return false;
        }

        private static bool ContainsControlChar(string value)
        {
            foreach (var c in value)
            {
                if (char.IsControl(c))
                    return true;
            }

            return false;
        }

        /// <summary>The live value, through the same formatter GET uses (PropertyManager.EnumerateWithPrefix).</summary>
        public static string FormatCurrent(string type, string key)
        {
            switch (type)
            {
                case TypeBool:   return PropertyManager.FormatBool(PropertyManager.GetBool(key).Item);
                case TypeLong:   return PropertyManager.FormatLong(PropertyManager.GetLong(key).Item);
                case TypeDouble: return PropertyManager.FormatDouble(PropertyManager.GetDouble(key).Item);
                case TypeString: return PropertyManager.GetString(key).Item;
                default:         return null;
            }
        }

        public static PropertyModifyResult TryModify(string type, string key, string raw, PropertyAdminActor actor, string expectedCurrent = null)
        {
            lock (Sync)
            {
                var result = actor.IsWeb ? TryModifyWeb(type, key, raw, actor, expectedCurrent) : TryModifyInGame(type, key, raw, actor);

                if (result?.Outcome == PropertyModifyOutcome.Updated)
                    NotifyUpdated(key);

                return result;
            }
        }

        /// <summary>
        /// Called after every successful modify from either actor. Today: the `[PVP] rules ...` log line when a
        /// PvP rules lever key changed (Docs/Pvp/DESIGN.md "PvP rules (levers)"). A throw here is logged and swallowed - the
        /// setting has already changed, so the caller must still see Updated.
        /// </summary>
        internal static Action<string> SettingUpdated = ACE.Server.Pvp.Rules.PvpRuleTunables.OnPropertyModified;

        private static void NotifyUpdated(string key)
        {
            try
            {
                SettingUpdated?.Invoke(key);
            }
            catch (Exception ex)
            {
                log.Warn($"post-modify notification for {key} threw: {ex.GetFullMessage()}");
            }
        }

        // ---- in-game: byte-identical to 911bc6220's four command bodies ----

        private static PropertyModifyResult TryModifyInGame(string type, string key, string raw, PropertyAdminActor actor)
        {
            var session = actor.Session;

            switch (type)
            {
                case TypeBool:   return InGameModifyBool(session, key, raw);
                case TypeLong:   return InGameModifyLong(session, key, raw);
                case TypeDouble: return InGameModifyDouble(session, key, raw);
                case TypeString: return InGameModifyString(session, key, raw);
                default:         return new PropertyModifyResult { Outcome = PropertyModifyOutcome.UnknownKey };
            }
        }

        private static PropertyModifyResult InGameModifyBool(Session session, string key, string raw)
        {
            try
            {
                var boolVal = bool.Parse(raw);

                var prevState = PropertyManager.GetBool(key);

                if (prevState.Item == boolVal && !string.IsNullOrWhiteSpace(prevState.Description))
                {
                    WriteOutputInfo(session, $"Bool property is already {boolVal} for {key}!");
                    return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Unchanged, Type = TypeBool };
                }

                if (PropertyManager.ModifyBool(key, boolVal))
                {
                    WriteOutputInfo(session, "Bool property successfully updated!");
                    AuditInGame(session?.Player, $"Successfully changed server bool property {key} to {boolVal}");

                    AfterBoolModified(key, boolVal);

                    return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Updated, Type = TypeBool };
                }
                else
                {
                    WriteOutputInfo(session, "Unknown bool property was not updated. Type showprops for a list of properties.");
                    return new PropertyModifyResult { Outcome = PropertyModifyOutcome.UnknownKey, Type = TypeBool };
                }
            }
            catch (Exception)
            {
                WriteOutputInfo(session, "Please input a valid bool", ChatMessageType.Help);
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.InvalidValue, Type = TypeBool };
            }
        }

        private static PropertyModifyResult InGameModifyLong(Session session, string key, string raw)
        {
            try
            {
                if (!TryParseWeb(TypeLong, raw, out var normalizedText))
                    throw new FormatException();

                var longVal = long.Parse(normalizedText, CultureInfo.InvariantCulture);

                if (PropertyManager.ModifyLong(key, longVal))
                {
                    WriteOutputInfo(session, "Long property successfully updated!");
                    AuditInGame(session?.Player, $"Successfully changed server long property {key} to {longVal}");
                    return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Updated, Type = TypeLong };
                }
                else
                {
                    WriteOutputInfo(session, "Unknown long property was not updated. Type showprops for a list of properties.");
                    return new PropertyModifyResult { Outcome = PropertyModifyOutcome.UnknownKey, Type = TypeLong };
                }
            }
            catch (Exception)
            {
                WriteOutputInfo(session, "Please input a valid long", ChatMessageType.Help);
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.InvalidValue, Type = TypeLong };
            }
        }

        private static PropertyModifyResult InGameModifyDouble(Session session, string key, string raw)
        {
            try
            {
                if (!TryParseWeb(TypeDouble, raw, out var normalizedText))
                    throw new FormatException();

                var doubleVal = double.Parse(normalizedText, CultureInfo.InvariantCulture);

                if (PropertyManager.ModifyDouble(key, doubleVal))
                {
                    WriteOutputInfo(session, "Double property successfully updated!");
                    AuditInGame(session?.Player, $"Successfully changed server double property {key} to {doubleVal}");
                    return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Updated, Type = TypeDouble };
                }
                else
                {
                    WriteOutputInfo(session, "Unknown double property was not updated. Type showprops for a list of properties.");
                    return new PropertyModifyResult { Outcome = PropertyModifyOutcome.UnknownKey, Type = TypeDouble };
                }
            }
            catch (Exception)
            {
                WriteOutputInfo(session, "Please input a valid double", ChatMessageType.Help);
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.InvalidValue, Type = TypeDouble };
            }
        }

        private static PropertyModifyResult InGameModifyString(Session session, string key, string raw)
        {
            if (PropertyManager.ModifyString(key, raw))
            {
                WriteOutputInfo(session, "String property successfully updated!");
                AuditInGame(session?.Player, $"Successfully changed server string property {key} to {raw}");
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Updated, Type = TypeString };
            }
            else
            {
                WriteOutputInfo(session, "Unknown string property was not updated. Type showprops for a list of properties.");
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.UnknownKey, Type = TypeString };
            }
        }

        private static void WriteOutputInfo(Session session, string output, ChatMessageType chatMessageType = ChatMessageType.Broadcast)
        {
            if (session != null)
            {
                if (session.State == SessionState.WorldConnected && session.Player != null)
                    ChatPacket.SendServerMessage(session, output, chatMessageType);
            }
            else
            {
                // This private copy of CommandHandlerHelper.WriteOutputInfo is what the four modifyX
                // commands write through, so it carries the same web console capture hook.
                ACE.Server.Command.Web.WebCommandContext.TryCapture(null, ACE.Server.Command.Web.WebCommandContext.LevelInfo, output);
                log.Info(output);
            }
        }

        // ---- web: PLAN-P2.md section 2 ----

        private static PropertyModifyResult TryModifyWeb(string type, string key, string raw, PropertyAdminActor actor, string expectedCurrent)
        {
            if (!TryResolveType(key, out var resolvedType) || resolvedType != type)
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.UnknownKey };

            if (!TryParseWeb(type, raw, out var normalizedText))
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.InvalidValue, Type = type };

            var prev = FormatCurrent(type, key);

            if (!string.Equals(expectedCurrent, prev, StringComparison.Ordinal))
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Conflict, Type = type, PreviousText = prev, CurrentText = prev };

            if (string.Equals(normalizedText, prev, StringComparison.Ordinal))
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Unchanged, Type = type, PreviousText = prev, CurrentText = prev };

            if (!ApplyModify(type, key, normalizedText))
                return new PropertyModifyResult { Outcome = PropertyModifyOutcome.UnknownKey, Type = type, PreviousText = prev, CurrentText = prev };

            try
            {
                AuditAs(actor.AccountName, $"Successfully changed server {type} property {key} from {prev} to {normalizedText} via the web admin panel");
            }
            catch (Exception ex)
            {
                log.Warn($"[MARKET][ADMIN] audit failed: {ex.GetFullMessage()}");
            }

            if (type == TypeBool)
                AfterBoolModified(key, bool.Parse(normalizedText));

            return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Updated, Type = type, PreviousText = prev, CurrentText = normalizedText };
        }

        private static bool ApplyModify(string type, string key, string normalizedText)
        {
            switch (type)
            {
                case TypeBool:   return PropertyManager.ModifyBool(key, bool.Parse(normalizedText));
                case TypeLong:   return PropertyManager.ModifyLong(key, long.Parse(normalizedText, CultureInfo.InvariantCulture));
                case TypeDouble: return PropertyManager.ModifyDouble(key, double.Parse(normalizedText, CultureInfo.InvariantCulture));
                case TypeString: return PropertyManager.ModifyString(key, normalizedText);
                default:         return false;
            }
        }

        /// <summary>The ONLY place the pk_server/pkl_server side effect is invoked, from either actor.</summary>
        private static void AfterBoolModified(string key, bool value)
        {
            if (key == "pk_server" || key == "pkl_server")
                WorldTypeChanged(key, value);
        }

        /// <summary>
        /// Modelled on LiveCharacterSheetWorld.RunQueued, not shared with it (that one logs [CHARSHEET]
        /// and does not distinguish a throw from a timeout). Enqueues work through <paramref name="enqueue"/>
        /// and waits up to <paramref name="timeout"/>. A waiter that gives up sets an abandon flag; the
        /// queued action then skips the work if it has not already started. A throw inside the work
        /// answers Faulted rather than the caller's exception. Null return means the wait timed out.
        /// </summary>
        internal static PropertyModifyResult RunOnWorld(Action<IAction> enqueue, Func<PropertyModifyResult> work, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<PropertyModifyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandoned = new AbandonFlag();

            enqueue(new ActionEventDelegate(() =>
            {
                if (abandoned.IsSet)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                try
                {
                    tcs.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    log.Warn($"[MARKET][ADMIN] a queued setting edit threw: {ex.GetFullMessage()}");
                    tcs.TrySetResult(new PropertyModifyResult { Outcome = PropertyModifyOutcome.Faulted });
                }
            }));

            if (tcs.Task.Wait(timeout))
                return tcs.Task.Result;

            abandoned.Set();
            return null;
        }

        private sealed class AbandonFlag
        {
            private int value;

            public bool IsSet => Volatile.Read(ref value) != 0;

            public void Set() => Interlocked.Exchange(ref value, 1);
        }
    }
}
