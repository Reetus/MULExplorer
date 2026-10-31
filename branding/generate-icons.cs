#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.Linux.NoDependencies@3.119.4

// Draws the MUL Explorer icon (an original design: three stacked isometric tiles, for the
// layered data files and UO's diamond land tiles) and writes every platform size.
//
//   dotnet run branding/generate-icons.cs
//
// Source: branding/mulexplorer-logo-original.png if present (any square-ish PNG, ideally
// 1024+ px with transparent corners); otherwise the placeholder drawn below.
//
// Outputs (paths relative to the repository root):
//   MULExplorer/Assets/mulexplorer.ico        Windows executable icon (16-256 px)
//   MULExplorer/Assets/mulexplorer-256.png    window/taskbar icon used at runtime
//   branding/linux/hicolor/NxN/apps/mulexplorer.png   freedesktop icon theme sizes
//   branding/mulexplorer-1024.png             master

using SkiaSharp;

var root = FindRepositoryRoot();

// A designed logo wins when present; otherwise draw the built-in placeholder.
var logoPath = Path.Combine( root, "branding", "mulexplorer-logo-original.png" );
using var master = File.Exists( logoPath ) ? LoadLogo( logoPath, 1024 ) : DrawMaster( 1024 );
Console.WriteLine( File.Exists( logoPath ) ? $"Using {logoPath}" : "No branding/mulexplorer-logo-original.png; drawing the placeholder." );

Write( Path.Combine( root, "branding", "mulexplorer-1024.png" ), EncodePng( master.Copy() ) );

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
Write( Path.Combine( root, "MULExplorer", "Assets", "mulexplorer.ico" ), BuildIco( icoSizes.Select( s => ( s, EncodePng( Resize( master, s ) ) ) ) ) );
Write( Path.Combine( root, "MULExplorer", "Assets", "mulexplorer-256.png" ), EncodePng( Resize( master, 256 ) ) );

foreach ( var size in new[] { 16, 24, 32, 48, 64, 128, 256, 512 } )
{
    Write( Path.Combine( root, "branding", "linux", "hicolor", $"{size}x{size}", "apps", "mulexplorer.png" ), EncodePng( Resize( master, size ) ) );
}

Console.WriteLine( "Icons generated." );

/// <summary>Centre-crops the logo to a square and scales it to <paramref name="size" />.</summary>
static SKBitmap LoadLogo( string path, int size )
{
    using var source = SKBitmap.Decode( path ) ?? throw new InvalidOperationException( $"Cannot decode {path}" );
    var side = Math.Min( source.Width, source.Height );
    var crop = SKRectI.Create( ( source.Width - side ) / 2, ( source.Height - side ) / 2, side, side );
    var result = new SKBitmap( size, size, SKColorType.Rgba8888, SKAlphaType.Premul );
    using var canvas = new SKCanvas( result );
    canvas.Clear( SKColors.Transparent );
    using var image = SKImage.FromBitmap( source );
    canvas.DrawImage( image, crop, new SKRect( 0, 0, size, size ), new SKSamplingOptions( SKCubicResampler.Mitchell ) );
    return result;
}

static SKBitmap DrawMaster( int size )
{
    var bitmap = new SKBitmap( size, size, SKColorType.Rgba8888, SKAlphaType.Premul );
    using var canvas = new SKCanvas( bitmap );
    canvas.Clear( SKColors.Transparent );

    float s = size;
    var margin = s * 0.04f;
    var tileRect = new SKRect( margin, margin, s - margin, s - margin );

    // Background tile: deep slate with a soft vertical gradient.
    using ( var bg = new SKPaint { IsAntialias = true } )
    {
        bg.Shader = SKShader.CreateLinearGradient( new SKPoint( 0, tileRect.Top ), new SKPoint( 0, tileRect.Bottom ),
            [new SKColor( 0x2E, 0x34, 0x40 ), new SKColor( 0x16, 0x19, 0x20 )], SKShaderTileMode.Clamp );
        canvas.DrawRoundRect( tileRect, s * 0.2f, s * 0.2f, bg );
    }

    // Three isometric diamonds stacked bottom to top: teal, blue, gold.
    var cx = s / 2;
    var halfW = s * 0.30f;
    var halfH = halfW / 2;
    var thickness = s * 0.07f;
    var step = s * 0.15f;
    var baseY = s * 0.66f;

    (SKColor Top, SKColor Left, SKColor Right)[] layers =
    [
        ( new SKColor( 0x3F, 0xB8, 0xA8 ), new SKColor( 0x2A, 0x86, 0x7B ), new SKColor( 0x1F, 0x66, 0x5E ) ),
        ( new SKColor( 0x5B, 0x8D, 0xEF ), new SKColor( 0x3E, 0x67, 0xB8 ), new SKColor( 0x2F, 0x4F, 0x8E ) ),
        ( new SKColor( 0xF2, 0xC1, 0x4E ), new SKColor( 0xC4, 0x93, 0x2C ), new SKColor( 0x9C, 0x72, 0x1F ) )
    ];

    for ( var i = 0; i < layers.Length; i++ )
    {
        var cy = baseY - i * step;
        DrawSlab( canvas, cx, cy, halfW, halfH, thickness, layers[i] );
    }

    return bitmap;
}

