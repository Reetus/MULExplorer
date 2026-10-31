using System.IO.MemoryMappedFiles;

namespace UODataAccess.IO;

/// <summary>
///     Read-only view over a whole file. Large files are memory-mapped and exposed as spans
///     without copying; if mapping fails (empty file, exotic filesystem) the bytes are read
///     into memory instead. Either way callers only ever see <see cref="ReadOnlySpan{T}" />.
/// </summary>
public sealed unsafe class MappedFile : IDisposable
{
    private readonly byte[]? _bytes;
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private byte* _ptr;

    private MappedFile( string path, byte[] bytes )
    {
        Path = path;
        _bytes = bytes;
        Length = bytes.LongLength;
    }

    private MappedFile( string path, MemoryMappedFile mmf, MemoryMappedViewAccessor view, long length )
    {
        Path = path;
        _mmf = mmf;
        _view = view;
        Length = length;
        view.SafeMemoryMappedViewHandle.AcquirePointer( ref _ptr );
        _ptr += view.PointerOffset;
    }

    public string Path { get; }

    public long Length { get; }

    /// <summary>Whole file as a span. Only valid for files under 2 GB (every UO file is).</summary>
    public ReadOnlySpan<byte> Span => Slice( 0, ( int )Math.Min( Length, int.MaxValue ) );

    public void Dispose()
    {
        if ( _view is not null && _ptr is not null )
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }

        _view?.Dispose();
        _mmf?.Dispose();
    }

    /// <summary>Files at or below this size are simply read into memory.</summary>
    private const long MapThreshold = 256 * 1024;

    public static MappedFile Open( string path )
    {
        var length = new FileInfo( path ).Length;

        if ( length > MapThreshold )
        {
            try
            {
                var mmf = MemoryMappedFile.CreateFromFile( path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read );
                var view = mmf.CreateViewAccessor( 0, 0, MemoryMappedFileAccess.Read );
                return new MappedFile( path, mmf, view, length );
            }
            catch ( IOException )
            {
                // Fall through to an in-memory copy.
            }
            catch ( UnauthorizedAccessException )
            {
            }
        }

        return new MappedFile( path, File.ReadAllBytes( path ) );
    }

    /// <summary>Returns the requested range, clamped to the end of the file.</summary>
    public ReadOnlySpan<byte> Slice( long offset, int length )
    {
        if ( offset < 0 || offset >= Length || length <= 0 )
        {
            return [];
        }

        length = ( int )Math.Min( length, Length - offset );

        return _bytes is not null
            ? new ReadOnlySpan<byte>( _bytes, ( int )offset, length )
            : new ReadOnlySpan<byte>( _ptr + offset, length );
    }
}
