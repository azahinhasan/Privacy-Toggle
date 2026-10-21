using System.IO;
using System.Windows;
using System.Windows.Threading;
using PrivacyToggle.Interop;

namespace PrivacyToggle;

public partial class App : Application
{
    // Also referenced by the installer (AppMutex) so setup can ask to close a running copy.
    private const string MutexName = "PrivacyToggle.SingleInstance";
    private const string ShowEventName = "PrivacyToggle.ShowSettings";
    private const string ExitEventName = "PrivacyToggle.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent, _exitEvent;
    private RegisteredWaitHandle? _showWait, _exitWait;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool HasArg(string name) => e.Args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst || HasArg("--exit"))
        {
            // Another copy is running: ask it to show Settings (or to exit, for the uninstaller), then quit.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(HasArg("--exit") ? ExitEventName : ShowEventName);
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                ev.Set();

                // For --exit, wait until the running copy has shut down (the uninstaller relies on this).
                if (!isFirst && HasArg("--exit"))
                {
                    try { isFirst = _mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { isFirst = true; }
                }
            }
            catch { }
            if (isFirst) _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) => Shutdown();

        _controller = new AppController(Dispatcher);
        _controller.Start(HasArg("--minimized"), HasArg("--show"));

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _controller?.ShowSettings()), null, Timeout.Infinite, false);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => Dispatcher.BeginInvoke(Shutdown), null, Timeout.Infinite, true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _exitWait?.Unregister(null);
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        _controller?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrivacyToggle");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:u}] {e.Exception}{Environment.NewLine}");
        }
        catch { }
        // Keep the tray app alive; a single failed action shouldn't kill it.
        e.Handled = true;
    }
}
