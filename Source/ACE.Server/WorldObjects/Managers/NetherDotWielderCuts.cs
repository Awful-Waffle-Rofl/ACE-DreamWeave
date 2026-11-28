using System.Runtime.CompilerServices;

using ACE.Entity.Models;

namespace ACE.Server.WorldObjects.Managers
{
    /// <summary>
    /// FORK: in-memory record of the Summoner wielder cut (SummonerFocus.WielderDamageMod) baked into a void curse
    /// (nether DoT) registry entry's StatModValue when it landed. The cut stays in StatModValue, so the curse's DAMAGE
    /// is cut and cannot be escaped by swapping weapons; GetNetherDotDamageRating divides it back out, so the curse's
    /// damage-resistance DEBUFF is the uncut curse's (owner ruling 2026-11-27).
    ///
    /// Keyed by entry REFERENCE, like ProcDotMarks: a refresh mutates the same entry in place and keeps its original
    /// StatModValue, so the record keeps describing it; a new layer is a new entry with its own record. Set only where
    /// the entry is built (EnchantmentManager.BuildEntry) and copied wherever another entry's StatModValue is copied
    /// over (Nether Bloom). Never persisted: an unrecorded entry (including one reloaded from the database) reads 1.0,
    /// i.e. its debuff is sized from StatModValue as it was before this record existed. Weak keys, so a removed
    /// entry's record is collected with it.
    /// </summary>
    public static class NetherDotWielderCuts
    {
        private static readonly ConditionalWeakTable<PropertiesEnchantmentRegistry, StrongBox<float>> cuts = new();

        /// <summary>Records the cut baked into the entry. A cut of 1.0 (no cut) clears any record instead of allocating one.</summary>
        public static void Set(PropertiesEnchantmentRegistry entry, float cut)
        {
            if (entry == null)
                return;

            if (cut != 1.0f)
                cuts.GetValue(entry, _ => new StrongBox<float>()).Value = cut;
            else if (cuts.TryGetValue(entry, out var box))
                box.Value = 1.0f;
        }

        /// <summary>The cut baked into the entry's StatModValue: 1.0 when none was recorded.</summary>
        public static float Get(PropertiesEnchantmentRegistry entry)
        {
            return entry != null && cuts.TryGetValue(entry, out var box) ? box.Value : 1.0f;
        }

        /// <summary>Gives <paramref name="to"/> the record of <paramref name="from"/>, for a site that copies from's StatModValue onto to.</summary>
        public static void Copy(PropertiesEnchantmentRegistry from, PropertiesEnchantmentRegistry to)
        {
            Set(to, Get(from));
        }
    }
}
