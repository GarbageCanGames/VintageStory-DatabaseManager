using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using DatabaseManager.API;
using Vintagestory.API.Common;

namespace DatabaseManager.Services
{
    /// <summary>
    /// Implementation of <see cref="IDatabaseSqLiteService"/> that provides managed SQLite database access.
    /// Databases are stored in the game's data folder under ModData/DatabaseManager.
    /// </summary>
    public sealed class DatabaseSqLiteService : IDatabaseSqLiteService
    {
        /// <summary>
        /// The Core API.
        /// </summary>
        private readonly ICoreAPI _api;
        /// <summary>
        /// The base path where all mod databases are stored.
        /// </summary>
        private readonly string _basePath;

        /// <summary>
        /// The logger used for reporting errors and information.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Lock object for thread-safe directory creation.
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// Set of databases that have been successfully opened in this session.
        /// Key is "modId:databaseName".
        /// </summary>
        private readonly ConcurrentDictionary<string, byte> _openedDatabases = new ConcurrentDictionary<string, byte>();

        /// <summary>
        /// List of active connections to be closed on disposal.
        /// </summary>
        private readonly ConcurrentDictionary<SqliteConnection, byte> _activeConnections = new ConcurrentDictionary<SqliteConnection, byte>();

        /// <summary>
        /// Cache for mod assemblies to avoid repeated stack trace walks and lookups.
        /// </summary>
        private readonly ConcurrentDictionary<Assembly, string> _assemblyModIdCache = new ConcurrentDictionary<Assembly, string>();

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseSqLiteService"/> class.
        /// </summary>
        /// <param name="api">The Core API (Server-side).</param>
        public DatabaseSqLiteService(ICoreAPI api)
        {
            _api = api;
            _basePath = Path.Combine(api.DataBasePath, "ModData", "DatabaseManager");
            _logger = api.Logger;

            try
            {
                if (!Directory.Exists(_basePath))
                {
                    Directory.CreateDirectory(_basePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[DatabaseManager] Failed to create base directory {0}: {1}", _basePath, ex.Message);
            }
        }

        /// <inheritdoc />
        public IDbConnection GetConnection(string databaseName = "main")
        {
            string modId = GetCallingModId();
            string connectionString = GetConnectionStringInternal(modId, databaseName);
            string dbKey = $"{modId}:{databaseName}";

            try
            {
                var connection = new SqliteConnection(connectionString);
                
                _activeConnections.TryAdd(connection, 0);
                connection.StateChange += (sender, e) =>
                {
                    if (e.CurrentState == ConnectionState.Closed)
                    {
                        _activeConnections.TryRemove(connection, out _);
                    }
                };

                bool alreadyOpened = _openedDatabases.ContainsKey(dbKey);

                if (!alreadyOpened)
                {
                    // Check for unclean shutdown before opening
                    if (CheckUncleanShutdown(modId, databaseName))
                    {
                        _logger.Warning("[DatabaseManager] WAL file exists for database `{0}` (mod `{1}`). Attempting proactive recovery...", databaseName, modId);
                        if (!RecoverDatabaseInternal(modId, databaseName))
                        {
                            throw new Exception($"Database `{databaseName}` for mod `{modId}` has a WAL file and proactive recovery failed. To prevent data corruption, connection is denied.");
                        }
                    }
                }

                connection.Open();

                // Enable WAL mode for better performance and concurrency
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA journal_mode=WAL;";
                    command.ExecuteNonQuery();
                }

                if (!alreadyOpened)
                {
                    // Quick integrity check on first open of this session
                    if (!VerifyIntegrity(connection))
                    {
                        _logger.Warning("[DatabaseManager] Database `{0}` for mod `{1}` failed quick integrity check. Attempting recovery...", databaseName, modId);
                        connection.Close();
                        if (RecoverDatabaseInternal(modId, databaseName))
                        {
                            connection.Open();
                        }
                        else
                        {
                            throw new Exception($"Database `{databaseName}` for mod `{modId}` is corrupted and recovery failed.");
                        }
                    }

                    _openedDatabases.TryAdd(dbKey, 0);
                }

                return connection;
            }
            catch (Exception ex)
            {
                _logger.Error("[DatabaseManager] Failed to open connection for mod `{0}`, database {1}: {2}", modId, databaseName, ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public bool CheckIntegrity(string databaseName = "main")
        {
            string modId = GetCallingModId();
            string connectionString = GetConnectionStringInternal(modId, databaseName);

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    return VerifyIntegrity(connection, quick: false);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[DatabaseManager] Error checking integrity for mod `{0}`, database {1}: {2}", modId, databaseName, ex.Message);
                return false;
            }
        }

        /// <inheritdoc />
        public bool RecoverDatabase(string databaseName = "main")
        {
            string modId = GetCallingModId();
            return RecoverDatabaseInternal(modId, databaseName);
        }

        /// <summary>
        /// Verifies the integrity of the database connection.
        /// </summary>
        private bool VerifyIntegrity(SqliteConnection connection, bool quick = true)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = quick ? "PRAGMA quick_check;" : "PRAGMA integrity_check;";
                    var result = command.ExecuteScalar()?.ToString();
                    return result != null && result.Equals("ok", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Internal implementation of database recovery.
        /// </summary>
        private bool RecoverDatabaseInternal(string modId, string databaseName)
        {
            string dbPath = GetDatabasePathInternal(modId, databaseName);
            if (!File.Exists(dbPath)) return true;

            _logger.Notification("[DatabaseManager] Attempting recovery for database: {0}", dbPath);

            string backupPath = dbPath + ".bak_" + DateTime.Now.Ticks;
            try
            {
                // Create a backup before attempting recovery
                File.Copy(dbPath, backupPath);

                string walPath = dbPath + "-wal";
                string shmPath = dbPath + "-shm";

                if (File.Exists(walPath)) File.Copy(walPath, backupPath + "-wal");
                if (File.Exists(shmPath)) File.Copy(shmPath, backupPath + "-shm");

                string recoveredPath = dbPath + ".recovered";
                
                // SQLite recovery using VACUUM INTO is only for non-corrupted databases usually.
                // For corrupted ones, we try to use the CLI if available, but here we are in C#.
                // One way to "recover" is to try to export whatever is left.
                // However, with Microsoft.Data.Sqlite, we have limited options if the file is truly corrupted.
                
                // Try simple REINDEX first
                try
                {
                    using (var connection = new SqliteConnection($"Data Source={dbPath};"))
                    {
                        connection.Open();
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "REINDEX;";
                            command.ExecuteNonQuery();
                        }
                    }
                    
                    // Check if REINDEX fixed it
                    using (var connection = new SqliteConnection($"Data Source={dbPath};"))
                    {
                        connection.Open();
                        if (VerifyIntegrity(connection))
                        {
                            _logger.Notification("[DatabaseManager] Recovery successful via REINDEX for {0}", dbPath);
                            connection.Close();
                            CleanupWalShm(dbPath);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning("[DatabaseManager] REINDEX failed during recovery: {0}", ex.Message);
                }

                // If REINDEX failed, try VACUUM (can sometimes fix minor corruption)
                try
                {
                    using (var connection = new SqliteConnection($"Data Source={dbPath};"))
                    {
                        connection.Open();
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "VACUUM;";
                            command.ExecuteNonQuery();
                        }
                    }
                    
                    using (var connection = new SqliteConnection($"Data Source={dbPath};"))
                    {
                        connection.Open();
                        if (VerifyIntegrity(connection))
                        {
                            _logger.Notification("[DatabaseManager] Recovery successful via VACUUM for {0}", dbPath);
                            connection.Close();
                            CleanupWalShm(dbPath);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning("[DatabaseManager] VACUUM failed during recovery: {0}", ex.Message);
                }

                _logger.Error("[DatabaseManager] Recovery failed for {0}. A backup was created at {1}", dbPath, backupPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error("[DatabaseManager] Critical error during recovery of {0}: {1}", dbPath, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Deletes WAL and SHM files for the given database path.
        /// </summary>
        private void CleanupWalShm(string dbPath)
        {
            try
            {
                string walPath = dbPath + "-wal";
                string shmPath = dbPath + "-shm";

                if (File.Exists(walPath)) File.Delete(walPath);
                if (File.Exists(shmPath)) File.Delete(shmPath);
            }
            catch (Exception ex)
            {
                _logger.Warning("[DatabaseManager] Failed to cleanup WAL/SHM files for {0}: {1}", dbPath, ex.Message);
            }
        }

        /// <inheritdoc />
        public string GetConnectionString(string databaseName = "main")
        {
            string modId = GetCallingModId();
            return GetConnectionStringInternal(modId, databaseName);
        }

        /// <summary>
        /// Gets the connection string for a mod's database.
        /// </summary>
        private string GetConnectionStringInternal(string modId, string databaseName)
        {
            string dbPath = GetDatabasePathInternal(modId, databaseName);
            return $"Data Source={dbPath};";
        }

        /// <inheritdoc />
        public string GetDatabasePath(string databaseName = "main")
        {
            string modId = GetCallingModId();
            return GetDatabasePathInternal(modId, databaseName);
        }

        /// <summary>
        /// Internal version of GetDatabasePath that takes modId.
        /// </summary>
        private string GetDatabasePathInternal(string modId, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("modId cannot be null or empty", nameof(modId));
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("databaseName cannot be null or empty", nameof(databaseName));

            // Sanitize names to prevent directory traversal
            string safeModId = SanitizeFileName(modId);
            string safeDbName = SanitizeFileName(databaseName);

            string modFolder = Path.Combine(_basePath, safeModId);

            lock (_lock)
            {
                if (!Directory.Exists(modFolder))
                {
                    try
                    {
                        Directory.CreateDirectory(modFolder);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("[DatabaseManager] Failed to create mod directory {0}: {1}", modFolder, ex.Message);
                        throw;
                    }
                }
            }

            return Path.Combine(modFolder, $"{safeDbName}.sqlite");
        }

        /// <summary>
        /// Identifies the mod ID of the caller using the stack trace.
        /// </summary>
        /// <returns>The mod ID of the caller.</returns>
        /// <exception cref="UnauthorizedAccessException">Thrown if the calling mod cannot be identified.</exception>
        private string GetCallingModId()
        {
            StackTrace stackTrace = new StackTrace();
            Assembly thisAssembly = Assembly.GetExecutingAssembly();

            for (int i = 1; i < stackTrace.FrameCount; i++)
            {
                MethodBase? method = stackTrace.GetFrame(i)?.GetMethod();
                if (method == null) continue;

                Assembly callerAssembly = method.DeclaringType?.Assembly ?? method.Module.Assembly;
                if (callerAssembly == thisAssembly) continue;

                if (_assemblyModIdCache.TryGetValue(callerAssembly, out string? cachedModId))
                {
                    return cachedModId;
                }

                // Try to find a mod that matches this assembly
                foreach (Mod mod in _api.ModLoader.Mods)
                {
                    // Check if the assembly matches the mod's own assembly or any of its systems' assemblies
                    if (mod.Systems.Any(s => s.GetType().Assembly == callerAssembly))
                    {
                        _assemblyModIdCache.TryAdd(callerAssembly, mod.Info.ModID);
                        return mod.Info.ModID;
                    }
                }
            }

            throw new UnauthorizedAccessException("[DatabaseManager] Could not identify the calling mod. Database access denied.");
        }

        /// <summary>
        /// Checks for the presence of -wal or -shm files which indicate an unclean shutdown.
        /// </summary>
        /// <returns>True if a -wal file exists, false otherwise.</returns>
        private bool CheckUncleanShutdown(string modId, string databaseName)
        {
            string dbPath = GetDatabasePathInternal(modId, databaseName);
            string walPath = dbPath + "-wal";
            string shmPath = dbPath + "-shm";

            bool walExists = File.Exists(walPath);
            if (walExists || File.Exists(shmPath))
            {
                _logger.Warning("[DatabaseManager] Detected potential unclean shutdown for database `{0}` (mod `{1}`). WAL/SHM files still exist.", databaseName, modId);
            }

            return walExists;
        }

        /// <summary>
        /// Disposes of all active connections and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            foreach (var connection in _activeConnections.Keys)
            {
                try
                {
                    if (connection.State != ConnectionState.Closed)
                    {
                        connection.Close();
                    }
                    connection.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Warning("[DatabaseManager] Error disposing connection: {0}", ex.Message);
                }
            }
            _activeConnections.Clear();
        }

        /// <summary>
        /// Sanitizes a string for use as a file or directory name.
        /// </summary>
        /// <param name="name">The name to sanitize.</param>
        /// <returns>A sanitized version of the string.</returns>
        private string SanitizeFileName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
            {
                name = name.Replace(c, '_');
            }
            // Additional check for common directory traversal patterns
            return name.Replace("..", "__");
        }
    }
}
