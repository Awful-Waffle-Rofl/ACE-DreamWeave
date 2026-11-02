using System.Collections.Generic;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// A mode handler that names teams by their colour (Docs/Pvp/TEMPLATES.md "Team hue"). The coordinator binds the match's
    /// team colours right after it assigns them at formation, and again at dispatch; a handler that never gets a binding keeps its
    /// colour-free wording. A separate interface, so <see cref="IObjectiveModeHandler"/> and its test fakes stay untouched.
    /// </summary>
    public interface ITeamColourAware
    {
        /// <summary>Team index to colour, for every team of the match.</summary>
        void BindTeamColours(IReadOnlyDictionary<int, PvpTeamColour> colours);
    }
}
