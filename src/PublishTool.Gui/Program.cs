using System;
using Velopack;

namespace PublishTool.Gui;

public static class Program
{
    // WPF's usual generated entry point (via App.xaml's StartupUri) runs too late for Velopack --
    // VelopackApp.Build().Run() has to be the very first thing that executes, before any UI or
    // window construction, so it can intercept install/uninstall/update lifecycle events that the
    // installer launches the app with.
    [STAThread]
    public static void Main(string[] args)
    {
        // Checked before VelopackApp.Build().Run() -- an update/install/uninstall relaunch is a
        // real, separate lifecycle event Velopack needs to see even if a normal instance happens
        // to already be running, so this only guards the ordinary "user launched it twice" case by
        // running after Velopack's own hook has had its chance to intercept and exit early.
        VelopackApp.Build().Run();

        if (!SingleInstanceGuard.TryAcquire())
        {
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
