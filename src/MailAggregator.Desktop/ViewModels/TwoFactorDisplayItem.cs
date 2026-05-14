using CommunityToolkit.Mvvm.ComponentModel;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.TwoFactor;

namespace MailAggregator.Desktop.ViewModels;

public partial class TwoFactorDisplayItem : ObservableObject
{
    private readonly string _decryptedSecret;
    private readonly ITwoFactorCodeService _codeService;

    public TwoFactorAccount Account { get; }

    [ObservableProperty]
    private string _currentCode = string.Empty;

    [ObservableProperty]
    private int _remainingSeconds;

    [ObservableProperty]
    private double _progressPercentage;

    public TwoFactorDisplayItem(TwoFactorAccount account, string decryptedSecret, ITwoFactorCodeService codeService)
    {
        Account = account;
        _decryptedSecret = decryptedSecret;
        _codeService = codeService;
        UpdateCode();
    }

    public void UpdateCode()
    {
        var newRemaining = _codeService.GetRemainingSeconds(Account.Period);

        // Regenerate code when period boundary is crossed (remaining went up) or on first call
        if (newRemaining > RemainingSeconds || string.IsNullOrEmpty(CurrentCode))
        {
            var code = _codeService.GenerateCode(_decryptedSecret, Account.Algorithm, Account.Digits, Account.Period);
            CurrentCode = FormatCode(code);
        }

        RemainingSeconds = newRemaining;
        ProgressPercentage = (double)RemainingSeconds / Account.Period * 100;
    }

    private static string FormatCode(string code)
    {
        return code.Length switch
        {
            6 => $"{code[..3]} {code[3..]}",
            8 => $"{code[..4]} {code[4..]}",
            _ => code
        };
    }
}
