using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using DatMotionData = ACE.DatLoader.Entity.MotionData;
using DatAnimData = ACE.DatLoader.Entity.AnimData;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The motion-stall root cause. MotionTable.GetObjectSequence's Action fallback branch (an action requested from a
    /// substate with no direct link to it) appends substate->default, default->action, default->substate and the
    /// substate cycle, and must report as numAnims exactly the link anims it appended (with the one-anim cycles used
    /// here, everything but the last, cyclic anim).
    /// MotionTableManager completes a node only after that many AnimDone hooks, so an over-count leaves the node waiting
    /// for a hook that never comes and every later motion queues behind it. Upstream counted motionData's anims a second
    /// time in place of motionData_'s; in the human table Falling->Ready is 2 anims and Ready->Falling is 1, so any
    /// attack or heal replayed on landing (HitGround while the forward command is still the action) stalled the player.
    ///
    /// Runs without the client dats: the table is built in code. Sequence.append_animation resolves each AnimId through
    /// DatManager.PortalDat, so when no dat is loaded a stand-in PortalDat is installed for the test, holding one cached
    /// empty Animation for id 0 (an empty Animation still appends), and removed again afterwards.
    /// </summary>
    [TestClass]
    public class MotionTableFallbackNumAnimsTests
    {
        private const uint Style = (uint)MotionStance.SwordShieldCombat;
        private const uint Default = (uint)MotionCommand.Ready;
        private const uint Substate = (uint)MotionCommand.Falling;
        private const uint Action = (uint)MotionCommand.ThrustMed;

        private static DatMotionData Motion(int anims)
        {
            var md = new DatMotionData();
            for (var i = 0; i < anims; i++)
                md.Anims.Add(new DatAnimData());
            return md;
        }

        /// <summary>
        /// A table where Substate has no link to Action, so GetObjectSequence takes the fallback through Default.
        /// toDefault / fromDefault are the Substate->Default and Default->Substate link lengths.
        /// </summary>
        private static MotionTable BuildTable(int toDefault, int action, int fromDefault, int cycle)
        {
            var table = new MotionTable();
            table.StyleDefaults = new Dictionary<uint, uint> { [Style] = Default };
            table.Links = new Dictionary<uint, Dictionary<uint, DatMotionData>>
            {
                // from Substate: only to Default (no Action, and no style-wide (Style << 16) bucket to fall back to)
                [(Style << 16) | (Substate & 0xFFFFFF)] = new Dictionary<uint, DatMotionData> { [Default] = Motion(toDefault) },
                // from Default: to Action, and back to Substate
                [(Style << 16) | (Default & 0xFFFFFF)] = new Dictionary<uint, DatMotionData> { [Action] = Motion(action), [Substate] = Motion(fromDefault) },
            };
            table.Cycles[(Style << 16) | (Substate & 0xFFFFFF)] = Motion(cycle);
            table.Cycles[(Style << 16) | (Default & 0xFFFFFF)] = Motion(1);
            return table;
        }

        private static (bool ok, uint numAnims, int linkAnimsAppended) RunFallback(MotionTable table)
        {
            var state = new MotionState { Style = Style, Substate = Substate, SubstateMod = 1.0f };
            var sequence = new Sequence();
            uint numAnims = 0;

            var ok = table.DoObjectMotion(Action, state, sequence, 1.0f, ref numAnims);

            // the sequence started empty, so everything but the last-appended (cyclic) anim is a link anim
            return (ok, numAnims, sequence.AnimList.Count - 1);
        }

        private static void WithPortalDat(System.Action body)
        {
            if (DatManager.PortalDat != null)
            {
                body();
                return;
            }

            var stub = (PortalDatDatabase)RuntimeHelpers.GetUninitializedObject(typeof(PortalDatDatabase));
            SetBackingField(stub, "AllFiles", new Dictionary<uint, DatFile>());
            // every AnimData built here has AnimId 0: pre-cache an empty Animation for it, so ReadFromDat returns it
            // from the cache and never reaches the (unconstructed) file reader
            var cache = new System.Collections.Concurrent.ConcurrentDictionary<uint, ACE.DatLoader.FileTypes.FileType>();
            cache[0] = new ACE.DatLoader.FileTypes.Animation();
            SetBackingField(stub, "FileCache", cache);

            var prop = typeof(DatManager).GetProperty(nameof(DatManager.PortalDat), BindingFlags.Public | BindingFlags.Static);
            prop.SetValue(null, stub);
            try
            {
                body();
            }
            finally
            {
                prop.SetValue(null, null);
            }
        }

        private static void SetBackingField(object target, string property, object value)
        {
            var field = typeof(DatDatabase).GetField($"<{property}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"DatDatabase.{property} backing field not found - the stand-in PortalDat needs updating");
            field.SetValue(target, value);
        }

        [TestMethod]
        public void Fallback_ReportsExactlyTheLinkAnimsItAppends_WhenTheTwoLinksDiffer()
        {
            // the human-table Falling shape: Falling->Ready 2 anims, Ready->Falling 1 anim
            WithPortalDat(() =>
            {
                var (ok, numAnims, appended) = RunFallback(BuildTable(toDefault: 2, action: 1, fromDefault: 1, cycle: 1));

                Assert.IsTrue(ok, "the table must reach the Action fallback branch");
                Assert.AreEqual(4, appended, "2 + 1 + 1 link anims, then the cycle");
                Assert.AreEqual((uint)appended, numAnims, $"numAnims={numAnims} but {appended} link anims were appended; the MotionTableManager node would never complete");
            });
        }

        [TestMethod]
        public void Fallback_ReportsExactlyTheLinkAnimsItAppends_WhenTheReturnLinkIsLonger()
        {
            WithPortalDat(() =>
            {
                var (ok, numAnims, appended) = RunFallback(BuildTable(toDefault: 1, action: 2, fromDefault: 3, cycle: 1));

                Assert.IsTrue(ok);
                Assert.AreEqual(6, appended);
                Assert.AreEqual((uint)appended, numAnims, $"numAnims={numAnims} but {appended} link anims were appended");
            });
        }

        [TestMethod]
        public void Fallback_EqualLinks_WasAlreadyCorrect()
        {
            // control: RunForward-shaped links (2 and 2) never stalled, which is why the bug stayed hidden
            WithPortalDat(() =>
            {
                var (ok, numAnims, appended) = RunFallback(BuildTable(toDefault: 2, action: 1, fromDefault: 2, cycle: 1));

                Assert.IsTrue(ok);
                Assert.AreEqual(5, appended);
                Assert.AreEqual((uint)appended, numAnims, $"numAnims={numAnims} but {appended} link anims were appended");
            });
        }
    }
}
