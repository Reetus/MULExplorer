using UODataAccess.IO;
using UODataAccess.Loaders;

namespace UODataAccess;

/// <summary>
///     Entry point to a UO client folder. Every loader is opened lazily on first access and then
///     shared, so the UI and the MCP server can use one instance from any thread. A loader whose
///     files are missing throws <see cref="FileNotFoundException" /> on access; use
///     <see cref="TryGet{T}" /> to probe.
/// </summary>
public sealed class UOData( string folder ) : IDisposable
{
    private readonly Lock _lock = new();

    public UOFileSet Files { get; } = new( folder );

    public string Folder => Files.Root;

    public TileData TileData => Get( ref field, static f => new TileData( f ) );

    public Hues Hues => Get( ref field, static f => new Hues( f ) );

    public Art Art => Get( ref field, static f => new Art( f ) );

    public Gumps Gumps => Get( ref field, static f => new Gumps( f ) );

    public Cliloc Cliloc => Get( ref field, static f => Cliloc.Load( f ) );

    public Multis Multis => Get( ref field, static f => new Multis( f ) );

    public Textures Textures => Get( ref field, static f => new Textures( f ) );

    public RadarColors RadarColors => Get( ref field, static f => new RadarColors( f ) );

    public Animations Animations => Get( ref field, static f => new Animations( f ) );

    public AnimData AnimData => Get( ref field, static f => new AnimData( f ) );

    public Sounds Sounds => Get( ref field, static f => new Sounds( f ) );

    public Fonts Fonts => Get( ref field, static f => new Fonts( f ) );

    public Speech Speech => Get( ref field, static f => new Speech( f ) );

    public Skills Skills => Get( ref field, static f => new Skills( f ) );

    public SkillGroups SkillGroups => Get( ref field, static f => new SkillGroups( f ) );

    public Lights Lights => Get( ref field, static f => new Lights( f ) );

    public Verdata Verdata => Get( ref field, static f => new Verdata( f ) );

    public Maps Maps => Get( ref field, static f => new Maps( f ) );

    public MultiMap MultiMap => Get( ref field, static f => new MultiMap( f ) );

    public void Dispose()
    {
        lock ( _lock )
        {
            foreach ( var disposable in _opened.OfType<IDisposable>() )
            {
                disposable.Dispose();
            }

            _opened.Clear();
        }
    }

    /// <summary>Runs <paramref name="access" />, returning null instead of throwing if the asset is missing or corrupt.</summary>
    public T? TryGet<T>( Func<UOData, T> access ) where T : class
    {
        try
        {
            return access( this );
        }
        catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException )
        {
            return null;
        }
    }

    private readonly List<object> _opened = [];

    private T Get<T>( ref T? slot, Func<UOFileSet, T> create ) where T : class
    {
        if ( Volatile.Read( ref slot ) is { } existing )
        {
            return existing;
        }

        lock ( _lock )
        {
            if ( slot is null )
            {
                var value = create( Files );
                _opened.Add( value );
                Volatile.Write( ref slot, value );
            }

            return slot;
        }
    }
}
