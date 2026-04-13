using Avalonia.Controls;
using Avalonia.Interactivity;
using Nevolution.App.Desktop.ViewModels;
using Nevolution.Core.Models;
using Nevolution.Core.Resources;

namespace Nevolution.App.Desktop;

public partial class AddAccountWindow : Window
{
    public AddAccountWindow()
    {
        InitializeComponent();
        DataContext = new AccountEditorViewModel();
    }

    public Func<MailAccount, Task>? SaveAccountAsync { get; init; }

    private AccountEditorViewModel ViewModel => (AccountEditorViewModel)DataContext!;

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (SaveAccountAsync is null)
        {
            ViewModel.SetSaveError(Strings.AccountDialog_ErrorSaveFailed);
            return;
        }

        MailAccount account;

        try
        {
            account = ViewModel.CreateAccount();
        }
        catch (Exception exception)
        {
            ViewModel.SetSaveError(exception.Message);
            return;
        }

        ViewModel.IsBusy = true;
        ViewModel.SetSaveError(string.Empty);

        try
        {
            await SaveAccountAsync(account);
            Close(account);
        }
        catch (Exception exception)
        {
            ViewModel.SetSaveError(exception.Message);
        }
        finally
        {
            ViewModel.IsBusy = false;
        }
    }
}
