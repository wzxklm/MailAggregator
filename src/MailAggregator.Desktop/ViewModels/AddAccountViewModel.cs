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
    private bool _isOAuthAvailable;

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

            // New account: use AddAccountAsync
            var passwordForAuth = SelectedAuthType == AuthType.Password ? Password : null;
            var account = await _accountService.AddAccountAsync(EmailAddress, passwordForAuth);

            // Apply proxy if configured
            if (!string.IsNullOrWhiteSpace(ProxyHost))
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
        }
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
