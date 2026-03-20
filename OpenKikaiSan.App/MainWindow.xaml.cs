using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace OpenKikaiSan.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        ApplicationAccentColorManager.Apply(
            Color.FromRgb(0x68, 0xBE, 0x8D),
            ApplicationTheme.Light,
            false,
            false
        );
    }
}
