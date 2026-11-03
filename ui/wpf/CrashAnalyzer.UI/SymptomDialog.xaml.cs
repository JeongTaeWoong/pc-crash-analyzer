using System.Windows;
using System.Windows.Controls;

namespace CrashAnalyzer.UI;

public sealed record SymptomAnswer(string Type, string Scope, string TargetProcess);

public partial class SymptomDialog : Window
{
    public SymptomDialog() => InitializeComponent();

    public static SymptomAnswer? Ask(Window owner, string presetTarget)
    {
        var dlg = new SymptomDialog { Owner = owner };
        dlg.txtTarget.Text = presetTarget;
        if (dlg.ShowDialog() != true) return null;
        return new SymptomAnswer(
            TagOf(dlg.cboType), TagOf(dlg.cboScope), dlg.txtTarget.Text.Trim());
    }

    private static string TagOf(ComboBox cbo) => (cbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "unknown";

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
