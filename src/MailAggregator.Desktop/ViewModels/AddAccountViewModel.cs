using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.AccountManagement;
using MailAggregator.Core.Services.Auth;
using MailAggregator.Core.Services.Discovery;
using Serilog;

namespace MailAggregator.Desktop.ViewModels;

public partial class AddAccountViewModel : ObservableObject
{
    private readonly IAccountService _accountService;
    private readonly IAutoDiscoveryService _autoDiscoveryService;
    private readonly IOAuthService _oAuthService;
    private readonly ILogger _logger;

    // Wizard step tracking
    [ObservableProperty]
    private int _currentStep; // 0 = Email, 1 = Discovery, 2 = Auth, 3 = Server Config, 4 = Complete

    // Step 0: Email input
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCommand))]
    private string _emailAddress = string.Empty;

    // Step 1: Discovery
    [ObservableProperty]
    private string _discoveryStatus = string.Empty;

    [ObservableProperty]
    private bool _isDiscovering;

    // Step 2: Auth
    [ObservableProperty]
    private AuthType _selectedAuthType = AuthType.Password;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordSelected))]
    private bool _isOAuthAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordSelected))]
    private bool _isOAuthSelected;

    public bool IsPasswordSelected => !IsOAuthSelected;

    [ObservableProperty]
    private string _oAuthStatus = string.Empty;

    // Step 3: Server config
    [ObservableProperty]
    private string _imapHost = string.Empty;

    [ObservableProperty]
    private int _imapPort = 993;

    [ObservableProperty]
    private ConnectionEncryptionType _imapEncryption = ConnectionEncryptionType.Ssl;

    [ObservableProperty]
    private string _smtpHost = string.Empty;

    [ObservableProperty]
    private int _smtpPort = 587;

    [ObservableProperty]
    private ConnectionEncryptionType _smtpEncryption = ConnectionEncryptionType.StartTls;

    // Proxy
    [ObservableProperty]
    private string _proxyHost = string.Empty;

    [ObservableProperty]
    private string _proxyPort = string.Empty;

    // Status
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private string _windowTitle = "Add Account";

    private Account? _editingAccount;

    public bool DialogResult { get; private set; }

    public event Action? CloseRequested;

    public AddAccountViewModel(
        IAccountService accountService,
        IAutoDiscoveryService autoDiscoveryService,
        IOAuthService oAuthService,
        ILogger logger)
    {
        _accountService = accountService;
        _autoDiscoveryService = autoDiscoveryService;
        _oAuthService = oAuthService;
        _logger = logger;
    }

    public void LoadForEdit(Account account)
    {
        IsEditMode = true;
        WindowTitle = "Edit Account";
        _editingAccount = account;
        CurrentStep = 3; // Go directly to server config

        EmailAddress = account.EmailAddress;
        ImapHost = account.ImapHost;
        ImapPort = account.ImapPort;
        ImapEncryption = account.ImapEncryption;
        SmtpHost = account.SmtpHost;
        SmtpPort = account.SmtpPort;
        SmtpEncryption = account.SmtpEncryption;
        SelectedAuthType = account.AuthType;
        ProxyHost = account.ProxyHost ?? string.Empty;
        ProxyPort = account.ProxyPort?.ToString() ?? string.Empty;
    }

    private bool CanDiscover() => !string.IsNullOrWhiteSpace(EmailAddress) && EmailAddress.Contains('@');

    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task DiscoverAsync()
    {
        try
        {
            IsDiscovering = true;
            ErrorMessage = string.Empty;
            DiscoveryStatus = "Discovering server configuration...";
            CurrentStep = 1;

            var config = await _autoDiscoveryService.DiscoverAsync(EmailAddress);

            if (config != null)
            {
                ImapHost = config.ImapHost;
                ImapPort = config.ImapPort;
                ImapEncryption = config.ImapEncryption;
                SmtpHost = config.SmtpHost;
                SmtpPort = config.SmtpPort;
                SmtpEncryption = config.SmtpEncryption;

                DiscoveryStatus = $"Found: {config.ImapHost}:{config.ImapPort}";

                // Check if OAuth is available
                IsOAuthAvailable = _oAuthService.FindProviderByHost(config.ImapHost) != null;
                IsOAuthSelected = IsOAuthAvailable;
                SelectedAuthType = IsOAuthAvailable ? AuthType.OAuth2 : AuthType.Password;

                CurrentStep = 2; // Move to auth step
            }
            else
            {
                DiscoveryStatus = "Could not auto-discover. Please configure manually.";
                CurrentStep = 3; // Skip to manual config
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Auto-discovery failed for {Email}", EmailAddress);
            DiscoveryStatus = "Discovery failed. Please configure manually.";
            ErrorMessage = ex.Message;
            CurrentStep = 3;
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [RelayCommand]
    private void GoToServerConfig()
    {
        CurrentStep = 3;
    }

    [RelayCommand]
    private void GoBack()
    {
        if (CurrentStep > 0 && !IsEditMode)
            CurrentStep--;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = string.Empty;

            // Sync radio button selection back to SelectedAuthType
            SelectedAuthType = IsOAuthSelected ? AuthType.OAuth2 : AuthType.Password;

            if (IsEditMode && _editingAccount != null)
            {
                _editingAccount.ImapHost = ImapHost;
                _editingAccount.ImapPort = ImapPort;
                _editingAccount.ImapEncryption = ImapEncryption;
                _editingAccount.SmtpHost = SmtpHost;
                _editingAccount.SmtpPort = SmtpPort;
                _editingAccount.SmtpEncryption = SmtpEncryption;
                ApplyProxySettings(_editingAccount);

                await _accountService.UpdateAccountAsync(_editingAccount);
                CurrentStep = 4;
                DialogResult = true;
                CloseRequested?.Invoke();
                return;
            }

            // OAuth2: run authorization flow before creating account
            OAuthTokenResult? oauthTokens = null;
            if (SelectedAuthType == AuthType.OAuth2)
            {
                oauthTokens = await RunOAuthFlowAsync();
                if (oauthTokens == null)
                    return; // Flow was cancelled or failed (error already set)
            }

            // Build manual server config from UI fields (skips redundant auto-discovery)
            var manualConfig = new ServerConfiguration
            {
                ImapHost = ImapHost,
                ImapPort = ImapPort,
                ImapEncryption = ImapEncryption,
                SmtpHost = SmtpHost,
                SmtpPort = SmtpPort,
                SmtpEncryption = SmtpEncryption,
            };

            // Create account
            var passwordForAuth = SelectedAuthType == AuthType.Password ? Password : null;
            var account = await _accountService.AddAccountAsync(EmailAddress, passwordForAuth, manualConfig);

            // Store OAuth tokens on the account
            if (oauthTokens != null)
            {
                account.EncryptedAccessToken = oauthTokens.AccessToken;
                account.EncryptedRefreshToken = oauthTokens.RefreshToken;
                account.OAuthTokenExpiry = oauthTokens.ExpiresAt;
            }

            // Apply proxy if configured
            if (!string.IsNullOrWhiteSpace(ProxyHost) || oauthTokens != null)
            {
                ApplyProxySettings(account);
                await _accountService.UpdateAccountAsync(account);
            }

            CurrentStep = 4;
            DialogResult = true;
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save account");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OAuthStatus = string.Empty;
        }
    }

    private async Task<OAuthTokenResult?> RunOAuthFlowAsync()
    {
        var provider = _oAuthService.FindProviderByHost(ImapHost);
        if (provider == null)
        {
            ErrorMessage = "No OAuth provider found for this server.";
            return null;
        }

        // Step 1: Prepare authorization URL (pass email as login_hint)
        var (authUrl, codeVerifier, listenerPort, redirectUri) = _oAuthService.PrepareAuthorization(provider, EmailAddress);

        // Step 2: Open browser for user authorization
        OAuthStatus = "Opening browser for authorization...";
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // Step 3: Wait for the OAuth callback
        OAuthStatus = "Waiting for authorization in browser...";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string authCode;
        try
        {
            authCode = await _oAuthService.WaitForAuthorizationCodeAsync(listenerPort, cts.Token);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "OAuth authorization timed out. Please try again.";
            return null;
        }

        // Step 4: Exchange code for tokens
        OAuthStatus = "Exchanging authorization code for tokens...";
        var tokens = await _oAuthService.ExchangeCodeForTokenAsync(provider, authCode, codeVerifier, redirectUri);

        OAuthStatus = "Authorization successful!";
        _logger.Information("OAuth flow completed for {Email}, token expires at {ExpiresAt}",
            EmailAddress, tokens.ExpiresAt);

        return tokens;
    }

    private void ApplyProxySettings(Account account)
    {
        if (!string.IsNullOrWhiteSpace(ProxyHost) && int.TryParse(ProxyPort, out var port))
        {
            account.ProxyHost = ProxyHost;
            account.ProxyPort = port;
        }
        else
        {
            account.ProxyHost = null;
            account.ProxyPort = null;
        }
    }
}
