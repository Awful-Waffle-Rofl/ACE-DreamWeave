using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md section 11): the summoner-side half of the summon flow.
    /// All gating and spawn logic lives in <see cref="MuleSummonHandler"/> - this file only tracks the
    /// one-per-summoner slot and resolves /mule's optional target name to an account.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// The summoner's one live mule vendor window, mirroring <see cref="CurrentActivePet"/>
        /// (Player_Use.cs:266). Cleared in WorldObject.Destroy exactly as the pet slot is
        /// (WorldObject.cs) - see that file's PersonalVendor branch.
        /// </summary>
        public PersonalVendor CurrentSummonedVendor { get; set; }

        /// <summary>
        /// /mule [playername] (command wiring is a later task's PlayerCommands.cs work; this is the
        /// HandleActionX half of the house thin-handler-plus-HandleActionX split, per /dn's template at
        /// PlayerCommands.cs:1099-1112).
        ///
        /// No argument summons the caller's own store. A named argument resolves that character's
        /// account via AccountVaultManager.TryResolveAccountForCharacter and summons THAT store, subject
        /// to the grant list checked inside MuleSummonHandler.TryGate (DESIGN section 10) - an unknown
        /// name is refused before ever reaching TrySummon, since there is no account to gate against.
        /// </summary>
        public void HandleActionSummonMule(string playerName)
        {
            if (!TrySummonMuleCore(playerName, DateTime.UtcNow, ResolveVaultCharacter, out var storeAccountId, out var ownerName, out var failReason))
            {
                SendTransientError(failReason);
                return;
            }

            if (!MuleSummonHandler.TrySummon(this, storeAccountId, out failReason, ownerName))
            {
                SendTransientError(failReason ?? AccountVaultStore.UnavailableMessage);
                return;
            }

            // The vault-fullness line that used to sit here moved into MuleSummonHandler.TrySummon,
            // which is the ONLY path both callers share - the contract item reaches TrySummon through
            // TryHandleUse and was silent while this copy lived at the command call site.
        }

        /// <summary>
        /// /mule search [pattern] (DESIGN section 13 addendum): filters the caller's OWN live mule
        /// vendor window down to entries whose name or spells match a regex pattern - purely display
        /// side, via <see cref="PersonalVendor.SetSearchFilter"/>. Never touches the underlying store,
        /// ledger, or deposit path.
        ///
        /// A bare/empty pattern clears the filter instead of compiling one.
        ///
        /// Throttling note: a rebuild allocates display guids exactly like a summon does, so this shares
        /// the same TryStartMuleVaultCommandCore window every other /mule vault command uses - but NOT
        /// twice in one call. When no vendor is live, summoning goes through
        /// <see cref="TrySummonMuleCore"/>, which already stamps that SAME window internally; calling it
        /// a second time here (or first, before falling into that branch) would refuse the summon on its
        /// own just-set timestamp. So the explicit call below only covers the two paths that do NOT also
        /// summon - clearing/refreshing an already-live vendor's view - and the summon branch is gated by
        /// TrySummonMuleCore's own internal call instead. Both are the SAME shared window
        /// (PrevMuleVaultCommand / MuleVaultReadCooldownSeconds), just reached from different call sites.
        /// </summary>
        public void HandleActionMuleSearch(string pattern)
        {
            var isEmpty = string.IsNullOrWhiteSpace(pattern);
            var vendor = CurrentSummonedVendor;
            var vendorLive = vendor != null && !vendor.IsDestroyed;

            if (isEmpty)
            {
                if (!vendorLive)
                {
                    SendTransientError("No vault vendor is summoned; there is no filter to clear.");
                    return;
                }

                if (!TryStartMuleVaultCommandCore(DateTime.UtcNow, out var failReason))
                {
                    SendTransientError(failReason);
                    return;
                }

                vendor.SetSearchFilter(null);

                // A cold store's TryPrepareApproach can refuse here too (see the non-empty-pattern
                // branches' remarks below), but "cleared" stays true regardless - the filter really is
                // gone in memory - so unlike the "N of M" report this reply is not gated on the refresh
                // having actually succeeded.
                RefreshMuleVendorView(vendor, out _);

                Session.Network.EnqueueSend(new GameMessageSystemChat("Vault filter cleared.", ChatMessageType.System));
                return;
            }

            if (!ItemTextSearch.TryCompile(pattern, out var regex, out var compileError))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat(compileError, ChatMessageType.System));
                return;
            }

            if (vendorLive)
            {
                if (!TryStartMuleVaultCommandCore(DateTime.UtcNow, out var failReason))
                {
                    SendTransientError(failReason);
                    return;
                }

                vendor.SetSearchFilter(regex);

                if (!RefreshMuleVendorView(vendor, out var refreshFailReason))
                {
                    // Fix round C, C1: a cold store (async load still in flight - see
                    // MuleSummonHandler's own load kickoff, and AccountVaultStore.IsLoaded) makes
                    // TryPrepareApproach/TryAuthorize refuse with StillLoadingMessage, and RebuildView
                    // never runs - so LastRebuildShown/LastRebuildTotal are still their default 0/0.
                    // The filter IS attached and will apply once the view actually builds; what must
                    // NOT happen is reporting that stale 0/0 as though it were the real count.
                    SendTransientError(refreshFailReason);
                    return;
                }
            }
            else
            {
                if (!TrySummonMuleCore(string.Empty, DateTime.UtcNow, ResolveVaultCharacter, out var storeAccountId, out var ownerName, out var failReason))
                {
                    SendTransientError(failReason);
                    return;
                }

                if (!MuleSummonHandler.TrySummon(this, storeAccountId, out failReason, ownerName))
                {
                    SendTransientError(failReason ?? AccountVaultStore.UnavailableMessage);
                    return;
                }

                vendor = CurrentSummonedVendor;

                if (vendor == null)
                {
                    // Should not happen - TrySummon just succeeded - but a search filter with nothing
                    // to attach it to is not a message worth sending; the summon itself already reported.
                    return;
                }

                vendor.SetSearchFilter(regex);

                // Forces an immediate view build (not a panel open - TryPrepareApproach sends no network
                // message) so the "N of M" reply below is accurate rather than reporting 0 of 0 for a
                // view that has never been built. The panel itself still opens on the player's next
                // click, same as any freshly summoned mule.
                //
                // Fix round C, C1: a freshly summoned vendor's store is very often STILL LOADING its
                // async index (summoning is what starts that load - see MuleSummonHandler), so
                // TryPrepareApproach legitimately refuses here with StillLoadingMessage and
                // LastRebuildShown/LastRebuildTotal never move off their 0/0 default. Reporting "0 of 0
                // entries shown" in that case would flatly contradict this comment's own stated intent -
                // the filter IS attached (SetSearchFilter above already ran) and will apply the moment
                // the panel actually opens; the player is told it is still loading instead of being told
                // a false count.
                if (!vendor.TryPrepareApproach(VaultActor.From(this), out var approachFailReason))
                {
                    SendTransientError(approachFailReason);
                    return;
                }
            }

            var trimmedPattern = pattern.Trim();

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"Vault filter \"{trimmedPattern}\": {vendor.LastRebuildShown} of {vendor.LastRebuildTotal} entries shown.",
                ChatMessageType.System));
        }

        /// <summary>
        /// Refreshes an already-live mule vendor's view after its search filter changed. If the panel is
        /// currently open (LastOpenedContainerId points at it), re-approaches through the ordinary
        /// ApproachVendor path so the open panel updates in place (Buy's own re-send after a rejected
        /// purchase, PersonalVendor.cs:1030, is the established precedent for this). Otherwise forces a
        /// rebuild through the same authorised path (TryPrepareApproach) without opening anything, so the
        /// "N of M" count reported back is accurate even though nothing is drawn yet.
        ///
        /// Fix round C, C1: returns whether the refresh actually ran, with the failure reason, so a
        /// caller composing the "N of M" reply can tell a genuine rebuild apart from a refused one
        /// (most commonly AccountVaultStore.StillLoadingMessage for a store whose async load has not
        /// finished) rather than reporting the view's untouched 0/0 default as a real count.
        /// </summary>
        private bool RefreshMuleVendorView(PersonalVendor vendor, out string failReason)
        {
            if (LastOpenedContainerId == vendor.Guid)
            {
                // ApproachVendor's own override already sends a transient error to the player on
                // failure (PersonalVendor.cs), but it does not hand the reason back to its caller, so
                // TryPrepareApproach is called directly first purely to capture failReason for the
                // caller's own gate. On success this repeats the SAME authorised check ApproachVendor is
                // about to run again internally; RebuildView's own cheap-exit (builtFromVersion already
                // matching) makes that repeat a no-op rather than a second materialization pass.
                if (!vendor.TryPrepareApproach(VaultActor.From(this), out failReason))
                    return false;

                vendor.ApproachVendor(this);
                return true;
            }

            return vendor.TryPrepareApproach(VaultActor.From(this), out failReason);
        }

        /// <summary>
        /// Resolves a character name to the owning account. Its own delegate type, so
        /// <see cref="TrySummonMuleCore"/> can be driven from a test without PlayerManager's name index
        /// or a shard database - and, more to the point, so a test can COUNT how many times it is
        /// reached, which is the only way to observe a throttle that works by not calling it.
        /// </summary>
        internal delegate bool VaultCharacterResolver(string characterName, out string ownerName, out uint accountId);

        private static bool ResolveVaultCharacter(string characterName, out string ownerName, out uint accountId)
        {
            return AccountVaultManager.TryResolveCharacter(characterName, out _, out ownerName, out accountId);
        }

        /// <summary>
        /// Fix round 2, F5: the throttled, testable core of <see cref="HandleActionSummonMule"/> -
        /// everything up to the point where a live Player is unavoidable.
        ///
        /// THE THROTTLE IS THE FIRST THING IT DOES, and that placement is the fix. /mule summon was the
        /// one command on this surface with no per-player cooldown at all: grant, revoke, access and log
        /// have carried TryStartMuleVaultCommand from fix round 1, while a NAMED summon ran
        /// AccountVaultManager.TryResolveCharacter - a synchronous ShardDbContext SELECT on the world
        /// thread - for every valid name given, and then TryGate ran a second SELECT for the grant list.
        /// Both happen before MuleSummonHandler.TryStartSummon, which is stamped only AFTER the gate so
        /// that a refused summon does not burn the summon window. That deliberate ordering is what left
        /// an unlimited two-queries-per-command loop open to anyone who could type a name.
        ///
        /// The bare form is throttled too. It costs nothing to include (it reads the caller's own
        /// Account and never resolves anything), the refusal message already reads correctly for it,
        /// and leaving it out would mean /mule with no argument was the one spammable spelling.
        ///
        /// TryStartSummon's own five second window is unchanged and still stamped after the gate: it
        /// bounds the spawn/despawn broadcast a summon that ACTUALLY HAPPENS costs everyone standing
        /// nearby, which is a different harm from the one this bounds. A refused summon now consumes
        /// the shared vault-command window - meaning a player refused for standing in the wrong
        /// landblock waits before /mule log - and that is the intended trade, since the refusal cost
        /// two shard queries to reach.
        /// </summary>
        internal bool TrySummonMuleCore(string playerName, DateTime now, VaultCharacterResolver resolve,
                                        out uint storeAccountId, out string ownerName, out string failReason)
        {
            storeAccountId = 0;
            ownerName = null;

            if (!TryStartMuleVaultCommandCore(now, out failReason))
                return false;

            // DESIGN 12: the displayed vendor name is derived from the OWNING account's character and
            // never from player input. Resolve it server-side here and hand it to TrySummon, which is
            // the only thing that assigns it - and which must do so before EnterWorld, because the
            // create message broadcast to bystanders serializes Name.
            if (string.IsNullOrWhiteSpace(playerName))
            {
                storeAccountId = Account?.AccountId ?? 0;
                ownerName = Name;
                return true;
            }

            if (!resolve(playerName, out ownerName, out storeAccountId) || storeAccountId == 0)
            {
                ownerName = null;
                storeAccountId = 0;
                failReason = $"{playerName} is not a known character.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// /mule grant &lt;name&gt; [withdraw] (DESIGN section 13, command parsing in
        /// MuleCommands.MuleCommandParser). Delegates entirely to AccountVaultStore.TryGrant, which
        /// self-enqueues onto the store's mutation queue (TryGrantCore asserts it is running there) and
        /// already refuses a grant to yourself or to an unknown character with a player-facing message -
        /// see TryGrantCore's own remarks. This method's only job is resolving the caller's own store
        /// and turning the result into chat.
        ///
        /// Fix round 1, F5: confirms with the CANONICAL name AccountVaultStore resolved and stored, not
        /// the raw text the player typed - a case-mismatched grant now confirms the same spelling
        /// "@mule access" will list, rather than echoing back what the player typed and leaving them
        /// unable to find their own grant afterward.
        ///
        /// Fix round 2, F6: shares the per-player command cooldown with the other three /mule vault
        /// commands, and only tells the grantee when the grant actually changed something and that
        /// grantee has not been told recently - see <see cref="TryNotifyGrantee"/>.
        /// </summary>
        public void HandleActionMuleGrant(string granteeName, bool canWithdraw)
        {
            if (!TryStartMuleVaultCommand())
                return;

            var store = AccountVaultManager.GetStore(Account?.AccountId ?? 0);

            if (store == null)
            {
                SendTransientError(AccountVaultStore.UnavailableMessage);
                return;
            }

            if (!store.TryGrant(this, granteeName, canWithdraw, out var canonicalName, out var changed, out var failReason))
            {
                SendTransientError(failReason ?? AccountVaultStore.UnavailableMessage);
                return;
            }

            // Fix round B, B5: two things the owner was never told.
            //
            // (1) A deposit-only grantee is still admitted to the FULL panel - gate 1 deliberately lets
            // them see everything in the vault, because an empty panel would be indistinguishable from a
            // bug (DESIGN 10). "May now deposit" alone reads as a one-way letterbox; it is not, and the
            // owner needs to know that before sharing something they would rather keep private.
            //
            // (2) Ownership is per ACCOUNT (AccountVaultStore keys on AccountId) but a grant is per
            // CHARACTER (the grant row keys on GranteeCharacterGuid) - both correct, neither previously
            // stated. A player with several characters on one account needs to know the grant does not
            // follow them to an alt.
            var confirmation = canWithdraw
                ? $"{canonicalName} may now deposit to and withdraw from your vault. This grant covers only that character."
                : $"{canonicalName} may now deposit to your vault. They can see everything in it, but cannot take anything out. This grant covers only that character.";

            Session.Network.EnqueueSend(new GameMessageSystemChat(confirmation, ChatMessageType.System));

            // Fix round B, B4: the grantee is otherwise never told a grant exists at all. Notify them
            // if online; there is no offline mail system to hang this on, so an offline grantee simply
            // never sees it (out of scope for this fix - see the report). Resolved by the STORE's
            // canonical name, same reasoning as the owner confirmation above (fix round 1, F5): a
            // case-mismatched grant still finds the right online player.
            //
            // Fix round 2, F6: nothing is sent for a grant that changed nothing, and TryNotifyGrantee
            // bounds how often the SAME grantee can be reached by one owner. The owner still gets the
            // confirmation above every time - the rate limit applies only to the message aimed at
            // somebody else. A first grant to a grantee who has never been granted before passes both
            // conditions and notifies normally.
            if (!changed)
                return;

            var grantee = PlayerManager.GetOnlinePlayer(canonicalName);

            if (grantee != null && TryNotifyGrantee(grantee.Guid.Full))
            {
                var granteeLevel = canWithdraw ? "deposit and withdraw" : "deposit only";

                // The command is quoted when the owner's name needs it. This line is the only place a
                // grantee is ever told what to type, so an instruction that cannot be typed back
                // verbatim strands exactly the player it exists to help - and a spaced or "+"-prefixed
                // owner name is common enough on this fork to hit immediately.
                var openCommand = $"/mule {MuleCommandParser.QuoteNameIfNeeded(Name)}";

                grantee.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{Name} has shared their vault with you - use {openCommand} to open it. You may {granteeLevel}.",
                    ChatMessageType.System));
            }
        }

        /// <summary>
        /// /mule revoke &lt;name&gt; (DESIGN section 13). Takes effect immediately with no session
        /// invalidation - AccountVaultStore.TryGetAccess is re-resolved inside every transaction (DESIGN
        /// section 10), so a grantee mid-window is refused on their very next deposit or withdraw.
        ///
        /// Fix round 1, F5: confirms with the CANONICAL name from the grant row's own snapshot, for the
        /// same reason HandleActionMuleGrant does - see that method's remarks.
        ///
        /// Fix round 2, F6: shares the per-player command cooldown with the other three /mule vault
        /// commands - see <see cref="PrevMuleVaultCommand"/>.
        /// </summary>
        public void HandleActionMuleRevoke(string granteeName)
        {
            if (!TryStartMuleVaultCommand())
                return;

            var store = AccountVaultManager.GetStore(Account?.AccountId ?? 0);

            if (store == null)
            {
                SendTransientError(AccountVaultStore.UnavailableMessage);
                return;
            }

            if (!store.TryRevoke(this, granteeName, out var canonicalName, out var failReason))
            {
                SendTransientError(failReason ?? AccountVaultStore.UnavailableMessage);
                return;
            }

            Session.Network.EnqueueSend(new GameMessageSystemChat($"{canonicalName} no longer has access to your vault.", ChatMessageType.System));
        }

        /// <summary>
        /// /mule access (DESIGN section 13): lists the grants on the caller's OWN store. A player named
        /// "Grant", "Revoke", "Access" or "Log" cannot be summoned by /mule &lt;name&gt; (the reserved
        /// words win, see MuleCommands.MuleCommandParser) - this is what lets that owner confirm the
        /// grant still exists rather than being left to wonder.
        ///
        /// AccountVaultStore.GetGrants returns null for a READ FAILURE and an empty list for "shared
        /// with nobody" - these are never the same message. Reporting a failed read as "shared with
        /// nobody" would tell an owner their sharing was silently dropped when it was only unreadable.
        ///
        /// Fix round 1, F6: shares a per-player cooldown with <see cref="HandleActionMuleLog"/> - see
        /// <see cref="PrevMuleVaultCommand"/>'s remarks for why.
        /// </summary>
        public void HandleActionMuleAccess()
        {
            if (!TryStartMuleVaultCommand())
                return;

            var store = AccountVaultManager.GetStore(Account?.AccountId ?? 0);

            if (store == null)
            {
                SendTransientError(AccountVaultStore.UnavailableMessage);
                return;
            }

            var grants = store.GetGrants();

            if (grants == null)
            {
                SendTransientError(AccountVaultStore.UnavailableMessage);
                return;
            }

            if (grants.Count == 0)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("You have not shared your vault with anyone.", ChatMessageType.System));
                return;
            }

            Session.Network.EnqueueSend(new GameMessageSystemChat("Your vault is shared with:", ChatMessageType.System));

            foreach (var grant in grants)
            {
                var level = grant.CanWithdraw ? "deposit and withdraw" : "deposit only";
                Session.Network.EnqueueSend(new GameMessageSystemChat($"  {grant.GranteeCharacterName} ({level})", ChatMessageType.System));
            }
        }

        /// <summary>
        /// /mule log (DESIGN section 13 and the closing paragraph of section 10): the caller's own
        /// recent vault activity - deposits, withdrawals, grants and revocations, each with the actor
        /// who did it. Actor and grantee names in these rows are SNAPSHOTS taken at write time, so a row
        /// still reads correctly after a rename or a character delete.
        ///
        /// AccountVaultStore.GetRecentLog returns null for a READ FAILURE and an empty list for
        /// genuinely no activity yet - the same null-versus-empty distinction as
        /// <see cref="HandleActionMuleAccess"/>, and for the same reason: a theft report answered with
        /// "no activity" when the log was merely unreadable is worse than no answer at all.
        ///
        /// Fix round 1, F6: shares a per-player cooldown with <see cref="HandleActionMuleAccess"/>.
        /// Fix round 1, F4: the detail column is formatted by <see cref="FormatLogDetail"/>, a pure
        /// helper extracted specifically so it is testable without a Player - see that method's remarks.
        /// </summary>
        public void HandleActionMuleLog()
        {
            const int displayLimit = 20;

            if (!TryStartMuleVaultCommand())
                return;

            var store = AccountVaultManager.GetStore(Account?.AccountId ?? 0);

            if (store == null)
            {
                SendTransientError(AccountVaultStore.UnavailableMessage);
                return;
            }

            var rows = store.GetRecentLog(displayLimit);

            if (rows == null)
            {
                SendTransientError(AccountVaultStore.UnavailableMessage);
                return;
            }

            if (rows.Count == 0)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("There is no recent activity on your vault.", ChatMessageType.System));
                return;
            }

            Session.Network.EnqueueSend(new GameMessageSystemChat("Recent vault activity:", ChatMessageType.System));

            foreach (var row in rows)
            {
                var action = (AccountVaultAction)row.Action;
                var detail = FormatLogDetail(action, row.ItemName, row.Count);

                Session.Network.EnqueueSend(new GameMessageSystemChat($"  {row.Timestamp:u} {row.ActorCharacterName} {action} {detail}", ChatMessageType.System));
            }
        }

        /// <summary>
        /// Fix round 1, F4: pure formatting for one /mule log line's detail column, extracted out of
        /// HandleActionMuleLog so it is testable without a Player - see MuleGrantTests.cs.
        ///
        /// WriteLog (AccountVaultStore.cs) puts the withdraw flag in Count for a Grant row (1 for
        /// deposit+withdraw, 0 for deposit-only) and leaves Count at 0 for a Revoke row. Rendering
        /// either of those through the generic "x{count}" units-count format is wrong twice over: a
        /// deposit-only grant (Count 0) is indistinguishable from a revoke, and a deposit+withdraw grant
        /// (Count 1) reads as "x1" beside an unrelated "x1" on a genuine single-unit withdraw. Grant and
        /// Revoke rows are branched out here instead.
        /// </summary>
        internal static string FormatLogDetail(AccountVaultAction action, string itemName, long count)
        {
            switch (action)
            {
                case AccountVaultAction.Grant:
                    return $"{itemName} ({(count > 0 ? "deposit and withdraw" : "deposit only")})";

                case AccountVaultAction.Revoke:
                    return itemName;

                default:
                    return count > 0 ? $"{itemName} x{count}" : itemName;
            }
        }

        /// <summary>
        /// Fix round 1, F6: last time this player successfully ran a /mule vault command. Shared
        /// between them - they read the same store's data over the same shard round trip
        /// (ShardDatabase_AccountVault.cs), and the cooldown exists to bound that round trip, not to
        /// limit any one command individually. Plain in-memory field, never persisted - mirrors
        /// Player.PrevObjSend (Player.cs:93), the existing precedent for this exact shape (the
        /// "objsend" cooldown at PlayerCommands.cs).
        ///
        /// Fix round 2, F6: renamed from PrevMuleVaultRead because /mule grant and /mule revoke now
        /// share it. They were never covered, and they are strictly MORE expensive than the two reads
        /// that were - each resolves a character and then writes a grant row and an audit row.
        /// </summary>
        public DateTime PrevMuleVaultCommand;

        /// <summary>
        /// Fix round 1, F6: GetGrants/GetRecentLog each open a ShardDbContext and block
        /// (ShardDatabase_AccountVault.cs), and /mule access and /mule log both run inline on a world
        /// thread (WorldManager.UpdateWorld -> InboundMessageManager). Five seconds - not the five
        /// MINUTES "objsend" uses - because these are legitimate to check right after a deposit or a
        /// grant, and a long cooldown would be hostile to exactly the "did that land?" use these
        /// commands exist for; five seconds still defeats a client-tick-rate macro completely.
        ///
        /// Fix round 2, F6: the "Read" in this name and in
        /// <see cref="IsMuleVaultReadOnCooldown"/>'s now covers the WRITE commands too - both are kept
        /// at their original names so the tests already pinning them keep compiling. Nothing about the
        /// five seconds changes for grant and revoke: they are more expensive than the reads, so a
        /// window sized to bound the reads bounds them as well.
        /// </summary>
        private const int MuleVaultReadCooldownSeconds = 5;

        /// <summary>
        /// True and stamps <see cref="PrevMuleVaultCommand"/> when outside the cooldown window; false
        /// and refuses with a chat message otherwise. The window arithmetic itself is the pure, testable
        /// <see cref="IsMuleVaultReadOnCooldown"/> - this wrapper is the only part that needs a live
        /// Player (the field and the outbound message).
        /// </summary>
        private bool TryStartMuleVaultCommand()
        {
            if (!TryStartMuleVaultCommandCore(DateTime.UtcNow, out var failReason))
            {
                SendTransientError(failReason);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Fix round 2, F5: the same window test and the same stamp, with the outbound message handed
        /// back rather than sent. Split out so <see cref="TrySummonMuleCore"/> can throttle without a
        /// Session - the summon path is the one that needed the throttle and the one no test could
        /// drive while sending was baked in.
        ///
        /// Takes <paramref name="now"/> rather than reading the clock, so a test can drive two calls
        /// inside one window without sleeping.
        /// </summary>
        internal bool TryStartMuleVaultCommandCore(DateTime now, out string failReason)
        {
            if (IsMuleVaultReadOnCooldown(PrevMuleVaultCommand, now, MuleVaultReadCooldownSeconds))
            {
                failReason = "You have used a vault command too recently. Try again in a few seconds.";
                return false;
            }

            PrevMuleVaultCommand = now;

            failReason = null;
            return true;
        }

        /// <summary>
        /// Fix round 1, F6: the pure window-arithmetic half of the /mule command cooldown, extracted so
        /// it is testable without a Player - see MuleGrantTests.cs. Kept named "Read" through fix round
        /// 2's widening to grant and revoke; see MuleVaultReadCooldownSeconds' remarks.
        /// </summary>
        internal static bool IsMuleVaultReadOnCooldown(DateTime previous, DateTime now, int cooldownSeconds)
        {
            return now - previous < TimeSpan.FromSeconds(cooldownSeconds);
        }

        /// <summary>
        /// Last time this player's grant notified each grantee, keyed by grantee character guid.
        ///
        /// The idempotency check in TryGrantCore silences a REPEATED grant, but an alternating
        /// grant/revoke macro changes the grant every time and so passes it. This bounds the one part
        /// of the command that reaches a player who did not ask for it and cannot squelch it - the
        /// notification is a GameMessageSystemChat, not player chat.
        ///
        /// In-memory only, never persisted, mirroring PrevMuleVaultCommand.
        /// </summary>
        private readonly Dictionary<uint, DateTime> prevGranteeNotify = new Dictionary<uint, DateTime>();

        private const int GranteeNotifyCooldownSeconds = 60;

        /// <summary>
        /// True when the grantee may be told about this grant. Stamps on success, so a caller that gets
        /// false must simply not send the message.
        /// </summary>
        private bool TryNotifyGrantee(uint granteeGuid)
        {
            var now = DateTime.UtcNow;

            if (prevGranteeNotify.TryGetValue(granteeGuid, out var previous)
                && IsMuleVaultReadOnCooldown(previous, now, GranteeNotifyCooldownSeconds))
            {
                return false;
            }

            prevGranteeNotify[granteeGuid] = now;
            return true;
        }

        /// <summary>
        /// DESIGN 11.4's distance leash, copying Pet.SlowTick (Pet.cs:228-244): destroys a summoned
        /// mule once the summoner strays more than account_vault_summon_leash metres from it. Called
        /// from Player_Tick.Heartbeat (fix round 1: that file was out of this task's original scope -
        /// the coordinator granted it specifically to wire this in, since Heartbeat is the actual
        /// per-player tick hook every other per-heartbeat upkeep method here already runs through).
        ///
        /// The null check below is deliberately the FIRST thing this does and the only cost paid by
        /// the overwhelming majority of players who have no mule summoned - no distance math runs
        /// unless CurrentSummonedVendor is set.
        ///
        /// Destroys via vendor.Destroy(), never any other path: that is what routes through
        /// WorldObject.Destroy's PersonalVendor branch (mule.Store = null releasing the window pin,
        /// CurrentSummonedVendor cleared) - the same lifecycle every other despawn path (rot, logout,
        /// re-summon) already goes through. A leash that destroyed the vendor a different way would
        /// reopen the window-count retention leak that branch exists to close.
        /// </summary>
        public void MuleVendorLeashTick()
        {
            var vendor = CurrentSummonedVendor;

            if (vendor == null || vendor.IsDestroyed)
                return;

            // Fix round 2, F3: mirrors Pet.SlowTick's P_PetOwner?.PhysicsObj == null guard
            // (Pet.cs:234-238). GetCylinderDistance dereferences both sides' PhysicsObj - the
            // summoner's own is only ever null in the sliver of time before EnterWorld finishes
            // (InitPhysicsObj, WorldObject.cs), and the vendor's likewise if it was destroyed and
            // recycled out from under CurrentSummonedVendor between the null/IsDestroyed check above
            // and here. Either way, a missing PhysicsObj means this leash tick cannot do its job, so
            // destroying rather than skipping matches Pet's own choice: an un-leashable mule is not
            // safe to leave summoned.
            if (PhysicsObj == null || vendor.PhysicsObj == null)
            {
                log.Error($"{Name} ({Guid}).MuleVendorLeashTick() - PhysicsObj: {PhysicsObj}, vendor.PhysicsObj: {vendor.PhysicsObj}");
                SendMuleVendorDepartedMessage();
                vendor.Destroy();
                return;
            }

            var leash = PropertyManager.GetDouble("account_vault_summon_leash").Item;

            if (GetCylinderDistance(vendor) > leash)
            {
                SendMuleVendorDepartedMessage();
                vendor.Destroy();
            }
        }

        /// <summary>
        /// Fix round B, B6: the mule cannot follow the summoner the way the pet this was modelled on
        /// does - it ships Stuck = True - so a player who runs off to loot a corpse comes back to find
        /// their vendor simply gone, with nothing said. Re-summon is free, so this is confusion rather
        /// than loss, but a silent despawn reads as a bug. Covers both despawn paths this file owns
        /// (the leash and the PhysicsObj-missing defensive branch above); the generic rot despawn lives
        /// in WorldObject_Decay.cs's shared decay tick, a file neither this fix nor the other agent's
        /// scope this round covers - see the fix report.
        /// </summary>
        private void SendMuleVendorDepartedMessage()
        {
            // Null-conditional, not a bare Session.Network - MuleVendorLeashTick fires from the tick path
            // of a live, connected player, whose Session is never null in production, but this method
            // must not turn a despawn into an uncaught NRE for any caller that lacks one (this file's own
            // test harness reflection-constructs a Player with no Session at all).
            Session?.Network.EnqueueSend(new GameMessageSystemChat("Your vault vendor departs.", ChatMessageType.System));
        }
    }
}
