using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace QuickDrop.App;

public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickDrop",
        "logs");

    private static readonly string LogPath = Path.Combine(LogDirectory, "quickdrop.log");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        WriteLog("QuickDrop 0.1.2 starting.");
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog("Unhandled UI exception.", e.Exception);
        MessageBox.Show(
            $"启动 QuickDrop 时发生错误。\n\n错误：{e.Exception.Message}\n\n诊断日志：{LogPath}",
            "QuickDrop 启动失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        WriteLog("Unhandled process exception.", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteLog("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    private static void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var entry = new StringBuilder()
                .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                .AppendLine(message);
            if (exception is not null)
            {
                entry.AppendLine(exception.ToString());
            }

            File.AppendAllText(LogPath, entry.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never replace the original startup error.
        }
    }
}
