using Avalonia.Controls;

namespace Nevolution.App.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        Console.WriteLine("Desktop startup: MainWindow ctor");
        InitializeComponent();
        Console.WriteLine("Desktop startup: MainWindow initialized");
    }
}
