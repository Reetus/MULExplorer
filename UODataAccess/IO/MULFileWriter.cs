using System.Buffers.Binary;

namespace UODataAccess.IO;

/// <summary>
///     Writes a legacy <c>xxx.mul</c> + <c>xxxidx.mul</c> pair. Payloads are appended to the data file
///     as they are added; the 12-byte <c>(offset, length, extra)</c> index records are written on
///     <see cref="Dispose" />. Ids never added are written as unused (<c>-1, -1, -1</c>; official
///     files use that and <c>0, 0, 0</c>).
/// </summary>
public sealed class MULFileWriter : IDisposable
{
    private readonly Stream _data;
    private readonly string _idxPath;
    private readonly List<(int Offset, int Length, int Extra)> _index = [];
    private bool _disposed;

    public MULFileWriter( string mulPath, string idxPath )
    {
        _data = File.Create( mulPath );
        _idxPath = idxPath;
    }

    public int Count => _index.Count;

    /// <summary>Stores <paramref name="data" /> for <paramref name="id" />. Ids must be added in ascending order.</summary>
    public void Add( int id, ReadOnlySpan<byte> data, int extra )
    {
        ObjectDisposedException.ThrowIf( _disposed, this );
        Pad( id );

        if ( _data.Position > int.MaxValue - data.Length )
        {
            throw new IOException( "MUL data files are limited to 2 GB." );
        }

        _index.Add( ( ( int )_data.Position, data.Length, extra ) );
        _data.Write( data );
    }

    /// <summary>Marks every id up to <paramref name="count" /> - 1 that was not added as unused.</summary>
    public void Pad( int count )
    {
        while ( _index.Count < count )
        {
            _index.Add( ( -1, -1, -1 ) );
        }
    }

    public void Dispose()
    {
        if ( _disposed )
        {
            return;
        }

        _disposed = true;
        _data.Dispose();

        var idx = new byte[_index.Count * 12];

        for ( var i = 0; i < _index.Count; i++ )
        {
            var (offset, length, extra) = _index[i];
            BinaryPrimitives.WriteInt32LittleEndian( idx.AsSpan( i * 12 ), offset );
            BinaryPrimitives.WriteInt32LittleEndian( idx.AsSpan( i * 12 + 4 ), length );
            BinaryPrimitives.WriteInt32LittleEndian( idx.AsSpan( i * 12 + 8 ), extra );
        }

        File.WriteAllBytes( _idxPath, idx );
    }
}
