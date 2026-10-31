using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MULExplorer.Resources;
using MULExplorer.ViewModels;

namespace MULExplorer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private IDisposable? _rootBrush;

    protected override void OnDataContextChanged( EventArgs e )
    {
        base.OnDataContextChanged( e );

        if ( ViewModel is { } vm )
        {
            vm.PropertyChanged += ( _, args ) =>
            {
                if ( args.PropertyName == nameof( MainWindowViewModel.Backdrop ) )
                {
                    ApplyBackdrop();
                }
            };
        }

        ApplyBackdrop();
    }

    protected override void OnPropertyChanged( AvaloniaPropertyChangedEventArgs change )
    {
        base.OnPropertyChanged( change );

        if ( change.Property == ActualTransparencyLevelProperty )
        {
            UpdateRootBrush();
        }
    }

    /// <summary>Asks the OS for the chosen backdrop, falling back in order (Mica → Acrylic → none).</summary>
    private void ApplyBackdrop()
    {
        TransparencyLevelHint = ViewModel?.Backdrop switch
        {
            "Acrylic" => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None],
            "Solid" => [WindowTransparencyLevel.None],
            _ => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
        };

        UpdateRootBrush();
    }

    /// <summary>Translucent tint when the OS gave us a material, otherwise an opaque fill (Linux, older Windows, "Solid").</summary>
    private void UpdateRootBrush()
    {
        var translucent = ActualTransparencyLevel == WindowTransparencyLevel.Mica || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        _rootBrush?.Dispose();
        _rootBrush = Root.Bind( Panel.BackgroundProperty, this.GetResourceObservable( translucent ? "WindowTintBrush" : "WindowSolidBrush" ) );
    }

    protected override void OnOpened( EventArgs e )
    {
        base.OnOpened( e );

        if ( ViewModel is { } vm )
        {
            Width = vm.Settings.WindowWidth;
            Height = vm.Settings.WindowHeight;
        }
    }

    protected override void OnClosing( WindowClosingEventArgs e )
    {
        base.OnClosing( e );

        if ( ViewModel is { } vm && WindowState == WindowState.Normal )
        {
            vm.Settings.WindowWidth = Width;
            vm.Settings.WindowHeight = Height;
            vm.Settings.Save();
        }
    }

    private async void OnSetFolder( object? sender, RoutedEventArgs e )
    {
        if ( ViewModel is not { } vm )
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync( new FolderPickerOpenOptions { Title = Strings.SelectFolderTitle, AllowMultiple = false } );

        if ( folders is [var folder] && folder.TryGetLocalPath() is { } path && !await vm.TrySetFolderAsync( path ) )
        {
            await ShowMessageAsync( Strings.InvalidFolder );
        }
    }

    private void OnExit( object? sender, RoutedEventArgs e ) => Close();

    private async void OnCopyMcpUrl( object? sender, RoutedEventArgs e )
    {
        if ( ViewModel is { } vm && Clipboard is { } clipboard )
        {
            await clipboard.SetTextAsync( vm.McpUrl );
        }
    }

    private async void OnAbout( object? sender, RoutedEventArgs e ) => await ShowMessageAsync( Strings.AboutText );

    private Task ShowMessageAsync( string message )
    {
        var dialog = new Window
        {
            Title = Strings.AppTitle,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new TextBlock { Text = message, Margin = new Avalonia.Thickness( 24 ), MaxWidth = 420, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }
        };

        return dialog.ShowDialog( this );
    }
}
