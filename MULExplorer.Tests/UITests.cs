using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MULExplorer.Mcp;
using MULExplorer.ViewModels.Tabs;
using MULExplorer.Views;

namespace MULExplorer.Tests;

/// <summary>Headless UI tests: the real window, styles and view-models on Avalonia's headless platform.</summary>
public sealed class UITests
{
    private static readonly string[] ExpectedTabs =
    [
        "Multis", "Animations", "Items", "Land Tiles", "Textures", "Gumps", "Sounds", "Hues", "Fonts", "CliLoc", "Map",
        "Light", "Speech", "Skills", "AnimData", "MultiMap/Facets", "Dress", "TileData", "RadarColor", "SkillGrp", "Verdata"
    ];

    private static async Task<(MainWindow Window, ViewModels.MainWindowViewModel VM, IUIAutomation UI)> OpenAsync( bool withData = true )
    {
        var folder = withData ? UOTestData.RequireFolder() : null;
        var (window, vm) = App.CreateMainWindow( UOTestData.IsolatedSettings( folder ) );
        window.Show();
        await vm.OpenFolderAsync( folder );
        Dispatcher.UIThread.RunJobs();
        return ( window, vm, vm.McpContext.UI! );
    }

    [AvaloniaFact]
    public async Task Tabs_MatchUOFiddlerOrder()
    {
        var (window, _, ui) = await OpenAsync( withData: false );

        Assert.Equal( ExpectedTabs, await ui.ListTabsAsync() );
        window.Close();
    }

