using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using ACE.Entity.Enum;

using log4net;

using Timer = System.Timers.Timer;

namespace ACE.Server.Managers
{
    /// <summary>
    /// One-way relay of in-game chat to Discord, routed per channel: General, Trade, the staff
    /// Audit feed and the World Events announcement feed each go to their own Discord channel via a
    /// separate incoming webhook.
    ///
    /// Each channel has its own queue and is flushed independently on a timer, so a busy channel
    /// never trips its webhook's rate limit and a slow/dead webhook never blocks the network
    /// thread. A channel is relayed only when the relay is enabled AND that channel has a webhook
    /// URL configured - a channel with no URL is silently skipped.
    ///
    /// The Audit feed is the staff-only record of admin command usage (see
    /// PlayerManager.BroadcastToAuditChannel), which is otherwise seen only by staff who happen to
    /// be online with the channel active. Relaying it gives monitoring a durable copy.
    ///
    /// The Events feed carries only the three World Events beats a player who is NOT logged in
    /// would want pushed at them - the teaser, the run going live, and the outcome (see
    /// WorldEventAnnouncer.Broadcast's relayToDiscord parameter). The intra-run chatter (the 30s
    /// Announced line, the 60s/30s countdown warnings, per-wave local flavour, the boss's parting
    /// line) is deliberately NOT relayed: in game it is atmosphere, in Discord it would be spam.
    ///
    /// Configuration precedence (highest first):
    ///   1. Environment variables (ACE_DISCORD_RELAY_ENABLED, ACE_DISCORD_WEBHOOK_URL_GENERAL,
    ///      ACE_DISCORD_WEBHOOK_URL_TRADE, ACE_DISCORD_WEBHOOK_URL_AUDIT,
    ///      ACE_DISCORD_WEBHOOK_URL_EVENTS, ACE_DISCORD_WEBHOOK_URL_PVP) - lets a single container
    ///      image be disabled in Stage and enabled in Prod purely from compose.
    ///   2. PropertyManager DB properties (discord_relay_enabled, discord_webhook_url_general,
    ///      discord_webhook_url_trade, discord_webhook_url_audit, discord_webhook_url_events,
    ///      discord_webhook_url_pvp) - runtime toggleable via /modifybool and /modifystring.
    ///   3. Defaults (disabled, empty URLs).
    /// </summary>
    public static class DiscordRelayManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Discord hard-caps webhook message content at 2000 chars.
        private const int MaxDiscordMessageLength = 1900;

        // How often each queue is drained and posted. 5s => at most 12 requests/min per channel,
        // comfortably under Discord's ~30/min per-webhook limit.
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

        // Safety valve so a message storm can't build an unbounded backlog (per channel).
        private const int MaxQueuedLines = 500;

        // Cap POSTs per flush so a huge backlog can't burst past the rate limit in one tick.
        private const int MaxMessagesPerFlush = 3;

        private static readonly HttpClient httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        private static readonly Timer flushTimer;

        private static readonly bool? envEnabled;

        // One relay destination per in-game public chat channel. Add a row here (plus the matching
        // config properties) to route another channel. Allegiance/Society/Olthoi are private/group
        // channels and are intentionally not routed.
        private static readonly IReadOnlyDictionary<ChatType, RelayChannel> chatChannels = new Dictionary<ChatType, RelayChannel>
        {
            [ChatType.General] = new RelayChannel("General", "ACE_DISCORD_WEBHOOK_URL_GENERAL", "discord_webhook_url_general"),
            [ChatType.Trade]   = new RelayChannel("Trade",   "ACE_DISCORD_WEBHOOK_URL_TRADE",   "discord_webhook_url_trade"),
        };

        // The staff Audit feed. Not a ChatType - it is fed by PlayerManager.BroadcastToAuditChannel
        // rather than by player chat, so it is held outside the chat routing table.
        private static readonly RelayChannel auditChannel =
            new RelayChannel("Audit", "ACE_DISCORD_WEBHOOK_URL_AUDIT", "discord_webhook_url_audit");

        // The World Events announcement feed. Like Audit, not a ChatType - it is fed by
        // WorldEventAnnouncer rather than by player chat.
        private static readonly RelayChannel eventsChannel =
            new RelayChannel("Events", "ACE_DISCORD_WEBHOOK_URL_EVENTS", "discord_webhook_url_events");

        // The PvP feed. Like Audit and Events, not a ChatType - it is fed by Player_Death's PK kill
        // broadcast (open-world only) and by the arena match coordinator on match resolution.
        private static readonly RelayChannel pvpChannel =
            new RelayChannel("PvP", "ACE_DISCORD_WEBHOOK_URL_PVP", "discord_webhook_url_pvp");

