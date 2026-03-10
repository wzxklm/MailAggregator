using System.Windows;
using MailAggregator.Desktop.ViewModels;

namespace MailAggregator.Desktop.Views;

public partial class AddTwoFactorWindow : Window
{
    public AddTwoFactorWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AddTwoFactorViewModel vm)
        {
            vm.CloseRequested += () =>
            {
                DialogResult = vm.DialogResult;
                Close();
            };
        }
    }
}
