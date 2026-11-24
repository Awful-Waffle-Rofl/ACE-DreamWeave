namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Salvage Roller (Docs/SalvageRoller/DESIGN.md 5.1): the player-side slot for the shared station the player last
    /// opened a view on. The view itself lives on the station (SalvageRollerVendor); the slot only says where to look.
    /// It is set by SalvageRollerStation.Open, released on logout (the view is dropped), and cleared when the station
    /// is destroyed.
    /// </summary>
    partial class Player
    {
        /// <summary>The station the player last opened a view on, or null. Read it through <see cref="GetLiveSalvageRollerStation"/>.</summary>
        public SalvageRollerVendor CurrentSalvageRollerStation { get; set; }

        /// <summary>The slot's station when it is still alive AND still holds a view for this player; otherwise null.</summary>
        internal SalvageRollerVendor GetLiveSalvageRollerStation()
        {
            var station = CurrentSalvageRollerStation;

            if (station == null || station.IsDestroyed || !station.HasView(this))
                return null;

            return station;
        }
    }
}
