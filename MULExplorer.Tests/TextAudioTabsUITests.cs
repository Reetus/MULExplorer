using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModelContextProtocol.Protocol;
using MULExplorer.Mcp;
using MULExplorer.Services;
using MULExplorer.ViewModels.Tabs;
using MULExplorer.Views;

namespace MULExplorer.Tests;

/// <summary>Records what would have been played instead of making noise.</summary>
internal sealed class FakeSoundPlayer( bool available = true ) : ISoundPlayer
{
    public List<byte[]> Played { get; } = [];

    public int Stops { get; private set; }

    public bool IsAvailable => available;

    public void Play( byte[] wav ) => Played.Add( wav );

    public void Stop() => Stops++;
}

/// <summary>MCP tools for the Sounds/Speech/Skills/SkillGrp/Verdata tabs.</summary>
public sealed class TextAudioMcpTests
{
    private static ToolRegistry Registry( string? folder = null ) =>
        MULExplorerMcpServer.CreateRegistry( new McpContext { Data = folder is null ? UOTestData.Require() : new UODataAccess.UOData( folder ) } );

    private static IReadOnlyDictionary<string, JsonElement> Args( Dictionary<string, object> values ) =>
        JsonSerializer.Deserialize( JsonSerializer.Serialize( values, McpTestJson.Default.DictionaryStringObject ), McpTestJson.Default.DictionaryStringJsonElement )!;

    private static JsonNode TextJson( CallToolResult result )
    {
        Assert.True( result.IsError is not true, ( result.Content.FirstOrDefault() as TextContentBlock )?.Text );
        return JsonNode.Parse( result.Content.OfType<TextContentBlock>().First().Text )!;
    }

    [Fact]
    public async Task ListSounds_FiltersByName()
    {
        var json = TextJson( await McpTestExtensions.Call( Registry(), "list_sounds", Args( new() { ["name"] = ".wav", ["limit"] = 3 } ) ) );

        Assert.True( json["total"]!.GetValue<int>() > 1000 );
        Assert.Equal( 3, json["sounds"]!.AsArray().Count );
        Assert.True( json["sounds"]![0]!["duration"]!.GetValue<double>() > 0 );
    }

    [Fact]
    public async Task GetSoundWav_ReturnsAudioContent()
    {
        var registry = Registry();
        var id = UOTestData.Require().Sounds.GetAll()[0].Id;

        var info = TextJson( await McpTestExtensions.Call( registry, "get_sound_info", Args( new() { ["id"] = $"0x{id:X}" } ) ) );
        var result = await McpTestExtensions.Call( registry, "get_sound_wav", Args( new() { ["id"] = id } ) );

        var audio = Assert.IsType<AudioContentBlock>( result.Content[0] );
        Assert.Equal( "audio/wav", audio.MimeType );
        Assert.True( audio.DecodedData.Span[..4].SequenceEqual( "RIFF"u8 ) );
        Assert.Equal( 44 + info["pcm_bytes"]!.GetValue<int>(), audio.DecodedData.Length );
        Assert.Equal( audio.DecodedData.Length, TextJson( result )["wav_bytes"]!.GetValue<int>() );
    }

    [Fact]
    public async Task GetSoundInfo_EmptySlotIsError()
    {
        var result = await McpTestExtensions.Call( Registry(), "get_sound_info", Args( new() { ["id"] = 0xFFFE } ) );
        Assert.True( result.IsError );
    }

