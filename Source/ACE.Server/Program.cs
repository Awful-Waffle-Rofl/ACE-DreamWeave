using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

using log4net;
using log4net.Config;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.DatLoader;
using ACE.Database.Models.Auth;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network.Managers;
using ACE.Server.WorldEvents;

namespace ACE.Server
{
    partial class Program
    {
        /// <summary>
        /// The timeBeginPeriod function sets the minimum timer resolution for an application or device driver. Used to manipulate the timer frequency.
        /// https://docs.microsoft.com/en-us/windows/desktop/api/timeapi/nf-timeapi-timebeginperiod
        /// Important note: This function affects a global Windows setting. Windows uses the lowest value (that is, highest resolution) requested by any process.
        /// </summary>
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint MM_BeginPeriod(uint uMilliseconds);

        /// <summary>
        /// The timeEndPeriod function clears a previously set minimum timer resolution
        /// https://docs.microsoft.com/en-us/windows/desktop/api/timeapi/nf-timeapi-timeendperiod
        /// </summary>
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint MM_EndPeriod(uint uMilliseconds);

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static readonly bool IsRunningInContainer = Convert.ToBoolean(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"));

        public static void Main(string[] args)
        {
            var consoleTitle = $"ACEmulator - v{ServerBuildInfo.FullVersion}";

            Console.Title = consoleTitle;

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);

            // In a container, `docker stop` delivers SIGTERM, whose default action is to terminate
            // the process immediately - which skips ProcessExit and ACE's shard-saving shutdown, so
            // any writes since the last periodic save are lost. Intercept SIGTERM (and SIGINT, for
            // an attached console) and route it to the normal graceful shutdown: log everyone off,
            // unload landblocks, and drain the shard DB queue before exiting. `docker stop -t
            // <seconds>` must allow enough time for that to finish before SIGKILL.
            if (IsRunningInContainer)
            {
                sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleContainerSignal);
                sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleContainerSignal);

