using System.ComponentModel;
using System.Windows;
using AppScale.UI.ViewModels;
using Microsoft.Win32;

namespace AppScale.UI;

public partial class MainWindow : Window, IUiServices
{
    /// <summary>When false, closing the window hides it to the tray instead.</summary>
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            ((App)Application.Current).ShowTrayHintOnce();
        }
        base.OnClosing(e);
    }

    public string? PickExecutable()
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose an executable to launch at a scale" };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    public string? PickExportPath()
    {
        var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "appscale-settings.json", Title = "Export AppScale settings and profiles" };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        AutoConfirm is bool auto ? auto :
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Self-test only: answer confirmations without a dialog.</summary>
    public bool? AutoConfirm { get; set; }
}
