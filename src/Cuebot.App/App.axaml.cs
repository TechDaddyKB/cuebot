using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cuebot.App.Shell;
using Cuebot.Core;
using Cuebot.Core.Logging;

namespace Cuebot.App;

public partial class App : Application
{
    private DesktopShell? _shell;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemeBrushes.Attach(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Program prepares the context; anything else launching the desktop lifetime gets a default one.
            var ctx = ShellContext.Current ??= CreateDefaultContext();
            var host = ctx.Host = AppHost.Create(ctx.Paths, ctx.Settings);
            ctx.FlushNotes(host.Get<AppLog>());
            // Sub-actions that edit the live profile (e.g. "Set action state") run on the UI thread, where view models read it.
            host.Get<Cuebot.Core.Actions.ProfileService>().EditDispatcher =
                edit => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(edit).GetTask();
            // The "Get input" sub-action asks in a dialog.
            host.Get<Cuebot.Core.Actions.UserPromptService>().Handler = Views.InputPromptWindow.AskAsync;
            _shell = new DesktopShell(this, desktop, ctx, host);
            _shell.Start();
            // Begin flushing as soon as shutdown starts, without blocking the UI thread (no deadlock on async
            // disposal); Program.Main waits for the same disposal task once the lifetime has ended.
            desktop.Exit += (_, _) =>
            {
                _shell.Dispose();
                _ = host.DisposeAsync();
            };
            _ = host.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static ShellContext CreateDefaultContext()
    {
        var paths = new AppPaths();
        return new ShellContext { Options = new StartupOptions(), Paths = paths, Settings = new AppSettingsStore(paths) };
    }
}
