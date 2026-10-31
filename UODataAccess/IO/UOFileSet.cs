namespace UODataAccess.IO;

/// <summary>
///     The files in a UO client folder, looked up case-insensitively. Client installs mix
///     <c>SKILNAME.ENU</c>, <c>Cliloc.enu</c> and <c>tiledata.mul</c>, which matters on
///     Linux/WSL where the filesystem is case-sensitive.
/// </summary>
public sealed class UOFileSet
{
    private readonly Dictionary<string, string> _files = new( StringComparer.OrdinalIgnoreCase );

    public UOFileSet( string root )
    {
        if ( !Directory.Exists( root ) )
        {
            throw new DirectoryNotFoundException( $"UO folder not found: {root}" );
        }

        Root = Path.GetFullPath( root );

        foreach ( var path in Directory.EnumerateFiles( Root ) )
        {
            _files.TryAdd( Path.GetFileName( path ), path );
        }
    }

    public string Root { get; }

    public IEnumerable<string> FileNames => _files.Keys;

    /// <summary>Full path of <paramref name="fileName" />, or null if the client does not have it.</summary>
    public string? Find( string fileName ) => _files.GetValueOrDefault( fileName );

    public bool Exists( string fileName ) => _files.ContainsKey( fileName );

    /// <summary>First of <paramref name="fileNames" /> that exists.</summary>
    public string? FindFirst( params ReadOnlySpan<string> fileNames )
    {
        foreach ( var name in fileNames )
        {
            if ( Find( name ) is { } path )
            {
                return path;
            }
        }

        return null;
    }

    public MappedFile? Open( string fileName ) => Find( fileName ) is { } path ? MappedFile.Open( path ) : null;
}
