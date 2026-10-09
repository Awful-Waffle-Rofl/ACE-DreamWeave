using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Managers.Market;

namespace ACE.Server.Command.Web
{
    /// <summary>The live command registry, behind a seam so tests need neither CommandManager.Initialize nor its console thread.</summary>
    public interface ICommandCatalog
    {
        IReadOnlyList<CommandHandlerInfo> GetCommands();

        /// <summary>CommandManager.TryGetCommandInfo: side-effect free, no console output, no sudo handling.</summary>
        bool TryGetCommandInfo(string name, out CommandHandlerInfo info);

        /// <summary>CommandManager.GetCommandHandler. The web command dispatcher calls it only with a character session, never null.</summary>
        CommandHandlerResponse GetCommandHandler(ACE.Server.Network.Session session, string command, string[] parameters, out CommandHandlerInfo info);
    }

    public sealed class CommandCatalog : ICommandCatalog
    {
        public static readonly CommandCatalog Live = new CommandCatalog();

        private CommandCatalog()
        {
        }

        public IReadOnlyList<CommandHandlerInfo> GetCommands() => CommandManager.GetCommands().ToList();

        public bool TryGetCommandInfo(string name, out CommandHandlerInfo info) => CommandManager.TryGetCommandInfo(name, out info);

        public CommandHandlerResponse GetCommandHandler(ACE.Server.Network.Session session, string command, string[] parameters, out CommandHandlerInfo info)
            => CommandManager.GetCommandHandler(session, command, parameters, out info);
    }

    /// <summary>GET /v1/admin/commands body (AdminCommandsResponse in market-api-v1.yaml).</summary>
    public sealed class WebCommandListing
    {
        public WebCommandCharacter Character { get; init; }
        public IReadOnlyList<WebCommandEntry> Commands { get; init; }
    }

    /// <summary>AdminCommandCharacter. Name is null (and so omitted on the wire) when Online is false.</summary>
    public sealed class WebCommandCharacter
    {
        public bool Online { get; init; }
        public string Name { get; init; }
    }

    /// <summary>AdminCommand.</summary>
    public sealed class WebCommandEntry
    {
        public string Name { get; init; }
        public string Description { get; init; }
        public string Usage { get; init; }
        public string AccessLevel { get; init; }
        public int ParameterCount { get; init; }
        public IReadOnlyList<string> Flags { get; init; }
        public string Bucket { get; init; }
        public string Capture { get; init; }
        public string Source { get; init; }
        public bool Permitted { get; init; }
        public string Note { get; init; }
    }

    /// <summary>
    /// The read-only half of the web command console (PLAN-P4.md P4a): every registered command with the
    /// bucket its LIVE handler resolves to. Access level, flags, description and usage come from the live
    /// CommandHandlerInfo, never from source. Runs nothing. P4b's WebCommandDispatcher is expected to take
    /// this over as its List.
    /// </summary>
    public sealed class WebCommandListService
    {
        private readonly ICommandCatalog catalog;
        private readonly CommandClassification classification;
        private readonly Func<uint, string> onlineCharacterName;

        /// <param name="onlineCharacterName">Account id to the name of its eligible online character, or null. May throw; the route answers server_error.</param>
        public WebCommandListService(ICommandCatalog catalog, CommandClassification classification, Func<uint, string> onlineCharacterName)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.classification = classification ?? throw new ArgumentNullException(nameof(classification));
            this.onlineCharacterName = onlineCharacterName;
        }

        public WebCommandListing List(AdminPrincipal principal)
        {
            var name = onlineCharacterName?.Invoke(principal.AccountId);
            var entries = new List<WebCommandEntry>();

            foreach (var info in catalog.GetCommands())
            {
                var attribute = info?.Attribute;
                if (attribute == null)
                    continue;

                var resolved = classification.Resolve(info);

                var flags = new List<string>();
                if ((attribute.Flags & CommandHandlerFlag.ConsoleInvoke) != 0)
                    flags.Add("console_invoke");
                if ((attribute.Flags & CommandHandlerFlag.RequiresWorld) != 0)
                    flags.Add("requires_world");
                if (attribute.IncludeRaw)
                    flags.Add("include_raw");
                if (resolved.Source == CommandClassification.SourceServer && resolved.Row != null && resolved.Row.HasFlag(CommandClassification.FlagLongRunning))
                    flags.Add("long_running");

                entries.Add(new WebCommandEntry
                {
                    Name = attribute.Command ?? string.Empty,
                    Description = attribute.Description ?? string.Empty,
                    Usage = attribute.Usage ?? string.Empty,
                    AccessLevel = AccessLevelWire(attribute.Access),
                    ParameterCount = attribute.ParameterCount,
                    Flags = flags,
                    Bucket = resolved.Bucket,
                    Capture = resolved.Capture,
                    Source = resolved.Source,
                    Permitted = attribute.Access <= principal.Level,
                    Note = resolved.Note ?? string.Empty,
                });
            }

            entries.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));

            return new WebCommandListing
            {
                Character = new WebCommandCharacter { Online = name != null, Name = name },
                Commands = entries,
            };
        }

        /// <summary>
        /// The account's eligible online character (PLAN-P4.md section 2): a WorldConnected session with a
        /// Player that is not logging out. NetworkManager.Find uses SingleOrDefault, so two sessions for one
        /// account throw; that propagates and the route answers server_error.
        /// </summary>
        public static string LiveOnlineCharacterName(uint accountId) => LiveWebCommandWorld.Instance.FindCharacter(accountId)?.Name;

        public static string AccessLevelWire(AccessLevel level) => level switch
        {
            ACE.Entity.Enum.AccessLevel.Player => "player",
            ACE.Entity.Enum.AccessLevel.Advocate => "advocate",
            ACE.Entity.Enum.AccessLevel.Sentinel => "sentinel",
            ACE.Entity.Enum.AccessLevel.Envoy => "envoy",
            ACE.Entity.Enum.AccessLevel.Developer => "developer",
            ACE.Entity.Enum.AccessLevel.Admin => "admin",
            _ => level.ToString().ToLowerInvariant(),
        };
    }
}
