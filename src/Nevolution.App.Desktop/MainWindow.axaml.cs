using Avalonia.Controls;
using Avalonia.Interactivity;
using Nevolution.App.Desktop.ViewModels;

namespace Nevolution.App.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        Console.WriteLine("Desktop startup: MainWindow ctor");
        InitializeComponent();
        Console.WriteLine("Desktop startup: MainWindow initialized");
    }

    private async void OnAddAccountMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        Console.WriteLine("[Accounts] Opening add account dialog.");

        var dialog = new AddAccountWindow
        {
            SaveAccountAsync = viewModel.AddAccountAsync
        };

        var result = await dialog.ShowDialog<Nevolution.Core.Models.MailAccount?>(this);

        if (result is not null)
        {
            Console.WriteLine($"[Accounts] Add account dialog completed successfully for '{result.Email}'.");
        }
        else
        {
            Console.WriteLine("[Accounts] Add account dialog closed without saving.");
        }
    }

    private void OnCloseMenuItemClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
