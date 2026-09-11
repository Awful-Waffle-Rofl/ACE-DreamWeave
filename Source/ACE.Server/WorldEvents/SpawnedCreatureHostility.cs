using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Makes a roster-drawn creature fight the PLAYER rather than the rest of its own wave.
    ///
    /// Shared by World Events and Threads because they share the roster: both draw their monsters from the
    /// species tables under Content/events/axes/species (see DungeonRosterSelector's remarks), those tables
    /// are mined from retail content, and retail carries three separate mechanisms for one monster to attack
    /// another. It lives in the World Events namespace because that is the lower layer - ACE.Server.ThreadDungeons
    /// already depends on ACE.Server.WorldEvents and not the reverse.
    ///
    /// The three mechanisms, measured against ace_world on 2026-09-07 over the 1,544 wcids in those tables:
    ///
    ///  - Tolerance NoAttack or Monster, on 10 members. See <see cref="BannedTolerance"/>.
    ///  - Faction1Bits, on 11 members. ObjectMaint.ApplyFilter hands a faction mob EVERY other monster that
    ///    is not of its own faction as an attack target, and hands every non-faction monster the faction mob
    ///    back (WeenieObject.SameFaction is a bit AND, so "no faction" is never the same faction as "some
    ///    faction"). The values in the tables are 8, 16, 32, 64, 128 and 1024 - all outside
    ///    FactionBits.ValidFactions (0x7) - but ACE tests the property for non-null/non-zero rather than for
    ///    membership, so every one of them makes a faction mob. This is the reported symptom (2026-09-07):
    ///    shadow.json holds three level-240 Void Lords, wcids 43899, 72837 and 72869, all role 1, and only
    ///    72837 carries Faction1Bits, so a run that drew 72837 alongside either of the others opened with two
    ///    identically named Elite Void Lords killing each other.
    ///  - FoeType, on 0 members today. Cleared anyway because WeenieObject.PotentialFoe is symmetric and
    ///    reads the LIVE weenie, so one future roster addition carrying it would reintroduce the same bug.
    ///
    /// Hatred1/2/3Bits are deliberately NOT touched. Creature_Properties.cs holds the only reference to them
    /// anywhere in ACE.Server, and it is the accessor pair itself with no caller, so nothing in the server
    /// ever reads them and they are inert rather than a fourth mechanism. (ACE.Database's SQLWriter names
    /// them too, but only to serialize a weenie back out to SQL - it has no bearing on combat.)
    /// </summary>
    public static class SpawnedCreatureHostility
    {
        /// <summary>
        /// The Tolerance bits a roster-drawn creature may never keep. Both make it refuse the only fight
        /// there is:
        ///  - NoAttack (1) never attacks anything, which is the Frozen Gearknight defect (wcid 87259, dropped
        ///    from the shared roster in #949) reappearing on a member that is still in the tables.
        ///  - Monster (128) attacks ONLY other monsters and never a player (Monster_Awareness.GetAttackTargets
        ///    skips every Player and CombatPet when it is set), so a creature carrying it walks past the
        ///    players and picks a fight with the rest of the wave instead.
        ///
        /// Nothing else in the enum is banned. Appraise, Provoke, Target and Retaliate all describe WHEN a
        /// retail monster starts fighting a player, not whether it will, and 47 of the 63 roster members
        /// carrying any Tolerance at all carry Retaliate - stripping those would turn every ambush-style
        /// creature in the tables into an aggro-on-sight one for no reason the bug report asks for.
        /// </summary>
        public const Tolerance BannedTolerance = Tolerance.NoAttack | Tolerance.Monster;

        /// <summary>
        /// Pure half of <see cref="MakeHostileToPlayers"/>, split out so the bit rule is unit-testable
        /// without a live Creature.
        /// </summary>
        public static Tolerance PlayerHostileTolerance(Tolerance tolerance) => tolerance & ~BannedTolerance;

        /// <summary>
        /// Which <see cref="WorldEventSpawnKind"/>s are roster-drawn monsters, and so get normalized.
        ///
        /// Wave and Boss only, and the exclusions are the point:
        ///  - Source and InertObjective are AUTHORED objective props (a rift, a pillar) that happen to be
        ///    Creatures. TryPlace already stamps an InertObjective non-attackable on purpose, and a passive
        ///    objective authored with Tolerance.NoAttack would start swinging at players if it came through
        ///    here. Their wcids come from a theme's sources.json, never from the species roster. Checked
        ///    2026-09-07: none of the 20 wcids in Content/events/axes/sources.json (1002638-1002668) carries
        ///    Tolerance, Faction1Bits or FoeType at all, so excluding them hides no live instance of this bug.
        ///    That is a fact about today's content, not a guarantee - a future objective weenie authored with
        ///    faction bits would reproduce it, and the fix then is to author the weenie correctly rather than
        ///    to widen this gate, because normalizing a prop breaks what the prop is for.
        ///  - Npc is a friendly. PlaceNpcs builds those on its own path; making one hostile is the whole
        ///    failure this method exists to prevent, pointed the wrong way.
        ///  - Decor is not required to be a Creature at all.
        ///  - Add is a generator-spawned object adopted from WorldObject.OnGeneration, i.e. ALREADY in the
        ///    world by the time the event sees it. Writing these properties then would break the
        ///    nothing-after-EnterWorld rule, and an add is retail generator content rather than a roster draw.
        /// </summary>
        public static bool NormalizesHostility(WorldEventSpawnKind kind)
            => kind == WorldEventSpawnKind.Wave || kind == WorldEventSpawnKind.Boss;

        /// <summary>
        /// Clears every reason this creature has to attack another monster. MUST be called before EnterWorld.
        ///
        /// Per instance and safe to call on a creature freshly built from a cached weenie: Faction*Bits,
        /// Tolerance and FoeType are all PropertyInt, and PropertiesInt is COPIED into a new object's biota
        /// rather than shared with the cached weenie (ACE.Entity/Adapter/WeenieConverter.cs:29-30 - the path
        /// matters, ACE.Database/Adapter has a second file of the same name whose line 29 is unrelated),
        /// unlike the four collections (PropertiesCreateList, PropertiesEmote, PropertiesEventFilter,
        /// PropertiesGenerator) that the referenceWeenieCollectionsForCommonProperties: true argument at
        /// WorldObject.cs:125 makes reference-shared. Each setter calls RemoveProperty for null, which is what
        /// clears the row rather than storing a zero - Monster.SetMonsterState tests Faction1Bits for
        /// NON-NULL, so a stored 0 would still read as a faction mob there.
        ///
        /// SetMonsterState is re-run because it CACHES IsFactionMob and HasFoeType, and it last ran inside
        /// WorldObjectFactory.CreateNewWorldObject (Creature.cs:220) - before any of these writes. It
        /// recomputes IsMonster from Attackable/TargetingTactic, neither of which this method touches, so the
        /// re-run is otherwise a no-op. The physics WeenieObject, which caches the same facts for ObjectMaint,
        /// is built later still, from Landblock.AddWorldObjectInternal during EnterWorld (Landblock.cs:1533),
        /// so it reads the corrected values without any help.
        /// </summary>
        public static void MakeHostileToPlayers(Creature creature)
        {
            if (creature == null)
                return;

            creature.Tolerance = PlayerHostileTolerance(creature.Tolerance);

            creature.Faction1Bits = null;
            creature.Faction2Bits = null;
            creature.Faction3Bits = null;

            creature.FoeType = null;

            creature.SetMonsterState();
        }
    }
}
