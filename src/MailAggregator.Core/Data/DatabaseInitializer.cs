using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MailAggregator.Core.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(MailAggregatorDbContext context)
    {
        Log.Information("Initializing database...");
        await context.Database.EnsureCreatedAsync();
        Log.Information("Database initialized successfully");
    }
}
