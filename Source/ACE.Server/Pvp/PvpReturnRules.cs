using ACE.Entity;

namespace ACE.Server.Pvp
{
    /// <summary>Where a PvP return sends a player (<see cref="PvpReturnRules.Decide"/>).</summary>
    public enum PvpReturnKind
    {
        /// <summary>Nowhere to send them: no Thread return, no stamp and no home (the caller keeps its own fallback).</summary>
        None,

        /// <summary>Back to the exact spot they were pulled from inside a Thread run that is still live and still accepts them.</summary>
        ThreadInside,

        /// <summary>The stamped exit (EphemeralRealmExitTo): where they stood before the match, or the Thread's outer exit.</summary>
        Stamp,

        /// <summary>The coordinator's own copy of the seat's exit, used only when the player carries no stamp.</summary>
        SeatExit,

        /// <summary>The lifestone (or, without one, the character's instantiation point).</summary>
        Home
    }

    /// <summary>The exit point taken at dispatch (<see cref="PvpReturnRules.ChooseExit"/>).</summary>
    public readonly record struct PvpExitChoice(Position Exit, bool RegisterThreadAway);

    /// <summary>One PvP return decision (<see cref="PvpReturnRules.Decide"/>).</summary>
    public readonly record struct PvpReturnDecision(Position Destination, PvpReturnKind Kind, bool KeepStamp, bool ReleaseThreadEntry);

    /// <summary>
    /// The pure halves of the match exit and the PvP return (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread", Docs/Pvp/DESIGN.md
    /// "Enter and exit"). The live callers are LivePvpPlayerGateway.GetExitPoint (the exit, at dispatch) and Player.ResolvePvpReturn (the
    /// return, from every one of the four return paths). Nothing here reads a live object, so both are unit tested as tables.
    /// </summary>
    public static class PvpReturnRules
    {
        /// <summary>
        /// The match exit for a player standing at <paramref name="current"/>. Never an ephemeral position:
        ///   - in a Thread copy as a roster member (<paramref name="inThreadAsMember"/>): the Thread's outer stamp
        ///     (<paramref name="stamp"/>, what the gem set), else the run's own exit, else home, and the player is to be recorded as
        ///     away from the run (<see cref="PvpExitChoice.RegisterThreadAway"/>) so the return can bring them back inside;
        ///   - in any other ephemeral copy: its stamp, else home;
        ///   - anywhere else: where they stand, exactly as before.
        /// A candidate that is itself ephemeral is skipped. With nothing else left, where they stand (the old behaviour) rather than
        /// nothing, because a null exit reads as "offline at placement". Null only for an offline player (<paramref name="current"/> null).
        /// </summary>
        public static PvpExitChoice ChooseExit(Position current, Position stamp, bool inThreadAsMember, Position runExitTo, Position sanctuary, Position instantiation)
        {
            if (current == null)
                return new PvpExitChoice(null, false);

            if (!inThreadAsMember && !current.IsEphemeralRealm)
                return new PvpExitChoice(new Position(current), false);

            var exit = inThreadAsMember
                ? FirstNonEphemeral(stamp, runExitTo, sanctuary, instantiation)
                : FirstNonEphemeral(stamp, sanctuary, instantiation);

            return new PvpExitChoice(new Position(exit ?? current), inThreadAsMember);
        }

        /// <summary>
        /// THE return rule, in priority order:
        ///   1. a Thread away entry that is still valid (<paramref name="threadReturnValid"/>, ThreadPvpAway.IsReturnValid): back inside,
        ///      and the stamp is KEPT (it is the Thread's outer exit, which the Thread's own exit portal and death return read);
        ///   2. the stamp, when it is usable (<paramref name="stampUsable"/>: not an ephemeral instance that is gone or refuses them);
        ///   3. the seat's exit the caller passed (<paramref name="seatExit"/>), when it is not ephemeral;
        ///   4. home, when there was a stamp (an unusable one: never land in a dead instance) or <paramref name="homeFallback"/> is set;
        ///   5. nothing.
        /// Every outcome but 1 clears the stamp and releases an invalid Thread entry.
        /// </summary>
        public static PvpReturnDecision Decide(bool hasThreadEntry, bool threadReturnValid, Position inside, Position stamp, bool stampUsable, Position seatExit,
            Position sanctuary, Position instantiation, bool homeFallback)
        {
            if (hasThreadEntry && threadReturnValid && inside != null)
                return new PvpReturnDecision(inside, PvpReturnKind.ThreadInside, KeepStamp: true, ReleaseThreadEntry: false);

            var release = hasThreadEntry;

            if (stamp != null && stampUsable)
                return new PvpReturnDecision(stamp, PvpReturnKind.Stamp, false, release);

            if (seatExit != null && !seatExit.IsEphemeralRealm)
                return new PvpReturnDecision(seatExit, PvpReturnKind.SeatExit, false, release);

            if (stamp != null || homeFallback)
            {
                var home = sanctuary ?? instantiation;

                if (home != null)
                    return new PvpReturnDecision(home, PvpReturnKind.Home, false, release);
            }

            return new PvpReturnDecision(null, PvpReturnKind.None, false, release);
        }

        private static Position FirstNonEphemeral(params Position[] candidates)
        {
            foreach (var c in candidates)
            {
                if (c != null && !c.IsEphemeralRealm)
                    return c;
            }

            return null;
        }
    }
}
