using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The Spellsword class's damage-type -> war-spell ladders, plus the shared "how high a rung can this
    /// character throw" selector. Every id here is transcribed from SPELLSWORD-DESIGN.md section 2, which
    /// read them out of the live ace_world.spell table on 2026-08-03 and cross-checked them against
    /// ACE.Entity.Enum.SpellId; the enum member names below were then resolved back from those numeric ids,
    /// so the two independent sources agree on every rung. Nothing here is derived from an id offset - see
    /// the Ring table for why that matters.
    ///
    /// WHY THIS IS A SEPARATE STATIC CLASS. Four handlers (Spellblade, Runeblade, Spellstorm, Sundermark)
    /// need the same damage-type dispatch and the same skill-to-level rule. Keeping it here makes the rule
    /// testable without constructing a Player, and makes "which rung did we pick" a pure function of
    /// (ladder, skill, rank, scale) rather than something buried in four copies of a proc handler.
    /// </summary>
    public static class SpellswordSpellTables
    {
        // ------------------------------------------------------------------------------------------------
        // Rung levels
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The actual SPELL LEVEL of each rung of the Streak ladder, parallel to every array in
        /// <see cref="StreakByType"/>.
        ///
        /// The Streak ladder IS contiguous 1-8, but do not take that on trust from the spell NAMES - level 7
        /// breaks the roman-numeral convention entirely. There is no "Flame Streak VII": the level-7 rung is
        /// a uniquely named spell per element (Sizzling Fury, Corrosive Flash, Rending Wind, Outlander's
        /// Insolence, Cameron's Curse, Sudden Frost, Lhen's Flare), exactly the way the level-7 vulnerability
        /// rung is the "X's Gift" family. The SpellId enum does carry them as ...Streak7 members.
        ///
        /// This is the same trap the vulnerability ladder sprang on ElementalRendAbility, which shipped
        /// clamped to 6 rungs on a comment asserting the ladder ended there. Verified here against
        /// portal.dat: the level-7 streaks report Power 300, which is MinPower[7], and their damage
        /// (42/42) sits exactly between level VI's 36/35 and the Incantation's 47/47. Verified they belong
        /// to the STREAK line rather than the Bolt line by that damage progression - Flame Bolt VI is
        /// 84/84, so 42/42 cannot continue it. The BOLT ladder is the one with no level 7.
        /// </summary>
        public static readonly uint[] StreakLevels = { 1, 2, 3, 4, 5, 6, 7, 8 };

        /// <summary>
        /// The actual SPELL LEVEL of each rung of the Blast ladder, parallel to every array in
        /// <see cref="BlastByType"/>. Blast has no level I or II at all (design section 2b: "Starts at III.
        /// There is no Blast I or II"), but it DOES have a level 7, under the same uniquely-named convention
        /// as the Streaks (Dissolving Vortex, Silencia's Scorn, Sau Kolin's Sword, Stinging Needles,
        /// Pummeling Storm, Winter's Embrace, Luminous Wrath). Hence {3,4,5,6,7,8} - the ladder skips only
        /// the bottom.
        ///
        /// The level-7 blasts report portal.dat Power 325, not 300; both fall in level 7's band, since
        /// MinPower[7] is 300 and MinPower[8] is 400. They carry 5 projectiles like the Incantation rather
        /// than the 3 of levels III-VI, and are distinguished from the 5-projectile VOLLEY family of the
        /// same tier by spread_Angle: Blast is 90 degrees, Volley is 0.
        /// </summary>
        public static readonly uint[] BlastLevels = { 3, 4, 5, 6, 7, 8 };

        /// <summary>
        /// The tier-I Ring block is a single rung, and it is level 5 (design section 2c). Declared as an
        /// array so it can be fed to the same selector as the other two ladders if a rank ladder is ever
        /// added; today Spellstorm is a single-rank entry and simply fires <see cref="RingByType"/>.
        /// </summary>
        public static readonly uint[] RingLevels = { 5 };

        /// <summary>
        /// The Vulnerability ladder's rung levels. Unlike the war families this one IS contiguous 1-8: the
        /// level-7 rung exists as the "X's Gift" family (Swordsman's Gift, Olthoi's Gift, ...) and level 8
        /// as "Incantation of X Vulnerability Other", so every level from 1 to 8 has a real spell. Used by
        /// Sundermark, which reads the ids themselves from ElementalRendAbility.GetVulnerabilitySpell
        /// rather than duplicating that table here.
        /// </summary>
        public static readonly uint[] VulnerabilityLevels = { 1, 2, 3, 4, 5, 6, 7, 8 };

        // ------------------------------------------------------------------------------------------------
        // Ladders
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Streak - the T1 game-changer (Spellblade). Complete for all seven physical/elemental types, which
        /// is exactly why the design picked it for tier 1: no damage type has to fall back to another.
        ///
        /// SPELLSWORD-DESIGN.md section 2a, transcribed row by row. Eight rungs: levels I-VI, then the
        /// uniquely-named level VII (2121-2147, e.g. Corrosive Flash for Acid), then the level-VIII
        /// Incantation - see <see cref="StreakLevels"/>:
        ///
        ///   Slash    (Whirling Blade) 1826 1827 1828 1829 1830 1831 2147 4458
        ///   Pierce   (Force)          1802 1803 1804 1805 1806 1807 2133 4444
        ///   Bludgeon (Shock Wave)     1820 1821 1822 1823 1824 1825 2145 4456
        ///   Cold     (Frost)          1808 1809 1810 1811 1812 1813 2137 4448
        ///   Fire     (Flame)          1796 1797 1798 1799 1800 1801 2129 4440
        ///   Acid                      1790 1791 1792 1793 1794 1795 2121 4432
        ///   Electric (Lightning)      1814 1815 1816 1817 1818 1819 2141 4452
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId[]> StreakByType = new Dictionary<DamageType, SpellId[]>
        {
            [DamageType.Slash] = new[]
            {
                SpellId.WhirlingBladeStreak1, SpellId.WhirlingBladeStreak2, SpellId.WhirlingBladeStreak3,
                SpellId.WhirlingBladeStreak4, SpellId.WhirlingBladeStreak5, SpellId.WhirlingBladeStreak6,
                SpellId.WhirlingBladeStreak7, SpellId.WhirlingBladeStreak8,
            },
            [DamageType.Pierce] = new[]
            {
                SpellId.ForceStreak1, SpellId.ForceStreak2, SpellId.ForceStreak3,
                SpellId.ForceStreak4, SpellId.ForceStreak5, SpellId.ForceStreak6,
                SpellId.ForceStreak7, SpellId.ForceStreak8,
            },
            [DamageType.Bludgeon] = new[]
            {
                SpellId.ShockwaveStreak1, SpellId.ShockwaveStreak2, SpellId.ShockwaveStreak3,
                SpellId.ShockwaveStreak4, SpellId.ShockwaveStreak5, SpellId.ShockwaveStreak6,
                SpellId.ShockwaveStreak7, SpellId.ShockwaveStreak8,
            },
            [DamageType.Cold] = new[]
            {
                SpellId.FrostStreak1, SpellId.FrostStreak2, SpellId.FrostStreak3,
                SpellId.FrostStreak4, SpellId.FrostStreak5, SpellId.FrostStreak6,
                SpellId.FrostStreak7, SpellId.FrostStreak8,
            },
            [DamageType.Fire] = new[]
            {
                SpellId.FlameStreak1, SpellId.FlameStreak2, SpellId.FlameStreak3,
                SpellId.FlameStreak4, SpellId.FlameStreak5, SpellId.FlameStreak6,
                SpellId.FlameStreak7, SpellId.FlameStreak8,
            },
            [DamageType.Acid] = new[]
            {
                SpellId.AcidStreak1, SpellId.AcidStreak2, SpellId.AcidStreak3,
                SpellId.AcidStreak4, SpellId.AcidStreak5, SpellId.AcidStreak6,
                SpellId.AcidStreak7, SpellId.AcidStreak8,
            },
            [DamageType.Electric] = new[]
            {
                SpellId.LightningStreak1, SpellId.LightningStreak2, SpellId.LightningStreak3,
                SpellId.LightningStreak4, SpellId.LightningStreak5, SpellId.LightningStreak6,
                SpellId.LightningStreak7, SpellId.LightningStreak8,
            },
        };

        /// <summary>
        /// Blast - the T2 game-changer (Runeblade). A 90 degree cone: one projectile lands on the centre
        /// target and the rest impact-check other monsters, so Blast's single-target damage equals Streak's
        /// at the same level and everything it adds over Spellblade is cleave (design section 2b / Q1).
        ///
        /// SPELLSWORD-DESIGN.md section 2b, transcribed row by row. THE LADDER STARTS AT LEVEL III - there
        /// is no Blast I or II - and its top rung is the level-VIII Incantation, so a Blast array has five
        /// entries where a Streak array has seven. See <see cref="BlastLevels"/>.
        ///
        ///   Slash    (Blade)      123 124 125 126 2124 4435
        ///   Pierce   (Force)      119 120 121 122 2131 4442
        ///   Bludgeon (Shock)      103 104 105 106 2143 4454
        ///   Cold     (Frost)      107 108 109 110 2135 4446
        ///   Fire     (Flame)      115 116 117 118 2127 4438
        ///   Acid                   99 100 101 102 2120 4431
        ///   Electric (Lightning)  111 112 113 114 2139 4450
        ///
        /// Several Blast rows have exact-duplicate spell rows in the world DB (Acid Blast III exists at both
        /// 99 and 3653); the design ruled that the LOWEST id is the canonical player-facing row, which is
        /// what the enum members below resolve to.
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId[]> BlastByType = new Dictionary<DamageType, SpellId[]>
        {
            [DamageType.Slash] = new[]
            {
                SpellId.BladeBlast3, SpellId.BladeBlast4, SpellId.BladeBlast5, SpellId.BladeBlast6,
                SpellId.BladeBlast7, SpellId.BladeBlast8,
            },
            [DamageType.Pierce] = new[]
            {
                SpellId.ForceBlast3, SpellId.ForceBlast4, SpellId.ForceBlast5, SpellId.ForceBlast6,
                SpellId.ForceBlast7, SpellId.ForceBlast8,
            },
            [DamageType.Bludgeon] = new[]
            {
                SpellId.ShockBlast3, SpellId.ShockBlast4, SpellId.ShockBlast5, SpellId.ShockBlast6,
                SpellId.ShockBlast7, SpellId.ShockBlast8,
            },
            [DamageType.Cold] = new[]
            {
                SpellId.FrostBlast3, SpellId.FrostBlast4, SpellId.FrostBlast5, SpellId.FrostBlast6,
                SpellId.FrostBlast7, SpellId.FrostBlast8,
            },
            [DamageType.Fire] = new[]
            {
                SpellId.FlameBlast3, SpellId.FlameBlast4, SpellId.FlameBlast5, SpellId.FlameBlast6,
                SpellId.FlameBlast7, SpellId.FlameBlast8,
            },
            [DamageType.Acid] = new[]
            {
                SpellId.AcidBlast3, SpellId.AcidBlast4, SpellId.AcidBlast5, SpellId.AcidBlast6,
                SpellId.AcidBlast7, SpellId.AcidBlast8,
            },
            [DamageType.Electric] = new[]
            {
                SpellId.LightningBlast3, SpellId.LightningBlast4, SpellId.LightningBlast5, SpellId.LightningBlast6,
                SpellId.LightningBlast7, SpellId.LightningBlast8,
            },
        };

        /// <summary>
        /// Ring - the T3 game-changer (Spellstorm). Nine projectiles at spread angle 360, radiating outward,
        /// so one target takes roughly one projectile and a surrounding pack takes one each. ONE fixed rung
        /// per damage type: the user's ruling is that Spellstorm fires only the tier-I ring, which is a
        /// shape change rather than a ladder, and the tier-I block is stat-identical (42/42, avg 63) across
        /// all seven types, so no normalization is needed.
        ///
        /// SPELLSWORD-DESIGN.md section 2c. THE IDS ARE NOT IN DAMAGE-TYPE ORDER, and the design says so
        /// explicitly: "the tier-I ids are not in damage-type order (Acid 1783, Slash 1784, Fire 1785,
        /// Pierce 1786, Cold 1787, Electric 1788, Bludgeon 1789), so the lookup table must be written out,
        /// not computed from an offset". Each row below pairs the design's thematic name with the id and the
        /// enum member that id resolves to:
        ///
        ///   Acid     Searing Disc            1783 -> SpellId.AcidRing
        ///   Slash    Horizon's Blades        1784 -> SpellId.BladeRing
        ///   Fire     Cassius' Ring of Fire   1785 -> SpellId.FlameRing
        ///   Pierce   Nuhmudira's Spines      1786 -> SpellId.ForceRing
        ///   Cold     Halo of Frost           1787 -> SpellId.FrostRing
        ///   Electric Eye of the Storm        1788 -> SpellId.LightningRing
        ///   Bludgeon Tectonic Rifts          1789 -> SpellId.ShockwaveRing
        ///
        /// Do not go looking for these by name: the family is named thematically, so a search for "Ring"
        /// finds neither Tectonic Rifts nor Halo of Frost, and the lesser "X Ring" rows (3805-3808) it does
        /// find are a different, incomplete set.
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId> RingByType = new Dictionary<DamageType, SpellId>
        {
            [DamageType.Acid]     = SpellId.AcidRing,
            [DamageType.Slash]    = SpellId.BladeRing,
            [DamageType.Fire]     = SpellId.FlameRing,
            [DamageType.Pierce]   = SpellId.ForceRing,
            [DamageType.Cold]     = SpellId.FrostRing,
            [DamageType.Electric] = SpellId.LightningRing,
            [DamageType.Bludgeon] = SpellId.ShockwaveRing,
        };

        // ------------------------------------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// TRUE when the weapon that actually landed THIS hit is a light weapon.
        ///
        /// READ THE WEAPON, NOT THE CURRENT ATTACK SKILL. The obvious implementation - and the one this
        /// class shipped with first - was "attacker.GetCurrentWeaponSkill() != Skill.LightWeapons", copying
        /// how Eagle Eye tests for a missile weapon. That is WRONG for a dual-wielding Spellsword, and
        /// silently so: Player.GetCurrentWeaponSkill returns Skill.DualWield instead of the weapon's own
        /// skill whenever the swing is an OFFHAND attack and Dual Wield is the lower skill
        /// (Player_Combat.cs, the IsDualWieldAttack + !DualWieldAlternate branch). Every offhand swing from
        /// such a character would fail the gate and proc nothing, with no message and no log line.
        ///
        /// DamageEvent.Weapon is the correct signal because it is resolved PER HIT:
        /// attacker.GetEquippedMeleeWeapon() returns the OFFHAND weapon on an offhand attack
        /// (Creature_Equipment.cs forwards to GetDualWieldWeapon unless the swing is main-hand), so this
        /// reads whichever blade actually connected. A fire main-hand and a lightning off-hand therefore
        /// proc fire and lightning respectively, which is also the cheapest way to SEE per-hand behaviour
        /// in game.
        /// </summary>
        public static bool IsLightWeaponHit(Player attacker, WorldObject weapon)
        {
            if (attacker == null || weapon == null)
                return false;

            return attacker.ConvertToMoASkill(weapon.WeaponSkill) == Skill.LightWeapons;
        }

        /// <summary>        /// The highest spell LEVEL a given rank of a war proc may throw.
        ///
        /// The design's cap, literally: 3 / 5 / 7 (SPELLSWORD-DESIGN.md sections 1, 3).
        ///
        /// The level-8 Incantation rung is therefore DELIBERATELY OUT OF REACH of the proc. It stays in the
        /// ladders because the ladders describe the spell family truthfully, not because anything can throw
        /// it today; raising this cap to 8, or scaling the thresholds, is the only way to reach it. An
        /// earlier revision capped rank 3 at 8 on the false premise that war magic had no level 7 - it does,
        /// under uniquely named spells rather than roman numerals. See <see cref="StreakLevels"/>.
        /// </summary>
        public static uint MaxLevelForRank(int rank)
        {
            if (rank <= 1)
                return 3;
            if (rank == 2)
                return 5;
            return 7;
        }

        /// <summary>
        /// The engine's own Power floor for a spell level, mirrored from ACE.Server.Entity.SpellFormula's
        /// MinPower table (SpellFormula.cs:95-105), verified against source at write time:
        ///
        ///   level    1   2    3    4    5    6    7    8
        ///   MinPower 1  50  100  150  200  250  300  400
        ///
        /// Mirrored rather than referenced so this table stays a pure, dependency-free function for tests,
        /// and so an out-of-range level returns a defined value instead of throwing out of a combat hook.
        /// The design's rule (section 1d) is "a proc fires the highest level whose MinPower your relevant
        /// magic skill's Current meets" - so the number returned here is compared directly against a
        /// CreatureSkill.Current, which is the whole reason nothing new had to be tuned.
        /// </summary>
        public static uint MinPowerForLevel(uint level)
        {
            switch (level)
            {
                case 1: return 1;
                case 2: return 50;
                case 3: return 100;
                case 4: return 150;
                case 5: return 200;
                case 6: return 250;
                case 7: return 300;
                case 8: return 400;
            }

            // Above the retail ceiling: unreachable by any ladder in this file, but a defined answer beats
            // an exception thrown from inside a melee hit.
            return level > 8 ? 400u : 1u;
        }

        /// <summary>
        /// Picks which rung of a ladder a character can throw: the HIGHEST rung whose level is within
        /// <paramref name="maxLevel"/> and whose <see cref="MinPowerForLevel"/> threshold (scaled by
        /// <paramref name="thresholdScale"/>) is met by <paramref name="magicSkillCurrent"/>.
        ///
        /// Returns an INDEX into the parallel arrays rather than a level, because the ladders are not
        /// contiguous - index 6 of a Streak array is level 8, not level 7.
        ///
        /// When no rung qualifies (a character whose magic skill is below even the lowest rung's threshold),
        /// this returns 0, the lowest rung. That is deliberate: a Spellsword who has bought the ability
        /// always procs something. The alternative - a silent no-op below some skill floor - would make the
        /// entry look broken to exactly the players least able to diagnose it, and the level-1 rungs are
        /// worth ~23 damage, so the floor costs nothing in balance terms.
        /// </summary>
        public static int SelectRungIndex(uint[] rungLevels, uint maxLevel, uint magicSkillCurrent, double thresholdScale)
        {
            if (rungLevels == null || rungLevels.Length == 0)
                return 0;

            for (var i = rungLevels.Length - 1; i >= 0; i--)
            {
                var level = rungLevels[i];

                if (level > maxLevel)
                    continue;

                if (MinPowerForLevel(level) * thresholdScale <= magicSkillCurrent)
                    return i;
            }

            return 0;
        }

        /// <summary>
        /// The spell a proc should cast: the damage type's ladder entry at the rung
        /// <see cref="SelectRungIndex"/> picks, capped by <see cref="MaxLevelForRank"/>.
        ///
        /// Returns NULL when <paramref name="damageType"/> is not one of the seven physical/elemental types
        /// the ladders cover. DamageEvent.DamageType can also be Health, Stamina, Mana, Nether or Base for
        /// exotic sources; those must proc nothing, silently and with no message (design section 1b's
        /// guard). Callers treat null as "no proc this swing".
        ///
        /// Pure and static so the ladder rule can be unit-tested without a Player, a Creature, or a live
        /// spell table.
        /// </summary>
        public static SpellId? SelectSpell(IReadOnlyDictionary<DamageType, SpellId[]> ladder, uint[] rungLevels,
            DamageType damageType, uint magicSkillCurrent, int rank, double thresholdScale)
        {
            if (ladder == null || rungLevels == null)
                return null;

            if (!ladder.TryGetValue(damageType, out var rungs) || rungs == null || rungs.Length == 0)
                return null;

            var index = SelectRungIndex(rungLevels, MaxLevelForRank(rank), magicSkillCurrent, thresholdScale);

            // Defensive: the parallel arrays are the same length by construction, so this only bites if a
            // future edit lengthens one without the other.
            if (index >= rungs.Length)
                index = rungs.Length - 1;

            return rungs[index];
        }
    }
}
