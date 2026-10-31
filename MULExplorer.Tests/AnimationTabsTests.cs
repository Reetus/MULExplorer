using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ModelContextProtocol.Protocol;
using MULExplorer.Mcp;
using MULExplorer.ViewModels.Tabs;
using UODataAccess.Loaders;

namespace MULExplorer.Tests;

/// <summary>Loader, MCP and headless UI tests for the Animations, AnimData and Dress tabs.</summary>
public sealed class AnimationTabsTests
{
    private const int HumanMale = 0x190;
    private const int GargoyleMale = 666;
    private const int Shirt = 0x1517;
    private const int Pants = 0x152E;

    // ---- Animations loader ------------------------------------------------------------------

    [Fact]
    public void Animations_HumanMaleIsMULWithPeopleActions()
    {
        var animations = UOTestData.Require().Animations;
        var info = animations.GetBody( HumanMale );

        Assert.Equal( AnimationSource.MUL, info.Source );
        Assert.Equal( "anim.mul", info.FileName );
        Assert.Equal( MobType.Human, info.Type );
        Assert.Equal( 35, info.ActionSlots );
        Assert.Contains( 0, info.Actions );

        var frames = animations.GetFrames( HumanMale, 0, 1 );
        Assert.InRange( frames.Length, 5, 20 );
        Assert.All( frames, f => Assert.False( f.IsEmpty ) );
        Assert.All( frames, f => Assert.InRange( f.Height, 40, 120 ) );
        Assert.Same( frames, animations.GetFrames( HumanMale, 0, 1 ) );
    }

    [Fact]
    public void Animations_MirroredDirectionsFlipStoredOnes()
    {
        var animations = UOTestData.Require().Animations;

        foreach ( var (mirrored, stored) in new[] { ( 5, 3 ), ( 6, 2 ), ( 7, 1 ) } )
        {
            var a = animations.GetFrames( HumanMale, 0, stored );
            var b = animations.GetFrames( HumanMale, 0, mirrored );
            Assert.Equal( a.Length, b.Length );

            var left = a[0].Image!;
            var right = b[0].Image!;
            Assert.Equal( left.Width, right.Width );
            Assert.Equal( left.Width - a[0].CenterX, b[0].CenterX );

            for ( var y = 0; y < left.Height; y += 7 )
            {
                for ( var x = 0; x < left.Width; x++ )
                {
                    Assert.Equal( left[x, y], right[left.Width - 1 - x, y] );
                }
            }
        }
    }

    [Fact]
    public void Animations_UOPBodyDecodes()
    {
        var animations = UOTestData.Require().Animations;
        var info = animations.GetBody( GargoyleMale );

        Assert.Equal( AnimationSource.UOP, info.Source );
        Assert.StartsWith( "AnimationFrame", info.FileName );
        Assert.True( info.Actions.Count > 35 );

        for ( var direction = 0; direction < 8; direction++ )
        {
            var frames = animations.GetFrames( GargoyleMale, 0, direction );
            Assert.NotEmpty( frames );
            Assert.Contains( frames, f => f is { IsEmpty: false, Height: > 40 } );
        }
    }

    [Fact]
    public void Animations_BodyConvRemapsToOtherFiles()
    {
        var animations = UOTestData.Require().Animations;
        var converted = animations.CandidateBodies().Select( animations.GetBody ).Where( b => b.Source == AnimationSource.MUL && b.FileName != "anim.mul" ).ToList();

        Assert.NotEmpty( converted );
        Assert.Contains( converted, b => b.FileBody != b.Body );
        Assert.NotEmpty( animations.GetFrames( converted[0].Body, converted[0].Actions[0], 0 ) );
    }

