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

        Log.Information("Database initialized successfully");
    }
}
