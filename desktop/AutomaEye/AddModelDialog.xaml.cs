using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AutomaEye.Models;

namespace AutomaEye;

/// <summary>
/// "Add model" flow: pick the main AI type (Detection/Classification/
/// Segmentation/OCR), then optionally layer rule-based add-ons on top -
/// mirrors the Keyence-style model wizard from the reference app.
/// </summary>
public partial class AddModelDialog : System.Windows.Window
{
    public string ModelName { get; private set; } = "";
    public List<string> Classes { get; private set; } = new();
    public AIType SelectedType { get; private set; } = AIType.Detection;
    public List<Addon> SelectedAddons { get; private set; } = new();

    private readonly List<Button> _typeButtons;

    public AddModelDialog()
    {
        InitializeComponent();
        _typeButtons = new List<Button> { TypeDetectionButton, TypeClassificationButton, TypeSegmentationButton, TypeOcrButton };
        HighlightType("Detection");
        Loaded += (_, _) => NameInput.Focus();
    }

    private void TypeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        HighlightType(tag);
    }

    private void HighlightType(string tag)
    {
        SelectedType = tag switch
        {
            "Classification" => AIType.Classification,
            "Segmentation" => AIType.Segmentation,
            "OCR" => AIType.OCR,
            _ => AIType.Detection,
        };
        foreach (var btn in _typeButtons)
        {
            bool selected = (string)btn.Tag == tag;
            btn.Background = selected ? (System.Windows.Media.Brush)FindResource("AccentBrush") : (System.Windows.Media.Brush)FindResource("PanelBrush");
            btn.Foreground = selected ? System.Windows.Media.Brushes.Black : (System.Windows.Media.Brush)FindResource("TextBrush");
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameInput.Text))
        {
            System.Windows.MessageBox.Show("Enter a model name.", "AutomaEye");
            return;
        }

        ModelName = NameInput.Text.Trim();
        Classes = ClassesInput.Text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (Classes.Count == 0) Classes = new List<string> { "OK", "NG" };

        SelectedAddons = new List<Addon>();
        if (AddonPresenceCheck.IsChecked == true) SelectedAddons.Add(Addon.PresenceCheck);
        if (AddonScratches.IsChecked == true) SelectedAddons.Add(Addon.Scratches);
        if (AddonGdt.IsChecked == true) SelectedAddons.Add(Addon.GdtMeasurement);
        if (AddonPositioning.IsChecked == true) SelectedAddons.Add(Addon.Positioning);
        if (AddonColorInspection.IsChecked == true) SelectedAddons.Add(Addon.ColorInspection);
        if (AddonCount.IsChecked == true) SelectedAddons.Add(Addon.Count);
        if (AddonCharacterRecognition.IsChecked == true) SelectedAddons.Add(Addon.CharacterRecognition);
        if (AddonCode1D.IsChecked == true) SelectedAddons.Add(Addon.Code1D);
        if (AddonCode2D.IsChecked == true) SelectedAddons.Add(Addon.Code2D);
        if (AddonCalibration.IsChecked == true) SelectedAddons.Add(Addon.Calibration);

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
