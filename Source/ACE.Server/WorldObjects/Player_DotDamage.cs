using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Whether this player sees the outgoing damage-over-time combat message: the line the
        /// caster sees when their own DoT spell ticks on a target. A null underlying property
        /// means the player has never set a preference, so this falls back to the server-wide
        /// PropertyManager tunable show_dot_messages (default false). Set by the player via
        /// /dotdamage on|off. Gates ONLY the caster-side message - the victim's incoming DoT
        /// message is unconditional and unaffected by this property.
        /// </summary>
        public bool ShowDotDamage
        {
            get => GetProperty(PropertyBool.ShowDotDamage) ?? PropertyManager.GetBool("show_dot_messages").Item;
            set => SetProperty(PropertyBool.ShowDotDamage, value);
        }

        /// <summary>
        /// The player's own ShowDotDamage choice, or null if they have never set one and are
        /// currently inheriting the server-wide show_dot_messages tunable.
        /// </summary>
        public bool? ShowDotDamageOverride => GetProperty(PropertyBool.ShowDotDamage);
    }
}
