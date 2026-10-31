using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MULExplorer.Mcp;
using MULExplorer.ViewModels;
using MULExplorer.Views;

namespace MULExplorer.Tests;

/// <summary>Headless UI tests for the Multis, Land Tiles, Textures, TileData and RadarColor tabs.</summary>
public sealed class ArtTabsUITests
{
    internal static async Task<(MainWindow Window, MainWindowViewModel VM, IUIAutomation UI)> OpenAsync()
    {
        var folder = UOTestData.RequireFolder();
        var (window, vm) = App.CreateMainWindow( UOTestData.IsolatedSettings( folder ) );
        window.Show();
        await vm.OpenFolderAsync( folder );
        Dispatcher.UIThread.RunJobs();
        return ( window, vm, vm.McpContext.UI! );
    }

    private static async Task AssertScreenshotAsync( IUIAutomation ui, string tab )
    {
        await ui.SelectTabAsync( tab );

        foreach ( var theme in new[] { "Light", "Dark" } )
        {
            await ui.SetThemeAsync( theme );
            var png = await ui.ScreenshotAsync();
            Assert.True( png.Length > 5000, $"{tab}/{theme} screenshot is suspiciously small" );
            Assert.Equal( [0x89, 0x50, 0x4E, 0x47], png[..4] );
        }
    }

    [AvaloniaFact]
    public async Task Multis_SelectSearchHeightAndSource()
    {
        var (window, _, ui) = await OpenAsync();

        await ui.SelectTabAsync( "Multis" );
        Assert.True( await ui.SelectEntryAsync( "Multis", 150 ) );

        var state = await ui.GetTabStateAsync( "Multis" );
        Assert.True( state["loaded"]!.GetValue<bool>() );
        Assert.Equal( 150, state["selectedId"]!.GetValue<int>() );
        Assert.Equal( "Large Marble Patio House", state["selectedName"]!.GetValue<string>() );
        Assert.True( state["components"]!.GetValue<int>() > 100 );
        Assert.NotNull( state["imageSize"] );
        var maxHeight = state["maxHeight"]!.GetValue<int>();
        Assert.Equal( maxHeight, state["heightCut"]!.GetValue<int>() );

        var cut = await ui.InvokeAsync( "Multis", "set_max_height", new JsonObject { ["height"] = 0 } );
        Assert.Equal( 0, cut["heightCut"]!.GetValue<int>() );
        Assert.Equal( state["imageSize"]!.GetValue<string>(), cut["imageSize"]!.GetValue<string>() );

        var visible = await ui.SearchAsync( "Multis", "tower" );
        Assert.InRange( visible, 1, 50 );

        var sources = state["sources"]!.AsArray().Select( s => s!.GetValue<string>() ).ToList();

        if ( sources.Contains( "MUL" ) )
        {
            var mul = await ui.InvokeAsync( "Multis", "set_source", new JsonObject { ["source"] = "MUL" } );
            Assert.Equal( "MUL", mul["source"]!.GetValue<string>() );
            Assert.True( mul["total"]!.GetValue<int>() > 0 );
        }

        Assert.False( await ui.SelectEntryAsync( "Multis", -1 ) );
        await AssertScreenshotAsync( ui, "Multis" );
        window.Close();
    }

