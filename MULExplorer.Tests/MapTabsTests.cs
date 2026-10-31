using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModelContextProtocol.Protocol;
using MULExplorer.Controls;
using MULExplorer.Mcp;
using MULExplorer.ViewModels.Tabs;
using MULExplorer.Views;
using UODataAccess.IO;
using UODataAccess.Loaders;

namespace MULExplorer.Tests;

/// <summary>Map and MultiMap/Facets: loaders on synthetic and real files, MCP tools, headless UI.</summary>
public sealed class MapTabsTests
{
    // ----- synthetic formats -----

    /// <summary>Builds map blocks: 4-byte header + 64 x (u16 id, s8 z), cells filled by a function of (cellX, cellY).</summary>
    private static byte[] Block( Func<int, int, (ushort Id, sbyte Z)> cell )
    {
        var block = new byte[FacetMap.BlockBytes];

        for ( var i = 0; i < 64; i++ )
        {
            var (id, z) = cell( i & 7, i >> 3 );
            BinaryPrimitives.WriteUInt16LittleEndian( block.AsSpan( 4 + i * 3 ), id );
            block[4 + i * 3 + 2] = ( byte )z;
        }

        return block;
    }

    private static byte[] Statics( params (ushort Id, byte X, byte Y, sbyte Z, ushort Hue)[] statics )
    {
        var data = new byte[statics.Length * 7];

        for ( var i = 0; i < statics.Length; i++ )
        {
            var s = statics[i];
            BinaryPrimitives.WriteUInt16LittleEndian( data.AsSpan( i * 7 ), s.Id );
            data[i * 7 + 2] = s.X;
            data[i * 7 + 3] = s.Y;
            data[i * 7 + 4] = ( byte )s.Z;
            BinaryPrimitives.WriteUInt16LittleEndian( data.AsSpan( i * 7 + 5 ), s.Hue );
        }

        return data;
    }

    private static byte[] Index( params (int Offset, int Length)[] records )
    {
        var data = new byte[records.Length * 12];

        for ( var i = 0; i < records.Length; i++ )
        {
            BinaryPrimitives.WriteInt32LittleEndian( data.AsSpan( i * 12 ), records[i].Offset );
            BinaryPrimitives.WriteInt32LittleEndian( data.AsSpan( i * 12 + 4 ), records[i].Length );
        }

        return data;
    }

    private static byte[] Ints( params int[] values )
    {
        var data = new byte[values.Length * 4];

        for ( var i = 0; i < values.Length; i++ )
        {
            BinaryPrimitives.WriteInt32LittleEndian( data.AsSpan( i * 4 ), values[i] );
        }

        return data;
    }

    /// <summary>Radar table: land id N → color N, item id N → color 0x1000 + N; item heights 5.</summary>
    private static MapRenderTables TestTables()
    {
        var radar = new ushort[0x4000 + 0x100];

        for ( var i = 0; i < radar.Length; i++ )
        {
            radar[i] = ( ushort )( i < 0x4000 ? i : 0x1000 + i - 0x4000 );
        }

        var heights = new byte[0x100];
        heights.AsSpan().Fill( 5 );
        return MapRenderTables.Create( radar, heights );
    }

