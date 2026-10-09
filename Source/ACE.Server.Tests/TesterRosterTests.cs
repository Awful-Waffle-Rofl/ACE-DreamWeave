using System;
using System.Linq;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers the fixed slot table that prod character seeding writes into. Every value here is
    /// load-bearing: the guids are reused by every refresh, so a change silently renames or
    /// re-points live characters rather than failing.
    /// </summary>
    [TestClass]
    public class TesterRosterTests
    {
        [TestMethod]
        public void SourceSlots_AreTenContiguousGuidsFromSlotBase()
        {
            CollectionAssert.AreEqual(
                new[] { 0x5A000000u, 0x5A000001u, 0x5A000002u, 0x5A000003u, 0x5A000004u,
                        0x5A000005u, 0x5A000006u, 0x5A000007u, 0x5A000008u, 0x5A000009u },
                TesterRoster.SourceSlots.ToArray());
        }

        [TestMethod]
        public void TesterSlots_AreFifty()
        {
            Assert.AreEqual(50, TesterRoster.TesterSlots.Count);
        }

        [TestMethod]
        public void TesterSlots_UseSixteenGuidStridePerAccount()
        {
            var alpha = TesterRoster.TesterSlots.Where(s => s.AccountIndex == 0).ToList();
            var bravo = TesterRoster.TesterSlots.Where(s => s.AccountIndex == 1).ToList();

            Assert.AreEqual(0x5A000100u, alpha[0].Guid);
            Assert.AreEqual(0x5A000109u, alpha[9].Guid);
            Assert.AreEqual(0x5A000110u, bravo[0].Guid);
            Assert.AreEqual(0x5A000149u, TesterRoster.TesterSlots.Last().Guid);
        }

        [TestMethod]
        public void EveryGuidIsUnique()
        {
            var all = TesterRoster.SourceSlots.Concat(TesterRoster.TesterSlots.Select(s => s.Guid)).ToList();

            Assert.AreEqual(60, all.Count);
            Assert.AreEqual(60, all.Distinct().Count());
        }

        [TestMethod]
        public void EveryNameIsUnique()
        {
            var names = TesterRoster.TesterSlots.Select(s => s.Name).ToList();

            Assert.AreEqual(50, names.Distinct().Count());
        }

        [TestMethod]
        public void SlotNameIsSlotWordThenAccountWord()
        {
            var first = TesterRoster.TesterSlots.First();
            var last = TesterRoster.TesterSlots.Last();

            Assert.AreEqual("Kestrel Alpha", first.Name);
            Assert.AreEqual("Vellum Echo", last.Name);
        }

        [TestMethod]
        public void EveryTesterSlotRankMapsToASourceSlot()
        {
            foreach (var slot in TesterRoster.TesterSlots)
            {
                Assert.IsTrue(slot.Rank >= 0 && slot.Rank < TesterRoster.SourceSlots.Count);
                Assert.AreEqual(TesterRoster.SourceSlots[slot.Rank], TesterRoster.SourceSlotForRank(slot.Rank));
            }
        }

        /// <summary>
        /// The flag on @refreshtestroster is not cosmetic: it decides whether the command can be
        /// invoked at all from the server console, which is the ONLY way
        /// .github/workflows/seed-stage-roster.yml runs it.
        ///
        /// RequiresWorld makes GetCommandHandler answer NotInWorld for every session-less invocation,
        /// and the console loop always passes a null session - so under that flag the handler body was
        /// unreachable from the console in every server state, and the workflow's injection was a
        /// silent no-op. ConsoleInvoke is the opposite mistake: it means console-ONLY, and would break
        /// the in-game @refreshtestroster that Docs/ProdCharSeed/RUNBOOK.md tells an operator to type.
        /// None is the only value that serves both, so it is pinned here.
        /// </summary>
        [TestMethod]
        public void RefreshTestRosterCommand_IsInvocableFromBothTheConsoleAndInGame()
        {
            // GetCustomAttributes (plural) because a handler may carry several, one per alias, and the
            // singular overload throws AmbiguousMatchException on those. CommandManager reads them the
            // same way, so this walks exactly what the server registers.
            var handler = typeof(TesterRosterCommands)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SelectMany(m => m.GetCustomAttributes<CommandHandlerAttribute>(), (m, a) => new { Method = m, Attribute = a })
                .SingleOrDefault(x => string.Equals(x.Attribute.Command, "refreshtestroster", StringComparison.OrdinalIgnoreCase));

            Assert.IsNotNull(handler, "No refreshtestroster command handler is declared on TesterRosterCommands.");

            Assert.AreEqual(CommandHandlerFlag.None, handler.Attribute.Flags,
                "refreshtestroster must be CommandHandlerFlag.None. RequiresWorld makes it unreachable from " +
                "the server console (seed-stage-roster.yml injects it there); ConsoleInvoke would block the " +
                "in-game invocation the runbook documents.");

            Assert.AreEqual(AccessLevel.Admin, handler.Attribute.Access,
                "refreshtestroster purges and re-copies fifty characters and stays admin-only.");

            Assert.AreEqual(0, handler.Attribute.ParameterCount,
                "refreshtestroster takes no arguments; the workflow injects the bare command name.");
        }

        [TestMethod]
        public void AllSlotGuidsSitInThePlayerGuidRange()
        {
            foreach (var guid in TesterRoster.SourceSlots.Concat(TesterRoster.TesterSlots.Select(s => s.Guid)))
            {
                Assert.IsTrue(guid >= ACE.Entity.ObjectGuid.PlayerMin, $"0x{guid:X8} below PlayerMin");
                Assert.IsTrue(guid <= ACE.Entity.ObjectGuid.PlayerMax, $"0x{guid:X8} above PlayerMax");
            }
        }
    }
}
