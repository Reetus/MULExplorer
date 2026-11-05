using UODataAccess.IO;
using UODataAccess.Loaders;

namespace UODataAccess.Merge;

/// <summary>
///     Merges one cliloc language by number. Text is compared (the flag byte is ignored); the output
///     is written uncompressed, which ClassicUO-based clients read alongside the BWT form.
/// </summary>
public static class ClilocMerger
{
    public static AssetMergeResult Merge( string language, UOFileSet local, UOFileSet upstream, IReadOnlyList<UOFileSet> stock, string? outputPath )
    {
        var fileName = $"Cliloc.{language}";
        var result = new AssetMergeResult( $"cliloc.{language}" );
        var l = Cliloc.Load( local, language );
        var u = Cliloc.Load( upstream, language );
        var stockClilocs = stock.Where( s => s.Exists( fileName ) ).Select( s => Cliloc.Load( s, language ) ).ToList();
        var merged = new List<ClilocEntry>( u.Count + 64 );

        foreach ( var number in l.Entries.Select( e => e.Number ).Union( u.Entries.Select( e => e.Number ) ).Order() )
        {
            // An empty string is an unused slot (older clients ship blanks for ids defined later).
            var hasLocal = l.TryGet( number, out var le ) && le.Text.Length > 0;
            var hasUpstream = u.TryGet( number, out var ue ) && ue.Text.Length > 0;

            if ( !hasLocal && !hasUpstream )
            {
                result.Record( number.ToString( System.Globalization.CultureInfo.InvariantCulture ), MergeAction.Unchanged );
                merged.Add( u.TryGet( number, out var blank ) ? blank : le );
                continue;
            }

            bool IsStock() => stockClilocs.Any( s => s.GetString( number ) == le.Text );

            var action = MergeRules.Decide( hasLocal, hasUpstream, hasLocal && hasUpstream && le.Text == ue.Text, IsStock );
            result.Record( number.ToString( System.Globalization.CultureInfo.InvariantCulture ), action, action == MergeAction.KeptLocalOnly && IsStock(), number );
            merged.Add( action.TakesLocal() ? le : ue );
        }

        if ( outputPath is not null )
        {
            UOPMerger.WriteAtomically( outputPath, path =>
            {
                using var stream = File.Create( path );
                Cliloc.Write( stream, merged );
            } );

            result.OutputPath = outputPath;
        }

        return result;
    }
}