    [Fact]
    public async Task SpeechTools_SearchAndMatch()
    {
        var registry = Registry();

        var search = TextJson( await McpTestExtensions.Call( registry, "search_speech", Args( new() { ["text"] = "bank" } ) ) );
        Assert.True( search["total"]!.GetValue<int>() > 0 );
        Assert.All( search["entries"]!.AsArray(), e => Assert.Contains( "bank", e!["keyword"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase ) );

        var match = TextJson( await McpTestExtensions.Call( registry, "match_speech_keywords", Args( new() { ["phrase"] = "I wish to bank" } ) ) );
        Assert.Contains( match["entries"]!.AsArray(), e => e!["keyword"]!.GetValue<string>() == "*bank*" );
    }

    [Fact]
    public async Task SkillTools_ListSkillsAndGroups()
    {
        var registry = Registry();

        var skills = TextJson( await McpTestExtensions.Call( registry, "list_skills", null ) ).AsArray();
        Assert.Equal( "Alchemy", skills[0]!["name"]!.GetValue<string>() );
        Assert.NotNull( skills[0]!["group"] );

        var groups = TextJson( await McpTestExtensions.Call( registry, "list_skill_groups", null ) );
        Assert.Equal( "Misc", groups["groups"]![0]!["name"]!.GetValue<string>() );
        Assert.Equal( skills.Count, groups["groups"]!.AsArray().Sum( g => g!["skills"]!.AsArray().Count ) );
    }

    [Fact]
    public async Task ListVerdataPatches_AbsentAndSynthetic()
    {
        if ( !UOTestData.Require().Files.Exists( "verdata.mul" ) )
        {
            var absent = TextJson( await McpTestExtensions.Call( Registry(), "list_verdata_patches", null ) );
            Assert.False( absent["present"]!.GetValue<bool>() );
            Assert.Equal( 0, absent["total"]!.GetValue<int>() );
        }

        using var dir = new TempDir();
        File.WriteAllBytes( dir.File( "verdata.mul" ), TextAudioFiles.VerdataFile( [( 4, 0x10, 0, [1] ), ( 12, 0x20, 0, [2] ), ( 4, 0x11, 0, [3] )] ) );

        var json = TextJson( await McpTestExtensions.Call( Registry( dir.Path ), "list_verdata_patches", Args( new() { ["file"] = 4 } ) ) );
        Assert.True( json["present"]!.GetValue<bool>() );
        Assert.Equal( 2, json["total"]!.GetValue<int>() );
        Assert.Equal( "art.mul", json["patches"]![0]!["file_name"]!.GetValue<string>() );
        Assert.Equal( 2, json["files"]!.AsArray().Count );
    }
}

/// <summary>Headless UI tests for the Sounds/Speech/Skills/SkillGrp/Verdata tabs, driven through <see cref="IUIAutomation" />.</summary>
public sealed class TextAudioUITests
{
    private static async Task<(MainWindow Window, ViewModels.MainWindowViewModel VM, IUIAutomation UI)> OpenAsync( string? folder = null )
    {
        folder ??= UOTestData.RequireFolder();
        var (window, vm) = App.CreateMainWindow( UOTestData.IsolatedSettings( folder ) );
        window.Show();
        await vm.OpenFolderAsync( folder );
        Dispatcher.UIThread.RunJobs();
        return ( window, vm, vm.McpContext.UI! );
    }

