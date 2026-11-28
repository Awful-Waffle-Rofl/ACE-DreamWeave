using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.CharacterDetails.CharacterDetailsSourcePins;

namespace ACE.Server.Tests.CharacterDetails
{
    /// <summary>
    /// Exact-line pins on the Player half of the stance seam and on CharacterDetailsReader.Read. No Player can be run in
    /// this harness without poisoning it, so these lines are pinned as text: each was mutation-proven (reverted in place,
    /// seen to fail, restored) when it was written.
    /// </summary>
    [TestClass]
    public class CharacterDetailsReaderPins
    {
        private const string PlayerCombatFile = "ACE.Server/WorldObjects/Player_Combat.cs";
        private const string ServiceFile = "ACE.Server/Managers/Market/CharacterDetailsService.cs";
        private const string ReadSignature = "public static CharacterDetailsInputs Read(Player player)";

        [TestMethod]
        public void Pin_MainHandOnly_SkipsTheDualWieldAlternation()
            => AssertLineOnce(PlayerCombatFile, "public Skill GetCurrentWeaponSkill(bool mainHandOnly)",
                "if (!mainHandOnly && IsDualWieldAttack && !DualWieldAlternate)");

        [TestMethod]
        public void Pin_EffectiveAttackSkill_ReadsTheMainHandOnlySkill()
            => AssertLineOnce(PlayerCombatFile, "public uint GetEffectiveAttackSkill(CombatMode? stance, float? accuracyBar, bool mainHandOnly)",
                "var attackSkill = GetCreatureSkill(GetCurrentWeaponSkill(mainHandOnly)).Current;");

        /// <summary>Owner ruling: a stance override answers a steady combat-stance value, so it skips the jump penalty; null still reads the live jump.</summary>
        [TestMethod]
        public void Pin_StanceOverride_SkipsTheJumpPenalty()
        {
            const string sig = "public float GetDefenseStanceMod(CombatMode? stance = null)";
            AssertFirstIf(PlayerCombatFile, sig, "if (stance == null && IsJumping)");
            AssertLineOnce(PlayerCombatFile, sig, "if ((stance ?? CombatMode) != CombatMode.NonCombat)");
        }

        [TestMethod]
        public void Pin_Reader_PassesTheStanceToEveryCombatRead()
        {
            AssertLineOnce(ServiceFile, ReadSignature, "weaponAttack = player.GetEffectiveAttackSkill(stance, CharacterDetailsProjector.MissileAccuracyBar, mainHandOnly: true);");
            AssertLineOnce(ServiceFile, ReadSignature, "MeleeDefense = player.GetEffectiveDefenseSkill(CombatType.Melee, stance),");
            AssertLineOnce(ServiceFile, ReadSignature, "MissileDefense = player.GetEffectiveDefenseSkill(CombatType.Missile, stance),");
            AssertLineOnce(ServiceFile, ReadSignature, "MagicDefense = player.GetEffectiveMagicDefense(stance),");
            AssertLineOnce(ServiceFile, ReadSignature, "Profile = ProfileBuilder.ReadDefenderProfile(player, stance),");
            AssertLineOnce(ServiceFile, ReadSignature, "var spellAbsorbFront = SpellProjectile.AbsorbModFor(player, stance, withinShieldArc: true);");
            AssertLineOnce(ServiceFile, ReadSignature, "var spellAbsorbBack = SpellProjectile.AbsorbModFor(player, stance, withinShieldArc: false);");
        }

