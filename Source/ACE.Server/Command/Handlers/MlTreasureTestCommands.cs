using System;
using System.Linq;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.MlDigsite;
using ACE.Server.MlTreasure;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /testtreasuremap (alias /ttm): an admin-only test tool for the full ML treasure map flow - map use
    /// (TreasureMapHandler), dig, and the digsite encounter it opens (MlDigsiteManager) - with the encounter
    /// type forced by a parameter instead of left to the weighted roll, and the dig site placed at the
    /// admin's own feet rather than rolled from the catalogue.
    ///
    /// Creates a real wcid 1004100 map (MlTreasureDrop.TreasureMapWcid), stamped so it behaves exactly like
    /// an ordinary map through TreasureMapHandler.TryHandleUse/RunDigStep/FinishDig - Far/Cardinal/Dig
    /// classification, the dig animation and step count, the currency payout and Tally-of-the-Unburied
    /// charge all run unchanged. Two things differ, both carried on the map instance itself so they survive
    /// a relog or restart:
    ///
    ///   - the dig site (PropertyFloat 9010/9011) is the ADMIN'S OWN position at creation time, written
    ///     directly rather than through MlTreasureDrop.StampSite/the catalogue, so the reward is exactly
    ///     where the admin is standing rather than up to ml_treasure_site_radius_metres away;
    ///   - PropertyBool.TreasureMapAdminTest plus, for an ordinary (non-Relaria) map,
    ///     PropertyInt.TreasureMapForcedDigsiteType (and for Boss Rush, TreasureMapForcedMechanicSet) force
    ///     FinishDig's MlDigsiteManager.TryStart call to open the requested encounter type instead of
    ///     rolling one, and make a refused TryStart report its reason to the digger in chat (an ordinary map
    ///     stays silent on a refusal, as always). Admission itself - enabled, one live encounter per digger,
    ///     separation from another live encounter, the server-wide cap - is never bypassed: a forced type
    ///     still has to clear the same gate a rolled one does.
    ///
    /// A Relaria (boss-variant) test map instead stamps PropertyInt.TreasureMapBossWcid
    /// (MlTreasureDrop.RelariaBossWcid), taking FinishDig's existing boss branch. TreasureMapAdminTest on
    /// that map makes MlRelariaSpawner.TrySpawn skip only the IsBossSite catalogue match
    /// (MlRelariaSpawner.EffectiveSiteIsBossOk) - the site was stamped here, not rolled from the catalogue,
    /// so the real check would refuse every test Relaria dig - while every other CanSpawn clause (enabled,
    /// realm/landblock, indoors, the town-exclusion margin) still applies unchanged.
    /// </summary>
    public static class MlTreasureTestCommands
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string UsageText = "Usage: /testtreasuremap waves | corruption | bossrush [setId] | relaria | random";

        /// <summary>Which kind of test map /testtreasuremap was asked to create - the parsed shape of the command's one argument.</summary>
        internal enum TestMapKind
        {
            Waves,
            Corruption,
            BossRush,
            Relaria,
            Random,
        }

        /// <summary>
        /// The result of parsing /testtreasuremap's arguments: pure over the raw parameter array, so every
        /// input shape - each named type, Boss Rush with and without a set id, and every malformed input -
        /// is unit-testable with no Session, no Player and no PropertyManager read (BossRushSetId is
        /// validated against the live set table separately, by the command handler, because that table is
        /// a PropertyManager tunable this struct never touches).
        /// </summary>
        internal readonly struct ParsedTestMapArgs
        {
            public bool IsValid { get; }
            public TestMapKind Kind { get; }
            public long? BossRushSetId { get; }
            public string Error { get; }

            private ParsedTestMapArgs(bool isValid, TestMapKind kind, long? bossRushSetId, string error)
            {
                IsValid = isValid;
                Kind = kind;
                BossRushSetId = bossRushSetId;
                Error = error;
            }

            public static ParsedTestMapArgs Valid(TestMapKind kind, long? bossRushSetId = null) => new ParsedTestMapArgs(true, kind, bossRushSetId, null);

            public static ParsedTestMapArgs Invalid(string error) => new ParsedTestMapArgs(false, default, null, error);
        }

        /// <summary>
        /// Pure argument parser: no session, no world, no PropertyManager. An empty or unrecognised first
        /// argument returns the usage text as the error, which is also what the command sends verbatim -
        /// there is deliberately only ONE usage string, authored here, so the two can never drift apart.
        /// "bossrush" takes an optional second argument, parsed as a positive integer set id; anything else
        /// there (non-numeric, zero, negative) is a parse error, not a silently-ignored extra argument.
        /// </summary>
        internal static ParsedTestMapArgs ParseArgs(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0 || string.IsNullOrWhiteSpace(parameters[0]))
                return ParsedTestMapArgs.Invalid(UsageText);

            switch (parameters[0].ToLowerInvariant())
            {
                case "waves":
                    return ParsedTestMapArgs.Valid(TestMapKind.Waves);

                case "corruption":
                    return ParsedTestMapArgs.Valid(TestMapKind.Corruption);

                case "bossrush":
                {
                    if (parameters.Length < 2)
                        return ParsedTestMapArgs.Valid(TestMapKind.BossRush);

                    if (!long.TryParse(parameters[1], out var setId) || setId <= 0)
                        return ParsedTestMapArgs.Invalid($"Could not parse '{parameters[1]}' as a Boss Rush set id. {UsageText}");

                    return ParsedTestMapArgs.Valid(TestMapKind.BossRush, setId);
                }

                case "relaria":
                    return ParsedTestMapArgs.Valid(TestMapKind.Relaria);

                case "random":
                    return ParsedTestMapArgs.Valid(TestMapKind.Random);

                default:
                    return ParsedTestMapArgs.Invalid(UsageText);
            }
        }

        [CommandHandler("testtreasuremap", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Creates a test ML treasure map with the digsite encounter type forced and the dig site at your feet.",
            "waves | corruption | bossrush [setId] | relaria | random")]
        [CommandHandler("ttm", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "Creates a test ML treasure map with the digsite encounter type forced and the dig site at your feet.",
            "waves | corruption | bossrush [setId] | relaria | random")]
        public static void HandleTestTreasureMap(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            var parsed = ParseArgs(parameters);

            if (!parsed.IsValid)
            {
                Msg(parsed.Error);
                return;
            }

            if (parsed.Kind == TestMapKind.BossRush && parsed.BossRushSetId.HasValue)
            {
                var sets = MlDigsiteTunables.BossRushSets;

                if (!sets.Any(s => s.SetId == parsed.BossRushSetId.Value))
                {
                    var validIds = string.Join(", ", sets.Select(s => s.SetId).OrderBy(id => id));
                    Msg($"Unknown Boss Rush set id {parsed.BossRushSetId.Value}. Valid set ids: {validIds}");
                    return;
                }
            }

            var mapCoords = player.Location.GetMapCoords();

            if (mapCoords == null)
            {
                Msg("You must be outdoors to create a test treasure map - the dig site is placed at your own map coordinates.");
                return;
            }

            var landblock = player.Location.LandblockId.Landblock;
            var realm = player.Location.RealmID;

            if (!MlTreasureLandblock.IsMaraeLassel(landblock, realm))
            {
                Msg("You must be on Marae Lassel (realm 1) to create a test treasure map.");
                return;
            }

            var map = WorldObjectFactory.CreateNewWorldObject(MlTreasureDrop.TreasureMapWcid);

            if (map == null)
            {
                Msg($"Could not create wcid {MlTreasureDrop.TreasureMapWcid}. Is Content/sql/weenies/1004100 ML Treasure Map.sql applied to this world database?");
                log.Error($"[ML_TREASURE] /testtreasuremap: wcid {MlTreasureDrop.TreasureMapWcid} failed to create for {player.Name}");
                return;
            }

            // The site is the admin's OWN position, written directly - never MlTreasureDrop.StampSite or
            // the catalogue, whose radius/nearest-site fallbacks could place it anywhere up to
            // ml_treasure_site_radius_metres away. X=EastWest, Y=NorthSouth, the same axis order
            // TreasureMapHandler/MlTreasureDrop use everywhere else.
            map.SetProperty(PropertyFloat.TreasureMapNorthSouth, (double)mapCoords.Value.Y);
            map.SetProperty(PropertyFloat.TreasureMapEastWest, (double)mapCoords.Value.X);
            map.SetProperty(PropertyBool.TreasureMapAdminTest, true);

            string label;

            switch (parsed.Kind)
            {
                case TestMapKind.Waves:
                    map.SetProperty(PropertyInt.TreasureMapForcedDigsiteType, MlDigsiteRules.EncodeForcedType(MlDigsiteType.WavesAndMiniBoss));
                    label = "Waves and Mini-Boss";
                    break;

                case TestMapKind.Corruption:
                    map.SetProperty(PropertyInt.TreasureMapForcedDigsiteType, MlDigsiteRules.EncodeForcedType(MlDigsiteType.CorruptionMeter));
                    label = "Corruption Meter";
                    break;

                case TestMapKind.BossRush:
                    map.SetProperty(PropertyInt.TreasureMapForcedDigsiteType, MlDigsiteRules.EncodeForcedType(MlDigsiteType.BossRush));

                    if (parsed.BossRushSetId.HasValue)
                    {
                        map.SetProperty(PropertyInt.TreasureMapForcedMechanicSet, (int)parsed.BossRushSetId.Value);
                        label = $"Boss Rush, set {parsed.BossRushSetId.Value}";
                    }
                    else
                        label = "Boss Rush";

                    break;

                case TestMapKind.Relaria:
                    // The boss-variant arm of FinishDig branches on this alone (bossWcid > 0); no forced-type
                    // property is relevant to it, because a Relaria dig never opens a digsite encounter at
                    // all - MlDigsiteManager.TryStart sits on the ordinary-payout arm only.
                    map.SetProperty(PropertyInt.TreasureMapBossWcid, (int)MlTreasureDrop.RelariaBossWcid);
                    label = "Relaria";
                    break;

                default: // Random: a normal weighted roll, still a test map for the site-at-your-feet
                         // placement and the refusal-reason reporting.
                    label = "Random";
                    break;
            }

            map.Name = $"Test Treasure Map ({label})";

            if (!player.TryCreateInInventoryWithNetworking(map))
            {
                map.Destroy();
                Msg("Your pack is too full to hold the test treasure map.");
                return;
            }

            Msg($"Created a test treasure map ({label}). Dig site: your current position ({mapCoords.Value.Y:0.0}N, {mapCoords.Value.X:0.0}E).");

            log.Info($"[ML_TREASURE] {player.Name} used /testtreasuremap to create a {label} test map (0x{map.Guid.Full:X8}) at {player.Location.ToLOCString()}");
        }
    }
}
