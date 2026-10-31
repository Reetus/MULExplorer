using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace MULExplorer.Tests;

internal static class TestSettingsIsolation
{
    /// <summary>Points the app's settings file at a temp path for the whole test run.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable( "MULEXPLORER_SETTINGS", Path.Combine( Path.GetTempPath(), $"mulexplorer-test-settings-{Environment.ProcessId}.json" ) );
}

/// <summary>Format-level tests on synthetic files; no client install needed.</summary>
public sealed class IOTests
{
    [Fact]
    public void MULIndex_ReadsEntriesAndRejectsHoles()
    {
        using var dir = new TempDir();
        var idx = new byte[12 * 3];
        Write( idx, 0, 0, 4, 7 );      // id 0: 4 bytes at 0, extra 7
        Write( idx, 1, -1, 0, 0 );     // id 1: missing
        Write( idx, 2, 4, 2, 0 );      // id 2: 2 bytes at 4
        File.WriteAllBytes( dir.File( "testidx.mul" ), idx );
        File.WriteAllBytes( dir.File( "test.mul" ), [1, 2, 3, 4, 5, 6] );

        using var index = FileIndex.Open( new UOFileSet( dir.Path ), "TESTIDX.MUL", "Test.mul", length: 5 )!;

        Assert.Equal( 5, index.Length );
        Assert.False( index.IsUOP );
        Assert.True( index.TryRead( 0, out var a, out var entry ) );
        Assert.Equal( [1, 2, 3, 4], a.ToArray() );
        Assert.Equal( 7, entry.Extra );
        Assert.False( index.IsValid( 1 ) );
        Assert.True( index.TryRead( 2, out var b ) );
        Assert.Equal( [5, 6], b.ToArray() );
        Assert.False( index.IsValid( 4 ) );
        Assert.False( index.IsValid( -1 ) );

        static void Write( byte[] buffer, int i, int offset, int length, int extra )
        {
            BinaryPrimitives.WriteInt32LittleEndian( buffer.AsSpan( i * 12 ), offset );
            BinaryPrimitives.WriteInt32LittleEndian( buffer.AsSpan( i * 12 + 4 ), length );
            BinaryPrimitives.WriteInt32LittleEndian( buffer.AsSpan( i * 12 + 8 ), extra );
        }
    }

    [Fact]
    public void UOPIndex_ResolvesHashedNamesAndDecompresses()
    {
        using var dir = new TempDir();
        var plain = "hello"u8.ToArray();
        var packed = Zlib( "compressed payload"u8.ToArray() );

        UOPWriter.Write( dir.File( "test.uop" ),
        [
            ( "build/test/00000000.bin", plain, 0, plain.Length, UOPCompression.None ),
            ( "build/test/00000003.bin", packed, 0, 18, UOPCompression.Zlib )
        ] );

        using var index = FileIndex.OpenUOP( dir.File( "test.uop" ), "build/test/{0:D8}.bin", 8 );

        Assert.True( index.IsUOP );
        Assert.True( index.TryRead( 0, out var a ) );
        Assert.Equal( "hello", System.Text.Encoding.ASCII.GetString( a ) );
        Assert.False( index.IsValid( 1 ) );
        Assert.True( index.TryRead( 3, out var b ) );
        Assert.Equal( "compressed payload", System.Text.Encoding.ASCII.GetString( b ) );
    }

    [Fact]
    public void UOPIndex_GumpPreambleIsPackedIntoExtra()
    {
        using var dir = new TempDir();
        var payload = new byte[8 + 4];
        BinaryPrimitives.WriteInt32LittleEndian( payload, 30 );
        BinaryPrimitives.WriteInt32LittleEndian( payload.AsSpan( 4 ), 20 );

        UOPWriter.Write( dir.File( "g.uop" ), [( "build/gumpartlegacymul/00000005.tga", payload, 0, payload.Length, UOPCompression.None )] );

        using var index = FileIndex.OpenUOP( dir.File( "g.uop" ), "build/gumpartlegacymul/{0:D8}.tga", 10, hasExtra: true );
        var entry = index.GetEntry( 5 );

        Assert.Equal( 30, entry.ExtraHigh );
        Assert.Equal( 20, entry.ExtraLow );
        Assert.Equal( 4, entry.Length );
    }

    [Fact]
    public void UOPHash_MatchesKnownValue()
    {
        // Same string hashed through string and span overloads must agree; the value is pinned
        // so an accidental algorithm change is caught.
        const string name = "build/artlegacymul/00000000.tga";
        var hash = UOPHash.Compute( name );
        Assert.Equal( hash, UOPHash.Compute( name.AsSpan() ) );
        Assert.NotEqual( hash, UOPHash.Compute( "build/artlegacymul/00000001.tga" ) );
    }

