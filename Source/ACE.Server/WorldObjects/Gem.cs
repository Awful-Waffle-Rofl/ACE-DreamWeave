using System;

using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics;

namespace ACE.Server.WorldObjects
{
    public class Gem : Stackable
    {
        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Gem(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Gem(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
        }

        /// <summary>
        /// This is raised by Player.HandleActionUseItem.<para />
        /// The item should be in the players possession.
        /// 
        /// The OnUse method for this class is to use a contract to add a tracked quest to our quest panel.
        /// This gives the player access to information about the quest such as starting and ending NPC locations,
        /// and shows our progress for kill tasks as well as any timing information such as when we can repeat the
        /// quest or how much longer we have to complete it in the case of at timed quest.   Og II
        /// </summary>
        public override void ActOnUse(WorldObject activator)
        {
            ActOnUse(activator, false);
        }

        public void ActOnUse(WorldObject activator, bool confirmed)
        {
            if (!(activator is Player player))
                return;

            if (player.IsBusy || player.Teleporting || player.suicideInProgress)
            {
                player.SendWeenieError(WeenieError.YoureTooBusy);
                return;
            }

            if (player.IsJumping)
            {
                player.SendWeenieError(WeenieError.YouCantDoThatWhileInTheAir);
                return;
            }

            if (!string.IsNullOrWhiteSpace(UseSendsSignal))
            {
                player.CurrentLandblock?.EmitSignal(player, UseSendsSignal);
                return;
            }

            // handle rare gems
            if (RareId != null && player.GetCharacterOption(CharacterOption.ConfirmUseOfRareGems) && !confirmed)
            {
                var msg = $"Are you sure you want to use {Name}?";
                var confirm = new Confirmation_Custom(player.Guid, () => ActOnUse(activator, true));
                if (!player.ConfirmationManager.EnqueueSend(confirm, msg))
                    player.SendWeenieError(WeenieError.ConfirmationInProgress);
                return;
            }

            if (RareUsesTimer)
            {
                var currentTime = Time.GetUnixTime();

                var timeElapsed = currentTime - player.LastRareUsedTimestamp;

                if (timeElapsed < RareTimer)
                {
                    // TODO: get retail message
                    var remainTime = (int)Math.Ceiling(RareTimer - timeElapsed);
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You may use another timed rare in {remainTime}s", ChatMessageType.Broadcast));
                    return;
                }
            }

            if (UseUserAnimation != MotionCommand.Invalid)
            {
                // some gems have UseUserAnimation and UseSound, similar to food
                // eg. 7559 - Condensed Dispel Potion

                // the animation is also weird, and differs from food, in that it is the full animation
                // instead of stopping at the 'eat/drink' point... so we pass 0.5 here?

                var animMod = (UseUserAnimation == MotionCommand.MimeDrink || UseUserAnimation == MotionCommand.MimeEat) ? 0.5f : 1.0f;

                player.ApplyConsumable(UseUserAnimation, () => UseGem(player), animMod);
            }
            else
                UseGem(player);
        }

        public void UseGem(Player player)
        {
            if (player.IsDead) return;

            // verify item is still valid
            if (player.FindObject(Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                //player.SendWeenieError(WeenieError.ObjectGone);   // results in 'Unable to move object!' transient error
                player.SendTransientError($"Cannot find the {Name}");   // custom message
                return;
            }

            // Mule Vendor: the summoning contract opts in by PropertyBool and runs its summon here,
            // sharing every gate with the /mule command (DESIGN 11.1).
            //
            // The IsDead check above and ActOnUse's IsBusy / Teleporting / suicideInProgress /
            // IsJumping checks are now ALSO enforced inside MuleSummonHandler.TrySummonerState, so the
            // /mule command path inherits them (fix round A, A4). The copies here are deliberately
            // left in place - they gate every other gem type in this file as well, so they are not the
            // mule feature's to remove - and they simply refuse a little earlier, with the retail
            // WeenieError wording. Do not delete them as "duplicates".
            if (GetProperty(PropertyBool.MuleVendorContract) == true && MuleSummonHandler.TryHandleUse(this, player))
                return;

            // Mule Form Token: used with NO target. Keyed on the PropertyBool rather than on a wcid -
            // deliberately unlike the Prismatic neighbour in HandleActionUseOnTarget - so a second
            // token weenie with different art and a different kill count is pure content.
            //
            // The token is NEVER consumed, on any branch. This returns before every consumption branch
            // below, the same way the summoning contract does.
            if (MuleFormToken.IsToken(this) && TryHandleMuleFormTokenUse(player))
                return;

            // class ability point item - a gem whose function is granting class ability points
            // (see ClassAbilityRegistry / Player_ClassAbilities). Data-driven: any Gem weenie with
            // PropertyInt.ClassAbilityPointValue set becomes a point item, no per-item code.
            var classAbilityPoints = GetProperty(PropertyInt.ClassAbilityPointValue) ?? 0;
            if (classAbilityPoints > 0)
            {
                if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat("Class abilities are not currently enabled on this server.", ChatMessageType.Broadcast));
                    return;
                }

                // defensive: only consume the item if the grant took (it can only be refused for a
                // non-positive amount, which the > 0 guard above already excludes - there is no cap)
                if (!player.GrantClassAbilityPoints(classAbilityPoints, Name, CapLedgerReason.GrantItem))
                    return;

                if (UseSound > 0)
                    player.Session.Network.EnqueueSend(new GameMessageSound(player.Guid, UseSound));

                if ((GetProperty(PropertyBool.UnlimitedUse) ?? false) == false)
                    player.TryConsumeFromInventoryWithNetworking(this, 1);

                return;
            }

            // class ability token - a Gem that teaches one specific class ability rank/tier when used, mirroring
            // /abilities learn: it runs the same learn path (so it spends the normal point cost) behind a
            // confirmation, with full success/failure branches. Data-driven via ClassAbilityTokenId +
            // ClassAbilityTokenTier; the token is consumed only on a successful learn (never on any failure).
            var tokenSkillId = GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0;
            if (tokenSkillId > 0)
            {
                UseClassAbilityToken(player, tokenSkillId, GetProperty(PropertyInt.ClassAbilityTokenTier) ?? 0, false);
                return;
            }

            // pick-up speed quest boon gem - a Gem with PropertyString.PickupBoonKey set claims a permanent
            // per-character pick-up speed bonus when used. Data-driven, mirroring the class ability token
            // branch above. See UsePickupBoonGem / Player_PickupBoons.cs.
            var pickupBoonKey = GetProperty(PropertyString.PickupBoonKey);
            if (!string.IsNullOrEmpty(pickupBoonKey))
            {
                UsePickupBoonGem(player);
                return;
            }

            // Threads (WaffleACE): a Gem carrying PropertyString.DungeonGemSpec opens or re-enters a
            // private dungeon copy. Sits before the portal-gem branch on purpose: a dungeon gem carries no
            // Destination, and every refusal or consume happens inside the handler. See ThreadDungeonGemHandler.
            if (ACE.Server.ThreadDungeons.ThreadDungeonGemHandler.TryHandleUse(this, player))
                return;

            // portal gem - a Gem with a Destination position teleports the player there instantly on use
            // (no recall animation). Data-driven: realm-aware via PortalRealm, guarded against combat use
            // and same-landblock cross-realm hops. See UsePortalGem.
            if (Destination != null)
            {
                UsePortalGem(player);
                return;
            }

            // trying to use a dispel potion while pk timer is active
            // send error message and cancel - do not consume item
            if (SpellDID != null)
            {
                var spell = new Spell(SpellDID.Value);

                if (spell.MetaSpellType == SpellType.Dispel && !VerifyDispelPKStatus(this, player))
                    return;
            }

            if (RareUsesTimer)
            {
                var currentTime = Time.GetUnixTime();

                player.LastRareUsedTimestamp = currentTime;

                // local broadcast usage
                player.EnqueueBroadcast(new GameMessageSystemChat($"{player.Name} used the rare item {Name}", ChatMessageType.Broadcast));
            }

            if (SpellDID.HasValue)
            {
                var spell = new Spell((uint)SpellDID);

                // Cast AS the player so the message reads 'You cast' rather than 'Item cast', but with this gem
                // passed as the itemCaster. Note the resulting CasterObjectId is NOT a reliable "came from a gem"
                // marker: WorldObject_Magic.cs:283-284 records an itemCaster that "is Gem" as the caster, but the
                // TryCastItemEnchantment_WithRedirects branch below drops the itemCaster before creating the
                // enchantment on the redirected item, so Impen/Bane/Aura entries land with the PLAYER as caster.
                // StripRareGemBuffs therefore identifies gem buffs by SPELL CATEGORY, not by caster.

                // TODO: figure this out better
                if (spell.MetaSpellType == SpellType.PortalSummon)
                    TryCastSpell(spell, player, this, tryResist: false);
                else if (spell.IsImpenBaneType || spell.IsItemRedirectableType)
                    player.TryCastItemEnchantment_WithRedirects(spell, player, this);
                else
                    player.TryCastSpell(spell, player, this, tryResist: false);
            }

            if (UseCreateContractId > 0)
            {
                if (!player.ContractManager.Add(UseCreateContractId.Value))
                    return;

                // this wasn't in retail, but the lack of feedback when using a contract gem just seems jarring so...
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{Name} accepted. Click on the quill icon in the lower right corner to open your contract tab to view your active contracts.", ChatMessageType.Broadcast));
            }

            if (UseCreateItem > 0)
            {
                if (!HandleUseCreateItem(player))
                    return;
            }

            if (UseSound > 0)
                player.Session.Network.EnqueueSend(new GameMessageSound(player.Guid, UseSound));

            if ((GetProperty(PropertyBool.UnlimitedUse) ?? false) == false)
                player.TryConsumeFromInventoryWithNetworking(this, 1);
        }

