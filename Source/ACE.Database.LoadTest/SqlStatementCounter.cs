using System;
using System.Data;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// Reads MySQL's global Questions counter, so a scenario can report how many SQL statements an operation
    /// actually issued rather than how many it was assumed to issue. A wall-clock number alone cannot distinguish
    /// "few statements" from "many statements against a warm buffer pool", and that ambiguity has already
    /// invalidated one measurement in this harness.
    ///
    /// Caveats, because this is a blunt instrument:
    /// - Questions is server-wide. It is only meaningful when nothing else is talking to the same MySQL instance.
    ///   Cross-check anything surprising against the general query log (SET GLOBAL general_log=ON with
    ///   log_output='TABLE', then group mysql.general_log by statement text), which gives an exact per-table
    ///   breakdown and cannot be polluted the same way.
    /// - It counts connection-pool housekeeping (the `SET NAMES utf8mb4` a reset connection issues) alongside
    ///   real queries, so it runs consistently higher than the SELECT count. Use it for orders of magnitude and
    ///   for before/after ratios, not as an exact query count.
    /// </summary>
    public static class SqlStatementCounter
    {
        /// <summary>
        /// The current global Questions value, or -1 if it could not be read. Each call is itself one statement.
        /// </summary>
        public static long Read()
        {
            try
            {
                using var context = new ShardDbContext();
                var connection = context.Database.GetDbConnection();

                if (connection.State != ConnectionState.Open)
                    connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SHOW GLOBAL STATUS LIKE 'Questions'";

                using var reader = command.ExecuteReader();

                return reader.Read() ? long.Parse(reader.GetString(1)) : -1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (could not read the Questions counter: {ex.Message})");
                return -1;
            }
        }

        /// <summary>
        /// Statements issued between two Read() values, discounting the second Read()'s own statement.
        /// Returns -1 if either endpoint was unreadable.
        /// </summary>
        public static long Delta(long before, long after) => before < 0 || after < 0 ? -1 : after - before - 1;
    }
}
