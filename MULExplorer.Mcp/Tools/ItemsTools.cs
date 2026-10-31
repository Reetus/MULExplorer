using System.Text.Json.Nodes;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Items tab: static art plus its tiledata entry.</summary>
public sealed class ItemsTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "get_item",
            "Tiledata for an item id (name, flags, weight, height, layer, animation...) and its art size.",
            Props( ( "id", Id( "Item id." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );

                if ( id < 0 || id >= data.TileData.ItemTable.Length )
                {
                    return Error( $"Item id {Hex( id )} is out of range (0..{Hex( data.TileData.ItemTable.Length - 1 )})." );
                }

                return Json( Describe( data.TileData.GetItem( id ), data.Art.GetItemSize( id ) ) );
            } );

        registry.Add(
            "get_item_image",
            "PNG of an item's art, optionally hued. Partial-hue items only recolor gray pixels.",
            Props( ( "id", Id( "Item id." ) ), ( "hue", Id( "Game hue number (0 = none)." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );
                var hue = args.GetInt( "hue", 0 );

                if ( data.Art.GetItem( id ) is not { } pixmap )
                {
                    return Error( $"Item {Hex( id )} has no art." );
                }

                if ( hue > 0 )
                {
                    pixmap = data.Hues.Apply( pixmap, hue, data.TileData.GetItem( id ).Flags.HasFlag( TileFlag.PartialHue ) );
                }

                return Image( pixmap, new JsonObject { ["id"] = Hex( id ), ["width"] = pixmap.Width, ["height"] = pixmap.Height } );
            } );

        registry.Add(
            "search_items",
            "Finds items by name substring and/or required flags. Paginated.",
            Props(
                ( "name", Str( "Case-insensitive name substring." ) ),
                ( "flags", Str( $"Comma-separated flags that must all be set, e.g. 'Wearable,PartialHue'. One of: {string.Join( ", ", Enum.GetNames<TileFlag>() )}." ) ),
                ( "with_art_only", Bool( "Only items that have art (default true)." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var name = args.GetString( "name" );
                var flags = ParseFlags( args.GetString( "flags" ) );
                var artOnly = args.GetBool( "with_art_only", true );
                var (offset, limit) = args.GetPage();

                var matches = data.TileData.ItemTable
                    .Where( i => ( name is null || i.Name.Contains( name, StringComparison.OrdinalIgnoreCase ) ) && ( i.Flags & flags ) == flags )
                    .Where( i => !artOnly || data.Art.IsValidItem( i.Id ) )
                    .ToList();

                return Json( new JsonObject
                {
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["items"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( i => ( JsonNode? )new JsonObject
                    {
                        ["id"] = Hex( i.Id ),
                        ["name"] = i.Name,
                        ["flags"] = i.Flags.ToString()
                    } ).ToArray() )
                } );
            } );

        registry.Add(
            "list_tile_flags",
            "All tiledata flag names with their bit values.",
            null,
            null,
            _ => Json( new JsonArray( Enum.GetValues<TileFlag>().Where( f => f != TileFlag.None )
                .Select( f => ( JsonNode? )new JsonObject { ["name"] = f.ToString(), ["value"] = $"0x{( ulong )f:X}" } ).ToArray() ) ) );
    }

    public static JsonObject Describe( ItemData item, (int Width, int Height)? size ) => new()
    {
        ["id"] = Hex( item.Id ),
        ["name"] = item.Name,
        ["flags"] = item.Flags.ToString(),
        ["weight"] = item.Weight,
        ["height"] = item.Height,
        ["quality_layer_light"] = item.Quality,
        ["quantity"] = item.Quantity,
        ["animation"] = item.Animation,
        ["hue"] = item.Hue,
        ["stacking_offset"] = item.StackingOffset,
        ["value"] = item.Value,
        ["misc_data"] = item.MiscData,
        ["unknown2"] = item.Unknown2,
        ["unknown3"] = item.Unknown3,
        ["art"] = size is { } s ? new JsonObject { ["width"] = s.Width, ["height"] = s.Height } : null
    };

    public static TileFlag ParseFlags( string? text )
    {
        var flags = TileFlag.None;

        if ( string.IsNullOrWhiteSpace( text ) )
        {
            return flags;
        }

        foreach ( var part in text.Split( [',', '|', ' '], StringSplitOptions.RemoveEmptyEntries ) )
        {
            flags |= Enum.TryParse<TileFlag>( part, true, out var f ) ? f : throw new ArgumentException( $"Unknown tile flag '{part}'." );
        }

        return flags;
    }
}
