using System;
using System.Data;

namespace DatabaseManager.API
{
    /// <summary>
    /// Provides managed access to databases for mods.
    /// </summary>
    public interface IDatabaseSqLiteService : IDisposable
    {
        /// <summary>
        /// Gets a connection to an SQLite database specific to the calling mod.
        /// </summary>
        /// <param name="databaseName">Optional name for the SQLite database if a mod needs multiple.</param>
        /// <returns>An open IDbConnection to the SQLite database.</returns>
        IDbConnection GetConnection(string databaseName = "main");

        /// <summary>
        /// Checks the integrity of the specified SQLite database.
        /// </summary>
        /// <param name="databaseName">The SQLite database name.</param>
        /// <returns>True if the SQLite database is healthy, false otherwise.</returns>
        bool CheckIntegrity(string databaseName = "main");

        /// <summary>
        /// Attempts to recover a corrupted SQLite database.
        /// Note: This is a best-effort operation and might result in data loss.
        /// </summary>
        /// <param name="databaseName">The SQLite database name.</param>
        /// <returns>True if recovery was successful or no corruption was found.</returns>
        bool RecoverDatabase(string databaseName = "main");

        /// <summary>
        /// Gets the absolute file path to the calling mod's SQLite database.
        /// </summary>
        /// <param name="databaseName">The SQLite database name.</param>
        /// <returns>The full path on the disk.</returns>
        string GetDatabasePath(string databaseName = "main");

        /// <summary>
        /// Gets the connection string for the calling mod's SQLite database.
        /// </summary>
        /// <param name="databaseName">The SQLite database name.</param>
        /// <returns>A valid SQLite connection string.</returns>
        string GetConnectionString(string databaseName = "main");
    }
}
