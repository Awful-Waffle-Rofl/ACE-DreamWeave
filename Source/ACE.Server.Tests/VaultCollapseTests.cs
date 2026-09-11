using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor collapse test (DESIGN section 8, risk R6). Collapse is the ONLY place in the design
    /// where an item is destroyed and later re-created, so it is the only place degradation can occur.
    /// A false positive here silently destroys a player's tinkered gear and replaces it with a
    /// vendor-fresh copy, which is unrecoverable and would not look like a bug to anyone.
    ///
    /// Every test below is a case the spec names explicitly. Add to them; never relax them.
    /// </summary>
    [TestClass]
    public class VaultCollapseTests
    {
        private static Biota Arrow(int stackSize = 100)
        {
            return new Biota
            {
                Id = 0x80000001,
                WeenieClassId = 300,
                WeenieType = WeenieType.Ammunition,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MaxStackSize, 250 },
                    { PropertyInt.StackSize, stackSize },
                    { PropertyInt.Value, stackSize * 1 },
                    { PropertyInt.EncumbranceVal, stackSize * 6 },
                    { PropertyInt.StackUnitValue, 1 },
                    { PropertyInt.StackUnitEncumbrance, 6 },
                    { PropertyInt.ItemType, (int)ItemType.MissileWeapon },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Arrow" } },
            };
        }

        private static Biota Reference()
        {
            var b = Arrow(1);
            b.Id = 0x80000999;
            return b;
        }

        [TestMethod]
        public void PlainStackable_DifferentStackSize_IsPristine()
        {
            // The payoff case: 10,000 plain healing kits must be ONE ledger entry, not 10,000 biotas.
            // StackSize, Value and EncumbranceVal all differ from a fresh weenie by arithmetic alone.
            var diff = VaultCollapse.Diff(Arrow(100), Reference(), candidateIsStackable: true);

            CollectionAssert.AreEqual(new List<string>(), diff, string.Join(" | ", diff));
        }

        [TestMethod]
        public void TinkeredArrow_IsNotPristine()
        {
            var arrow = Arrow(100);
            arrow.PropertiesInt[PropertyInt.NumTimesTinkered] = 3;

            var diff = VaultCollapse.Diff(arrow, Reference(), candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "a tinkered arrow must keep its own biota");
        }

        [TestMethod]
        public void PartUsedHealingKit_IsNotPristine()
        {
            // Healing kits stack AND carry Structure, so a half-used kit differs from a fresh one.
            // This is the case a Stackable-based test would get wrong.
            var kit = Arrow(1);
            kit.PropertiesInt[PropertyInt.MaxStructure] = 100;
            kit.PropertiesInt[PropertyInt.Structure] = 43;

            var reference = Reference();
            reference.PropertiesInt[PropertyInt.MaxStructure] = 100;
            reference.PropertiesInt[PropertyInt.Structure] = 100;

            var diff = VaultCollapse.Diff(kit, reference, candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "a part-used kit must keep its own biota");
        }

        [TestMethod]
        public void DyedItem_IsNotPristine()
        {
            var dyed = Arrow(1);
            dyed.PropertiesInt[PropertyInt.PaletteTemplate] = 22;
            dyed.PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.Shade, 0.75 } };

            var diff = VaultCollapse.Diff(dyed, Reference(), candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "a dyed item must keep its own biota");
        }

        [TestMethod]
        public void UnknownFutureProperty_IsNotPristine()
        {
            // The whole point of diffing rather than blacklisting: a property nobody has thought of
            // yet must land on the conservative side without anyone editing this file.
            var odd = Arrow(1);
            odd.PropertiesInt[(PropertyInt)9999] = 1;

            var diff = VaultCollapse.Diff(odd, Reference(), candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "an unrecognized property must keep the item's biota");
        }

        [TestMethod]
        public void LiveEnchantment_IsNotPristine()
        {
            var buffed = Arrow(1);
            buffed.PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry> { new PropertiesEnchantmentRegistry() };

            var diff = VaultCollapse.Diff(buffed, Reference(), candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "an item carrying live enchantments is not pristine");
        }

        [TestMethod]
        public void RenamedItem_IsNotPristine()
        {
            var named = Arrow(1);
            named.PropertiesString[PropertyString.Name] = "Bob's Arrow";

            var diff = VaultCollapse.Diff(named, Reference(), candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "a renamed item must keep its own biota");
        }

        [TestMethod]
        public void GuidAndContainerAndPosition_AreNotDifferences()
        {
            var placed = Arrow(1);
            placed.Id = 0x8ABCDEF0;
            placed.PropertiesIID = new Dictionary<PropertyInstanceId, uint>
            {
                { PropertyInstanceId.Container, 0x81234567 },
                { PropertyInstanceId.Owner, 0x50000001 },
                { PropertyInstanceId.Wielder, 0x50000002 },
            };
            placed.PropertiesInt[PropertyInt.PlacementPosition] = 7;
            placed.PropertiesInt[PropertyInt.CreationTimestamp] = 1756000000;
            placed.PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.SoldTimestamp, 1756000001d } };
            placed.PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
            {
                { PositionType.Location, new PropertiesPosition { ObjCellId = 0x7F7F001C, PositionX = 12f, PositionY = 34f, PositionZ = 56f, RotationW = 1f } },
            };

            var diff = VaultCollapse.Diff(placed, Reference(), candidateIsStackable: true);

            CollectionAssert.AreEqual(new List<string>(), diff, string.Join(" | ", diff));
        }

        [TestMethod]
        public void PersistenceTimestamps_AreNotDifferences()
        {
            // The regression test for "the ledger actually fires". CheckpointTimestamp is stamped by
            // WorldObject.SaveBiotaToDatabase on every enqueued save, so almost anything a player has
            // ever held carries one, while a reference built from the weenie never does. If this is a
            // difference, the collapse test can never return true for any real item and the entire
            // wcid x count ledger is dead code.
            var saved = Arrow(1);
            saved.PropertiesFloat = new Dictionary<PropertyFloat, double>
            {
                { PropertyFloat.CheckpointTimestamp, 1756000000d },
                { PropertyFloat.ReleasedTimestamp, 1756000002d },
            };

            var diff = VaultCollapse.Diff(saved, Reference(), candidateIsStackable: true);

            CollectionAssert.AreEqual(new List<string>(), diff, string.Join(" | ", diff));
        }

        [TestMethod]
        public void CandidateMissingAPropertyTheReferenceHas_IsNotPristine()
        {
            // The other null direction: every other test populates the candidate and leaves the
            // reference bare. An item that has LOST a property is modified just as surely as one that
            // has gained one.
            var stripped = Arrow(1);
            stripped.PropertiesInt = null;

            var diff = VaultCollapse.Diff(stripped, Reference(), candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "an item missing properties the reference has is modified");
        }

        [TestMethod]
        public void ZeroStackSize_IsNotPristine()
        {
            // A zero stack satisfies both stack derivations trivially (0 == unit * 0). If the
            // exemption engaged, StackSize would be skipped and a zero-size stack would collapse into
            // a ledger row of quantity 0.
            var empty = Arrow(0);

            var diff = VaultCollapse.Diff(empty, Reference(), candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "a zero-size stack must never be judged pristine");
        }

        /// <summary>
        /// The regression guard on the WRONG fix for the icon defect.
        ///
        /// Nothing carrying a ClothingBase could collapse, because CalculateObjDesc writes the dat's
        /// sub-palette icon over the authored one on a real item's first render while a scratch
        /// reference kept the authored value. The tempting one-line fix is to exempt
        /// PropertyDataId.Icon, and it is wrong in the unrecoverable direction: Aetheria and tailoring
        /// both legitimately rewrite an item's icon, so exempting it would judge those items pristine
        /// and DESTROY them. The fix is to normalize the reference instead - see
        /// VaultCollapse.NormalizeDatDerivedState.
        /// </summary>
        [TestMethod]
        public void IconDifference_IsAlwaysADifference_AndIsNeverIgnored()
        {
            var candidate = Arrow(1);
            candidate.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Icon, 100669066 } };

            var reference = Reference();
            reference.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Icon, 100669065 } };

            var diff = VaultCollapse.Diff(candidate, reference, candidateIsStackable: true);

            Assert.AreEqual(1, diff.Count, string.Join(" | ", diff));
            StringAssert.Contains(diff[0], nameof(PropertyDataId.Icon), "an icon that has genuinely moved is a modification and must keep the item's biota");
        }

        /// <summary>
        /// The grouping rule's tolerance, asserted directly rather than only through the store.
        ///
        /// Both directions, and the negative list is the load-bearing one: this set exists so that
        /// equivalent salvage bags share a panel row, and every key NOT on it is a property a player
        /// could use to tell two rows apart.
        /// </summary>
        [TestMethod]
        public void AreGroupable_ToleratesExactlyWorkmanshipItemCountAndValue()
        {
            Assert.IsTrue(VaultCollapse.AreGroupable(Arrow(1), Reference()), "baseline: two identical biotas group");

            foreach (var key in new[] { PropertyInt.ItemWorkmanship, PropertyInt.NumItemsInMaterial, PropertyInt.Value })
            {
                var candidate = Arrow(1);
                candidate.PropertiesInt[key] = 12345;

                Assert.IsTrue(VaultCollapse.AreGroupable(candidate, Reference()), $"{key} must be a tolerated difference");
            }

            foreach (var key in new[]
                     {
                         PropertyInt.Structure, PropertyInt.MaxStructure, PropertyInt.MaterialType,
                         PropertyInt.NumTimesTinkered, PropertyInt.StackSize, PropertyInt.EncumbranceVal,
                         PropertyInt.ItemType,
                     })
            {
                var candidate = Arrow(1);
                candidate.PropertiesInt[key] = 12345;

                Assert.IsFalse(VaultCollapse.AreGroupable(candidate, Reference()), $"{key} must NOT be a tolerated difference");
            }

            var renamed = Arrow(1);
            renamed.PropertiesString[PropertyString.Name] = "Bob's Arrow";

            Assert.IsFalse(VaultCollapse.AreGroupable(renamed, Reference()), "a hand-renamed item must keep its own row");

            var enchanted = Arrow(1);
            enchanted.PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry> { new PropertiesEnchantmentRegistry() };

            Assert.IsFalse(VaultCollapse.AreGroupable(enchanted, Reference()), "a difference outside PropertiesInt entirely can never be tolerated");
        }

        [TestMethod]
        public void CoverageGuard_HoldsForTheCurrentBiotaShape()
        {
            // This is the coverage guard's only direct exercise: every other test reaches it merely as
            // a side effect of calling Diff, and only ever in its non-throwing state. Its
            // deliberate-failure run is recorded in the task report - adding a property to
            // ACE.Entity.Models.Biota makes this test fail and name that property.
            //
            // Be clear about what it does NOT protect. Deleting the AssertBiotaCoverage() call from
            // Diff leaves this test green too, because it calls the guard directly rather than through
            // Diff. No test covers deletion of that call site.
            VaultCollapse.AssertBiotaCoverage();
        }

        /// <summary>
        /// Two ordinal-adjacent string properties, which no type in ACE.Entity.Models has. The
        /// signature walker renders properties in ordinal name order, so A and B here land next to
        /// each other with nothing between them, which is the only shape in which the old quoted
        /// encoding could merge two different objects into one signature.
        ///
        /// Public, not private: the signature walker reads these through reflection from ACE.Server,
        /// and reflection invocation is access-checked against the declaring type as well as the
        /// member.
        /// </summary>
        public class AdjacentStringProbe
        {
            public string A { get; set; }
            public string B { get; set; }
        }

        [TestMethod]
        public void SignatureEncoding_CannotMergeTwoDifferentObjects()
        {
            // Asserted on the encoding directly rather than through a diff outcome, because a test
            // written through Diff passes with or without the length prefix and so pins nothing.
            //
            // Under the OLD encoding (quote + raw text + quote) both of these render exactly
            //     AdjacentStringProbe{A="p";B="q";B="r";}
            // because the separators live inside the strings. That is a FALSE POSITIVE shape: two
            // different objects compare equal, so a modified item is judged pristine and destroyed.
            // Under the length prefix they cannot collide, because the prefix fixes where each string
            // ends before its content is ever read.
            var first = new AdjacentStringProbe { A = "p", B = "q\";B=\"r" };
            var second = new AdjacentStringProbe { A = "p\";B=\"q", B = "r" };

            var firstSignature = VaultCollapse.SignatureForTests(first);
            var secondSignature = VaultCollapse.SignatureForTests(second);

            Assert.AreNotEqual(firstSignature, secondSignature,
                $"two different objects canonicalized to one signature: {firstSignature}");

            // The prefix is a length, so it must count characters, not bytes or escapes.
            StringAssert.Contains(VaultCollapse.SignatureForTests("abc"), "3:abc");

            // An empty string is a real value and must be distinguishable from a missing one.
            StringAssert.Contains(VaultCollapse.SignatureForTests(new AdjacentStringProbe { A = "", B = null }), "A=0:;");
            Assert.AreEqual("null", VaultCollapse.SignatureForTests(null));
            Assert.AreNotEqual(VaultCollapse.SignatureForTests(""), VaultCollapse.SignatureForTests(null));
        }

        [TestMethod]
        public void NonStackable_InconsistentValue_IsNotPristine()
        {
            // The stack-derived exemption is guarded, not blanket. A non-stackable whose Value differs
            // is a modification (a tinkered item's value moves), and must never be exempted.
            var item = Arrow(1);
            item.PropertiesInt[PropertyInt.Value] = 5000;

            var diff = VaultCollapse.Diff(item, Reference(), candidateIsStackable: false);

            Assert.AreNotEqual(0, diff.Count, "a non-stackable with a changed Value is modified");
        }

        [TestMethod]
        public void Stackable_ValueNotMatchingItsOwnDerivation_IsNotPristine()
        {
            var item = Arrow(100);
            item.PropertiesInt[PropertyInt.Value] = 99999;   // not StackUnitValue * StackSize

            var diff = VaultCollapse.Diff(item, Reference(), candidateIsStackable: true);

            Assert.AreNotEqual(0, diff.Count, "a stack Value that does not match its own derivation is a modification");
        }

        #region The icon defect, end to end (needs client_portal.dat)

        /// <summary>
        /// The real mod-hammer appearance, read out of ace_world 2026-08-31 for wcids 1001910-1001918:
        /// Setup 33554766, PaletteBase 67111919, ClothingBase 268435776, Icon 100669065,
        /// PaletteTemplate 20. That combination is what sends CalculateObjDesc down the
        /// ClothingSubPalEffects branch that rewrites the icon; the same shard's 32 hammer biotas all
        /// carry 100669066 instead, which is the defect this test covers.
        /// </summary>
        private const uint HammerSetup = 33554766;
        private const uint HammerPaletteBase = 67111919;
        private const uint HammerClothingBase = 268435776;
        private const uint HammerAuthoredIcon = 100669065;
        private const int HammerPaletteTemplate = 20;

        private const uint HammerProbeWcid = 990101;

        private static readonly object datInitLock = new object();

        private static bool datInitialized;
        private static string datSkipReason;
        private static bool guidManagerSeeded;

        /// <summary>
        /// Loads client_portal.dat once, or records why it could not be. Same shape and same candidate
        /// directories as BadSetupDidTests.RequireDats, which is the project's other dat consumer - the
        /// two are deliberately independent so neither test class depends on the other's run order.
        /// </summary>
        private static void RequireDats()
        {
            lock (datInitLock)
            {
                if (!datInitialized)
                {
                    datInitialized = true;
                    datSkipReason = InitializeDats();
                }
            }

            if (datSkipReason != null)
                Assert.Inconclusive(datSkipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;    // already loaded by something else in this test host

            var candidates = new List<string>();

            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                using (new FileStream(Path.Combine(datDir, "client_portal.dat"), FileMode.Open, FileAccess.Read))
                { }
            }
            catch (IOException ex)
            {
                return $"Skipped: client_portal.dat in {datDir} could not be opened for reading ({ex.Message}). Close any running client that holds the .dat files.";
            }

            try
            {
                // the dat readers use Encoding.Default (cp1252), which is not present on .NET without this
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            if (DatManager.PortalDat == null)
                return $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat.";

            if (ACE.Server.Physics.PhysicsEngine.Instance == null)
            {
                var engine = new ACE.Server.Physics.PhysicsEngine(new ACE.Server.Physics.Common.ObjectMaint(), new ACE.Server.Physics.Common.SmartBox());
                engine.Server = true;
            }

            return null;
        }

        /// <summary>
        /// Seeds GuidManager's private dynamicAlloc so the REAL WorldObjectFactory path can allocate
        /// and recycle guids without a shard database. Same technique and same reasoning as
        /// PersonalVendorTests.EnsureGuidManagerConstructible; kept local so this class does not depend
        /// on that one having run.
        /// </summary>
        private static void EnsureGuidManagerConstructible()
        {
            if (guidManagerSeeded)
                return;

            var allocatorType = typeof(GuidManager).GetNestedType("DynamicGuidAllocator", BindingFlags.NonPublic);
            Assert.IsNotNull(allocatorType, "GuidManager.DynamicGuidAllocator was not found by reflection - has it been renamed?");

            var existing = typeof(GuidManager).GetField("dynamicAlloc", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(existing, "GuidManager.dynamicAlloc was not found by reflection - has it been renamed?");

            if (existing.GetValue(null) == null)
            {
                var allocator = FormatterServices.GetUninitializedObject(allocatorType);

                void SetField(string name, object value) =>
                    allocatorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(allocator, value);

                SetField("min", ObjectGuid.DynamicMin);
                SetField("max", ObjectGuid.DynamicMax);
                SetField("current", 0x7D000000u);
                SetField("name", "test-dynamic");
                SetField("recycledGuids", Activator.CreateInstance(typeof(Queue<Tuple<DateTime, uint>>)));

                var availableIdsFieldType = allocatorType.GetField("availableIDs", BindingFlags.NonPublic | BindingFlags.Instance).FieldType;
                SetField("availableIDs", Activator.CreateInstance(availableIdsFieldType));
                SetField("useSequenceGapExhaustedMessageDisplayed", false);

                existing.SetValue(null, allocator);
            }

            guidManagerSeeded = true;
        }

        /// <summary>Puts a hammer-shaped weenie into the world cache, so no live ace_world is needed.</summary>
        private static void SeedHammerWeenie()
        {
            var weenie = new Weenie
            {
                WeenieClassId = HammerProbeWcid,
                ClassName = "vaultcollapseiconprobe",
                WeenieType = WeenieType.CraftTool,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                    { PropertyInt.PaletteTemplate, HammerPaletteTemplate },
                    { PropertyInt.EncumbranceVal, 100 },
                    { PropertyInt.MaxStackSize, 1 },
                    { PropertyInt.StackSize, 1 },
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Setup, HammerSetup },
                    { PropertyDataId.PaletteBase, HammerPaletteBase },
                    { PropertyDataId.ClothingBase, HammerClothingBase },
                    { PropertyDataId.Icon, HammerAuthoredIcon },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Icon Probe Hammer" } },
            };

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var cache = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            cache[HammerProbeWcid] = weenie;
        }

        /// <summary>Same walk-up idiom as AccountVaultStoreTests.FindInSourceTree, kept local rather than shared.</summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// The UNCONDITIONAL half of the icon fix's coverage, and it exists because the conditional
        /// half is not enough on its own.
        ///
        /// The end-to-end test below is the real proof, but it is dat-gated and goes Inconclusive on any
        /// machine without client_portal.dat - CI included. So deleting the NormalizeDatDerivedState
        /// call outright would leave the whole suite green, which is exactly the silent regression the
        /// icon defect already demonstrated once.
        ///
        /// There is no runtime seam to assert against instead. CalculateObjDesc's icon write lives
        /// behind a ClothingSubPalEffects lookup that needs DatManager.PortalDat
        /// (WorldObject_Networking.cs:988-995); with no dats loaded the method reaches AddBaseModelData
        /// and its early returns and makes no observable change to any biota at all, so no dat-free
        /// call can distinguish "normalized" from "not normalized".
        ///
        /// Hence a source-order assertion, the same instrument AccountVaultStoreTests already uses for
        /// LedgerWithdraw_WritesItsAuditRowBeforeTheDebit. BE CLEAR ABOUT ITS LIMIT: it proves the call
        /// is written in both methods ahead of the diff they feed, not that it does anything. It catches
        /// deletion and reordering, which is precisely the gap; it cannot catch the call being made
        /// against a different object or the method being emptied out.
        /// </summary>
        [TestMethod]
        public void IsPristineAndDiff_NormalizeTheirReferenceBeforeDiffingIt()
        {
            const string relativePath = "Source/ACE.Server/Entity/AccountVault/VaultCollapse.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            // Both public entry points that build a scratch reference. Neither may diff against a
            // reference it has not normalized first.
            var signatures = new[]
            {
                "public static bool IsPristine(WorldObject item)",
                "public static List<string> Diff(WorldObject item)",
            };

            foreach (var signature in signatures)
            {
                var start = code.IndexOf(signature, StringComparison.Ordinal);
                Assert.IsTrue(start >= 0, $"{signature} was not found in VaultCollapse.cs - has it been renamed? This test must follow it, not silently scan nothing.");

                // Bounded to this method, so a deletion here cannot be masked by the other method's
                // call appearing later in the file.
                var end = code.IndexOf("static", start + signature.Length, StringComparison.Ordinal);
                if (end < 0)
                    end = code.Length;

                var body = code.Substring(start, end - start);

                var iDiff = body.IndexOf("Diff(item.Biota, reference.Biota", StringComparison.Ordinal);
                Assert.IsTrue(iDiff >= 0, $"{signature} no longer diffs item.Biota against reference.Biota - this guard must be re-aimed rather than left scanning nothing.");

                var iNormalize = body.IndexOf("NormalizeDatDerivedState(reference)", StringComparison.Ordinal);

                Assert.IsTrue(iNormalize >= 0 && iNormalize < iDiff,
                    $"{signature} must call NormalizeDatDerivedState(reference) BEFORE it diffs against that reference. Without it a real item carries the dat-written icon while the scratch reference still carries the authored one, the diff is never empty, and NOTHING carrying a ClothingBase can ever collapse. Do not 'fix' that by ignoring PropertyDataId.Icon - Aetheria and tailoring both rewrite icons legitimately, and ignoring it would judge those items pristine and destroy them.");
            }
        }

        /// <summary>
        /// The defect, end to end: a ClothingBase item whose dat icon differs from its authored icon is
        /// judged PRISTINE.
        ///
        /// Before the fix, IsPristine built its reference with CreateNewWorldObject and diffed
        /// immediately, so the reference kept the AUTHORED icon while every real item had already had
        /// the DAT icon written over it by its first render. The diff was never empty and nothing
        /// carrying a ClothingBase could ever collapse - observed on the live shard as all 32 mod
        /// hammer biotas sitting on 100669066 against five weenies on 100669065.
        ///
        /// The two preconditions are asserted rather than assumed, because without them a green result
        /// here would prove nothing: the fresh object must START on the authored icon, and
        /// CalculateObjDesc must actually MOVE it.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void IsPristine_JudgesAClothingBaseItemPristineDespiteItsDatRewrittenIcon()
        {
            RequireDats();
            EnsureGuidManagerConstructible();
            SeedHammerWeenie();

            var item = WorldObjectFactory.CreateNewWorldObject(HammerProbeWcid);

            Assert.IsNotNull(item, "the probe weenie must materialize, or nothing below is being tested");
            Assert.AreEqual(HammerAuthoredIcon, item.IconId, "precondition: a fresh object starts on the AUTHORED icon");

            // The first render, which every stored item has already been through by the time it reaches
            // a vault. This is the write that used to make the item permanently un-collapsible.
            item.CalculateObjDesc();

            Assert.AreNotEqual(HammerAuthoredIcon, item.IconId,
                "precondition: the dat must really rewrite this item's icon, or this test cannot fail");

            Assert.IsTrue(VaultCollapse.IsPristine(item),
                "an otherwise untouched item must not be held back from collapsing by an icon the dat writes on both sides");

            // The control, and the half that matters: normalizing the reference must not have turned
            // IsPristine into a rubber stamp.
            item.NumTimesTinkered = 3;

            Assert.IsFalse(VaultCollapse.IsPristine(item), "a genuinely modified item must still keep its biota");
        }

        #endregion
    }
}
