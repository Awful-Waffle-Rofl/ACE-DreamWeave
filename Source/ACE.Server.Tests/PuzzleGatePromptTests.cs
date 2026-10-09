using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.PuzzleGates;

namespace ACE.Server.Tests
{
    /// <summary>The approach prompt's pure pieces: who is prompted, when it re-arms, when the loop runs, and the text.</summary>
    [TestClass]
    public class PuzzleGatePromptTests
    {
        private const uint A = 0x50000001;
        private const uint B = 0x50000002;

        private static bool Scan(PuzzlePromptTracker t, uint guid, float distance)
        {
            t.BeginScan();
            var prompt = t.Observe(guid, distance);
            t.EndScan();
            return prompt;
        }

        [TestMethod]
        public void PromptsOnce_OnEnteringTheRadius()
        {
            var t = new PuzzlePromptTracker();

            Assert.IsFalse(Scan(t, A, 20f), "outside the prompt radius");
            Assert.IsTrue(Scan(t, A, 11.9f), "first time inside");
            Assert.IsFalse(Scan(t, A, 5f), "already prompted");
            Assert.IsFalse(Scan(t, A, 11.9f));
        }

        [TestMethod]
        public void RearmsOnlyBeyondTheResetRadius()
        {
            var t = new PuzzlePromptTracker();
            Assert.IsTrue(Scan(t, A, 10f));

            // Between the two radii: still armed-off, so wandering at the edge never repeats the line.
            Assert.IsFalse(Scan(t, A, 20f));
            Assert.IsFalse(Scan(t, A, 10f));

            Assert.IsFalse(Scan(t, A, 25.1f), "leaving re-arms but does not prompt");
            Assert.IsTrue(Scan(t, A, 10f), "coming back prompts again");
        }

        [TestMethod]
        public void PlayersAreIndependent()
        {
            var t = new PuzzlePromptTracker();

            t.BeginScan();
            Assert.IsTrue(t.Observe(A, 5f));
            Assert.IsTrue(t.Observe(B, 6f));
            t.EndScan();

            t.BeginScan();
            Assert.IsFalse(t.Observe(A, 5f));
            Assert.IsFalse(t.Observe(B, 30f));
            t.EndScan();

            t.BeginScan();
            Assert.IsFalse(t.Observe(A, 5f), "A stays prompted");
            Assert.IsTrue(t.Observe(B, 5f), "B re-armed by leaving");
            t.EndScan();
        }

        [TestMethod]
        public void MissingPlayer_RearmsOnlyAfterTheUnseenWindow()
        {
            var t = new PuzzlePromptTracker();
            Assert.IsTrue(Scan(t, A, 5f));

            // A steps off the landblock (not observed) for one scan short of the window, then returns.
            for (var i = 0; i < PuzzleGateTunables.PromptUnseenRearmScans - 1; i++)
            {
                t.BeginScan();
                t.EndScan();
            }

            Assert.IsFalse(Scan(t, A, 5f), "a short absence (landblock edge) does not repeat the hint");

            for (var i = 0; i < PuzzleGateTunables.PromptUnseenRearmScans; i++)
            {
                t.BeginScan();
                t.EndScan();
            }

            Assert.AreEqual(0, t.PromptedCount);
            Assert.IsTrue(Scan(t, A, 5f), "a long absence re-arms");
        }

        [TestMethod]
        public void LoopRunsOnlyWhileLive_StopsWhenSolvedOrGone()
        {
            Assert.AreEqual(PuzzlePromptScan.Wait, PuzzlePromptTracker.Decide(PuzzlePlacementState.Spawning));
            Assert.AreEqual(PuzzlePromptScan.Scan, PuzzlePromptTracker.Decide(PuzzlePlacementState.Live));
            Assert.AreEqual(PuzzlePromptScan.Stop, PuzzlePromptTracker.Decide(PuzzlePlacementState.Solved), "never prompts for a solved placement");
            Assert.AreEqual(PuzzlePromptScan.Stop, PuzzlePromptTracker.Decide(PuzzlePlacementState.Failed));
            Assert.AreEqual(PuzzlePromptScan.Stop, PuzzlePromptTracker.Decide(PuzzlePlacementState.Cleared));
        }

        [TestMethod]
        public void PromptText_IsTheApprovedLinePerType()
        {
            Assert.AreEqual("A ward bars the way. Match the beam color.", PuzzleGateText.PromptFor(PuzzleGateType.Sigil, 0));
            Assert.AreEqual("A ward bars the way. Follow the beam that matches the gate's color to its lever.", PuzzleGateText.PromptFor(PuzzleGateType.Beam, 2));
            Assert.AreEqual("A ward bars the way. Follow the beam that matches the gate's color to its lever.", PuzzleGateText.PromptFor(PuzzleGateType.Beam, 4));
            Assert.AreEqual("A ward bars the way. Follow the beam to its lever.", PuzzleGateText.PromptFor(PuzzleGateType.Beam, 1));
            Assert.AreEqual("A ward bars the way. Pick the lever that is different.", PuzzleGateText.PromptFor(PuzzleGateType.Odd, 0));
            Assert.AreEqual("A ward bars the way. Find the lever.", PuzzleGateText.PromptFor(PuzzleGateType.Shuffle, 0));
        }
    }
}
