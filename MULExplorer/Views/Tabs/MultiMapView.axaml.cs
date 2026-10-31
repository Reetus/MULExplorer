using Avalonia.Controls;
using MULExplorer.ViewModels.Tabs;

namespace MULExplorer.Views.Tabs;

public partial class MultiMapView : UserControl
{
    public MultiMapView()
    {
        InitializeComponent();

        // "Fit" needs the drawing-area size, which only the view knows.
        Canvas.SizeChanged += ( _, e ) =>
        {
            if ( DataContext is MultiMapViewModel vm )
            {
                vm.ViewportSize = e.NewSize;
            }
        };
    }
}
