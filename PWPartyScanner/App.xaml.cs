using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace PWPartyScanner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
        var window = new MainWindow();
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "DispatcherUnhandledException");

        MessageBox.Show(
            e.Exception.Message + "\n\nДеталі записано у PWPartyScanner_error.log поруч із програмою.",
            "PW Party Scanner — помилка",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Managed UI exceptions should not silently close the application.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            WriteCrashLog(ex, "AppDomain.UnhandledException");
        else
            WriteCrashLogText($"AppDomain.UnhandledException: {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    public static void WriteCrashLog(Exception ex, string context)
    {
        WriteCrashLogText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\r\n{ex}\r\n");
    }

    private static void WriteCrashLogText(string text)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "PWPartyScanner_error.log");
            File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // Logging must never cause another application failure.
        }
    }
}
