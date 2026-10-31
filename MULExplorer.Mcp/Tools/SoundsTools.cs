using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Sounds tab: sound effects (name, duration) and their audio as WAV.</summary>
public sealed class SoundsTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_sounds",
            "Lists playable sound effects (id, name, duration in seconds), optionally filtered by a name substring. Paginated.",
            Props(
                ( "name", Str( "Case-insensitive name substring, e.g. 'door'." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var sounds = context.RequireData().Sounds;
                var name = args.GetString( "name" );
                var (offset, limit) = args.GetPage();
                var matches = sounds.GetAll().Where( s => name is null || s.Name.Contains( name, StringComparison.OrdinalIgnoreCase ) ).ToList();

                return Json( new JsonObject
                {
                    ["source"] = sounds.Source,
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["sounds"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( s => ( JsonNode? )Describe( s ) ).ToArray() )
                } );
            } );

        registry.Add(
            "get_sound_info",
            "Name, duration, PCM size and Sound.def remap of one sound id.",
            Props( ( "id", Id( "Sound id." ) ) ),
            ["id"],
            args =>
            {
                var id = args.RequireInt( "id" );
                return context.RequireData().Sounds.GetInfo( id ) is { } info ? Json( Describe( info ) ) : Error( $"Sound {Hex( id )} is empty." );
            } );

        registry.Add(
            "get_sound_wav",
            "The sound as a complete WAV file (PCM 22050 Hz mono 16-bit), returned as audio/wav audio content plus a JSON info block.",
            Props( ( "id", Id( "Sound id." ) ) ),
            ["id"],
            args =>
            {
                var sounds = context.RequireData().Sounds;
                var id = args.RequireInt( "id" );

                if ( sounds.GetInfo( id ) is not { } info || sounds.GetWav( id ) is not { } wav )
                {
                    return Error( $"Sound {Hex( id )} is empty." );
                }

                var json = Describe( info );
                json["wav_bytes"] = wav.Length;

                return new CallToolResult
                {
                    Content =
                    [
                        AudioContentBlock.FromBytes( wav, "audio/wav" ),
                        new TextContentBlock { Text = json.ToJsonString() }
                    ]
                };
            } );
    }

    public static JsonObject Describe( SoundInfo s ) => new()
    {
        ["id"] = Hex( s.Id ),
        ["name"] = s.Name,
        ["duration"] = Math.Round( s.Duration, 3 ),
        ["pcm_bytes"] = s.SampleBytes,
        ["remapped_to"] = s.IsRemapped ? Hex( s.SourceId ) : null
    };
}
