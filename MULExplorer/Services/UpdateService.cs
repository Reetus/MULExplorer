using Velopack;
using Velopack.Sources;

namespace MULExplorer.Services;

/// <summary>
///     Checks GitHub Releases for a newer MUL Explorer and downloads it in the background. A
///     downloaded update is applied by <see cref="ApplyAndRestart" /> or automatically the next
///     time the app starts. Only active in Velopack installs (Setup.exe or the AppImage).
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/Reetus/MULExplorer";

    private readonly UpdateManager _manager = new( new GithubSource( RepositoryUrl, null, false ) );
    private UpdateInfo? _pending;

    /// <summary>False when running from a build or publish folder rather than an install.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    /// <summary>Version downloaded and waiting for a restart, if any.</summary>
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>Downloads the latest release if it is newer. Returns its version, or null when up to date or not installed.</summary>
    public async Task<string?> DownloadLatestAsync( CancellationToken cancellationToken = default )
    {
        if ( !_manager.IsInstalled )
        {
            return null;
        }

        if ( _pending is not null )
        {
            return PendingVersion;
        }

        var update = await _manager.CheckForUpdatesAsync().ConfigureAwait( false );

        if ( update is null )
        {
            return null;
        }

        await _manager.DownloadUpdatesAsync( update, cancelToken: cancellationToken ).ConfigureAwait( false );
        _pending = update;
        return PendingVersion;
    }

    /// <summary>Exits, installs the downloaded update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if ( _pending is not null )
        {
            _manager.ApplyUpdatesAndRestart( _pending.TargetFullRelease );
        }
    }
}
