using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MULExplorer.Mcp;
using MULExplorer.ViewModels.Tabs;
using MULExplorer.Views;

namespace MULExplorer.Tests;

/// <summary>Headless UI tests for the Gumps, Hues, Fonts, CliLoc and Light tabs, driven through <see cref="IUIAutomation" />.</summary>
public sealed class UIAssetTabsUITests
{
    private static async Task<(MainWindow Window, ViewModels.MainWindowViewModel VM, IUIAutomation UI)> OpenAsync()
    {
        var folder = UOTestData.RequireFolder();
        var (window, vm) = App.CreateMainWindow( UOTestData.IsolatedSettings( folder ) );
        window.Show();
        await vm.OpenFolderAsync( folder );
        Dispatcher.UIThread.RunJobs();
        return ( window, vm, vm.McpContext.UI! );
    }

    private static async Task AssertScreenshotsAsync( IUIAutomation ui, string tab )
    {
        foreach ( var theme in new[] { "Light", "Dark" } )
        {
            await ui.SetThemeAsync( theme );
            var png = await ui.ScreenshotAsync();
            Assert.Equal( [0x89, 0x50, 0x4E, 0x47], png[..4] );
            Assert.True( png.Length > 5000, $"{tab} {theme} screenshot looks empty" );
        }
    }

