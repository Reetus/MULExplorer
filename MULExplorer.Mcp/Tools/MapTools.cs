using System.Text.Json.Nodes;
using UODataAccess;
using UODataAccess.Imaging;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Map tab: facets, land/statics at a cell, radar renders.</summary>
public sealed class MapTools : IToolProvider
{
    /// <summary>Largest side of a rendered PNG.</summary>
    public const int MaxImageSize = 2048;

    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_facets",
            "Facets (map0..5) the client has: index, name, size in tiles, source file, whether statics, diff patches (stadif/mapdif) and alternate 'x' files exist.",
            null,
            null,
            _ =>
            {
                var maps = context.RequireData().Maps;
                var list = new JsonArray();

                for ( var i = 0; i < Maps.FacetCount; i++ )
                {
                    if ( maps.GetFacet( i ) is not { } f )
                    {
                        continue;
                    }

                    list.Add( ( JsonNode )new JsonObject
                    {
                        ["index"] = f.Index,
                        ["name"] = f.Name,
                        ["width"] = f.Width,
                        ["height"] = f.Height,
                        ["file"] = Path.GetFileName( f.MapPath ),
                        ["file_index"] = f.FileIndex,
                        ["uop"] = f.IsUOP,
                        ["statics"] = f.HasStatics,
                        ["land_patches"] = f.LandPatchCount,
                        ["static_patches"] = f.StaticPatchCount,
                        ["has_alternate"] = maps.HasAlternate( i )
                    } );
                }

                return Json( list );
            } );

        registry.Add(
            "get_map_tile",
            "Land tile (id, name, altitude) and every static (id, name, z, hue, height) at a map cell.",
            Props( FacetProp, ( "x", Int( "Cell x." ) ), ( "y", Int( "Cell y." ) ), PatchesProp, AlternateProp ),
            ["x", "y"],
            args =>
            {
                var data = context.RequireData();
                var facet = RequireFacet( data, args );
                var x = args.RequireInt( "x" );
                var y = args.RequireInt( "y" );
                var patched = args.GetBool( "patches" );

                if ( !facet.Contains( x, y ) )
                {
                    return Error( $"({x}, {y}) is outside {facet.Name} (0..{facet.Width - 1}, 0..{facet.Height - 1})." );
                }

                var land = facet.GetLand( x, y, patched );

                return Json( new JsonObject
                {
                    ["facet"] = facet.Index,
                    ["x"] = x,
                    ["y"] = y,
                    ["land"] = DescribeLand( data, land ),
                    ["statics"] = new JsonArray( facet.GetStatics( x, y, patched ).Select( s => ( JsonNode? )DescribeStatic( data, s, null ) ).ToArray() ),
                    ["radar_color"] = $"0x{facet.GetRadarColor( x, y, true, patched ):X4}",
                    ["land_patched"] = patched && facet.IsLandBlockPatched( x >> 3, y >> 3 ),
                    ["statics_patched"] = patched && facet.IsStaticBlockPatched( x >> 3, y >> 3 )
                } );
            } );

        registry.Add(
            "get_map_block",
            "The 8x8 block containing a cell: 64 land cells (row-major, with absolute x/y) and all its statics.",
            Props( FacetProp, ( "x", Int( "Any cell x inside the block." ) ), ( "y", Int( "Any cell y inside the block." ) ), PatchesProp, AlternateProp ),
            ["x", "y"],
            args =>
            {
                var data = context.RequireData();
                var facet = RequireFacet( data, args );
                var x = args.RequireInt( "x" );
                var y = args.RequireInt( "y" );
                var patched = args.GetBool( "patches" );

                if ( !facet.Contains( x, y ) )
                {
                    return Error( $"({x}, {y}) is outside {facet.Name}." );
                }

                var bx = x >> 3;
                var by = y >> 3;
                var land = new JsonArray();
                var cells = facet.GetLandBlock( bx, by, patched );

                for ( var i = 0; i < 64; i++ )
                {
                    land.Add( ( JsonNode )new JsonObject
                    {
                        ["x"] = bx * 8 + ( i & 7 ),
                        ["y"] = by * 8 + ( i >> 3 ),
                        ["id"] = Hex( cells[i].Id ),
                        ["z"] = cells[i].Z
                    } );
                }

                var statics = new JsonArray();

                foreach ( var s in facet.GetStaticBlock( bx, by, patched ) )
                {
                    statics.Add( ( JsonNode )DescribeStatic( data, s, ( bx * 8 + ( s.X & 7 ), by * 8 + ( s.Y & 7 ) ) ) );
                }

                return Json( new JsonObject
                {
                    ["facet"] = facet.Index,
                    ["block_x"] = bx,
                    ["block_y"] = by,
                    ["land"] = land,
                    ["statics"] = statics
                } );
            } );

        registry.Add(
            "render_map_region",
            $"PNG radar view of a facet rectangle (UOFiddler-style: highest static over land, radarcol colors). " +
            $"Defaults to the whole facet. 'scale' is pixels per tile (8, 4, 2, 1, 0.5, 0.25, ...); when omitted the image is fitted to 'max_size'. " +
            $"Output is limited to {MaxImageSize}x{MaxImageSize}.",
            Props(
                FacetProp,
                ( "x", Int( "Left cell (default 0)." ) ),
                ( "y", Int( "Top cell (default 0)." ) ),
                ( "width", Int( "Width in cells (default: to the facet's right edge)." ) ),
                ( "height", Int( "Height in cells (default: to the facet's bottom edge)." ) ),
                ( "scale", new JsonObject { ["type"] = "number", ["description"] = "Pixels per tile; >1 enlarges, <1 downsamples." } ),
                ( "max_size", Int( $"Fit the longer side to this many pixels when 'scale' is omitted (default 1024, max {MaxImageSize})." ) ),
                ( "statics", Bool( "Draw statics (default true)." ) ),
                PatchesProp,
                AlternateProp ),
            null,
            args =>
            {
                var facet = RequireFacet( context.RequireData(), args );
                var x = Math.Clamp( args.GetInt( "x", 0 ), 0, facet.Width - 1 );
                var y = Math.Clamp( args.GetInt( "y", 0 ), 0, facet.Height - 1 );
                var width = Math.Clamp( args.GetInt( "width", facet.Width - x ), 1, facet.Width - x );
                var height = Math.Clamp( args.GetInt( "height", facet.Height - y ), 1, facet.Height - y );
                var maxSize = Math.Clamp( args.GetInt( "max_size", 1024 ), 16, MaxImageSize );

                int step, enlarge;

                if ( GetDouble( args, "scale" ) is { } scale and > 0 )
                {
                    enlarge = scale >= 1 ? ( int )Math.Round( scale ) : 1;
                    step = scale >= 1 ? 1 : ( int )Math.Round( 1 / scale );
                }
                else
                {
                    step = Math.Max( 1, ( Math.Max( width, height ) + maxSize - 1 ) / maxSize );
                    enlarge = 1;
                }

                var outW = ( width + step - 1 ) / step * enlarge;
                var outH = ( height + step - 1 ) / step * enlarge;

                if ( outW > MaxImageSize || outH > MaxImageSize )
                {
                    return Error( $"Output {outW}x{outH} exceeds {MaxImageSize}x{MaxImageSize}; use a smaller region or scale." );
                }

                var pixmap = facet.RenderRadar( x, y, width, height, step, args.GetBool( "statics", true ), args.GetBool( "patches" ) );

                if ( enlarge > 1 )
                {
                    pixmap = Enlarge( pixmap, enlarge );
                }

                return Image( pixmap, new JsonObject
                {
                    ["facet"] = facet.Index,
                    ["x"] = x,
                    ["y"] = y,
                    ["width"] = width,
                    ["height"] = height,
                    ["tiles_per_pixel"] = step,
                    ["pixels_per_tile"] = enlarge,
                    ["image_width"] = pixmap.Width,
                    ["image_height"] = pixmap.Height
                } );
            } );
    }

    private static readonly (string, JsonObject) FacetProp = ( "facet", Int( "Facet index: 0 Felucca, 1 Trammel, 2 Ilshenar, 3 Malas, 4 Tokuno, 5 Ter Mur (default 0)." ) );

    private static readonly (string, JsonObject) PatchesProp = ( "patches", Bool( "Apply mapdif/stadif diff patches (default false)." ) );

    private static readonly (string, JsonObject) AlternateProp = ( "alternate", Bool( "Use the alternate map{N}x / staidx{N}x files if present (default false)." ) );

    private static FacetMap RequireFacet( UOData data, ToolArgs args )
    {
        var index = args.GetInt( "facet", 0 );
        var alternate = args.GetBool( "alternate" );
        return data.Maps.GetFacet( index, alternate ) ?? throw new ArgumentException( $"Facet {index}{( alternate ? " (alternate)" : "" )} is not available." );
    }

    private static double? GetDouble( ToolArgs args, string name ) =>
        args.GetString( name ) is { } text && double.TryParse( text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v ) ? v : null;

    private static JsonObject DescribeLand( UOData data, LandTile land )
    {
        var info = data.TileData.GetLand( land.Id );
        return new JsonObject { ["id"] = Hex( land.Id ), ["name"] = info.Name, ["z"] = land.Z, ["flags"] = info.Flags.ToString() };
    }

    private static JsonObject DescribeStatic( UOData data, StaticTile s, (int X, int Y)? at )
    {
        var item = data.TileData.GetItem( s.Id );
        var obj = new JsonObject();

        if ( at is { } p )
        {
            obj["x"] = p.X;
            obj["y"] = p.Y;
        }

        obj["id"] = Hex( s.Id );
        obj["name"] = item.Name;
        obj["z"] = s.Z;
        obj["hue"] = s.Hue;
        obj["height"] = item.Height;
        return obj;
    }

    private static Pixmap Enlarge( Pixmap source, int factor )
    {
        var result = new Pixmap( source.Width * factor, source.Height * factor );

        for ( var y = 0; y < result.Height; y++ )
        {
            var src = source.Row( y / factor );
            var dst = result.Row( y );

            for ( var x = 0; x < dst.Length; x++ )
            {
                dst[x] = src[x / factor];
            }
        }

        return result;
    }
}
