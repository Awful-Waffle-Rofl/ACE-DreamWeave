namespace ACE.Common
{
    public class DatabaseConfiguration
    {
        public MySqlConfiguration Authentication { get; set; } = new MySqlConfiguration()
        {
            Host     = "127.0.0.1",
            Port     = 3306,
            Database = "ace_auth",
            Username = "root",
            Password = ""
        };

        public MySqlConfiguration Shard { get; set; } = new MySqlConfiguration()
        {
            Host = "127.0.0.1",
            Port = 3306,
            Database = "ace_shard",
            Username = "root",
            Password = ""
        };

        public MySqlConfiguration World { get; set; } = new MySqlConfiguration()
        {
            Host = "127.0.0.1",
            Port = 3306,
            Database = "ace_world",
            Username = "root",
            Password = ""
        };

        /// <summary>
        /// Optional 4th database for the monitoring/analytics pipeline (players-online snapshots,
        /// per-character xp/lum rates, and later trade/give/bank audit events). Only used when
        /// Server.EnableAnalytics is true. Kept separate so analytics writes never touch the hot
        /// shard DB. See Docs/Monitoring/DESIGN.md §5.
        /// </summary>
        public MySqlConfiguration Analytics { get; set; } = new MySqlConfiguration()
        {
            Host = "127.0.0.1",
            Port = 3306,
            Database = "ace_analytics",
            Username = "root",
            Password = ""
        };
    }
}
