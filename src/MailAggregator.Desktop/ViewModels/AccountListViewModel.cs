using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.AccountManagement;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MailAggregator.Desktop.ViewModels;

public partial class AccountListViewModel : ObservableObject
{
    private readonly IAccountService _accountService;
    private readonly ILogger _logger;

    [ObservableProperty]
    private ObservableCollection<Account> _accounts = [];

    [ObservableProperty]
    private Account? _selectedAccount;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public AccountListViewModel(IAccountService accountService, ILogger logger)
    {
        _accountService = accountService;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await LoadAccountsAsync();
    }

    [RelayCommand]
    private async Task LoadAccountsAsync()
    {
        try
        {
            var accounts = await _accountService.GetAllAccountsAsync();
            Accounts = new ObservableCollection<Account>(accounts);
            StatusText = $"{accounts.Count} account(s)";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load accounts");
            StatusText = "Error loading accounts";
        }
    }

    [RelayCommand]
    private void AddAccount()
    {
        var vm = App.Services.GetRequiredService<AddAccountViewModel>();
        var window = new Views.AddAccountWindow { DataContext = vm };
        if (window.ShowDialog() == true)
        {
            _ = LoadAccountsAsync();
        }
    }

    [RelayCommand]
    private void EditAccount()
    {
        if (SelectedAccount == null) return;

        var vm = App.Services.GetRequiredService<AddAccountViewModel>();
        vm.LoadForEdit(SelectedAccount);
        var window = new Views.AddAccountWindow { DataContext = vm };
        if (window.ShowDialog() == true)
        {
            _ = LoadAccountsAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (SelectedAccount == null) return;

        var result = System.Windows.MessageBox.Show(
            $"Delete account '{SelectedAccount.EmailAddress}'?\n\nThis will remove all local cached data.",
            "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await _accountService.DeleteAccountAsync(SelectedAccount.Id);
            Accounts.Remove(SelectedAccount);
            SelectedAccount = null;
            StatusText = "Account deleted";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete account");
            StatusText = "Error deleting account";
        }
    }
}
