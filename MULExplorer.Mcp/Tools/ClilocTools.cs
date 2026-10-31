using System.Text.Json.Nodes;
using UODataAccess;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>CliLoc tab: localized strings in every language the client ships.</summary>
public sealed class ClilocTools : IToolProvider
{
    private static readonly JsonObject LanguageSchema = Str( "Language code, i.e. the Cliloc file extension: 'enu' (default), 'deu', 'fra', 'esp', 'jpn', 'kor', 'chs', 'cht'... See list_cliloc_languages." );

    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_cliloc_languages",
            "Languages available in the client folder (Cliloc.* file extensions).",
            null,
            null,
            _ => Json( new JsonArray( Cliloc.AvailableLanguages( context.RequireData().Files ).Select( l => ( JsonNode? )l ).ToArray() ) ) );

        registry.Add(
            "get_cliloc",
            "Text of a cliloc number. With 'args' (tab-separated, like the server sends them) ~1_NAME~ placeholders are filled.",
            Props(
                ( "number", Id( "Cliloc number, e.g. 1041522." ) ),
                ( "language", LanguageSchema ),
                ( "args", Str( "Optional tab-separated arguments for ~n_NAME~ placeholders." ) ) ),
            ["number"],
            args =>
            {
                var cliloc = Get( context.RequireData(), args );
                var number = args.RequireInt( "number" );

                if ( !cliloc.TryGet( number, out var entry ) )
                {
                    return Error( $"Cliloc {number} not found in '{cliloc.Language}'." );
                }

                var json = Describe( entry );
                json["language"] = cliloc.Language;

                if ( args.GetString( "args" ) is { } a )
                {
                    json["formatted"] = cliloc.Format( number, a );
                }

                return Json( json );
            } );

        registry.Add(
            "search_cliloc",
            "Finds cliloc entries whose text contains the query (case-insensitive) or matches it as a regex, or whose number equals it. Paginated.",
            Props(
                ( "query", Str( "Text to find, or a number." ) ),
                ( "language", LanguageSchema ),
                ( "regex", Bool( "Treat query as a .NET regular expression (default false)." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            ["query"],
            args =>
            {
                var cliloc = Get( context.RequireData(), args );
                var (offset, limit) = args.GetPage();
                var matches = cliloc.Search( args.RequireString( "query" ), args.GetBool( "regex" ) ).ToList();

                return Json( new JsonObject
                {
                    ["language"] = cliloc.Language,
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["entries"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( e => ( JsonNode? )Describe( e ) ).ToArray() )
                } );
            } );
    }

    private static Cliloc Get( UOData data, ToolArgs args ) => Cliloc.Get( data.Files, args.GetString( "language" ) is { Length: > 0 } l ? l : "enu" );

    private static JsonObject Describe( ClilocEntry e ) => new()
    {
        ["number"] = e.Number,
        ["hex"] = $"0x{e.Number:X}",
        ["flag"] = e.Flag,
        ["text"] = e.Text
    };
}
