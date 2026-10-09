using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Models;

namespace ACE.Entity
{
    public class ObjDesc
    {
        public uint PaletteID { get; set; }
        public List<PropertiesPalette> SubPalettes { get; set; } = new List<PropertiesPalette>();
        public List<PropertiesTextureMap> TextureChanges { get; set; } = new List<PropertiesTextureMap>();
        public List<PropertiesAnimPart> AnimPartChanges { get; set; } = new List<PropertiesAnimPart>();

        /// <summary>
        /// Helper function to ensure we don't add redundant parts to the list
        /// </summary>
        public void AddTextureChange(PropertiesTextureMap tm)
        {
            var e = TextureChanges.FirstOrDefault(c => c.PartIndex == tm.PartIndex && c.OldTexture == tm.OldTexture && c.NewTexture == tm.NewTexture);
            if (e == null)
                TextureChanges.Add(tm);
        }

        /// <summary>
        /// Merges a texture swap that was authored on an object's OWN biota (weenie/instance
        /// texture_map rows) into an ObjDesc that has already been composed from something else -
        /// in practice a wearer's ObjDesc, built from the ClothingTable of every equipped piece.
        ///
        /// The one case a plain Add gets wrong is a CHAIN: the ClothingTable already swapped
        /// texture A to B on this part, and the item's own row swaps B to C. Two independent
        /// entries would leave the result depending on the client applying them in order, so the
        /// chain is collapsed here instead - the existing A -> B entry becomes A -> C.
        ///
        /// A row that chains onto nothing is added as-is. If its OldTexture is not present on that
        /// part of the wearer it is simply a no-op client-side, which is cheaper than trying to
        /// work out server-side which textures a given body part actually carries.
        ///
        /// <paramref name="startIndex"/> bounds the chain search to entries the caller contributed.
        /// A wearer's ObjDesc holds the entries of EVERY equipped piece, and two pieces can easily
        /// touch the same part - so without this bound an item's row could splice itself onto an
        /// unrelated earlier item's swap that happens to end at the same texture, silently changing
        /// what that other piece renders. Callers pass the count taken before their own entries went
        /// in; the default of 0 searches everything, which is right when there is only one source.
        /// </summary>
        public void MergeItemTextureChange(PropertiesTextureMap tm, int startIndex = 0)
        {
            var chained = false;

            if (startIndex < 0)
                startIndex = 0;

            for (var i = startIndex; i < TextureChanges.Count; i++)
            {
                var existing = TextureChanges[i];

                if (existing.PartIndex != tm.PartIndex || existing.NewTexture != tm.OldTexture)
                    continue;

                // Replace rather than mutate: the entry may be a row instance owned by a biota.
                TextureChanges[i] = new PropertiesTextureMap
                {
                    PartIndex = existing.PartIndex,
                    OldTexture = existing.OldTexture,
                    NewTexture = tm.NewTexture,
                };

                chained = true;
            }

            if (!chained)
                AddTextureChange(tm);
        }

        /// <summary>
        /// Helper function to ensure we only have one AnimationPartChange.PartId in the list
        /// </summary>
        public void AddAnimPartChange(PropertiesAnimPart ap)
        {
            var p = AnimPartChanges.FirstOrDefault(c => c.Index == ap.Index && c.AnimationId == ap.AnimationId);
            if (p != null)
                AnimPartChanges.Remove(p);
            AnimPartChanges.Add(ap);
        }
    }
}
