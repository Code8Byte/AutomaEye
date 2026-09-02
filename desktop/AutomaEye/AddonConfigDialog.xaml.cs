using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AutomaEye.Models;

namespace AutomaEye;

/// <summary>
/// "Aturan Inspeksi (Add-ons)" - configures the parameters for the two
/// addons that actually gate a verdict: Count (exact detection count) and
/// GD&amp;T Measurement (per-class shape + nominal/tolerance in mm). Mirrors
/// the reference app's model.html card of the same name, editable any time
/// after model creation (unlike the AI type/add-on selection itself, which
/// is fixed at creation in AddModelDialog).
/// </summary>
public partial class AddonConfigDialog : System.Windows.Window
{
    private readonly Model _model;
    private readonly System.Collections.Generic.Dictionary<string, (System.Windows.Controls.Primitives.ToggleButton circle, System.Windows.Controls.Primitives.ToggleButton rect, TextBox diaNom, TextBox diaTol, TextBox longNom, TextBox longTol, TextBox shortNom, TextBox shortTol)> _rows = new();

    public AddonConfigDialog(Model model)
    {
        InitializeComponent();
        _model = model;
        ModelNameText.Text = model.Name;

        bool hasCount = model.Addons.Contains(Addon.Count);
        bool hasGdt = model.Addons.Contains(Addon.GdtMeasurement);
        CountPanel.Visibility = hasCount ? Visibility.Visible : Visibility.Collapsed;
        GdtPanel.Visibility = hasGdt ? Visibility.Visible : Visibility.Collapsed;
        NoneText.Visibility = !hasCount && !hasGdt ? Visibility.Visible : Visibility.Collapsed;

        if (hasCount) CountExpectedInput.Text = model.AddonConfig.CountExpected?.ToString(CultureInfo.InvariantCulture) ?? "";
        if (hasGdt)
        {
            MmPerPixelInput.Text = model.AddonConfig.MmPerPixel?.ToString("F6", CultureInfo.InvariantCulture) ?? "";
            BuildGdtRows();
        }
    }

    private void BuildGdtRows()
    {
        var items = new System.Collections.Generic.List<UIElement>();
        foreach (var className in _model.Classes)
        {
            _model.AddonConfig.GdtPerClass.TryGetValue(className, out var cfg);
            cfg ??= new GdtClassConfig();

            var circle = new RadioButton { Content = "Lingkaran (Ø)", GroupName = "shape_" + className, IsChecked = cfg.Shape == GdtShape.Circle, Margin = new Thickness(0, 0, 12, 6) };
            var rect = new RadioButton { Content = "Persegi panjang (P x L)", GroupName = "shape_" + className, IsChecked = cfg.Shape == GdtShape.Rect, Margin = new Thickness(0, 0, 0, 6) };

            var diaNom = new TextBox { Width = 70, Text = cfg.NominalDiameterMm?.ToString(CultureInfo.InvariantCulture) ?? "" };
            var diaTol = new TextBox { Width = 60, Text = cfg.ToleranceDiameterMm?.ToString(CultureInfo.InvariantCulture) ?? "" };
            var longNom = new TextBox { Width = 70, Text = cfg.NominalLongMm?.ToString(CultureInfo.InvariantCulture) ?? "" };
            var longTol = new TextBox { Width = 60, Text = cfg.ToleranceLongMm?.ToString(CultureInfo.InvariantCulture) ?? "" };
            var shortNom = new TextBox { Width = 70, Text = cfg.NominalShortMm?.ToString(CultureInfo.InvariantCulture) ?? "" };
            var shortTol = new TextBox { Width = 60, Text = cfg.ToleranceShortMm?.ToString(CultureInfo.InvariantCulture) ?? "" };

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(new TextBlock { Text = className, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { circle, rect } });

            var diaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            diaRow.Children.Add(new TextBlock { Text = "Diameter nominal ± toleransi (mm):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Width = 220 });
            diaRow.Children.Add(diaNom);
            diaRow.Children.Add(new TextBlock { Text = " ± ", VerticalAlignment = VerticalAlignment.Center });
            diaRow.Children.Add(diaTol);
            panel.Children.Add(diaRow);

            var longRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            longRow.Children.Add(new TextBlock { Text = "Sisi panjang nominal ± toleransi (mm):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Width = 220 });
            longRow.Children.Add(longNom);
            longRow.Children.Add(new TextBlock { Text = " ± ", VerticalAlignment = VerticalAlignment.Center });
            longRow.Children.Add(longTol);
            panel.Children.Add(longRow);

            var shortRow = new StackPanel { Orientation = Orientation.Horizontal };
            shortRow.Children.Add(new TextBlock { Text = "Sisi pendek nominal ± toleransi (mm):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Width = 220 });
            shortRow.Children.Add(shortNom);
            shortRow.Children.Add(new TextBlock { Text = " ± ", VerticalAlignment = VerticalAlignment.Center });
            shortRow.Children.Add(shortTol);
            panel.Children.Add(shortRow);

            panel.Children.Add(new Border { BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush2"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 10, 0, 0) });

            items.Add(panel);
            _rows[className] = (circle, rect, diaNom, diaTol, longNom, longTol, shortNom, shortTol);
        }
        GdtClassList.ItemsSource = items;
    }

    private static double? ParseOrNull(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CountPanel.Visibility == Visibility.Visible)
        {
            _model.AddonConfig.CountExpected = string.IsNullOrWhiteSpace(CountExpectedInput.Text)
                ? null
                : int.TryParse(CountExpectedInput.Text, out var n) ? n : null;
        }

        if (GdtPanel.Visibility == Visibility.Visible)
        {
            _model.AddonConfig.MmPerPixel = ParseOrNull(MmPerPixelInput.Text);
            foreach (var (className, row) in _rows)
            {
                _model.AddonConfig.GdtPerClass[className] = new GdtClassConfig
                {
                    Shape = row.circle.IsChecked == true ? GdtShape.Circle : GdtShape.Rect,
                    NominalDiameterMm = ParseOrNull(row.diaNom.Text),
                    ToleranceDiameterMm = ParseOrNull(row.diaTol.Text),
                    NominalLongMm = ParseOrNull(row.longNom.Text),
                    ToleranceLongMm = ParseOrNull(row.longTol.Text),
                    NominalShortMm = ParseOrNull(row.shortNom.Text),
                    ToleranceShortMm = ParseOrNull(row.shortTol.Text),
                };
            }
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