    [AvaloniaFact]
    public async Task Sounds_SelectSearchPlayStop()
    {
        var (window, vm, ui) = await OpenAsync();
        var sounds = ( SoundsViewModel )vm.FindTab( "Sounds" )!;
        var player = new FakeSoundPlayer();
        sounds.Player = player;
        await ui.SelectTabAsync( "Sounds" );

        var state = await ui.GetTabStateAsync( "Sounds" );
        Assert.True( state["loaded"]!.GetValue<bool>() );
        Assert.True( state["total"]!.GetValue<int>() > 1000 );
        Assert.True( state["playbackAvailable"]!.GetValue<bool>() );

        var first = vm.Data!.Sounds.GetAll()[3];
        Assert.True( await ui.SelectEntryAsync( "Sounds", first.Id ) );
        state = await ui.GetTabStateAsync( "Sounds" );
        Assert.Equal( first.Name, state["selectedName"]!.GetValue<string>() );

        var visible = await ui.SearchAsync( "Sounds", first.Name );
        Assert.InRange( visible, 1, 20 );
        Assert.Equal( first.Id, ( await ui.GetTabStateAsync( "Sounds" ) )["selectedId"]!.GetValue<int>() ); // selection survives the filter

        var played = await ui.InvokeAsync( "Sounds", "play", new JsonObject() );
        Assert.True( played["playing"]!.GetValue<bool>() );
        Assert.Single( player.Played );
        Assert.Equal( vm.Data.Sounds.GetWav( first.Id ), player.Played[0] );

        var stopped = await ui.InvokeAsync( "Sounds", "stop", new JsonObject() );
        Assert.False( stopped["playing"]!.GetValue<bool>() );
        Assert.True( player.Stops > 0 );

        var sorted = await ui.InvokeAsync( "Sounds", "sort_by_name", new JsonObject { ["enabled"] = true } );
        Assert.True( sorted["sortByName"]!.GetValue<bool>() );

        Assert.False( await ui.SelectEntryAsync( "Sounds", -1 ) );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sounds_PlayDisabledWithoutBackend()
    {
        var (window, vm, ui) = await OpenAsync();
        var sounds = ( SoundsViewModel )vm.FindTab( "Sounds" )!;
        sounds.Player = new FakeSoundPlayer( available: false );
        await ui.SelectTabAsync( "Sounds" );

        var button = window.GetVisualDescendants().OfType<Button>().Single( b => b.Name == "PlayButton" );
        Assert.False( button.IsEffectivelyEnabled );
        Assert.NotNull( ToolTip.GetTip( button ) );
        Assert.False( ( await ui.GetTabStateAsync( "Sounds" ) )["playbackAvailable"]!.GetValue<bool>() );
        await Assert.ThrowsAsync<InvalidOperationException>( () => ui.InvokeAsync( "Sounds", "play", new JsonObject() ) );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Speech_SearchByKeywordAndId()
    {
        var (window, _, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Speech" );

        var total = ( await ui.GetTabStateAsync( "Speech" ) )["total"]!.GetValue<int>();
        Assert.True( total > 1000 );

        Assert.InRange( await ui.SearchAsync( "Speech", "bank" ), 1, total - 1 );
        var byId = await ui.SearchAsync( "Speech", "0x2" );
        Assert.True( byId > 0 );

        Assert.True( await ui.SelectEntryAsync( "Speech", 2 ) );
        var state = await ui.GetTabStateAsync( "Speech" );
        Assert.Equal( 2, state["selectedId"]!.GetValue<int>() );
        Assert.False( await ui.SelectEntryAsync( "Speech", 0xFFFF ) );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Skills_SelectAndSearch()
    {
        var (window, _, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Skills" );

        Assert.True( await ui.SelectEntryAsync( "Skills", 1 ) );
        var state = await ui.GetTabStateAsync( "Skills" );
        Assert.Equal( "Anatomy", state["selectedName"]!.GetValue<string>() );
        Assert.True( state["selectedIsAction"]!.GetValue<bool>() );
        Assert.NotNull( state["selectedGroup"] );

        Assert.Equal( 1, await ui.SearchAsync( "Skills", "Alchemy" ) );
        Assert.False( await ui.SelectEntryAsync( "Skills", 999 ) );
        window.Close();
    }

    [AvaloniaFact]
    public async Task SkillGrp_TreeSelectionAndSearch()
    {
        var (window, vm, ui) = await OpenAsync();
        await ui.SelectTabAsync( "SkillGrp" );

        var state = await ui.GetTabStateAsync( "SkillGrp" );
        Assert.Equal( vm.Data!.SkillGroups.Groups.Count, state["visibleGroups"]!.GetValue<int>() );

        Assert.True( await ui.SelectEntryAsync( "SkillGrp", 0 ) ); // Alchemy
        state = await ui.GetTabStateAsync( "SkillGrp" );
        Assert.Equal( "Alchemy", state["selectedText"]!.GetValue<string>() );
        Assert.Contains( state["expanded"]!.AsArray(), g => g!.GetValue<string>() == state["selectedGroup"]!.GetValue<string>() );

        Assert.Equal( 1, await ui.SearchAsync( "SkillGrp", "Alchemy" ) );
        state = await ui.InvokeAsync( "SkillGrp", "select_group", new JsonObject { ["index"] = 0 } );
        Assert.Equal( "Misc", state["selectedGroup"]!.GetValue<string>() );
        Assert.Equal( string.Empty, state["search"]!.GetValue<string>() ); // the hidden group brought the filter down
        window.Close();
    }

    [AvaloniaFact]
    public async Task Verdata_AbsentShowsMessage()
    {
        var (window, vm, ui) = await OpenAsync();

        if ( vm.Data!.Files.Exists( "verdata.mul" ) )
        {
            Assert.Skip( "This client has a verdata.mul." );
        }

        await ui.SelectTabAsync( "Verdata" );
        var state = await ui.GetTabStateAsync( "Verdata" );

        Assert.True( state["loaded"]!.GetValue<bool>() );
        Assert.Null( state["error"] );
        Assert.False( state["present"]!.GetValue<bool>() );
        Assert.Equal( Resources.VerdataStrings.NotPresent, state["message"]!.GetValue<string>() );
        Assert.True( window.GetVisualDescendants().OfType<Border>().Single( b => b.Name == "NoticeBox" ).IsEffectivelyVisible );
        window.Close();
    }

    [AvaloniaFact]
    public async Task Verdata_SyntheticPatchesGroupedByType()
    {
        using var dir = new TempDir();
        File.WriteAllBytes( dir.File( "verdata.mul" ), TextAudioFiles.VerdataFile(
            [( 4, 0x20, 0, [0x41, 0x42] ), ( 3, 0x10, 0, [1] ), ( 12, 0x30, 0, [2] ), ( 77, 1, 0, [3] )] ) );
        var (window, _, ui) = await OpenAsync( dir.Path );
        await ui.SelectTabAsync( "Verdata" );

        var state = await ui.GetTabStateAsync( "Verdata" );
        Assert.True( state["present"]!.GetValue<bool>() );
        Assert.Null( state["message"] );
        Assert.Equal( 3, state["types"]!.AsArray().Count ); // Art (3+4), Gumps, File 77
        Assert.Equal( 0x10, state["selectedId"]!.GetValue<int>() ); // patches sorted by index

        Assert.True( await ui.SelectEntryAsync( "Verdata", 0x20 ) );
        state = await ui.GetTabStateAsync( "Verdata" );
        Assert.Equal( 4, state["selectedFile"]!.GetValue<int>() );

        state = await ui.InvokeAsync( "Verdata", "select_type", new JsonObject { ["file"] = 77 } );
        Assert.Equal( 77, state["selectedFile"]!.GetValue<int>() );
        Assert.False( await ui.SelectEntryAsync( "Verdata", 0x999 ) );
        window.Close();
    }
}