    [AvaloniaFact]
    public async Task Gumps_SelectFilterAndHue()
    {
        var (window, _, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Gumps" );

        var state = await ui.GetTabStateAsync( "Gumps" );
        Assert.True( state["loaded"]!.GetValue<bool>(), state["error"]?.ToString() );
        Assert.True( state["total"]!.GetValue<int>() > 1000 );

        Assert.True( await ui.SelectEntryAsync( "Gumps", 60 ) );
        state = await ui.GetTabStateAsync( "Gumps" );
        Assert.Equal( 60, state["selectedId"]!.GetValue<int>() );
        Assert.Equal( "backpack", state["selectedName"]!.GetValue<string>() );
        Assert.NotNull( state["previewSize"] );

        Assert.InRange( await ui.SearchAsync( "Gumps", "backpack" ), 1, 20 );
        var tagged = await ui.InvokeAsync( "Gumps", "set_tag", new JsonObject { ["tag"] = "container" } );
        Assert.Equal( "container", tagged["tag"]!.GetValue<string>() );

        var hued = await ui.InvokeAsync( "Gumps", "set_preview_hue", new JsonObject { ["hue"] = 0x21, ["only_gray"] = true } );
        Assert.Equal( 0x21, hued["previewHue"]!.GetValue<int>() );
        Assert.True( hued["onlyGray"]!.GetValue<bool>() );

        // Selecting an id hidden by the filters clears them.
        Assert.True( await ui.SelectEntryAsync( "Gumps", 0x0001 ) || !( await ui.GetTabStateAsync( "Gumps" ) )["total"]!.GetValue<int>().Equals( 0 ) );
        Assert.False( await ui.SelectEntryAsync( "Gumps", -3 ) );

        await AssertScreenshotsAsync( ui, "Gumps" );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hues_SelectSearchAndPreview()
    {
        var (window, vm, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Hues" );

        Assert.True( await ui.SelectEntryAsync( "Hues", 0x21 ) );
        var state = await ui.GetTabStateAsync( "Hues" );
        Assert.Equal( 0x21, state["selectedHue"]!.GetValue<int>() );
        Assert.Equal( 32, state["colors"]!.AsArray().Count );
        Assert.NotNull( state["itemPreviewSize"] );

        var name = vm.Data!.Hues.Get( 0x21 )!.Name;
        Assert.True( await ui.SearchAsync( "Hues", name ) >= 1 );

        state = await ui.InvokeAsync( "Hues", "show_game_numbers", new JsonObject { ["value"] = true } );
        Assert.True( state["showGameNumbers"]!.GetValue<bool>() );
        Assert.Equal( 0x21, state["selectedHue"]!.GetValue<int>() );

        state = await ui.InvokeAsync( "Hues", "set_preview_item", new JsonObject { ["id"] = "0x0EED" } );
        Assert.Equal( 0x0EED, state["previewItem"]!.GetValue<int>() );
        Assert.NotNull( state["itemPreviewSize"] );
        state = await ui.InvokeAsync( "Hues", "set_only_gray", new JsonObject { ["value"] = true } );
        Assert.True( state["onlyGray"]!.GetValue<bool>() );

        Assert.False( await ui.SelectEntryAsync( "Hues", 0 ) );
        await ui.SearchAsync( "Hues", "" );
        await AssertScreenshotsAsync( ui, "Hues" );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Fonts_SwitchFontsAndRenderPreview()
    {
        var (window, vm, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Fonts" );

        var state = await ui.GetTabStateAsync( "Fonts" );
        Assert.Equal( "ascii", state["kind"]!.GetValue<string>() );
        Assert.Equal( 224, state["total"]!.GetValue<int>() );
        Assert.NotNull( state["textPreviewSize"] );

        Assert.True( await ui.SelectEntryAsync( "Fonts", 'W' ) );
        state = await ui.GetTabStateAsync( "Fonts" );
        Assert.Equal( 'W', state["selectedChar"]!.GetValue<int>() );
        Assert.NotNull( state["selectedWidth"] );

        state = await ui.InvokeAsync( "Fonts", "set_font", new JsonObject { ["kind"] = "unicode", ["font"] = 1 } );
        Assert.Equal( "unicode", state["kind"]!.GetValue<string>() );
        Assert.Equal( 1, state["font"]!.GetValue<int>() );
        Assert.True( state["total"]!.GetValue<int>() > 100 );

        state = await ui.InvokeAsync( "Fonts", "set_preview_text", new JsonObject { ["text"] = "Hello\nBritannia" } );
        var size = state["textPreviewSize"]!.GetValue<string>();
        state = await ui.InvokeAsync( "Fonts", "set_preview_hue", new JsonObject { ["hue"] = 0x35 } );
        Assert.Equal( size, state["textPreviewSize"]!.GetValue<string>() );

        Assert.Equal( 1, await ui.SearchAsync( "Fonts", "U+0041" ) );
        await ui.SearchAsync( "Fonts", "" );

        state = await ui.InvokeAsync( "Fonts", "set_font", new JsonObject { ["kind"] = "ascii", ["font"] = 3 } );
        Assert.Equal( 3, state["font"]!.GetValue<int>() );
        Assert.Equal( vm.Data!.Fonts.Ascii.Count, state["fonts"]!.AsArray().Count );

        await AssertScreenshotsAsync( ui, "Fonts" );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cliloc_LanguagesSearchAndGoTo()
    {
        var (window, vm, ui) = await OpenAsync();
        await ui.SelectTabAsync( "CliLoc" );

        var state = await ui.GetTabStateAsync( "CliLoc" );
        Assert.Equal( "enu", state["language"]!.GetValue<string>() );
        Assert.True( state["total"]!.GetValue<int>() > 10000 );

        var cliloc = vm.Data!.Cliloc;
        var target = cliloc.Entries[cliloc.Count / 2];
        state = await ui.InvokeAsync( "CliLoc", "goto", new JsonObject { ["number"] = target.Number } );
        Assert.Equal( target.Number, state["selectedNumber"]!.GetValue<int>() );
        Assert.Equal( target.Text, state["selectedText"]!.GetValue<string>() );

        var visible = await ui.SearchAsync( "CliLoc", "gold coin" );
        Assert.InRange( visible, 1, 1000 );

        // goto an entry hidden by the filter clears it
        Assert.True( await ui.SelectEntryAsync( "CliLoc", target.Number ) );
        state = await ui.GetTabStateAsync( "CliLoc" );
        Assert.Equal( "", state["search"]!.GetValue<string>() );

        await ui.InvokeAsync( "CliLoc", "set_regex", new JsonObject { ["value"] = true } );
        Assert.True( await ui.SearchAsync( "CliLoc", "^gold" ) >= 1 );
        Assert.Equal( 0, await ui.SearchAsync( "CliLoc", "(" ) );
        Assert.NotNull( ( await ui.GetTabStateAsync( "CliLoc" ) )["message"] );
        await ui.SearchAsync( "CliLoc", "" );
        await ui.InvokeAsync( "CliLoc", "set_regex", new JsonObject { ["value"] = false } );

        var other = state["languages"]!.AsArray().Select( n => n!.GetValue<string>() ).First( l => l != "enu" );
        state = await ui.InvokeAsync( "CliLoc", "set_language", new JsonObject { ["language"] = other } );
        Assert.Equal( other, state["language"]!.GetValue<string>() );
        Assert.True( state["total"]!.GetValue<int>() > 0 );

        await ui.InvokeAsync( "CliLoc", "set_language", new JsonObject { ["language"] = "enu" } );
        await AssertScreenshotsAsync( ui, "CliLoc" );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Light_SelectAndInGamePreview()
    {
        var (window, vm, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Light" );

        var id = vm.Data!.Lights.ValidIds[1];
        Assert.True( await ui.SelectEntryAsync( "Light", id ) );
        var state = await ui.GetTabStateAsync( "Light" );
        Assert.Equal( id, state["selectedId"]!.GetValue<int>() );
        Assert.False( state["inGame"]!.GetValue<bool>() );
        var rawSize = state["previewSize"]!.GetValue<string>();

        state = await ui.InvokeAsync( "Light", "set_in_game", new JsonObject { ["value"] = true } );
        Assert.True( state["inGame"]!.GetValue<bool>() );
        Assert.NotEqual( rawSize, state["previewSize"]!.GetValue<string>() );

        state = await ui.InvokeAsync( "Light", "set_land", new JsonObject { ["id"] = "0x0004" } );
        Assert.Equal( 4, state["landTile"]!.GetValue<int>() );
        state = await ui.InvokeAsync( "Light", "set_light_source", new JsonObject { ["id"] = -1 } );
        Assert.Equal( -1, state["lightSource"]!.GetValue<int>() );
        await ui.InvokeAsync( "Light", "set_light_source", new JsonObject { ["id"] = "0x0B20" } );

        Assert.False( await ui.SelectEntryAsync( "Light", 9999 ) );
        await AssertScreenshotsAsync( ui, "Light" );
        window.Close();
    }
}
