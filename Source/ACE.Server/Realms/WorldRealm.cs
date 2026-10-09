using ACE.Entity;
using ACE.Entity.Enum;

namespace ACE.Server.Realms
{
    /// <summary>
    /// ACRealms port (rulesets stubbed): a permanent parallel copy of the world.
    /// Realm 0 is the implicit base world and always exists.
    /// </summary>
    public class WorldRealm
    {
        public ushort Id { get; }
        public string Name { get; }
        public RealmType Type { get; }
        public ushort? ParentRealmId { get; }

        public WorldRealm(ushort id, string name, RealmType type, ushort? parentRealmId = null)
        {
            Id = id;
            Name = name;
            Type = type;
            ParentRealmId = parentRealmId;
        }

        /// <summary>
        /// The default (shortInstanceId 0, non-ephemeral) instance of this realm
        /// </summary>
        public uint DefaultInstanceID => Position.InstanceIDFromVars(Id, 0, false);

        public override string ToString() => $"{Id}: {Name} ({Type})";
    }
}