    [AvaloniaFact]
    public async Task NoFolder_ShowsMessageInsteadOfCrashing()
    {
        var (window, vm, ui) = await OpenAsync( withData: false );

        await ui.SelectTabAsync( "Items" );
        var state = await ui.GetTabStateAsync( "Items" );

        Assert.False( state["loaded"]!.GetValue<bool>() );
        Assert.NotNull( state["error"] );
        Assert.Null( vm.Data );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Items_SelectSearchAndHue()
    {
        var (window, _, ui) = await OpenAsync();

        await ui.SelectTabAsync( "Items" );
        Assert.True( await ui.SelectEntryAsync( "Items", 0x0EED ) );

        var state = await ui.GetTabStateAsync( "Items" );
        Assert.True( state["loaded"]!.GetValue<bool>() );
        Assert.Equal( 0x0EED, state["selectedId"]!.GetValue<int>() );
        Assert.Equal( "gold coin", state["selectedName"]!.GetValue<string>() );
        Assert.NotNull( state["previewSize"] );

        var visible = await ui.SearchAsync( "Items", "gold coin" );
        Assert.InRange( visible, 1, 50 );

        var hued = await ui.InvokeAsync( "Items", "set_preview_hue", new JsonObject { ["hue"] = 0x21 } );
        Assert.Equal( 0x21, hued["previewHue"]!.GetValue<int>() );

        Assert.False( await ui.SelectEntryAsync( "Items", -5 ) );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Items_SelectingHiddenEntryClearsFilter()
    {
        var (window, vm, ui) = await OpenAsync();
        var items = ( ItemsViewModel )vm.FindTab( "Items" )!;
        await items.EnsureLoadedAsync();

        items.Search( "gold coin" );
        Assert.True( items.SelectEntry( 0x0001 ) || !vm.Data!.Art.IsValidItem( 1 ) );
        Assert.Contains( items.SelectedId, items.VisibleIds );
        window.Close();
        await Task.CompletedTask;
    }

    /// <summary>Regression: selecting before the first layout used to leave the grid scrolled to the end.</summary>
    [AvaloniaFact]
    public async Task Items_GridScrollsSelectionIntoView()
    {
        var (window, vm, ui) = await OpenAsync();
        await ui.SelectEntryAsync( "Items", 0x0EED );
        await ui.SelectTabAsync( "Items" );
        Dispatcher.UIThread.RunJobs();

        var grid = window.GetVisualDescendants().OfType<Controls.TileGrid>().Single();
        var row = Controls.TileGrid.IndexOf( grid.Ids!, 0x0EED ) / grid.Columns;

        Assert.True( grid.Columns > 1 );
        Assert.InRange( row, grid.FirstRow, grid.FirstRow + grid.VisibleRows - 1 );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Screenshot_IsPng()
    {
        var (window, _, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Items" );

        var png = await ui.ScreenshotAsync();

        Assert.True( png.Length > 1000 );
        Assert.Equal( [0x89, 0x50, 0x4E, 0x47], png[..4] );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Theme_SwitchesBetweenDarkAndLight()
    {
        var (window, _, ui) = await OpenAsync( withData: false );

        Assert.Equal( ThemeVariant.Dark, window.ActualThemeVariant );
        await ui.SetThemeAsync( "Light" );
        Assert.Equal( ThemeVariant.Light, window.ActualThemeVariant );
        await ui.SetThemeAsync( "Dark" );
        Assert.Equal( ThemeVariant.Dark, window.ActualThemeVariant );
        window.Close();
    }

    /// <summary>Text uses the softened palette, not Fluent's pure white/black.</summary>
    [AvaloniaFact]
    public async Task Palette_UsesSoftTextColors()
    {
        var (window, _, ui) = await OpenAsync( withData: false );

        Assert.Equal( Avalonia.Media.Color.Parse( "#FFD4D4D4" ), BaseHigh() );
        await ui.SetThemeAsync( "Light" );
        Assert.Equal( Avalonia.Media.Color.Parse( "#FF242424" ), BaseHigh() );
        await ui.SetThemeAsync( "Dark" );
        window.Close();

        Avalonia.Media.Color BaseHigh() =>
            window.TryFindResource( "SystemBaseHighColor", window.ActualThemeVariant, out var value ) && value is Avalonia.Media.Color c ? c : default;
    }

    [AvaloniaFact]
    public async Task Backdrop_MapsToTransparencyHint()
    {
        var (window, vm, ui) = await OpenAsync( withData: false );

        Assert.Equal( "Mica", vm.Backdrop );
        Assert.Equal( Avalonia.Controls.WindowTransparencyLevel.Mica, window.TransparencyLevelHint[0] );

        vm.SetBackdropCommand.Execute( "Acrylic" );
        Assert.Equal( Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur, window.TransparencyLevelHint[0] );

        vm.SetBackdropCommand.Execute( "Solid" );
        Assert.Equal( [Avalonia.Controls.WindowTransparencyLevel.None], window.TransparencyLevelHint );
        Assert.Equal( "Solid", vm.Settings.Backdrop );
        Assert.Equal( "Solid", ( await ui.GetWindowInfoAsync() )["backdrop"]!.GetValue<string>() );
        window.Close();
    }

    [AvaloniaFact]
    public async Task EveryTab_LoadsWithoutError()
    {
        var (window, vm, ui) = await OpenAsync();

        foreach ( var tab in vm.Tabs )
        {
            await ui.SelectTabAsync( tab.Header );
            var state = await ui.GetTabStateAsync( tab.Header );

            Assert.True( state["error"] is null, $"{tab.Key}: {state["error"]}" );
            Assert.Equal( tab.IsImplemented, state["loaded"]!.GetValue<bool>() );
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task McpUITools_DriveTheWindow()
    {
        var (window, vm, _) = await OpenAsync();
        var registry = MULExplorerMcpServer.CreateRegistry( vm.McpContext );

        var select = await McpTestExtensions.Call( registry, "ui_select_entry", new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["tab"] = System.Text.Json.JsonSerializer.SerializeToElement( "Items", McpTestJson.Default.String ),
            ["id"] = System.Text.Json.JsonSerializer.SerializeToElement( "0x0EED", McpTestJson.Default.String )
        } );
        Assert.True( select.IsError is not true );

        var shot = await McpTestExtensions.Call( registry, "ui_screenshot", null );
        Assert.IsType<ModelContextProtocol.Protocol.ImageContentBlock>( shot.Content[0] );

        var info = await McpTestExtensions.Call( registry, "ui_get_window_info", null );
        Assert.Contains( vm.Data!.Folder.Replace( "\\", "\\\\" ), ( ( ModelContextProtocol.Protocol.TextContentBlock )info.Content[0] ).Text );
        window.Close();
    }
}
