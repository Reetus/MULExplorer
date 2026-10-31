using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>A sound slot: its name header, PCM size and, for Sound.def remaps, the id actually played.</summary>
public readonly record struct SoundInfo( int Id, string Name, int SampleBytes, int SourceId )
{
    /// <summary>True when the slot is empty and Sound.def points it at another sound.</summary>
    public bool IsRemapped => SourceId != Id;

    public double Duration => SampleBytes / ( double )( Sounds.SampleRate * Sounds.BytesPerSample );
}

/// <summary>
///     Sound effects: <c>soundLegacyMUL.uop</c> (<c>build/soundlegacymul/{id:D8}.dat</c>) or
///     <c>soundidx.mul</c>+<c>sound.mul</c>. Each entry is a 40-byte name block (ASCII name, then
///     uninitialised bytes the client skips) followed by 16-bit 22050 Hz mono PCM. Empty slots can be
///     redirected to another id by <c>Sound.def</c> (<c>index {alt, ...} volume</c>).
/// </summary>
public sealed class Sounds : IDisposable
{
    public const int SampleRate = 22050;
    public const int BytesPerSample = 2;

    /// <summary>Bytes before the samples; the client skips 0x28 (UOFiddler, ClassicUO).</summary>
    public const int HeaderLength = 0x28;

    /// <summary>Highest id probed (matches ClassicUO's <c>MAX_SOUND_DATA_INDEX_COUNT</c>).</summary>
    public const int MaxCount = 0xFFFF;

    private const int WavHeaderLength = 44;

    private readonly FileIndex _index;
    private readonly Dictionary<int, int> _remaps = [];
    private IReadOnlyList<SoundInfo>? _all;

    public Sounds( UOFileSet files )
    {
        _index = FileIndex.Open( files, "soundidx.mul", "sound.mul", "soundLegacyMUL.uop", "build/soundlegacymul/{0:D8}.dat", MaxCount )
                 ?? throw new FileNotFoundException( "soundLegacyMUL.uop or soundidx.mul/sound.mul not found." );

        if ( files.Find( "Sound.def" ) is { } def )
        {
            LoadDefinitions( File.ReadAllText( def ) );
        }
    }

    /// <summary>"UOP" or "MUL".</summary>
    public string Source => _index.Source;

    /// <summary>Number of id slots (valid or not).</summary>
    public int Count => _index.Length;

    /// <summary>Empty slots redirected by Sound.def: id → id actually played.</summary>
    public IReadOnlyDictionary<int, int> Remaps => _remaps;

    public void Dispose() => _index.Dispose();

    /// <summary>True if <paramref name="id" /> has samples (directly or via Sound.def).</summary>
    public bool IsValid( int id ) => Resolve( id ) >= 0;

    /// <summary>Name and size without decoding samples; null for empty slots.</summary>
    public SoundInfo? GetInfo( int id )
    {
        var source = Resolve( id );

        if ( source < 0 || !_index.TryRead( source, out var data ) || data.Length < HeaderLength )
        {
            return null;
        }

        return new SoundInfo( id, ReadName( data[..HeaderLength] ), data.Length - HeaderLength, source );
    }

    /// <summary>Every playable sound in id order (scanned once, then cached).</summary>
    public IReadOnlyList<SoundInfo> GetAll()
    {
        if ( Volatile.Read( ref _all ) is { } all )
        {
            return all;
        }

        var scanned = Scan();
        return Interlocked.CompareExchange( ref _all, scanned, null ) ?? scanned;
    }

    private IReadOnlyList<SoundInfo> Scan()
    {
        var list = new List<SoundInfo>();

        for ( var id = 0; id < Count; id++ )
        {
            if ( GetInfo( id ) is { } info )
            {
                list.Add( info );
            }
        }

        return list;
    }

    /// <summary>Raw 16-bit little-endian mono PCM samples (header stripped), or null.</summary>
    public byte[]? GetPCM( int id )
    {
        var source = Resolve( id );
        return source >= 0 && _index.TryRead( source, out var data ) && data.Length >= HeaderLength ? data[HeaderLength..].ToArray() : null;
    }

