using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /charsheet parsing, reply text and dispatch (Docs/CharacterSheet/DESIGN.md). Player-facing
    /// strings are asserted verbatim (Assert.AreEqual on the full string), not just outcome shape.
    /// </summary>
    [TestClass]
    public class CharacterSheetCommandTests
    {
        private static readonly SheetLink EnabledLink = new SheetLink
        {
            Enabled = true,
            Slug = "Ab3dE5gH9k",
            Url = "https://char.example.test/Ab3dE5gH9k",
        };

        // ---- Parse ----

        [TestMethod]
        public void Parse_Empty_IsShow()
        {
            Assert.AreEqual(CharSheetCommandKind.Show, CharacterSheetCommandText.Parse(new string[0]));
            Assert.AreEqual(CharSheetCommandKind.Show, CharacterSheetCommandText.Parse(null));
        }

        [TestMethod]
        public void Parse_Off_IsOff_CaseInsensitive()
        {
            Assert.AreEqual(CharSheetCommandKind.Off, CharacterSheetCommandText.Parse(new[] { "off" }));
            Assert.AreEqual(CharSheetCommandKind.Off, CharacterSheetCommandText.Parse(new[] { "OFF" }));
            Assert.AreEqual(CharSheetCommandKind.Off, CharacterSheetCommandText.Parse(new[] { "Off" }));
        }

        [TestMethod]
        public void Parse_Rotate_IsRotate_CaseInsensitive()
        {
            Assert.AreEqual(CharSheetCommandKind.Rotate, CharacterSheetCommandText.Parse(new[] { "rotate" }));
            Assert.AreEqual(CharSheetCommandKind.Rotate, CharacterSheetCommandText.Parse(new[] { "ROTATE" }));
            Assert.AreEqual(CharSheetCommandKind.Rotate, CharacterSheetCommandText.Parse(new[] { "Rotate" }));
        }

        [TestMethod]
        public void Parse_AnythingElse_IsUsage()
        {
            Assert.AreEqual(CharSheetCommandKind.Usage, CharacterSheetCommandText.Parse(new[] { "bogus" }));
            Assert.AreEqual(CharSheetCommandKind.Usage, CharacterSheetCommandText.Parse(new[] { "off", "extra" }));
            Assert.AreEqual(CharSheetCommandKind.Usage, CharacterSheetCommandText.Parse(new[] { "rotate", "extra" }));
        }

        // ---- Reply ----

        [TestMethod]
        public void Reply_Show_NewlyCreated()
        {
            var result = new LinkResult(SheetOutcome.Ok, EnabledLink);

            Assert.AreEqual(
                "Your character sheet is now public: https://char.example.test/Ab3dE5gH9k - anyone with this link can see your equipped gear, attributes, vitals, skills, class abilities and leaderboard ranks. Use /charsheet off to hide it, or /charsheet rotate for a new link.",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Show, result, wasAlreadyOn: false));
        }

        [TestMethod]
        public void Reply_Show_AlreadyOn()
        {
            var result = new LinkResult(SheetOutcome.Ok, EnabledLink);

            Assert.AreEqual(
                "Your character sheet: https://char.example.test/Ab3dE5gH9k",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Show, result, wasAlreadyOn: true));
        }

        [TestMethod]
        public void Reply_Rotate()
        {
            var result = new LinkResult(SheetOutcome.Ok, EnabledLink);

            Assert.AreEqual(
                "Your character sheet has a new link: https://char.example.test/Ab3dE5gH9k - the old link no longer works.",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Rotate, result, wasAlreadyOn: false));
        }

        [TestMethod]
        public void Reply_Off()
        {
            var result = new LinkResult(SheetOutcome.Ok, new SheetLink { Enabled = false });

            Assert.AreEqual(
                "Your character sheet is now hidden. Its link no longer works.",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Off, result, wasAlreadyOn: false));
        }

        [TestMethod]
        public void Reply_Disabled()
        {
            var result = new LinkResult(SheetOutcome.Disabled, null);

            Assert.AreEqual(
                "Character sheets are not currently enabled on this server.",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Show, result, wasAlreadyOn: false));
        }

        [TestMethod]
        public void Reply_FailedOrBusy()
        {
            Assert.AreEqual(
                "Your character sheet could not be updated right now, try again shortly.",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Show, new LinkResult(SheetOutcome.Failed, null), wasAlreadyOn: false));

            Assert.AreEqual(
                "Your character sheet could not be updated right now, try again shortly.",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Rotate, new LinkResult(SheetOutcome.Busy, null), wasAlreadyOn: false));

            Assert.AreEqual(
                "Your character sheet could not be updated right now, try again shortly.",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Show, new LinkResult(SheetOutcome.NotFound, null), wasAlreadyOn: false));

            // Ok outcome but a null Link is treated the same as Failed/Busy.
            Assert.AreEqual(
                "Your character sheet could not be updated right now, try again shortly.",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Show, new LinkResult(SheetOutcome.Ok, null), wasAlreadyOn: false));
        }

        [TestMethod]
        public void Reply_Usage()
        {
            Assert.AreEqual(
                "Usage: /charsheet (show or create your public link), /charsheet rotate (new link), /charsheet off (hide it).",
                CharacterSheetCommandText.Reply(CharSheetCommandKind.Usage, default, wasAlreadyOn: false));
        }

        // ---- Execute (dispatch against a fake service) ----

        [TestMethod]
        public void Execute_Show_NoLinkYet_CallsGetLinkThenEnableOrRotate()
        {
            var svc = new FakeCharacterSheetService
            {
                NextLink = new LinkResult(SheetOutcome.Ok, new SheetLink { Enabled = false }),
                NextEnableOrRotate = new LinkResult(SheetOutcome.Ok, EnabledLink),
            };

            var reply = CharacterSheetCommands.Execute(svc, 0x50000001, CharSheetCommandKind.Show);

            Assert.AreEqual(1, svc.GetLinkCalls);
            Assert.AreEqual(1, svc.EnableCalls);
            Assert.IsTrue(reply.Contains("now public"));
        }

        [TestMethod]
        public void Execute_Show_AlreadyOn_DoesNotCallEnableOrRotate()
        {
            var svc = new FakeCharacterSheetService
            {
                NextLink = new LinkResult(SheetOutcome.Ok, EnabledLink),
            };

            var reply = CharacterSheetCommands.Execute(svc, 0x50000001, CharSheetCommandKind.Show);

            Assert.AreEqual(1, svc.GetLinkCalls);
            Assert.AreEqual(0, svc.EnableCalls);
            Assert.AreEqual("Your character sheet: https://char.example.test/Ab3dE5gH9k", reply);
        }

        [TestMethod]
        public void Execute_Show_GetLinkNotOk_DoesNotCallEnableOrRotate()
        {
            var svc = new FakeCharacterSheetService
            {
                NextLink = new LinkResult(SheetOutcome.Disabled, null),
            };

            var reply = CharacterSheetCommands.Execute(svc, 0x50000001, CharSheetCommandKind.Show);

            Assert.AreEqual(1, svc.GetLinkCalls);
            Assert.AreEqual(0, svc.EnableCalls);
            Assert.AreEqual("Character sheets are not currently enabled on this server.", reply);
        }

        [TestMethod]
        public void Execute_Rotate_CallsEnableOrRotateWithRotateTrue()
        {
            var svc = new FakeCharacterSheetService
            {
                NextEnableOrRotate = new LinkResult(SheetOutcome.Ok, EnabledLink),
            };

            var reply = CharacterSheetCommands.Execute(svc, 0x50000001, CharSheetCommandKind.Rotate);

            Assert.AreEqual(1, svc.EnableCalls);
            Assert.AreEqual(0, svc.GetLinkCalls);
            Assert.IsTrue(reply.Contains("new link"));
        }

        [TestMethod]
        public void Execute_Off_CallsDisable()
        {
            var svc = new FakeCharacterSheetService
            {
                NextDisable = new LinkResult(SheetOutcome.Ok, new SheetLink { Enabled = false }),
            };

            var reply = CharacterSheetCommands.Execute(svc, 0x50000001, CharSheetCommandKind.Off);

            Assert.AreEqual(1, svc.DisableCalls);
            Assert.AreEqual("Your character sheet is now hidden. Its link no longer works.", reply);
        }
    }
}