    [Fact]
    public void MULMap_ReadsColumnMajorBlocksStaticsAndRendersRadar()
    {
        using var dir = new TempDir();

        // Block 0 is (bx 0, by 0); block 1 is (bx 0, by 1) because blocks are column-major.
        var block0 = Block( ( x, y ) => ( ( ushort )( 0x10 + y * 8 + x ), ( sbyte )( x == 7 ? 20 : 0 ) ) );
        var block1 = Block( ( _, _ ) => ( 0x99, -5 ) );
        File.WriteAllBytes( dir.File( "map0.mul" ), [..block0, ..block1] );

        var statics = Statics( ( 0x20, 3, 2, 0, 0 ), ( 0x21, 3, 2, 2, 0 ), ( 0x22, 7, 0, 0, 0 ) );
        File.WriteAllBytes( dir.File( "statics0.mul" ), statics );
        File.WriteAllBytes( dir.File( "staidx0.mul" ), Index( ( 0, statics.Length ), ( -1, 0 ) ) );

        using var maps = new Maps( new UOFileSet( dir.Path ) );
        maps.Tables = TestTables();

        Assert.True( maps.IsAvailable( 0 ) );
        Assert.True( maps.IsAvailable( 1 ) ); // map1 falls back to map0
        Assert.False( maps.IsAvailable( 2 ) );
        Assert.Null( maps.GetFacet( 2 ) );

        var facet = maps.GetFacet( 0 )!;
        Assert.Equal( 6144, facet.Width ); // tiny file: pre-ML size
        Assert.Equal( 4096, facet.Height );
        Assert.False( facet.IsUOP );

        Assert.Equal( 0x10 + 2 * 8 + 3, facet.GetLand( 3, 2 ).Id );
        Assert.Equal( 0x99, facet.GetLand( 3, 10 ).Id );
        Assert.Equal( -5, facet.GetLand( 3, 10 ).Z );
        Assert.Equal( 0, facet.GetLand( 9, 0 ).Id ); // beyond the file
        Assert.Equal( default, facet.GetLand( -1, 0 ) );

        var cell = facet.GetStatics( 3, 2 );
        Assert.Equal( [0x20, 0x21], cell.Select( s => ( int )s.Id ) );
        Assert.Equal( 2, cell[1].Z );
        Assert.Empty( facet.GetStatics( 3, 10 ) );
        Assert.Equal( 3, facet.GetStaticBlock( 0, 0 ).Length );

        // Highest static (z 2 + height 5) wins over land z 0; land z 20 at x 7 beats a static there.
        Assert.Equal( 0x1000 + 0x21, facet.GetRadarColor( 3, 2 ) );
        Assert.Equal( 0x10 + 7, facet.GetRadarColor( 7, 0 ) );
        Assert.Equal( 0x10 + 2 * 8 + 3, facet.GetRadarColor( 3, 2, statics: false ) );

        var pixmap = facet.RenderRadar( 0, 0, 16, 16 );
        Assert.Equal( 16, pixmap.Width );
        Assert.Equal( UODataAccess.Imaging.Color16.ToArgb( 0x1000 + 0x21 ), pixmap[3, 2] );
        Assert.Equal( UODataAccess.Imaging.Color16.ToArgb( 0x99 ), pixmap[3, 10] );

        var outside = facet.RenderRadar( 6140, 0, 8, 1 );
        Assert.Equal( 0xFF00_0000u, outside[7, 0] );
    }

    [Fact]
    public void UOPMap_ReadsAcrossChunks()
    {
        using var dir = new TempDir();
        var chunk0 = new byte[FacetMap.UOPChunkBlocks * FacetMap.BlockBytes];
        Block( ( _, _ ) => ( 0x11, 1 ) ).CopyTo( chunk0, 0 );
        var chunk1 = Block( ( _, _ ) => ( 0x22, 2 ) );

        UOPWriter.Write( dir.File( "map0LegacyMUL.uop" ),
        [
            ( "build/map0legacymul/00000000.dat", chunk0, 0, chunk0.Length, UOPCompression.None ),
            ( "build/map0legacymul/00000001.dat", chunk1, 0, chunk1.Length, UOPCompression.None )
        ] );

        using var maps = new Maps( new UOFileSet( dir.Path ) );
        var facet = maps.GetFacet( 0 )!;

        Assert.True( facet.IsUOP );
        Assert.False( facet.HasStatics );
        Assert.Equal( 0x11, facet.GetLand( 0, 0 ).Id );

        // Block 4096 = bx 4096 / 512 = 8, by 0: the first block of chunk 1.
        var land = facet.GetLand( 8 * 8 + 3, 5 );
        Assert.Equal( 0x22, land.Id );
        Assert.Equal( 2, land.Z );
        Assert.Empty( facet.GetStatics( 0, 0 ) );
    }

