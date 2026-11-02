using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The team hue "@wcid" swap (Docs/Pvp/TEMPLATES.md "Team hue"): an override value "@&lt;wcid&gt;" copies a world weenie's whole look
    /// onto an issued kit item. Pure core only: the override grammar, <see cref="PvpTeamHue.ResolveItem"/> and
    /// <see cref="PvpTeamHue.Apply"/>, the audit, the suit preview, and the world look cache. No DAT, no PropertyManager.
    /// </summary>
    [TestClass]
    public class PvpTeamHueSwapTests
    {
        private const uint AegisCb = 0x10000156;
        private const uint RedAegisWcid = 1007256;
        private const int ShieldSlot = 0x200000;

        private static readonly int CbKey = (int)PropertyDataId.ClothingBase;
        private static readonly int SetupKey = (int)PropertyDataId.Setup;
        private static readonly int PaletteBaseKey = (int)PropertyDataId.PaletteBase;
        private static readonly int IconKey = (int)PropertyDataId.Icon;
        private static readonly int PaletteKey = (int)PropertyInt.PaletteTemplate;
        private static readonly int ShadeKey = (int)PropertyFloat.Shade;

        /// <summary>The native blue aegis lists palettes 2 and 85; the red one is reached only through the swap.</summary>
        private static IReadOnlyCollection<uint> Valid(uint cb) => cb == AegisCb ? new uint[] { 2, 85, 88 } : null;

        private static uint TableIcon(uint cb, uint palette) => 0x06000000u + palette;

        private static PvpLookSource RedAegis(Action<PvpLookSource> tweak = null)
        {
            var s = new PvpLookSource
            {
                Wcid = RedAegisWcid,
                Name = "PvP Arena Aegis (Red)",
                Look = new LookFields
                {
                    Setup = 0x0200057B,
                    ClothingBase = 0x1000015A,
                    PaletteBase = 0x04000BEF,
                    Icon = 0x060012F7,
                    PaletteTemplate = 14,
                },
                ValidLocations = ShieldSlot,
            };

            tweak?.Invoke(s);
            return s;
        }

        private static Func<uint, PvpLookSource> Sources(params PvpLookSource[] sources) =>
            wcid => sources.FirstOrDefault(s => s.Wcid == wcid);

        /// <summary>The native blue aegis as a kit item: the shield slot, retail blue look, a Strings name, and stat-ish rows that must never change.</summary>
        private static PvpTemplateKitItem BlueAegis()
        {
            var item = new PvpTemplateKitItem { WeenieClassId = 1006764, WieldLocation = ShieldSlot };
            item.DataIds[SetupKey] = 0x02000577;
            item.DataIds[PaletteBaseKey] = 0x04000BEF;
            item.DataIds[CbKey] = AegisCb;
            item.DataIds[IconKey] = 0x060012F7;
            item.DataIds[22] = 0x3400002B;
            item.Ints[PaletteKey] = 2;
            item.Ints[(int)PropertyInt.ArmorLevel] = 0;
            item.Floats[ShadeKey] = 0.5;
            item.Floats[(int)PropertyFloat.AbsorbMagicDamage] = 0.25;
            item.Strings[(int)PropertyString.Name] = "PvP Arena Aegis";
            return item;
        }

        private static PvpTeamHueDials DialsWith(string overrides, bool enabled = true) => new PvpTeamHueDials(enabled,
            new PvpTeamHueLook(85, 0.0), new PvpTeamHueLook(88, 0.0), new PvpTeamHueLook(90, 0.0),
            PvpTeamHue.ParseOverrides(overrides));

        private static PvpTemplateDefinition Definition(params PvpTemplateKitItem[] items)
        {
            var d = new PvpTemplateDefinition { Key = "duelist", Version = 3, DisplayName = "Duelist" };
            d.Kit.AddRange(items);
            return d;
        }

        private static string Json(PvpTemplateDefinition d) => PvpTemplateJson.SerializeDefinition(d);

        // ---------------- parsing ----------------

        [TestMethod]
        public void ParseOverrides_AWcidValue_IsAccepted_AsASwap()
        {
            var warnings = new List<string>();
            var parsed = PvpTeamHue.ParseOverrides("10000156:red=@1007256", warnings.Add);

            Assert.AreEqual(0, warnings.Count, string.Join("|", warnings));
            Assert.AreEqual(1, parsed.Count);
            Assert.AreEqual(new PvpTeamHueLook(0, 0.0, RedAegisWcid), parsed[(AegisCb, KitHue.Red)]);
            Assert.AreEqual(RedAegisWcid, parsed[(AegisCb, KitHue.Red)].SourceWcid);
        }

        [TestMethod]
        public void ParseOverrides_AMixedEntry_AcceptsBothForms_AndTheOtherEntriesStillParse()
        {
            var warnings = new List<string>();
            var parsed = PvpTeamHue.ParseOverrides("10000156:red=@1007256,blue=2/0;1000018E:red=85/0.4, neutral = @ 77 ", warnings.Add);

            Assert.AreEqual(0, warnings.Count, string.Join("|", warnings));
            Assert.AreEqual(4, parsed.Count);
            Assert.AreEqual(RedAegisWcid, parsed[(AegisCb, KitHue.Red)].SourceWcid);
            Assert.AreEqual(new PvpTeamHueLook(2, 0.0), parsed[(AegisCb, KitHue.Blue)]);
            Assert.AreEqual(0u, parsed[(AegisCb, KitHue.Blue)].SourceWcid);
            Assert.AreEqual(new PvpTeamHueLook(85, 0.4), parsed[(0x1000018Eu, KitHue.Red)]);
            Assert.AreEqual(77u, parsed[(0x1000018Eu, KitHue.Neutral)].SourceWcid);
        }

        [TestMethod]
        public void ParseOverrides_BadWcidForms_AreWarnedAndSkippedWhole_NeverThrow()
        {
            foreach (var bad in new[] { "red=@", "red=@0", "red=@abc", "red=@-5", "red=@12/0.4", "red=@1.5", "red=@@7", "red=@99999999999" })
            {
                var warnings = new List<string>();
                var parsed = PvpTeamHue.ParseOverrides($"10000156:{bad};1000018E:blue=88", warnings.Add);

                Assert.AreEqual(1, warnings.Count, $"{bad}: {string.Join("|", warnings)}");
                Assert.IsTrue(warnings[0].Contains("is ignored"), bad);
                Assert.IsFalse(parsed.ContainsKey((AegisCb, KitHue.Red)), $"{bad}: the whole entry is skipped");
                Assert.IsTrue(parsed.ContainsKey((0x1000018Eu, KitHue.Blue)), $"{bad}: the next entry survives");
            }
        }

        [TestMethod]
        public void ParseOverrides_APairAfterABadWcid_IsSkippedWithTheEntry()
        {
            var warnings = new List<string>();
            var parsed = PvpTeamHue.ParseOverrides("10000156:blue=88,red=@nope", warnings.Add);

            Assert.AreEqual(1, warnings.Count);
            Assert.AreEqual(0, parsed.Count, "an entry is accepted or skipped as a whole");
        }

        // ---------------- ResolveItem ----------------

        [TestMethod]
        public void ResolveItem_ASwap_ReturnsTheSourceLook_AndIsSourceSwap()
        {
            var dials = DialsWith("10000156:red=@1007256,blue=2/0");
            var r = PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Red, dials, Valid, Sources(RedAegis()));

            Assert.IsTrue(r.Applied, r.Detail);
            Assert.AreEqual(HueSource.Swap, r.Source);
            Assert.AreEqual(RedAegisWcid, r.SwapWcid);
            Assert.AreEqual(0x0200057Bu, r.SwapLook.Setup);
            Assert.AreEqual(14, r.Palette);
        }

        [TestMethod]
        public void ResolveItem_ATeamWithNoSwap_KeepsThePalettePath()
        {
            var dials = DialsWith("10000156:red=@1007256,blue=2/0");

            // blue is the explicit palette-2 override on the same ClothingBase
            var blue = PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Blue, dials, Valid, Sources(RedAegis()));
            Assert.IsTrue(blue.Applied);
            Assert.AreEqual(HueSource.Override, blue.Source);
            Assert.IsNull(blue.SwapLook);

            // the neutral team has no override at all: the global palette applies
            var neutral = PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Neutral, DialsWith("10000156:red=@1007256"), (cb) => new uint[] { 2, 90 }, Sources(RedAegis()));
            Assert.IsTrue(neutral.Applied);
            Assert.AreEqual(HueSource.Global, neutral.Source);
            Assert.AreEqual(90, neutral.Palette);
        }

        [TestMethod]
        public void ResolveItem_AnUnknownWcid_SkipsTheItem_WithAReason_AndDoesNotFallBackToTheGlobalPalette()
        {
            var r = PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Red, DialsWith("10000156:red=@555"), Valid, Sources(RedAegis()));

            Assert.IsFalse(r.Applied);
            Assert.AreEqual(HueSkipKind.BadLookSource, r.Skip);
            Assert.IsTrue(r.Detail.Contains("@555"), r.Detail);
        }

        [TestMethod]
        public void ResolveItem_ASourceWithNoClothingBase_SkipsTheItem_WithAReason()
        {
            var src = RedAegis(s => s.Look.ClothingBase = null);
            var r = PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Red, DialsWith("10000156:red=@1007256"), Valid, Sources(src));

            Assert.IsFalse(r.Applied);
            Assert.AreEqual(HueSkipKind.BadLookSource, r.Skip);
            Assert.IsTrue(r.Detail.Contains("no ClothingBase"), r.Detail);
        }

        [TestMethod]
        public void ResolveItem_ASourceWithRawRows_SkipsTheItem_WithAReasonPerRowKind()
        {
            foreach (var (tweak, word) in new (Action<PvpLookSource>, string)[]
            {
                (s => s.HasPaletteRows = true, "palette"),
                (s => s.HasTextureMapRows = true, "texture-map"),
                (s => s.HasAnimPartRows = true, "anim-part"),
            })
            {
                var r = PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Red, DialsWith("10000156:red=@1007256"), Valid, Sources(RedAegis(tweak)));

                Assert.IsFalse(r.Applied, word);
                Assert.AreEqual(HueSkipKind.BadLookSource, r.Skip, word);
                Assert.IsTrue(r.Detail.Contains("raw") && r.Detail.Contains(word), r.Detail);
            }
        }

        [TestMethod]
        public void ResolveItem_ASourceWhoseSlotsDoNotCoverTheKitItem_SkipsTheItem_WithAReason()
        {
            var chestSource = RedAegis(s => s.ValidLocations = 0x200);
            var r = PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Red, DialsWith("10000156:red=@1007256"), Valid, Sources(chestSource));

            Assert.IsFalse(r.Applied);
            Assert.AreEqual(HueSkipKind.BadLookSource, r.Skip);
            Assert.IsTrue(r.Detail.Contains("slot"), r.Detail);

            var wider = RedAegis(s => s.ValidLocations = ShieldSlot | 0x200);
            Assert.IsTrue(PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Red, DialsWith("10000156:red=@1007256"), Valid, Sources(wider)).Applied,
                "a source covering MORE slots than the kit item still fits (the subset rule /pvphue look uses)");
        }

        [TestMethod]
        public void ResolveItem_NoLookSourceSeam_OrAThrowingOne_SkipsWithoutThrowing()
        {
            var dials = DialsWith("10000156:red=@1007256");

            Assert.IsFalse(PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Red, dials, Valid).Applied);
            Assert.IsFalse(PvpTeamHue.ResolveItem(BlueAegis(), KitHue.Red, dials, Valid, w => throw new InvalidOperationException("db")).Applied);
        }

        [TestMethod]
        public void ResolveItem_ASwapOverride_StillRequiresAWornItemWithAClothingBaseAndNoRawRows()
        {
            var dials = DialsWith("10000156:red=@1007256");
            var packItem = BlueAegis();
            packItem.WieldLocation = null;
            var rawRows = BlueAegis();
            rawRows.Palettes.Add(new PvpTemplatePalette());

            Assert.AreEqual(HueSkipKind.NotWorn, PvpTeamHue.ResolveItem(packItem, KitHue.Red, dials, Valid, Sources(RedAegis())).Skip);
            Assert.AreEqual(HueSkipKind.RawRows, PvpTeamHue.ResolveItem(rawRows, KitHue.Red, dials, Valid, Sources(RedAegis())).Skip);
        }

        // ---------------- Apply ----------------

        [TestMethod]
        public void Apply_ASwap_CopiesEveryLookField_AndNothingElseChanges()
        {
            var source = RedAegis(s => s.Look.IgnoreCloIcons = true);
            var input = Definition(BlueAegis());
            var result = PvpTeamHue.Apply(input, KitHue.Red, DialsWith("10000156:red=@1007256,blue=2/0"), Valid, TableIcon, Sources(source));

            Assert.AreEqual(1, result.AppliedCount);
            Assert.AreEqual(0, result.Skipped.Count, string.Join("|", result.Skipped.Select(s => s.Detail)));

            var item = result.Definition.Kit[0];
            Assert.AreEqual(0x0200057Bu, item.DataIds[SetupKey], "Setup");
            Assert.AreEqual(0x1000015Au, item.DataIds[CbKey], "ClothingBase");
            Assert.AreEqual(0x04000BEFu, item.DataIds[PaletteBaseKey], "PaletteBase");
            Assert.AreEqual(14, item.Ints[PaletteKey], "PaletteTemplate");
            Assert.AreEqual(0x060012F7u, item.DataIds[IconKey], "Icon: IgnoreCloIcons on the source means the source's own icon");
            Assert.IsTrue(item.Bools[(int)PropertyBool.IgnoreCloIcons]);
            Assert.IsFalse(item.Floats.ContainsKey(ShadeKey), "the source has no Shade, so the swapped item has none (as /pvphue look)");

            // Everything outside the look is the kit item's own, untouched.
            Assert.AreEqual(0x3400002Bu, item.DataIds[22]);
            Assert.AreEqual(0.25, item.Floats[(int)PropertyFloat.AbsorbMagicDamage]);
            Assert.AreEqual(0, item.Ints[(int)PropertyInt.ArmorLevel]);
            Assert.AreEqual("PvP Arena Aegis", item.Strings[(int)PropertyString.Name]);
            Assert.AreEqual(ShieldSlot, item.WieldLocation);
            Assert.AreEqual(1006764u, item.WeenieClassId);
        }

        [TestMethod]
        public void Apply_ASwap_UsesTheClothingTableIcon_WhenTheSourceDoesNotOptOut()
        {
            var result = PvpTeamHue.Apply(Definition(BlueAegis()), KitHue.Red, DialsWith("10000156:red=@1007256"), Valid, TableIcon, Sources(RedAegis()));

            Assert.AreEqual(0x06000000u + 14u, result.Definition.Kit[0].DataIds[IconKey], "the table icon for the source's palette, as PlanLook decides");
            Assert.IsFalse(result.Definition.Kit[0].Bools.ContainsKey((int)PropertyBool.IgnoreCloIcons));
        }

        [TestMethod]
        public void Apply_ASwap_CarriesTheSourceShade()
        {
            var result = PvpTeamHue.Apply(Definition(BlueAegis()), KitHue.Red, DialsWith("10000156:red=@1007256"), Valid, TableIcon, Sources(RedAegis(s => s.Look.Shade = 0.75)));

            Assert.AreEqual(0.75, result.Definition.Kit[0].Floats[ShadeKey]);
        }

        [TestMethod]
        public void Apply_DoesNotMutateTheInput_ForASwap()
        {
            var input = Definition(BlueAegis());
            var before = Json(input);

            var result = PvpTeamHue.Apply(input, KitHue.Red, DialsWith("10000156:red=@1007256"), Valid, TableIcon, Sources(RedAegis()));

            Assert.AreNotEqual(before, Json(result.Definition), "fixture: the swap changed the clone");
            Assert.AreEqual(before, Json(input), "the input definition is byte-identical");
            Assert.AreNotSame(input, result.Definition);
            Assert.AreEqual(0x02000577u, input.Kit[0].DataIds[SetupKey]);
        }

        [TestMethod]
        public void Apply_RedAndBlueOfTheSameKit_DifferOnlyInTheSwappedItemsLook()
        {
            var chest = new PvpTemplateKitItem { WeenieClassId = 1001, WieldLocation = 0x200 };
            chest.DataIds[CbKey] = 0x1000018E;
            chest.Ints[PaletteKey] = 14;
            chest.Strings[(int)PropertyString.Name] = "Chest";
            var input = Definition(chest, BlueAegis());
            var dials = DialsWith("10000156:red=@1007256,blue=2/0");
            IReadOnlyCollection<uint> palettes(uint cb) => cb == 0x1000018E ? new uint[] { 2, 14, 85, 88, 90 } : cb == AegisCb ? new uint[] { 2, 85, 88 } : null;

            var red = PvpTeamHue.Apply(input, KitHue.Red, dials, palettes, TableIcon, Sources(RedAegis())).Definition;
            var blue = PvpTeamHue.Apply(input, KitHue.Blue, dials, palettes, TableIcon, Sources(RedAegis())).Definition;

            Assert.AreEqual(85, red.Kit[0].Ints[PaletteKey], "the chest takes the global red palette");
            Assert.AreEqual(88, blue.Kit[0].Ints[PaletteKey], "the chest takes the global blue palette");
            Assert.AreEqual(0x0200057Bu, red.Kit[1].DataIds[SetupKey]);
            Assert.AreEqual(0x1000015Au, red.Kit[1].DataIds[CbKey]);
            Assert.AreEqual(0x02000577u, blue.Kit[1].DataIds[SetupKey], "blue keeps the native blue model");
            Assert.AreEqual(AegisCb, blue.Kit[1].DataIds[CbKey]);
            Assert.AreEqual(2, blue.Kit[1].Ints[PaletteKey], "blue's explicit 2/0 palette override");
        }

        [TestMethod]
        public void Apply_ABadSource_KeepsTheItemsOwnLook_AndReportsTheReason()
        {
            var input = Definition(BlueAegis());
            var result = PvpTeamHue.Apply(input, KitHue.Red, DialsWith("10000156:red=@4242"), Valid, TableIcon, Sources(RedAegis()));

            Assert.AreEqual(0, result.AppliedCount);
            Assert.AreEqual(1, result.Skipped.Count);
            Assert.AreEqual(HueSkipKind.BadLookSource, result.Skipped[0].Kind);
            Assert.IsTrue(result.Skipped[0].Detail.Contains("@4242"));
            Assert.AreEqual(Json(input), Json(result.Definition), "the kit keeps its own look");
        }

        [TestMethod]
        public void Apply_WithHueDisabled_NothingChanges_EvenForASwap()
        {
            var input = Definition(BlueAegis());
            var result = PvpTeamHue.Apply(input, KitHue.Red, DialsWith("10000156:red=@1007256", enabled: false), Valid, TableIcon, Sources(RedAegis()));

            Assert.AreSame(input, result.Definition);
            Assert.AreEqual(0, result.AppliedCount);
        }

        // ---------------- audit and suit preview ----------------

        [TestMethod]
        public void AuditLines_ShowTheSwap_AndAgreeWithApply()
        {
            var input = Definition(BlueAegis());
            var dials = DialsWith("10000156:red=@1007256,blue=@4242");
            var lines = PvpTeamHue.AuditLines(input, dials, Valid, Sources(RedAegis()));

            Assert.AreEqual(1, lines.Count);
            Assert.IsTrue(lines[0].Contains("red swap @1007256"), lines[0]);
            Assert.IsTrue(lines[0].Contains("0x1000015A"), lines[0]);
            Assert.IsTrue(lines[0].Contains("blue skipped") && lines[0].Contains("@4242"), lines[0]);

            Assert.AreEqual(1, PvpTeamHue.Apply(input, KitHue.Red, dials, Valid, TableIcon, Sources(RedAegis())).AppliedCount, "audit and Apply agree on red");
            Assert.AreEqual(0, PvpTeamHue.Apply(input, KitHue.Blue, dials, Valid, TableIcon, Sources(RedAegis())).AppliedCount, "audit and Apply agree on blue");
        }

        [TestMethod]
        public void PlanSuit_OfASwappedKit_CopiesTheSwappedModelOntoTheWornShield()
        {
            var dressed = PvpTeamHue.Apply(Definition(BlueAegis()), KitHue.Red, DialsWith("10000156:red=@1007256"), Valid, TableIcon, Sources(RedAegis())).Definition;
            var plan = PvpTeamHue.PlanSuit(dressed, new[] { new HueWorn(77, "Shield", ShieldSlot) });

            Assert.AreEqual(1, plan.Steps.Count, string.Join("|", plan.Left));
            Assert.AreEqual(0x0200057Bu, plan.Steps[0].Source.Setup, "/pvphue suit shows the swapped model");
            Assert.AreEqual(0x1000015Au, plan.Steps[0].Source.ClothingBase);
            Assert.AreEqual(14, plan.Steps[0].Source.PaletteTemplate);
        }

        // ---------------- the world look cache ----------------

        private static void WithReader(Func<uint, Weenie> reader, Action body)
        {
            var original = PvpWorldLooks.WeenieReader;

            try
            {
                PvpWorldLooks.ClearCache();
                PvpWorldLooks.WeenieReader = reader;
                body();
            }
            finally
            {
                PvpWorldLooks.WeenieReader = original;
                PvpWorldLooks.ClearCache();
            }
        }

        private static Weenie AegisWeenie() => new Weenie
        {
            WeenieClassId = RedAegisWcid,
            ClassName = "ace1007256-pvparenaaegisred",
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "PvP Arena Aegis (Red)" },
            PropertiesDID = new Dictionary<PropertyDataId, uint>
            {
                [PropertyDataId.Setup] = 0x0200057B,
                [PropertyDataId.ClothingBase] = 0x1000015A,
                [PropertyDataId.PaletteBase] = 0x04000BEF,
                [PropertyDataId.Icon] = 0x060012F7,
            },
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.PaletteTemplate] = 14, [PropertyInt.ValidLocations] = ShieldSlot },
        };

        [TestMethod]
        public void WorldLooks_ReadsTheLookFieldsSlotsAndRawRowFlags_OfAWeenie()
        {
            WithReader(w => AegisWeenie(), () =>
            {
                var s = PvpWorldLooks.Get(RedAegisWcid);

                Assert.AreEqual("PvP Arena Aegis (Red)", s.Name);
                Assert.AreEqual(0x0200057Bu, s.Look.Setup);
                Assert.AreEqual(0x1000015Au, s.Look.ClothingBase);
                Assert.AreEqual(0x04000BEFu, s.Look.PaletteBase);
                Assert.AreEqual(0x060012F7u, s.Look.Icon);
                Assert.AreEqual(14, s.Look.PaletteTemplate);
                Assert.IsNull(s.Look.Shade);
                Assert.AreEqual(ShieldSlot, s.ValidLocations);
                Assert.IsFalse(s.HasPaletteRows || s.HasTextureMapRows || s.HasAnimPartRows);
                Assert.IsNull(s.Problem());
            });
        }

        [TestMethod]
        public void WorldLooks_NeverCachesANull_AndAThrowReadsAsNullThenRecovers()
        {
            var calls = 0;
            var weenie = AegisWeenie();

            WithReader(w =>
            {
                calls++;
                return calls == 1 ? null : calls == 2 ? throw new InvalidOperationException("db") : weenie;
            }, () =>
            {
                Assert.IsNull(PvpWorldLooks.Get(RedAegisWcid), "a missing weenie reads as null");
                Assert.IsNull(PvpWorldLooks.Get(RedAegisWcid), "a throw reads as null");
                Assert.IsNotNull(PvpWorldLooks.Get(RedAegisWcid), "the null and the throw were not remembered");
                Assert.AreEqual(3, calls, "the reader is asked on every call");
            });
        }

        [TestMethod]
        public void WorldLooks_AnUnchangedWeenieInstance_IsNotRebuilt_AChangedInstanceIs()
        {
            var a = AegisWeenie();
            var b = AegisWeenie();
            b.PropertiesInt[PropertyInt.PaletteTemplate] = 99;
            var current = a;
            var reads = 0;

            WithReader(w => { reads++; return current; }, () =>
            {
                var first = PvpWorldLooks.Get(RedAegisWcid);
                var again = PvpWorldLooks.Get(RedAegisWcid);

                Assert.AreEqual(2, reads, "the reader is consulted on every Get");
                Assert.AreSame(first, again, "the same weenie instance serves the same extraction (not rebuilt)");
                Assert.AreEqual(14, first.Look.PaletteTemplate);

                current = b;   // a live re-import hands back a fresh instance
                var reimported = PvpWorldLooks.Get(RedAegisWcid);

                Assert.AreNotSame(first, reimported);
                Assert.AreEqual(99, reimported.Look.PaletteTemplate, "the re-imported look is seen");
                Assert.AreSame(reimported, PvpWorldLooks.Get(RedAegisWcid), "and then memoized again");
            });
        }

        [TestMethod]
        public void WorldLooks_AWeenieThatDisappears_ReadsAsNull_NotAsTheStaleLook()
        {
            Weenie current = AegisWeenie();

            WithReader(w => current, () =>
            {
                Assert.IsNotNull(PvpWorldLooks.Get(RedAegisWcid));
                current = null;
                Assert.IsNull(PvpWorldLooks.Get(RedAegisWcid));
            });
        }
    }
}
