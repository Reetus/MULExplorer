using System.Globalization;
using System.Text.Json.Nodes;
using UODataAccess.Imaging;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Hues tab: the 32-color ramps in hues.mul.</summary>
public sealed class HuesTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "get_hue",
            "A hue's name, table start/end and its 32 colors (ARGB1555 and #RRGGBB). Takes the in-game hue number (1-based).",
            Props( ( "hue", Id( "Game hue number (1..count; the file index is hue - 1)." ) ) ),
            ["hue"],
            args =>
            {
                var data = context.RequireData();
                var number = args.RequireInt( "hue" );
                return data.Hues.Get( number ) is { } hue ? Json( Describe( hue, true ) ) : Error( $"Hue {number} does not exist (1..{data.Hues.List.Count})." );
            } );

        registry.Add(
            "search_hues",
            "Finds hues by name substring or number. Paginated; colors omitted (use get_hue).",
            Props(
                ( "query", Str( "Name substring (case-insensitive), or a hue number (decimal or 0x hex)." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var (offset, limit) = args.GetPage();
                var matches = Filter( data.Hues, args.GetString( "query" ) ).ToList();

                return Json( new JsonObject
                {
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["hues"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( h => ( JsonNode? )Describe( h, false ) ).ToArray() )
                } );
            } );

        registry.Add(
            "get_hue_ramp_image",
            "PNG of a hue's 32-color ramp, each color drawn as a cell_width x height block.",
            Props(
                ( "hue", Id( "Game hue number." ) ),
                ( "cell_width", Int( "Width of each color cell in pixels (default 12)." ) ),
                ( "height", Int( "Image height in pixels (default 24)." ) ) ),
            ["hue"],
            args =>
            {
                var data = context.RequireData();
                var number = args.RequireInt( "hue" );

                if ( data.Hues.Get( number ) is not { } hue )
                {
                    return Error( $"Hue {number} does not exist." );
                }

                var image = RampImage( hue, Math.Clamp( args.GetInt( "cell_width", 12 ), 1, 64 ), Math.Clamp( args.GetInt( "height", 24 ), 1, 256 ) );
                return Image( image, Describe( hue, false ) );
            } );
    }

    /// <summary>Matches by number (decimal or 0x hex) or name substring; empty query = all.</summary>
    public static IEnumerable<Hue> Filter( Hues hues, string? query )
    {
        query = query?.Trim();

        if ( string.IsNullOrEmpty( query ) )
        {
            return hues.List;
        }

        var number = ToolArgs.ParseInt( query );
        return hues.List.Where( h => h.Index + 1 == number || h.Name.Contains( query, StringComparison.OrdinalIgnoreCase ) );
    }

    public static Pixmap RampImage( Hue hue, int cellWidth, int height )
    {
        var pixmap = new Pixmap( 32 * cellWidth, height );

        for ( var y = 0; y < height; y++ )
        {
            var row = pixmap.Row( y );

            for ( var i = 0; i < 32; i++ )
            {
                row.Slice( i * cellWidth, cellWidth ).Fill( hue.ArgbAt( i ) );
            }
        }

        return pixmap;
    }

    public static JsonObject Describe( Hue hue, bool withColors )
    {
        var json = new JsonObject
        {
            ["hue"] = hue.Index + 1,
            ["hex"] = $"0x{hue.Index + 1:X}",
            ["index"] = hue.Index,
            ["name"] = hue.Name,
            ["tableStart"] = $"0x{hue.TableStart:X4}",
            ["tableEnd"] = $"0x{hue.TableEnd:X4}"
        };

        if ( withColors )
        {
            json["colors"] = new JsonArray( Enumerable.Range( 0, 32 ).Select( i => ( JsonNode? )new JsonObject
            {
                ["index"] = i,
                ["color16"] = $"0x{hue.Colors[i]:X4}",
                ["rgb"] = "#" + ( hue.ArgbAt( i ) & 0xFFFFFF ).ToString( "X6", CultureInfo.InvariantCulture )
            } ).ToArray() );
        }

        return json;
    }
}
