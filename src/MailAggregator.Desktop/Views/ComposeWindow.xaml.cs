using System.Windows;
using MailAggregator.Desktop.ViewModels;

namespace MailAggregator.Desktop.Views;

public partial class ComposeWindow : Window
{
    public ComposeWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ComposeViewModel vm)
        {
            vm.CloseRequested += () => Close();
        }
    }
}
