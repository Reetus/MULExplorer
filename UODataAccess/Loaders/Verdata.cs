using System.Buffers.Binary;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>One verdata.mul record: entry <see cref="Index" /> of file <see cref="File" /> is replaced by the bytes at <see cref="Lookup" />.</summary>
public readonly record struct VerdataPatch( int File, int Index, int Lookup, int Length, int Extra )
{
    public string FileName => Verdata.GetFileName( File );
}

/// <summary>
///     <c>verdata.mul</c>: <c>count:i32</c> then <c>(file, index, lookup, length, extra)</c> records
///     (ClassicUO's <c>UOFileIndex5D</c>). Old clients used it to patch other files; modern ones ship
///     none, so a missing file is not an error: <see cref="IsPresent" /> is false and there are no patches.
///     A truncated table is read as far as it goes rather than throwing.
/// </summary>
public sealed class Verdata : IDisposable
{
    private const int RecordSize = 20;

    private readonly MappedFile? _file;

    public Verdata( UOFileSet files )
    {
        if ( files.Find( "verdata.mul" ) is not { } path )
        {
            return;
        }

        _file = MappedFile.Open( path );
        Path = path;
        var data = _file.Slice( 0, ( int )Math.Min( _file.Length, int.MaxValue ) );

        if ( data.Length < 4 )
        {
            return;
        }

        var declared = BinaryPrimitives.ReadInt32LittleEndian( data );
        var count = ( int )Math.Clamp( declared, 0, ( data.Length - 4 ) / RecordSize );
        var patches = new VerdataPatch[count];

        for ( var i = 0; i < count; i++ )
        {
            var r = data.Slice( 4 + i * RecordSize, RecordSize );
            patches[i] = new VerdataPatch(
                BinaryPrimitives.ReadInt32LittleEndian( r ),
                BinaryPrimitives.ReadInt32LittleEndian( r[4..] ),
                BinaryPrimitives.ReadInt32LittleEndian( r[8..] ),
                BinaryPrimitives.ReadInt32LittleEndian( r[12..] ),
                BinaryPrimitives.ReadInt32LittleEndian( r[16..] ) );
        }

        Patches = patches;
        DeclaredCount = declared;
    }

    /// <summary>False when the client has no verdata.mul (all modern clients).</summary>
    public bool IsPresent => _file is not null;

    public string? Path { get; }

    /// <summary>Record count from the header (may exceed <see cref="Patches" /> for a truncated file).</summary>
    public int DeclaredCount { get; }

    /// <summary>True if the header declared more records than the file holds.</summary>
    public bool IsTruncated => DeclaredCount > Patches.Count;

    public IReadOnlyList<VerdataPatch> Patches { get; } = [];

    public void Dispose() => _file?.Dispose();

    /// <summary>The patch payload (clamped to the end of the file).</summary>
    public ReadOnlySpan<byte> GetData( in VerdataPatch patch ) => _file is null || patch.Lookup < 0 ? [] : _file.Slice( patch.Lookup, patch.Length );

    /// <summary>The file a verdata file id patches (UOFiddler's VerdataControl mapping).</summary>
    public static string GetFileName( int fileId ) => fileId switch
    {
        0 => "map0.mul",
        1 => "staidx0.mul",
        2 => "statics0.mul",
        3 => "artidx.mul",
        4 => "art.mul",
        5 => "anim.idx",
        6 => "anim.mul",
        7 => "soundidx.mul",
        8 => "sound.mul",
        9 => "texidx.mul",
        10 => "texmaps.mul",
        11 => "gumpidx.mul",
        12 => "gumpart.mul",
        13 => "multi.idx",
        14 => "multi.mul",
        15 => "skills.idx",
        16 => "skills.mul",
        30 => "tiledata.mul",
        31 => "animdata.mul",
        _ => "unknown"
    };
}
