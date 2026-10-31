using UODataAccess.Loaders;

namespace UODataAccess.Imaging;

/// <summary>
///     Composites a multi's components isometrically from static art, like UOFiddler's
///     <c>MultiComponentList.GetImage</c>: a tile at (x, y, z) is drawn with its bottom centre at
///     <c>((x - y) * 22, (x + y) * 22 - z * 4)</c>.
/// </summary>
public static class MultiRenderer
{
    /// <summary>
    ///     Renders <paramref name="multi" />. Tiles are painted back to front: by diagonal (x + y), then by
    ///     z raised by the tiledata threshold (+1 for height &gt; 0, +1 for non-background), then by that
    ///     threshold, then in file order (UOFiddler's <c>MTile.CompareTo</c> and ClassicUO's priority z).
    /// </summary>
    /// <param name="multi">Components to draw.</param>
    /// <param name="art">Static art source.</param>
    /// <param name="tileData">Item heights/background flags for the draw order; optional.</param>
    /// <param name="maxHeight">Components above this z are skipped (UOFiddler's height slider). The canvas keeps its full size.</param>
    /// <param name="includeInvisible">Also draw components flagged invisible (UOFiddler draws them, the client does not).</param>
    /// <returns>The image, or null if no component has art.</returns>
    public static Pixmap? Render( MultiComponents multi, Art art, TileData? tileData, int maxHeight = int.MaxValue, bool includeInvisible = false )
    {
        var images = new Dictionary<ushort, Pixmap?>();
        var draws = new List<(int Order, int PriorityZ, int Threshold, int Index, Pixmap Image, int X, int Y)>( multi.Tiles.Length );
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for ( var i = 0; i < multi.Tiles.Length; i++ )
        {
            var t = multi.Tiles[i];

            if ( !includeInvisible && !t.IsVisible )
            {
                continue;
            }

            if ( !images.TryGetValue( t.ItemId, out var image ) )
            {
                images[t.ItemId] = image = art.GetItem( t.ItemId );
            }

            if ( image is null )
            {
                continue;
            }

            var cx = t.X - multi.MinX;
            var cy = t.Y - multi.MinY;
            var px = ( cx - cy ) * 22 - image.Width / 2;
            var py = ( cx + cy ) * 22 - t.Z * 4 - image.Height;

            minX = Math.Min( minX, px );
            minY = Math.Min( minY, py );
            maxX = Math.Max( maxX, px + image.Width );
            maxY = Math.Max( maxY, py + image.Height );

            if ( t.Z > maxHeight )
            {
                continue;
            }

            var threshold = 0;

            if ( tileData is not null )
            {
                var item = tileData.GetItem( t.ItemId );
                threshold = ( item.Height > 0 ? 1 : 0 ) + ( item.IsBackground ? 0 : 1 );
            }

            draws.Add( ( cx + cy, t.Z + threshold, threshold, i, image, px, py ) );
        }

        if ( maxX <= minX || maxY <= minY )
        {
            return null;
        }

        draws.Sort( static ( a, b ) =>
        {
            var c = a.Order.CompareTo( b.Order );
            c = c != 0 ? c : a.PriorityZ.CompareTo( b.PriorityZ );
            c = c != 0 ? c : a.Threshold.CompareTo( b.Threshold );
            return c != 0 ? c : a.Index.CompareTo( b.Index );
        } );

        var canvas = new Pixmap( maxX - minX, maxY - minY );

        foreach ( var d in draws )
        {
            canvas.Blit( d.Image, d.X - minX, d.Y - minY );
        }

        return canvas;
    }
}
