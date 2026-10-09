using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Queue-join admission (Docs/Pvp/DESIGN.md "Joining the queue" > "Refused when the player:"): every
    /// refusal reason, each with a control test showing an ordinary eligible player is accepted.
    /// </summary>
    [TestClass]
    public class PvpAdmissionTests
    {
        private static PvpAdmissionRequest Eligible() => new PvpAdmissionRequest(
            ArenaEnabled: true,
            ModeEnabled: true,
            AlreadyInInstance: false,
            InRespite: false,
            HasActivePkTimer: false,
            IsOlthoi: false,
            IsMule: false,
            IsDead: false,
            IsTeleporting: false,
            AlreadyQueued: false,
            AlreadyInMatch: false,
            CharacterLevel: 100,
            MinLevel: 50);

        [TestMethod]
        public void OrdinaryEligiblePlayer_IsAccepted()
        {
            Assert.AreEqual(PvpAdmissionRefusal.None, PvpAdmission.Evaluate(Eligible()));
        }

        [TestMethod]
        public void ArenaDisabled_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.ArenaDisabled, PvpAdmission.Evaluate(Eligible() with { ArenaEnabled = false }));
        }

        [TestMethod]
        public void ModeDisabled_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.ModeDisabled, PvpAdmission.Evaluate(Eligible() with { ModeEnabled = false }));
        }

        /// <summary>DISCRIMINATES: pvp_arena_denylist (ported from Doctide arenas_blacklist) refuses ahead of every other check.</summary>
        [TestMethod]
        public void Denylisted_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.Denylisted, PvpAdmission.Evaluate(Eligible() with { IsDenylisted = true }));
        }

        [TestMethod]
        public void AlreadyInInstance_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.AlreadyInInstance, PvpAdmission.Evaluate(Eligible() with { AlreadyInInstance = true }));
        }

        /// <summary>
        /// The PK facet is required only by a mode that opted out of templates (owner rulings 2026-10-03,
        /// Docs/Pvp/TEMPLATES.md): RequiresPkFacet defaults false, so a templated mode admits an off-facet player, and an
        /// untemplated one refuses it. The coordinator-level proofs are PvpMatchCoordinatorTests.Join_OffThePkFacet_IsAdmitted
        /// and BattlegroundCoordinatorTests.Koth_PkFacet_IsNotRequired.
        /// </summary>
        [TestMethod]
        public void PkFacet_RequiredOnlyWhenTheModeOptsOutOfTemplates()
        {
            Assert.AreEqual(PvpAdmissionRefusal.None, PvpAdmission.Evaluate(Eligible() with { OnPkFacet = false }), "templated (the default): admitted");
            Assert.AreEqual(PvpAdmissionRefusal.NotOnPkFacet, PvpAdmission.Evaluate(Eligible() with { RequiresPkFacet = true, OnPkFacet = false }), "opted out: refused");
            Assert.AreEqual(PvpAdmissionRefusal.None, PvpAdmission.Evaluate(Eligible() with { RequiresPkFacet = true, OnPkFacet = true }), "control");
        }

        /// <summary>DISCRIMINATES: a character on pvp_template_account is refused (an authoring source, never a fighter).</summary>
        [TestMethod]
        public void TemplateAccount_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.TemplateAccount, PvpAdmission.Evaluate(Eligible() with { IsTemplateAccount = true }));
        }

        /// <summary>DISCRIMINATES: a character still carrying a template restore record is refused until staff restore it.</summary>
        [TestMethod]
        public void StillTemplated_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.TemplateLocked, PvpAdmission.Evaluate(Eligible() with { IsPvpTemplated = true }));
        }

        [TestMethod]
        public void InRespite_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.InRespite, PvpAdmission.Evaluate(Eligible() with { InRespite = true }));
        }

        [TestMethod]
        public void ActivePkTimer_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.ActivePkTimer, PvpAdmission.Evaluate(Eligible() with { HasActivePkTimer = true }));
        }

        [TestMethod]
        public void Olthoi_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.IsOlthoi, PvpAdmission.Evaluate(Eligible() with { IsOlthoi = true }));
        }

        [TestMethod]
        public void Mule_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.IsMule, PvpAdmission.Evaluate(Eligible() with { IsMule = true }));
        }

        [TestMethod]
        public void Dead_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.IsDead, PvpAdmission.Evaluate(Eligible() with { IsDead = true }));
        }

        [TestMethod]
        public void Teleporting_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.IsTeleporting, PvpAdmission.Evaluate(Eligible() with { IsTeleporting = true }));
        }

        [TestMethod]
        public void AlreadyQueued_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.AlreadyQueued, PvpAdmission.Evaluate(Eligible() with { AlreadyQueued = true }));
        }

        [TestMethod]
        public void AlreadyInMatch_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.AlreadyInMatch, PvpAdmission.Evaluate(Eligible() with { AlreadyInMatch = true }));
        }

        [TestMethod]
        public void BelowMinLevel_Refuses()
        {
            Assert.AreEqual(PvpAdmissionRefusal.BelowMinLevel, PvpAdmission.Evaluate(Eligible() with { CharacterLevel = 49 }));
        }

        [TestMethod]
        public void AtExactlyMinLevel_IsAccepted()
        {
            Assert.AreEqual(PvpAdmissionRefusal.None, PvpAdmission.Evaluate(Eligible() with { CharacterLevel = 50 }));
        }
    }
}
