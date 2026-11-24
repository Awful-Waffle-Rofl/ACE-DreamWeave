using System;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Source pins for the support credit (playability review P4): GauntletHooks.OnBeneficialAct is called after the resist
    /// check and before the spell lands (WorldObject.TryCastSpell_Inner), and at the start of both kit paths that heal another
    /// member or the fighter (Healer.DoHealing, DoHealingGauntletFighter). Each checker returns the first fault; the real source
    /// must pass and each mutated copy must fault, so the pins are mutation-proved inside the test.
    /// </summary>
    [TestClass]
    public class GauntletSupportHookSiteTests
    {
        private const string MagicPath = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";
        private const string HealerPath = "Source/ACE.Server/WorldObjects/Healer.cs";

        private const string InnerSig = "public void TryCastSpell_Inner(Spell spell, WorldObject target, WorldObject itemCaster = null, WorldObject weapon = null, bool isWeaponSpell = false, bool fromProc = false, bool tryResist = true)";
        private const string SpellCall = "ACE.Server.Avatars.Gauntlet.GauntletHooks.OnBeneficialAct(this, target, spell.IsBeneficial);";
        private const string Resist = "if (tryResist && TryResistSpell(target, spell, itemCaster))\n                return;";
        private const string Cast = "HandleCastSpell(spell, target, itemCaster, weapon, isWeaponSpell, fromProc);";

        private const string KitSig = "public void DoHealing(Player healer, Player target, uint missingVital)";
        private const string KitCall = "ACE.Server.Avatars.Gauntlet.GauntletHooks.OnBeneficialAct(healer, target, true);";
        private const string FighterKitSig = "private void DoHealingGauntletFighter(Player healer, ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter fighter)";
        private const string FighterKitCall = "ACE.Server.Avatars.Gauntlet.GauntletHooks.OnBeneficialAct(healer, fighter, true);";

        internal static string SpellFault(string body)
        {
            var resist = body.IndexOf(Resist, StringComparison.Ordinal);
            var call = body.IndexOf(SpellCall, StringComparison.Ordinal);
            var cast = body.IndexOf(Cast, StringComparison.Ordinal);

            if (call < 0) return "no support call in TryCastSpell_Inner";
            if (resist < 0 || call < resist) return "the support call is ahead of the resist check (a resisted spell would count)";
            if (cast < call) return "the support call is after the spell lands";
            return null;
        }

        internal static string KitFault(string body, string call)
        {
            var at = body.IndexOf(call, StringComparison.Ordinal);
            var uses = body.IndexOf("UsesLeft--", StringComparison.Ordinal);

            if (at < 0) return "no support call in the kit path";
            if (uses >= 0 && uses < at) return "the support call is after the kit's use is spent";
            return null;
        }

        [TestMethod]
        public void Spell_SupportCredit_AfterResist_BeforeTheCast_MutationProved()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MagicPath), InnerSig);

            Assert.IsNull(SpellFault(body), "the real source passes");
            Assert.IsNotNull(SpellFault(body.Replace(SpellCall, "")), "no call faults");
            Assert.IsNotNull(SpellFault(body.Replace(SpellCall, "").Replace(Resist, SpellCall + "\n            " + Resist)), "a call ahead of the resist faults");
            Assert.IsNotNull(SpellFault(body.Replace(SpellCall, "").Replace(Cast, Cast + "\n            " + SpellCall)), "a call after the cast faults");
        }

        private const string MagicTargetPath = "Source/ACE.Server/WorldObjects/Player_Magic.cs";
        private const string HeldHead = "if (ACE.Server.Avatars.Gauntlet.GauntletHooks.RefusesHeldSupport(this, target, spell.IsBeneficial))\n                return true;";
        private const string FighterHead = "if (ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter.IsInvalidFighterSpellTarget(targetCreature, spell, out var fighterInvalid))";

        internal static string HeldFault(string source)
        {
            var held = source.IndexOf(HeldHead, StringComparison.Ordinal);
            var fighter = source.IndexOf(FighterHead, StringComparison.Ordinal);

            if (held < 0) return "no held-support refusal in the spell target check";
            if (source.IndexOf(HeldHead, held + 1, StringComparison.Ordinal) >= 0) return "the held-support refusal appears twice";
            if (fighter < held) return "the held-support refusal is after the fighter's opt-in (a held member could buff the fighter)";
            return null;
        }

        [TestMethod]
        public void HeldSupport_RefusedInTheSpellTargetCheck_MutationProved()
        {
            var source = PooledLootSourceText.Read(MagicTargetPath);

            Assert.IsNull(HeldFault(source), "the real source passes");
            Assert.IsNotNull(HeldFault(source.Replace(HeldHead, "")), "no refusal faults");
            Assert.IsNotNull(HeldFault(source.Replace(HeldHead, "").Replace(FighterHead, "{ }\n            " + HeldHead + "\n            " + FighterHead)
                .Replace("{ }\n            " + HeldHead + "\n            " + FighterHead, FighterHead + "\n                return fighterInvalid;\n            " + HeldHead)), "a refusal after the fighter's opt-in faults");
        }

        [TestMethod]
        public void Kits_SupportCredit_OnMembersAndTheFighter_MutationProved()
        {
            var source = PooledLootSourceText.Read(HealerPath);
            var kit = PooledLootSourceText.MethodBody(source, KitSig);
            var fighterKit = PooledLootSourceText.MethodBody(source, FighterKitSig);

            Assert.IsNull(KitFault(kit, KitCall), "the member kit path passes");
            Assert.IsNull(KitFault(fighterKit, FighterKitCall), "the fighter kit path passes");
            Assert.IsNotNull(KitFault(kit.Replace(KitCall, ""), KitCall), "no call faults");
            Assert.IsNotNull(KitFault(fighterKit.Replace(FighterKitCall, "").Replace("UsesLeft--;", "UsesLeft--; " + FighterKitCall), FighterKitCall), "a call after the use is spent faults");
        }
    }
}
