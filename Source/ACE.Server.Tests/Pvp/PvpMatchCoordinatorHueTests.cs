using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Team hue through the coordinator (Docs/Pvp/TEMPLATES.md "Team hue"), driven through the same fakes as the rest of
    /// PvpMatchCoordinatorTests (this is a partial of that class, so run it with
    /// <c>--filter "FullyQualifiedName~TeamHue_"</c>). The dials are taken once per dispatch; each seat's frozen definition is the
    /// catalog definition dressed in its hue; the catalog itself is never changed.
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        private const uint HueCbChest = 0x1000018E;
        private const uint HueCbHelm = 0x10000200;

        private static readonly int HuePaletteKey = (int)PropertyInt.PaletteTemplate;
        private static readonly int HueShadeKey = (int)PropertyFloat.Shade;
        private static readonly int HueIconKey = (int)PropertyDataId.Icon;

        /// <summary>The chest ClothingBase lists 2, 14, 85, 88, 90; the helm ClothingBase lists only 2 and 13 (so the global palettes do not fit it).</summary>
        private static IReadOnlyCollection<uint> HuePalettes(uint clothingBase) =>
            clothingBase == HueCbChest ? new uint[] { 2, 14, 85, 88, 90 } : clothingBase == HueCbHelm ? new uint[] { 2, 13 } : null;

        private static uint HueIcon(uint clothingBase, uint palette) => 0x06000000u + palette;

        private static PvpTeamHueDials HueDials(bool enabled = true) => new PvpTeamHueDials(enabled,
            new PvpTeamHueLook(85, 0.0), new PvpTeamHueLook(88, 0.0), new PvpTeamHueLook(90, 0.0),
            new Dictionary<(uint, KitHue), PvpTeamHueLook>());

        /// <summary>A template row whose kit is a worn chest piece, a worn helm that cannot take the global palettes, and a pack item.</summary>
        private static PvpTemplateRecord HueTemplateRow(string key = DefaultTemplateKey)
        {
            var chest = new PvpTemplateKitItem { WeenieClassId = 1001, WieldLocation = 0x200 };
            chest.DataIds[(int)PropertyDataId.ClothingBase] = HueCbChest;
            chest.DataIds[HueIconKey] = 0x06001111;
            chest.Ints[HuePaletteKey] = 14;
            chest.Floats[HueShadeKey] = 0.5;
            chest.Strings[(int)PropertyString.Name] = "Plate Chest";

            var helm = new PvpTemplateKitItem { WeenieClassId = 1002, WieldLocation = 0x1 };
            helm.DataIds[(int)PropertyDataId.ClothingBase] = HueCbHelm;
            helm.DataIds[HueIconKey] = 0x06002222;
            helm.Ints[HuePaletteKey] = 2;
            helm.Floats[HueShadeKey] = 0.25;
            helm.Strings[(int)PropertyString.Name] = "Plate Helm";

            var potion = new PvpTemplateKitItem { WeenieClassId = 1003 };
            potion.Strings[(int)PropertyString.Name] = "Potion";

            var definition = new PvpTemplateDefinition { Key = key, Version = 1, DisplayName = "Duelist" };
            definition.Kit.AddRange(new[] { chest, helm, potion });

            return new PvpTemplateRecord
            {
                TemplateKey = key,
                DisplayName = "Duelist",
                Version = 1,
                Enabled = true,
                Modes = "1v1,2v2,ffa",
                DefinitionJson = PvpTemplateJson.SerializeDefinition(definition),
                SnapshotAt = T0,
                SnapshotBy = "test",
            };
        }

        private static Rig HueRig(bool enabled = true, Func<PvpTeamHueDials> dials = null, bool withPalettes = true)
        {
            var rig = new Rig();
            rig.Sink.TemplateRows = new List<PvpTemplateRecord> { HueTemplateRow() };
            rig.Build();

            if (withPalettes)
            {
                rig.Gateway.PaletteLookup = HuePalettes;
                rig.Gateway.IconLookup = HueIcon;
            }

            rig.Coordinator.TeamHueDials = dials ?? (() => HueDials(enabled));

            return rig;
        }

        private static PvpTemplateDefinition HueWorn(Rig rig, uint id) => rig.Gateway.Entered.First(e => e.Id == id).Binding.Template;

        private static string HueJson(Rig rig, uint id) => PvpTemplateJson.SerializeDefinition(HueWorn(rig, id));

        private static int ChestPalette(PvpTemplateDefinition d) => d.Kit.Single(k => k.WeenieClassId == 1001).Ints[HuePaletteKey];

        private static PvpTemplateDefinition CatalogDefinition(Rig rig)
        {
            var field = typeof(PvpMatchCoordinator).GetField("templateCatalog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(field, "fixture: the coordinator's catalog field");

            return ((PvpTemplateCatalog)field.GetValue(rig.Coordinator)).Get(DefaultTemplateKey).Definition;
        }

        private static PvpMatch HueOneVOneToLive(Rig rig)
        {
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(B, "1v1", false).Joined);
            rig.Tick();
            rig.AcceptAll(A, B);
            return rig.DriveAcceptedToLive(A);
        }

        private static PvpMatch HueTwoVTwoToLive(Rig rig)
        {
            rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B });
            rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B });
            rig.Gateway.Add(C, "Charlie");
            rig.Gateway.Add(D, "Delta");

            rig.Coordinator.Join(A, "2v2", true);
            rig.Coordinator.Join(C, "2v2", false);
            rig.Coordinator.Join(D, "2v2", false);
            rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
            rig.Tick();
            rig.AcceptAll(A, B, C, D);
            return rig.DriveAcceptedToLive(A);
        }

        private static int HueTeamOf(PvpMatch match, uint id) => match.Teams.Single(t => t.Members.Any(p => p.CharacterId == id)).TeamIndex;

        /// <summary>
        /// Drives: a 2v2 under both coin outcomes. DISCRIMINATES the per-team hue: the two seats on a team wear a byte-identical kit
        /// look, the two teams differ, the Red team wears the red palette and the Blue team the blue palette, and which team is
        /// which follows the coin.
        /// </summary>
        [TestMethod]
        public void TeamHue_TwoVTwo_EverySeatOnATeamGetsAnIdenticalKitLook_AndTheTeamsDiffer()
        {
            foreach (var coin in new[] { true, false })
            {
                var rig = HueRig();
                rig.ColourCoin = coin;

                var match = HueTwoVTwoToLive(rig);

                var teamOfA = HueTeamOf(match, A);
                var teamOfC = HueTeamOf(match, C);

                Assert.AreNotEqual(teamOfA, teamOfC, "fixture: A and C are on different teams");
                Assert.AreEqual(HueTeamOf(match, B), teamOfA, "fixture: the duo shares a team");
                Assert.AreEqual(HueTeamOf(match, D), teamOfC, "fixture: the solos share a team");

                Assert.AreEqual(HueJson(rig, A), HueJson(rig, B), $"coin {coin}: one team, one look");
                Assert.AreEqual(HueJson(rig, C), HueJson(rig, D), $"coin {coin}: one team, one look");
                Assert.AreNotEqual(HueJson(rig, A), HueJson(rig, C), $"coin {coin}: the teams differ");

                // coin true makes team 0 Red; false swaps.
                var aIsRed = teamOfA == (coin ? 0 : 1);

                Assert.AreEqual(aIsRed ? 85 : 88, ChestPalette(HueWorn(rig, A)), $"coin {coin}: A's team");
                Assert.AreEqual(aIsRed ? 88 : 85, ChestPalette(HueWorn(rig, C)), $"coin {coin}: C's team");
            }
        }

        [TestMethod]
        public void TeamHue_OneVOne_RedSeatWearsRed_BlueSeatWearsBlue_FollowingTheCoin()
        {
            foreach (var coin in new[] { true, false })
            {
                var rig = HueRig();
                rig.ColourCoin = coin;

                var match = HueOneVOneToLive(rig);
                var redId = HueTeamOf(match, A) == (coin ? 0 : 1) ? A : B;
                var blueId = redId == A ? B : A;

                Assert.AreEqual(85, ChestPalette(HueWorn(rig, redId)), $"coin {coin}");
                Assert.AreEqual(88, ChestPalette(HueWorn(rig, blueId)), $"coin {coin}");
                Assert.AreEqual(0x06000000u + 85u, HueWorn(rig, redId).Kit[0].DataIds[HueIconKey], "the icon follows the palette");
            }
        }

        /// <summary>The helm cannot take the global palettes; it keeps exactly its own look on every seat, and the pack item is untouched.</summary>
        [TestMethod]
        public void TeamHue_APieceThatCannotTakeThePalette_KeepsItsOwnLook_AndNothingElseChanges()
        {
            var rig = HueRig();
            HueOneVOneToLive(rig);

            foreach (var id in new[] { A, B })
            {
                var worn = HueWorn(rig, id);
                var helm = worn.Kit.Single(k => k.WeenieClassId == 1002);

                Assert.AreEqual(2, helm.Ints[HuePaletteKey]);
                Assert.AreEqual(0.25, helm.Floats[HueShadeKey]);
                Assert.AreEqual(0x06002222u, helm.DataIds[HueIconKey]);

                var original = CatalogDefinition(rig).Kit.Single(k => k.WeenieClassId == 1003);
                var pack = worn.Kit.Single(k => k.WeenieClassId == 1003);
                Assert.AreEqual(PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = "k", Kit = { original } }),
                    PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = "k", Kit = { pack } }), "the pack item is identical");
            }
        }

        private static readonly uint[] HueIds = { A, B, C, D, E };

        /// <summary>Joins five players to a Tugak Brawl and drives it to Live. The rig's Dials must already allow a five-seat FFA.</summary>
        private static void HueTugakToLive(Rig rig)
        {
            var names = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo" };

            for (var i = 0; i < HueIds.Length; i++)
            {
                rig.Gateway.Add(HueIds[i], names[i]);
                Assert.IsTrue(rig.Coordinator.Join(HueIds[i], "ffa", false).Joined);
            }

            rig.Tick();
            rig.AcceptAll(HueIds);
            rig.DriveAcceptedToLive(A);
        }

        /// <summary>
        /// A Tugak rig whose chest AND helm ClothingBases both list 2, 13, 14, 85, 88, 90, so any pool entry fits every piece. The pool,
        /// the FFA shade, the overrides and the random source are the test's; the random source defaults to a fixed one.
        /// </summary>
        private static Rig FfaRig(IReadOnlyList<int> pool, Func<int, int> random = null, double shade = 0.3, string overrides = null, bool enabled = true)
        {
            var rig = HueRig(withPalettes: false);
            rig.Gateway.PaletteLookup = cb => cb == HueCbChest || cb == HueCbHelm ? new uint[] { 2, 13, 14, 85, 88, 90 } : null;
            rig.Gateway.IconLookup = HueIcon;
            rig.Coordinator.TeamHueDials = () => HueDials(enabled) with
            {
                FfaPalettes = pool,
                FfaShade = shade,
                Overrides = overrides == null ? new Dictionary<(uint, KitHue), PvpTeamHueLook>() : PvpTeamHue.ParseOverrides(overrides),
            };
            rig.Coordinator.TeamHueRandom = random ?? (n => 0);
            rig.Dials = PvpTunables.Defaults with { FfaTargetPlayers = 5, FfaMinPlayers = 5 };

            return rig;
        }

        private static int HueHelmPalette(PvpTemplateDefinition d) => d.Kit.Single(k => k.WeenieClassId == 1002).Ints[HuePaletteKey];

        /// <summary>
        /// Drives: a 5-seat Tugak Brawl with a 5-entry pool, once per random source (a fixed one and 25 seeds). DISCRIMINATES the distinct
        /// draw: with seats at most the pool size no two seats share a palette and the five seats between them wear the whole pool.
        /// A draw with replacement (or one that ignores the shuffle) repeats a palette under the fixed source and fails here.
        /// </summary>
        [TestMethod]
        public void TeamHue_Tugak_SeatsWithinThePool_NeverShareAPalette()
        {
            var pool = new[] { 2, 13, 14, 85, 88 };
            var sources = new List<Func<int, int>> { n => 0 };

            for (var seed = 1; seed <= 25; seed++)
                sources.Add(new Random(seed).Next);

            foreach (var source in sources)
            {
                var rig = FfaRig(pool, source);
                HueTugakToLive(rig);

                var palettes = HueIds.Select(id => ChestPalette(HueWorn(rig, id))).ToList();

                Assert.AreEqual(5, palettes.Distinct().Count(), $"no two seats share a palette: {string.Join(",", palettes)}");
                CollectionAssert.AreEquivalent(pool, palettes);
            }
        }

        /// <summary>Drives: five seats on a two-entry pool. Every seat is still dressed from the pool, both palettes are used, and the split is as even as it can be.</summary>
        [TestMethod]
        public void TeamHue_Tugak_MoreSeatsThanThePool_StillDressesEverySeat()
        {
            var rig = FfaRig(new[] { 13, 85 }, new Random(3).Next);
            HueTugakToLive(rig);

            var palettes = HueIds.Select(id => ChestPalette(HueWorn(rig, id))).ToList();

            Assert.IsTrue(palettes.All(p => p == 13 || p == 85), $"every seat wears a pool palette: {string.Join(",", palettes)}");
            Assert.AreEqual(2, palettes.Distinct().Count(), "both palettes are in use");
            Assert.IsTrue(Math.Abs(palettes.Count(p => p == 13) - palettes.Count(p => p == 85)) <= 1, "a reshuffle deals every palette again before any is dealt a third time");
        }

        /// <summary>Drives: five Tugak seats, a chest and a helm each. DISCRIMINATES one palette per PLAYER: both worn pieces of a seat carry that seat's palette, shade and icon.</summary>
        [TestMethod]
        public void TeamHue_Tugak_EveryPieceOfOnePlayerWearsThatPlayersPalette()
        {
            var rig = FfaRig(new[] { 2, 13, 14, 85, 88 }, new Random(11).Next, shade: 0.3);
            HueTugakToLive(rig);

            foreach (var id in HueIds)
            {
                var kit = HueWorn(rig, id);
                var chest = kit.Kit.Single(k => k.WeenieClassId == 1001);
                var helm = kit.Kit.Single(k => k.WeenieClassId == 1002);

                Assert.AreEqual(ChestPalette(kit), HueHelmPalette(kit), $"seat 0x{id:X8}: one palette for the whole kit");
                Assert.AreEqual(0.3, chest.Floats[HueShadeKey], $"seat 0x{id:X8}");
                Assert.AreEqual(0.3, helm.Floats[HueShadeKey], $"seat 0x{id:X8}");
                Assert.AreEqual(0x06000000u + (uint)ChestPalette(kit), chest.DataIds[HueIconKey], $"seat 0x{id:X8}: the icon follows the palette");
            }

            Assert.AreEqual(5, HueIds.Select(id => HueJson(rig, id)).Distinct().Count(), "five seats, five different kits");
        }

        /// <summary>A pool palette a piece's ClothingBase does not list is never written: that piece keeps its own look and the rest of the kit is dressed.</summary>
        [TestMethod]
        public void TeamHue_Tugak_APieceThatCannotTakeTheDrawnPalette_KeepsItsOwnLook()
        {
            var rig = FfaRig(new[] { 85 });
            rig.Gateway.PaletteLookup = HuePalettes;
            HueTugakToLive(rig);

            foreach (var id in HueIds)
            {
                var kit = HueWorn(rig, id);

                Assert.AreEqual(85, ChestPalette(kit), "the chest lists 85");
                Assert.AreEqual(2, HueHelmPalette(kit), "the helm lists only 2 and 13, so 85 is never written");
                Assert.AreEqual(0.25, kit.Kit.Single(k => k.WeenieClassId == 1002).Floats[HueShadeKey]);
            }
        }

        /// <summary>
        /// Drives: a Tugak match whose overrides name the chest's ClothingBase for every team (88), with a pool that excludes 88.
        /// DISCRIMINATES "overrides are ignored in Tugak": no seat wears 88.
        /// </summary>
        [TestMethod]
        public void TeamHue_Tugak_IgnoresThePerTeamOverrides()
        {
            var rig = FfaRig(new[] { 2, 13, 14, 85, 90 }, new Random(2).Next, overrides: "1000018E:red=88/0.9,blue=88/0.9,neutral=88/0.9");
            HueTugakToLive(rig);

            var palettes = HueIds.Select(id => ChestPalette(HueWorn(rig, id))).ToList();

            CollectionAssert.AreEquivalent(new[] { 2, 13, 14, 85, 90 }, palettes, "the overrides' 88 reached nobody");
            Assert.IsTrue(HueIds.All(id => HueWorn(rig, id).Kit.Single(k => k.WeenieClassId == 1001).Floats[HueShadeKey] == 0.3), "the override's shade 0.9 reached nobody");
        }

        /// <summary>Drives: Tugak with an "@wcid" look swap override for the aegis's ClothingBase. DISCRIMINATES "no look swaps in Tugak": every aegis keeps its native model.</summary>
        [TestMethod]
        public void TeamHue_Tugak_TakesNoLookSwaps()
        {
            var rig = HueSwapRig("10000156:red=@1007256,blue=@1007256,neutral=@1007256", wcid => wcid == HueRedAegisWcid ? HueRedAegisSource() : null);
            rig.Coordinator.TeamHueDials = () => new PvpTeamHueDials(true,
                new PvpTeamHueLook(85, 0.0), new PvpTeamHueLook(88, 0.0), new PvpTeamHueLook(90, 0.0),
                PvpTeamHue.ParseOverrides("10000156:red=@1007256,blue=@1007256,neutral=@1007256"), new[] { 85, 88 }, 0.0);
            rig.Coordinator.TeamHueRandom = n => 0;
            rig.Dials = PvpTunables.Defaults with { FfaTargetPlayers = 5, FfaMinPlayers = 5 };

            HueTugakToLive(rig);

            foreach (var id in HueIds)
            {
                var aegis = HueAegisOf(rig, id);

                Assert.AreEqual(0x02000577u, aegis.DataIds[(int)PropertyDataId.Setup], $"seat 0x{id:X8}: the native model, not the swapped one");
                Assert.AreEqual(HueCbAegis, aegis.DataIds[(int)PropertyDataId.ClothingBase]);
                Assert.IsTrue(new[] { 85, 88 }.Contains(aegis.Ints[HuePaletteKey]), $"seat 0x{id:X8}: a pool palette");
            }
        }

        /// <summary>The catalog definition is unchanged after a Tugak freeze that dressed five seats in five kits.</summary>
        [TestMethod]
        public void TeamHue_Tugak_NeverMutatesTheCatalog()
        {
            var rig = FfaRig(new[] { 2, 13, 14, 85, 88 }, new Random(4).Next);
            var before = PvpTemplateJson.SerializeDefinition(CatalogDefinition(rig));

            HueTugakToLive(rig);

            Assert.AreEqual(before, PvpTemplateJson.SerializeDefinition(CatalogDefinition(rig)));
            Assert.AreEqual(14, ChestPalette(CatalogDefinition(rig)), "the catalog chest still has its own palette");

            foreach (var id in HueIds)
                Assert.AreNotSame(CatalogDefinition(rig), HueWorn(rig, id));
        }

        /// <summary>One dials snapshot for the whole dispatch, however many seats are dressed.</summary>
        [TestMethod]
        public void TeamHue_Tugak_TheDialsAreStillSnapshottedOncePerDispatch()
        {
            var reads = 0;
            var rig = FfaRig(new[] { 2, 13, 14, 85, 88 });
            var inner = rig.Coordinator.TeamHueDials;
            rig.Coordinator.TeamHueDials = () => { reads++; return inner(); };

            HueTugakToLive(rig);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(1, reads);
        }

        /// <summary>Hue switched OFF: Tugak seats keep the catalog look and the pool is not drawn from.</summary>
        [TestMethod]
        public void TeamHue_Tugak_WhenDisabled_TheKitIsTheCatalogDefinition()
        {
            var draws = 0;
            var rig = FfaRig(new[] { 2, 13, 14, 85, 88 }, n => { draws++; return 0; }, enabled: false);
            var before = PvpTemplateJson.SerializeDefinition(CatalogDefinition(rig));

            HueTugakToLive(rig);

            foreach (var id in HueIds)
                Assert.AreEqual(before, HueJson(rig, id));

            Assert.AreEqual(0, draws, "no draw while the master switch is off");
        }

        /// <summary>
        /// Drives: a 5-seat Tugak Brawl under both coin outcomes with no pool (the dials' pool is null, then an all-invalid setting
        /// string, then empty). DISCRIMINATES the fallback: the previous behaviour, every seat the SAME coin-flipped Red or Blue palette.
        /// </summary>
        [TestMethod]
        public void TeamHue_Tugak_WithNoUsablePool_FallsBackToOneSharedCoinFlippedColour()
        {
            var pools = new IReadOnlyList<int>[] { null, PvpTeamHue.ParseFfaPalettes("abc,0,-3"), new int[0] };

            foreach (var pool in pools)
            {
                foreach (var coin in new[] { true, false })
                {
                    var draws = 0;
                    var rig = FfaRig(pool, n => { draws++; return 0; });
                    rig.ColourCoin = coin;

                    HueTugakToLive(rig);

                    var expected = coin ? 85 : 88;

                    CollectionAssert.AreEqual(Enumerable.Repeat(expected, HueIds.Length).ToList(), HueIds.Select(id => ChestPalette(HueWorn(rig, id))).ToList(), $"coin {coin}");
                    Assert.AreEqual(1, HueIds.Select(id => HueJson(rig, id)).Distinct().Count(), $"coin {coin}: byte-identical kits");
                    Assert.AreEqual(0, draws, "the fallback draws nothing");
                }
            }
        }

        /// <summary>The same dials, a pool included, leave 1v1 and 2v2 exactly as they were: the red and blue palettes by the coin.</summary>
        [TestMethod]
        public void TeamHue_OneVOneAndTwoVTwo_IgnoreTheTugakPool()
        {
            foreach (var coin in new[] { true, false })
            {
                var oneVOne = HueRig(dials: () => HueDials() with { FfaPalettes = new[] { 2, 14, 90 }, FfaShade = 0.4 });
                oneVOne.ColourCoin = coin;
                var match = HueOneVOneToLive(oneVOne);
                var redId = HueTeamOf(match, A) == (coin ? 0 : 1) ? A : B;
                var blueId = redId == A ? B : A;

                Assert.AreEqual(85, ChestPalette(HueWorn(oneVOne, redId)), $"1v1 coin {coin}");
                Assert.AreEqual(88, ChestPalette(HueWorn(oneVOne, blueId)), $"1v1 coin {coin}");
                Assert.AreEqual(0.0, HueWorn(oneVOne, redId).Kit.Single(k => k.WeenieClassId == 1001).Floats[HueShadeKey], "the FFA shade is not used");

                var twoVTwo = HueRig(dials: () => HueDials() with { FfaPalettes = new[] { 2, 14, 90 }, FfaShade = 0.4 });
                twoVTwo.ColourCoin = coin;
                var match2 = HueTwoVTwoToLive(twoVTwo);
                var aIsRed = HueTeamOf(match2, A) == (coin ? 0 : 1);

                Assert.AreEqual(aIsRed ? 85 : 88, ChestPalette(HueWorn(twoVTwo, A)), $"2v2 coin {coin}");
                Assert.AreEqual(aIsRed ? 85 : 88, ChestPalette(HueWorn(twoVTwo, B)), $"2v2 coin {coin}");
                Assert.AreEqual(aIsRed ? 88 : 85, ChestPalette(HueWorn(twoVTwo, C)), $"2v2 coin {coin}");
                Assert.AreEqual(aIsRed ? 88 : 85, ChestPalette(HueWorn(twoVTwo, D)), $"2v2 coin {coin}");
            }
        }

        /// <summary>The catalog's own definition is unchanged after a freeze, for a match whose seats were dressed in two different hues.</summary>
        [TestMethod]
        public void TeamHue_TheCatalogDefinitionIsUnmodifiedAfterAFreeze()
        {
            var rig = HueRig();
            var before = PvpTemplateJson.SerializeDefinition(CatalogDefinition(rig));

            HueOneVOneToLive(rig);

            Assert.AreNotEqual(HueJson(rig, A), HueJson(rig, B), "fixture: the two seats were dressed differently");
            Assert.AreEqual(before, PvpTemplateJson.SerializeDefinition(CatalogDefinition(rig)));
            Assert.AreEqual(14, ChestPalette(CatalogDefinition(rig)), "the catalog piece still has its own palette");
            Assert.AreNotSame(CatalogDefinition(rig), HueWorn(rig, A));
            Assert.AreNotSame(CatalogDefinition(rig), HueWorn(rig, B));
        }

        [TestMethod]
        public void TeamHue_WhenDisabled_TheFrozenKitIsTheCatalogDefinition()
        {
            var rig = HueRig(enabled: false);
            var before = PvpTemplateJson.SerializeDefinition(CatalogDefinition(rig));

            HueOneVOneToLive(rig);

            foreach (var id in new[] { A, B })
            {
                Assert.AreEqual(before, HueJson(rig, id));
                Assert.AreEqual(14, ChestPalette(HueWorn(rig, id)));
            }
        }

        /// <summary>A gateway that cannot read the dat (the interface default) never dresses a kit: no palette is written that was not listed.</summary>
        [TestMethod]
        public void TeamHue_WithNoPaletteLookup_NoKitIsDressed()
        {
            var rig = HueRig(withPalettes: false);
            var before = PvpTemplateJson.SerializeDefinition(CatalogDefinition(rig));

            HueOneVOneToLive(rig);

            Assert.AreEqual(before, HueJson(rig, A));
            Assert.AreEqual(before, HueJson(rig, B));
        }

        // ---------------- "@wcid" look swap ----------------

        private const uint HueCbAegis = 0x10000156;
        private const uint HueRedAegisWcid = 1007256;
        private const int HueShieldSlot = 0x200000;

        /// <summary>The red aegis as the world database holds it (a look source for the "@wcid" override).</summary>
        private static PvpLookSource HueRedAegisSource() => new PvpLookSource
        {
            Wcid = HueRedAegisWcid,
            Name = "PvP Arena Aegis (Red)",
            Look = new LookFields { Setup = 0x0200057B, ClothingBase = 0x1000015A, PaletteBase = 0x04000BEF, Icon = 0x060012F7, PaletteTemplate = 14 },
            ValidLocations = HueShieldSlot,
        };

        /// <summary>
        /// A template whose kit is the native blue aegis with its REAL values (palette 2, icon 0x060012F7, no Shade). The clothing
        /// table's icon for palette 2 is a different one, so any override that writes a palette would visibly change the piece.
        /// With a pack potion.
        /// </summary>
        private static PvpTemplateRecord HueAegisRow()
        {
            var aegis = new PvpTemplateKitItem { WeenieClassId = 1006764, WieldLocation = HueShieldSlot };
            aegis.DataIds[(int)PropertyDataId.Setup] = 0x02000577;
            aegis.DataIds[(int)PropertyDataId.PaletteBase] = 0x04000BEF;
            aegis.DataIds[(int)PropertyDataId.ClothingBase] = HueCbAegis;
            aegis.DataIds[HueIconKey] = 0x060012F7;
            aegis.Ints[HuePaletteKey] = 2;
            aegis.Floats[(int)PropertyFloat.AbsorbMagicDamage] = 0.25;
            aegis.Strings[(int)PropertyString.Name] = "PvP Arena Aegis";

            var potion = new PvpTemplateKitItem { WeenieClassId = 1003 };
            potion.Strings[(int)PropertyString.Name] = "Potion";

            var definition = new PvpTemplateDefinition { Key = DefaultTemplateKey, Version = 1, DisplayName = "Duelist" };
            definition.Kit.AddRange(new[] { aegis, potion });

            return new PvpTemplateRecord
            {
                TemplateKey = DefaultTemplateKey,
                DisplayName = "Duelist",
                Version = 1,
                Enabled = true,
                Modes = "1v1,2v2,ffa",
                DefinitionJson = PvpTemplateJson.SerializeDefinition(definition),
                SnapshotAt = T0,
                SnapshotBy = "test",
            };
        }

        private static Rig HueSwapRig(string overrides, Func<uint, PvpLookSource> lookSource)
        {
            var rig = new Rig();
            rig.Sink.TemplateRows = new List<PvpTemplateRecord> { HueAegisRow() };
            rig.Build();
            rig.Gateway.PaletteLookup = cb => cb == HueCbAegis ? new uint[] { 2, 85, 88 } : null;
            rig.Gateway.IconLookup = HueIcon;
            rig.Gateway.LookSourceLookup = lookSource;
            rig.Coordinator.TeamHueDials = () => new PvpTeamHueDials(true,
                new PvpTeamHueLook(85, 0.0), new PvpTeamHueLook(88, 0.0), new PvpTeamHueLook(90, 0.0),
                PvpTeamHue.ParseOverrides(overrides));

            return rig;
        }

        private static PvpTemplateKitItem HueAegisOf(Rig rig, uint id) => HueWorn(rig, id).Kit.Single(k => k.WeenieClassId == 1006764);

        /// <summary>
        /// Drives: a 1v1 under both coin outcomes with "10000156:red=@1007256,blue=0". DISCRIMINATES the look swap through the
        /// coordinator: the red seat's aegis wears the red aegis's whole look, the blue seat's aegis is the native blue look
        /// unchanged, and everything else about the two kits (the pack item, the aegis's stats, name and slot) is identical.
        /// </summary>
        [TestMethod]
        public void TeamHue_ASwapOverride_RedAndBlueSeatsDifferOnlyInTheSwappedItemsLook()
        {
            foreach (var coin in new[] { true, false })
            {
                var rig = HueSwapRig("10000156:red=@1007256,blue=0", wcid => wcid == HueRedAegisWcid ? HueRedAegisSource() : null);
                rig.ColourCoin = coin;

                var match = HueOneVOneToLive(rig);
                var redId = HueTeamOf(match, A) == (coin ? 0 : 1) ? A : B;
                var blueId = redId == A ? B : A;

                var red = HueAegisOf(rig, redId);
                var blue = HueAegisOf(rig, blueId);
                var native = CatalogDefinition(rig).Kit.Single(k => k.WeenieClassId == 1006764);

                Assert.AreEqual(0x0200057Bu, red.DataIds[(int)PropertyDataId.Setup], $"coin {coin}: red model");
                Assert.AreEqual(0x1000015Au, red.DataIds[(int)PropertyDataId.ClothingBase], $"coin {coin}: red ClothingBase");
                Assert.AreEqual(14, red.Ints[HuePaletteKey], $"coin {coin}: red palette");
                Assert.AreEqual(0x02000577u, blue.DataIds[(int)PropertyDataId.Setup], $"coin {coin}: blue keeps the native model");
                Assert.AreEqual(HueCbAegis, blue.DataIds[(int)PropertyDataId.ClothingBase], $"coin {coin}");

                var onlyNative = PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = "k", Kit = { native } });
                Assert.AreEqual(onlyNative, PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = "k", Kit = { blue } }), $"coin {coin}: blue is byte-identical to the catalog's aegis");

                // The differing keys between the two seats' aegis are all look fields.
                var lookKeys = new HashSet<int> { (int)PropertyDataId.Setup, (int)PropertyDataId.ClothingBase, (int)PropertyDataId.PaletteBase, HueIconKey };
                var differing = red.DataIds.Keys.Union(blue.DataIds.Keys).Where(k => !red.DataIds.TryGetValue(k, out var r) || !blue.DataIds.TryGetValue(k, out var b) || r != b).ToList();

                Assert.IsTrue(differing.Count > 0 && differing.All(lookKeys.Contains), $"coin {coin}: DataIds differ only in look keys: {string.Join(",", differing)}");
                Assert.AreEqual(red.Floats[(int)PropertyFloat.AbsorbMagicDamage], blue.Floats[(int)PropertyFloat.AbsorbMagicDamage]);
                CollectionAssert.AreEqual(red.Strings.OrderBy(s => s.Key).ToList(), blue.Strings.OrderBy(s => s.Key).ToList(), $"coin {coin}: name unchanged");
                Assert.AreEqual(red.WieldLocation, blue.WieldLocation);

                var redPotion = HueWorn(rig, redId).Kit.Single(k => k.WeenieClassId == 1003);
                var bluePotion = HueWorn(rig, blueId).Kit.Single(k => k.WeenieClassId == 1003);
                Assert.AreEqual(PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = "k", Kit = { redPotion } }),
                    PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = "k", Kit = { bluePotion } }), $"coin {coin}: the pack item is identical");
            }
        }

        /// <summary>The catalog's aegis is untouched after a swapped dispatch, and a bad source leaves both seats on the native look.</summary>
        [TestMethod]
        public void TeamHue_ASwapOverride_NeverMutatesTheCatalog_AndABadSourceKeepsTheOwnLook()
        {
            var good = HueSwapRig("10000156:red=@1007256,blue=0", wcid => HueRedAegisSource());
            var before = PvpTemplateJson.SerializeDefinition(CatalogDefinition(good));
            HueOneVOneToLive(good);
            Assert.AreEqual(before, PvpTemplateJson.SerializeDefinition(CatalogDefinition(good)), "the catalog definition is unchanged");

            var bad = HueSwapRig("10000156:red=@1007256,blue=@1007256", wcid => null);
            HueOneVOneToLive(bad);

            foreach (var id in new[] { A, B })
                Assert.AreEqual(0x02000577u, HueAegisOf(bad, id).DataIds[(int)PropertyDataId.Setup], "no look source: the native look stays on both seats");
        }

        /// <summary>
        /// Drives: a 1v1 from form through Live and several more ticks, counting the dials read. DISCRIMINATES the once-per-match
        /// snapshot: a second read at the freeze or on a later tick would make the count 2, and a setting changed after dispatch could
        /// split a match's rules.
        /// </summary>
        [TestMethod]
        public void TeamHue_TheDialsAreSnapshottedOncePerDispatch()
        {
            var reads = 0;
            var rig = HueRig(dials: () =>
            {
                reads++;
                return HueDials();
            });

            var match = HueOneVOneToLive(rig);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.AreEqual(1, reads);
        }

        /// <summary>A snapshot is fixed for the match: dials flipped to OFF after dispatch change nothing already frozen.</summary>
        [TestMethod]
        public void TeamHue_ADialChangeAfterDispatch_DoesNotReachTheFrozenKits()
        {
            var on = true;
            var rig = HueRig(dials: () => HueDials(on));

            HueOneVOneToLive(rig);
            var before = new[] { HueJson(rig, A), HueJson(rig, B) };

            on = false;
            rig.Tick();

            CollectionAssert.AreEqual(before, new[] { HueJson(rig, A), HueJson(rig, B) });
            Assert.AreNotEqual(14, ChestPalette(HueWorn(rig, A)), "still dressed");
        }

        /// <summary>A throwing dials source falls back to the shipped defaults instead of failing the dispatch.</summary>
        [TestMethod]
        public void TeamHue_AThrowingDialsSource_DoesNotFailTheDispatch()
        {
            var rig = HueRig(dials: () => throw new InvalidOperationException("settings"));

            var match = HueOneVOneToLive(rig);

            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.IsTrue(new[] { 85, 88 }.Contains(ChestPalette(HueWorn(rig, A))), "the compiled defaults dressed the kit");
        }
    }
}
