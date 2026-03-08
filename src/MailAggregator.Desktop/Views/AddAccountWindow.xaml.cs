using System.Windows;
using MailAggregator.Desktop.ViewModels;

namespace MailAggregator.Desktop.Views;

public partial class AddAccountWindow : Window
{
    public AddAccountWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AddAccountViewModel vm)
        {
            vm.CloseRequested += () =>
            {
                DialogResult = vm.DialogResult;
                Close();
            };

            // Bind PasswordBox (can't bind directly in XAML for security)
            PasswordBox.PasswordChanged += (_, _) => vm.Password = PasswordBox.Password;
        }
    }
}