    [Fact]
    public void DiffPatches_ApplyOnlyWhenRequested()
    {
        using var dir = new TempDir();
        File.WriteAllBytes( dir.File( "map0.mul" ), Block( ( _, _ ) => ( 0x10, 0 ) ) );
        var statics = Statics( ( 0x20, 1, 1, 0, 0 ) );
        File.WriteAllBytes( dir.File( "statics0.mul" ), statics );
        File.WriteAllBytes( dir.File( "staidx0.mul" ), Index( ( 0, statics.Length ) ) );

        File.WriteAllBytes( dir.File( "mapdifl0.mul" ), Ints( 0 ) );
        File.WriteAllBytes( dir.File( "mapdif0.mul" ), Block( ( _, _ ) => ( 0x30, 7 ) ) );
        var patchStatics = Statics( ( 0x40, 1, 1, 3, 0x21 ), ( 0x41, 2, 2, 0, 0 ) );
        File.WriteAllBytes( dir.File( "stadifl0.mul" ), Ints( 0 ) );
        File.WriteAllBytes( dir.File( "stadifi0.mul" ), Index( ( 0, patchStatics.Length ) ) );
        File.WriteAllBytes( dir.File( "stadif0.mul" ), patchStatics );

        using var maps = new Maps( new UOFileSet( dir.Path ) );
        var facet = maps.GetFacet( 0 )!;

        Assert.True( facet.HasPatches );
        Assert.Equal( 1, facet.LandPatchCount );
        Assert.Equal( 1, facet.StaticPatchCount );
        Assert.True( facet.IsLandBlockPatched( 0, 0 ) );

        Assert.Equal( 0x10, facet.GetLand( 1, 1 ).Id );
        Assert.Equal( 0x30, facet.GetLand( 1, 1, patched: true ).Id );
        Assert.Equal( 0x20, Assert.Single( facet.GetStatics( 1, 1 ) ).Id );

        var patched = Assert.Single( facet.GetStatics( 1, 1, patched: true ) );
        Assert.Equal( 0x40, patched.Id );
        Assert.Equal( 0x21, patched.Hue );
        Assert.Single( facet.GetStatics( 2, 2, patched: true ) );
    }

    [Fact]
    public void MultiMapRle_DecodesRunsInkOnPaper()
    {
        // 4x2: 3 ink, 4 paper, 1 ink.
        byte[] data = [..Ints( 4, 2 ), 0x83, 0x04, 0x81];
        var image = MultiMap.DecodeRle( data );

        Assert.Equal( 4, image.Width );
        Assert.Equal( 2, image.Height );
        Assert.Equal( [MultiMap.Ink, MultiMap.Ink, MultiMap.Ink, MultiMap.Paper, MultiMap.Paper, MultiMap.Paper, MultiMap.Paper, MultiMap.Ink], image.Colors.ToArray() );

        var pixmap = image.ToPixmap();
        Assert.Equal( 0xFF00_0000u, pixmap[0, 0] );
        Assert.Equal( 0xFFFF_FFFFu, pixmap[3, 0] );
    }

    [Fact]
    public void FacetImage_DecodesPerRowRuns()
    {
        // 3x2: row 0 = 2 x 0x7C00 + 1 x 0x001F, row 1 = 3 x 0x03E0.
        var data = new List<byte>();
        data.AddRange( [3, 0, 2, 0] );
        data.AddRange( Ints( 6 ) );
        data.AddRange( [2, 0x00, 0x7C, 1, 0x1F, 0x00] );
        data.AddRange( Ints( 3 ) );
        data.AddRange( [3, 0xE0, 0x03] );

        var image = MultiMap.DecodeFacet( data.ToArray() );

        Assert.Equal( 3, image.Width );
        Assert.Equal( 2, image.Height );
        Assert.Equal( [0x7C00, 0x7C00, 0x001F, 0x03E0, 0x03E0, 0x03E0], image.Colors.ToArray().Select( c => ( int )c ) );
        Assert.Equal( 0xFFFF_0000u, image.ToPixmap()[0, 0] );
        Assert.Equal( 2, image.ToPixmap( 2 ).Width );
    }