    /// <summary>A complete RIFF/WAVE file (PCM, 22050 Hz, mono, 16-bit) for <paramref name="id" />, or null.</summary>
    public byte[]? GetWav( int id )
    {
        var source = Resolve( id );
        return source >= 0 && _index.TryRead( source, out var data ) && data.Length >= HeaderLength ? CreateWav( data[HeaderLength..] ) : null;
    }

    /// <summary>Wraps PCM samples in a canonical 44-byte WAV header.</summary>
    public static byte[] CreateWav( ReadOnlySpan<byte> pcm, int sampleRate = SampleRate, short channels = 1, short bitsPerSample = 16 )
    {
        var wav = new byte[WavHeaderLength + pcm.Length];
        var span = wav.AsSpan();
        var blockAlign = ( short )( channels * bitsPerSample / 8 );

        "RIFF"u8.CopyTo( span );
        BinaryPrimitives.WriteInt32LittleEndian( span[4..], 36 + pcm.Length );
        "WAVE"u8.CopyTo( span[8..] );
        "fmt "u8.CopyTo( span[12..] );
        BinaryPrimitives.WriteInt32LittleEndian( span[16..], 16 );
        BinaryPrimitives.WriteInt16LittleEndian( span[20..], 1 ); // PCM
        BinaryPrimitives.WriteInt16LittleEndian( span[22..], channels );
        BinaryPrimitives.WriteInt32LittleEndian( span[24..], sampleRate );
        BinaryPrimitives.WriteInt32LittleEndian( span[28..], sampleRate * blockAlign );
        BinaryPrimitives.WriteInt16LittleEndian( span[32..], blockAlign );
        BinaryPrimitives.WriteInt16LittleEndian( span[34..], bitsPerSample );
        "data"u8.CopyTo( span[36..] );
        BinaryPrimitives.WriteInt32LittleEndian( span[40..], pcm.Length );
        pcm.CopyTo( span[WavHeaderLength..] );
        return wav;
    }

    /// <summary>The id whose data plays for <paramref name="id" />, or -1.</summary>
    private int Resolve( int id )
    {
        if ( _index.IsValid( id ) )
        {
            return id;
        }

        return _remaps.TryGetValue( id, out var target ) ? target : -1;
    }

    /// <summary>
    ///     Like ClassicUO: only empty slots are remapped; each alternative in the braces that has data
    ///     replaces the previous one (so the last valid wins) and <c>-1</c> clears the slot.
    /// </summary>
    private void LoadDefinitions( string text )
    {
        foreach ( var rawLine in text.AsSpan().EnumerateLines() )
        {
            var line = rawLine;
            var comment = line.IndexOf( '#' );

            if ( comment >= 0 )
            {
                line = line[..comment];
            }

            line = line.Trim();
            var open = line.IndexOf( '{' );
            var close = line.IndexOf( '}' );

            if ( open <= 0 || close < open || !int.TryParse( line[..open].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index ) )
            {
                continue;
            }

            if ( index < 0 || index >= MaxCount || _index.IsValid( index ) )
            {
                continue;
            }

            var target = -1;
            var group = line[( open + 1 )..close];

            foreach ( var range in group.SplitAny( ", \t" ) )
            {
                if ( !int.TryParse( group[range], NumberStyles.Integer, CultureInfo.InvariantCulture, out var alt ) || alt < -1 || alt >= MaxCount )
                {
                    continue;
                }

                if ( alt == -1 )
                {
                    target = -1;
                }
                else if ( _index.IsValid( alt ) )
                {
                    target = alt;
                }
            }

            if ( target >= 0 )
            {
                _remaps[index] = target;
            }
        }
    }

    private static string ReadName( ReadOnlySpan<byte> header )
    {
        var end = header.IndexOf( ( byte )0 );
        var name = end < 0 ? header : header[..end];
        return Encoding.ASCII.GetString( name ).Trim();
    }
}
