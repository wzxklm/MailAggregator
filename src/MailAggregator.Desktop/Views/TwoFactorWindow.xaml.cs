using System.Windows;
using MailAggregator.Desktop.ViewModels;

namespace MailAggregator.Desktop.Views;

public partial class TwoFactorWindow : Window
{
    public TwoFactorWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is TwoFactorViewModel vm)
                await vm.InitializeAsync();
        };
        Closed += (_, _) =>
        {
            if (DataContext is TwoFactorViewModel vm)
                vm.Dispose();
        };
    }
}
