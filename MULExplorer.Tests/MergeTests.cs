using System.Buffers.Binary;
using System.IO.Compression;
using UODataAccess.Imaging;
using UODataAccess.IO;
using UODataAccess.Loaders;
using UODataAccess.Merge;

namespace MULExplorer.Tests;

/// <summary>Writers and client-merge rules, on synthetic files.</summary>
public sealed class MergeTests
{
    private const string GumpName = "build/gumpartlegacymul/{0:D8}.tga";

    [Fact]
    public void UOPArchiveWriter_RoundTripsAcrossBlocks()
    {
        using var dir = new TempDir();
        var packed = Zlib( "zlib payload"u8.ToArray() );

        using ( var writer = new UOPArchiveWriter( File.Create( dir.File( "a.uop" ) ), version: 4, blockCapacity: 2 ) )
        {
            for ( var i = 0; i < 4; i++ )
            {
                writer.Add( $"entry/{i}", [( byte )i, 1, 2] );
            }

            writer.Add( UOPHash.Compute( "entry/packed" ), [9, 9, 9], packed, 12, UOPCompression.Zlib );
            Assert.Equal( 5, writer.Count );
        }

        using var archive = UOPArchive.Open( dir.File( "a.uop" ) );
        Assert.Equal( 4, archive.Version );
        Assert.Equal( 2, archive.BlockCapacity );
        Assert.Equal( 5, archive.Count );

        for ( var i = 0; i < 4; i++ )
        {
            Assert.True( archive.TryGet( $"entry/{i}", out var e ) );
            Assert.Equal( [( byte )i, 1, 2], archive.Read( e ).ToArray() );
            Assert.Equal( UOPArchiveWriter.Adler32( [( byte )i, 1, 2] ), e.Checksum );
        }

        Assert.True( archive.TryGet( "entry/packed", out var p ) );
        Assert.Equal( [9, 9, 9], archive.ReadHeader( p ).ToArray() );
        Assert.Equal( "zlib payload"u8.ToArray(), archive.Read( p ).ToArray() );

        // Verbatim copy keeps header, compression and checksum.
        using ( var copy = UOPArchiveWriter.Create( dir.File( "b.uop" ), archive ) )
        {
            copy.CopyFrom( archive, p );
        }

        using var copied = UOPArchive.Open( dir.File( "b.uop" ) );
        Assert.True( copied.TryGet( "entry/packed", out var c ) );
        Assert.Equal( p.Checksum, c.Checksum );
        Assert.Equal( UOPCompression.Zlib, c.Compression );
        Assert.Equal( [9, 9, 9], copied.ReadHeader( c ).ToArray() );
        Assert.Equal( "zlib payload"u8.ToArray(), copied.Read( c ).ToArray() );
    }

    [Fact]
    public void Adler32_MatchesReference()
    {
        Assert.Equal( 0x11E60398u, UOPArchiveWriter.Adler32( "Wikipedia"u8 ) );

        // Long enough to cross the deferred-modulo chunk boundary.
        var big = Enumerable.Range( 0, 20000 ).Select( i => ( byte )0xFF ).ToArray();
        uint a = 1, b = 0;

        foreach ( var x in big )
        {
            a = ( a + x ) % 65521;
            b = ( b + a ) % 65521;
        }

        Assert.Equal( ( b << 16 ) | a, UOPArchiveWriter.Adler32( big ) );
    }

    [Fact]
    public void MULFileWriter_RoundTripsWithHoles()
    {
        using var dir = new TempDir();

        using ( var writer = new MULFileWriter( dir.File( "test.mul" ), dir.File( "testidx.mul" ) ) )
        {
            writer.Add( 0, [1, 2], 7 );
            writer.Add( 2, [3], 8 );
            writer.Pad( 4 );
        }

        using var index = FileIndex.OpenMUL( dir.File( "testidx.mul" ), dir.File( "test.mul" ) );
        Assert.Equal( 4, index.Length );
        Assert.True( index.TryRead( 0, out var a, out var ea ) );
        Assert.Equal( [1, 2], a.ToArray() );
        Assert.Equal( 7, ea.Extra );
        Assert.False( index.IsValid( 1 ) );
        Assert.True( index.TryRead( 2, out var b, out var eb ) );
        Assert.Equal( [3], b.ToArray() );
        Assert.Equal( 8, eb.Extra );
        Assert.False( index.IsValid( 3 ) );
    }

    [Fact]
    public void Cliloc_WriteRoundTrips()
    {
        using var dir = new TempDir();

        using ( var stream = File.Create( dir.File( "Cliloc.enu" ) ) )
        {
            Cliloc.Write( stream, [new ClilocEntry( 500001, "Ünïcode ~1_NAME~", 2 ), new ClilocEntry( 3000000, "", 0 ), new ClilocEntry( 1, "first", 0 )] );
        }

        var cliloc = Cliloc.Load( dir.File( "Cliloc.enu" ), "enu" );
        Assert.Equal( [1, 500001, 3000000], cliloc.Entries.Select( e => e.Number ) );
        Assert.Equal( "Ünïcode ~1_NAME~", cliloc.GetString( 500001 ) );
        Assert.Equal( 2, cliloc.Entries[1].Flag );
        Assert.Equal( "", cliloc.GetString( 3000000 ) );
    }