                // SIGHUP is repurposed as a live "reload content caches" trigger, so a content
                // deploy (deploy-content-prod.yml) can pick up weenie/realm changes without a
                // restart. A detached container is never sent SIGHUP by a terminal hangup, so
                // hijacking its default (terminate) action is safe.
                sigHupRegistration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, HandleReloadSignal);
            }

            // Typically, you wouldn't force the current culture on an entire application unless you know sure your application is used in a specific region (which ACE is not)
            // We do this because almost all of the client/user input/output code does not take culture into account, and assumes en-US formatting.
            // Without this, many commands that require special characters like , and . will break
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            // Init our text encoding options. This will allow us to use more than standard ANSI text, which the client also supports.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // Look for the log4net.config first in the current environment directory, then in the ExecutingAssembly location
            var exeLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Resolves to /ace/Config in a container (today's behavior, unchanged), or to
            // ACE_CONFIG_DIR if that environment variable is set (even inside a container), or
            // to null outside a container with no ACE_CONFIG_DIR - meaning "no external
            // directory, use the exe directory" exactly as before this mechanism existed.
            var externalConfigDirectory = ExternalConfigDirectory.Resolve(IsRunningInContainer);

            if (externalConfigDirectory != null && !Directory.Exists(externalConfigDirectory))
                Directory.CreateDirectory(externalConfigDirectory);

            var log4netConfig = Path.Combine(exeLocation, "log4net.config");
            var log4netConfigExample = Path.Combine(exeLocation, "log4net.config.example");
            var log4netConfigExternal = externalConfigDirectory != null ? Path.Combine(externalConfigDirectory, "log4net.config") : null;

            if (log4netConfigExternal != null && File.Exists(log4netConfigExternal))
                File.Copy(log4netConfigExternal, log4netConfig, true);

            var log4netFileInfo = new FileInfo("log4net.config");
            if (!log4netFileInfo.Exists)
                log4netFileInfo = new FileInfo(log4netConfig);

            if (!log4netFileInfo.Exists)
            {
                var exampleFile = new FileInfo(log4netConfigExample);
                if (!exampleFile.Exists)
                {
                    Console.WriteLine("log4net Configuration file is missing.  Please copy the file log4net.config.example to log4net.config and edit it to match your needs before running ACE.");
                    throw new Exception("missing log4net configuration file");
                }
                else
                {
                    if (externalConfigDirectory == null)
                    {
                        Console.WriteLine("log4net Configuration file is missing,  cloning from example file.");
                        File.Copy(log4netConfigExample, log4netConfig);
                    }
                    else
                    {
                        if (!File.Exists(log4netConfigExternal))
                        {
                            Console.WriteLine($"log4net Configuration file is missing, an external config directory is in use ({externalConfigDirectory}), cloning from docker file.");
                            var log4netConfigDocker = Path.Combine(exeLocation, "log4net.config.docker");
                            File.Copy(log4netConfigDocker, log4netConfig);
                            File.Copy(log4netConfigDocker, log4netConfigExternal);
                        }
                        else
                        {
                            File.Copy(log4netConfigExternal, log4netConfig);
                        }

                    }
                }
            }

            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            XmlConfigurator.ConfigureAndWatch(logRepository, log4netFileInfo);

            if (Environment.ProcessorCount < 2)
                log.Warn("Only one vCPU was detected. ACE may run with limited performance. You should increase your vCPU count for anything more than a single player server.");

            // Do system specific initializations here
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // On many windows systems, the default resolution for Thread.Sleep is 15.6ms. This allows us to command a tighter resolution
                    MM_BeginPeriod(1);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
            }

            log.Info("Starting ACEmulator...");

            if (IsRunningInContainer)
                log.Info("ACEmulator is running in a container...");

            // Named here, once, so it is obvious from the log which Config.js/log4net.config the
            // server actually loaded - a silently-wrong config file is the failure mode this
            // mechanism is designed to prevent.
            if (externalConfigDirectory != null)
                log.Info($"Using external config directory: {externalConfigDirectory}");

            var configFile = Path.Combine(exeLocation, "Config.js");
            var configFileExternal = externalConfigDirectory != null ? Path.Combine(externalConfigDirectory, "Config.js") : null;

            // Once an external config directory is named it is AUTHORITATIVE. If it has no Config.js
            // yet, seed it - by adopting the exe directory's existing copy if there is one, otherwise
            // by running out-of-box setup into it - and only then copy it in.
            //
            // Getting this wrong is not a cosmetic bug. The first version of this gated the whole
            // block on the EXE-directory file being absent, so pointing ACE_CONFIG_DIR at an empty
            // directory left the server running the exe directory's stale Config.js while the log
            // above cheerfully announced the external directory. That is exactly the silently-wrong
            // config this mechanism exists to prevent, and a boot test caught it (2026-07-29).
            if (configFileExternal != null)
            {
                if (!File.Exists(configFileExternal))
                {
                    if (File.Exists(configFile))
                    {
                        log.Info($"External config directory has no Config.js yet - adopting the existing {configFile} into it.");
                        File.Copy(configFile, configFileExternal);
                    }
                    else
                    {
                        DoOutOfBoxSetup(configFile);
                        File.Copy(configFile, configFileExternal);
                    }
                }

                File.Copy(configFileExternal, configFile, true);
            }
            else if (!File.Exists(configFile))
            {
                DoOutOfBoxSetup(configFile);
            }

            log.Info("Initializing ConfigManager...");
            ConfigManager.Initialize();

            log.Info("Initializing ModManager...");
            ModManager.Initialize();

            if (ConfigManager.Config.Server.WorldName != "ACEmulator")
            {
                consoleTitle = $"{ConfigManager.Config.Server.WorldName} | {consoleTitle}";
                Console.Title = consoleTitle;
            }

            if (ConfigManager.Config.Offline.PurgeDeletedCharacters)
            {
                log.Info($"Purging deleted characters, and their possessions, older than {ConfigManager.Config.Offline.PurgeDeletedCharactersDays} days ({DateTime.Now.AddDays(-ConfigManager.Config.Offline.PurgeDeletedCharactersDays).ToCommonString()})...");
                ShardDatabaseOfflineTools.PurgeCharactersInParallel(ConfigManager.Config.Offline.PurgeDeletedCharactersDays, out var charactersPurged, out var playerBiotasPurged, out var possessionsPurged);
                log.Info($"Purged {charactersPurged:N0} characters, {playerBiotasPurged:N0} player biotas and {possessionsPurged:N0} possessions.");
            }

            if (ConfigManager.Config.Offline.PurgeOrphanedBiotas)
            {
                log.Info($"Purging orphaned biotas...");
                ShardDatabaseOfflineTools.PurgeOrphanedBiotasInParallel(out var numberOfBiotasPurged);
                log.Info($"Purged {numberOfBiotasPurged:N0} biotas.");
            }

            if (ConfigManager.Config.Offline.PruneDeletedCharactersFromFriendLists)
            {
                log.Info($"Pruning invalid friends from all friend lists...");
                ShardDatabaseOfflineTools.PruneDeletedCharactersFromFriendLists(out var numberOfFriendsPruned);
                log.Info($"Pruned {numberOfFriendsPruned:N0} invalid friends found on friend lists.");
            }

            if (ConfigManager.Config.Offline.PruneDeletedObjectsFromShortcutBars)
            {
                log.Info($"Pruning invalid shortcuts from all shortcut bars...");
                ShardDatabaseOfflineTools.PruneDeletedObjectsFromShortcutBars(out var numberOfShortcutsPruned);
                log.Info($"Pruned {numberOfShortcutsPruned:N0} deleted objects found on shortcut bars.");
            }

            if (ConfigManager.Config.Offline.PruneDeletedCharactersFromSquelchLists)
            {
                log.Info($"Pruning invalid squelches from all squelch lists...");
                ShardDatabaseOfflineTools.PruneDeletedCharactersFromSquelchLists(out var numberOfSquelchesPruned);
                log.Info($"Pruned {numberOfSquelchesPruned:N0} invalid squelched characters found on squelch lists.");
            }

            if (ConfigManager.Config.Offline.AutoServerUpdateCheck)
                CheckForServerUpdate();
            else
                log.Info($"AutoServerVersionCheck is disabled...");

            if (ConfigManager.Config.Offline.AutoUpdateWorldDatabase)
            {
                CheckForWorldDatabaseUpdate();

                if (ConfigManager.Config.Offline.AutoApplyWorldCustomizations)
                    AutoApplyWorldCustomizations();
            }
            else
                log.Info($"AutoUpdateWorldDatabase is disabled...");

            if (ConfigManager.Config.Offline.AutoApplyDatabaseUpdates)
                AutoApplyDatabaseUpdates();
            else
                log.Info($"AutoApplyDatabaseUpdates is disabled...");

            // This should only be enabled manually. To enable it, simply uncomment this line
            //ACE.Database.OfflineTools.Shard.BiotaGuidConsolidator.ConsolidateBiotaGuids(0xA0000000, true, false, out int numberOfBiotasConsolidated, out int numberOfBiotasSkipped, out int numberOfErrors);
            //ACE.Database.OfflineTools.Shard.BiotaGuidConsolidator.ConsolidateBiotaGuids(0xD0000000, false, true, out int numberOfBiotasConsolidated2, out int numberOfBiotasSkipped2, out int numberOfErrors2);

            ShardDatabaseOfflineTools.CheckForBiotaPropertiesPaletteOrderColumnInShard();

            // pre-load starterGear.json, abort startup if file is not found as it is required to create new characters.
            if (Factories.StarterGearFactory.GetStarterGearConfiguration() == null)
            {
                log.Fatal("Unable to load or parse starterGear.json. ACEmulator will now abort startup.");
                ServerManager.StartupAbort();
                Environment.Exit(0);
            }

            // The vault collapse test destroys an item and replaces it with a ledger row, so it must
            // refuse to run at all if someone has added a collection to Biota that it does not
            // compare. Checking that here means the failure stops the boot; left to first use it
            // would instead throw out of the first player deposit after a deploy.
            try
            {
                Entity.AccountVault.VaultCollapse.AssertBiotaCoverage();
            }
            catch (Exception ex)
            {
                log.Fatal($"VaultCollapse coverage check failed. ACEmulator will now abort startup. {ex.Message}");
                ServerManager.StartupAbort();
                Environment.Exit(0);
            }

            log.Info("Initializing ServerManager...");
            ServerManager.Initialize();

            log.Info("Initializing DatManager...");
            DatManager.Initialize(ConfigManager.Config.Server.DatFilesDirectory, true);

            // hand the game layer its formula/spell tables (unit tests inject synthetic tables here instead)
            Entity.GameTables.Initialize(DatManager.PortalDat.SkillTable, DatManager.PortalDat.SecondaryAttributeTable);
            Entity.SpellSet.Initialize(DatManager.PortalDat.SpellTable);

            if (ConfigManager.Config.DDD.EnableDATPatching)
            {
                log.Info("Initializing DDDManager...");
                DDDManager.Initialize();
            }
            else
                log.Info("DAT Patching Disabled...");

            log.Info("Initializing DatabaseManager...");
            DatabaseManager.Initialize();

            if (DatabaseManager.InitializationFailure)
            {
                log.Fatal("DatabaseManager initialization failed. ACEmulator will now abort startup.");
                ServerManager.StartupAbort();
                Environment.Exit(0);
            }

            log.Info("Starting DatabaseManager...");
            DatabaseManager.Start();

            log.Info("Starting PropertyManager...");
            PropertyManager.Initialize();

            log.Info("Initializing GuidManager...");
            GuidManager.Initialize();

            // Must run BEFORE anything can activate a landblock, which is why it sits here rather than
            // beside the other managers below. It reads account_vault once and seeds the filter that
            // keeps vault container biotas out of the world (Docs/MuleVendor/DESIGN.md 7.2, risk R1);
            // a landblock that activated ahead of it would spawn every account's vault as a lootable
            // backpack. It never throws and never aborts startup - see AccountVaultManager.Initialize
            // for what a failed read does instead.
            log.Info("Initializing AccountVaultManager...");
            AccountVaultManager.Initialize();

            // Barrel retention (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md 6.3). Started here rather
            // than beside the market, even though the barrel's only player-facing door is the market
            // API today: the barrel belongs to the vault, /vaultrestore reaches it with the market
            // switched off, and an item whose retention window has expired must be destroyed either
            // way. It logs the window in days at startup so an operator can see the setting without
            // querying the shard.
            log.Info("Starting the account vault barrel retention sweep...");
            AccountVaultBarrelReaper.Start();

            if (ConfigManager.Config.Server.ServerPerformanceMonitorAutoStart)
            {
                log.Info("Server Performance Monitor auto starting...");
                ServerPerformanceMonitor.Start();
            }

            if (ConfigManager.Config.Server.WorldDatabasePrecaching)
            {
                log.Info("Precaching Weenies...");
                DatabaseManager.World.CacheAllWeenies();
                log.Info("Precaching Cookbooks...");
                DatabaseManager.World.CacheAllCookbooks();
                log.Info("Precaching Events...");
                DatabaseManager.World.GetAllEvents();
                log.Info("Precaching House Portals...");
                DatabaseManager.World.CacheAllHousePortals();
                log.Info("Precaching Points Of Interest...");
                DatabaseManager.World.CacheAllPointsOfInterest();
                log.Info("Precaching Realm Landblock Rules...");
                DatabaseManager.World.CacheAllRealmLandblockRules();
                log.Info("Precaching Spells...");
                DatabaseManager.World.CacheAllSpells();
                log.Info("Precaching Treasures - Death...");
                DatabaseManager.World.CacheAllTreasuresDeath();
                log.Info("Precaching Treasures - Material Base...");
                DatabaseManager.World.CacheAllTreasureMaterialBase();
                log.Info("Precaching Treasures - Material Groups...");
                DatabaseManager.World.CacheAllTreasureMaterialGroups();
                log.Info("Precaching Treasures - Material Colors...");
                DatabaseManager.World.CacheAllTreasureMaterialColor();
                log.Info("Precaching Treasures - Wielded...");
                DatabaseManager.World.CacheAllTreasureWielded();
                log.Info("Precaching Rare Gem Spells...");
                DatabaseManager.World.GetRareGemSpellIds();
            }
            else
                log.Info("Precaching World Database Disabled...");

            log.Info("Initializing RealmManager...");
            RealmManager.Initialize();

            log.Info("Initializing SpeedSeasonManager...");
            SpeedSeasonManager.Initialize();

            log.Info("Initializing SpeedBoardManager...");
            SpeedBoardManager.Initialize();

            log.Info("Initializing PlayerManager...");
            PlayerManager.Initialize();

            log.Info("Initializing HouseManager...");
            HouseManager.Initialize();

            log.Info("Initializing InboundMessageManager...");
            InboundMessageManager.Initialize();

            log.Info("Initializing SocketManager...");
            SocketManager.Initialize();

            log.Info("Initializing WorldManager...");
            WorldManager.Initialize();

            // Started here on purpose: after WorldManager.Initialize so there is a world thread to watch,
            // and before ServerMetrics.Initialize so the ace.world.* gauges have a published state to read
            // from their very first scrape. The watchdog must also be running DURING
            // LandblockManager.PreloadConfigLandblocks, which is where the world thread spends its first
            // minutes and where it can also die - the "starting" heartbeat is what tells an external
            // healthcheck the difference between a slow start and a hung process.
            if (ConfigManager.Config.Server.WorldWatchdogEnabled)
            {
                log.Info("Initializing WorldWatchdog...");
                WorldWatchdog.Initialize();
            }
            else
                log.Warn("WorldWatchdog is DISABLED by configuration (WorldWatchdogEnabled=false). A dead or hung world thread will not be detected, reported by ace.world.status, or written to the heartbeat file.");

            log.Info("Initializing ServerMetrics...");
            ServerMetrics.Initialize();

            log.Info("Initializing AnalyticsManager...");
            ACE.Server.Managers.Analytics.AnalyticsManager.Initialize();

            log.Info("Initializing EventManager...");
            EventManager.Initialize();

            log.Info("Initializing WorldEventManager...");
            WorldEventManager.Initialize();

            log.Info("Initializing ThreadDungeonManager...");
            ACE.Server.ThreadDungeons.ThreadDungeonManager.Initialize();

            // Free up memory before the server goes online. This can free up 6 GB+ on larger servers.
            log.Info("Forcing .net garbage collection...");
            for (int i = 0; i < 10; i++)
            {
                // https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals
                // https://learn.microsoft.com/en-us/dotnet/api/system.runtime.gcsettings.largeobjectheapcompactionmode
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

                GC.Collect();
            }

            // This should be last
            log.Info("Initializing CommandManager...");
            CommandManager.Initialize();

            //Register mod commands
            log.Info("Registering ModManager commands...");
            ModManager.RegisterCommands();
            ModManager.ListMods();

            // The Market (Docs/Market/DESIGN.md 4.1). Last, deliberately: it needs DatabaseManager,
            // PropertyManager, PlayerManager and AccountVaultManager already up. Config-gated and OFF
            // by default; with Market.Enabled false nothing below runs and no listener is bound.
            if (ConfigManager.Config.Market != null && ConfigManager.Config.Market.Enabled)
            {
                log.Info("Initializing MarketManager...");
                ACE.Server.Managers.Market.MarketManager.Initialize();

                // Boot recovery BEFORE the API opens, so no new purchase can interleave with the
                // examination of the interrupted ones (DESIGN 5.4, "Restart mid-purchase").
                ACE.Server.Managers.Market.MarketManager.RecoverPendingTransactions(
                    ConfigManager.Config.Market.RequestTimeoutMs);

                // Orders recover, reconcile and expire BEFORE the API opens, for the same reason as the
                // line above. Reconciliation runs AFTER the two recovery passes on purpose: it reads
                // Completed fill rows, and RecoverPendingTransactions is what settles the interrupted
                // ones first (WANTED-DESIGN 6.5).
                ACE.Server.Managers.Market.MarketManager.RecoverPendingOrders(ConfigManager.Config.Market.RequestTimeoutMs);
                ACE.Server.Managers.Market.MarketManager.ReconcileFilledOrders();
                ACE.Server.Managers.Market.MarketManager.ExpireOrders(DateTime.UtcNow);
                MarketBuyOrderExpiry.Start();
                MarketAdvertiserJob.Start();

                log.Info("Starting MarketApiHost...");
                ACE.Server.Managers.Market.MarketApiHost.Start(BuildMarketApiOptions());
            }

            if (!PropertyManager.GetBool("world_closed", false).Item)
            {
                WorldManager.Open(null);
            }
        }

        /// <summary>
        /// Config.js plus the two environment overrides deployment needs. ConfigManager has no
        /// environment layer of its own, and Config.js is gitignored and hand-managed per host, so
        /// without ACE_MARKET_SHARED_KEY the secret would have to be edited in on every deploy.
        /// </summary>
        private static ACE.Server.Managers.Market.MarketApiOptions BuildMarketApiOptions()
        {
            var config = ConfigManager.Config.Market;

            var key = Environment.GetEnvironmentVariable("ACE_MARKET_SHARED_KEY");
            var url = Environment.GetEnvironmentVariable("ACE_MARKET_LISTEN_URL");

            return new ACE.Server.Managers.Market.MarketApiOptions
            {
                ListenUrl = string.IsNullOrWhiteSpace(url) ? config.ListenUrl : url,
                SharedKey = string.IsNullOrWhiteSpace(key) ? config.SharedKey : key,
                MaxInFlight = config.MaxInFlight,
                WriteRatePerMinute = config.WriteRatePerMinute,
                RequestTimeoutMs = config.RequestTimeoutMs,
                LoginRatePerMinutePerAccount = config.LoginRatePerMinutePerAccount,
                LoginRatePerMinutePerIp = config.LoginRatePerMinutePerIp,
                TrustedForwardedForSource = config.TrustedForwardedForSource,
                SessionLifetimeMinutes = config.SessionLifetimeMinutes,
                Wallet = new ACE.Server.Managers.Market.BankMarketWallet(),

                // Delegates rather than direct calls, so MarketApiHost needs neither DatabaseManager
                // nor a live Player and stays testable.
                AuthenticateAccount = (name, password) =>
                {
                    var account = DatabaseManager.Authentication.GetAccountByName(name);

                    // An unknown account and a wrong password must be indistinguishable, or the
                    // endpoint enumerates accounts.
                    return account != null && account.PasswordMatches(password) ? account.AccountId : 0u;
                },

                ListCharacters = accountId => PlayerManager.GetAccountPlayersSnapshot(accountId)
                    .Where(p => !p.IsDeleted && !p.IsPendingDeletion)
                    .Select(p => (p.Guid.Full, p.Name))
                    .ToList(),
            };
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            log.Error(e.ExceptionObject);
        }

        // Kept in static fields so the registrations are not garbage-collected while active.
        private static PosixSignalRegistration sigTermRegistration;
        private static PosixSignalRegistration sigIntRegistration;
        private static PosixSignalRegistration sigHupRegistration;

        // Live cache reload on SIGHUP - the restart-free half of the content-deploy invalidation.
        // Clears the world content caches (weenie/spell/recipe/wielded/landblock-instance) and
        // re-registers realms, so a weenie or realm content apply takes effect without a restart.
        // NOTE: this does NOT respawn already-loaded landblocks - placement/landblock_instance
        // changes still need reload-landblock (an admin standing in the block) or a restart.
        private static void HandleReloadSignal(PosixSignalContext context)
        {
            context.Cancel = true;   // do not let SIGHUP terminate the process
            try
            {
                log.Info("SIGHUP received - reloading content caches (clearcache + reload-realms)...");
                ACE.Server.Command.Handlers.Processors.DeveloperContentCommands.HandleClearCache(null);
                var (added, updated, missing) = RealmManager.Reload();
                log.Info($"[SIGHUP] content caches cleared; realms reloaded (added {added}, updated {updated}, missing {missing.Count}).");
            }
            catch (Exception ex)
            {
                log.Error("[SIGHUP] content-cache reload failed", ex);
            }
        }

        // Guards the container graceful shutdown so the signal handler and OnProcessExit, which
        // both funnel into it, only start the warned countdown once. A signal arriving after the
        // countdown has already started is handled separately inside InitiateContainerShutdown -
        // it either forces an immediate shutdown (countdown still pending) or is ignored (final
        // shutdown already in progress) - see there for details.
        private static int containerShutdownStarted;

        private static void HandleContainerSignal(PosixSignalContext context)
        {
            // Cancel the default action (immediate termination) so the graceful shutdown can run.
            context.Cancel = true;
            InitiateContainerShutdown($"{context.Signal} received");
        }

        private static void InitiateContainerShutdown(string reason)
        {
            if (Interlocked.CompareExchange(ref containerShutdownStarted, 1, 0) != 0)
            {
                // A repeat signal while a countdown is already running. Once the countdown expires,
                // ServerManager.ShutdownInProgress flips true and the final shutdown (log off players,
                // unload landblocks, drain the shard DB queue, Environment.Exit) is already underway -
                // in that case treat the repeat signal as a no-op instead of re-entering DoShutdownNow.
                // Otherwise the operator is asking for force-now on a double signal, so skip the warned
                // countdown and shut down immediately.
                if (ServerManager.ShutdownInProgress)
                {
                    log.Warn($"{reason} - shutdown already in progress, ignoring repeat signal.");
                    return;
                }

                log.Warn($"{reason} - repeat signal received during warned countdown, forcing immediate shutdown...");
                ServerManager.DoShutdownNow();
                return;
            }

            // Triage BEFORE reading the online count, because with a dead world that count is a lie.
            // PlayerManager's online roster is only cleaned up by the world thread, so on 2026-09-01 the
            // SIGTERM handler read 34 sessions that had stopped existing hours earlier, took the warned
            // path on their behalf, and started a 300s countdown that then ran on the dead world thread.
            // The stop had to be force-killed. If the world is not running there is nobody to warn and
            // nothing that can service a countdown, so go straight to the world-independent fast path.
            //
            // Classify() and NOT WorldWatchdog.CurrentState: CurrentState is only ever moved by the
            // watchdog's poll thread, so reading it here would make this fix a subscriber to an optional
            // subsystem - setting WorldWatchdogEnabled to false would leave it frozen at Starting and
            // silently restore the exact pre-fix behaviour on the one path that must never regress.
            // Classify computes the same verdict on this thread from the world thread's own statics,
            // which are maintained unconditionally.
            var worldState = WorldWatchdog.Classify(out var staleSeconds);

            if (worldState == WorldWatchdog.WorldState.Dead || worldState == WorldWatchdog.WorldState.Stalled)
            {
                // The watchdog's published view is only worth quoting when it is actually running; when
                // it is not, say so, because otherwise a reader of this line has no way to tell whether
                // the absence of the usual watchdog FATAL line above means anything.
                var watchdogView = WorldWatchdog.WatchdogRunning
                    ? $"watchdog last published {WorldWatchdog.CurrentState.ToString().ToLowerInvariant()}"
                    : "watchdog not running, classified directly from the world thread's own state";

                log.Warn($"{reason} - world is {worldState.ToString().ToUpperInvariant()} (last world tick {(staleSeconds < 0 ? "never" : $"{staleSeconds}s ago")}; {watchdogView}). " +
                         $"Skipping the player warning countdown because the world is not running - any online count is stale - and shutting down via the fast path.");

                // Exit code 0: this is a requested, orderly stop that we are simply completing without
                // the world's help. The watchdog's own self-exit is the one that reports a fault (70).
                ServerManager.DoFastShutdown(reason, 0);
                return;
            }

            var onlineCount = PlayerManager.GetOnlineCount();
            var warningSeconds = ConfigManager.Config.Server.ContainerShutdownWarningSeconds;

            if (onlineCount == 0)
            {
                log.Warn($"{reason} - no players online, shutting down immediately...");
                ServerManager.DoShutdownNow();
                return;
            }

            if (warningSeconds == 0)
            {
                log.Warn($"{reason} - {onlineCount} player{(onlineCount > 1 ? "s" : "")} online, but ContainerShutdownWarningSeconds is 0 (warning disabled), shutting down immediately...");
                ServerManager.DoShutdownNow();
                return;
            }

            log.Warn($"{reason} - {onlineCount} player{(onlineCount > 1 ? "s" : "")} online, warning them for {warningSeconds}s before graceful shutdown...");

            // Reuses the existing countdown thread, which broadcasts ATTENTION/WARNING/"Please log out!"
            // notices at retail cadence via NotifyPlayersOfPendingShutdown and ends by calling
            // Environment.Exit (which re-enters OnProcessExit below).
            ServerManager.SetShutdownInterval(warningSeconds);
            ServerManager.BeginShutdown();
        }

        /// <summary>
        /// Entry point for WorldWatchdog's self-exit. Takes the same containerShutdownStarted latch a
        /// SIGTERM would, so a stop signal arriving at the same moment cannot start a second shutdown
        /// alongside this one, and returns quietly if that latch is already taken - if an operator's
        /// stop is already running, let it run.
        ///
        /// Exit code 70 (EX_SOFTWARE) marks this as a fault-driven exit rather than a requested stop,
        /// so it is distinguishable in container restart logs from the code 0 taken by
        /// InitiateContainerShutdown's dead-world triage.
        /// </summary>
        internal static void InitiateWatchdogShutdown(string reason)
        {
            if (Interlocked.CompareExchange(ref containerShutdownStarted, 1, 0) != 0)
                return;

            ServerManager.DoFastShutdown(reason, 70);
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            // First, and outside both branches: Shutdown clears AccountVaultStore.PreWithdrawHook,
            // so a stopping market never intercepts a withdraw during the drain below. Both are
            // no-ops when the module never started.
            ACE.Server.Managers.Market.MarketApiHost.Stop();
            MarketBuyOrderExpiry.Stop();
            MarketAdvertiserJob.Stop();
            ACE.Server.Managers.Market.MarketManager.Shutdown();

            // Also a no-op when it never started. Stopped before the database goes down below, so a
            // pass in flight is not cut off mid-write; a pass that does not finish simply leaves its
            // rows for the next process, because nothing is stamped until its item is destroyed.
            AccountVaultBarrelReaper.Stop();

            if (!IsRunningInContainer)
            {
                if (!ServerManager.ShutdownInitiated)
                    log.Warn("Unsafe server shutdown detected! Data loss is possible!");

                PropertyManager.StopUpdating();
                DatabaseManager.Stop();

                // Do system specific cleanup here
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        MM_EndPeriod(1);
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex.ToString());
                }
            }
            else
            {
                // A no-op if a SIGTERM/SIGINT handler already started the shutdown; otherwise this
                // covers a plain process exit that did not arrive via one of those signals.
                InitiateContainerShutdown("Process exit");
                DatabaseManager.Stop();
            }
        }
    }
}
