using System.Text.Json.Nodes;
using UODataAccess.Imaging;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>AnimData tab: animated items from animdata.mul.</summary>
public sealed class AnimDataTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_animdata",
            "Item ids that have an animdata.mul entry, with name and frame count. Paginated.",
            Props(
                ( "name", Str( "Case-insensitive item name substring." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var name = args.GetString( "name" );
                var (offset, limit) = args.GetPage();
                var matches = data.AnimData.Ids
                    .Where( id => name is null || data.TileData.GetItem( id ).Name.Contains( name, StringComparison.OrdinalIgnoreCase ) )
                    .ToList();

                return Json( new JsonObject
                {
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["items"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( id => ( JsonNode? )new JsonObject
                    {
                        ["id"] = Hex( id ),
                        ["name"] = data.TileData.GetItem( id ).Name,
                        ["frames"] = data.AnimData.Get( id )!.FrameCount
                    } ).ToArray() )
                } );
            } );

        registry.Add(
            "get_animdata",
            "animdata.mul entry of an item: frame count, frame interval, start interval, unknown byte and the frame offsets with the resulting item ids.",
            Props( ( "id", Id( "Item id." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );

                if ( data.AnimData.Get( id ) is not { } entry )
                {
                    return Error( $"Item {Hex( id )} has no animdata entry." );
                }

                return Json( new JsonObject
                {
                    ["id"] = Hex( id ),
                    ["name"] = data.TileData.GetItem( id ).Name,
                    ["frameCount"] = entry.FrameCount,
                    ["frameInterval"] = entry.FrameInterval,
                    ["startInterval"] = entry.StartInterval,
                    ["unknown"] = entry.Unknown,
                    ["frameDelayMs"] = entry.FrameDelayMilliseconds,
                    ["frames"] = new JsonArray( Enumerable.Range( 0, entry.FrameCount ).Select( i => ( JsonNode? )new JsonObject
                    {
                        ["offset"] = entry.Offsets[Math.Min( i, entry.Offsets.Length - 1 )],
                        ["item"] = Hex( entry.FrameItem( i ) )
                    } ).ToArray() )
                } );
            } );

        registry.Add(
            "get_animdata_strip",
            "PNG with the art of every animdata frame of an item side by side (bottom-aligned).",
            Props( ( "id", Id( "Item id." ) ), ( "hue", Id( "Game hue number (0 = none)." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );
                var hue = args.GetInt( "hue", 0 );

                if ( data.AnimData.Get( id ) is not { } entry )
                {
                    return Error( $"Item {Hex( id )} has no animdata entry." );
                }

                var frames = Enumerable.Range( 0, entry.FrameCount ).Select( i => data.Art.GetItem( entry.FrameItem( i ) ) ).ToList();

                if ( frames.All( f => f is null ) )
                {
                    return Error( $"Item {Hex( id )}'s frames have no art." );
                }

                var cell = frames.Max( f => f?.Width ?? 0 );
                var height = frames.Max( f => f?.Height ?? 0 );
                var strip = new Pixmap( frames.Count * ( cell + 2 ), height );

                for ( var i = 0; i < frames.Count; i++ )
                {
                    if ( frames[i] is not { } art )
                    {
                        continue;
                    }

                    if ( hue > 0 )
                    {
                        art = data.Hues.Apply( art, hue, data.TileData.GetItem( id ).Flags.HasFlag( TileFlag.PartialHue ) );
                    }

                    strip.Blit( art, i * ( cell + 2 ) + ( cell - art.Width ) / 2, height - art.Height );
                }

                return Image( strip, new JsonObject { ["id"] = Hex( id ), ["frames"] = frames.Count, ["width"] = strip.Width, ["height"] = strip.Height } );
            } );
    }
}
