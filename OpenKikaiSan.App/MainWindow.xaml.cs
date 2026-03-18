using System.Windows;
using Wpf.Ui.Appearance;

namespace OpenKikaiSan.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
    }
}
