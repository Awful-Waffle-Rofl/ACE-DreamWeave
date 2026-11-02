using System;
using System.Collections.Concurrent;

using log4net;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// World weenies read as look sources (<see cref="PvpLookSource"/>), cached. The weenie-facing piece of the team hue `@wcid` swap
    /// (Docs/Pvp/TEMPLATES.md "Team hue"): the live player gateway and the /pvphue audit, preview and look command all read through
    /// it, so they see the same look.
    ///
    /// The world database layer already caches a weenie (a missing one as null too, WorldDatabaseWithEntityCache.GetCachedWeenie), so
    /// an unknown wcid costs one read per process and a dispatch never reads the database per match. This class therefore asks the
    /// reader on EVERY call and only memoizes the extraction: the look is kept per wcid with the weenie instance it was built from
    /// and rebuilt when the reader hands back a different instance, so a live re-import (ClearCachedWeenie, then a fresh
    /// GetCachedWeenie) is seen. A null is never cached; only a read that throws is retried (it simply returns null this time).
    /// </summary>
    internal static class PvpWorldLooks
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpWorldLooks));

        private sealed class Entry
        {
            public Weenie Source;
            public PvpLookSource Look;
        }

        private static readonly ConcurrentDictionary<uint, Entry> cache = new ConcurrentDictionary<uint, Entry>();

        /// <summary>Reads one world weenie, or null when there is none. Test seam: production reads the cached world weenie. A throw is treated like null.</summary>
        internal static Func<uint, Weenie> WeenieReader = wcid => DatabaseManager.World.GetCachedWeenie(wcid);

        /// <summary>Forgets every memoized look. Test use.</summary>
        internal static void ClearCache() => cache.Clear();

        /// <summary>The look source for <paramref name="wcid"/>, or null when there is no such world weenie or it could not be read.</summary>
        public static PvpLookSource Get(uint wcid)
        {
            try
            {
                var weenie = WeenieReader(wcid);

                if (weenie == null)
                {
                    cache.TryRemove(wcid, out _);
                    return null;
                }

                if (cache.TryGetValue(wcid, out var cached) && ReferenceEquals(cached.Source, weenie))
                    return cached.Look;

                var source = FromWeenie(wcid, weenie);
                cache[wcid] = new Entry { Source = weenie, Look = source };
                return source;
            }
            catch (Exception ex)
            {
                log.Warn($"[PVPTEMPLATE] team hue: world weenie {wcid} could not be read as a look source (will retry): {ex.Message}");
                return null;
            }
        }

        /// <summary>The look-bearing fields, slot coverage and raw-row flags of a weenie. Shared with /pvphue look.</summary>
        internal static PvpLookSource FromWeenie(uint wcid, Weenie weenie) => new PvpLookSource
        {
            Wcid = wcid,
            Name = weenie.GetProperty(PropertyString.Name) ?? weenie.ClassName,
            Look = new LookFields
            {
                Setup = weenie.GetProperty(PropertyDataId.Setup),
                ClothingBase = weenie.GetProperty(PropertyDataId.ClothingBase),
                PaletteBase = weenie.GetProperty(PropertyDataId.PaletteBase),
                Icon = weenie.GetProperty(PropertyDataId.Icon),
                PaletteTemplate = weenie.GetProperty(PropertyInt.PaletteTemplate),
                Shade = weenie.GetProperty(PropertyFloat.Shade),
                Shade2 = weenie.GetProperty(PropertyFloat.Shade2),
                Shade3 = weenie.GetProperty(PropertyFloat.Shade3),
                Shade4 = weenie.GetProperty(PropertyFloat.Shade4),
                IgnoreCloIcons = weenie.GetProperty(PropertyBool.IgnoreCloIcons),
            },
            ValidLocations = weenie.GetProperty(PropertyInt.ValidLocations) ?? 0,
            HasPaletteRows = weenie.PropertiesPalette != null && weenie.PropertiesPalette.Count > 0,
            HasTextureMapRows = weenie.PropertiesTextureMap != null && weenie.PropertiesTextureMap.Count > 0,
            HasAnimPartRows = weenie.PropertiesAnimPart != null && weenie.PropertiesAnimPart.Count > 0,
        };
    }
}
