using System;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The one way an Avatar fighter is made and put into the world, shared by the Gauntlet (LiveGauntletFighterHooks) and the
/// private Avatar World Events. Three steps, all before the fighter can be seen, so every client gets the whole figure from
/// its CreateObject:
/// <list type="number">
/// <item><see cref="CreateFromWeenie"/>: wcid 1008504, shaped again by GauntletAvatarFighter.BuildFighterWeenie.</item>
/// <item><see cref="Dress"/>: the Avatar's stored look and the body-scale factor (AvatarLookApplier.ApplyFighter), keeping the
/// fighter's own weapon and shield.</item>
/// <item><see cref="PlaceInWorld"/>: never rots, EnterWorld, then CombatPet.Init's world half (combat mode, awake).</item>
/// </list>
/// </summary>
internal static class AvatarFighterFactory
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Test seam: the Avatar's cached look (null when never dressed, or when the Avatar is gone).</summary>
    internal static Func<uint, AvatarLook> LookOf = LiveLook;

    private static AvatarLook LiveLook(uint avatarId) => AvatarManager.Get(avatarId)?.Look;

    internal static void ResetSeams()
    {
        LookOf = LiveLook;
    }

    /// <summary>An unplaced, undressed fighter from wcid 1008504, or null when the weenie is missing or is not the fighter weenie.</summary>
    public static GauntletAvatarFighter CreateFromWeenie()
    {
        var weenie = DatabaseManager.World.GetCachedWeenie(GauntletAvatarFighter.FighterWcid);

        if (weenie == null)
            return null;

        // Shaped again even though the weenie ships shaped: a content edit that brings back a Lifespan or a treasure row
        // must not reach the fighter. The cached weenie is never written (BuildFighterWeenie copies what it changes).
        var shaped = GauntletAvatarFighter.BuildFighterWeenie(weenie, null);

        return WorldObjectFactory.CreateWorldObject(shaped, GuidManager.NewDynamicGuid()) as GauntletAvatarFighter;
    }

    /// <summary>
    /// Dresses an unplaced fighter in Avatar <paramref name="avatarId"/>'s look and binds it to that Avatar. A dress that
    /// throws is logged and the fighter keeps its weenie outfit: an undressed fighter still fights, a missing one does not.
    /// True when the dress ran to the end.
    /// </summary>
    public static bool Dress(GauntletAvatarFighter fighter, uint avatarId)
    {
        if (fighter == null)
            return false;

        fighter.AvatarId = avatarId;

        try
        {
            AvatarLookApplier.ApplyFighter(fighter, LookOf(avatarId));
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] dressing the fighter 0x{fighter.Guid} for Avatar {avatarId} threw; it keeps its weenie outfit", ex);
            return false;
        }
    }

    /// <summary>
    /// Puts a dressed fighter into the world at <paramref name="at"/> (its instance included). False when refused. Its
    /// landblock thread.
    /// </summary>
    public static bool PlaceInWorld(GauntletAvatarFighter fighter, Position at)
    {
        if (fighter == null || at == null)
            return false;

        fighter.Location = new Position(at);

        // A hand-placed object with a dynamic guid and no generator rots on the landblock heartbeat (IsDecayable), and a rot
        // is a bare Destroy with no death and so no respawn: never rot, as ThreadDungeonSpawner and Pet do.
        fighter.TimeToRot = -1;

        return PlaceAtLocation(fighter);
    }

    /// <summary>EnterWorld at the fighter's Location, then CombatPet.Init's world half minus everything owner-shaped.</summary>
    public static bool PlaceAtLocation(GauntletAvatarFighter fighter)
    {
        fighter.Location.LandblockId = new LandblockId(fighter.Location.GetCell());

        if (!fighter.EnterWorld())
            return false;

        fighter.SetCombatMode(CombatMode.Melee);
        fighter.MonsterState = Creature.State.Awake;
        fighter.IsAwake = true;

        return true;
    }
}
