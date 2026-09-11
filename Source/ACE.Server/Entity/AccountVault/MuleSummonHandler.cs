using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md section 11; risks R9, R10): summons the disposable
    /// PersonalVendor window in front of the summoner. All gating and spawn logic lives here - the
    /// contract item (Gem.ActOnUse, behind PropertyBool.MuleVendorContract) and the /mule command
    /// (Player.HandleActionSummonMule, wired by a later task) are both thin callers into
    /// <see cref="TrySummon"/>.
    /// </summary>
    public static class MuleSummonHandler
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Content/wcid-registry.tsv, wcid block 1003150-1003199 (owner mule-vendor): 1003150 is the
        /// summonable Personal Vendor weenie (PropertyBool 9049 PersonalVendor). Content authoring for
        /// this weenie is a separate task; this constant is the code side's only reference to it,
        /// matching the established hardcoded-wcid-constant convention (see e.g.
        /// Player_Bank.cs's PromissoryNoteWcid / ClassAbilityPointCurrencyWcid).
        /// </summary>
        private const uint MuleVendorWcid = 1003150;

        /// <summary>
        /// (account, form wcid) pairs already reported as unusable, so a stale look logs ONCE rather
        /// than on every summon for the rest of the process. Same shape, and the same reason, as
        /// WorldObject_Setup's reportedBadSetups.
        /// </summary>
        private static readonly ConcurrentDictionary<(uint AccountId, uint FormWcid), byte> reportedBadForms = new ConcurrentDictionary<(uint, uint), byte>();

        private const string DenylistConfigKey = "account_vault_denylist";

        /// <summary>
        /// The repo owner reversed DESIGN 11.2's "summonable anywhere" after the first live test: the
        /// mule is Marketplace-only now. That is expressed as a CONFIGURABLE allowlist rather than a
        /// hardcoded landblock, for the same reason the denylist is configurable - the decision has
        /// already changed once, and the next change should not need a build.
        ///
        /// Empty means "no allowlist", i.e. every landblock is permitted subject only to the denylist.
        /// That is what the summon tests rely on, and it is also the honest degradation if the row is
        /// ever blanked. The SHIPPED default is 016C, the Marketplace - the same landblock
        /// mule_landblocks already defaults to for the IP active-player limit.
        /// </summary>
        private const string AllowlistConfigKey = "account_vault_allowlist";

        /// <summary>
        /// The player-facing NAME of the allowlisted area, used only in the refusal message. It is a
        /// separate config row rather than a string baked into the message because a landblock id
        /// means nothing to a player, and "the Marketplace" would silently become a lie the moment
        /// <see cref="AllowlistConfigKey"/> was pointed somewhere else.
        /// </summary>
        private const string AllowlistNameConfigKey = "account_vault_allowlist_name";

        /// <summary>
        /// Used when <see cref="AllowlistNameConfigKey"/> is blank, so a cleared row degrades to a
        /// vague-but-true message instead of "You can only summon your vault vendor in ."
        /// </summary>
        private const string AllowlistNameFallback = "the permitted area";

        /// <summary>
        /// Fix round A, A5: a summon is far more expensive than the two /mule read commands that
        /// already carry a cooldown - it creates a Creature WorldObject, broadcasts its spawn to every
        /// client in radar range, and destroys the previous one. Unlimited, a macro in a crowded
        /// Marketplace is a spawn/despawn strobe for everyone standing there. Five seconds matches
        /// Player.MuleVaultReadCooldownSeconds deliberately, so the whole /mule surface behaves the
        /// same way; the two want unifying into one helper once the file ownership allows it.
        /// </summary>
        private const int SummonCooldownSeconds = 5;

        /// <summary>
        /// Last successful summon per summoner guid. Keyed by guid rather than held on Player because
        /// Player_Mule_Vendor.cs (which owns the equivalent read cooldown field) is not this fix's to
        /// edit. Pruned opportunistically - see <see cref="PruneSummonCooldowns"/> - so a long uptime
        /// with many distinct summoners cannot grow it without bound.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, DateTime> lastSummon = new ConcurrentDictionary<uint, DateTime>();

        private const int SummonCooldownPruneThreshold = 256;

        /// <summary>
        /// Contract-item entry point (DESIGN 11.1). Returns true when this handler consumed the use -
        /// which is always, once <paramref name="player"/> is non-null, since the contract opted in via
        /// PropertyBool.MuleVendorContract and there is no other behavior for Gem.ActOnUse to fall back
        /// to. A refused summon still consumes the use; the refusal reaches the player as a transient
        /// error rather than as normal item-use failure.
        /// </summary>
        public static bool TryHandleUse(WorldObject wo, Player player)
        {
            if (player == null)
                return false;

            var storeAccountId = player.Account?.AccountId ?? 0;

            // A contract is always a self-summon, so the owning account's character is the user
            // themselves - DESIGN section 12's "derived from the owning account's character, never
            // player-supplied" is satisfied by the server-side Name, not by anything the player typed.
            if (!TrySummon(player, storeAccountId, out var failReason, player.Name))
            {
                log.Warn($"[MULE VENDOR] TryHandleUse: summon via {wo?.Name ?? "<unknown contract>"} (0x{wo?.Guid.Full:X8}) for {player.Name} refused: {failReason}");
                player.SendTransientError(failReason ?? AccountVaultStore.UnavailableMessage);
            }

            return true;
        }

        #region Mule form appearance

        /// <summary>
        /// The weenie a summoned mule should be created from: the plain mule weenie, or a clone of it
        /// wearing the account's earned donor body.
        ///
        /// EVERY FAILURE BRANCH RETURNS THE PLAIN MULE WEENIE, and that is the point of the method. A
        /// look is a cosmetic; a summon is the player's access to their own items. A donor weenie that
        /// has been deleted from the world database, a donor whose model cannot be measured, and a
        /// disabled tunable all mean "wear the default", never "refuse". Nothing here sets a
        /// failReason and nothing here can return false.
        ///
        /// Returns null only when the mule weenie itself could not be resolved, which tells the caller
        /// to create by wcid exactly as it did before this hook existed.
        ///
        /// <paramref name="getWeenie"/> and <paramref name="setupHeight"/> are injected so the whole
        /// decision is unit-testable with no world database and no dat.
        /// </summary>
        internal static Weenie ResolveVendorWeenie(Weenie mule, MuleFormLook? form, Func<uint, Weenie> getWeenie,
                                                   Func<uint, float> setupHeight, uint accountId)
        {
            if (mule == null || form == null)
                return mule;

            // Gates the APPEARANCE ONLY (orchestrator ruling 2026-09-01). Tokens still attune, still
            // fill, and a completed token still saves the look with this off.
            if (!PropertyManager.GetBool("mule_form_enabled").Item)
                return mule;

            var formWcid = form.Value.Wcid;
            var donor = getWeenie?.Invoke(formWcid);

            if (donor == null)
                return ReportUnusableForm(mule, accountId, formWcid, "its weenie is not in the world database");

            // The target height comes off the MULE weenie - its own Setup and its own DefaultScale - so
            // nothing about the current Lugian body is hard-coded and retuning the default mule retunes
            // every earned form with it.
            var targetHeight = MuleTargetHeight(mule, setupHeight);

            var clone = MuleFormBody.CloneWithDonorBody(mule, donor, targetHeight, setupHeight);

            if (clone == null)
                return ReportUnusableForm(mule, accountId, formWcid, "its Setup could not be measured");

            return clone;
        }

        private static Weenie ReportUnusableForm(Weenie mule, uint accountId, uint formWcid, string why)
        {
            if (reportedBadForms.TryAdd((accountId, formWcid), 0))
                log.Warn($"[MULEFORM] account {accountId}: saved form wcid {formWcid} cannot be worn ({why}). Summoning the default mule instead; the saved look is left alone and another token can replace it.");

            return mule;
        }

        /// <summary>
        /// How tall the finished body must stand: the mule's own model height at the mule's own
        /// DefaultScale. 0 when either is unavailable, which MuleFormBody.TryComputeScale then refuses.
        /// </summary>
        private static float MuleTargetHeight(Weenie mule, Func<uint, float> setupHeight)
        {
            if (setupHeight == null || mule?.PropertiesDID == null)
                return 0f;

            if (!mule.PropertiesDID.TryGetValue(PropertyDataId.Setup, out var setupId))
                return 0f;

            var scale = 1f;

            if (mule.PropertiesFloat != null && mule.PropertiesFloat.TryGetValue(PropertyFloat.DefaultScale, out var authored) && authored > 0)
                scale = (float)authored;

            return setupHeight(setupId) * scale;
        }

        #endregion

        /// <summary>
        /// Shared gating and spawn (DESIGN 11.1). Both the contract item and /mule land here.
        /// <paramref name="storeAccountId"/> is the summoner's own account for a self-summon, or a
        /// grantor's account resolved via AccountVaultManager.TryResolveAccountForCharacter for a
        /// shared summon (DESIGN section 10 / Player.HandleActionSummonMule).
        ///
        /// <paramref name="ownerName"/> is the OWNING account's character name, resolved server-side by
        /// the caller (DESIGN section 12: derived from the owning account's character, NEVER
        /// player-supplied). It only ever reaches <see cref="PersonalVendor.Name"/>. Optional so the
        /// existing call sites keep compiling; when it is null or blank the weenie's own Name is left
        /// untouched rather than a blank being written over it.
        /// </summary>
        public static bool TrySummon(Player summoner, uint storeAccountId, out string failReason, string ownerName = null)
        {
            if (summoner == null)
            {
                failReason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            // DESIGN 11.1, fix round A item A4: ALL gating lives in this one static, so both callers
            // inherit it. These five were previously only enforced on the contract-item path (Gem.
            // ActOnUse / Gem.UseGem, which serve every other gem type too and therefore keep their own
            // copies); the /mule command path checked none of them. Mid-teleport is the one with a
            // visible defect behind it - the placement ladder below runs against summoner.Location in a
            // landblock the player is in the act of leaving.
            if (!TrySummonerState(summoner, out failReason))
                return false;

            var actor = VaultActor.From(summoner);
            var store = AccountVaultManager.GetStore(storeAccountId);

            if (!TryGate(actor, summoner.Location, store, out failReason))
                return false;

            // Fix round A, A5. Checked AFTER the gate and stamped only here, so a refused summon (wrong
            // landblock, no permission) does not burn the window - the harm this cooldown exists to
            // stop is the spawn/despawn broadcast of a summon that actually happens.
            if (!TryStartSummon(summoner.Guid.Full, DateTime.UtcNow, out failReason))
                return false;

            // Mule Form Token (design section 6): if the OWNING account has earned a look, the vendor is
            // created from a clone of the mule weenie wearing that donor's body. It has to be decided
            // HERE, before construction: WorldObject's constructor runs InitPhysicsObj, which builds
            // PhysicsObj from SetupTableId and MotionTableId, so a DID written after construction would
            // leave physics on the Lugian.
            //
            // The store's form is read from a cache and every failure branch inside ResolveVendorWeenie
            // falls back to the plain mule weenie. Nothing about a look may refuse a summon.
            var muleWeenie = DatabaseManager.World.GetCachedWeenie(MuleVendorWcid);
            var form = store?.GetMuleForm();
            var vendorWeenie = ResolveVendorWeenie(muleWeenie, form, DatabaseManager.World.GetCachedWeenie, MuleFormToken.DatSetupHeight, storeAccountId);

            // A fresh clone per summon, never a memoized one: the vendor keeps a reference to the
            // weenie it was built from, so a shared clone would be reachable from every live mule at
            // once. Summons are already rate-limited to one per 5 s per player, so the cost is nothing.
            var wo = vendorWeenie != null
                ? WorldObjectFactory.CreateNewWorldObject(vendorWeenie)
                : WorldObjectFactory.CreateNewWorldObject(MuleVendorWcid);

            var vendor = wo as PersonalVendor;

            if (vendor == null)
            {
                var formNote = form != null ? $" (form wcid {form.Value.Wcid} was applied)" : string.Empty;

                log.Error($"[MULE VENDOR] TrySummon: wcid {MuleVendorWcid} did not produce a PersonalVendor{formNote} - is PropertyBool.PersonalVendor missing from that weenie, or the wcid wrong?");

                // Fix round 2, F4: wo is a live WorldObject with a dynamic guid the factory already
                // allocated - leaving it undestroyed leaks that guid forever (RecycleDynamicGuid runs
                // only from Destroy). wo?.Destroy() is a no-op when wo is itself null.
                wo?.Destroy();

                failReason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            vendor.SummonerGuid = summoner.Guid.Full;

            // DESIGN section 12: the vendor is named after the OWNING account's character, so three
            // mules standing in the Marketplace are told apart before anyone clicks one - which is the
            // case this feature is sold on, and the case where clicking the wrong one answers "You do
            // not have permission to use this vault" with no hint as to why. Never from player input:
            // ownerName is resolved server-side by the caller.
            //
            // Set BEFORE placement, not after Store is bound, even though every other post-creation
            // step waits for a successful placement. EnterWorld broadcasts a create message that
            // serializes the object's Name to every client in range; a Name assigned after that
            // broadcast would leave everyone looking at the weenie's default "Mule Vendor" until some
            // unrelated update happened to re-send it. Nothing about naming needs the placement to have
            // succeeded, and a vendor that fails placement is destroyed unseen.
            var vendorName = ComposeVendorName(ownerName);

            if (vendorName != null)
                vendor.Name = vendorName;

            // Fix round 2, F1: Store is bound ONLY after a successful placement, never before.
            // Nothing between here and the successful-placement branch below reads vendor.Store, so
            // this costs nothing and makes it structurally impossible for a throw or a refused
            // placement to strand a window pin - there is no window to release, because none was
            // ever taken. (The earlier "bind then unbind on failure" shape is exactly what the F1
            // review found reopening the permanent-retention leak this task exists to close, since
            // TryPlaceVendor's old PhysicsObj dereference threw AFTER the bind and skipped the
            // unbind entirely.)
            //
            // Fix round 3, item 2: TryPlaceVendor (or the EnterWorld -> LandblockManager.AddObject ->
            // new Landblock(...) chain underneath it) can THROW rather than return false - proven
            // reachable by this file's own TryPlaceVendor_ComputesTheVendorsRadiusFromTheDat... and
            // TrySummon_GatesCreatesAndAttemptsPlacement_EndToEnd tests, which both tolerate exactly
            // that exception class. A bare `if (!TryPlaceVendor(...))` lets a throw escape the
            // condition entirely and skip Destroy(), leaking the dynamic guid the factory already
            // allocated (F4) even though it does NOT reopen the window-pin leak (Store is still bound
            // only after this whole block succeeds - see above). The catch destroys and rethrows,
            // never swallows, so the caller's failure handling (TryHandleUse's transient-error path)
            // is unchanged for a throwing placement.
            bool placed;

            try
            {
                placed = TryPlaceVendor(vendor, summoner);
            }
            catch
            {
                vendor.Destroy();
                throw;
            }

            if (!placed)
            {
                // Fix round 2, F4: Destroy() (not a bare `vendor.Store = null`) both releases nothing
                // (Store was never bound - see above) AND recycles the dynamic guid. Confirmed safe to
                // call here even though this vendor is not yet tracked anywhere live: the summoner
                // lookup in WorldObject.Destroy's PersonalVendor branch finds the real player but
                // CurrentSummonedVendor == this is false (AssignSummonedVendor has not run yet), so no
                // live slot is clobbered, and RemoveBiotaFromDatabase early-returns for a never-saved
                // biota.
                vendor.Destroy();
                failReason = "Couldn't summon your vault vendor.";
                return false;
            }

            vendor.Store = store;

            // Fix round A, A2: warm the store HERE, not on the first click. PersonalVendor.
            // TryAuthorize refuses on !store.IsLoaded from CheckUseRequirements, i.e. BEFORE ActOnUse,
            // and reading IsLoaded is itself the call that STARTS the load - TryEnsureLoadedLocked
            // reads the index and constructs the containers, whose inventory load is async, so the
            // same call cannot also report loaded. Nothing else on the summon path touches it
            // (TryGetAccess short-circuits for the owning account before any load), so without this
            // every returning player's FIRST click was always refused with "still loading". The walk
            // from the summoner to the vendor now covers the async load instead.
            //
            // PersonalVendor.cs's own remark named the summon path as the owner of this warming. This
            // is that owner.
            if (!store.IsLoaded)
            {
                // Only when it is actually still loading: this line exists to explain a wait, and
                // printing it on an already-warm store would be noise on every re-summon. Session is
                // null-checked because a summon can be driven without one in tests.
                summoner.Session?.Network?.EnqueueSend(new GameMessageSystemChat("Your vault vendor is fetching your goods.", ChatMessageType.Broadcast));
            }

            // Fix round B, B3, completed here rather than in the command handler: EntryCount and
            // the cap were read in exactly ONE place before this - PersonalVendor's at-cap refusal -
            // so a player never learned how full their vault was until the moment it stopped accepting
            // deposits, and had no way to know the cap counts ENTRIES (one stack of any size) rather
            // than units. That is the difference between "you have used 1 of 500" and "you have used
            // 9,000 of 500" for the same 9,000 healing kits, and it is the exact case DESIGN 7.3's
            // collapse rule exists to make free.
            //
            // It lives HERE, on the shared path, rather than at the /mule call site, because the
            // contract item reaches TrySummon through TryHandleUse and would otherwise be silent. Both
            // summon routes are one static per DESIGN 11.1; so is everything they tell the player.
            SendOrDeferVaultLine(summoner, vendor, store, 0.0);

            // Summoning is the only thing most players will ever do with /mule, and nothing else in the
            // feature mentions that sharing, the access list or the audit log exist - a player who never
            // reads the command help never learns any of it. On the SHARED path for the same reason as
            // the line above: the contract item reaches TrySummon through TryHandleUse and is many
            // players' first contact with the feature, so pointing the hint only at the command would
            // miss exactly the people who most need it.
            summoner.Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                MuleCommandParser.HelpHint,
                ChatMessageType.System));

            // DESIGN 11.4: TimeToRot stamped at summon. Routed through PersonalVendor.RestampRot()
            // rather than duplicating its config read (and its clamp - a non-positive
            // account_vault_summon_rot_seconds must never resolve to TimeToRot == -1, which means
            // "Never Rot" per WorldObject_Decay.cs:42 and would pin this store's window forever) so
            // there is exactly one place this logic can drift, not two. RestampRot re-runs the same
            // stamp on every subsequent interaction (Task 7's ApproachVendor override).
            vendor.RestampRot();

            AssignSummonedVendor(summoner, vendor);

            failReason = null;
            return true;
        }

        /// <summary>
        /// DESIGN section 12's vendor name, from the OWNING account's character name. Returns null for
        /// a name that cannot be resolved, which the caller reads as "leave the weenie's own Name
        /// alone" - writing a blank, or a bare "'s Vault", is worse than the generic default.
        /// Pure and internal so the format is testable without a summon: the summon itself cannot be
        /// driven to completion in this test host (no live landblock).
        /// </summary>
        internal static string ComposeVendorName(string ownerName)
        {
            if (string.IsNullOrWhiteSpace(ownerName))
                return null;

            return $"{ownerName.Trim()}'s Vault";
        }

        /// <summary>What the summon path should do with the vault line for a given store, right now.</summary>
        internal enum VaultLineAction
        {
            /// <summary>The store is ready and the count is real. Send it.</summary>
            Send,

            /// <summary>The store is still finishing its async load. Ask again shortly.</summary>
            Defer,

            /// <summary>The index load FAILED, or there is no store. Say nothing, ever.</summary>
            Drop,
        }

        /// <summary>How often the deferred vault line re-checks readiness, and how long it keeps trying.</summary>
        private const double VaultLinePollSeconds = 0.5;
        private const double VaultLineMaxWaitSeconds = 20.0;

        /// <summary>
        /// The vault line is worth nothing if the number in it is not real, and on a COLD summon it
        /// never was: <see cref="AccountVaultStore.EntryCount"/> answers 0 for a store that is not
        /// READY, and its own doc-comment says that zero means "unknown", not "empty". The summon path
        /// is precisely where a store is coldest - reading IsLoaded a few lines above is the call that
        /// STARTS the load, and the container inventories finish asynchronously after it returns - so
        /// every returning player was told "Vault: 0 of 1000 entries" about a vault full of their
        /// goods. Deferring is the only honest answer: the count exists, it is just not knowable yet.
        ///
        /// <see cref="AccountVaultStore.TryCheckReady"/> rather than IsLoaded, because IsLoaded
        /// collapses "still loading" and "the index read failed" into one false, and those want
        /// opposite handling here - the first is worth waiting for, the second never resolves.
        ///
        /// Only StillLoadingMessage defers; every OTHER fail reason drops the line silently. That is a
        /// deliberate default, not a fallthrough: an unrecognized reason is one this method cannot
        /// prove is transient, and waiting out the full poll budget on a permanent condition buys
        /// nothing. A future THIRD reason that really is transient has to be named here, or it will be
        /// dropped rather than waited for.
        ///
        /// Pure and internal so the decision is testable without a summon: the summon itself cannot be
        /// driven to completion in this test host (no live landblock).
        /// </summary>
        internal static VaultLineAction DecideVaultLine(AccountVaultStore store, out string message)
        {
            message = null;

            if (store == null)
                return VaultLineAction.Drop;

            if (store.TryCheckReady(out var failReason))
            {
                message = ComposeVaultLine(store.EntryCount, store.EffectiveEntryCap);
                return VaultLineAction.Send;
            }

            return failReason == AccountVaultStore.StillLoadingMessage ? VaultLineAction.Defer : VaultLineAction.Drop;
        }

        /// <summary>
        /// DESIGN 7.3's collapse rule is the whole reason this line spells out what an entry is: the
        /// same 9,000 healing kits are "1 of 500" here and would read as "9,000 of 500" under a unit
        /// count, and a player with no way to tell the two apart cannot plan a deposit.
        /// </summary>
        internal static string ComposeVaultLine(int entryCount, int entryCap)
        {
            // The cap is the store's EffectiveEntryCap, not the static AccountVaultStore.BaseEntryCap:
            // this line is what tells a player how much room they have, so it must quote the same
            // number the deposit path will actually enforce, including whatever AugmentationMuleSpace
            // the account holds.
            return $"Vault: {entryCount} of {entryCap} entries. A whole stack of one item counts as a single entry, however many units it holds.";
        }

        /// <summary>
        /// Sends the vault line now if the store can stand behind the number, and otherwise re-arms
        /// itself every <see cref="VaultLinePollSeconds"/> until it can, giving up silently after
        /// <see cref="VaultLineMaxWaitSeconds"/>. See <see cref="DecideVaultLine"/> for why.
        ///
        /// The deferred line arrives AFTER the help hint rather than before it, and that is intended:
        /// the "fetching your goods" line above has already explained the wait, so a late count reads
        /// as the answer to it. Holding the hint back to preserve the original order would delay a
        /// message that has nothing to wait for.
        /// </summary>
        private static void SendOrDeferVaultLine(Player summoner, PersonalVendor vendor, AccountVaultStore store, double waitedSeconds)
        {
            switch (DecideVaultLine(store, out var message))
            {
                case VaultLineAction.Send:
                    summoner.Session?.Network?.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.System));
                    return;

                case VaultLineAction.Drop:
                    return;
            }

            if (waitedSeconds >= VaultLineMaxWaitSeconds)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(VaultLinePollSeconds);
            chain.AddAction(summoner, () =>
            {
                // Nothing here is worth telling a player who has logged out, or who is standing at a
                // DIFFERENT vendor than the one this summon created - re-summoned, rotted or destroyed,
                // any of which replaced or cleared the slot. Either way this chain has outlived what it
                // was reporting on, so it dies quietly rather than sending a count out of context.
                if (summoner.Session?.Network == null || !ReferenceEquals(summoner.CurrentSummonedVendor, vendor))
                    return;

                SendOrDeferVaultLine(summoner, vendor, store, waitedSeconds + VaultLinePollSeconds);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// DESIGN 11.1 / fix round A item A4: the summoner-state half of the gating, in the one static
        /// both entry points share. Mirrors Gem.ActOnUse's four checks (Gem.cs, IsBusy / Teleporting /
        /// suicideInProgress / IsJumping) plus Gem.UseGem's IsDead. Those copies STAY in Gem.cs - that
        /// file serves every other gem type and its gating is not this change's to alter - so the
        /// contract path simply refuses a little earlier than it does here, with the retail
        /// WeenieError wording; this method is what makes the /mule command path refuse at all.
        ///
        /// Not folded into <see cref="TryGate"/> deliberately: TryGate is the store-and-landblock gate
        /// and is unit-tested against a VaultActor with no live Player, which is exactly what these
        /// checks need.
        /// </summary>
        private static bool TrySummonerState(Player summoner, out string failReason)
        {
            if (summoner.IsDead)
            {
                failReason = "You cannot summon your vault vendor while dead.";
                return false;
            }

            if (summoner.IsBusy || summoner.Teleporting || summoner.suicideInProgress)
            {
                failReason = "You are too busy.";
                return false;
            }

            if (summoner.IsJumping)
            {
                failReason = "You can't do that while in the air.";
                return false;
            }

            failReason = null;
            return true;
        }

        /// <summary>
        /// Fix round A, A5: true and stamps the window when <paramref name="summonerGuid"/> is outside
        /// its summon cooldown. The window arithmetic itself is the pure
        /// <see cref="IsSummonOnCooldown"/>, mirroring Player.IsMuleVaultReadOnCooldown so the two
        /// cooldowns on this feature are the same shape and can be unified later.
        /// </summary>
        internal static bool TryStartSummon(uint summonerGuid, DateTime now, out string failReason)
        {
            if (lastSummon.TryGetValue(summonerGuid, out var previous) && IsSummonOnCooldown(previous, now, SummonCooldownSeconds))
            {
                failReason = "You have summoned your vault vendor too recently. Try again in a few seconds.";
                return false;
            }

            lastSummon[summonerGuid] = now;

            PruneSummonCooldowns(now);

            failReason = null;
            return true;
        }

        /// <summary>
        /// The pure window-arithmetic half of the summon cooldown, extracted so it is testable without
        /// a Player. Identical in shape to Player.IsMuleVaultReadOnCooldown.
        /// </summary>
        internal static bool IsSummonOnCooldown(DateTime previous, DateTime now, int cooldownSeconds)
        {
            return now - previous < TimeSpan.FromSeconds(cooldownSeconds);
        }

        /// <summary>
        /// Drops entries that are already outside their window, so the table cannot accumulate one row
        /// per summoner for the whole of a long uptime. Only runs once the table is large enough to be
        /// worth walking, since the common case is a handful of entries.
        /// </summary>
        private static void PruneSummonCooldowns(DateTime now)
        {
            if (lastSummon.Count < SummonCooldownPruneThreshold)
                return;

            foreach (var kvp in lastSummon)
            {
                if (!IsSummonOnCooldown(kvp.Value, now, SummonCooldownSeconds))
                    lastSummon.TryRemove(kvp.Key, out _);
            }
        }

        /// <summary>
        /// Test-only reset of the summon cooldown table (the production table is process-wide static
        /// state, and a test that summons twice for the same guid would otherwise be order-dependent).
        /// </summary>
        internal static void ClearSummonCooldowns()
        {
            lastSummon.Clear();
        }

        /// <summary>
        /// DESIGN 11.4, "load-bearing twice": destroys any vendor the summoner already has out before
        /// tracking the new one. This is BOTH the one-at-a-time rule AND the self-service recovery for a
        /// vendor that spawned somewhere unreachable (11.3 - a spawn inside a wall is not detectable, so
        /// re-summon is the only fix). An "optimization" that no-ops the destroy when a vendor already
        /// exists silently removes the recovery path and leaves players stuck. Split out from
        /// <see cref="TrySummon"/> so it is testable without a live Player's PhysicsObj/EnterWorld -
        /// see MuleSummonTests.Summon_DestroysThePreviousVendor.
        /// </summary>
        internal static void AssignSummonedVendor(Player summoner, PersonalVendor vendor)
        {
            summoner.CurrentSummonedVendor?.Destroy();
            summoner.CurrentSummonedVendor = vendor;
        }

        /// <summary>
        /// Denylist (11.2) plus authorization (section 10), re-resolved every call. Split out from
        /// <see cref="TrySummon"/>, and takes <paramref name="store"/> as a parameter rather than
        /// resolving it itself via AccountVaultManager.GetStore, so it is testable without touching the
        /// account-keyed store table or a live shard database - a test builds a store directly from
        /// Task 4's fakes (AccountVaultFakes.cs), exactly as PersonalVendorTests and
        /// AccountVaultStoreTests already do.
        /// </summary>
        internal static bool TryGate(VaultActor actor, Position summonerLocation, AccountVaultStore store, out string failReason)
        {
            if (!IsSummonPermitted(summonerLocation, out failReason))
                return false;

            if (store == null)
            {
                failReason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            // Reconciliation fix (Task 12): was AccountVaultStore.GetAccess, which collapses a
            // grant-read FAILURE into VaultAccess.None - a database outage was reported to the
            // summoner as "you do not have permission" rather than "unavailable". TryGetAccess
            // distinguishes the two, matching PersonalVendor.CanAcceptCore's pattern (F4). Security is
            // unaffected: the store's own withdraw-time check is unconditional regardless of this gate.
            if (!store.TryGetAccess(actor, out var access, out failReason))
                return false;

            // Deposit-only access is enough to SUMMON (mirrors PersonalVendor.CheckUseRequirements /
            // gate 1 - only WITHDRAWAL requires DepositWithdraw specifically, re-checked independently at
            // gate 2 on every transaction per DESIGN section 10). This rule is NOT what changed above.
            if (access == VaultAccess.None)
            {
                failReason = "You do not have permission to use this vault.";
                return false;
            }

            failReason = null;
            return true;
        }

        /// <summary>
        /// DESIGN 11.3's placement ladder: in front at <paramref name="spawnDist"/>, then 0.6x, then
        /// 0.3x, then the summoner's exact position - guaranteed valid because the player occupies it,
        /// and harmless because the vendor is Ethereal. Home is set on every rung BEFORE
        /// <paramref name="placeAttempt"/> runs (R10: ApproachVendor's PrepareResetToHome chain
        /// dereferences Home.Pos - Vendor.cs:396 via CheckResetToHome - so a hand-spawned vendor with no
        /// Home set yet would NRE on the first approach if this order were reversed).
        ///
        /// <paramref name="placeAttempt"/> is the seam that lets this be tested without a live landblock:
        /// production resolves the cell and calls the real EnterWorld() (see
        /// <see cref="TryPlaceVendor"/>); a test injects a delegate that can fail deterministically per
        /// rung and observe Home at the moment it is called.
        /// </summary>
        internal static bool TryPlace(WorldObject vendor, Position summonerLocation, float spawnDist, Func<WorldObject, bool> placeAttempt)
        {
            foreach (var dist in new[] { spawnDist, spawnDist * 0.6f, spawnDist * 0.3f, 0f })
            {
                // rotate180 (repo owner's ruling after live test 2): the vendor faces the SUMMONER
                // rather than standing with its back to them. Position.InFrontOf's own rotate180 arm
                // does exactly this for the three offset rungs.
                //
                // The final rung keeps the summoner's exact Pos - PlacementLadder_FallsBackToThe
                // SummonerPosition_WhenEveryDistanceFails asserts that, and routing it through
                // InFrontOf(0f, true) would also add InFrontOf's 0.05 bump height - so it is rotated
                // in place by FacingBack instead. Both arms produce the same heading; only the
                // position differs.
                vendor.Location = dist > 0f ? summonerLocation.InFrontOf(dist, true) : FacingBack(summonerLocation);

                // R10, belt and braces alongside PersonalVendor.PrepareResetToHome's inert override:
                // Home must be live before placeAttempt ever calls EnterWorld/ApproachVendor.
                vendor.Home = new Position(vendor.Location);

                if (placeAttempt(vendor))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// <paramref name="location"/> with its heading turned through 180 degrees and everything else
        /// left alone. Same quaternion math as Position.InFrontOf's rotate180 arm (Position.cs:133),
        /// deliberately - this exists only because InFrontOf couples the rotation to a translation plus
        /// a 0.05 bump height, and the final placement rung must keep the summoner's exact position.
        /// </summary>
        internal static Position FacingBack(Position location)
        {
            var rotated = new Quaternion(0, 0, location.RotationZ, location.RotationW) * Quaternion.CreateFromYawPitchRoll(0, 0, (float)Math.PI);

            return new Position(location.LandblockId.Raw, location.PositionX, location.PositionY, location.PositionZ, 0f, 0f, rotated.Z, rotated.W, location.Instance);
        }

        /// <summary>
        /// Production glue for <see cref="TryPlace"/>: computes the real spawn distance and resolves
        /// the cell / calls the real EnterWorld() inside the injected delegate, so that resolution and
        /// the live-landblock call stay out of the testable core.
        ///
        /// `internal` (fix round 2, F1) rather than `private` so a test can drive this exact
        /// production glue directly - it is the one code path guaranteed to run on every single
        /// summon, and the review that found the PhysicsObj bug below also found that nothing
        /// exercised it: every placement test drove the lower-level <see cref="TryPlace"/> core
        /// through an injected delegate instead.
        /// </summary>
        internal static bool TryPlaceVendor(PersonalVendor vendor, Player summoner)
        {
            // Fix round 2, F1 (coordinator-owned defect - the original brief prescribed this exact
            // vendor.PhysicsObj.GetPhysicsRadius() call and mis-cited it as "copying Pet.Init"; Pet.Init
            // actually uses GetPetRadius, reading the dat, for precisely this reason): WorldObject.
            // PhysicsObj is not initialized by either WorldObject constructor (WorldObject.cs:63-75) -
            // InitPhysicsObj() runs lazily from Landblock.AddWorldObjectInternal, i.e. from INSIDE
            // EnterWorld, which this method is computing a distance in order to call. Dereferencing
            // vendor.PhysicsObj here is therefore a guaranteed NullReferenceException on every summon.
            // The summoner IS already in world, so summoner.PhysicsObj.GetPhysicsRadius() is fine;
            // only the not-yet-placed vendor's side needs the dat fallback.
            var spawnDist = summoner.PhysicsObj.GetPhysicsRadius() + GetVendorRadius(vendor) + 0.5f;

            return TryPlace(vendor, summoner.Location, spawnDist, wo =>
            {
                wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());

                // Fix round 2, F5: TryGate's denylist check (in TrySummon, before this method ever
                // runs) only tested the SUMMONER's own landblock. The placement ladder's InFrontOf
                // offset can resolve a rung into an ADJACENT landblock the summoner never stood in, so
                // a summon started just inside a permitted landblock, facing a denied one, could still
                // land the vendor across the line. Skip (never EnterWorld) a rung whose resolved
                // Location falls inside a denylisted landblock, rather than placing it and checking
                // after - cheaper, and it degrades to the next rung instead of failing the whole
                // summon outright. The final rung (the summoner's own exact position) can never trip
                // this, since TryGate already cleared that landblock.
                if (!IsSummonPermitted(wo.Location, out _))
                    return false;

                return wo.EnterWorld();
            });
        }

        /// <summary>
        /// The vendor's placement radius, read from the dat rather than from PhysicsObj (which does
        /// not exist yet - see <see cref="TryPlaceVendor"/>'s remarks). Mirrors Pet.GetPetRadius
        /// (Pet.cs:328-338): WorldObject.GetSetupModel (not a raw DatManager.PortalDat.ReadFromDat
        /// call) is used because it already handles an invalid/missing setup id defensively
        /// (WorldObject_Setup.cs:100-113, `invalidSetupFallback`) - never throws, never returns null -
        /// and its fallback SetupModel.CreateSimpleSetup() leaves Spheres empty, which is why this
        /// checks Count rather than indexing Spheres[0] unconditionally the way Pet.GetPetRadius does.
        /// </summary>
        private static float GetVendorRadius(PersonalVendor vendor)
        {
            var setup = WorldObject.GetSetupModel(vendor.SetupTableId);

            return setup.Spheres.Count > 0 ? setup.Spheres[0].Radius * (float)(vendor.ObjScale ?? 1.0) : 0f;
        }

        /// <summary>
        /// The whole LOCATION gate - denylist and allowlist, in that order - plus the refusal message
        /// the summoner sees. Called from <see cref="TryGate"/> for the summoner's own landblock, and
        /// again per placement rung inside <see cref="TryPlaceVendor"/>, because the InFrontOf offset
        /// can resolve a rung into an ADJACENT landblock the summoner never stood in.
        ///
        /// A null location is permitted - there is nothing to test, and this must never be the thing
        /// that fails a summon. Copies the IsConfined shape (IpLimitManager.cs:495-509).
        ///
        /// The DENYLIST is evaluated first and wins outright. A landblock named in both lists is
        /// refused: the denylist is the emergency lever ("this specific place is a problem right now")
        /// and an allowlist entry must never be able to re-open it.
        /// </summary>
        internal static bool IsSummonPermitted(Position location, out string failReason)
        {
            failReason = null;

            if (location == null)
                return true;

            var landblock = (ushort)location.LandblockShort;

            if (GetDenylistLandblocks().Contains(landblock))
            {
                failReason = "You cannot summon your vault vendor here.";
                return false;
            }

            var allowlist = GetAllowlistLandblocks();

            // Count == 0 is "no allowlist configured", NOT "nothing is allowed" - see
            // AllowlistConfigKey's remarks. Inverting this would make a blanked config row silently
            // disable the entire feature everywhere.
            if (allowlist.Count > 0 && !allowlist.Contains(landblock))
            {
                failReason = $"You can only summon your vault vendor in {GetAllowlistName()}.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The player-facing name of the allowlisted area, for the refusal message only. Never empty -
        /// a blank or whitespace config row degrades to <see cref="AllowlistNameFallback"/>.
        /// </summary>
        private static string GetAllowlistName()
        {
            var raw = PropertyManager.GetString(AllowlistNameConfigKey).Item;

            return string.IsNullOrWhiteSpace(raw) ? AllowlistNameFallback : raw.Trim();
        }

        /// <summary>
        /// Parses account_vault_denylist exactly like IpLimitManager.GetMuleLandblocks
        /// (IpLimitManager.cs:421-449): accepts 0x016C or 016C, case-insensitively, tolerates whitespace
        /// and empty entries, and SKIPS a malformed entry with a logged warning rather than throwing - a
        /// bad config string must never break a summon, for the same reason it must never break login.
        /// Empty by default. Since 2026-08-28 the LOCATION rule is carried by
        /// <see cref="AllowlistConfigKey"/> instead (Marketplace-only); this list is the narrower
        /// emergency lever that outranks it.
        /// </summary>
        public static HashSet<ushort> GetDenylistLandblocks()
        {
            return ParseLandblockList(DenylistConfigKey);
        }

        /// <summary>
        /// Parses account_vault_allowlist with the exact same rules as
        /// <see cref="GetDenylistLandblocks"/> - deliberately the same parser, so the two config rows
        /// can never disagree about what "0x016C" means. An EMPTY set means no allowlist is configured
        /// and every landblock is permitted; it does not mean nothing is.
        /// </summary>
        public static HashSet<ushort> GetAllowlistLandblocks()
        {
            return ParseLandblockList(AllowlistConfigKey);
        }

        private static HashSet<ushort> ParseLandblockList(string configKey)
        {
            var raw = PropertyManager.GetString(configKey).Item;

            var result = new HashSet<ushort>();

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            foreach (var token in raw.Split(','))
            {
                var trimmed = token.Trim();

                if (trimmed.Length == 0)
                    continue;

                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring(2);

                if (ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var landblock))
                {
                    result.Add(landblock);
                }
                else
                {
                    log.Warn($"MuleSummonHandler.ParseLandblockList: skipping malformed entry '{token}' in '{configKey}'.");
                }
            }

            return result;
        }
    }
}
