using System;

namespace ACE.Server.Pvp
{
    /// <summary>Every v1 arena mode's tick handler: no per-mode tick behaviour beyond the shared coordinator logic.</summary>
    public sealed class NoOpMatchTickHandler : IMatchTickHandler
    {
        public void OnLive(IMatchContext m) { }

        public void OnTick(IMatchContext m, DateTime utcNow) { }
    }
}
