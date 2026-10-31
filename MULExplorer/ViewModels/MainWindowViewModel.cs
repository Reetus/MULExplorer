using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MULExplorer.Mcp;
using MULExplorer.Resources;
using MULExplorer.Services;
using MULExplorer.ViewModels.Tabs;
using UODataAccess;

namespace MULExplorer.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly McpHttpServer _mcp;

    public MainWindowViewModel( Settings settings, McpContext mcpContext )
    {
        Settings = settings;
        McpContext = mcpContext;
        _mcp = new McpHttpServer( mcpContext );

        // Same order as UOFiddler (minus its Start tab).
        Tabs =
        [
            new MultisViewModel(),
            new AnimationsViewModel(),
            new ItemsViewModel(),
            new LandTilesViewModel(),
            new TexturesViewModel(),
            new GumpsViewModel(),
            new SoundsViewModel(),
            new HuesViewModel(),
            new FontsViewModel(),
            new ClilocViewModel(),
            new MapViewModel(),
            new LightViewModel(),
            new SpeechViewModel(),
            new SkillsViewModel(),
            new AnimDataViewModel(),
            new MultiMapViewModel(),
            new DressViewModel(),
            new TileDataViewModel(),
            new RadarColorViewModel(),
            new SkillGrpViewModel(),
            new VerdataViewModel()
        ];

        SelectedTab = Tabs.FirstOrDefault( t => t.Key == settings.LastTab ) ?? Tabs[0];
        ApplyTheme( settings.Theme );
        Backdrop = NormalizeBackdrop( settings.Backdrop );
    }

    public Settings Settings { get; }

    public McpContext McpContext { get; }

    public IReadOnlyList<TabViewModel> Tabs { get; }

    public UOData? Data { get; private set; }

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial string FolderStatus { get; private set; } = Strings.StatusNoFolder;

    [ObservableProperty]
    public partial string McpStatus { get; private set; } = Strings.StatusMcpStopped;

    [ObservableProperty]
    public partial string Theme { get; private set; } = "Dark";

    /// <summary>Requested window backdrop: "Mica", "Acrylic" or "Solid". The window maps it to a transparency hint.</summary>
    [ObservableProperty]
    public partial string Backdrop { get; private set; } = "Mica";

    /// <summary>Set by the app for real runs; null in tests so nothing talks to GitHub.</summary>
    public UpdateService? Updates { get; set; }

    /// <summary>Version of a downloaded update waiting for a restart, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor( nameof( IsUpdateReady ) )]
    public partial string? UpdateReadyVersion { get; private set; }

    public bool IsUpdateReady => UpdateReadyVersion is not null;

    public string UpdateReadyText => UpdateReadyVersion is null ? "" : string.Format( Strings.UpdateReady, UpdateReadyVersion );

    partial void OnUpdateReadyVersionChanged( string? value ) => OnPropertyChanged( nameof( UpdateReadyText ) );

    /// <summary>Downloads a newer release in the background; failures (offline, rate limits) are ignored.</summary>
    public async Task CheckForUpdatesAsync()
    {
        if ( Updates is not { IsInstalled: true } updates )
        {
            return;
        }

        try
        {
            UpdateReadyVersion = await updates.DownloadLatestAsync();
        }
        catch ( Exception e ) when ( e is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException )
        {
            // Not worth interrupting anyone over; the next start tries again.
        }
    }

    [RelayCommand]
    private void RestartToUpdate() => Updates?.ApplyAndRestart();

    public bool IsMcpRunning => _mcp.IsRunning;

    public string McpUrl => _mcp.Url;

    public TabViewModel? FindTab( string keyOrHeader ) =>
        Tabs.FirstOrDefault( t => t.Key.Equals( keyOrHeader, StringComparison.OrdinalIgnoreCase ) ) ??
        Tabs.FirstOrDefault( t => t.Header.Equals( keyOrHeader, StringComparison.OrdinalIgnoreCase ) );

    partial void OnSelectedTabChanged( TabViewModel? value )
    {
        if ( value is not null )
        {
            Settings.LastTab = value.Key;
            _ = value.EnsureLoadedAsync();
        }
    }

    /// <summary>Opens <paramref name="folder" /> (or nothing) and resets every tab.</summary>
    public async Task OpenFolderAsync( string? folder )
    {
        var old = Data;
        Data = folder is null ? null : new UOData( folder );
        McpContext.Data = Data;
        FolderStatus = Data is null ? Strings.StatusNoFolder : string.Format( Strings.StatusFolder, Data.Folder );

        foreach ( var tab in Tabs )
        {
            tab.SetData( Data );
        }

        old?.Dispose();

        if ( SelectedTab is not null )
        {
            await SelectedTab.EnsureLoadedAsync();
        }
    }

    public async Task<bool> TrySetFolderAsync( string folder )
    {
        if ( !Settings.IsClientFolder( folder ) )
        {
            return false;
        }

        Settings.UOFolder = folder;
        Settings.Save();
        await OpenFolderAsync( folder );
        return true;
    }

    [RelayCommand]
    private Task ReloadAsync() => OpenFolderAsync( Data?.Folder );

    [RelayCommand]
    private void SetTheme( string variant )
    {
        ApplyTheme( variant );
        Settings.Theme = Theme;
        Settings.Save();
    }

    [RelayCommand]
    private void SetBackdrop( string backdrop )
    {
        Backdrop = NormalizeBackdrop( backdrop );
        Settings.Backdrop = Backdrop;
        Settings.Save();
    }

    private static string NormalizeBackdrop( string? backdrop ) => backdrop switch
    {
        "Acrylic" => "Acrylic",
        "Solid" => "Solid",
        _ => "Mica"
    };

    public void ApplyTheme( string variant )
    {
        Theme = variant switch
        {
            "Light" => "Light",
            "Default" => "Default",
            _ => "Dark"
        };

        if ( Application.Current is { } app )
        {
            app.RequestedThemeVariant = Theme switch
            {
                "Light" => ThemeVariant.Light,
                "Default" => ThemeVariant.Default,
                _ => ThemeVariant.Dark
            };
        }
    }

    [RelayCommand]
    public async Task StartMcpAsync()
    {
        try
        {
            await _mcp.StartAsync( Settings.McpPort );
            McpStatus = string.Format( Strings.StatusMcpRunning, _mcp.Url );
        }
        catch ( Exception e ) when ( e is IOException or System.Net.Sockets.SocketException or InvalidOperationException )
        {
            McpStatus = string.Format( Strings.StatusMcpFailed, e.Message );
        }

        OnPropertyChanged( nameof( IsMcpRunning ) );
    }

    [RelayCommand]
    public async Task StopMcpAsync()
    {
        await _mcp.StopAsync();
        McpStatus = Strings.StatusMcpStopped;
        OnPropertyChanged( nameof( IsMcpRunning ) );
    }

    public async ValueTask DisposeAsync()
    {
        await _mcp.DisposeAsync();
        Data?.Dispose();
    }
}
