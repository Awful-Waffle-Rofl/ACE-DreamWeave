using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// The one item test every template gate uses (TEMPLATES.md "Gates"): no site reads PvpTemplateIssued
    /// inline.
    /// </summary>
    public static class PvpTemplate
    {
        /// <summary>Recursion guard for a corrupt container cycle. A real pack nests one level.</summary>
        private const int MaxDepth = 8;

        /// <summary>
        /// True when <paramref name="item"/> is an issued template item, or is a container holding one at any depth.
        /// A personal side pack with an issued potion in it is therefore treated as issued, so a gate on giving,
        /// dropping or vaulting the pack refuses it - the issued item cannot leave the player inside a personal
        /// container.
        /// </summary>
        public static bool IsIssued(WorldObject item) => IsIssued(item, 0);

        private static bool IsIssued(WorldObject item, int depth)
        {
            if (item == null)
                return false;

            if (item.GetProperty(PropertyBool.PvpTemplateIssued) == true)
                return true;

            // Only a real container (a pack, a chest) is looked inside. A Creature - a player, a monster, an NPC - is
            // also a Container in the class tree, but what it carries is not "inside the item": a player is never
            // an issued item because they hold one.
            if (depth >= MaxDepth || item is not Container container || item is Creature || container.Inventory == null)
                return false;

            foreach (var child in container.Inventory.Values)
            {
                if (IsIssued(child, depth + 1))
                    return true;
            }

            return false;
        }

        /// <summary>True when the object ITSELF carries the issued mark (no recursion). The login sweep and the restore key on this.</summary>
        public static bool IsMarkedIssued(WorldObject item) => item?.GetProperty(PropertyBool.PvpTemplateIssued) == true;
    }
}
