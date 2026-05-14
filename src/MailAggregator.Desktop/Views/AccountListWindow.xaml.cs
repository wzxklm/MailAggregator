using System.Windows;
using MailAggregator.Desktop.ViewModels;

namespace MailAggregator.Desktop.Views;

public partial class AccountListWindow : Window
{
    public AccountListWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is AccountListViewModel vm)
                await vm.InitializeAsync();
        };
    }
}
