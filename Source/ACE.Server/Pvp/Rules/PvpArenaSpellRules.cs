using ACE.Entity.Enum;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// Which harmful spells are refused inside an arena match, ported from Doctide/ACE (Zaiustide/ACE
    /// @76240f84a, Player_Combat.cs CheckPKStatusVsTarget and EnchantmentManager.cs). Applies ONLY where the
    /// arena damage gate (<see cref="PvpArenaGate"/>) has already decided Allow - Void Magic before Live is
    /// already refused there ("neither in a match nor Live" fails the gate outright), so this rule does not
    /// re-check it.
    ///
    /// Pure, no live server types, so both halves (the refusal and the DoT-tick suppression below) are unit
    /// tested directly.
    /// </summary>
    public static class PvpArenaSpellRules
    {
        /// <summary>
        /// TRUE when a harmful spell is refused inside an arena match. Doctide's three refusal rules, minus
        /// the Void-before-Live one the gate already enforces, checked in DOCTIDE'S OWN ORDER (their
        /// Player_Combat.cs runs the Creature/Item Enchantment check before the "tugak" FFA check, an ORed
        /// condition where order does not change the outcome for any spell EXCEPT one that both branches
        /// would treat differently - see the CurseRavenFury note below for why that never actually happens):
        ///
        ///  - Item Enchantment: always refused.
        ///  - Creature Enchantment: refused unless its category is one of the three defense-lowering
        ///    categories (Magic/Melee/Missile Defense Lowering), which stay legal so a match still has a
        ///    debuff tool.
        ///  - FFA (their "tugak"): every harmful spell that survives the check above is refused except
        ///    <see cref="SpellId.CurseRavenFury"/>, regardless of school or category - Life Magic vulns/drains
        ///    ARE restricted here, unlike 1v1/2v2.
        ///
        /// CurseRavenFury is Life Magic / HealthLowering, verified against the client dat (see
        /// Docs/Pvp/DESIGN.md "Arena spell rules" for the command and result), so it never reaches the
        /// Creature/Item Enchantment branch above and the FFA exception always gets a chance to apply to it.
        /// Had it instead been a non-defense-lowering Creature Enchantment spell, the first branch would
        /// refuse it before the FFA exception is ever consulted, and FFA would then allow no harmful spells
        /// at all - which is Doctide's real behavior, checked in Doctide's real order, not an assumption.
        ///
        /// War Magic and Life Magic outside FFA are never refused by this rule (1v1/2v2 vulns and drains
        /// stay legal, per the owner ruling).
        /// </summary>
        public static bool IsRefused(MagicSchool school, SpellCategory category, uint spellId, bool isFfa)
        {
            if (school == MagicSchool.ItemEnchantment)
                return true;

            // Only the Creature Enchantment half of IsCountdownVuln can matter here (its Life Magic half needs school LifeMagic),
            // so this branch refuses exactly the Creature Enchantments it refused before the 2026-10-08 widening.
            if (school == MagicSchool.CreatureEnchantment && !IsCountdownVuln(school, category))
                return true;

            if (isFfa)
                return spellId != (uint)SpellId.CurseRavenFury;

            return false;
        }

        /// <summary>
        /// TRUE for the vulnerability spells an arena player may cast on an enemy during the pre-match Countdown, matched by
        /// school AND category from the spell data (never by name):
        ///  - Creature Enchantment, the three defense-lowering categories (Magic/Melee/Missile Defense Lowering): Magic Yield,
        ///    Vulnerability and Defenselessness Other. These are also the only Creature Enchantments that stay legal in an
        ///    arena match at all (<see cref="IsRefused"/>).
        ///  - Life Magic, the seven elemental vulnerability categories (Acid, Bludgeon, Cold, Electric, Fire, Pierce, Slash
        ///    Vulnerability: the "X Vulnerability Other" families) and Armor Lowering (Imperil Other). Owner ruling 2026-10-08.
        /// The single owner of that list: <see cref="IsRefused"/> and the pre-match Countdown allowance in
        /// <see cref="PvpArenaGate"/> both read it.
        /// </summary>
        public static bool IsCountdownVuln(MagicSchool school, SpellCategory category)
        {
            switch (school)
            {
                case MagicSchool.CreatureEnchantment:
                    return category == SpellCategory.MagicDefenseLowering
                        || category == SpellCategory.MeleeDefenseLowering
                        || category == SpellCategory.MissileDefenseLowering;

                case MagicSchool.LifeMagic:
                    return category == SpellCategory.AcidVulnerability
                        || category == SpellCategory.BludgeonVulnerability
                        || category == SpellCategory.ColdVulnerability
                        || category == SpellCategory.ElectricVulnerability
                        || category == SpellCategory.FireVulnerability
                        || category == SpellCategory.PierceVulnerability
                        || category == SpellCategory.SlashVulnerability
                        || category == SpellCategory.ArmorLowering;

                default:
                    return false;
            }
        }

        /// <summary>
        /// TRUE when a DoT tick from <paramref name="damagerBinding"/> onto <paramref name="targetBinding"/>
        /// deals no damage, ported from EnchantmentManager.cs's tick hook: no DoT damage inside an arena
        /// match unless the match is Live, and none at all in FFA even when Live. NotApplicable (either side
        /// unbound, or the two bindings are different matches) returns false - not this rule's call, so the
        /// tick applies unchanged.
        /// </summary>
        public static bool ShouldSuppressDotTick(PvpPlayerBinding targetBinding, PvpPlayerBinding damagerBinding, System.DateTime? nowUtc = null, ACE.Entity.Position targetAt = null)
        {
            if (targetBinding?.Match == null || damagerBinding?.Match == null)
                return false;

            if (targetBinding.Match.MatchId != damagerBinding.Match.MatchId)
                return false;

            // Battlegrounds spawn protection: a tick from ANOTHER player deals nothing inside the target's window. The same shared test
            // as the gate (PvpArenaGate.IsSpawnProtected), so a self-cast tick (one binding on both sides) is never suppressed by it.
            if (nowUtc.HasValue && PvpArenaGate.IsSpawnProtected(damagerBinding, targetBinding, nowUtc.Value, targetAt))
                return true;

            // Battlegrounds: a respawning side counts as not Live (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 7).
            if (targetBinding.Respawning || damagerBinding.Respawning)
                return true;

            if (targetBinding.Match.ModeKey == ArenaMapCatalog.FfaKey)
                return true;

            return targetBinding.State != PvpMatchState.Live || damagerBinding.State != PvpMatchState.Live;
        }
    }
}
