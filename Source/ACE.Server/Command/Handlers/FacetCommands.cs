using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Entity.Facets;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Player Facets: /facet, /facet &lt;N&gt;, /facet name &lt;N&gt; &lt;text&gt; and /facet help. All the actual
    /// gating, mutation and persistence lives on Player_Facets.cs (Player.CheckFacetGates,
    /// Player.TrySwitchFacet, Player.TrySetFacetName); this file is the thin command-layer dispatch
    /// plus the wording of the first-visit confirmation prompt. See Docs/Facets/DESIGN.md section 8.
    /// </summary>
    public static class FacetCommands
    {
        [CommandHandler("facet", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Player Facets: switch between configurable builds for this character.",
            "[<N> [trim] | pk [trim] | name <N> <text> | help]\n" +
            "  (no args)          - show your facets\n" +
            "  <N>                - turn to facet N, e.g. /facet 2\n" +
            "  <N> trim           - turn to facet N, trimming its skills to cover an experience shortfall\n" +
            "  pk [trim]          - turn to your PK facet, where you are always a player killer\n" +
            "  name <N> <text>    - set facet N's display name, e.g. /facet name 1 Void (N may be pk)\n" +
            "  help               - what a facet keeps, shares and costs")]
        public static void HandleFacet(Session session, params string[] parameters)
        {
            var player = session.Player;

            // facet_enabled gates the WHOLE command family - a plain refusal rather than being hidden,
            // so a player who was told about the feature gets an answer (PropertyManager tunable doc).
            if (!FacetTunables.DialSource().Enabled)
            {
                Msg(player, "Facets are not available on this server yet.");
                return;
            }

            // Every form below - the bare list included - can block the world thread on a shard read
            // (GetCharacterFacetRowsOrEmpty, TrySwitchFacet, TrySetFacetName), so the whole family shares
            // one per-player window, the same five seconds and shape as the /mule vault commands.
            if (!TryStartFacetCommandCore(player, DateTime.UtcNow, out var cooldownRefusal))
            {
                Msg(player, cooldownRefusal);
                return;
            }

            if (parameters.Length == 0)
            {
                HandleList(player);
                return;
            }

            if (string.Equals(parameters[0], "name", StringComparison.OrdinalIgnoreCase))
            {
                HandleName(player, parameters.Skip(1).ToArray());
                return;
            }

            if (string.Equals(parameters[0], "help", StringComparison.OrdinalIgnoreCase))
            {
                Msg(player, ComposeHelp(FacetTunables.DialSource()));
                return;
            }

            if (!TryParseSwitchArgs(parameters, out var targetSlot, out var trim))
            {
                Msg(player, "Usage: /facet [<N> [trim] | pk [trim] | name <N> <text> | help] - type /facet help for what a facet actually does.");
                return;
            }

            // A typed switch is a fresh request: it supersedes any switch still waiting on the vault.
            HandleSwitch(session, player, targetSlot, false, trim, player.BeginFacetSwitchRequest(), 0.0);
        }

        /// <summary>Per-player window between accepted /facet commands. Matches Player.MuleVaultReadCooldownSeconds.</summary>
        internal const int CooldownSeconds = 5;

        internal const string CooldownLine = "You must wait a few seconds before using /facet again.";

        /// <summary>
        /// True and stamps <see cref="Player.PrevFacetCommand"/> when outside the cooldown window; false with
        /// <see cref="CooldownLine"/> otherwise. A refused call does not restamp, so a held-down macro cannot
        /// keep the window open forever. Takes <paramref name="now"/> so a test can drive two calls inside one
        /// window without sleeping - the shape of Player.TryStartMuleVaultCommandCore. Only re-entries that do
        /// not come through HandleFacet (the confirmation accept and the vault poll) are outside it; each of
        /// those is bounded by a typed /facet that was itself gated.
        /// </summary>
        internal static bool TryStartFacetCommandCore(Player player, DateTime now, out string refusal)
        {
            if (now - player.PrevFacetCommand < TimeSpan.FromSeconds(CooldownSeconds))
            {
                refusal = CooldownLine;
                return false;
            }

            player.PrevFacetCommand = now;
            refusal = null;
            return true;
        }

        /// <summary>
        /// "/facet N" or "/facet N trim" (trim case-insensitive). Anything else after N is REFUSED rather
        /// than ignored: a mistyped "trim" silently running a plain switch would hand the player a refusal
        /// that looks like trim does not work. Pure, for unit tests.
        /// </summary>
        internal static bool TryParseSwitchArgs(string[] parameters, out int targetSlot, out bool trim)
        {
            trim = false;
            targetSlot = 0;

            if (parameters == null || parameters.Length == 0 || parameters.Length > 2 || !TryParseSlot(parameters[0], out targetSlot))
                return false;

            if (parameters.Length == 2)
            {
                if (!string.Equals(parameters[1], "trim", StringComparison.OrdinalIgnoreCase))
                    return false;

                trim = true;
            }

            return true;
        }

        /// <summary>
        /// One slot argument: a number, or "pk" (case-insensitive) for the PK facet's reserved slot. The
        /// reserved number itself is NOT accepted when typed - it fails the parse, so the caller shows its
        /// usage line - and the PK facet is only ever reached by its name. Pure, for unit tests.
        /// </summary>
        internal static bool TryParseSlot(string text, out int slot)
        {
            if (string.Equals(text, "pk", StringComparison.OrdinalIgnoreCase))
            {
                slot = Player.PkFacetSlot;
                return true;
            }

            if (int.TryParse(text, out slot) && !Player.IsPkFacetSlot(slot))
                return true;

            slot = 0;
            return false;
        }

        /// <summary>
        /// /facet help. Everything variable in it - the slot count, the unlock levels, whether there is a
        /// location restriction at all and what it is called - is read from the SAME <see cref="FacetDials"/>
        /// the gates read, for the reason SendFacetUnlockNoticeIfDue gives about its own wording: help that
        /// hardcodes a threshold promises a rule the server is not enforcing the moment the tunable moves.
        /// An empty allowlist means no location restriction (CheckFacetGates' remarks on Count == 0), so the
        /// location line is dropped rather than named.
        ///
        /// Pure and static so it is unit-testable without a live Player, the same way
        /// Player.ComposeSkillCreditCost and Player.ComposeAttributeSurplusLine are.
        /// </summary>
        internal static string ComposeHelp(FacetDials dials)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Facets - separate builds for one character.");
            sb.AppendLine();
            sb.AppendLine("  /facet                 - list your facets, with the active one marked");
            sb.AppendLine($"  /facet <N>             - turn to facet N (1-{Player.MaxFacetSlot})");
            sb.AppendLine("  /facet <N> trim        - turn to facet N even when you are short experience for it (see below)");

            if (dials.PkEnabled)
                sb.AppendLine("  /facet pk [trim]       - turn to your PK facet (see below)");

            sb.AppendLine("  /facet name <N> <text> - name facet N, up to 32 characters");
            sb.AppendLine("  /facet help            - this text");
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("  /facet 2               - turn to your second facet");
            sb.AppendLine("  /facet 2 trim          - turn to it, giving up just enough of its skill ranks to afford it");
            sb.AppendLine("  /facet name 1 Void     - names your first facet \"Void\"");
            sb.AppendLine();
            sb.AppendLine("Each facet keeps its own trained and specialized skills, its own class abilities, its own arrangement of your innate attribute points, and the gear it was wearing when you left it.");
            sb.AppendLine("Every facet shares your level, lifetime experience, attribute ranks, vitals, augmentations and spellbook. Switching costs nothing and never resets any of those.");
            sb.AppendLine();

            var unlocks = new List<string>();

            for (var slot = 2; slot <= Player.MaxFacetSlot; slot++)
            {
                var requiredLevel = slot switch
                {
                    2 => dials.Slot2Level,
                    3 => dials.Slot3Level,
                    4 => dials.Slot4Level,
                    _ => 0,
                };

                if (requiredLevel > 0)
                    unlocks.Add($"facet {slot} at level {requiredLevel:N0}");
            }

            sb.Append("Facet 1 always exists.");
            sb.AppendLine(unlocks.Count > 0 ? $" You unlock {string.Join(", ", unlocks)}." : "");

            if (dials.Allowlist.Count > 0)
                sb.AppendLine($"You can only change facets in {dials.AllowlistName}.");

            sb.AppendLine("The first time you turn to a facet you are asked to confirm: it has never been cut, so it starts with its skills untrained - except always-trained and augmentation-specialized ones - and no class abilities learned, and hands back all of its experience, skill credits and class ability points for you to spend differently.");
            sb.AppendLine("Gear you are wearing is unequipped into your pack when you leave a facet, and the facet you turn to re-equips whatever it remembers, telling you about anything it could not.");
            sb.AppendLine("Experience you spend outside your skills - on attributes, vitals or augmentations - is shared by every facet and never comes back, so a facet you left can end up needing more experience than you can free. Plain /facet <N> then refuses and shows exactly which skill ranks /facet <N> trim would remove: the most expensive ranks first, just enough to cover the gap, with any experience left over from the last rank returned to you. You are warned the moment a spend puts one of your facets out of reach.");

            // Omitted entirely while the PK facet is switched off - help must not describe a facet the gates refuse.
            if (dials.PkEnabled)
            {
                sb.AppendLine();
                sb.AppendLine(ComposePkHelpParagraph(dials));
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>The /facet help paragraph for the PK facet. Its unlock level comes from the dials. Pure, for unit tests.</summary>
        internal static string ComposePkHelpParagraph(FacetDials dials)
        {
            return "/facet pk - switch to your PK facet. Your other facets are always non-PK; on the PK facet you are always a player killer, and cannot become non-PK until you switch away (a PK death still grants the usual respite). " +
                   "Class abilities, class ability points, equipment mods, and pickup and turn speed bonuses do not work there. " +
                   $"Unlocks at level {dials.PkLevel:N0}.";
        }

        /// <summary>
        /// The /facet list line for the PK facet, with the same markers the numbered lines carry: its name when
        /// it has one, "(active)" when the player stands on it, and "locked" when the player is below its level
        /// and not already on it. Pure, for unit tests.
        /// </summary>
        internal static string ComposePkListLine(FacetDials dials, string name, bool active, bool unlocked)
        {
            var label = string.IsNullOrWhiteSpace(name) ? "PK facet" : $"PK facet \"{name}\"";

            if (active)
                label += " (active)";

            var line = $"  {label} - always player killer, no class abilities or speed bonuses (unlocks at level {dials.PkLevel:N0})";

            if (!unlocked && !active)
                line += " - locked";

            return line;
        }

        /// <summary>
        /// The first-visit confirmation prompt. The PK facet's names what turning to it means, because the
        /// status change is the part a player must not be surprised by. Pure, for unit tests.
        /// </summary>
        internal static string ComposeFirstVisitPrompt(int targetSlot)
        {
            if (Player.IsPkFacetSlot(targetSlot))
            {
                return "Turn to your PK facet? This face has never been cut: your current gear will be stripped to your pack, " +
                       "and the new facet starts fresh (skills untrained except always-trained and augmentation-specialized ones). " +
                       "On the PK facet you are always a player killer, and class abilities, class ability points, equipment mods, " +
                       "and pickup and turn speed bonuses do not work.";
            }

            return $"Turn to facet {targetSlot}? This face has never been cut: your current " +
                   "gear will be stripped to your pack, and the new facet starts fresh (skills untrained except " +
                   "always-trained and augmentation-specialized ones, no class abilities learned).";
        }

        /// <summary>
        /// The switch entry point, re-entered with confirmed=true from the Confirmation_Custom callback.
        /// Gates are re-checked on EVERY call (Player.CheckFacetGates, and again inside
        /// Player.TrySwitchFacet), including the confirmed re-entry - ClassAbilityCommands' unlearn path
        /// documents why: the player can walk out of the allowlisted area, start a trade, or die while the
        /// confirmation prompt is up.
        ///
        /// WHETHER a confirmation is needed is decided by Player.TrySwitchFacet, from the single
        /// character_facet read it has to do anyway; only the prompt WORDING lives here. The switch used
        /// to answer that question with its own extra blocking read (Player.IsFacetSlotFresh, now gone),
        /// which meant every switch blocked the world tick thread twice - and a shard stall there is paid
        /// by every player in the landblock group, not by the one who typed the command. The confirmed
        /// re-entry runs the whole method again, so it still gets its own fresh read and its own full gate
        /// re-check; a first visit costs two reads, which is once per slot ever.
        ///
        /// <paramref name="trim"/> rides through the confirmation re-entry unchanged, so "/facet N trim" on a
        /// first visit prompts exactly like "/facet N" and then runs as a trimmed switch. A never-cut slot
        /// commits no skill PP, so there is never a shortfall to trim on that pass; carrying the flag is
        /// for consistency, not because it can bite.
        ///
        /// VAULT WAIT. When Player.TrySwitchFacet reports awaitingVault (the facet's remembered gear needs
        /// the account vault, which is still loading), nothing has been mutated, and this waits for the
        /// vault and then re-enters with the same arguments - see <see cref="AwaitVaultThenResume"/>. Like
        /// the confirmation re-entry, the resume re-runs every gate and the fresh row read.
        ///
        /// <paramref name="requestGeneration"/> identifies the player request this call serves (see
        /// Player.BeginFacetSwitchRequest); <paramref name="vaultWaitedSeconds"/> is how long that request
        /// has already waited on the vault, carried across re-entries so the total wait is bounded once.
        /// </summary>
        private static void HandleSwitch(Session session, Player player, int targetSlot, bool confirmed, bool trim, int requestGeneration, double vaultWaitedSeconds)
        {
            if (!player.CheckFacetGates(targetSlot, out var gateRefusal))
            {
                Msg(player, gateRefusal);
                return;
            }

            if (player.TrySwitchFacet(targetSlot, confirmed, trim, out var result, out var needsConfirmation, out var awaitingVault))
            {
                Msg(player, result);

                // Every committed entry into the PK facet, not only the first visit. Only this success
                // branch reaches it: a refused or deferred switch returns false above, leaving the PK
                // facet always targets another slot, and login never comes through HandleSwitch.
                if (Player.IsPkFacetSlot(targetSlot))
                    player.Session?.Network.EnqueueSend(new GameEventPopupString(player.Session, FacetPk.ComposeEntryPopup()));

                return;
            }

            if (awaitingVault)
            {
                AwaitVaultThenResume(session, player, targetSlot, confirmed, trim, requestGeneration, vaultWaitedSeconds);
                return;
            }

            if (!needsConfirmation)
            {
                Msg(player, result);
                return;
            }

            // First visit to the target slot (DESIGN.md section 8). TrySwitchFacet mutated nothing on
            // this path - it refused before its first write - so re-entering it after the prompt is a
            // clean re-run, not a resume.
            var prompt = ComposeFirstVisitPrompt(targetSlot);

            // Accepting the prompt is a fresh player request, so it takes a new generation (superseding any
            // OTHER switch still waiting on the vault) and starts with no vault wait. It cannot cancel its
            // own request's pending state: a request only ever has a pending vault poll after
            // TrySwitchFacet returned awaitingVault, and that method decides needsConfirmation BEFORE its
            // vault gate - so a request is never waiting on the vault and on this prompt at once, and the
            // generation is taken before the confirmed re-entry could arm a poll of its own.
            if (!player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(player.Guid, () => HandleSwitch(session, player, targetSlot, true, trim, player.BeginFacetSwitchRequest(), 0.0)), prompt))
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>How often a switch waiting on the vault re-checks readiness, and the most it waits in total.</summary>
        internal const double VaultPollSeconds = 0.5;
        internal const double VaultMaxWaitSeconds = 20.0;

        internal const string VaultLoadingLine = "Your vault is loading; your facet switch will continue in a moment.";
        internal const string VaultTimeoutLine = "Your vault took too long to load, so your facet was not changed. Try the switch again.";

        /// <summary>What one tick of the vault poll should do.</summary>
        internal enum VaultPollAction
        {
            /// <summary>The request is gone - the character is logging out, logged out or relogged (see IsPollPlayerOnline), or a newer /facet request superseded it. Stop silently.</summary>
            Abandon,

            /// <summary>Still loading, within budget. Poll again.</summary>
            KeepWaiting,

            /// <summary>Still loading and the total budget is spent. Tell the player; nothing was changed.</summary>
            GiveUp,

            /// <summary>
            /// Ready, or failed for a reason waiting will not fix, or there is no store at all: re-enter the
            /// switch, whose own gate then proceeds or refuses with the real reason.
            /// </summary>
            Resume,
        }

        /// <summary>
        /// Whether the Player a pending vault poll captured is still the live, staying character on its
        /// session. A poll that gets false abandons silently, so a detached Player never has a switch run
        /// against it.
        ///
        /// NOT <c>Session?.Network != null</c>: Session.DropSession releases Network's resources but
        /// deliberately leaves the reference set (Session.cs, DropSession), so that test never fires on a
        /// disconnect. What does change is Session.Player: SendFinalLogOffMessages sets it to null once
        /// logoff completes, and a relog assigns a NEW Player - so identity with the captured instance is
        /// the test. The request generation cannot stand in for it, because the counter lives on the Player
        /// instance and a detached Player's counter never moves.
        ///
        /// Identity alone still leaves the window before Session.Player is cleared, so two flags that mark
        /// a logout already under way also count as offline:
        ///   - IsLoggingOut, set by Player.LogOut_Inner at the start of the ~6 s logout (the character stays
        ///     on its landblock and on the session until that completes);
        ///   - PKLogout, set by Player.LogOut when a player killer's logout is delayed by pk_timer, before
        ///     LogOut_Inner runs.
        /// Neither is ever cleared on a Player instance (a relog constructs a new one), so neither can
        /// abandon a poll for a player who is actually staying.
        ///
        /// Typed as object so the decision is unit-testable without constructing a Player.
        /// </summary>
        internal static bool IsPollPlayerOnline(object sessionPlayer, object capturedPlayer, bool isLoggingOut, bool pkLogout)
        {
            if (capturedPlayer == null || !ReferenceEquals(sessionPlayer, capturedPlayer))
                return false;

            return !isLoggingOut && !pkLogout;
        }

        /// <summary>True once a request has waited its whole budget. Shared by the poll and a re-entered switch.</summary>
        internal static bool HasVaultWaitExpired(double waitedSeconds) => waitedSeconds >= VaultMaxWaitSeconds;

        /// <summary>
        /// One tick of the vault poll, as a pure decision. The abandon checks come FIRST and
        /// <paramref name="probeVault"/> is only invoked after them, because probing is not free of side
        /// effects: GetStore creates a store and TryCheckReady starts its load, which is pointless for a
        /// player who has logged out or a request that no longer stands.
        ///
        /// The probe reads ONLY in-memory store readiness. The poll deliberately never re-runs
        /// TrySwitchFacet per tick: that method does a blocking shard read on the world thread, and a stall
        /// there is paid by every player on it.
        /// </summary>
        internal static VaultPollAction DecideVaultPoll(bool playerOnline, int requestGeneration, int currentGeneration, Func<Player.FacetVaultStatus> probeVault, double waitedSeconds)
        {
            if (!playerOnline || requestGeneration != currentGeneration)
                return VaultPollAction.Abandon;

            var status = probeVault != null ? probeVault() : Player.FacetVaultStatus.NoStore;

            if (!status.IsStillLoading)
                return VaultPollAction.Resume;

            return HasVaultWaitExpired(waitedSeconds) ? VaultPollAction.GiveUp : VaultPollAction.KeepWaiting;
        }

        /// <summary>
        /// Entered when TrySwitchFacet refused with awaitingVault. Tells the player once (only on the
        /// request's first wait - a re-entered switch that is somehow still waiting carries its elapsed
        /// time and stays quiet), then polls. The clock is NOT restarted by a re-entry: an already-spent
        /// budget gives up here rather than arming another poll.
        /// </summary>
        private static void AwaitVaultThenResume(Session session, Player player, int targetSlot, bool confirmed, bool trim, int requestGeneration, double vaultWaitedSeconds)
        {
            if (HasVaultWaitExpired(vaultWaitedSeconds))
            {
                Msg(player, VaultTimeoutLine);
                return;
            }

            if (vaultWaitedSeconds <= 0.0)
                Msg(player, VaultLoadingLine);

            ScheduleVaultPoll(session, player, targetSlot, confirmed, trim, requestGeneration, vaultWaitedSeconds);
        }

        /// <summary>
        /// Modelled on MuleSummonHandler.SendOrDeferVaultLine: a delay then one action on the player's own
        /// queue, re-armed until the decision says otherwise. The store is re-fetched from
        /// AccountVaultManager on every tick rather than captured, so a store the idle sweep retired during
        /// the wait is replaced (and its load started) instead of being polled forever.
        /// </summary>
        private static void ScheduleVaultPoll(Session session, Player player, int targetSlot, bool confirmed, bool trim, int requestGeneration, double waitedSeconds)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(VaultPollSeconds);
            chain.AddAction(player, () =>
            {
                var waited = waitedSeconds + VaultPollSeconds;

                var action = DecideVaultPoll(
                    IsPollPlayerOnline(player.Session?.Player, player, player.IsLoggingOut, player.PKLogout),
                    requestGeneration,
                    player.FacetSwitchRequestGeneration,
                    () => Player.FacetVaultStatus.Probe(AccountVaultManager.GetStore(player.Account?.AccountId ?? 0)),
                    waited);

                switch (action)
                {
                    case VaultPollAction.Abandon:
                        return;

                    case VaultPollAction.KeepWaiting:
                        ScheduleVaultPoll(session, player, targetSlot, confirmed, trim, requestGeneration, waited);
                        return;

                    case VaultPollAction.GiveUp:
                        Msg(player, VaultTimeoutLine);
                        return;

                    case VaultPollAction.Resume:
                        // The same arguments and the same request, with the elapsed wait carried forward.
                        // Re-runs CheckFacetGates, the fresh row read and every refusal, exactly as the
                        // confirmation re-entry does - leaving the allowlisted area, trading, teleporting,
                        // becoming busy or dying during the wait yields the ordinary refusal.
                        HandleSwitch(session, player, targetSlot, confirmed, trim, requestGeneration, waited);
                        return;
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// Bare /facet: number, name, unlock state, and a one-line specialized-skill summary for every
        /// slot, with the active one marked. The active slot's summary comes from the LIVE character
        /// (it may have no stored row yet, if it has never been switched away from); every other slot
        /// reads its stored row, or reports "not yet cut" when there is none.
        /// </summary>
        private static void HandleList(Player player)
        {
            // Read-only display: ADVISORY, per Player.GetCharacterFacetRowsOrEmpty's remarks - a failed
            // or timed-out read just shows a stale/empty listing here, never a mutation.
            var rows = player.GetCharacterFacetRowsOrEmpty();
            var dials = FacetTunables.DialSource();

            var sb = new StringBuilder();
            sb.AppendLine("Facets:");

            for (var slot = 1; slot <= Player.MaxFacetSlot; slot++)
            {
                var active = slot == player.ActiveFacetSlot;
                var row = rows.FirstOrDefault(r => r.Slot == (byte)slot);

                var requiredLevel = slot switch
                {
                    2 => dials.Slot2Level,
                    3 => dials.Slot3Level,
                    4 => dials.Slot4Level,
                    _ => 0,
                };

                var unlocked = requiredLevel <= 0 || (player.Level ?? 0) >= requiredLevel;

                var label = string.IsNullOrWhiteSpace(row?.Name) ? $"Facet {slot}" : $"Facet {slot} \"{row.Name}\"";

                if (active)
                    label += " (active)";

                // A grandfathered active slot (its threshold raised past the player's current level
                // while they stood on it - DESIGN.md section 2, "the player keeps the slot") must never
                // display as locked: they are not locked out of anything, they simply could not switch
                // back in if they left. Only a non-active, currently out-of-reach slot shows this line.
                if (!unlocked && !active)
                {
                    sb.AppendLine($"  {label} - locked until level {requiredLevel:N0}");
                    continue;
                }

                List<FacetSkillEntry> skills;

                if (active)
                    skills = player.CaptureFacetSkills();
                else if (row != null)
                    // A stored row can predate an augmentation the player has since bought - see
                    // Player.NormalizeIncomingFacetSkills - so without this, a specialized aug-spec skill
                    // (a tinkering skill, or Salvaging) can list here as merely Trained on a facet the
                    // player is not standing on, even though it would come back Specialized the moment
                    // they switched to it.
                    skills = player.NormalizeIncomingFacetSkills(FacetSnapshot.DeserializeSkills(row.SkillsJson));
                else
                    skills = null;

                string summary;

                if (skills == null)
                {
                    summary = "not yet cut";
                }
                else
                {
                    var specialized = skills
                        .Where(s => s.Sac == SkillAdvancementClass.Specialized)
                        .Select(s => s.Skill.ToSentence())
                        .ToList();

                    summary = specialized.Count > 0 ? string.Join(", ", specialized) : "no specialized skills";
                }

                sb.AppendLine($"  {label} - {summary}");
            }

            // The PK facet, only while it is switched on - or while the player is standing on it, so a player
            // left there when it was switched off can still see where they are.
            if (dials.PkEnabled || player.IsOnPkFacet)
            {
                var pkRow = rows.FirstOrDefault(r => Player.IsPkFacetSlot(r.Slot));
                var pkUnlocked = dials.PkLevel <= 0 || (player.Level ?? 0) >= dials.PkLevel;

                sb.AppendLine(ComposePkListLine(dials, pkRow?.Name, player.IsOnPkFacet, pkUnlocked));
            }

            sb.AppendLine("Turn to one with /facet <N>" + (dials.PkEnabled ? " or /facet pk" : "") + ", name one with /facet name <N> <text>. Type /facet help for what a facet keeps and what it shares.");

            Msg(player, sb.ToString().TrimEnd());
        }

        private static void HandleName(Player player, string[] args)
        {
            if (args.Length < 2 || !TryParseSlot(args[0], out var slot))
            {
                Msg(player, "Usage: /facet name <N> <text> - for example, /facet name 1 Void names your first facet \"Void\".");
                return;
            }

            var name = string.Join(" ", args.Skip(1)).Trim();

            if (name.Length > 32)
                name = name.Substring(0, 32);

            if (!player.TrySetFacetName(slot, name, out var refusal))
            {
                Msg(player, refusal);
                return;
            }

            Msg(player, $"{Player.FacetSlotDisplayCapitalized(slot)} is now named \"{name}\".");
        }

        private static void Msg(Player player, string text)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
