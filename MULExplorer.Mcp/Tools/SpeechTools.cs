using System.Text.Json.Nodes;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Speech tab: speech.mul keywords.</summary>
public sealed class SpeechTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "search_speech",
            "Searches speech.mul keywords by text substring and/or speech id. Paginated; entries are in file order.",
            Props(
                ( "text", Str( "Case-insensitive keyword substring, e.g. 'bank'." ) ),
                ( "id", Id( "Only entries with this speech id." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var speech = context.RequireData().Speech;
                var text = args.GetString( "text" );
                var id = args.GetInt( "id" );
                var (offset, limit) = args.GetPage();
                var matches = speech.Entries
                    .Where( e => ( id is null || e.Id == id ) && ( text is null || e.Keyword.Contains( text, StringComparison.OrdinalIgnoreCase ) ) )
                    .ToList();

                return Json( new JsonObject
                {
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["entries"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( e => ( JsonNode? )Describe( e ) ).ToArray() )
                } );
            } );

        registry.Add(
            "match_speech_keywords",
            "Which speech keywords the client would detect (and send as keyword ids) in a spoken phrase, e.g. 'I wish to bank'.",
            Props( ( "phrase", Str( "What the player says." ) ) ),
            ["phrase"],
            args =>
            {
                var matches = context.RequireData().Speech.GetKeywords( args.RequireString( "phrase" ) );
                return Json( new JsonObject
                {
                    ["ids"] = new JsonArray( matches.Select( e => e.Id ).Distinct().Select( i => ( JsonNode? )i ).ToArray() ),
                    ["entries"] = new JsonArray( matches.Select( e => ( JsonNode? )Describe( e ) ).ToArray() )
                } );
            } );
    }

    private static JsonObject Describe( SpeechEntry e ) => new() { ["order"] = e.Order, ["id"] = e.Id, ["keyword"] = e.Keyword };
}
