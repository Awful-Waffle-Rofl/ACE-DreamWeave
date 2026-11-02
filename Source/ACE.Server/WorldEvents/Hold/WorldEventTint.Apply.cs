using ACE.Entity.Models;

namespace ACE.Server.WorldEvents
{
    public static partial class WorldEventTint
    {
        /// <summary>
        /// Lays <paramref name="palette"/> over the whole of <paramref name="objDesc"/>'s palette (pure): one sub-palette, offset
        /// 0 and length 0 (the client reads length 0 as all 2048 colors), added last so it wins over the creature's own colors.
        /// An ObjDesc with no base palette gets the tint as its base too. Nothing for palette 0 or a null ObjDesc.
        /// Ported from the contributor's WorldEventTint.Apply (DreamWeave #17).
        /// </summary>
        public static void Apply(ACE.Entity.ObjDesc objDesc, uint palette)
        {
            if (objDesc == null || palette == 0)
                return;

            objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = palette, Offset = 0, Length = 0 });

            if (objDesc.PaletteID == 0)
                objDesc.PaletteID = palette;
        }
    }
}
