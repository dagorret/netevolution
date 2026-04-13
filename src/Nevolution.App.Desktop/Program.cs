using Avalonia;
using System;
using System.Diagnostics;

namespace Nevolution.App.Desktop;

class Program
{
    internal static readonly Stopwatch StartupStopwatch = Stopwatch.StartNew();

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        Console.WriteLine("[Startup] Process started");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Console.WriteLine("Desktop shutdown: Program.Main completed");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Desktop fatal startup error: {exception}");
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        Console.WriteLine($"Unhandled exception: {eventArgs.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        Console.WriteLine($"Unobserved task exception: {eventArgs.Exception}");
        eventArgs.SetObserved();
    }
}