        /// <summary>
        /// Portal gem use: an instant teleport to the gem's Destination, resolved exactly the way a Portal
        /// weenie resolves its own destination (see Portal.ResolvePortalDestination, which both call so the
        /// realm routing can never drift). There is no recall animation and no delay - the gem is meant to be
        /// a pocket portal, so the guards below are what keep it from being an escape button: it is refused in
        /// combat (deliberately not auto-peacing the way HandleActionTeleToMarketPlace does) and while the PK
        /// timer is running. The gem is never consumed here - portal gems are expected to set UnlimitedUse,
        /// and this branch returns before the normal consumption path.
        /// </summary>
        private void UsePortalGem(Player player)
        {
            if (player.CombatMode != CombatMode.NonCombat)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You cannot use the {Name} while in combat.", ChatMessageType.Broadcast));
                return;
            }

            if (player.PKTimerActive)
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            var portalDest = new Position(Destination);
            AdjustDungeon(portalDest);

            portalDest = Portal.ResolvePortalDestination(this, player, portalDest);

            // a cross-realm hop that stays inside one landblock renders as a blend of both realms' content,
            // because the landblock is already loaded for the instance the player is standing in. The Town
            // Network and the Drift hub share landblock 0x0007, so this is reachable in practice - refuse it
            // rather than drop the player into a half-drawn world. See Content/realms/driftnetwork_entry.sql.
            if (player.Location.LandblockShort == portalDest.LandblockShort && player.Location.Instance != portalDest.Instance)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {Name} fizzles. It cannot be used this close to its destination's reflection.", ChatMessageType.Broadcast));
                return;
            }

