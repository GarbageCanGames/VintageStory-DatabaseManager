using Vintagestory.API.Common;
using Vintagestory.API.Server;
using DatabaseManager.API;
using DatabaseManager.Services;

namespace DatabaseManager
{
    /// <summary>
    /// The DatabaseManager mod for managing databases.
    /// This mod provides a service that allows other mods to easily create and manage their own databases.
    /// </summary>
    public class DatabaseManager : ModSystem
    {
        /// <summary>
        /// The instance of the SQLite database service.
        /// </summary>
        private IDatabaseSqLiteService? _databaseSqLiteService;

        /// <summary>
        /// Determines if this mod should be loaded on the given side.
        /// This mod is server-side only.
        /// </summary>
        /// <param name="side">The side (Client or Server).</param>
        /// <returns>True if it should load (Server side only).</returns>
        public override bool ShouldLoad(EnumAppSide side)
        {
            return side == EnumAppSide.Server;
        }

        /// <summary>
        /// Initializes the mod and registers the <see cref="IDatabaseSqLiteService"/> on the server side.
        /// </summary>
        /// <param name="api">The server core API.</param>
        public override void StartServerSide(ICoreServerAPI api)
        {
            _databaseSqLiteService = new DatabaseSqLiteService(api);

            // Register the service in ObjectCache so other mods can access it.
            // Other mods can get it via: 
            // var dbService = api.ObjectCache["databaseManagerSqliteService"] as IDatabaseSqLiteService;
            api.ObjectCache["databaseManagerSqliteService"] = _databaseSqLiteService;

            api.Logger.Notification("[DatabaseManager] Service registered and ready.");
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (_databaseSqLiteService != null)
            {
                _databaseSqLiteService.Dispose();
                _databaseSqLiteService = null;
            }
            base.Dispose();
        }
    }
}