        /// <summary>Owner ruling: offense shows what the held gear uses. Each held-gear read in the reader, pinned at its exact line.</summary>
        [TestMethod]
        public void Pin_Reader_HeldGearReads()
        {
            AssertLineOnce(ServiceFile, ReadSignature, "if (skill.AdvancementClass >= SkillAdvancementClass.Trained)");
            AssertLineOnce(ServiceFile, ReadSignature, "var wand = player.GetEquippedWand();");
            // the held caster's crit skill goes through PickMagicCritSkill (Summoner focus, wield skill, then the highest of War/Void/Life), never the old War/Void pick
            AssertLineOnce(ServiceFile, ReadSignature, "var magicSkill = player.GetCreatureSkill(PickMagicCritSkill(");
            AssertLineOnce(ServiceFile, ReadSignature, "SummonerFocus.IsSummonerFocus(wand),");
            AssertLineOnce(ServiceFile, ReadSignature, "wand?.WieldSkillType,");
            AssertLineOnce(ServiceFile, ReadSignature, "player.GetCreatureSkill(Skill.LifeMagic).Current));");
            AssertNoLineContains(ServiceFile, ReadSignature, "voidSkill");
            AssertLineOnce(ServiceFile, ReadSignature, "? player.GetEquippedMissileLauncher() ?? player.GetMissileAmmo()");
            AssertLineOnce(ServiceFile, ReadSignature, ": player.GetEquippedMeleeWeapon(forceMainHand: true);");
            AssertLineOnce(ServiceFile, ReadSignature, "var offWeapon = stance == CombatMode.Melee ? player.GetDualWieldWeapon() : null;");
            AssertLineOnce(ServiceFile, ReadSignature, "var offSkill = player.GetCreatureSkill(offAttackSkill);");
            AssertNoLineContains(ServiceFile, ReadSignature, "dualWield");
            AssertLineOnce(ServiceFile, ReadSignature, "HeldCritPreResist = heldCritPre,");
            AssertLineOnce(ServiceFile, ReadSignature, "OffCritPreResist = offCritPre,");
            AssertLineOnce(ServiceFile, ReadSignature, "HeldCritDamageMod = heldCritDamageMod,");
            AssertLineOnce(ServiceFile, ReadSignature, "OffCritDamageMod = offCritDamageMod,");

            // dual wield attack: the off-hand swing's own skill, read through the non-mutating variant
            AssertLineOnce(ServiceFile, ReadSignature, "offAttack = player.GetEffectiveOffHandAttackSkill(stance, out offAttackSkill);");
            AssertLineOnce(ServiceFile, ReadSignature, "OffWeaponSkill = offAttackSkill,");
            AssertLineOnce(ServiceFile, ReadSignature, "OffWeaponAttackSkill = offAttack,");
        }

        /// <summary>The off-hand swing's skill, pinned at the lines that mirror the engine's alternation branch and write nothing.</summary>
        [TestMethod]
        public void Pin_OffHandAttackSkill_MirrorsTheAlternation_AndWritesNothing()
        {
            const string sig = "public uint GetEffectiveOffHandAttackSkill(CombatMode? stance, out Skill skillUsed)";
            AssertLineOnce(PlayerCombatFile, sig, "var offWeapon = GetDualWieldWeapon();");
            AssertLineOnce(PlayerCombatFile, sig, "var skill = offWeapon.WeaponSkill == null ? GetHighestMeleeSkill() : ConvertToMoASkill(offWeapon.WeaponSkill);");
            AssertLineOnce(PlayerCombatFile, sig, "if (dualWield.Current < weaponSkill.Current)");
            AssertLineOnce(PlayerCombatFile, sig, "skill = Skill.DualWield;");
            AssertLineOnce(PlayerCombatFile, sig, "var offenseMod = GetWeaponOffenseModifier(this, stance);");
            AssertNoLineContains(PlayerCombatFile, sig, "DualWieldAlternate");
            AssertNoLineContains(PlayerCombatFile, sig, "CombatMode =");
        }

        /// <summary>The main-hand-only reads take the main-hand WEAPON too, so the off hand is never read mid dual-wield alternation.</summary>
        [TestMethod]
        public void Pin_MainHandOnly_ReadsTheMainHandWeapon()
        {
            AssertLineOnce(PlayerCombatFile, "public Skill GetCurrentWeaponSkill(bool mainHandOnly)", "var weapon = GetEquippedWeapon(forceMainHand: mainHandOnly);");
            AssertLineOnce(PlayerCombatFile, "public uint GetEffectiveAttackSkill(CombatMode? stance, float? accuracyBar, bool mainHandOnly)", "var weapon = GetEquippedWeapon(forceMainHand: mainHandOnly);");
        }

        /// <summary>The reader never moves a live player into a stance or a loadout: those helpers are for objects outside the world.</summary>
        [TestMethod]
        public void Pin_Reader_NeverMutatesThePlayer()
        {
            foreach (var forbidden in new[] { "SimulatorStance", "SetCombatModeForSimulator", "PlayerLoadout", ".With(" })
                AssertNoLineContains(ServiceFile, ReadSignature, forbidden);
        }
    }
}
