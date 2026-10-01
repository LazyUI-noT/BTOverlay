using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BToverlay;

internal sealed class ColorPaletteWindow : Window
{
    static readonly string[] Colors =
    [
        "#FFFFFF", "#D9E3F0", "#A5B8CC", "#687B91", "#303B4C", "#121722", "#000000", "#8B8B8B",
        "#FFCDD2", "#EF5350", "#C62828", "#FF8A65", "#FFCA28", "#FFD700", "#FFF59D", "#F5F5DC",
        "#C5E1A5", "#76C442", "#32CD32", "#00897B", "#80DEEA", "#00D4FF", "#42A5F5", "#1565C0",
        "#BBDEFB", "#9370DB", "#7E57C2", "#CE93D8", "#EC407A", "#8D6E63", "#B88D35", "#FF6600"
    ];

    readonly TextBox _hexBox;
    readonly Border _preview;
    public string SelectedHex { get; private set; }

    public ColorPaletteWindow(string initial)
    {
        SelectedHex = initial;
        Title = "색상 팔레트";
        Width = 380;
        Height = 295;
        MinWidth = 380;
        MinHeight = 295;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 23, 35));
        Foreground = Brushes.White;

        var stack = new StackPanel { Margin = new Thickness(15) };
        stack.Children.Add(new TextBlock { Text = "색상을 선택하거나 색상 코드를 입력하세요.", Margin = new Thickness(0, 0, 0, 10) });
        var swatches = new UniformGrid { Rows = 4, Columns = 8 };
        foreach (var hex in Colors)
        {
            var button = new Button
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Height = 30,
                ToolTip = hex
            };
            button.Click += (_, _) => { SelectedHex = hex; DialogResult = true; };
            swatches.Children.Add(button);
        }
        stack.Children.Add(swatches);

        var custom = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        custom.Children.Add(new TextBlock { Text = "색상 코드", Width = 72, VerticalAlignment = VerticalAlignment.Center });
        _hexBox = new TextBox { Text = initial, Width = 112, VerticalContentAlignment = VerticalAlignment.Center };
        custom.Children.Add(_hexBox);
        _preview = new Border { Width = 28, Height = 25, BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 0, 0) };
        custom.Children.Add(_preview);
        _hexBox.TextChanged += (_, _) => UpdatePreview();
        var apply = new Button { Content = "선택", Width = 62, Margin = new Thickness(15, 0, 0, 0) };
        apply.Click += (_, _) =>
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(_hexBox.Text.Trim());
                SelectedHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                DialogResult = true;
            }
            catch { MessageBox.Show(this, "#RRGGBB 형식의 색상 코드를 입력하세요.", "색상 확인"); }
        };
        custom.Children.Add(apply);
        stack.Children.Add(custom);
        Content = stack;
        UpdatePreview();
    }

    void UpdatePreview()
    {
        try { _preview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_hexBox.Text.Trim())); }
        catch { _preview.Background = Brushes.Transparent; }
    }
}
