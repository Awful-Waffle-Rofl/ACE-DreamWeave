using System.Collections.Generic;

using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.Server.Entity
{
    public static class SpellSet
    {
        // helper collection for spell sorting
        public static readonly HashSet<int> SetSpells = new HashSet<int>();

        /// <summary>
        /// Collects the equipment set spells from the spell table. Called once at boot
        /// (Program.cs, right after DatManager initializes); until then the set is empty
        /// and enchantment sorting treats nothing as a set spell.
        /// </summary>
        public static void Initialize(SpellTable spellTable)
        {
            SetSpells.Clear();

            foreach (var spellSet in spellTable.SpellSet.Values)
            {
                foreach (var tier in spellSet.SpellSetTiers.Values)
                {
                    foreach (var spell in tier.Spells)
                    {
                        // cutoff for enchantment manager bug fix sorting
                        if (spell >= (uint)SpellId.SetCoordination1)
                            SetSpells.Add((int)spell);
                    }
                }
            }
            /*Console.WriteLine($"Added {SetSpells.Count} set spells:");

            foreach (var setSpell in SetSpells.OrderBy(i => i))
                Console.WriteLine($"{setSpell} - {(SpellId)setSpell}");*/
        }
    }
}
