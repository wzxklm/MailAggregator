using System.Windows;
using MailAggregator.Desktop.ViewModels;

namespace MailAggregator.Desktop.Views;

public partial class AiSettingsWindow : Window
{
    public AiSettingsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiSettingsViewModel vm)
        {
            vm.CloseRequested += () =>
            {
                DialogResult = vm.DialogResult;
                Close();
            };

            // Bind PasswordBox manually (can't bind directly in XAML for security)
            ApiKeyBox.PasswordChanged += (_, _) =>
            {
                vm.ApiKey = ApiKeyBox.Password;
                vm.NotifyApiKeyChanged();
            };

            await vm.LoadAsync();
        }
    }
}
