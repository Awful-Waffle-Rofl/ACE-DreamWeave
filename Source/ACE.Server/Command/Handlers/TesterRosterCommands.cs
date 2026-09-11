using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Refreshes the tester roster: fifty fixed slots holding copies of the prod characters parked
    /// in the ten source slots. See Docs/ProdCharSeed/TECH-DESIGN.md.
    ///
    /// This command copies from the source slots that already exist in this shard. It does not read
    /// prod. Getting fresher source data is a separate, offline step (the extractor plus a transplant
    /// applied while the server is stopped).
    /// </summary>
    public static class TesterRosterCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // CommandHandlerFlag.None, NOT RequiresWorld, and that is the whole point of this attribute.
        //
        // GetCommandHandler answers NotInWorld for a RequiresWorld command whenever
        // "session == null || session.Player == null || session.Player.CurrentLandblock == null"
        // (Source/ACE.Server/Command/CommandManager.cs), and the console command loop always calls it
        // with a null session. So under RequiresWorld this command could NEVER run from the console,
        // at any time, in any server state - the handler body was simply never entered, which is what
        // made .github/workflows/seed-stage-roster.yml's console injection a silent no-op.
        //
        // ConsoleInvoke would be equally wrong in the other direction: it means console-ONLY, and a
        // session invocation of a ConsoleInvoke command is rejected with NoConsoleInvoke. The
        // in-game path has to keep working, because Docs/ProdCharSeed/RUNBOOK.md documents an
        // operator typing @refreshtestroster by hand. None is the established flag for a command
        // that must answer from both, and it is what @deletecharacter and @set-characteraccess use
        // (Source/ACE.Server/Command/Handlers/CharacterCommands.cs).
        [CommandHandler("refreshtestroster", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Rebuilds the fifty tester roster characters from the ten parked source characters.",
            "\nPurges each of the fifty fixed tester slots and re-copies the matching source character into it.\n" +
            "Refuses if any slot character is currently online. Requires the source slots to have been\n" +
            "transplanted first - see Docs/ProdCharSeed/RUNBOOK.md.")]
        public static void HandleRefreshTestRoster(Session session, params string[] parameters)
        {
            // WORLD-OPEN GUARD. Its one job is to keep this command out of the boot window.
            //
            // WorldManager.Open is the LAST statement of server startup
            // (Source/ACE.Server/Program.cs:491-493), and the console command loop is live well
            // before it: CommandManager.Initialize runs at Program.cs:458. So a console command can
            // land while startup is still finishing - mod command registration, MarketManager's
            // pending-transaction and order recovery, and the world thread's own
            // PreloadConfigLandblocks are all still in flight at that point. A fifty-slot purge and
            // copy has no business running against a server in that state, and WorldStatus == Open is
            // the server's own single signal that startup actually completed.
            //
            // WHAT THIS GUARD IS NOT. It is not protection against testers logging in: an admin can
            // log in to a closed world anyway (Network/Handlers/AuthenticationHandler.cs:237,
            // CharacterHandler.cs:40), and the roster characters themselves are already covered by
            // the online check below plus the per-slot re-check inside the purge loop.
            //
            // It DOES cost a maintenance pattern that used to work: "@world close, then
            // @refreshtestroster" to keep players out while fifty slots are rebuilt. That is a
            // deliberate trade - the same guard has to hold on both paths, because an admin can log
            // in during the boot window too and a console-only check would leave that hole open - so
            // the refusal below names @world open as the way forward when the closure was deliberate.
            //
            // Worded as "Refusing to refresh:" deliberately, matching the two refusals below. The
            // workflow greps for exactly that prefix and reports a match as the command answering
            // normally rather than as a dead console loop (seed-stage-roster.yml, "Verify the refresh
            // from the new log lines"). Checked here rather than inside the queued work below so the
            // answer is immediate and no work is queued that is already known to be refused.
            if (WorldManager.WorldStatus != WorldManager.WorldStatusState.Open)
            {
                CommandHandlerHelper.WriteOutputInfo(session,
                    $"Refusing to refresh: the world is {WorldManager.WorldStatus} and this command needs it open. " +
                    "If the server is still starting up, wait for the \"World is now open\" line and run it again. " +
                    "If you closed the world on purpose, run @world open first, then refresh.",
                    ChatMessageType.Broadcast);
                return;
            }

            // MARSHAL THE ACTUAL WORK ONTO THE WORLD THREAD.
            //
            // Everything below PurgeSlot's in-memory cleanup - AllegianceManager.DoHandlePlayerDelete,
            // HouseManager.DoHandlePlayerDelete, PlayerManager.ProcessDeletedPlayer - mutates world
            // state that is only single-threaded by convention, and a console command runs on the
            // "Command Manager" thread, not the world tick (CommandManager invokes the handler inline
            // from its own console loop). Under RequiresWorld every invocation arrived through
            // InboundMessageManager and was therefore already on the world thread; that guarantee is
            // gone the moment console invocation is allowed, so it has to be re-established here.
            //
            // This is the codebase's own mechanism for it, not a new one:
            // AllegianceManager.HandlePlayerDelete wraps DoHandlePlayerDelete in exactly this call
            // (Source/ACE.Server/Managers/AllegianceManager.cs:419-422) to get database-callback
            // threads onto the world thread, and @deletecharacter - CommandHandlerFlag.None, the
            // closest precedent for a character-mutating command that must answer from the console -
            // pushes its PlayerManager.HandlePlayerDelete / ProcessDeletedPlayer pair onto
            // WorldManager.ActionQueue through an ActionChain for the same reason
            // (Source/ACE.Server/Command/Handlers/CharacterCommands.cs:95-105).
            //
            // This costs the in-game path one queue hop, NOT a tick of latency. A chat command
            // arrives through NetworkManager.InboundMessageQueue.RunActions(), which the world loop
            // runs at Managers/WorldManager.cs:661, and WorldManager.ActionQueue.RunActions() follows
            // at :666 in the SAME loop iteration - so the action queued here is dequeued later in the
            // same tick that dispatched the command.
            WorldManager.EnqueueAction(new ActionEventDelegate(() => DoRefreshTestRoster(session)));
        }

        /// <summary>
        /// The refresh itself. Always runs on the world thread - see the queueing comment in
        /// HandleRefreshTestRoster, which is the only caller.
        /// </summary>
        private static void DoRefreshTestRoster(Session session)
        {
            var online = TesterRoster.TesterSlots
                .Where(slot => PlayerManager.GetOnlinePlayer(slot.Guid) != null)
                .ToList();

            if (online.Count > 0)
            {
                var names = string.Join(", ", online.Select(s => s.Name));
                CommandHandlerHelper.WriteOutputInfo(session,
                    $"Refusing to refresh: {online.Count} roster character(s) are online ({names}). Have them log out first.",
                    ChatMessageType.Broadcast);
                return;
            }

            // A DATABASE read, not a PlayerManager lookup. The parked source characters carry
            // AccountId = 0 and IsDeleted = 1, and PlayerManager.FindByGuid only ever answers from the
            // in-memory online/offline dictionaries - there is no guarantee a row parked in that shape
            // was ever loaded into either one. GetCharacterStubByGuid reads the character table
            // directly and does not filter on IsDeleted, so it correctly reports a parked source as
            // present.
            var missing = TesterRoster.SourceSlots
                .Where(guid => DatabaseManager.Shard.BaseDatabase.GetCharacterStubByGuid(guid) == null)
                .ToList();

            if (missing.Count > 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session,
                    $"Refusing to refresh: {missing.Count} of {TesterRoster.SourceSlots.Count} source slots are empty. Apply the transplant first (Docs/ProdCharSeed/RUNBOOK.md).",
                    ChatMessageType.Broadcast);
                return;
            }

            CommandHandlerHelper.WriteOutputInfo(session,
                $"Refreshing {TesterRoster.TesterSlots.Count} tester roster slots. Watch the audit channel for per-slot results.",
                ChatMessageType.Broadcast);

            // TWO SEQUENTIAL PASSES over the fifty slots: purge them all, then copy into them all.
            //
            // Interleaving purge and copy per slot is what allowed one slot's cleanup to reach another
            // slot's fresh copy. PurgeSlot calls AllegianceManager.DoHandlePlayerDelete, which walks the
            // outgoing occupant's allegiance tree and re-saves every player it touches - patron, vassals,
            // and the outgoing occupant itself. If slot B's purge ran after slot A had already been
            // re-copied, and A was in B's allegiance tree, that walk would write A's OLD biota back over
            // the fresh copy at A's (reused, fixed) guid. Purging everything first means every one of
            // those trees is already dismantled before the first copy lands.
            //
            // A slot that fails or is skipped in the purge pass never enters the copy pass: copying on
            // top of a partly-purged slot is exactly the corruption the purge exists to prevent.
            var toCopy = new List<(TesterRoster.TesterSlot Slot, uint AccountId)>();
            var purgeFailures = new List<string>();

            foreach (var slot in TesterRoster.TesterSlots)
            {
                var accountName = slot.AccountName;
                var account = DatabaseManager.Authentication.GetAccountByName(accountName);

                if (account == null)
                {
                    CommandHandlerHelper.WriteOutputInfo(session,
                        $"Skipping {slot.Name}: account \"{accountName}\" does not exist. Create it with @accountcreate.",
                        ChatMessageType.Broadcast);
                    continue;
                }

                // Re-check immediately before purging, not just once up front. The up-front check
                // above is a fast refusal before any destructive work starts, but by the time the loop
                // reaches slot N it has already done up to N-1 rounds of real synchronous database
                // work, during which a tester could have logged into slot N. Purging out from under a
                // now-live session would delete a connected player's character and biota.
                if (PlayerManager.GetOnlinePlayer(slot.Guid) != null)
                {
                    CommandHandlerHelper.WriteOutputInfo(session,
                        $"Skipping {slot.Name}: came online during this refresh. Have them log out and refresh again.",
                        ChatMessageType.Broadcast);
                    continue;
                }

                if (!PurgeSlot(slot.Guid))
                {
                    purgeFailures.Add($"{slot.Name} (0x{slot.Guid:X8})");
                    continue;
                }

                toCopy.Add((slot, account.AccountId));
            }

            if (purgeFailures.Count > 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session,
                    $"{purgeFailures.Count} slot(s) failed to purge and were NOT re-copied: {string.Join(", ", purgeFailures)}. " +
                    "Check the server log for the underlying database error; those slots may be partly populated.",
                    ChatMessageType.Broadcast);
            }

            foreach (var (slot, accountId) in toCopy)
            {
                var sourceGuid = TesterRoster.SourceSlotForRank(slot.Rank);

                AdminCommands.DoCopyChar(session, $"0x{sourceGuid:X8}", sourceGuid, false, slot.Name, accountId, slot.Guid);
            }

            CommandHandlerHelper.WriteOutputInfo(session,
                $"Attempted {toCopy.Count + purgeFailures.Count} purge(s) of {TesterRoster.TesterSlots.Count} slot(s), " +
                $"{purgeFailures.Count} failed; queued {toCopy.Count} copy job(s).",
                ChatMessageType.Broadcast);
        }

        /// <summary>
        /// Removes everything occupying a tester slot: the in-memory allegiance/house/cache state first,
        /// then the player's possessions, the player biota, and the character row. Each parent delete
        /// cascades to its child tables through real foreign keys, so this is three deletes rather than
        /// thirty-three.
        ///
        /// Returns true only when every delete it issued reported success. A false return means the slot
        /// may be partly populated, and the caller must NOT copy into it.
        ///
        /// THE CLEANUP CALLS RUN BEFORE THE DELETES, AND THAT ORDER IS LOAD-BEARING.
        /// AllegianceManager.DoHandlePlayerDelete ends by calling SaveBiotaToDatabase on every player it
        /// touched, the outgoing occupant included, and ShardDatabase.SaveBiota INSERTS when no row
        /// exists. Run after the deletes, that save re-creates the biota row this method just removed -
        /// guaranteed, not incidental, because ShardDatabaseWithCaching.RemoveBiota has already evicted
        /// the cache entry by then. Run before them, the save lands on a row that is about to be deleted
        /// anyway and the deletes have the last word.
        ///
        /// Every DELETE here goes through DatabaseManager.Shard.BaseDatabase, the raw ShardDatabase, NOT
        /// DatabaseManager.Shard itself. That distinction is what makes the ordering against DoCopyChar
        /// hold. DatabaseManager.Shard is a SerializedShardDatabase: its methods (GetCharacter,
        /// GetPossessedBiotasInParallel, IsCharacterNameAvailable, and so on, which is what DoCopyChar
        /// calls) enqueue work onto one worker thread and return before that work runs. BaseDatabase's
        /// methods do not queue anything - RemoveCharacter opens its own ExecutionStrategy-wrapped
        /// transaction and commits before returning, and the other calls below are equally synchronous EF
        /// calls on the caller's thread. So every delete in this method has fully committed to MySQL by
        /// the time PurgeSlot returns.
        ///
        /// That is what guarantees the ordering the refresh depends on, not FIFO queue order (the deletes
        /// never touch the queue at all). HandleRefreshTestRoster purges all fifty slots before it calls
        /// DoCopyChar even once, so every character row is committed-deleted before the first copy is
        /// enqueued; DoCopyChar's later IsCharacterNameAvailable check - whenever the worker thread gets
        /// to it - therefore runs against a database with no character row at this guid, and the slot's
        /// name is already free.
        ///
        /// Possessions are purged before the player biota, per instruction; this is not required by
        /// any foreign key (item biotas are independent rows carrying a plain PropertyInstanceId
        /// value, not a constrained reference to the player's biota row), but purging in this order
        /// costs nothing and keeps the sequence unsurprising to read.
        /// </summary>
        private static bool PurgeSlot(uint guid)
        {
            // PlayerManager.HandlePlayerDelete does NOT touch the offline-player cache - it only
            // ENQUEUES AllegianceManager.DoHandlePlayerDelete and HouseManager.DoHandlePlayerDelete
            // onto WorldManager.ActionQueue for a later tick. Both of those start with
            // PlayerManager.FindByGuid(guid) and return immediately if it comes back null. Since our
            // slot guids are REUSED (unlike normal character deletion), enqueuing them here would run
            // them only after this whole refresh command has returned control to the world tick - by
            // which point DoCopyChar's async chain has likely already replaced this guid's entry in
            // PlayerManager with the NEW copy. Enqueued, the cleanup would either find nothing (if
            // ProcessDeletedPlayer below already ran) or find the new copy and silently no-op against
            // its already-blank allegiance/house state (DoCopyChar explicitly nulls Allegiance,
            // MonarchId, PatronId, HouseId and HouseInstance before saving it) - meaning the OLD
            // occupant's real allegiance-tree references and house rent-queue entry never actually get
            // cleaned up. Every refresh, silently.
            //
            // Fixed by calling the Do* methods DIRECTLY instead of through the enqueueing wrapper.
            // This is safe specifically because the whole refresh is itself queued onto the world
            // thread by HandleRefreshTestRoster before it ever reaches here, so this method is already
            // executing ON the world tick thread no matter which path invoked the command. (That used
            // to be provided by CommandHandlerFlag.RequiresWorld plus InboundMessageManager, whose own
            // comment records that "HandleClientMessage() queues work into
            // NetworkManager.InboundMessageQueue that is run in WorldManager.UpdateWorld()"; the flag
            // is None now, and the explicit WorldManager.EnqueueAction replaces that guarantee for
            // both the in-game and the console path.) WorldManager.EnqueueAction inside
            // HandlePlayerDelete exists to GET a caller onto that thread (its own comment: "This
            // function is called from a database callback. We must add thread safety..."); calling
            // directly from a spot that is already on that thread does not skip any thread-safety
            // step, it just skips a redundant hop that would otherwise arrive too late. Made
            // DoHandlePlayerDelete internal on both AllegianceManager and HouseManager (was private)
            // so this file can call it, same pattern as DoCopyChar in Task 3.
            //
            // ProcessDeletedPlayer is LAST of the three: AllegianceManager.DoHandlePlayerDelete and
            // HouseManager.DoHandlePlayerDelete both resolve the player through PlayerManager.FindByGuid,
            // which answers null the moment ProcessDeletedPlayer has removed the offlinePlayers /
            // playerNames / playerAccounts entries. Until it runs, FindByGuid still resolves to the
            // outgoing occupant, which is the character this purge is cleaning up after.
            //
            // ProcessDeletedPlayer itself must run here, synchronously, before this method returns:
            // DoCopyChar's tail calls PlayerManager.AddOfflinePlayer(newPlayer) on the SAME guid once its
            // async chain completes, using a plain dictionary indexer write. If ProcessDeletedPlayer for
            // the old occupant ran AFTER that write instead of before it, it would remove the new copy's
            // freshly-added entry by guid, not the old one, because both occupy the same key. Calling it
            // synchronously here, ahead of DoCopyChar, rules that out.
            AllegianceManager.DoHandlePlayerDelete(guid);
            HouseManager.DoHandlePlayerDelete(guid);
            PlayerManager.ProcessDeletedPlayer(guid);

            var possessions = DatabaseManager.Shard.BaseDatabase.GetPossessedBiotasInParallel(guid);

            var possessionIds = possessions.Inventory.Select(b => b.Id)
                .Concat(possessions.WieldedItems.Select(b => b.Id))
                .ToList();

            var possessionsRemoved = possessionIds.Count == 0
                || DatabaseManager.Shard.BaseDatabase.RemoveBiotasInParallel(possessionIds);

            var biotaRemoved = DatabaseManager.Shard.BaseDatabase.RemoveBiota(guid);
            var characterRemoved = DatabaseManager.Shard.BaseDatabase.RemoveCharacter(guid);

            var ok = possessionsRemoved && biotaRemoved && characterRemoved;

            if (ok)
            {
                log.Info($"[ROSTER] purged slot 0x{guid:X8} and {possessionIds.Count} possession(s)");
            }
            else
            {
                log.Error($"[ROSTER] purge of slot 0x{guid:X8} FAILED (possessions: {possessionsRemoved}, " +
                          $"biota: {biotaRemoved}, character: {characterRemoved}) - the slot may be partly populated " +
                          "and will not be re-copied this run");
            }

            return ok;
        }
    }
}
