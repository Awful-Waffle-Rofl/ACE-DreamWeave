using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;

using ACE.Entity.Enum;

using log4net;

using Timer = System.Timers.Timer;

namespace ACE.Server.Managers
{
    /// <summary>
    /// One-way relay of in-game public global chat to Discord, routed per channel: General and
    /// Trade each go to their own Discord channel via a separate incoming webhook.
    ///
    /// Each channel has its own queue and is flushed independently on a timer, so a busy channel
    /// never trips its webhook's rate limit and a slow/dead webhook never blocks the network
    /// thread. A channel is relayed only when the relay is enabled AND that channel has a webhook
    /// URL configured - a channel with no URL is silently skipped.
    ///
    /// Configuration precedence (highest first):
    ///   1. Environment variables (ACE_DISCORD_RELAY_ENABLED, ACE_DISCORD_WEBHOOK_URL_GENERAL,
    ///      ACE_DISCORD_WEBHOOK_URL_TRADE) - lets a single container image be disabled in Stage
    ///      and enabled in Prod purely from compose.
    ///   2. PropertyManager DB properties (discord_relay_enabled, discord_webhook_url_general,
    ///      discord_webhook_url_trade) - runtime toggleable via /modifybool and /modifystring.
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

        // One relay destination per in-game channel. Add a row here (plus the matching config
        // properties) to route another channel. Allegiance/Society/Olthoi are private/group
        // channels and are intentionally not routed.
        private static readonly IReadOnlyDictionary<ChatType, RelayChannel> channels = new Dictionary<ChatType, RelayChannel>
        {
            [ChatType.General] = new RelayChannel(ChatType.General, "ACE_DISCORD_WEBHOOK_URL_GENERAL", "discord_webhook_url_general"),
            [ChatType.Trade]   = new RelayChannel(ChatType.Trade,   "ACE_DISCORD_WEBHOOK_URL_TRADE",   "discord_webhook_url_trade"),
        };

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

            public ChatType ChatType { get; }
            public ConcurrentQueue<string> Queue { get; } = new ConcurrentQueue<string>();

            public RelayChannel(ChatType chatType, string envVarName, string dbPropertyKey)
            {
                ChatType = chatType;
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
            if (!Enabled)
                return;

            if (string.IsNullOrWhiteSpace(message))
                return;

            if (!channels.TryGetValue(chatType, out var channel))
                return;

            if (string.IsNullOrWhiteSpace(channel.WebhookUrl))
                return;

            if (channel.Queue.Count >= MaxQueuedLines)
                return;

            channel.Queue.Enqueue($"`{Sanitize(playerName)}`: {Sanitize(message)}");
        }

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

                foreach (var channel in channels.Values)
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

                        Post(url, batch);
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

        private static void Post(string url, string content)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                content,
                allowed_mentions = new { parse = Array.Empty<string>() },
            });

            // Fire-and-forget: never block the flush timer thread on network I/O.
            _ = PostAsync(url, payload);
        }

        private static async System.Threading.Tasks.Task PostAsync(string url, string payload)
        {
            try
            {
                using var body = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(url, body).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    log.Warn($"[DISCORD] Webhook POST failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception ex)
            {
                log.Warn("[DISCORD] Webhook POST threw", ex);
            }
        }
    }
}
