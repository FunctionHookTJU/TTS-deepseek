using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DeepSeekTTS;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：不让未处理异常直接崩掉窗口
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogFatal(ex);
        };

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            MessageBox.Show($"启动失败：\n\n{ex.Message}", "DeepSeek 语音合成",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception);
        MessageBox.Show($"发生未处理的错误：\n\n{e.Exception.Message}", "DeepSeek 语音合成",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogFatal(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var path = Path.Combine(AppSettings.DataDir, "error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch
        {
            // 日志写入失败时不再抛出
        }
    }
}
