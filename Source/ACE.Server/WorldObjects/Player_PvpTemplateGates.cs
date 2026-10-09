using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Refusal delivery for the Phase B template gate sites (Docs/Pvp/TEMPLATES.md "Gates"). The decision is always
    /// Player.PvpTemplateBlocked / PvpTemplateCastBlocked; this only puts the PvpTemplateText string in front of
    /// the player so every site says it the same way.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// The Enlightenment every stat read site uses (TEMPLATES.md power-source table, "Mask via EffectiveEnlightenment").
        /// While templated the build is the template's, which carries no Enlightenment, so this reads 0. The
        /// stored PropertyInt.Enlightenment is NEVER written: Player_AltCharacterBonus reads other characters'
        /// stored value for the alt bonus, and the restore needs nothing put back. A player with no Enlightenment
        /// (the common case, and the hot attribute getter) answers from the first read without touching the record.
        /// </summary>
        public int EffectiveEnlightenment
        {
            get
            {
                var enlightenment = Enlightenment;

                if (enlightenment == 0)
                    return 0;

                return IsPvpTemplated ? 0 : enlightenment;
            }
        }

        /// <summary>
        /// True while the heritage weapon bonus is off (TEMPLATES.md "The heritage weapon bonus is off while templated"):
        /// templated AND pvp_template_suppress_heritage_bonus. Every read of the bonus (the damage event, the spell
        /// projectile, the war-magic path, the appraisal panel) goes through Player.GetHeritageBonus, which asks this.
        /// </summary>
        public bool HeritageBonusSuppressed => IsPvpTemplated && PvpTemplateSettings.SuppressHeritageBonusSource();

        /// <summary>
        /// Which gate action an inventory move maps to, from where the object is and where it is going. Pure, so a
        /// test can pin it. Within the player: a stack move (split or merge) is MergeOrSplit, an unequip is Unequip,
        /// anything else MoveWithinPack. From the world into the player: GroundPickup. Out of the player to anywhere
        /// else: MoveToForeignContainer.
        /// </summary>
        internal static PvpTemplateAction PvpTemplateMoveAction(bool fromPlayer, bool toPlayer, bool wasEquipped, bool stackMove)
        {
            if (fromPlayer && toPlayer)
                return stackMove ? PvpTemplateAction.MergeOrSplit : wasEquipped ? PvpTemplateAction.Unequip : PvpTemplateAction.MoveWithinPack;

            return toPlayer ? PvpTemplateAction.GroundPickup : PvpTemplateAction.MoveToForeignContainer;
        }

        /// <summary>
        /// A split-off stack is built fresh from the weenie, so it carries none of the source's issued stamps. When
        /// the source is an issued stack the new stack is stamped the same way PvpTemplateKit.BuildIssuedBiota stamps
        /// a kit item (issued mark, Attuned, Bonded, Value 0), or the split would hand the player a personal copy of
        /// an issued item that the restore and the login sweep (both keyed on the mark) would never remove.
        /// </summary>
        internal static void StampPvpTemplateSplitStack(WorldObject source, WorldObject newStack)
        {
            if (newStack == null || !PvpTemplate.IsMarkedIssued(source))
                return;

            newStack.SetProperty(PropertyBool.PvpTemplateIssued, true);
            newStack.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            newStack.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            newStack.Value = 0;

            // Built fresh from the weenie, so SetStackSize has already priced the new stack at the weenie's burden. An issued
            // ammunition or spell component stack is weightless (PvpTemplateKit.BuildIssuedBiota), and so is its child.
            if (PvpTemplateKit.IsWeightlessIssuedType(newStack.WeenieType))
            {
                newStack.StackUnitEncumbrance = 0;
                newStack.EncumbranceVal = 0;
            }
        }

        /// <summary>
        /// The pack items of <paramref name="wcid"/> that may be used as spell components right now: each is asked of
        /// the gate (SpellComponent), so while templated only issued components count and burn, and an issued one
        /// outside a match counts for nothing.
        /// </summary>
        internal List<WorldObject> GetSpellComponentItems(uint wcid)
        {
            var items = GetInventoryItemsOfWCID(wcid);

            // Not templated: only issued components are excluded, so skip the per-item gate and its allocations.
            if (!IsPvpTemplated && !PvpTemplateSystemBypass)
            {
                foreach (var i in items)
                {
                    if (PvpTemplate.IsIssued(i))
                        return items.Where(x => !PvpTemplate.IsIssued(x)).ToList();
                }

                return items;
            }

            return items.Where(i => PvpTemplateBlocked(PvpTemplateAction.SpellComponent, i) == null).ToList();
        }

        /// <summary>Gate for the trade handlers: true (with the refusal sent) when this player's template forbids trading.</summary>
        internal bool PvpTemplateTradeRefused() => PvpTemplateRefuses(PvpTemplateAction.Trade);

        /// <summary>Asks the gate about <paramref name="action"/> with no item; true (refusal already sent) when it refuses. For command and handler entry points.</summary>
        internal bool PvpTemplateRefuses(PvpTemplateAction action)
        {
            var refusal = PvpTemplateBlocked(action);

            if (refusal == null)
                return false;

            SendPvpTemplateRefusal(refusal);
            return true;
        }

        /// <summary>The number of units of component <paramref name="wcid"/> that count for a cast (see <see cref="GetSpellComponentItems"/>).</summary>
        internal int GetNumSpellComponents(uint wcid)
            => GetSpellComponentItems(wcid).Select(i => i.StackSize ?? 1).Sum();

        /// <summary>Sends one PvpTemplateText refusal to the player as a broadcast chat line. Null or empty sends nothing.</summary>
        internal void SendPvpTemplateRefusal(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