    [Fact]
    public void MultiMap_MissingFilesThrow()
    {
        using var dir = new TempDir();
        Assert.Throws<FileNotFoundException>( () => new MultiMap( new UOFileSet( dir.Path ) ) );
        Assert.Throws<FileNotFoundException>( () => new Maps( new UOFileSet( dir.Path ) ) );
    }

    // ----- real client -----

    [Fact]
    public void Felucca_BritainHasWalkableLandAndStatics()
    {
        var data = UOTestData.Require();
        var facet = data.Maps.GetFacet( 0 )!;

        Assert.Equal( 7168, facet.Width );
        Assert.Equal( 4096, facet.Height );

        var land = facet.GetLand( 1434, 1699 );
        var info = data.TileData.GetLand( land.Id );
        Assert.NotEqual( 0, land.Id );
        Assert.False( info.Flags.HasFlag( TileFlag.Impassable ), info.Name );
        Assert.False( info.Flags.HasFlag( TileFlag.Wet ), info.Name );
        Assert.InRange( land.Z, -5, 30 );

        // Britain is dense with buildings: plenty of statics around the bank.
        var count = 0;

        for ( var bx = 1420 / 8; bx <= 1450 / 8; bx++ )
        {
            for ( var by = 1680 / 8; by <= 1710 / 8; by++ )
            {
                count += facet.GetStaticBlock( bx, by ).Length;
            }
        }

        Assert.True( count > 100, $"{count} statics" );

        // The open sea between Britain and Moonglow is water.
        Assert.True( data.TileData.GetLand( facet.GetLand( 3000, 2000 ).Id ).Flags.HasFlag( TileFlag.Wet ) );
    }

    [Fact]
    public void AllFacets_HaveUOFiddlerSizes()
    {
        var maps = UOTestData.Require().Maps;
        (int, int)[] sizes = [( 7168, 4096 ), ( 7168, 4096 ), ( 2304, 1600 ), ( 2560, 2048 ), ( 1448, 1448 ), ( 1280, 4096 )];

        for ( var i = 0; i < Maps.FacetCount; i++ )
        {
            if ( maps.GetFacet( i ) is { } facet )
            {
                Assert.Equal( sizes[i], ( facet.Width, facet.Height ) );
                Assert.Equal( Maps.GetName( i ), facet.Name );
            }
        }
    }

