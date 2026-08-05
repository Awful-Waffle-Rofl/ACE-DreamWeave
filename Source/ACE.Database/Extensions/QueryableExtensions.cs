using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;


namespace ACE.Database.Extensions
{
    public static class QueryableExtensions
    {
        public const int DefaultInClauseChunkSize = 1000;

        /// <summary>
        /// Runs queryPerChunk once per chunk of ids (MySQL has practical limits on how large a single IN-clause
        /// should be) and concatenates the results, instead of issuing one query per id.
        /// </summary>
        public static List<TResult> QueryChunked<TResult>(IReadOnlyCollection<uint> ids, Func<uint[], IEnumerable<TResult>> queryPerChunk, int chunkSize = DefaultInClauseChunkSize)
        {
            var results = new List<TResult>(ids.Count);

            foreach (var chunk in ids.Chunk(chunkSize))
                results.AddRange(queryPerChunk(chunk));

            return results;
        }

        /// <summary>
        /// The write-side sibling of QueryChunked: runs executePerChunk once per chunk of ids and sums the
        /// affected-row counts, for set-based statements that return a count rather than rows. Shares
        /// DefaultInClauseChunkSize with QueryChunked on purpose - the constraint being respected (how large a
        /// single IN-clause should be) is the same one, and duplicating the number in two places would let the
        /// read and write paths silently drift apart.
        /// Chunking is a statement-size concern only. If the chunks must all land or none of them land, the
        /// caller is responsible for wrapping the whole call in a transaction.
        /// </summary>
        public static int ExecuteChunked(IReadOnlyCollection<uint> ids, Func<uint[], int> executePerChunk, int chunkSize = DefaultInClauseChunkSize)
        {
            var affected = 0;

            foreach (var chunk in ids.Chunk(chunkSize))
                affected += executePerChunk(chunk);

            return affected;
        }

        /// <exception cref="InvalidOperationException"></exception>
        public static string ToSql<TEntity>(this IQueryable<TEntity> query)
        {
            var enumerator = query.Provider.Execute<IEnumerable<TEntity>>(query.Expression).GetEnumerator();
            var enumeratorType = enumerator.GetType();
            var selectFieldInfo = enumeratorType.GetField("_selectExpression", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new InvalidOperationException($"cannot find field _selectExpression on type {enumeratorType.Name}");
            var sqlGeneratorFieldInfo = enumeratorType.GetField("_querySqlGeneratorFactory", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new InvalidOperationException($"cannot find field _querySqlGeneratorFactory on type {enumeratorType.Name}");
            var selectExpression = selectFieldInfo.GetValue(enumerator) as SelectExpression ?? throw new InvalidOperationException($"could not get SelectExpression");
            var factory = sqlGeneratorFieldInfo.GetValue(enumerator) as IQuerySqlGeneratorFactory ?? throw new InvalidOperationException($"could not get IQuerySqlGeneratorFactory");
            var sqlGenerator = factory.Create();
            var command = sqlGenerator.GetCommand(selectExpression);
            var sql = command.CommandText;

            return sql;
        }
    }
}