            if (UseSound > 0)
                player.Session.Network.EnqueueSend(new GameMessageSound(player.Guid, UseSound));

            WorldManager.ThreadSafeTeleport(player, portalDest, new ActionEventDelegate(() =>
            {
                player.SendWeenieError(WeenieError.ITeleported);

            }), true);
        }

        /// <summary>
        /// Class ability token use: teaches the token's specific class ability rank (ClassAbilityTokenTier), which
        /// must be the player's current rank + 1, by running the same learn path /abilities learn uses - so it
        /// spends the normal class ability point cost. Confirmed before spending; the token is consumed only on
        /// a successful learn. Every failure branch (system disabled, misconfigured, wrong tier order, already
        /// max, not enough points, token gone) leaves the token untouched in the pack.
        /// </summary>
        private void UseClassAbilityToken(Player player, int tokenSkillId, int tier, bool confirmed)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("Class abilities are not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            // The registry - not Enum.IsDefined - is the source of truth: the generated Enhanced-stat family
            // uses synthetic ClassAbilityIds (0x1000+) that are registered but are not named enum members.
            if (tokenSkillId <= 0 ||
                !ClassAbilityRegistry.Abilities.TryGetValue((ClassAbilityId)tokenSkillId, out var skill))
            {
                // A token for a RETIRED ability is not misconfigured - it was legitimately bought, and its
                // points are recoverable at the exchanger (Player.RefundUnusedVoucher). Say so, instead of
                // telling the player their prepaid token is broken.
                if (tokenSkillId > 0 &&
                    RetiredClassAbilities.TryGetRefund((ClassAbilityId)tokenSkillId, GetProperty(PropertyInt.ClassAbilityTokenTier) ?? 0, out _, out var retiredName))
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"{retiredName} has been retired and can no longer be learned. Return the {Name} to a Drift Network exchanger to recover the class ability points it cost.",
                        ChatMessageType.Broadcast));
                    return;
                }

                player.SendTransientError($"The {Name} is misconfigured and cannot be used.");
                return;
            }

            // Every class ability token is a PREPAID voucher: the class ability point cost is charged when the
            // token is acquired (trainer vendor currency, or TryPurchaseClassAbilityVoucher), never here on use.
            // There is deliberately no per-instance discriminator - a token that reached the pack by any route
            // applies its rank for free, so no acquisition path can charge twice by forgetting to mark it.
            var rank = player.GetClassAbilityRank(skill.Id);

            // The point balance is irrelevant (already paid), so it must not fail the InsufficientPoints check -
            // pass an unbounded balance so only the rank/tier/implemented rules apply.
            var eval = ClassAbilityTokenCatalog.Evaluate(skill, rank, tier, int.MaxValue);

            // failure branches - all leave the token in the pack
            switch (eval.Outcome)
            {
                case ClassAbilityTokenOutcome.NotImplemented:
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{skill.DisplayName} cannot be learned yet.", ChatMessageType.Broadcast));
                    return;
                case ClassAbilityTokenOutcome.AlreadyMaxRank:
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You already have {skill.DisplayName} at its maximum rank ({rank}/{skill.MaxRank}).", ChatMessageType.Broadcast));
                    return;
                case ClassAbilityTokenOutcome.WrongTierOrder:
                    var wrong = tier <= rank
                        ? $"You already have {skill.DisplayName} rank {tier}."
                        : $"This {Name} teaches {skill.DisplayName} rank {tier}, but you must learn rank {rank + 1} first.";
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(wrong, ChatMessageType.Broadcast));
                    return;
                // No InsufficientPoints case: the balance passed to Evaluate above is int.MaxValue, so that
                // outcome is unreachable here. Affordability is decided at purchase, not at use.
            }

            // Tier 2/3 unlock gate - checked here too (not just in LearnClassAbility) so a locked token
            // reports why up front instead of after a pointless confirmation dialog. Leaves it in the pack.
            if (rank == 0 && !player.MeetsClassAbilityTierUnlock(skill, out var tierError))
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(tierError, ChatMessageType.Broadcast));
                return;
            }

            if (!confirmed)
            {
                var prompt = $"Use {Name} to learn {skill.DisplayName} rank {tier} of {skill.MaxRank}?\n\nThis token is already paid for and will be consumed.";
                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(player.Guid, () => UseClassAbilityToken(player, tokenSkillId, tier, true)), prompt))
                    player.SendWeenieError(WeenieError.ConfirmationInProgress);
                return;
            }

            // re-validate after the confirmation dialog - state may have changed while it was up
            if (player.FindObject(Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                player.SendTransientError($"Cannot find the {Name}");
                return;
            }
            rank = player.GetClassAbilityRank(skill.Id);
            if (tier != rank + 1)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You can no longer learn {skill.DisplayName} rank {tier}.", ChatMessageType.Broadcast));
                return;
            }

            // Re-checks Implemented / max rank / rank order; on any failure it returns false with a message and
            // no state change, so the token stays in the pack. The rank is applied for free - the points were
            // charged when the token was acquired.
            if (!player.ApplyClassAbilityRankPrepaid(skill, out var error))
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(error, ChatMessageType.Broadcast));
                return;
            }

            var newRank = player.GetClassAbilityRank(skill.Id);
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"You use {Name} and learn {skill.DisplayName} rank {newRank}/{skill.MaxRank}.", ChatMessageType.Broadcast));

            // The item level-up burst, so learning the rank reads as an upgrade.
            player.PlayParticleEffect(PlayScript.LevelUp, player.Guid);

            if (UseSound > 0)
                player.Session.Network.EnqueueSend(new GameMessageSound(player.Guid, UseSound));

            if ((GetProperty(PropertyBool.UnlimitedUse) ?? false) == false)
                player.TryConsumeFromInventoryWithNetworking(this, 1);
        }

        /// <summary>
        /// Pick-up speed quest boon gem use: claims the permanent bonus named by the gem's PickupBoonKey,
        /// mirroring UseClassAbilityToken's shape - every failure branch (misconfigured, mule, already
        /// claimed) leaves the gem in the pack, and it is consumed only after a successful claim.
        /// </summary>
        private void UsePickupBoonGem(Player player)
        {
            var key = GetProperty(PropertyString.PickupBoonKey);

            if (!player.TryClaimPickupBoon(key, Name, out var error))
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(error, ChatMessageType.Advancement));
                return;
            }

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(player.FormatPickupBoonClaimedMessage(Name), ChatMessageType.Advancement));

            // The item level-up burst, so claiming the boon reads as an upgrade (same as UseClassAbilityToken).
            player.PlayParticleEffect(PlayScript.LevelUp, player.Guid);

            if (UseSound > 0)
                player.Session.Network.EnqueueSend(new GameMessageSound(player.Guid, UseSound));

            if ((GetProperty(PropertyBool.UnlimitedUse) ?? false) == false)
                player.TryConsumeFromInventoryWithNetworking(this, 1);
        }

        public bool HandleUseCreateItem(Player player)
        {
            var amount = UseCreateQuantity ?? 1;

            var itemsToReceive = new ItemsToReceive(player);

            itemsToReceive.Add(UseCreateItem.Value, amount);

            if (itemsToReceive.PlayerExceedsLimits)
            {
                if (itemsToReceive.PlayerExceedsAvailableBurden)
                    player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "You are too encumbered to use that!"));
                else if (itemsToReceive.PlayerOutOfInventorySlots)
                    player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "You do not have enough pack space to use that!"));
                else if (itemsToReceive.PlayerOutOfContainerSlots)
                    player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "You do not have enough container slots to use that!"));

                return false;
            }

            if (itemsToReceive.RequiredSlots > 0)
            {
                var remaining = amount;

                while (remaining > 0)
                {
                    var item = WorldObjectFactory.CreateNewWorldObject(UseCreateItem.Value);

                    if (item is Stackable)
                    {
                        var stackSize = Math.Min(remaining, item.MaxStackSize ?? 1);

                        item.SetStackSize(stackSize);
                        remaining -= stackSize;
                    }
                    else
                        remaining--;

                    player.TryCreateInInventoryWithNetworking(item);
                }
            }
            else
            {
                player.SendTransientError($"Unable to use {Name} at this time!");
                return false;
            }
            return true;
        }

        public int? RareId
        {
            get => GetProperty(PropertyInt.RareId);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.RareId); else SetProperty(PropertyInt.RareId, value.Value); }
        }

        public bool RareUsesTimer
        {
            get => GetProperty(PropertyBool.RareUsesTimer) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.RareUsesTimer); else SetProperty(PropertyBool.RareUsesTimer, value); }
        }

        public override void HandleActionUseOnTarget(Player player, WorldObject target)
        {
            // should tailoring kit / aetheria be subtyped?
            if (Tailoring.IsTailoringKit(WeenieClassId))
            {
                Tailoring.UseObjectOnTarget(player, this, target);
                return;
            }

            // the Prismatic Drift Stone is a dynamic-outcome application (the rend it grants depends
            // on the target weapon's damage type), which data recipes cannot express - so it gets a
            // manager of its own, keyed off the gem's wcid, same as the tailoring kits above.
            if (PrismaticDriftStone.IsPrismaticDriftStone(WeenieClassId))
            {
                PrismaticDriftStone.UseObjectOnTarget(player, this, target);
                return;
            }

            // Mule Form Token: used ON a creature to attune it. Keyed on the PropertyBool, not on the
            // wcid the two neighbours above key on, so a second token weenie is pure content.
            //
            // The token is NEVER consumed, on any branch, and never falls through to the recipe
            // manager once it has been recognised.
            //
            // The token carries a Usable Target bit only while UNATTUNED (no Self bit), so the
            // ordinary no-target path is a plain double-click through UseGem once attunement has
            // rewritten the item to Contained (see HandleMuleFormTokenUseOnTarget). A self-target can
            // still arrive here if the player aims the use-on cursor at themselves; route it to the
            // same no-target handler rather than refusing it as an invalid donor.
            if (MuleFormToken.IsToken(this))
            {
                if (target == player)
                    TryHandleMuleFormTokenUse(player);
                else
                    HandleMuleFormTokenUseOnTarget(player, target);

                // Player_Use.HandleActionUseWithTarget sends no GameEventUseDone after this call; the
                // handler that ends a use-on-target owns it (RecipeManager, PrismaticDriftStone and
                // Healer all send it on every branch), or the client's use state stays locked. The
                // double-click path does not need it: TryUseItem sends it after OnActivate returns.
                player.SendUseDoneEvent();
                return;
            }

            // Threads: a Raw Fragment is a Gem carrying DungeonGemSpec whose ItemUseable has target
            // bits. Keyed on the property already reserved for the spec rather than on a wcid or a second
            // bool, so the ten rung weenies (and any later rung) are pure content. A finished Thread Gem
            // carries the same property but ItemUseable Contained, so the client never sends a use-on for
            // one; if one ever arrives, RawFragment refuses it by name.
            if (GetProperty(PropertyString.DungeonGemSpec) != null)
            {
                ACE.Server.ThreadDungeons.RawFragment.UseObjectOnTarget(player, this, target);

                // Same reason as the Mule branch above: Player_Use.HandleActionUseWithTarget sends no
                // GameEventUseDone after this call, so the handler that ends a use-on-target owns it.
                player.SendUseDoneEvent();
                return;
            }

            // fallback on recipe manager?
            base.HandleActionUseOnTarget(player, target);
        }

        /// <summary>
        /// The no-target use of a mule form token. Returns true when it handled the use.
        ///
        /// Thin by design: every decision it makes belongs to
        /// <see cref="ACE.Server.Entity.AccountVault.MuleFormToken"/>, which is unit-tested, and every
        /// player-facing string here is one of that class's constants or one of its Compose results.
        /// Nothing in this method can be unit-tested, because Player.UpdateProperty's SendNetwork
        /// dereferences Session with no null check.
        /// </summary>
        private bool TryHandleMuleFormTokenUse(Player player)
        {
            var formWcid = MuleFormToken.GetFormWcid(this);

            if (formWcid == null)
            {
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(MuleFormToken.NeedsATargetMessage, ChatMessageType.Broadcast));
                return true;
            }

            if (!MuleFormToken.IsComplete(this))
            {
                // Chat gets only the short status line, never the item's full instructions. Recomposed
                // from the attuned WCID, never from this item's Name: the Name has already been
                // rewritten to "Beast Effigy (Tusker Guard)" by the time any token can reach this
                // branch, so passing it as the donor name would read "Attuned to Beast Effigy (Tusker
                // Guard). 37 of 100 slain."
                var status = MuleFormToken.ComposeStatus(
                    ResolveDonorName(formWcid.Value), MuleFormToken.GetStructure(this), MuleFormToken.GetMaxStructure(this));

                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(status, ChatMessageType.Broadcast));
                return true;
            }

            var accountId = player.Account?.AccountId ?? 0;

            if (accountId == 0)
            {
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(AccountVaultStore.UnavailableMessage, ChatMessageType.Broadcast));
                return true;
            }

            var store = AccountVaultManager.GetStore(accountId);

            if (store == null)
            {
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(AccountVaultStore.UnavailableMessage, ChatMessageType.Broadcast));
                return true;
            }

            // THE WRITE COMES FIRST, AND THE TWO DECISIONS ARE DELIBERATELY SEPARATE (design section 8).
            // A summon can be refused for reasons that have nothing to do with the look - wrong
            // landblock, cooldown, mid-jump - and a player who spent a hundred kills earning a form must
            // never be told it was wasted because of where they happened to be standing.
            if (!store.TrySetMuleForm(formWcid.Value, ResolveDonorName(formWcid.Value), VaultActor.From(player), out var saveFailure))
            {
                // The look was NOT saved, so there is nothing to summon in and no summon is attempted.
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(saveFailure ?? AccountVaultStore.UnavailableMessage, ChatMessageType.Broadcast));
                return true;
            }

            if (!MuleSummonHandler.TrySummon(player, accountId, out var summonFailure, player.Name))
            {
                var reason = summonFailure ?? AccountVaultStore.UnavailableMessage;

                player.Session?.Network.EnqueueSend(new GameMessageSystemChat($"{reason} {MuleFormToken.LookSavedSuffix}", ChatMessageType.Broadcast));
            }

            return true;
        }

        /// <summary>
        /// The use-on-target path of a mule form token: attunement. There is no re-attunement by
        /// design, so an already-attuned token refuses every target rather than being rewritten.
        ///
        /// Every client-visible write goes through <see cref="Player.UpdateProperty(WorldObject, PropertyInt, int?, bool)"/>
        /// and its string and data id siblings, never SetProperty: SetProperty alone writes the biota
        /// and sends nothing, so the renamed token, its new description, and its icon overlay (set
        /// from the donor's own Icon property) would not reach the client until some unrelated update
        /// happened to re-send them.
        /// </summary>
        private void HandleMuleFormTokenUseOnTarget(Player player, WorldObject target)
        {
            if (MuleFormToken.IsAttuned(this))
            {
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(MuleFormToken.AlreadyAttunedMessage, ChatMessageType.Broadcast));
                return;
            }

            if (!MuleFormToken.IsValidAttunementTarget(target, MuleFormToken.DatSetupHeight, MuleFormToken.DatSetupEffectScale, out var refusal))
            {
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(refusal, ChatMessageType.Broadcast));
                return;
            }

            var donorName = target.Name;
            var maxStructure = MuleFormToken.GetMaxStructure(this);
            var structure = MuleFormToken.GetStructure(this);
            var longDesc = MuleFormToken.ComposeLongDesc(donorName, structure, maxStructure);

            player.UpdateProperty(this, PropertyInt.MuleFormWcid, (int)target.WeenieClassId);

            // The overlay is the donor's own inventory icon, so the red gem visually shows the attuned monster.
            // The full-object refresh sent below (GameMessageUpdateObject) carries the new overlay to the client.
            player.UpdateProperty(this, PropertyDataId.IconOverlay, target.GetProperty(PropertyDataId.Icon));

            player.UpdateProperty(this, PropertyString.Name, MuleFormToken.ComposeName(MuleFormToken.GetBaseName(this), donorName, complete: false));
            player.UpdateProperty(this, PropertyString.LongDesc, longDesc);

            // The token is attuned for good, so the use-on cursor has no further purpose and would
            // stop a plain double-click from ever reaching UseGem (with a Usable Target bit the client
            // sends nothing for a target-less use; seen live 2026-09-02). Rewrite the item to plain
            // Contained and push a full object refresh, since the client reads usability from the
            // object description rather than from the property update alone. From here on a
            // double-click is the status line while filling and the summon once complete.
            player.UpdateProperty(this, PropertyInt.ItemUseable, (int)Usable.Contained);
            player.EnqueueBroadcast(new GameMessageUpdateObject(this));
            if (player.FindObject(Guid.Full, Player.SearchLocations.MyInventory) != null)
                player.MoveItemToFirstContainerSlot(this);

            // The confirmation is the short status line, not the item's full description: chat gets
            // only the status, the item carries the full instructions.
            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(MuleFormToken.ComposeStatus(donorName, structure, maxStructure), ChatMessageType.Broadcast));
        }

        /// <summary>
        /// The donor's name for the account_mule_form snapshot column. A world-cache read, so it stays
        /// out of <see cref="ACE.Server.Entity.AccountVault.MuleFormToken"/>, which is otherwise pure.
        /// The column is operator convenience and nothing keys on it, so a donor whose weenie has since
        /// been deleted gets a legible placeholder rather than failing the write.
        /// </summary>
        private static string ResolveDonorName(uint wcid)
        {
            var name = DatabaseManager.World.GetCachedWeenie(wcid)?.GetName();

            return string.IsNullOrWhiteSpace(name) ? $"wcid {wcid}" : name;
        }

        /// <summary>
        /// For Rares that use cooldown timers (RareUsesTimer),
        /// any other rares with RareUsesTimer may not be used for 3 minutes
        /// Note that if the player logs out, this cooldown timer continues to tick/expire (unlike enchantments)
        /// </summary>
        public static int RareTimer = 180;

        public string UseSendsSignal
        {
            get => GetProperty(PropertyString.UseSendsSignal);
            set { if (value == null) RemoveProperty(PropertyString.UseSendsSignal); else SetProperty(PropertyString.UseSendsSignal, value); }
        }

        public override void OnActivate(WorldObject activator)
        {
            if (ItemUseable == Usable.Contained && activator is Player player)
            {               
                var containedItem = player.FindObject(Guid.Full, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);
                if (containedItem != null) // item is contained by player
                {
                    if (player.IsBusy || player.Teleporting || player.suicideInProgress)
                    {
                        player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YoureTooBusy));
                        player.EnchantmentManager.StartCooldown(this);
                        return;
                    }

                    if (player.IsDead)
                    {
                        player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.Dead));
                        player.EnchantmentManager.StartCooldown(this);
                        return;
                    }
                }
                else
                    return;
            }

            base.OnActivate(activator);
        }
    }
}
