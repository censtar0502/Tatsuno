using System.ComponentModel;
using System.Windows;
using Tatsuno.WpfApp.ViewModels;

namespace Tatsuno.WpfApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.OnWindowClosing();

        base.OnClosing(e);
    }
}
