using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Services.TwoFactor;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MailAggregator.Desktop.ViewModels;

public partial class TwoFactorViewModel : ObservableObject, IDisposable
{
    private readonly ITwoFactorCodeService _codeService;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private ObservableCollection<TwoFactorDisplayItem> _items = [];

    [ObservableProperty]
    private TwoFactorDisplayItem? _selectedItem;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public TwoFactorViewModel(ITwoFactorCodeService codeService, ILogger logger)
    {
        _codeService = codeService;
        _logger = logger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
    }

    public async Task InitializeAsync()
    {
        await LoadAccountsAsync();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        foreach (var item in Items)
        {
            item.UpdateCode();
        }
    }

    [RelayCommand]
    private async Task LoadAccountsAsync()
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var accountService = scope.ServiceProvider.GetRequiredService<ITwoFactorAccountService>();

            var accounts = await accountService.GetAllAsync();
            var items = new ObservableCollection<TwoFactorDisplayItem>();
            foreach (var account in accounts)
            {
                var secret = accountService.GetDecryptedSecret(account);
                items.Add(new TwoFactorDisplayItem(account, secret, _codeService));
            }
            Items = items;
            StatusText = $"{accounts.Count} account(s)";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load 2FA accounts");
            StatusText = "Error loading accounts";
        }
    }

    [RelayCommand]
    private void CopyCode()
    {
        if (SelectedItem == null) return;
        try
        {
            var code = SelectedItem.CurrentCode.Replace(" ", "");
            System.Windows.Clipboard.SetText(code);
            StatusText = "Code copied";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to copy code to clipboard");
            StatusText = "Failed to copy code";
        }
    }

    [RelayCommand]
    private void AddAccount()
    {
        var vm = App.Services.GetRequiredService<AddTwoFactorViewModel>();
        var window = new Views.AddTwoFactorWindow { DataContext = vm };
        if (window.ShowDialog() == true)
        {
            _ = LoadAccountsAsync();
        }
    }

    [RelayCommand]
    private void EditAccount()
    {
        if (SelectedItem == null) return;
        var vm = App.Services.GetRequiredService<AddTwoFactorViewModel>();
        vm.LoadForEdit(SelectedItem.Account);
        var window = new Views.AddTwoFactorWindow { DataContext = vm };
        if (window.ShowDialog() == true)
        {
            _ = LoadAccountsAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (SelectedItem == null) return;

        var result = System.Windows.MessageBox.Show(
            $"Delete 2FA account '{SelectedItem.Account.Issuer}'?",
            "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            using var scope = App.Services.CreateScope();
            var accountService = scope.ServiceProvider.GetRequiredService<ITwoFactorAccountService>();

            await accountService.DeleteAsync(SelectedItem.Account.Id);
            Items.Remove(SelectedItem);
            SelectedItem = null;
            StatusText = "Account deleted";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete 2FA account");
            StatusText = "Error deleting account";
        }
    }
}
