using System;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    public abstract class Confirmation
    {
        public ObjectGuid PlayerGuid;

        public ConfirmationType ConfirmationType;

        public uint ContextId;

        public Confirmation(ObjectGuid playerGuid, ConfirmationType confirmationType)
        {
            PlayerGuid = playerGuid;

            ConfirmationType = confirmationType;
        }

        public virtual void ProcessConfirmation(bool response, bool timeout = false)
        {
            // empty base
        }

        /// <summary>
        /// True only for a dialog whose Yes is the arena's own exit (Confirmation_PvpArenaLeave). Every other Yes is
        /// refused by ConfirmationManager.HandleResponse while the player wears a PvP template (default-deny).
        /// </summary>
        public virtual bool PermittedWhilePvpTemplated => false;

        public Player Player => PlayerManager.GetOnlinePlayer(PlayerGuid);
    }

    public class Confirmation_AlterAttribute: Confirmation
    {
        public ObjectGuid AttributeTransferDevice;

        public Confirmation_AlterAttribute(ObjectGuid playerGuid, ObjectGuid attributeTransferDevice)
            : base(playerGuid, ConfirmationType.AlterAttribute)
        {
            AttributeTransferDevice = attributeTransferDevice;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (!response) return;

            var player = Player;
            if (player == null) return;

            var attributeTransferDevice = player.FindObject(AttributeTransferDevice.Full, Player.SearchLocations.MyInventory) as AttributeTransferDevice;

            if (attributeTransferDevice != null)
                attributeTransferDevice.ActOnUse(player, true);
        }
    }

    public class Confirmation_AlterSkill : Confirmation
    {
        public ObjectGuid SkillAlterationDevice;

        public Confirmation_AlterSkill(ObjectGuid playerGuid, ObjectGuid skillAlterationDevice)
            : base(playerGuid, ConfirmationType.AlterSkill)
        {
            SkillAlterationDevice = skillAlterationDevice;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (!response) return;

            var player = Player;
            if (player == null) return;

            var skillAlterationDevice = player.FindObject(SkillAlterationDevice.Full, Player.SearchLocations.MyInventory) as SkillAlterationDevice;

            if (skillAlterationDevice != null)
                skillAlterationDevice.ActOnUse(player, true);
        }
    }

    public class Confirmation_Augmentation: Confirmation
    {
        public ObjectGuid AugmentationGuid;

        public Confirmation_Augmentation(ObjectGuid playerGuid, ObjectGuid augmentationGuid)
            : base(playerGuid, ConfirmationType.Augmentation)
        {
            AugmentationGuid = augmentationGuid;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (!response) return;

            var player = Player;
            if (player == null) return;

            var augmentation = player.FindObject(AugmentationGuid.Full, Player.SearchLocations.MyInventory) as AugmentationDevice;

            if (augmentation != null)
                augmentation.ActOnUse(player, true);
        }
    }

    public class Confirmation_CraftInteration: Confirmation
    {
        public ObjectGuid SourceGuid;
        public ObjectGuid TargetGuid;

        public bool Tinkering;

        public Confirmation_CraftInteration(ObjectGuid playerGuid, ObjectGuid sourceGuid, ObjectGuid targetGuid)
            : base (playerGuid, ConfirmationType.CraftInteraction)
        {
            SourceGuid = sourceGuid;
            TargetGuid = targetGuid;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null) return;

            if (!response)
            {
                player.SendWeenieError(WeenieError.YouChickenOut);

                return;
            }

            // inventory only?
            var source = player.FindObject(SourceGuid.Full, Player.SearchLocations.LocationsICanMove);
            var target = player.FindObject(TargetGuid.Full, Player.SearchLocations.LocationsICanMove);

            if (source == null || target == null) return;

            RecipeManager.UseObjectOnTarget(player, source, target, true);
        }
    }

    /// <summary>
    /// "Are you sure?" for the Prismatic Drift Stone, whose application permanently closes the
    /// target weapon's tinkering future.
    ///
    /// Replay safety comes from ConfirmationManager: HandleResponse TryRemoves this instance from
    /// the per-player dictionary BEFORE calling ProcessConfirmation, and rejects any response whose
    /// contextId does not match. So only this dialog, answered once, can run this code - a stale or
    /// duplicated client response finds nothing to confirm. The work itself re-verifies from scratch.
    /// </summary>
    public class Confirmation_PrismaticDriftStone : Confirmation
    {
        public ObjectGuid SourceGuid;
        public ObjectGuid TargetGuid;

        public Confirmation_PrismaticDriftStone(ObjectGuid playerGuid, ObjectGuid sourceGuid, ObjectGuid targetGuid)
            : base(playerGuid, ConfirmationType.CraftInteraction)
        {
            SourceGuid = sourceGuid;
            TargetGuid = targetGuid;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null) return;

            if (!response)
            {
                player.SendWeenieError(WeenieError.YouChickenOut);
                return;
            }

            var source = player.FindObject(SourceGuid.Full, Player.SearchLocations.LocationsICanMove);
            var target = player.FindObject(TargetGuid.Full, Player.SearchLocations.LocationsICanMove);

            if (source == null || target == null) return;

            PrismaticDriftStone.UseObjectOnTarget(player, source, target, true);
        }
    }

    /// <summary>Confirmation for applying a Cloak Mantle / Proc Amplifier gem; re-verifies from scratch on the yes.</summary>
    public class Confirmation_CloakMantle : Confirmation
    {
        public ObjectGuid SourceGuid;
        public ObjectGuid TargetGuid;

        public Confirmation_CloakMantle(ObjectGuid playerGuid, ObjectGuid sourceGuid, ObjectGuid targetGuid)
            : base(playerGuid, ConfirmationType.CraftInteraction)
        {
            SourceGuid = sourceGuid;
            TargetGuid = targetGuid;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null) return;

            if (!response)
            {
                player.SendWeenieError(WeenieError.YouChickenOut);
                return;
            }

            var source = player.FindObject(SourceGuid.Full, Player.SearchLocations.LocationsICanMove);
            var target = player.FindObject(TargetGuid.Full, Player.SearchLocations.LocationsICanMove);

            if (source == null || target == null) return;

            CloakMantleApplicator.UseObjectOnTarget(player, source, target, true);
        }
    }

    /// <summary>
    /// Confirmation for a Paragon gem that would push a mod past a perfect roll. Copied from
    /// Confirmation_CloakMantle: No or timeout does nothing, Yes re-enters the manager, which re-verifies from scratch.
    /// </summary>
    public class Confirmation_ParagonOverflow : Confirmation
    {
        public ObjectGuid SourceGuid;
        public ObjectGuid TargetGuid;

        public Confirmation_ParagonOverflow(ObjectGuid playerGuid, ObjectGuid sourceGuid, ObjectGuid targetGuid)
            : base(playerGuid, ConfirmationType.CraftInteraction)
        {
            SourceGuid = sourceGuid;
            TargetGuid = targetGuid;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null) return;

            if (!response)
            {
                player.SendWeenieError(WeenieError.YouChickenOut);
                return;
            }

            ACE.Server.WeaponMods.ParagonModManager.OnOverflowConfirmed(player, SourceGuid, TargetGuid);
        }
    }

    /// <summary>Confirmation for applying a Jewelers Aspect Gem; re-verifies from scratch on the yes.</summary>
    public class Confirmation_JewelryAspect : Confirmation
    {
        public ObjectGuid SourceGuid;
        public ObjectGuid TargetGuid;

        public Confirmation_JewelryAspect(ObjectGuid playerGuid, ObjectGuid sourceGuid, ObjectGuid targetGuid)
            : base(playerGuid, ConfirmationType.CraftInteraction)
        {
            SourceGuid = sourceGuid;
            TargetGuid = targetGuid;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null) return;

            if (!response)
            {
                player.SendWeenieError(WeenieError.YouChickenOut);
                return;
            }

            var source = player.FindObject(SourceGuid.Full, Player.SearchLocations.LocationsICanMove);
            var target = player.FindObject(TargetGuid.Full, Player.SearchLocations.LocationsICanMove);

            if (source == null || target == null) return;

            JewelryAspectApplicator.UseObjectOnTarget(player, source, target, true);
        }
    }

    public class Confirmation_Fellowship : Confirmation
    {
        public ObjectGuid InviterGuid;

        public Confirmation_Fellowship(ObjectGuid inviterGuid, ObjectGuid invitedGuid)
            : base(invitedGuid, ConfirmationType.Fellowship)
        {
            InviterGuid = inviterGuid;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            //if (!response) return;

            var invited = Player;
            var inviter = PlayerManager.GetOnlinePlayer(InviterGuid);

            if (!response)
            {
                inviter?.SendMessage($"{invited.Name} {(timeout ? "did not respond to" : "has declined")} your offer of fellowship.");
                return;
            }

            if (invited != null && inviter != null && inviter.Fellowship != null)
                inviter.Fellowship.AddConfirmedMember(inviter, invited, response);
        }
    }

    public class Confirmation_SwearAllegiance : Confirmation
    {
        public ObjectGuid VassalGuid;

        public Confirmation_SwearAllegiance(ObjectGuid patronGuid, ObjectGuid vassalGuid)
            : base(patronGuid, ConfirmationType.SwearAllegiance)
        {
            VassalGuid = vassalGuid;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            //if (!response) return;

            var patron = Player;
            if (patron == null) return;

            var vassal = PlayerManager.GetOnlinePlayer(VassalGuid);

            if (!response)
            {
                vassal?.SendMessage($"{patron.Name} {(timeout ? "did not respond to" : "has declined")} your offer of allegiance.");
                return;
            }

            if (vassal != null)
                vassal.SwearAllegiance(patron.Guid.Full, true, true);
        }
    }

    public class Confirmation_YesNo: Confirmation
    {
        public ObjectGuid SourceGuid;

        public string Quest;

        public Confirmation_YesNo(ObjectGuid sourceGuid, ObjectGuid targetPlayerGuid, string quest)
            : base(targetPlayerGuid, ConfirmationType.Yes_No)
        {
            SourceGuid = sourceGuid;
            Quest = quest;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null) return;

            var source = player.FindObject(SourceGuid.Full, Player.SearchLocations.Landblock);

            if (source is Hook hook && hook.Item != null)
                source = hook.Item;

            if (source != null)
                source.EmoteManager.ExecuteEmoteSet(response ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, Quest, player);
        }
    }

    /// <summary>How a <see cref="Confirmation_Choice"/> answer was read.</summary>
    public enum ConfirmationChoiceAnswer
    {
        /// <summary>A timeout, or a late answer (Confirmation_ThreadGroupStart.IsLate): nothing runs.</summary>
        Ignored,

        /// <summary>An explicit Yes: the Yes action runs.</summary>
        Yes,

        /// <summary>An explicit No: the No action runs (when there is one).</summary>
        No,
    }

    /// <summary>
    /// A Yes/No confirmation whose Yes AND explicit No each run an action, and whose timeout runs nothing
    /// (Confirmation_Custom runs only its Yes). Timeout: for Yes_No, ConfirmationManager's 30 s abort only says "You
    /// waited too long" and leaves the entry, and the client's automatic reply then arrives as a plain No. So an answer
    /// flagged as a timeout, or late by Confirmation_ThreadGroupStart.IsLate (the same 29.5 s rule), is
    /// <see cref="ConfirmationChoiceAnswer.Ignored"/>. The answer is read when it arrives; the action then runs on the
    /// PLAYER's own action queue (the Confirmation_ThreadGroupStart hop), so it may read the player's position and open
    /// another confirmation there.
    /// </summary>
    public class Confirmation_Choice : Confirmation
    {
        public Action OnYes { get; }

        /// <summary>Runs on an explicit No. Null: an explicit No does nothing.</summary>
        public Action OnNo { get; }

        public DateTime SentUtc { get; }

        public Confirmation_Choice(ObjectGuid playerGuid, Action onYes, Action onNo, DateTime sentUtc)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            OnYes = onYes;
            OnNo = onNo;
            SentUtc = sentUtc;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null)
                return;

            var answer = Classify(response, timeout, SentUtc, DateTime.UtcNow);

            if (answer == ConfirmationChoiceAnswer.Ignored)
                return;

            player.EnqueueAction(new ActionEventDelegate(() => Run(answer)));
        }

        /// <summary>The answer rule (<see cref="Classify"/>), then the matching action inline. Player-free, for the unit tests.</summary>
        internal ConfirmationChoiceAnswer Dispatch(bool response, bool timeout, DateTime nowUtc)
        {
            var answer = Classify(response, timeout, SentUtc, nowUtc);
            Run(answer);
            return answer;
        }

        private void Run(ConfirmationChoiceAnswer answer)
        {
            if (answer == ConfirmationChoiceAnswer.Yes)
                OnYes?.Invoke();
            else if (answer == ConfirmationChoiceAnswer.No)
                OnNo?.Invoke();
        }

        /// <summary>A timeout or a late answer (Confirmation_ThreadGroupStart.IsLate) is Ignored; otherwise Yes or No as answered. Pure.</summary>
        internal static ConfirmationChoiceAnswer Classify(bool response, bool timeout, DateTime sentUtc, DateTime nowUtc)
        {
            if (Confirmation_ThreadGroupStart.IsLate(sentUtc, nowUtc, timeout))
                return ConfirmationChoiceAnswer.Ignored;

            return response ? ConfirmationChoiceAnswer.Yes : ConfirmationChoiceAnswer.No;
        }
    }

    public class Confirmation_Custom: Confirmation
    {
        public Action Action;

        public Confirmation_Custom(ObjectGuid playerGuid, Action action)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            Action = action;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (!response) return;

            var player = Player;
            if (player == null) return;

            Action();
        }
    }
}