    [Fact]
    public void UOPMerger_KeepsCustomUpdatesStockAndAddsNew()
    {
        using var root = new TempDir();
        var local = Folder( root, "local" );
        var upstream = Folder( root, "upstream" );
        var stock = Folder( root, "stock" );

        const ushort Old = 0x1111, New = 0x2222, Custom = 0x3333;

        // 1: local has an older official image -> updated. 2: custom -> kept. 3: new upstream -> added.
        // 4: local-only -> kept. 5: same pixels, different run encoding -> unchanged.
        WriteGumps( stock, (1, Gump( Old )), (2, Gump( Old )) );
        WriteGumps( local, (1, Gump( Old )), (2, Gump( Custom )), (4, Gump( Custom )), (5, Gump( New )) );
        WriteGumps( upstream, (1, Gump( New )), (2, Gump( New )), (3, Gump( New )), (5, GumpSplitRuns( New )) );

        var output = Path.Combine( root.Path, "out", "gumpartLegacyMUL.uop" );
        var result = UOPMerger.Merge( UOPMergeSpec.Gumps, new UOFileSet( local ), new UOFileSet( upstream ), [new UOFileSet( stock )], output,
            TestContext.Current.CancellationToken );

        Assert.Equal( MergeAction.Updated, Action( result, 1 ) );
        Assert.Equal( MergeAction.KeptCustom, Action( result, 2 ) );
        Assert.Equal( MergeAction.Added, Action( result, 3 ) );
        Assert.Equal( MergeAction.KeptLocalOnly, Action( result, 4 ) );
        Assert.Equal( 1, result.Counts[MergeAction.Unchanged] );

        using var gumps = new Gumps( new UOFileSet( Path.GetDirectoryName( output )! ) );
        Assert.Equal( Color( New ), gumps.Get( 1 )![0, 0] );
        Assert.Equal( Color( Custom ), gumps.Get( 2 )![0, 0] );
        Assert.Equal( Color( New ), gumps.Get( 3 )![0, 0] );
        Assert.Equal( Color( Custom ), gumps.Get( 4 )![1, 0] );
        Assert.Equal( Color( New ), gumps.Get( 5 )![1, 0] );
    }

    [Fact]
    public void UOPMerger_StoresUpstreamEntriesInLocalCompression()
    {
        using var root = new TempDir();
        var local = Folder( root, "local" );
        var upstream = Folder( root, "upstream" );
        WriteGumps( local, (1, Gump( 0x1111 )) );
        WriteGumps( upstream, UOPCompression.Zlib, (1, Gump( 0x2222 )), (2, Gump( 0x3333 )) );

        var output = Path.Combine( root.Path, "out", "gumpartLegacyMUL.uop" );
        var result = UOPMerger.Merge( UOPMergeSpec.Gumps, new UOFileSet( local ), new UOFileSet( upstream ), [], output, TestContext.Current.CancellationToken );

        Assert.Equal( 1, result.Transcoded ); // id 2; id 1 is local's own entry

        using ( var archive = UOPArchive.Open( output ) )
        {
            Assert.All( archive.Entries.Values, e => Assert.Equal( UOPCompression.None, e.Compression ) );
        }

        using var gumps = new Gumps( new UOFileSet( Path.GetDirectoryName( output )! ) );
        Assert.Equal( Color( 0x1111 ), gumps.Get( 1 )![0, 0] ); // custom kept (no stock to say otherwise)
        Assert.Equal( Color( 0x3333 ), gumps.Get( 2 )![1, 0] );
    }

    [Fact]
    public void RecordFileMerger_IgnoresEmptyAndFillerSlots()
    {
        using var root = new TempDir();
        var local = Folder( root, "local" );
        var upstream = Folder( root, "upstream" );
        var stock = Folder( root, "stock" );
        const ushort Filler = 0x0421;

        ushort[] Colors() => Enumerable.Repeat( Filler, 100 ).ToArray();

        var l = Colors();
        var u = Colors();
        var s = Colors();
        l[1] = 0;      u[1] = 0x1234;                // empty local slot -> added
        u[2] = 0x2345;                               // local filler -> added
        l[3] = 0x7000; u[3] = 0x7001;                // custom -> kept
        l[4] = 0x6000; u[4] = 0x6001; s[4] = 0x6000; // stale stock -> updated
        l[5] = 0x5000;                               // upstream filler, local custom -> kept
        l[6] = 0x4000; s[6] = 0x4000;                // upstream filler, local stale stock -> updated

        WriteColors( local, l );
        WriteColors( upstream, u );
        WriteColors( stock, s );

        var output = Path.Combine( root.Path, "radarcol.mul" );
        var result = RecordFileMerger.Merge( RecordFileSpec.RadarColors, new UOFileSet( local ), new UOFileSet( upstream ), [new UOFileSet( stock )], output );

        Assert.Equal( MergeAction.Added, Action( result, 1 ) );
        Assert.Equal( MergeAction.Added, Action( result, 2 ) );
        Assert.Equal( MergeAction.KeptCustom, Action( result, 3 ) );
        Assert.Equal( MergeAction.Updated, Action( result, 4 ) );
        Assert.Equal( MergeAction.KeptLocalOnly, Action( result, 5 ) );
        Assert.Equal( MergeAction.Updated, Action( result, 6 ) );

        var merged = File.ReadAllBytes( output );
        ushort At( int i ) => BinaryPrimitives.ReadUInt16LittleEndian( merged.AsSpan( i * 2 ) );
        Assert.Equal( [0x1234, 0x2345, 0x7000, 0x6001, 0x5000, Filler], Enumerable.Range( 1, 6 ).Select( At ) );
    }