static void DrawSlab( SKCanvas canvas, float cx, float cy, float halfW, float halfH, float thickness, (SKColor Top, SKColor Left, SKColor Right) colors )
{
    var top = new SKPoint( cx, cy - halfH );
    var right = new SKPoint( cx + halfW, cy );
    var bottom = new SKPoint( cx, cy + halfH );
    var left = new SKPoint( cx - halfW, cy );
    var down = new SKPoint( 0, thickness );

    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

    paint.Color = colors.Left;
    canvas.DrawPath( Polygon( left, bottom, bottom + down, left + down ), paint );
    paint.Color = colors.Right;
    canvas.DrawPath( Polygon( bottom, right, right + down, bottom + down ), paint );
    paint.Color = colors.Top;
    canvas.DrawPath( Polygon( top, right, bottom, left ), paint );

    // A thin dark outline keeps the layers separate at 16-32 px.
    using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = thickness * 0.18f, Color = new SKColor( 0x10, 0x12, 0x18, 0xC0 ), StrokeJoin = SKStrokeJoin.Round };
    canvas.DrawPath( Polygon( top, right, right + down, bottom + down, left + down, left ), outline );
    canvas.DrawPath( Polyline( left, bottom, right ), outline );
}

static SKPath Polygon( params SKPoint[] points ) => BuildPath( true, points );

static SKPath Polyline( params SKPoint[] points ) => BuildPath( false, points );

static SKPath BuildPath( bool close, SKPoint[] points )
{
    var path = new SKPath();
    path.AddPoly( points, close );
    return path;
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo( Environment.CurrentDirectory );

    while ( directory is not null && !File.Exists( Path.Combine( directory.FullName, "MULExplorer.slnx" ) ) )
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException( "Run from inside the MULExplorer repository." );
}

static SKBitmap Resize( SKBitmap source, int size )
{
    // Halve repeatedly so small sizes average every source pixel instead of aliasing.
    var current = source.Copy();

    while ( current.Width / 2 >= size )
    {
        var half = current.Resize( new SKImageInfo( current.Width / 2, current.Height / 2, SKColorType.Rgba8888, SKAlphaType.Premul ),
            new SKSamplingOptions( SKFilterMode.Linear, SKMipmapMode.None ) );
        current.Dispose();
        current = half;
    }

    var final = current.Resize( new SKImageInfo( size, size, SKColorType.Rgba8888, SKAlphaType.Premul ), new SKSamplingOptions( SKCubicResampler.Mitchell ) );
    current.Dispose();
    return final;
}

static byte[] EncodePng( SKBitmap bitmap )
{
    using ( bitmap )
    using ( var image = SKImage.FromBitmap( bitmap ) )
    using ( var data = image.Encode( SKEncodedImageFormat.Png, 100 ) )
    {
        return data.ToArray();
    }
}

static byte[] BuildIco( IEnumerable<(int Size, byte[] Png)> images )
{
    var entries = images.ToList();
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter( stream );
    writer.Write( ( ushort )0 );
    writer.Write( ( ushort )1 );
    writer.Write( ( ushort )entries.Count );
    var offset = 6 + 16 * entries.Count;

    foreach ( var (size, png) in entries )
    {
        writer.Write( ( byte )( size >= 256 ? 0 : size ) );
        writer.Write( ( byte )( size >= 256 ? 0 : size ) );
        writer.Write( ( byte )0 );
        writer.Write( ( byte )0 );
        writer.Write( ( ushort )1 );
        writer.Write( ( ushort )32 );
        writer.Write( png.Length );
        writer.Write( offset );
        offset += png.Length;
    }

    foreach ( var (_, png) in entries )
    {
        writer.Write( png );
    }

    return stream.ToArray();
}

static void Write( string path, byte[] bytes )
{
    Directory.CreateDirectory( Path.GetDirectoryName( path )! );
    File.WriteAllBytes( path, bytes );
    Console.WriteLine( $"  {Path.GetRelativePath( Environment.CurrentDirectory, path )} ({bytes.Length:N0} bytes)" );
}