    [Fact]
    public void RadarRender_512IsFast()
    {
        var facet = UOTestData.Require().Maps.GetFacet( 1 )!;
        facet.GetRadarColor( 0, 0 ); // load radar tables outside the timing
        facet.ClearRenderCache();

        var sw = Stopwatch.StartNew();
        var pixmap = facet.RenderRadar( 1200, 1500, 512, 512 );
        sw.Stop();

        Assert.Equal( 512, pixmap.Width );
        Assert.True( sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms" );
        Assert.True( pixmap.Pixels.Distinct().Count() > 50 );
    }

    [Fact]
    public void RealMultiMapAndFacetImages_Decode()
    {
        var multiMap = UOTestData.Require().MultiMap;

        if ( multiMap.HasMultiMap )
        {
            var rle = multiMap.GetMultiMap()!;
            Assert.Equal( multiMap.GetMultiMapSize(), ( rle.Width, rle.Height ) );
            Assert.Contains( MultiMap.Ink, rle.Colors.ToArray() );
            Assert.Contains( MultiMap.Paper, rle.Colors.ToArray() );
        }

        foreach ( var id in multiMap.FacetIds )
        {
            var image = multiMap.GetFacetImage( id )!;
            Assert.Equal( multiMap.GetFacetSize( id ), ( image.Width, image.Height ) );
        }
    }

    // ----- MCP -----

    private static ToolRegistry Registry() => MULExplorerMcpServer.CreateRegistry( new McpContext { Data = UOTestData.Require() } );

    private static IReadOnlyDictionary<string, JsonElement> Args( params (string Key, object Value)[] values ) =>
        values.ToDictionary( v => v.Key, v => v.Value switch
        {
            int i => JsonSerializer.SerializeToElement( i, McpTestJson.Default.Int32 ),
            bool b => JsonDocument.Parse( b ? "true" : "false" ).RootElement.Clone(),
            double d => JsonDocument.Parse( d.ToString( System.Globalization.CultureInfo.InvariantCulture ) ).RootElement.Clone(),
            _ => JsonSerializer.SerializeToElement( v.Value.ToString()!, McpTestJson.Default.String )
        } );

    private static JsonNode TextJson( CallToolResult result )
    {
        Assert.True( result.IsError is not true, ( result.Content.FirstOrDefault() as TextContentBlock )?.Text );
        return JsonNode.Parse( result.Content.OfType<TextContentBlock>().First().Text )!;
    }

    [Fact]
    public async Task Mcp_ListFacetsAndTile()
    {
        var registry = Registry();

        var facets = TextJson( await McpTestExtensions.Call( registry, "list_facets", null ) ).AsArray();
        Assert.Contains( facets, f => f!["name"]!.GetValue<string>() == "Felucca" && f["width"]!.GetValue<int>() == 7168 );

        var tile = TextJson( await McpTestExtensions.Call( registry, "get_map_tile", Args( ( "facet", 0 ), ( "x", 1434 ), ( "y", 1699 ) ) ) );
        Assert.Equal( 1434, tile["x"]!.GetValue<int>() );
        Assert.NotNull( tile["land"]!["name"] );

        var block = TextJson( await McpTestExtensions.Call( registry, "get_map_block", Args( ( "x", 1434 ), ( "y", 1699 ) ) ) );
        Assert.Equal( 64, block["land"]!.AsArray().Count );
        Assert.Equal( 1434 / 8, block["block_x"]!.GetValue<int>() );

        var outside = await McpTestExtensions.Call( registry, "get_map_tile", Args( ( "facet", 2 ), ( "x", 5000 ), ( "y", 10 ) ) );
        Assert.True( outside.IsError );
    }

    [Fact]
    public async Task Mcp_RenderMapRegion()
    {
        var registry = Registry();

        var region = await McpTestExtensions.Call( registry, "render_map_region", Args( ( "x", 1300 ), ( "y", 1550 ), ( "width", 256 ), ( "height", 200 ), ( "scale", 2 ) ) );
        var image = Assert.IsType<ImageContentBlock>( region.Content[0] );
        Assert.Equal( 0x89, image.DecodedData.Span[0] );
        var info = JsonNode.Parse( ( ( TextContentBlock )region.Content[1] ).Text )!;
        Assert.Equal( 512, info["image_width"]!.GetValue<int>() );
        Assert.Equal( 400, info["image_height"]!.GetValue<int>() );

        var whole = await McpTestExtensions.Call( registry, "render_map_region", Args( ( "facet", 2 ), ( "max_size", 512 ) ) );
        var wholeInfo = JsonNode.Parse( ( ( TextContentBlock )whole.Content[1] ).Text )!;
        Assert.InRange( wholeInfo["image_width"]!.GetValue<int>(), 256, 512 );
        Assert.Equal( 2304, wholeInfo["width"]!.GetValue<int>() );

        var tooBig = await McpTestExtensions.Call( registry, "render_map_region", Args( ( "scale", 1 ) ) );
        Assert.True( tooBig.IsError );
    }

    [Fact]
    public async Task Mcp_MultiMapImages()
    {
        var registry = Registry();

        var list = TextJson( await McpTestExtensions.Call( registry, "list_multimap_images", null ) );
        Assert.NotEmpty( list["facets"]!.AsArray() );

        var multimap = await McpTestExtensions.Call( registry, "get_multimap_image", Args( ( "max_size", 640 ) ) );
        Assert.IsType<ImageContentBlock>( multimap.Content[0] );
        Assert.Equal( 640, JsonNode.Parse( ( ( TextContentBlock )multimap.Content[1] ).Text )!["image_width"]!.GetValue<int>() );

        var facet = await McpTestExtensions.Call( registry, "get_facet_image", Args( ( "id", 0 ) ) );
        var info = JsonNode.Parse( ( ( TextContentBlock )facet.Content[1] ).Text )!;
        Assert.True( info["image_width"]!.GetValue<int>() <= 1024 );

        Assert.True( ( await McpTestExtensions.Call( registry, "get_facet_image", Args( ( "id", 9 ) ) ) ).IsError );
    }

    // ----- UI -----

    private static async Task<(MainWindow Window, ViewModels.MainWindowViewModel VM, IUIAutomation UI)> OpenAsync()
    {
        var folder = UOTestData.RequireFolder();
        var (window, vm) = App.CreateMainWindow( UOTestData.IsolatedSettings( folder ) );
        window.Show();
        await vm.OpenFolderAsync( folder );
        Dispatcher.UIThread.RunJobs();
        return ( window, vm, vm.McpContext.UI! );
    }

    private static MapCanvas FindCanvas( MainWindow window )
    {
        for ( var i = 0; i < 3; i++ )
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        return window.GetVisualDescendants().OfType<MapCanvas>().Single();
    }

    [AvaloniaFact]
    public async Task MapTab_FacetGoToInspectAndToggles()
    {
        var (window, vm, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Map" );

        var state = await ui.GetTabStateAsync( "Map" );
        Assert.True( state["loaded"]!.GetValue<bool>(), state["error"]?.ToString() );
        Assert.Equal( 0, state["facet"]!.GetValue<int>() );
        Assert.Equal( 7168, state["width"]!.GetValue<int>() );

        var canvas = FindCanvas( window );
        Assert.True( ( await ui.ScreenshotAsync() ).Length > 1000 );
        await canvas.WhenIdleAsync();
        Assert.True( canvas.CachedTileCount > 0 );

        state = await ui.InvokeAsync( "Map", "go_to", new JsonObject { ["x"] = 1434, ["y"] = 1699 } );
        Assert.Equal( 1434.5, state["centerX"]!.GetValue<double>() );
        Assert.Equal( 1434, state["inspected"]!["x"]!.GetValue<int>() );
        Assert.False( string.IsNullOrEmpty( state["inspected"]!["landName"]!.GetValue<string>() ) );

        // A cell with statics: find one in the bank block and inspect it.
        var facet = vm.Data!.Maps.GetFacet( 0 )!;
        var s = facet.GetStaticBlock( 1434 / 8, 1690 / 8 )[0];
        var (sx, sy) = ( 1434 / 8 * 8 + s.X, 1690 / 8 * 8 + s.Y );
        state = await ui.InvokeAsync( "Map", "inspect", new JsonObject { ["x"] = sx, ["y"] = sy } );
        Assert.NotEmpty( state["inspected"]!["statics"]!.AsArray() );
        Assert.Equal( $"0x{s.Id:X4}", state["inspected"]!["statics"]![0]!["id"]!.GetValue<string>() );

        state = await ui.InvokeAsync( "Map", "set_zoom", new JsonObject { ["zoom"] = 4.0 } );
        Assert.Equal( 4, state["zoom"]!.GetValue<double>() );
        state = await ui.InvokeAsync( "Map", "zoom_out", new JsonObject() );
        Assert.Equal( 2, state["zoom"]!.GetValue<double>() );
        state = await ui.InvokeAsync( "Map", "set_zoom", new JsonObject { ["zoom"] = 100.0 } );
        Assert.Equal( MapViewModel.MaxZoom, state["zoom"]!.GetValue<double>() );

        state = await ui.InvokeAsync( "Map", "toggle_statics", new JsonObject { ["value"] = false } );
        Assert.False( state["showStatics"]!.GetValue<bool>() );
        state = await ui.InvokeAsync( "Map", "toggle_statics", new JsonObject() );
        Assert.True( state["showStatics"]!.GetValue<bool>() );

        state = await ui.InvokeAsync( "Map", "toggle_patches", new JsonObject { ["value"] = true } );
        Assert.True( state["usePatches"]!.GetValue<bool>() );
        Assert.Equal( facet.HasPatches, state["hasPatches"]!.GetValue<bool>() );

        Assert.Equal( 1, await ui.SearchAsync( "Map", "1500, 1600" ) );
        Assert.Equal( 0, await ui.SearchAsync( "Map", "not a place" ) );
        Assert.NotNull( ( await ui.GetTabStateAsync( "Map" ) )["message"] );

        Assert.True( await ui.SelectEntryAsync( "Map", 2 ) );
        state = await ui.GetTabStateAsync( "Map" );
        Assert.Equal( 2304, state["width"]!.GetValue<int>() );
        Assert.InRange( state["centerX"]!.GetValue<double>(), 0, 2304 );
        Assert.False( await ui.SelectEntryAsync( "Map", 42 ) );

        await Assert.ThrowsAsync<ArgumentException>( () => ui.InvokeAsync( "Map", "go_to", new JsonObject { ["x"] = 99999, ["y"] = 1 } ) );
        window.Close();
    }

    [AvaloniaFact]
    public async Task MapCanvas_PixelAtAndWheelZoomKeepAnchor()
    {
        var (window, _, ui) = await OpenAsync();
        await ui.SelectTabAsync( "Map" );
        await ui.InvokeAsync( "Map", "go_to", new JsonObject { ["x"] = 1000, ["y"] = 1000 } );
        await ui.InvokeAsync( "Map", "set_zoom", new JsonObject { ["zoom"] = 2.0 } );

        var canvas = FindCanvas( window );
        var size = canvas.ViewSize;
        Assert.True( size.Width > 100 );

        var centre = canvas.PixelAt( new Avalonia.Point( size.Width / 2, size.Height / 2 ) );
        Assert.Equal( new Avalonia.PixelPoint( 1000, 1000 ), centre );

        var anchor = new Avalonia.Point( 40, 30 );
        var before = canvas.PixelAt( anchor );
        canvas.ZoomBy( 1, anchor );
        Assert.Equal( 4, canvas.Zoom );
        Assert.Equal( before, canvas.PixelAt( anchor ) );
        window.Close();
    }

    [AvaloniaFact]
    public async Task MultiMapTab_SwitchImagesAndZoom()
    {
        var (window, _, ui) = await OpenAsync();
        await ui.SelectTabAsync( "MultiMap/Facets" );

        var state = await ui.GetTabStateAsync( "MultiMap" );
        Assert.True( state["loaded"]!.GetValue<bool>(), state["error"]?.ToString() );
        Assert.NotEmpty( state["images"]!.AsArray() );
        Assert.True( state["width"]!.GetValue<int>() > 0 );

        Assert.True( await ui.SelectEntryAsync( "MultiMap", 2 ) );
        state = await ui.GetTabStateAsync( "MultiMap" );
        Assert.Equal( 2304, state["width"]!.GetValue<int>() );
        Assert.Equal( 1600, state["height"]!.GetValue<int>() );

        state = await ui.InvokeAsync( "MultiMap", "set_zoom", new JsonObject { ["zoom"] = 1.0 } );
        Assert.Equal( 1, state["zoom"]!.GetValue<double>() );
        state = await ui.InvokeAsync( "MultiMap", "zoom_fit", new JsonObject() );
        Assert.True( state["zoom"]!.GetValue<double>() < 1 );

        state = await ui.InvokeAsync( "MultiMap", "set_image", new JsonObject { ["id"] = MultiMapViewModel.MultiMapId } );
        Assert.Equal( 2560, state["width"]!.GetValue<int>() );
        Assert.False( await ui.SelectEntryAsync( "MultiMap", 9 ) );
        window.Close();
    }
}
