using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.TwoFactor;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MailAggregator.Desktop.ViewModels;

public partial class AddTwoFactorViewModel : ObservableObject
{
    private readonly ITwoFactorCodeService _codeService;
    private readonly ILogger _logger;
    private int? _editingId;

    [ObservableProperty]
    private string _issuer = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _secret = string.Empty;

    [ObservableProperty]
    private string _uriText = string.Empty;

    [ObservableProperty]
    private bool _isUriMode;

    [ObservableProperty]
    private OtpAlgorithm _selectedAlgorithm = OtpAlgorithm.Sha1;

    [ObservableProperty]
    private int _digits = 6;

    [ObservableProperty]
    private int _period = 30;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isEditMode;

    public string WindowTitle => IsEditMode ? "Edit 2FA Account" : "Add 2FA Account";

    public bool DialogResult { get; private set; }

    public event Action? CloseRequested;

    public AddTwoFactorViewModel(ITwoFactorCodeService codeService, ILogger logger)
    {
        _codeService = codeService;
        _logger = logger;
    }

    public void LoadForEdit(TwoFactorAccount account)
    {
        _editingId = account.Id;
        IsEditMode = true;
        Issuer = account.Issuer;
        Label = account.Label;
        OnPropertyChanged(nameof(WindowTitle));
    }

    [RelayCommand]
    private void ParseUri()
    {
        if (string.IsNullOrWhiteSpace(UriText))
        {
            StatusText = "Please enter an otpauth:// URI";
            return;
        }

        try
        {
            var parameters = _codeService.ParseOtpAuthUri(UriText);
            Secret = parameters.Secret;
            Issuer = parameters.Issuer;
            Label = parameters.Label;
            SelectedAlgorithm = parameters.Algorithm;
            Digits = parameters.Digits;
            Period = parameters.Period;
            StatusText = "URI parsed successfully";
        }
        catch (Exception ex)
        {
            StatusText = $"Invalid URI: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var accountService = scope.ServiceProvider.GetRequiredService<ITwoFactorAccountService>();

            if (IsEditMode)
            {
                if (string.IsNullOrWhiteSpace(Issuer))
                {
                    StatusText = "Issuer is required";
                    return;
                }
                await accountService.UpdateAsync(_editingId!.Value, Issuer, Label);
            }
            else
            {
                if (IsUriMode && !string.IsNullOrWhiteSpace(UriText))
                {
                    await accountService.AddFromUriAsync(UriText);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(Secret))
                    {
                        StatusText = "Secret key is required";
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(Issuer))
                    {
                        StatusText = "Issuer is required";
                        return;
                    }
                    await accountService.AddAsync(Issuer, Label, Secret.Trim(), SelectedAlgorithm, Digits, Period);
                }
            }

            DialogResult = true;
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save 2FA account");
            StatusText = $"Error: {ex.Message}";
        }
    }
}
