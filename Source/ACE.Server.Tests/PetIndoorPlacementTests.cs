using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pet.Init's last-chance summon attempt: the owner's own spot lifted by
    /// PetFollowRules.OwnerSpotBumpHeight (Pet.cs, the `if (!success)` block after the RelocateToOwnerSpot
    /// correction).
    ///
    /// WHAT WENT WRONG. Indoors GetInFrontPlacement always declines (PetFollowRules.InFrontCandidateUsable),
    /// so `inFront` is null, the owner-spot retry is skipped, and a dungeon summon got exactly ONE placement:
    /// the owner's position copied verbatim, Z included. A standing player's Z sits a few millimetres above
    /// the floor plane under them, and at that height the placement insert refuses a physics sphere larger
    /// than a player's own while accepting the player - so a combat pet failed where its owner was standing,
    /// deterministically, forever, at the same spot.
    ///
    /// WHAT THESE TESTS COVER, and what they do not. The dat-backed pair below drives the real spawn path
    /// (WorldObject.InitPhysicsObj then WorldObject.AddPhysicsObj: AdjustDungeon, LScape.get_landcell,
    /// PhysicsObj.enter_world) at the prod failure position and shows the refusal and the remedy, with the
    /// player-sized body as the positive control. They characterise the ENGINE, so they also pass on the
    /// unfixed server - they are what establishes that the lift is the right remedy, not that Pet.Init
    /// applies it. The wiring is pinned separately by the source pins at the bottom, which are the only thing
    /// here that can see Pet.Init at all: a Player, a PetDevice and a live LandblockManager are out of reach
    /// in this project (MuleSummonTests asserts that Landblock construction throws here). The behavioural
    /// half of the fix is therefore owed a live in-game test.
    ///
    /// PhysicsEngine.Server is flipped to false for the dat-backed tests so LScape builds the landblock from
    /// client_cell.dat instead of routing through LandblockManager and a world database, and restored
    /// afterwards - the same bootstrap ExactPlacementTests uses, including its CellDat swap, because other
    /// classes in this project depend on DatManager.CellDat being null.
    ///
    /// Nothing here reads PropertyManager: OwnerSpotBumpHeight is a const and AcceptBumpedOwnerSpot is pure.
    /// </summary>
    [TestClass]
    public class PetIndoorPlacementTests
    {
        /// <summary>The human male setup. Spheres[0].Radius 0.48 at scale 1.0 - the quantity Pet.GetPetRadius reads.</summary>
        private const uint HumanSetup = 0x02000001;

        /// <summary>
        /// The prod failure position: 0x00AE0149 [84.748344 -59.842636 -17.995001], Iron Sill, where one
        /// player's pet failed 2,504 times in 105 minutes at this exact point. The cell's floor plane is at
        /// z -18.00, so the owner is standing 0.005 m above it.
        /// </summary>
        private const uint ProbeCell = 0x00AE0149;
        private const float OwnerX = 84.748344f, OwnerY = -59.842636f, OwnerZ = -17.995001f;

        /// <summary>Scales of <see cref="HumanSetup"/> and the physics sphere radius each produces.</summary>
        private const float PlayerScale = 1.0f;          // radius 0.480 - a player's own body
        private const float LargerPetScale = 1.5f;       // radius 0.720
        private const float LargestPetScale = 2.0f;      // radius 0.960

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        private static uint nextGuid = 0x7E0E0001;

        // ===================== pure: the lift =====================

        /// <summary>
        /// The lift is the one Position.InFrontOf already applies to the outdoor spot, read out of InFrontOf
        /// itself rather than restated as a literal. Fails if either value moves away from the other, which is
        /// the whole justification for the number: the outdoor path never needed this fix because it was
        /// already getting the bump for free.
        /// </summary>
        [TestMethod]
        public void OwnerSpotBumpHeight_IsTheSameLiftPositionInFrontOfApplies()
        {
            var at = new Position(ProbeCell, OwnerX, OwnerY, OwnerZ, 0f, 0f, 0f, 1f, 0);

            var inFrontLift = at.InFrontOf(0.0).PositionZ - at.PositionZ;

            Assert.AreEqual(inFrontLift, PetFollowRules.OwnerSpotBumpHeight, 1e-6f,
                "the indoor lift must stay the same one Position.InFrontOf gives the outdoor spot");
            Assert.AreEqual(0.05f, PetFollowRules.OwnerSpotBumpHeight, 1e-6f, "and that value is 0.05 m");
        }

        // ===================== pure: the landing check =====================

        /// <summary>A landing on the requested cell, a hair off the requested point, is kept.</summary>
        [TestMethod]
        public void AcceptBumpedOwnerSpot_AcceptsTheRequestedSpot()
        {
            Assert.IsTrue(PetFollowRules.AcceptBumpedOwnerSpot(
                ProbeCell, new Vector3(OwnerX, OwnerY, OwnerZ + 0.05f), 0,
                ProbeCell, new Vector3(OwnerX, OwnerY, OwnerZ), 0, true));
        }

        /// <summary>
        /// A landing in ANOTHER landblock instance is refused. This is the case that discriminates
        /// AcceptBumpedOwnerSpot from PetFollowRules.AcceptPlacement: substitute AcceptPlacement for it (which
        /// takes no instance and cannot see one, because an ObjCellID does not carry it) and this test fails
        /// while every other case here still passes.
        /// </summary>
        [TestMethod]
        public void AcceptBumpedOwnerSpot_RefusesAnotherInstance()
        {
            var requested = new Vector3(OwnerX, OwnerY, OwnerZ + 0.05f);
            var landed = new Vector3(OwnerX, OwnerY, OwnerZ);

            Assert.IsFalse(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, ProbeCell, landed, 7, true),
                "a pet in a different instance from its owner is invisible to them");
            Assert.IsFalse(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 7, ProbeCell, landed, 0, true),
                "and the same the other way round");
            Assert.IsTrue(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 7, ProbeCell, landed, 7, true),
                "control: the same two positions in the SAME non-zero instance are accepted, so the rejections above are the instance test and not the positions");
        }

        /// <summary>A landing the Slide moved out of the owner's cell, or metres away inside it, is refused.</summary>
        [TestMethod]
        public void AcceptBumpedOwnerSpot_RefusesASlideThatMovedThePet()
        {
            var requested = new Vector3(OwnerX, OwnerY, OwnerZ + 0.05f);

            Assert.IsFalse(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, 0x00AE0147, requested, 0, true),
                "another cell - typically the far side of a wall");
            Assert.IsFalse(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, ProbeCell, new Vector3(OwnerX + 1.5f, OwnerY, OwnerZ), 0, true),
                "1.5 m away, past PlacementHorizontalTolerance");
            Assert.IsFalse(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, ProbeCell, new Vector3(OwnerX, OwnerY, OwnerZ - 3f), 0, true),
                "3 m below, past PlacementVerticalTolerance");
            Assert.IsFalse(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, ProbeCell, requested, 0, false),
                "no live physics cell");
        }

        /// <summary>
        /// The lifted-spot check uses its OWN tolerance, not the in-front pair. The two landings rejected here
        /// are both comfortably inside PlacementHorizontalTolerance (1 m) and PlacementVerticalTolerance (2 m),
        /// and the final assertions show plain AcceptPlacement still accepts them - so the rejection is
        /// demonstrably the new constant and not something else in the call.
        ///
        /// The 1.5 m drop is the case that matters: one EnvCell spanning a sunken pit or a mezzanine can resolve
        /// the lifted request to the lower floor at the same cell id, and under the inherited 2 m latitude that
        /// pet would have been kept somewhere the player can neither reach nor see.
        /// </summary>
        [TestMethod]
        public void AcceptBumpedOwnerSpot_UsesItsOwnTighterToleranceNotTheInFrontOne()
        {
            var requested = new Vector3(OwnerX, OwnerY, OwnerZ + PetFollowRules.OwnerSpotBumpHeight);

            var pitFloor = new Vector3(OwnerX, OwnerY, OwnerZ - 1.5f);
            var acrossTheRoom = new Vector3(OwnerX + 0.8f, OwnerY, OwnerZ);

            Assert.IsFalse(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, ProbeCell, pitFloor, 0, true),
                "1.5 m below the owner is not the owner's spot, however much vertical latitude the in-front candidate needs");
            Assert.IsFalse(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, ProbeCell, acrossTheRoom, 0, true),
                "0.8 m sideways is not the owner's spot either");

            // inside the new bound: the transition settling the pet back onto the floor plane, which is what
            // every measured landing actually did
            Assert.IsTrue(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, ProbeCell, new Vector3(OwnerX, OwnerY, OwnerZ), 0, true),
                "the lift being absorbed is the normal outcome and must be accepted");
            Assert.IsTrue(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, ProbeCell, new Vector3(OwnerX + 0.2f, OwnerY, OwnerZ - 0.2f), 0, true),
                "and so must a small nudge well inside the tolerance");

            // the two rejections above are the tolerance, not the cell, the instance or the physics cell: the
            // in-front tolerances accept both of these landings today
            Assert.IsTrue(PetFollowRules.AcceptPlacement(ProbeCell, requested, ProbeCell, pitFloor, true),
                "control: the in-front tolerances accept the pit floor, which is why they must not be reused here");
            Assert.IsTrue(PetFollowRules.AcceptPlacement(ProbeCell, requested, ProbeCell, acrossTheRoom, true),
                "control: and they accept 0.8 m sideways too");

            Assert.IsTrue(PetFollowRules.OwnerSpotPlacementTolerance < PetFollowRules.PlacementHorizontalTolerance
                && PetFollowRules.OwnerSpotPlacementTolerance < PetFollowRules.PlacementVerticalTolerance,
                "the lifted-spot tolerance must stay tighter than both in-front tolerances");
            Assert.IsTrue(PetFollowRules.OwnerSpotPlacementTolerance > PetFollowRules.OwnerSpotBumpHeight,
                "and looser than the lift itself, or the normal landing would be refused");
        }

        // ===================== dat-backed: the refusal and the remedy =====================

        /// <summary>
        /// POSITIVE CONTROL for the two tests below. At the owner's exact Z the placement insert takes a
        /// player-sized sphere (0.480 m) and refuses the two larger ones, which is the whole failure: the
        /// owner is standing there and their pet cannot be put there. If this stops failing for the larger
        /// bodies, the harness no longer reaches the refusal and the remedy test below proves nothing.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void OwnersExactSpot_TakesAPlayerSizedBodyAndRefusesABiggerOne()
        {
            RequireDats();

            Assert.IsTrue(TrySpawn(PlayerScale, 0f, out var playerRadius, out _),
                $"a player-sized sphere ({playerRadius:0.000} m) must enter at the spot a player is standing on");
            Assert.AreEqual(0.480f, playerRadius, 1e-3f, "the control body is the human setups' own sphere");

            Assert.IsFalse(TrySpawn(LargerPetScale, 0f, out var largerRadius, out _),
                $"a {largerRadius:0.000} m sphere must be REFUSED at that same point - this is the bug");
            Assert.IsFalse(TrySpawn(LargestPetScale, 0f, out var largestRadius, out _),
                $"and so must a {largestRadius:0.000} m one");
        }

        /// <summary>
        /// The remedy: the same two bodies that the unlifted spot refuses enter when the requested point is
        /// lifted by OwnerSpotBumpHeight, and they land where they were sent - PetFollowRules.AcceptBumpedOwnerSpot
        /// on the real landed cell and origin, which is the same call Pet.Init makes before it keeps the pet.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void OwnersLiftedSpot_TakesTheBodiesTheUnliftedSpotRefuses()
        {
            RequireDats();

            foreach (var scale in new[] { LargerPetScale, LargestPetScale })
            {
                Assert.IsTrue(TrySpawn(scale, PetFollowRules.OwnerSpotBumpHeight, out var radius, out var landed),
                    $"a {radius:0.000} m sphere must enter once the spot is lifted by {PetFollowRules.OwnerSpotBumpHeight} m");

                var requested = new Vector3(OwnerX, OwnerY, OwnerZ + PetFollowRules.OwnerSpotBumpHeight);

                Assert.IsTrue(PetFollowRules.AcceptBumpedOwnerSpot(ProbeCell, requested, 0, landed.Cell, landed.Pos, 0, true),
                    $"and it must land on the owner's own cell within the placement tolerances - it landed on 0x{landed.Cell:X8} at {landed.Pos}");
            }
        }

        /// <summary>
        /// The honest limit of the remedy, kept as a test so the limit is a measured fact and not a guess: a
        /// body big enough is refused at this spot however far it is lifted. Pet.Init still reports the clean
        /// failure for those, which is the intended outcome - the fallback never forces a placement.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void OwnersLiftedSpot_StillRefusesABodyThatSimplyDoesNotFit()
        {
            RequireDats();

            Assert.IsFalse(TrySpawn(2.5f, PetFollowRules.OwnerSpotBumpHeight, out var radius, out _),
                $"a {radius:0.000} m sphere does not fit in this cell at any lift");
        }

        // ===================== source pins: the wiring =====================
        //
        // The dat-backed tests above are about the engine and the pure tests are about two helpers; between
        // them they cannot see whether Pet.Init actually makes the extra attempt, nor where in Init it sits.
        // The ordering is an invariant in its own right: an extra placement attempt inserted before the
        // retirement dismissal would dismiss the pets being replaced on a summon that then failed. Comments
        // are stripped first, so the doc comments in Pet.cs that name these very symbols cannot satisfy a pin.

        private const string PetPath = "Source/ACE.Server/WorldObjects/Pet.cs";

        private const string ExistingCorrection = "RelocateToOwnerSpot(player);";
        private const string LiftUse = "PetFollowRules.OwnerSpotBumpHeight";
        private const string LandingCheck = "PetFollowRules.AcceptBumpedOwnerSpot(";
        private const string CleanFailure = "PlacementFailed = true;";
        private const string Dismissal = "petsPendingSwapDismissal != null";

        [TestMethod]
        public void Init_MakesTheLiftedAttemptAfterTheExistingTwoAndBeforeTheCleanFailure()
        {
            var code = StrippedPetSource();

            var correction = Single(code, ExistingCorrection);
            var lift = Single(code, LiftUse);
            var check = Single(code, LandingCheck);
            var failure = Single(code, CleanFailure);

            Assert.IsTrue(correction < lift,
                "the lifted attempt must come AFTER the in-front placement, the owner-spot retry and the RelocateToOwnerSpot correction, so none of those outdoor behaviours changes");
            Assert.IsTrue(lift < check, "the attempt is made before its landing is judged");
            Assert.IsTrue(check < failure, "and the landing is judged before the clean failure reports it");
        }

        /// <summary>
        /// The GATE, not just the order. The three ordering pins read only the relative position of five
        /// literal markers, so inverting `if (!success)` to `if (success)` - which would run the lifted attempt
        /// on an already-placed pet and never run it when it is needed - leaves every marker exactly where it
        /// was and passes all of them. This pin reads the condition itself: the last `if (` between the
        /// RelocateToOwnerSpot correction and the lift must be `if (!success)`.
        ///
        /// Deliberately exact rather than a substring check, so widening the gate has to come through here and
        /// be looked at, instead of passing silently.
        /// </summary>
        [TestMethod]
        public void Init_RunsTheLiftedAttemptOnlyWhenEveryEarlierAttemptFailed()
        {
            var code = StrippedPetSource();

            var correction = Single(code, ExistingCorrection) + ExistingCorrection.Length;
            var lift = Single(code, LiftUse);

            Assert.IsTrue(correction < lift, "the lifted attempt must come after the RelocateToOwnerSpot correction");

            var span = code.Substring(correction, lift - correction);
            var lastIf = span.LastIndexOf("if (", StringComparison.Ordinal);

            Assert.IsTrue(lastIf >= 0, "no condition at all guards the lifted attempt - it would run on every summon");

            var gate = Collapse(span.Substring(lastIf));

            StringAssert.StartsWith(gate, "if (!success)",
                "the lifted attempt must be guarded by `if (!success)`. An inverted or widened gate would keep every ordering pin green while the behaviour is wrong: `if (success)` would run the extra placement on a pet that is already placed and never run it on the one that failed.");
        }

        /// <summary>Runs of whitespace squeezed to one space, so a pin reads the code and not its indentation.</summary>
        private static string Collapse(string text)
        {
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }

        [TestMethod]
        public void Init_MakesTheLiftedAttemptBeforeTheRetirementDismissal()
        {
            var code = StrippedPetSource();

            Assert.IsTrue(Single(code, LiftUse) < Single(code, Dismissal),
                "an extra placement attempt after the retirement dismissal would dismiss the replaced pets on a summon that then failed");
        }

        [TestMethod]
        public void Init_TearsDownAPetThatDidNotLandWhereItWasSent()
        {
            var code = StrippedPetSource();

            var check = Single(code, LandingCheck);
            var failure = Single(code, CleanFailure);

            var between = code.Substring(check, failure - check);

            StringAssert.Contains(between, "Destroy();",
                "a rejected landing has to be taken back out of the world, not left standing somewhere the player cannot use it");
        }

        /// <summary>The one index of <paramref name="marker"/> in <paramref name="code"/>; fails if it is absent or repeated.</summary>
        private static int Single(string code, string marker)
        {
            var at = code.IndexOf(marker, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"{PetPath} no longer contains: {marker}");
            Assert.AreEqual(-1, code.IndexOf(marker, at + marker.Length, StringComparison.Ordinal),
                $"{PetPath} contains more than one: {marker} - this pin can no longer tell which one it found");
            return at;
        }

        private static string StrippedPetSource()
        {
            var native = PetPath.Replace('/', Path.DirectorySeparatorChar);
            string path = null;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && path == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate))
                    path = candidate;
            }

            Assert.IsNotNull(path, $"Could not find {PetPath} by walking up from {AppContext.BaseDirectory} - this suite must run in-tree");

            return StripComments(File.ReadAllText(path));
        }

        /// <summary>Removes // and /* */ comments, leaving string and char literals intact.</summary>
        private static string StripComments(string code)
        {
            var sb = new StringBuilder(code.Length);
            var i = 0;

            while (i < code.Length)
            {
                var c = code[i];
                var next = i + 1 < code.Length ? code[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < code.Length && code[i] != '\n')
                        i++;
                }
                else if (c == '/' && next == '*')
                {
                    var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? code.Length : end + 2;
                }
                else if (c == '"' || c == '\'')
                {
                    var verbatim = c == '"' && i > 0 && code[i - 1] == '@';
                    sb.Append(c);
                    i++;

                    while (i < code.Length)
                    {
                        var s = code[i];
                        sb.Append(s);
                        i++;

                        if (!verbatim && s == '\\' && i < code.Length)
                        {
                            sb.Append(code[i]);
                            i++;
                        }
                        else if (s == c)
                        {
                            if (verbatim && i < code.Length && code[i] == '"')
                            {
                                sb.Append('"');
                                i++;
                            }
                            else
                                break;
                        }
                        else if (s == '\n' && !verbatim)
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            return sb.ToString();
        }

        // ===================== dat-backed helpers =====================

        /// <summary>
        /// Runs the real spawn path for an Ethereal creature whose sphere is <paramref name="scale"/> times the
        /// human setup's, at the owner's own cell and X/Y with its Z raised by <paramref name="lift"/>. Returns
        /// whether AddPhysicsObj accepted it, and always removes it again so the next case starts clean.
        /// </summary>
        private static bool TrySpawn(float scale, float lift, out float physicsRadius, out Position landed)
        {
            var wo = MakeBody(scale, lift);

            var engine = PhysicsEngine.Instance;
            var wasServer = engine.Server;
            engine.Server = false;

            try
            {
                wo.InitPhysicsObj();
                physicsRadius = wo.PhysicsObj?.GetPhysicsRadius() ?? -1f;

                var ok = wo.AddPhysicsObj();
                landed = ok ? new Position(wo.Location) : null;

                if (ok)
                    wo.PhysicsObj?.leave_world();

                return ok;
            }
            finally
            {
                engine.Server = wasServer;
            }
        }

        /// <summary>
        /// Ethereal, like every Pet (Pet.SetEphemeralValues), so the only thing that can refuse it is static
        /// geometry - which is the point: Ethereal is why the owner standing on the spot does not block their
        /// own pet, and why it does nothing at all about a dungeon wall or floor.
        /// </summary>
        private static Creature MakeBody(float scale, float lift)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999870,
                ClassName = "petindoorplacementtestbody",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Pet Placement Test Body" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, HumanSetup } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Ethereal, true } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, scale } },
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var wo = new Creature(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(ProbeCell, OwnerX, OwnerY, OwnerZ + lift, 0f, 0f, 0f, 1f, 0);
            return wo;
        }

        // ===================== dat handling =====================
        //
        // As ExactPlacementTests: this class needs client_cell_1.dat, but other classes in this project DEPEND
        // on DatManager.CellDat being null (MuleSummonTests asserts Landblock construction throws). So the cell
        // dat is opened once into a private instance and swapped into DatManager.CellDat only for the duration
        // of each test, then swapped back out in TestCleanup together with the landblock it built.

        private static CellDatDatabase ownCellDat;
        private static bool cellDatSwapped;
        private static CellDatDatabase previousCellDat;

        private static readonly System.Reflection.PropertyInfo cellDatProperty = typeof(DatManager).GetProperty(nameof(DatManager.CellDat));

        private static void RequireDats()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = InitializeDats();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);

            previousCellDat = DatManager.CellDat;
            cellDatProperty.SetValue(null, ownCellDat);
            cellDatSwapped = true;
        }

        [TestCleanup]
        public void RestoreGlobalState()
        {
            if (!cellDatSwapped)
                return;

            var engine = PhysicsEngine.Instance;
            var wasServer = engine.Server;
            engine.Server = false;

            try
            {
                ACE.Server.Physics.Common.LScape.unload_landblock(ProbeCell | 0xFFFF, 0);
            }
            finally
            {
                engine.Server = wasServer;
                cellDatProperty.SetValue(null, previousCellDat);
                previousCellDat = null;
                cellDatSwapped = false;
            }
        }

        private static string InitializeDats()
        {
            if (PhysicsEngine.Instance == null)
            {
                var engine = new PhysicsEngine(new ACE.Server.Physics.Common.ObjectMaint(), new ACE.Server.Physics.Common.SmartBox());
                engine.Server = true;
            }

            var candidates = new List<string>();

            var fromEnv = System.Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")) && File.Exists(Path.Combine(c, "client_cell_1.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat + client_cell_1.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                if (DatManager.PortalDat == null)
                    DatManager.Initialize(datDir, true, false);

                ownCellDat = new CellDatDatabase(Path.Combine(datDir, "client_cell_1.dat"), true);
            }
            catch (Exception ex)
            {
                return $"Skipped: loading the dats from {datDir} failed: {ex.Message}";
            }

            if (DatManager.PortalDat == null || ownCellDat == null)
                return $"Skipped: {datDir} did not produce both a PortalDat and a CellDat.";

            return null;
        }
    }
}
