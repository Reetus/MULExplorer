using System.Text.Json.Nodes;
using UODataAccess;
using UODataAccess.Imaging;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>RadarColor tab: radarcol.mul colors for land tiles and items.</summary>
public sealed class RadarColorTools : IToolProvider
{
    private static JsonObject KindSchema => OneOf( "Tile kind (default item).", "item", "land" );

    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "get_radar_color",
            "The radar (overview map) color of a land tile or item: raw ARGB1555 value, 5-bit and 8-bit RGB, and the tile name.",
            Props( ( "id", Id( "Land or item id." ) ), ( "kind", KindSchema ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var land = IsLand( args );
                var id = args.RequireInt( "id" );
                var count = land ? Math.Min( 0x4000, data.RadarColors.Colors.Length ) : data.RadarColors.ItemCount;

                if ( ( uint )id >= ( uint )count )
                {
                    return Error( $"{( land ? "Land" : "Item" )} id {Hex( id )} is out of range (0..{Hex( count - 1 )})." );
                }

                return Json( Describe( data, land, id ) );
            } );

        registry.Add(
            "list_radar_colors",
            "Radar colors for a run of consecutive land or item ids. Paginated by start id.",
            Props( ( "kind", KindSchema ), ( "start", Id( "First id (default 0)." ) ), ( "limit", Int( "Number of ids (default 50, max 1000)." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var land = IsLand( args );
                var count = land ? Math.Min( 0x4000, data.RadarColors.Colors.Length ) : data.RadarColors.ItemCount;
                var start = Math.Max( 0, args.GetInt( "start", 0 ) );
                var limit = Math.Clamp( args.GetInt( "limit", 50 ), 1, 1000 );

                return Json( new JsonObject
                {
                    ["kind"] = land ? "land" : "item",
                    ["total"] = count,
                    ["colors"] = new JsonArray( Enumerable.Range( start, Math.Max( 0, Math.Min( limit, count - start ) ) )
                        .Select( id => ( JsonNode? )Describe( data, land, id ) ).ToArray() )
                } );
            } );
    }

    private static bool IsLand( ToolArgs args ) => args.GetString( "kind" ) is { } kind && kind.Equals( "land", StringComparison.OrdinalIgnoreCase );

    private static JsonObject Describe( UOData data, bool land, int id )
    {
        var color = land ? data.RadarColors.GetLandColor( id ) : data.RadarColors.GetItemColor( id );
        var argb = Color16.ToArgb( color );
        var tiles = data.TryGet( d => d.TileData );

        return new JsonObject
        {
            ["id"] = Hex( id ),
            ["name"] = land ? tiles?.GetLand( id ).Name : tiles?.GetItem( id ).Name,
            ["color"] = $"0x{color:X4}",
            ["rgb5"] = new JsonArray( ( color >> 10 ) & 0x1F, ( color >> 5 ) & 0x1F, color & 0x1F ),
            ["rgb8"] = $"#{argb & 0xFFFFFF:X6}"
        };
    }
}
