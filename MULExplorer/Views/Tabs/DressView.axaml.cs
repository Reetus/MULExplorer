using Avalonia.Controls;
using Avalonia.Input;
using MULExplorer.ViewModels.Tabs;

namespace MULExplorer.Views.Tabs;

public partial class DressView : UserControl
{
    public DressView()
    {
        InitializeComponent();
    }

    private void OnItemDoubleTapped( object? sender, TappedEventArgs e )
    {
        if ( DataContext is DressViewModel vm )
        {
            vm.DressSelectedCommand.Execute( null );
        }
    }
}