    [Fact]
    public void Animations_DecodeMULHandlesSyntheticFrame()
    {
        // Palette with entry 1 = pure red; one 2x1 frame with center (0, 0): run at x=0, y=-1, length 2.
        var data = new byte[512 + 4 + 4 + 8 + 4 + 2 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian( data.AsSpan( 2 ), 0x7C00 );
        BinaryPrimitives.WriteInt32LittleEndian( data.AsSpan( 512 ), 1 );
        BinaryPrimitives.WriteInt32LittleEndian( data.AsSpan( 516 ), 8 );
        var frame = data.AsSpan( 520 );
        BinaryPrimitives.WriteInt16LittleEndian( frame, 0 );
        BinaryPrimitives.WriteInt16LittleEndian( frame[2..], 0 );
        BinaryPrimitives.WriteUInt16LittleEndian( frame[4..], 2 );
        BinaryPrimitives.WriteUInt16LittleEndian( frame[6..], 1 );
        BinaryPrimitives.WriteUInt32LittleEndian( frame[8..], ( 0u << 22 ) | ( ( uint )( -1 & 0x3FF ) << 12 ) | 2 );
        frame[12] = 1;
        frame[13] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian( frame[14..], 0x7FFF7FFF );

        var frames = Animations.DecodeMUL( data, false );

        Assert.Single( frames );
        Assert.Equal( 0xFFFF0000u, frames[0].Image![0, 0] );
        Assert.Equal( 0xFF000000u, frames[0].Image![1, 0] );

        var flipped = Animations.DecodeMUL( data, true );
        Assert.Equal( 0xFFFF0000u, flipped[0].Image![1, 0] );
        Assert.Equal( 2, flipped[0].CenterX );
    }

    [Fact]
    public void AnimationNames_LoadFromEmbeddedJson()
    {
        Assert.Equal( "h male", AnimationNames.Get( HumanMale )?.Name );
        Assert.True( AnimationNames.Entries.Count() > 1000 );
        Assert.Contains( AnimationNames.Entries, e => e.Group == MobType.Equipment );
    }

    // ---- AnimData + Dress loaders -----------------------------------------------------------

    [Fact]
    public void AnimData_ReadsEntries()
    {
        var data = UOTestData.Require();
        var animData = data.AnimData;

        Assert.True( animData.Count > 100 );
        Assert.Equal( animData.Count, animData.Ids.Count );

        var entry = animData.Get( animData.Ids[0] )!;
        Assert.InRange( entry.FrameCount, 1, 64 );
        Assert.Null( animData.Get( -1 ) );

        var withArt = animData.Ids.Take( 200 ).Count( id => data.Art.IsValidItem( animData.Get( id )!.FrameItem( 0 ) ) );
        Assert.True( withArt > 150 );
    }

    [Fact]
    public void Dress_PaperdollAndFigureIncludeItems()
    {
        var dress = new DressRenderer( UOTestData.Require() );

        Assert.True( dress.IsWearable( Shirt ) );
        Assert.NotNull( dress.PaperdollGump( DressBody.HumanMale, Shirt, out _ ) );

        var naked = dress.RenderPaperdoll( DressBody.HumanMale, 0, [] )!;
        var dressed = dress.RenderPaperdoll( DressBody.HumanMale, 0, [new DressItem( Shirt ), new DressItem( Pants, 0x21 )] )!;
        Assert.NotEqual( naked.Pixels, dressed.Pixels );

        var figure = dress.RenderFigure( DressBody.HumanMale, 0, [new DressItem( Shirt )], 4, 1 );
        Assert.NotEmpty( figure );
        Assert.All( figure, f => Assert.Equal( figure[0].Width, f.Width ) );

        var female = dress.RenderPaperdoll( DressBody.GargoyleFemale, 0, [] );
        Assert.NotNull( female );
    }

    // ---- MCP ------------------------------------------------------------------------------------

    private static ToolRegistry Registry() => MULExplorerMcpServer.CreateRegistry( new McpContext { Data = UOTestData.Require() } );

    private static IReadOnlyDictionary<string, JsonElement> Args( Dictionary<string, object> values ) =>
        JsonSerializer.Deserialize( JsonSerializer.Serialize( values, McpTestJson.Default.DictionaryStringObject ), McpTestJson.Default.DictionaryStringJsonElement )!;

    private static JsonNode TextJson( CallToolResult result )
    {
        Assert.True( result.IsError is not true, ( result.Content.FirstOrDefault() as TextContentBlock )?.Text );
        return JsonNode.Parse( result.Content.OfType<TextContentBlock>().First().Text )!;
    }

    private static void AssertPng( CallToolResult result )
    {
        Assert.True( result.IsError is not true, ( result.Content.FirstOrDefault() as TextContentBlock )?.Text );
        var image = Assert.IsType<ImageContentBlock>( result.Content[0] );
        Assert.Equal( "image/png", image.MimeType );
        Assert.Equal( 0x89, image.DecodedData.Span[0] );
    }

    [Fact]
    public async Task Mcp_AnimationTools()
    {
        var registry = Registry();

        var list = TextJson( await McpTestExtensions.Call( registry, "list_bodies", Args( new() { ["group"] = "human", ["limit"] = 500 } ) ) );
        Assert.Contains( list["bodies"]!.AsArray(), b => b!["body"]!.GetValue<string>() == "0x0190" );

        var info = TextJson( await McpTestExtensions.Call( registry, "get_body_info", Args( new() { ["body"] = "0x190" } ) ) );
        Assert.Equal( "MUL", info["source"]!.GetValue<string>() );
        Assert.Equal( 35, info["actions"]!.AsArray().Count );

        var uop = TextJson( await McpTestExtensions.Call( registry, "get_body_info", Args( new() { ["body"] = GargoyleMale } ) ) );
        Assert.Equal( "UOP", uop["source"]!.GetValue<string>() );

        AssertPng( await McpTestExtensions.Call( registry, "get_animation_frame", Args( new() { ["body"] = "0x190", ["action"] = 0, ["direction"] = 6, ["hue"] = 0x21 } ) ) );
        AssertPng( await McpTestExtensions.Call( registry, "get_animation_strip", Args( new() { ["body"] = GargoyleMale, ["direction"] = 2 } ) ) );

        var bad = await McpTestExtensions.Call( registry, "get_animation_strip", Args( new() { ["body"] = "0x190", ["action"] = 79 } ) );
        Assert.True( bad.IsError is true );
    }

    [Fact]
    public async Task Mcp_AnimDataAndDressTools()
    {
        var registry = Registry();
        var data = UOTestData.Require();
        var id = data.AnimData.Ids[0];

        var list = TextJson( await McpTestExtensions.Call( registry, "list_animdata", Args( new() { ["limit"] = 5 } ) ) );
        Assert.True( list["total"]!.GetValue<int>() > 100 );

        var entry = TextJson( await McpTestExtensions.Call( registry, "get_animdata", Args( new() { ["id"] = id } ) ) );
        Assert.Equal( entry["frameCount"]!.GetValue<int>(), entry["frames"]!.AsArray().Count );

        AssertPng( await McpTestExtensions.Call( registry, "get_animdata_strip", Args( new() { ["id"] = id } ) ) );

        var wearables = TextJson( await McpTestExtensions.Call( registry, "list_wearables", Args( new() { ["name"] = "shirt" } ) ) );
        Assert.True( wearables["total"]!.GetValue<int>() > 0 );

        AssertPng( await McpTestExtensions.Call( registry, "render_paperdoll", Args( new() { ["body"] = "HumanFemale", ["items"] = "0x1517,0x152E:0x21" } ) ) );
        AssertPng( await McpTestExtensions.Call( registry, "render_dressed_figure", Args( new() { ["items"] = "0x1517", ["direction"] = 5 } ) ) );

        var badBody = await McpTestExtensions.Call( registry, "render_paperdoll", Args( new() { ["body"] = "Orc" } ) );
        Assert.True( badBody.IsError is true );
    }

    // ---- Headless UI ----------------------------------------------------------------------------

    private static async Task<(Views.MainWindow Window, ViewModels.MainWindowViewModel VM, IUIAutomation UI)> OpenAsync()
    {
        var folder = UOTestData.RequireFolder();
        var (window, vm) = App.CreateMainWindow( UOTestData.IsolatedSettings( folder ) );
        window.Show();
        await vm.OpenFolderAsync( folder );
        Dispatcher.UIThread.RunJobs();
        return ( window, vm, vm.McpContext.UI! );
    }

    [AvaloniaFact]
    public async Task UI_AnimationsTab()
    {
        var (window, vm, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Animations" );

        var state = await ui.GetTabStateAsync( "Animations" );
        Assert.True( state["loaded"]!.GetValue<bool>() );
        Assert.True( state["total"]!.GetValue<int>() > 500 );
        Assert.Equal( HumanMale, state["selectedBody"]!.GetValue<int>() );

        Assert.True( await ui.SelectEntryAsync( "Animations", GargoyleMale ) );
        state = await ui.InvokeAsync( "Animations", "set_direction", new JsonObject { ["direction"] = 6 } );
        Assert.Equal( 6, state["direction"]!.GetValue<int>() );
        Assert.True( state["frameCount"]!.GetValue<int>() > 0 );
        Assert.StartsWith( "AnimationFrame", state["source"]!.GetValue<string>() );

        state = await ui.InvokeAsync( "Animations", "set_action", new JsonObject { ["action"] = 4 } );
        Assert.Equal( 4, state["action"]!.GetValue<int>() );
        state = await ui.InvokeAsync( "Animations", "set_frame", new JsonObject { ["frame"] = 1 } );
        Assert.Equal( 1, state["frame"]!.GetValue<int>() );
        state = await ui.InvokeAsync( "Animations", "set_hue", new JsonObject { ["hue"] = 0x21 } );
        Assert.NotNull( state["previewSize"] );

        state = await ui.InvokeAsync( "Animations", "play", new JsonObject() );
        Assert.True( state["playing"]!.GetValue<bool>() );
        state = await ui.InvokeAsync( "Animations", "pause", new JsonObject() );
        Assert.False( state["playing"]!.GetValue<bool>() );

        var visible = await ui.SearchAsync( "Animations", "0x190" );
        Assert.InRange( visible, 1, 5 );
        Assert.False( await ui.SelectEntryAsync( "Animations", 0xFFFF ) );

        var animations = ( AnimationsViewModel )vm.FindTab( "Animations" )!;
        Assert.Contains( animations.Groups.SelectMany( g => g.Children ), n => n.Body == HumanMale );
        window.Close();
    }

    [AvaloniaFact]
    public async Task UI_AnimDataTab()
    {
        var (window, vm, ui) = await OpenAsync();
        var id = vm.Data!.AnimData.Ids[1];
        await ui.SelectTabAsync( "AnimData" );

        Assert.True( await ui.SelectEntryAsync( "AnimData", id ) );
        var state = await ui.InvokeAsync( "AnimData", "pause", new JsonObject() );
        Assert.Equal( id, state["selectedId"]!.GetValue<int>() );
        Assert.Equal( vm.Data.AnimData.Get( id )!.FrameCount, state["frameCount"]!.GetValue<int>() );
        Assert.False( state["playing"]!.GetValue<bool>() );

        state = await ui.InvokeAsync( "AnimData", "set_frame", new JsonObject { ["frame"] = state["frameCount"]!.GetValue<int>() - 1 } );
        Assert.Equal( state["frameCount"]!.GetValue<int>() - 1, state["frame"]!.GetValue<int>() );
        Assert.NotNull( state["previewSize"] );

        Assert.InRange( await ui.SearchAsync( "AnimData", $"0x{id:X4}" ), 1, 50 );
        Assert.False( await ui.SelectEntryAsync( "AnimData", 0 ) && vm.Data.AnimData.Get( 0 ) is null );
        window.Close();
    }

    [AvaloniaFact]
    public async Task UI_DressTab()
    {
        var (window, _, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Dress" );

        var state = await ui.GetTabStateAsync( "Dress" );
        Assert.True( state["total"]!.GetValue<int>() > 100 );
        var naked = state["paperdollSize"]!.GetValue<string>();
        Assert.NotNull( state["figureSize"] );

        state = await ui.InvokeAsync( "Dress", "equip", new JsonObject { ["id"] = "0x1517", ["hue"] = 0x21 } );
        state = await ui.InvokeAsync( "Dress", "equip", new JsonObject { ["id"] = Pants } );
        Assert.Equal( 2, state["worn"]!.AsArray().Count );
        Assert.Equal( naked, state["paperdollSize"]!.GetValue<string>() );

        state = await ui.InvokeAsync( "Dress", "set_body", new JsonObject { ["body"] = "GargoyleFemale" } );
        Assert.Equal( "GargoyleFemale", state["body"]!.GetValue<string>() );
        state = await ui.InvokeAsync( "Dress", "set_body", new JsonObject { ["body"] = "HumanFemale" } );
        state = await ui.InvokeAsync( "Dress", "set_direction", new JsonObject { ["direction"] = 3 } );
        state = await ui.InvokeAsync( "Dress", "set_action", new JsonObject { ["action"] = 0 } );
        Assert.True( state["frameCount"]!.GetValue<int>() > 1 );

        state = await ui.InvokeAsync( "Dress", "unequip", new JsonObject { ["id"] = Pants } );
        Assert.Single( state["worn"]!.AsArray() );
        state = await ui.InvokeAsync( "Dress", "undress_all", new JsonObject() );
        Assert.Empty( state["worn"]!.AsArray() );

        Assert.True( await ui.SelectEntryAsync( "Dress", Shirt ) );
        Assert.False( await ui.SelectEntryAsync( "Dress", 0x0EED ) );
        await Assert.ThrowsAsync<ArgumentException>( () => ui.InvokeAsync( "Dress", "equip", new JsonObject { ["id"] = 0x0EED } ) );
        window.Close();
    }
}
