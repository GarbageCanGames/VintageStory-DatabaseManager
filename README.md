# Database Manager for Vintage Story

Database Manager is a server-side mod for Vintage Story that provides a simplified API for other mods to create and manage their own databases. It handles database file locations, connection management, and basic maintenance tasks like integrity checks and automated best effort recovery.

## Currently Supported Database Engines
- SQLite

## Requirements
- Vintage Story 1.21.0 or later stable build

## Getting Started

### Accessing the Service

To use the Database Manager in your mod, you need to access the `IDatabaseSqLiteService` from the server's `ObjectCache`. It is recommended to do this in your mod's `StartServerSide` method.

```csharp
using DatabaseManager.API;
// ...

public override void StartServerSide(ICoreServerAPI api)
{
    var dbService = api.ObjectCache["databaseManagerSqliteService"] as IDatabaseSqLiteService;
    
    if (dbService == null) 
    {
        api.Logger.Error("DatabaseManager SQLite service not found!");
        return;
    }
    
    // Get a connection to your mod's database
    using (var connection = dbService.GetConnection())
    {
        // Use the connection...
    }
}
```

### Mod Dependencies

Ensure your mod includes `databasemanager` as a dependency in your `modinfo.json`:

```json
"dependencies": {
    "databasemanager": "1.0.0"
}
```

## Database Location

Databases are stored in the server's data folder under a `ModData/DatabaseManager` directory, organized by mod ID.

Example: `(DataFolder)/ModData/DatabaseManager/yourmodid/main.sqlite`

## Usage Examples

### Proper Connection Disposal

Always use a `using` block to ensure that the connection is closed and disposed of, even if an exception occurs.

```csharp
// The using block automatically calls Dispose() on the connection
using (var connection = dbService.GetConnection())
{
    // Perform database operations
    // ...
} // Connection is closed and disposed here
```

### Executing a Simple Query

```csharp
using (var connection = dbService.GetConnection())
{
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "CREATE TABLE IF NOT EXISTS players (id TEXT PRIMARY KEY, score INTEGER)";
        command.ExecuteNonQuery();
    }
}
```

### Integrity Check and Recovery

```csharp
if (!dbService.CheckIntegrity())
{
    api.Logger.Warning("Database corruption detected! Attempting recovery...");
    if (dbService.RecoverDatabase())
    {
        api.Logger.Notification("Database recovery successful.");
    }
    else
    {
        api.Logger.Error("Database recovery failed!");
    }
}
```

## Documentation

### Building

1. Clone the repo
   ```shell
    git clone https://github.com/GarbageCanGames/VintageStory-DatabaseManager.git
   ```
2. Change directory to the project root
   ```shell
   cd VintageStory-DatabaseManager
   ```
3. Copy `VintagestoryAPI.dll` into the `VintageStory-DatabaseManager/lib` directory. You will need to download it from the official Vintage Story website.

4. Build the project
   ```shell
   dotnet restore
   ```
    * Run the build command for debug
        ```shell
        dotnet build
        ```
    * Run the build command for release
        ```shell
        dotnet build -c Release
        ```
5. Package the output at `DatabaseManager/bin/Debug/net8.0/` OR `DatabaseManager/bin/Release/net8.0/` into a zip file named `DatabaseManager-v1.X.X.zip`, make sure to match the version number in modinfo.json file.

### API Documentation

To generate the API documentation for this project, you need to have [Doxygen](https://www.doxygen.nl/) installed.

Run the following command in the project root:

```bash
doxygen Doxyfile
```

The documentation will be generated in the `html/` directory. You can view it by opening `html/index.html` in your browser.
