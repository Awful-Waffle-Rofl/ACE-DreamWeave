using System;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
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
                if (!player.GrantClassAbilityPoints(classAbilityPoints, Name))
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

            // Attuned Drift Prism: permanently aligns an orb to one damage type. Self-contained
            // wcid-set check (AttunedDriftPrism.PrismElements) so this dispatch stays one block.
            if (AttunedDriftPrism.IsPrism(WeenieClassId))
            {
                AttunedDriftPrism.UseObjectOnTarget(player, this, target);
                return;
            }

            // fallback on recipe manager?
            base.HandleActionUseOnTarget(player, target);
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
