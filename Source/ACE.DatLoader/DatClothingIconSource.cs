using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;

namespace ACE.DatLoader
{
    /// <summary>
    /// Production IClothingIconSource: reads ClothingTable files from client_portal.dat, cached per
    /// instance. DatManager.Initialize must have run before any lookup.
    /// </summary>
    public class DatClothingIconSource : IClothingIconSource
    {
        private readonly Dictionary<uint, ClothingIconTable> cache = new Dictionary<uint, ClothingIconTable>();

        public ClothingIconTable TryGetTable(uint clothingBase)
        {
            ClothingIconTable cached;
            if (cache.TryGetValue(clothingBase, out cached))
                return cached;

            ClothingIconTable result = null;

            ClothingTable table = DatManager.PortalDat.ReadFromDat<ClothingTable>(clothingBase);
            if (table != null)
            {
                // ToList() on Keys preserves the dictionary's own enumeration order, which is what
                // the client's Keys.ElementAt(0) fallback depends on. Never sort these.
                var setupIds = table.ClothingBaseEffects.Keys.ToList();
                var templateKeys = table.ClothingSubPalEffects.Keys.ToList();

                var iconByTemplate = new Dictionary<uint, uint>();
                foreach (KeyValuePair<uint, CloSubPalEffect> entry in table.ClothingSubPalEffects)
                    iconByTemplate[entry.Key] = entry.Value.Icon;

                result = new ClothingIconTable(setupIds, templateKeys, iconByTemplate);
            }

            cache[clothingBase] = result;
            return result;
        }
    }
}
