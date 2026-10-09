using System;

namespace ACE.Database
{
    /// <summary>
    /// How the bulk weenie loader's switches reach the database layer. ACE.Database cannot see ACE.Server's
    /// PropertyManager, so ACE.Server installs READ-THROUGH providers here once at startup
    /// (ACE.Server.Managers.WeeniePrefetch.InstallSettings) and every read calls them.
    /// <para />
    /// Providers rather than a pushed static bool (the MissAttributionEnabled shape): PropertyManager has no change
    /// event and several write paths - LoadDefaultProperties, LoadPropertiesFromDB writing its cache directly,
    /// ModifyBool from /modifybool and the web admin panel, and the periodic resync - so a copied bool would need a
    /// hook on every one of them and would go stale on whichever was missed. A provider cannot go stale: it reads the
    /// PropertyManager cache (one ConcurrentDictionary lookup) at the moment of use, and the per-id miss path it sits
    /// on is about to run 17-21 world-DB queries anyway.
    /// <para />
    /// With no provider installed (unit tests, ACE.Content.Tools, the LoadTest harness, and the server's own boot
    /// before PropertyManager starts - DatabaseManager.Initialize's "human" read) the shipped defaults apply:
    /// enabled, self-check sample 50.
    /// </summary>
    public static class WeenieBulkLoadSettings
    {
        /// <summary>Shipped default of weenie_bulk_load.</summary>
        public const bool DefaultEnabled = true;

        /// <summary>Shipped default of weenie_bulk_selfcheck_sample (weenies compared per bulk chunk).</summary>
        public const long DefaultSelfCheckSample = 50;

        private static volatile Func<bool> enabledProvider;

        private static volatile Func<long> selfCheckSampleProvider;

        /// <summary>
        /// Installs the live source of weenie_bulk_load. Null restores the shipped default.
        /// </summary>
        public static Func<bool> EnabledProvider
        {
            get => enabledProvider;
            set => enabledProvider = value;
        }

        /// <summary>
        /// Installs the live source of weenie_bulk_selfcheck_sample. Null restores the shipped default.
        /// </summary>
        public static Func<long> SelfCheckSampleProvider
        {
            get => selfCheckSampleProvider;
            set => selfCheckSampleProvider = value;
        }

        /// <summary>
        /// weenie_bulk_load. False routes the per-id miss path back to the legacy per-table reader
        /// (WorldDatabase.GetWeenieLegacy) and turns every bulk prefetch into a no-op, so callers fall through to
        /// their unchanged per-id loops: exactly the behaviour before the bulk loader existed.
        /// A provider that throws reads as FALSE - the legacy path is the safe side of this switch.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                var provider = enabledProvider;

                if (provider == null)
                    return DefaultEnabled;

                try
                {
                    return provider();
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// weenie_bulk_selfcheck_sample: how many weenies of each bulk chunk are compared against a legacy per-id
        /// read before the chunk is published. 0 or less disables the check; a value at or above the chunk size
        /// checks every weenie in it. A provider that throws reads as the shipped default.
        /// </summary>
        public static long SelfCheckSample
        {
            get
            {
                var provider = selfCheckSampleProvider;

                if (provider == null)
                    return DefaultSelfCheckSample;

                try
                {
                    return provider();
                }
                catch
                {
                    return DefaultSelfCheckSample;
                }
            }
        }
    }
}