    [AvaloniaFact]
    public async Task LandTiles_SelectSearchAndFreeSlots()
    {
        var (window, _, ui) = await OpenAsync();

        await ui.SelectTabAsync( "Land Tiles" );
        Assert.True( await ui.SelectEntryAsync( "LandTiles", 3 ) );

        var state = await ui.GetTabStateAsync( "LandTiles" );
        Assert.Equal( 3, state["selectedId"]!.GetValue<int>() );
        Assert.Equal( "grass", state["selectedName"]!.GetValue<string>() );
        Assert.Equal( "44x44", state["previewSize"]!.GetValue<string>() );
        Assert.True( state["hasTexture"]!.GetValue<bool>() );

        var total = state["total"]!.GetValue<int>();
        var grass = await ui.SearchAsync( "LandTiles", "grass" );
        Assert.InRange( grass, 1, total - 1 );

        await ui.SearchAsync( "LandTiles", "" );
        var free = await ui.InvokeAsync( "LandTiles", "set_show_free_slots", new JsonObject { ["value"] = true } );
        Assert.Equal( 0x4000, free["visible"]!.GetValue<int>() );

        Assert.False( await ui.SelectEntryAsync( "LandTiles", 0x4000 ) );
        await ui.InvokeAsync( "LandTiles", "set_show_free_slots", new JsonObject { ["value"] = false } );
        await AssertScreenshotAsync( ui, "LandTiles" );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Textures_SelectAndSearchByLandName()
    {
        var (window, vm, ui) = await OpenAsync();
        var grassTexture = vm.Data!.TileData.GetLand( 3 ).TextureId;

        await ui.SelectTabAsync( "Textures" );
        Assert.True( await ui.SelectEntryAsync( "Textures", grassTexture ) );

        var state = await ui.GetTabStateAsync( "Textures" );
        Assert.Equal( grassTexture, state["selectedId"]!.GetValue<int>() );
        Assert.Contains( state["previewSize"]!.GetValue<string>(), new[] { "64x64", "128x128" } );
        Assert.Contains( state["usedByLand"]!.AsArray(), n => n!.GetValue<int>() == 3 );

        var visible = await ui.SearchAsync( "Textures", "grass" );
        Assert.InRange( visible, 1, state["total"]!.GetValue<int>() - 1 );

        await AssertScreenshotAsync( ui, "Textures" );
        window.Close();
    }

    [AvaloniaFact]
    public async Task TileData_ItemsAndLandSubViews()
    {
        var (window, _, ui) = await OpenAsync();

        await ui.SelectTabAsync( "TileData" );
        Assert.True( await ui.SelectEntryAsync( "TileData", 0x0EED ) );

        var items = await ui.GetTabStateAsync( "TileData" );
        Assert.Equal( "items", items["view"]!.GetValue<string>() );
        Assert.Equal( "gold coin", items["selectedName"]!.GetValue<string>() );
        Assert.Contains( items["selectedFlags"]!.AsArray(), f => f!.GetValue<string>() == "Generic" );
        Assert.NotNull( items["previewSize"] );
        Assert.InRange( await ui.SearchAsync( "TileData", "gold coin" ), 1, 50 );

        var land = await ui.InvokeAsync( "TileData", "show_land", new JsonObject() );
        Assert.Equal( "land", land["view"]!.GetValue<string>() );
        Assert.Equal( 0x4000, land["total"]!.GetValue<int>() );
        Assert.True( await ui.SelectEntryAsync( "TileData", 3 ) );
        Assert.Equal( "grass", ( await ui.GetTabStateAsync( "TileData" ) )["selectedName"]!.GetValue<string>() );
        Assert.False( await ui.SelectEntryAsync( "TileData", 0x4000 ) );

        await AssertScreenshotAsync( ui, "TileData" );
        await ui.InvokeAsync( "TileData", "show_items", new JsonObject() );
        await AssertScreenshotAsync( ui, "TileData" );
        window.Close();
    }

    [AvaloniaFact]
    public async Task RadarColor_ItemsAndLand()
    {
        var (window, vm, ui) = await OpenAsync();

        await ui.SelectTabAsync( "RadarColor" );
        Assert.True( await ui.SelectEntryAsync( "RadarColor", 0x0EED ) );

        var items = await ui.GetTabStateAsync( "RadarColor" );
        Assert.Equal( $"0x{vm.Data!.RadarColors.GetItemColor( 0x0EED ):X4}", items["color"]!.GetValue<string>() );
        Assert.NotNull( items["previewSize"] );

        var land = await ui.InvokeAsync( "RadarColor", "show_land", new JsonObject() );
        Assert.Equal( "land", land["view"]!.GetValue<string>() );
        Assert.True( await ui.SelectEntryAsync( "RadarColor", 3 ) );
        var grass = await ui.GetTabStateAsync( "RadarColor" );
        Assert.Equal( $"0x{vm.Data.RadarColors.GetLandColor( 3 ):X4}", grass["color"]!.GetValue<string>() );
        Assert.InRange( await ui.SearchAsync( "RadarColor", "grass" ), 1, 0x3FFF );

        await AssertScreenshotAsync( ui, "RadarColor" );
        window.Close();
    }
}
