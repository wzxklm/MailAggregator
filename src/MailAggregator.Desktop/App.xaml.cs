using System.IO;
using System.Windows;
using MailAggregator.Core.Data;
using MailAggregator.Core.Services.AccountManagement;
using MailAggregator.Core.Services.Auth;
using MailAggregator.Core.Services.Discovery;
using MailAggregator.Core.Services.Mail;
using MailAggregator.Core.Services.Sync;
using MailAggregator.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MailAggregator.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MailAggregator");
        Directory.CreateDirectory(appDataPath);

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(appDataPath, "logs", "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        var services = new ServiceCollection();
        ConfigureServices(services, appDataPath);

        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        // Initialize database
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MailAggregatorDbContext>();
            await DatabaseInitializer.InitializeAsync(dbContext);
        }

        // Initialize notification system
        NotificationHelper.Initialize();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services, string appDataPath)
    {
        var dbPath = Path.Combine(appDataPath, "mail.db");
        var keyPath = Path.Combine(appDataPath, "encryption.key");

        // Database
        services.AddDbContext<MailAggregatorDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // Logging
        services.AddSingleton<ILogger>(Log.Logger);

        // Auth services
        services.AddSingleton<IKeyProtector, DpapiKeyProtector>();
        services.AddSingleton<ICredentialEncryptionService>(sp =>
            new CredentialEncryptionService(sp.GetRequiredService<IKeyProtector>(), keyPath));
        services.AddSingleton<IPasswordAuthService, PasswordAuthService>();
        services.AddSingleton<IOAuthService>(sp =>
        {
            var oauthConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "oauth-providers.json");
            return new OAuthService(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<ICredentialEncryptionService>(),
                sp.GetRequiredService<ILogger>(),
                oauthConfigPath);
        });

        // HTTP client
        services.AddSingleton<HttpClient>();

        // Discovery
        services.AddSingleton<IAutoDiscoveryService, AutoDiscoveryService>();

        // Connection services
        services.AddSingleton<IImapConnectionService, ImapConnectionService>();
        services.AddSingleton<ISmtpConnectionService, SmtpConnectionService>();

        // Mail services
        services.AddScoped<IEmailSyncService, EmailSyncService>();
        services.AddScoped<IEmailSendService, EmailSendService>();

        // Account management
        services.AddScoped<IAccountService, AccountService>();

        // Sync manager
        services.AddSingleton<ISyncManager, SyncManager>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<AccountListViewModel>();
        services.AddTransient<AddAccountViewModel>();
        services.AddTransient<ComposeViewModel>();

        // Windows
        services.AddTransient<MainWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider != null)
        {
            var syncManager = _serviceProvider.GetService<ISyncManager>();
            if (syncManager != null)
            {
                await syncManager.StopAllAsync();
            }

            _serviceProvider.Dispose();
        }

        NotificationHelper.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
