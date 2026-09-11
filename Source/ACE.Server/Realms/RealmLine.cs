using ACE.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Realms
{
    /// <summary>
    /// The single definition of the "[REALM] ..." chat line's wire format, published so a Decal
    /// plugin can recover a player's realm/instance without the client ever seeing
    /// Position.Instance (only Cell is sent).
    ///
    /// This format is a CONSUMED CONTRACT: name= must stay LAST and no key may be added after it,
    /// because realm names may contain spaces and a parser takes the whole remainder of the line
    /// as the name. Keys must never be reordered or renamed.
    /// </summary>
    public static class RealmLine
    {
        public const string Prefix = "[REALM] ";

        /// <summary>
        /// Pure formatter - no manager calls, so it is safe to unit test directly.
        /// </summary>
        public static string Format(ushort realmId, string realmName, ushort shortInstanceId, bool isEphemeral)
        {
            var name = string.IsNullOrWhiteSpace(realmName) ? "unregistered" : realmName;

            return $"{Prefix}id={realmId} inst={shortInstanceId} eph={(isEphemeral ? 1 : 0)} name={name}";
        }

        public static string ForPosition(Position position)
        {
            if (position == null)
                return Format(0, null, 0, false);

            Position.ParseInstanceID(position.Instance, out var isEphemeral, out var realmId, out var shortInstanceId);

            var realmName = RealmManager.GetRealm(realmId)?.Name;

            return Format(realmId, realmName, shortInstanceId, isEphemeral);
        }
    }
}
