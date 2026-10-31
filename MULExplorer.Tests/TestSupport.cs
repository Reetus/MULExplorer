using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using MULExplorer.Services;
using MULExplorer.Tests;
using UODataAccess;

[assembly: AvaloniaTestApplication( typeof( TestAppBuilder ) )]

namespace MULExplorer.Tests;

/// <summary>Runs the real <see cref="App" /> (real styles and theme) on the headless platform with Skia, so screenshots render.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless( new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false } );
}

/// <summary>
///     Locates a real UO client for data-driven tests: <c>UO_DATA_FOLDER</c>, else the folder saved
///     in the user's MULExplorer settings. Tests call <see cref="Require" />, which skips when none is found.
/// </summary>
public static class UOTestData
{
    private static readonly Lazy<UOData?> Shared = new( () => Folder is { } f ? new UOData( f ) : null );

    /// <summary>`UO_DATA_FOLDER=none` forces the no-client path (what CI runners see) even when settings name a client.</summary>
    public static string? Folder { get; } = Environment.GetEnvironmentVariable( "UO_DATA_FOLDER" ) == "none"
        ? null
        : Settings.Load( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "MULExplorer", "settings.json" ) ).ResolveUOFolder( null );

    /// <summary>A process-wide shared instance (loaders are thread-safe and read-only).</summary>
    public static UOData Require()
    {
        return Shared.Value ?? throw SkipException();

        static Exception SkipException()
        {
            Assert.Skip( "No UO client found. Set UO_DATA_FOLDER to a client folder to run this test." );
            return new UnreachableException();
        }
    }

    public static string RequireFolder() => Require().Folder;

    /// <summary>Settings pointing at a temp file so tests never write the user's real settings.</summary>
    public static Settings IsolatedSettings( string? folder = null ) =>
        new() { UOFolder = folder, McpAutoStart = false, Theme = "Dark" };
}

/// <summary>A temp directory deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine( System.IO.Path.GetTempPath(), "mulexplorer-tests-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( Path );
    }

    public string Path { get; }

    public string File( string name ) => System.IO.Path.Combine( Path, name );

    public void Dispose()
    {
        try
        {
            Directory.Delete( Path, true );
        }
        catch ( IOException )
        {
        }
    }
}
