using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MailAggregator.Core.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(MailAggregatorDbContext context)
    {
        Log.Information("Initializing database...");
        await context.Database.EnsureCreatedAsync();

        // EnsureCreatedAsync only creates tables when the database is new.
        // For existing databases, new tables must be created manually.
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS TwoFactorAccounts (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Issuer TEXT NOT NULL,
                Label TEXT NOT NULL,
                EncryptedSecret TEXT NOT NULL,
                Algorithm INTEGER NOT NULL DEFAULT 0,
                Digits INTEGER NOT NULL DEFAULT 6,
                Period INTEGER NOT NULL DEFAULT 30,
                CreatedAt INTEGER NOT NULL DEFAULT 0,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            )
            """);

        // Add UseIdle column to existing Accounts table (defaults to 1 = true).
        // SQLite ALTER TABLE ADD COLUMN throws if column already exists, so catch and ignore.
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Accounts ADD COLUMN UseIdle INTEGER NOT NULL DEFAULT 1");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists — ignore (SQLite error code 1 = SQLITE_ERROR for duplicate column)
        }

        Log.Information("Database initialized successfully");
    }
}