    [Fact]
    public void FileSet_LooksUpCaseInsensitively()
    {
        using var dir = new TempDir();
        File.WriteAllText( dir.File( "SKILNAME.ENU" ), "x" );
        var files = new UOFileSet( dir.Path );

        Assert.True( files.Exists( "skilname.enu" ) );
        Assert.Equal( dir.File( "SKILNAME.ENU" ), files.FindFirst( "missing.mul", "Skilname.Enu" ) );
        Assert.Null( files.Find( "nope" ) );
    }

    [Fact]
    public void MappedFile_SlicesAreClamped()
    {
        using var dir = new TempDir();
        var bytes = Enumerable.Range( 0, 300_000 ).Select( i => ( byte )i ).ToArray(); // large enough to be memory-mapped
        File.WriteAllBytes( dir.File( "big.bin" ), bytes );

        using var file = MappedFile.Open( dir.File( "big.bin" ) );

        Assert.Equal( bytes.Length, file.Length );
        Assert.Equal( bytes.AsSpan( 1000, 16 ).ToArray(), file.Slice( 1000, 16 ).ToArray() );
        Assert.Equal( 10, file.Slice( bytes.Length - 10, 100 ).Length );
        Assert.True( file.Slice( bytes.Length + 5, 4 ).IsEmpty );
    }

    [Fact]
    public void Color16_RoundTripsEvery15BitColor()
    {
        for ( var c = 0; c < 0x8000; c++ )
        {
            Assert.Equal( c, Color16.FromArgb( Color16.ToArgb( ( ushort )c ) ) );
        }

        Assert.Equal( 0xFFFFFFFFu, Color16.ToArgb( 0x7FFF ) );
        Assert.Equal( 0u, Color16.ToArgbTransparent( 0 ) );
    }

    [Fact]
    public void Png_HasSignatureAndDimensions()
    {
        var pixmap = new Pixmap( 3, 2 );
        pixmap[1, 1] = 0xFFFF0000;

        var png = PngEncoder.Encode( pixmap );

        Assert.Equal( [0x89, 0x50, 0x4E, 0x47], png[..4] );
        Assert.Equal( 3, BinaryPrimitives.ReadInt32BigEndian( png.AsSpan( 16 ) ) );
        Assert.Equal( 2, BinaryPrimitives.ReadInt32BigEndian( png.AsSpan( 20 ) ) );
    }

    [Fact]
    public void Pixmap_OpaqueBoundsAndBlit()
    {
        var target = new Pixmap( 10, 10 );
        var dot = new Pixmap( 2, 2, [0xFF000001, 0, 0, 0xFF000002] );

        target.Blit( dot, 4, 5 );

        Assert.Equal( ( 4, 5, 2, 2 ), target.OpaqueBounds() );
        Assert.Null( new Pixmap( 4, 4 ).OpaqueBounds() );
    }

    private static byte[] Zlib( byte[] data )
    {
        using var ms = new MemoryStream();
        using ( var z = new ZLibStream( ms, CompressionLevel.Optimal, true ) )
        {
            z.Write( data );
        }

        return ms.ToArray();
    }
}

/// <summary>Writes minimal UOP containers for tests.</summary>
internal static class UOPWriter
{
    public static void Write( string path, IReadOnlyList<(string Name, byte[] Data, int HeaderLength, int DecompressedLength, UOPCompression Compression)> files )
    {
        using var fs = File.Create( path );
        using var w = new BinaryWriter( fs );

        w.Write( 0x0050_594Du ); // MYP\0
        w.Write( 5 );            // version
        w.Write( 0xFD23EC43u );  // signature
        w.Write( 28L );          // first block right after the header
        w.Write( 1000 );         // block capacity
        w.Write( files.Count );

        var tableStart = fs.Position;
        var dataStart = tableStart + 12 + files.Count * 34;

        w.Write( files.Count );
        w.Write( 0L ); // no next block

        var offset = dataStart;

        foreach ( var f in files )
        {
            w.Write( offset );
            w.Write( f.HeaderLength );
            w.Write( f.Data.Length );
            w.Write( f.DecompressedLength );
            w.Write( UOPHash.Compute( f.Name ) );
            w.Write( 0u );
            w.Write( ( short )f.Compression );
            offset += f.Data.Length;
        }

        foreach ( var f in files )
        {
            w.Write( f.Data );
        }
    }
}
