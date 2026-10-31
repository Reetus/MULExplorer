using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MULExplorer.Services;

/// <summary>Plays a complete in-memory WAV file. Implementations must not block the caller.</summary>
public interface ISoundPlayer
{
    /// <summary>False when this platform has no way to play audio; the UI disables playback.</summary>
    bool IsAvailable { get; }

    /// <summary>Stops anything playing, then starts <paramref name="wav" /> asynchronously.</summary>
    void Play( byte[] wav );

    void Stop();
}

/// <summary>
///     Dependency-free, AOT-safe playback: <c>winmm!PlaySound</c> (<c>SND_MEMORY | SND_ASYNC</c>) on
///     Windows; elsewhere a temp .wav handed to <c>paplay</c>, <c>aplay</c> or <c>afplay</c> if one is on
///     the PATH; otherwise <see cref="IsAvailable" /> is false.
/// </summary>
public static class SoundPlayer
{
    public static ISoundPlayer CreateDefault()
    {
        if ( OperatingSystem.IsWindows() )
        {
            return new WinMMSoundPlayer();
        }

        foreach ( var tool in ( ReadOnlySpan<string> )["paplay", "aplay", "afplay"] )
        {
            if ( FindOnPath( tool ) is { } path )
            {
                return new ProcessSoundPlayer( path );
            }
        }

        return new NullSoundPlayer();
    }

    private static string? FindOnPath( string tool )
    {
        var paths = Environment.GetEnvironmentVariable( "PATH" )?.Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries ) ?? [];

        foreach ( var dir in paths )
        {
            var candidate = Path.Combine( dir, tool );

            if ( File.Exists( candidate ) )
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>No audio backend: playback is disabled.</summary>
public sealed class NullSoundPlayer : ISoundPlayer
{
    public bool IsAvailable => false;

    public void Play( byte[] wav )
    {
    }

    public void Stop()
    {
    }
}

/// <summary>Windows: PlaySound from unmanaged memory, which must stay valid until the sound stops.</summary>
internal sealed unsafe partial class WinMMSoundPlayer : ISoundPlayer
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private readonly Lock _lock = new();
    private void* _buffer;

    public bool IsAvailable => true;

    public void Play( byte[] wav )
    {
        lock ( _lock )
        {
            StopLocked();
            _buffer = NativeMemory.Alloc( ( nuint )wav.Length );
            wav.CopyTo( new Span<byte>( _buffer, wav.Length ) );
            PlaySound( ( nint )_buffer, 0, SndMemory | SndAsync | SndNoDefault );
        }
    }

    public void Stop()
    {
        lock ( _lock )
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        // A null sound stops playback synchronously, after which the buffer can be freed.
        PlaySound( 0, 0, 0 );

        if ( _buffer is not null )
        {
            NativeMemory.Free( _buffer );
            _buffer = null;
        }
    }

    [LibraryImport( "winmm.dll", EntryPoint = "PlaySoundW" )]
    [return: MarshalAs( UnmanagedType.Bool )]
    private static partial bool PlaySound( nint sound, nint module, uint flags );
}

/// <summary>Linux/macOS: writes a temp .wav and runs a command-line player on it.</summary>
internal sealed class ProcessSoundPlayer( string playerPath ) : ISoundPlayer
{
    private readonly Lock _lock = new();
    private readonly string _tempFile = Path.Combine( Path.GetTempPath(), $"mulexplorer-sound-{Environment.ProcessId}.wav" );
    private Process? _process;

    public bool IsAvailable => true;

    public void Play( byte[] wav )
    {
        lock ( _lock )
        {
            StopLocked();
            File.WriteAllBytes( _tempFile, wav );

            var info = new ProcessStartInfo( playerPath ) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add( _tempFile );

            try
            {
                _process = Process.Start( info );
            }
            catch ( System.ComponentModel.Win32Exception )
            {
                _process = null;
            }
        }
    }

    public void Stop()
    {
        lock ( _lock )
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        if ( _process is null )
        {
            return;
        }

        try
        {
            if ( !_process.HasExited )
            {
                _process.Kill();
                _process.WaitForExit( 1000 );
            }
        }
        catch ( InvalidOperationException )
        {
        }

        _process.Dispose();
        _process = null;
    }
}