        // Everything Flush() drains: chat plus audit plus world events plus PvP.
        private static readonly IReadOnlyList<RelayChannel> allChannels =
            new List<RelayChannel>(chatChannels.Values) { auditChannel, eventsChannel, pvpChannel };

        // Guard so overlapping flushes never run (timer + slow POST).
        private static int flushing;

        static DiscordRelayManager()
        {
            httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("ACE.Server");

            var rawEnabled = Environment.GetEnvironmentVariable("ACE_DISCORD_RELAY_ENABLED");
            if (!string.IsNullOrWhiteSpace(rawEnabled) && bool.TryParse(rawEnabled, out var parsed))
                envEnabled = parsed;

            flushTimer = new Timer(FlushInterval.TotalMilliseconds) { AutoReset = true };
            flushTimer.Elapsed += (_, _) => Flush();
            flushTimer.Start();
        }

        private static bool Enabled => envEnabled ?? PropertyManager.GetBool("discord_relay_enabled").Item;

        /// <summary>
        /// A single relay destination: one in-game channel mapped to one Discord webhook, with its
        /// own queue. The webhook URL is resolved live (env override, else DB property) so runtime
        /// /modifystring takes effect without a restart.
        /// </summary>
        private sealed class RelayChannel
        {
            // Env files and copy-pasted secrets routinely pick up a leading UTF-8 BOM (U+FEFF) or
            // stray whitespace/newline, yielding a URL like "<U+FEFF>https://..." that HttpClient
            // can't POST - and the failure surfaces only as a swallowed "[DISCORD] Webhook POST
            // threw" warning, so the relay dies silently. Strip those from the resolved URL. Note
            // U+FEFF is NOT .NET whitespace, so a plain Trim()/IsNullOrWhiteSpace does not catch it.
            private static readonly char[] UrlTrimChars =
                { ' ', '\t', '\r', '\n', '\f', '\v', '\uFEFF', '\u200B', '\u00A0' };

            private readonly string envUrl;
            private readonly string dbPropertyKey;

            /// <summary>Human-readable channel name; used only in log lines.</summary>
            public string Name { get; }

            public ConcurrentQueue<string> Queue { get; } = new ConcurrentQueue<string>();

            public RelayChannel(string name, string envVarName, string dbPropertyKey)
            {
                Name = name;
                envUrl = Clean(Environment.GetEnvironmentVariable(envVarName));
                this.dbPropertyKey = dbPropertyKey;
            }

            public string WebhookUrl => !string.IsNullOrWhiteSpace(envUrl)
                ? envUrl
                : Clean(PropertyManager.GetString(dbPropertyKey).Item);

            private static string Clean(string url)
                => string.IsNullOrEmpty(url) ? url : url.Trim(UrlTrimChars);
        }

        /// <summary>
        /// Queue a global chat line for relay to Discord. Cheap and non-blocking; safe to call from
        /// the chat handler. No-ops unless the relay is enabled and the channel is routed to a
        /// configured webhook.
        /// </summary>
        public static void QueueMessage(ChatType chatType, string playerName, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (!chatChannels.TryGetValue(chatType, out var channel))
                return;

            Enqueue(channel, $"`{Sanitize(playerName)}`: {Sanitize(message)}");
        }

        /// <summary>
        /// Queue an Audit channel line (an admin command record) for relay to Discord. Cheap and
        /// non-blocking; safe to call from world/network threads and from the console. No-ops
        /// unless the relay is enabled and discord_webhook_url_audit is configured.
        /// </summary>
        /// <param name="issuerName">The staff member who ran the command, or null for console.</param>
        public static void QueueAudit(string issuerName, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            // Most audit messages already open with the issuer's own name ("Bob has deleted ..."),
            // so only prefix when it would actually add information.
            var prefix = !string.IsNullOrWhiteSpace(issuerName)
                         && !message.StartsWith(issuerName, StringComparison.OrdinalIgnoreCase)
                ? $"`{Sanitize(issuerName)}`: "
                : "";

            Enqueue(auditChannel, $"{prefix}{Sanitize(Redact(message))}");
        }

        /// <summary>
        /// Queue a World Events announcement for relay to Discord. Cheap and non-blocking; safe to
        /// call from the world thread. No-ops unless the relay is enabled and
        /// discord_webhook_url_events is configured.
        ///
        /// The line arrives already composed for in-game chat, carrying
        /// WorldEventAnnouncer.AnnouncePrefix - it is relayed verbatim (after sanitizing) rather
        /// than restyled, so what Discord shows is exactly what players saw. It still goes through
        /// Sanitize because outcome lines embed PLAYER names (the MVP and the top killer).
        /// </summary>
        public static void QueueWorldEvent(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            Enqueue(eventsChannel, Sanitize(message));
        }

