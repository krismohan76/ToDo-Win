using System.Windows;

namespace FloatingTodo;

public partial class App : Application
{
    private static Mutex _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one copy at a time (e.g. if it is already running from startup).
        _mutex = new Mutex(true, "FloatingTodo.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
