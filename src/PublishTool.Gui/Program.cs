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
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