    [Fact]
    public void ClilocMerger_TreatsBlankStringsAsUnused()
    {
        using var root = new TempDir();
        var local = Folder( root, "local" );
        var upstream = Folder( root, "upstream" );
        var stock = Folder( root, "stock" );

        WriteCliloc( stock, (1, "old wording"), (2, "official") );
        WriteCliloc( local, (1, "old wording"), (2, "shard text"), (3, ""), (4, "same") );
        WriteCliloc( upstream, (1, "new wording"), (2, "official"), (3, "new item"), (4, "same"), (5, "brand new") );

        var output = Path.Combine( root.Path, "Cliloc.enu" );
        var result = ClilocMerger.Merge( "enu", new UOFileSet( local ), new UOFileSet( upstream ), [new UOFileSet( stock )], output );

        Assert.Equal( MergeAction.Updated, Action( result, 1 ) );
        Assert.Equal( MergeAction.KeptCustom, Action( result, 2 ) );
        Assert.Equal( MergeAction.Added, Action( result, 3 ) );
        Assert.Equal( MergeAction.Added, Action( result, 5 ) );

        var merged = Cliloc.Load( output, "enu" );
        Assert.Equal( ["new wording", "shard text", "new item", "same", "brand new"], merged.Entries.Select( e => e.Text ) );
    }

    private static MergeAction Action( AssetMergeResult result, int id ) =>
        result.Items.FirstOrDefault( i => i.Id == id ) is { Key: not null } item ? item.Action : MergeAction.Unchanged;

    private static string Folder( TempDir root, string name )
    {
        var path = Path.Combine( root.Path, name );
        Directory.CreateDirectory( path );
        return path;
    }

    private static uint Color( ushort c ) => Color16.ToArgbTransparent( c );

    /// <summary>A 2x1 gump: row table (1 word) then one (color, run 2) pair; payload has the UOP (w, h) preamble.</summary>
    private static byte[] Gump( ushort color ) => GumpRuns( [( color, 2 )] );

    /// <summary>The same 2x1 image encoded as two runs of 1.</summary>
    private static byte[] GumpSplitRuns( ushort color ) => GumpRuns( [( color, 1 ), ( color, 1 )] );

    private static byte[] GumpRuns( (ushort Color, ushort Run)[] runs )
    {
        var data = new byte[8 + 4 + runs.Length * 4];
        BinaryPrimitives.WriteInt32LittleEndian( data, 2 );
        BinaryPrimitives.WriteInt32LittleEndian( data.AsSpan( 4 ), 1 );
        BinaryPrimitives.WriteInt32LittleEndian( data.AsSpan( 8 ), 1 );

        for ( var i = 0; i < runs.Length; i++ )
        {
            BinaryPrimitives.WriteUInt16LittleEndian( data.AsSpan( 12 + i * 4 ), runs[i].Color );
            BinaryPrimitives.WriteUInt16LittleEndian( data.AsSpan( 14 + i * 4 ), runs[i].Run );
        }

        return data;
    }

    private static void WriteGumps( string folder, params (int Id, byte[] Data)[] gumps ) => WriteGumps( folder, UOPCompression.None, gumps );

    private static void WriteGumps( string folder, UOPCompression compression, params (int Id, byte[] Data)[] gumps )
    {
        using var writer = new UOPArchiveWriter( File.Create( Path.Combine( folder, "gumpartLegacyMUL.uop" ) ) );

        foreach ( var (id, data) in gumps )
        {
            var name = string.Format( System.Globalization.CultureInfo.InvariantCulture, GumpName, id );
            writer.Add( name, compression == UOPCompression.Zlib ? Zlib( data ) : data, compression, data.Length );
        }
    }

    private static void WriteColors( string folder, ushort[] colors )
    {
        var bytes = new byte[colors.Length * 2];

        for ( var i = 0; i < colors.Length; i++ )
        {
            BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( i * 2 ), colors[i] );
        }

        File.WriteAllBytes( Path.Combine( folder, "radarcol.mul" ), bytes );
    }

    private static void WriteCliloc( string folder, params (int Number, string Text)[] entries )
    {
        using var stream = File.Create( Path.Combine( folder, "Cliloc.enu" ) );
        Cliloc.Write( stream, entries.Select( e => new ClilocEntry( e.Number, e.Text, 0 ) ) );
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
