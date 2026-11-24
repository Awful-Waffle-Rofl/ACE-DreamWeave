namespace ACE.Server.Realms
{
    /// <summary>
    /// An <see cref="IEphemeralHold"/> that keeps an outdoor group's footprint MEMBERS loaded the way a live Thread run does
    /// (EphemeralRealm.HoldsFootprintMembers): while <see cref="HoldsMembers"/> is true no member unloads on its own idle
    /// timer, and the group empties out only through its primary. An Allegiance Gauntlet arena says true until its run closes.
    /// Pulled on every landblock heartbeat, so a change takes effect on the next one.
    /// </summary>
    public interface IFootprintHold
    {
        bool HoldsMembers { get; }
    }
}
