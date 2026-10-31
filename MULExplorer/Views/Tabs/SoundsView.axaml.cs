using Avalonia.Controls;
using Avalonia.Input;
using MULExplorer.ViewModels.Tabs;

namespace MULExplorer.Views.Tabs;

public partial class SoundsView : UserControl
{
    public SoundsView()
    {
        InitializeComponent();
    }

    /// <summary>Double-click plays, like UOFiddler.</summary>
    private void OnDoubleTapped( object? sender, TappedEventArgs e )
    {
        if ( DataContext is SoundsViewModel vm && vm.PlayCommand.CanExecute( null ) )
        {
            vm.PlayCommand.Execute( null );
        }
    }
}
