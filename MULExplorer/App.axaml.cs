using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MULExplorer.Mcp;
using MULExplorer.Services;
using MULExplorer.ViewModels;
using MULExplorer.Views;

namespace MULExplorer;

public partial class App : Application
{
    /// <summary>Set by <see cref="Program" /> from the command line before the app starts.</summary>
    internal static string? CommandLineFolder { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load( this );
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if ( ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop )
        {
            var (window, vm) = CreateMainWindow( Settings.Load() );
            vm.Updates = new UpdateService();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async ( _, _ ) => await vm.DisposeAsync();

            _ = vm.OpenFolderAsync( vm.Settings.ResolveUOFolder( CommandLineFolder ) );

            _ = vm.CheckForUpdatesAsync();

            if ( vm.Settings.McpAutoStart )
            {
                _ = vm.StartMcpAsync();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds the main window and wires MCP UI automation to it. Also used by headless tests.</summary>
    public static (MainWindow Window, MainWindowViewModel ViewModel) CreateMainWindow( Settings settings )
    {
        var context = new McpContext();
        var vm = new MainWindowViewModel( settings, context );
        var window = new MainWindow { DataContext = vm };
        context.UI = new UIAutomation( window, vm );
        return ( window, vm );
    }
}
