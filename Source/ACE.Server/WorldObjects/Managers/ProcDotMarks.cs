using System.Runtime.CompilerServices;

using ACE.Entity.Models;

namespace ACE.Server.WorldObjects.Managers
{
    /// <summary>
    /// FORK: in-memory "excluded from the Focus DoT bonus" mark per enchantment registry entry. The DoT tick cannot
    /// tell a hand-cast DoT from an item cast-on-strike, cloak or class-ability proc DoT (all record the player as
    /// CasterObjectId), and a melee bleed (Dirty Fighting) or a Nether Bloom copy of a marked DoT is not a "casted
    /// damage spell" either, so each of those applications is marked here and the tick skips the bonus for it.
    /// The parameter is still called procSourced. Keyed by entry REFERENCE: a refresh mutates the same entry object
    /// in place and the tick reads the same objects out of the biota, so the mark follows the entry. Every
    /// application OVERWRITES the mark (latest application wins). Never persisted: an unmarked entry (including
    /// one reloaded from the database) is not excluded. Weak keys, so a removed entry's mark is collected with it.
    /// </summary>
    public static class ProcDotMarks
    {
        private static readonly ConditionalWeakTable<PropertiesEnchantmentRegistry, StrongBox<bool>> marks = new();

        public static void Set(PropertiesEnchantmentRegistry entry, bool procSourced)
        {
            if (entry == null)
                return;

            // Only a true mark allocates; clearing updates an existing box and otherwise does nothing.
            if (procSourced)
                marks.GetValue(entry, _ => new StrongBox<bool>()).Value = true;
            else if (marks.TryGetValue(entry, out var box))
                box.Value = false;
        }

        public static bool IsProcSourced(PropertiesEnchantmentRegistry entry)
        {
            return entry != null && marks.TryGetValue(entry, out var box) && box.Value;
        }
    }
}