        /// <summary>
        /// Queue a PvP feed line (an open-world PK kill, or a finished arena match's mode/winners/losers) for
        /// relay to Discord. Cheap and non-blocking; safe to call from the world thread. No-ops unless the
        /// relay is enabled and discord_webhook_url_pvp is configured - an empty URL means no post and no
        /// work, since Enqueue's gate below checks the webhook before this does anything else. Sanitized like
        /// every other feed, since these lines embed player names.
        /// </summary>
        public static void QueuePvp(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            Enqueue(pvpChannel, Sanitize(message));
        }

        /// <summary>
        /// Shared gate for every queue: relay on, destination configured, backlog not saturated.
        /// </summary>
        private static void Enqueue(RelayChannel channel, string line)
        {
            if (!Enabled)
                return;

            if (string.IsNullOrWhiteSpace(channel.WebhookUrl))
                return;

            if (channel.Queue.Count >= MaxQueuedLines)
                return;

            channel.Queue.Enqueue(line);
        }

        // "/modifystring discord_webhook_url_audit <url>" is itself announced on the Audit channel,
        // which would post the new webhook's secret token straight into Discord. Strip the token
        // out of anything that looks like a Discord webhook URL before it is relayed.
        private static readonly Regex WebhookUrlPattern = new Regex(
            @"(https?://(?:[\w-]+\.)?discord(?:app)?\.com/api/(?:v\d+/)?webhooks/\d+/)[\w-]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string Redact(string input)
            => string.IsNullOrEmpty(input) ? input : WebhookUrlPattern.Replace(input, "$1[redacted]");

        /// <summary>
        /// Neutralize Discord markdown/mention control characters so player text can't ping roles,
        /// break formatting, or inject the webhook body. Mentions are additionally disarmed at the
        /// payload level via allowed_mentions, this is belt-and-suspenders for the visible text.
        /// </summary>
        private static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var sb = new StringBuilder(input.Length + 8);
            foreach (var c in input)
            {
                switch (c)
                {
                    // Zero-width space (U+200B) between @ and the rest defuses @everyone/@here/<@id>.
                    case '@':
                        sb.Append('@').Append('\u200B');
                        break;
                    case '`':
                    case '*':
                    case '_':
                    case '~':
                    case '>':
                    case '|':
                    case '\\':
                        sb.Append('\\').Append(c);
                        break;
                    case '\r':
                        break;
                    case '\n':
                        sb.Append(' ');
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void Flush()
        {
            // Skip if a previous flush is still in flight.
            if (Interlocked.CompareExchange(ref flushing, 1, 0) != 0)
                return;

            try
            {
                var enabled = Enabled;

                foreach (var channel in allChannels)
                {
                    var url = enabled ? channel.WebhookUrl : null;

                    // Disabled, or this channel has no webhook: drop anything queued so it doesn't
                    // burst when the relay/webhook is later configured.
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        while (channel.Queue.TryDequeue(out _)) { }
                        continue;
                    }

                    for (var sent = 0; sent < MaxMessagesPerFlush && !channel.Queue.IsEmpty; sent++)
                    {
                        var batch = BuildBatch(channel.Queue);
                        if (batch.Length == 0)
                            break;

                        Post(channel.Name, url, batch);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn("[DISCORD] Unexpected error flushing chat relay", ex);
            }
            finally
            {
                Interlocked.Exchange(ref flushing, 0);
            }
        }

        /// <summary>
        /// Drain queued lines from one channel into a single message body, up to Discord's length
        /// cap. A line that would overflow the current message is left on the queue for the next
        /// batch/flush.
        /// </summary>
        private static string BuildBatch(ConcurrentQueue<string> queue)
        {
            var sb = new StringBuilder();
            while (queue.TryPeek(out var line))
            {
                var addedLength = (sb.Length == 0 ? 0 : 1) + line.Length;
                if (sb.Length > 0 && sb.Length + addedLength > MaxDiscordMessageLength)
                    break;

                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(line);

                queue.TryDequeue(out _);
            }
            return sb.ToString();
        }

        private static void Post(string channelName, string url, string content)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                content,
                allowed_mentions = new { parse = Array.Empty<string>() },
            });

            // Fire-and-forget: never block the flush timer thread on network I/O.
            _ = PostAsync(channelName, url, payload);
        }

        private static async System.Threading.Tasks.Task PostAsync(string channelName, string url, string payload)
        {
            try
            {
                using var body = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(url, body).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    log.Warn($"[DISCORD] Webhook POST failed for {channelName}: {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception ex)
            {
                log.Warn($"[DISCORD] Webhook POST threw for {channelName}", ex);
            }
        }
    }
}
