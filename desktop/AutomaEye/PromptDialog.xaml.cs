using System.Windows;

namespace AutomaEye;

/// <summary>Small two-field modal used for "New project" / "Add model" - the second field is optional (leave label null to hide it).</summary>
public partial class PromptDialog : Window
{
    public string? Value1 { get; private set; }
    public string? Value2 { get; set; }

    public PromptDialog(string title, string label1, string? label2 = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        Label1Text.Text = label1;

        if (label2 == null)
        {
            Label2Text.Visibility = Visibility.Collapsed;
            Input2.Visibility = Visibility.Collapsed;
        }
        else
        {
            Label2Text.Text = label2;
        }

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrEmpty(Value2)) Input2.Text = Value2;
            Input1.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value1 = Input1.Text;
        Value2 = Input2.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
