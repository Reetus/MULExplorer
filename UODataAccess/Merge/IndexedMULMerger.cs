using UODataAccess.IO;

namespace UODataAccess.Merge;

/// <summary>An idx/mul pair, e.g. <c>texidx.mul</c> + <c>texmaps.mul</c>.</summary>
public sealed record IndexedMULSpec( string Asset, string IdxName, string MULName )
{
    public static IndexedMULSpec Textures => new( "textures", "texidx.mul", "texmaps.mul" );

    /// <summary>MUL animations: <c>anim.idx/anim.mul</c> for 1, <c>animN.idx/animN.mul</c> otherwise.</summary>
    public static IndexedMULSpec Animations( int file ) => file <= 1
        ? new IndexedMULSpec( "anim", "anim.idx", "anim.mul" )
        : new IndexedMULSpec( $"anim{file}", $"anim{file}.idx", $"anim{file}.mul" );
}

/// <summary>
///     Merges an idx/mul pair id by id (payload bytes plus the idx extra field) and writes a fresh,
///     compact pair. Payloads are copied verbatim.
/// </summary>
public static class IndexedMULMerger
{
    public static AssetMergeResult Merge( IndexedMULSpec spec, UOFileSet local, UOFileSet upstream, IReadOnlyList<UOFileSet> stock, string? mulOutput, string? idxOutput )
    {
        var result = new AssetMergeResult( spec.Asset );
        using var l = Open( local ) ?? throw new FileNotFoundException( $"{spec.IdxName}/{spec.MULName} missing from {local.Root}." );
        using var u = Open( upstream ) ?? throw new FileNotFoundException( $"{spec.IdxName}/{spec.MULName} missing from {upstream.Root}." );
        var stockIndexes = stock.Select( Open ).OfType<FileIndex>().ToList();

        try
        {
            var count = Math.Max( l.Length, u.Length );
            var takeLocal = new bool[count];

            for ( var id = 0; id < count; id++ )
            {
                var hasLocal = l.TryRead( id, out var ld, out var le );
                var hasUpstream = u.TryRead( id, out var ud, out var ue );

                if ( !hasLocal && !hasUpstream )
                {
                    continue;
                }

                var localData = hasLocal ? ld.ToArray() : [];
                var localExtra = le.Extra;

                bool IsStock()
                {
                    foreach ( var s in stockIndexes )
                    {
                        if ( s.TryRead( id, out var sd, out var se ) && se.Extra == localExtra && sd.SequenceEqual( localData ) )
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var action = MergeRules.Decide( hasLocal, hasUpstream, hasLocal && hasUpstream && le.Extra == ue.Extra && ld.SequenceEqual( ud ), IsStock );
                result.Record( $"0x{id:X4}", action, action == MergeAction.KeptLocalOnly && IsStock(), id );
                takeLocal[id] = action.TakesLocal();
            }

            if ( mulOutput is not null && idxOutput is not null )
            {
                // Both files must land together: write to temps, then move.
                var mulTemp = mulOutput + ".tmp";
                var idxTemp = idxOutput + ".tmp";

                try
                {
                    using ( var writer = new MULFileWriter( mulTemp, idxTemp ) )
                    {
                        for ( var id = 0; id < count; id++ )
                        {
                            var source = takeLocal[id] ? l : u;

                            if ( source.TryRead( id, out var data, out var entry ) )
                            {
                                writer.Add( id, data, entry.Extra );
                            }
                        }

                        writer.Pad( count );
                    }

                    File.Move( mulTemp, mulOutput, true );
                    File.Move( idxTemp, idxOutput, true );
                }
                finally
                {
                    File.Delete( mulTemp );
                    File.Delete( idxTemp );
                }

                result.OutputPath = mulOutput;
            }

            return result;
        }
        finally
        {
            foreach ( var s in stockIndexes )
            {
                s.Dispose();
            }
        }

        FileIndex? Open( UOFileSet files ) =>
            files.Find( spec.IdxName ) is { } idx && files.Find( spec.MULName ) is { } mul ? FileIndex.OpenMUL( idx, mul ) : null;
    }
}
