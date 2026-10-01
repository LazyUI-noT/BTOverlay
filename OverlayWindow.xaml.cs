using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace BToverlay;

public partial class OverlayWindow : Window
{
    readonly Board _board;
    readonly UserSettings _settings;
    readonly DispatcherTimer _markerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    ImageSource? _itemImage;
    public bool CaptureExclusionApplied { get; private set; } = true;
    public OverlayWindow(Board board, UserSettings settings)
    {
        InitializeComponent(); _board = board; _settings = settings;
        _board.Changed += Draw;
        _markerTimer.Tick += (_, _) => _board.ExpireOffTurnMarkers();
        _markerTimer.Start();
        Closed += (_, _) => { _markerTimer.Stop(); _board.Changed -= Draw; };
        SourceInitialized += (_, _) => ApplyCapture();
        Draw(); ApplyStyle();
    }
    public void ApplyStyle()
    {
        _itemImage = LoadItemImage(_settings.ItemImage);
        Left = _settings.OverlayX; Top = _settings.OverlayY;
        Root.LayoutTransform = new ScaleTransform(Math.Clamp(_settings.OverlayScale, .4, 3), Math.Clamp(_settings.OverlayScale, .4, 3));
        Opacity = Math.Clamp(_settings.OverlayOpacity, .1, 1);
        Visibility = _settings.ShowOverlay ? Visibility.Visible : Visibility.Hidden;
        ApplyCapture(); Draw();
    }
    void ApplyCapture()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, -20);
        SetWindowLongPtr(hwnd, -20, style | (nint)0x20 | (nint)0x08000000 | (nint)0x80);
        CaptureExclusionApplied = SetWindowDisplayAffinity(hwnd, _settings.HideFromCapture ? 0x11u : 0u);
    }
    void Draw()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Draw); return; }
        PartiesPanel.Children.Clear();
        if (_settings.LayoutStyle == 2) DrawCompact();
        else DrawIcons();
    }
    void DrawIcons()
    {
        for (var party = 0; party < 2; party++)
        {
            var strip = new StackPanel();
            strip.Children.Add(new TextBlock { Text = $"{party + 1}파티", Foreground = Brushes.White,
                FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 0, 4, 4) });
            var tiles = new StackPanel { Orientation = Orientation.Horizontal };
            for (var pos = party * 4; pos < party * 4 + 4; pos++)
            {
                var slot = _board.Order[pos];
                if (!_board.Included[slot]) continue;
                var size = _settings.EnlargeCurrentImage && slot == _board.Turn ? 58 : 46;
                var width = Math.Max(54, size + 8);
                var column = new StackPanel { Width = width, Margin = new Thickness(2, 0, 2, 0) };
                column.Children.Add(new TextBlock { Text = slot == _board.StartSlot ? "▼ 시작" : "", Height = 12,
                    Foreground = Brushes.LawnGreen, FontSize = 8, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
                column.Children.Add(CreateIconTile(slot, size));
                column.Children.Add(new TextBlock { Text = $"{slot + 1}. {_board.Names[slot]}", Foreground = Brushes.White,
                    FontSize = 9, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Width = width });
                tiles.Children.Add(column);
            }
            strip.Children.Add(tiles);
            PartiesPanel.Children.Add(PartyBox(party, strip));
        }
    }
    void DrawCompact()
    {
        var own = new StackPanel { Margin = new Thickness(2, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };
        own.Children.Add(CreateIconTile(_board.MySlot, _settings.EnlargeCurrentImage && _board.MySlot == _board.Turn ? 70 : 56));
        own.Children.Add(new TextBlock { Text = "나", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center });
        PartiesPanel.Children.Add(own);
        var groups = new StackPanel();
        for (var party = 0; party < 2; party++)
        {
            var tiles = new StackPanel { Orientation = Orientation.Horizontal };
            for (var pos = party * 4; pos < party * 4 + 4; pos++)
            {
                var slot = _board.Order[pos];
                if (!_board.Included[slot]) continue;
                tiles.Children.Add(CreateStatusTile(slot));
            }
            groups.Children.Add(PartyBox(party, tiles));
        }
        PartiesPanel.Children.Add(groups);
    }
    Border PartyBox(int party, UIElement content)
    {
        var colorText = party == 0 ? _settings.Party1Color : _settings.Party2Color;
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(colorText); }
        catch { color = party == 0 ? Colors.LimeGreen : Colors.MediumPurple; }
        return new Border { Child = content, BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B)),
            Padding = new Thickness(4), Margin = new Thickness(3) };
    }
    Border CreateIconTile(int slot, int size)
    {
        var current = slot == _board.Turn;
        var mine = slot == _board.MySlot;
        var marked = _board.HasOffTurnMarker(slot);
        var currentBrush = OutlineBrush(_settings.CurrentOutlineColor, Brushes.Gold);
        var myBrush = OutlineBrush(_settings.MyOutlineColor, Brushes.DeepSkyBlue);
        var tile = new Border { Width = size, Height = size, BorderThickness = new Thickness(current || mine || marked ? 2 : 1),
            BorderBrush = current ? currentBrush : mine ? myBrush : marked ? Brushes.Gold : Brushes.SlateGray, Background = Brushes.Black,
            CornerRadius = new CornerRadius(2) };
        var grid = new Grid();
        if (_itemImage is not null)
            grid.Children.Add(new Image { Source = _itemImage, Stretch = Stretch.UniformToFill,
                Opacity = _board.Counts[slot] == 0 ? .28 : 1 });
        else grid.Children.Add(new TextBlock { Text = "◆", Foreground = _board.Counts[slot] == 0 ? Brushes.Gray : Brushes.DodgerBlue,
            FontSize = size * .68, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(2, 0, 2, 0), Child = new TextBlock { Text = _board.Counts[slot].ToString(),
                Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = size < 50 ? 13 : 16 } };
        grid.Children.Add(badge);
        if (marked)
            grid.Children.Add(new Border { Width = 10, Height = 10, Background = Brushes.Gold, BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        tile.Child = mine && current ? new Border { BorderBrush = myBrush, BorderThickness = new Thickness(2), Child = grid } : grid;
        ApplyOwnGlow(tile, slot);
        return tile;
    }
    Border CreateStatusTile(int slot)
    {
        var count = _board.Counts[slot];
        var current = slot == _board.Turn;
        var mine = slot == _board.MySlot;
        var marked = _board.HasOffTurnMarker(slot);
        var size = _settings.EnlargeCurrentImage && current ? 36 : 29;
        var currentBrush = OutlineBrush(_settings.CurrentOutlineColor, Brushes.Gold);
        var myBrush = OutlineBrush(_settings.MyOutlineColor, Brushes.DeepSkyBlue);
        var tile = new Border { Width = size, Height = size, Margin = new Thickness(2),
            BorderThickness = new Thickness(current || mine || marked ? 2 : 1),
            BorderBrush = current ? currentBrush : mine ? myBrush : marked ? Brushes.Gold : count == 0 ? Brushes.DimGray : Brushes.White,
            Background = current ? Brushes.Gold : count == 0 ? new SolidColorBrush(Color.FromRgb(65, 48, 50)) : Brushes.White,
            CornerRadius = new CornerRadius(1) };
        var grid = new Grid();
        grid.Children.Add(new TextBlock { Text = count.ToString(), Foreground = count == 0 && !current ? Brushes.White : Brushes.Black,
            FontWeight = FontWeights.Bold, FontSize = 15, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (slot == _board.StartSlot)
            grid.Children.Add(new TextBlock { Text = "시", Foreground = Brushes.LimeGreen, Background = Brushes.Black,
                FontSize = 8, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        if (marked)
            grid.Children.Add(new Border { Width = 7, Height = 7, Background = Brushes.Gold, BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
        tile.Child = mine && current ? new Border { BorderBrush = myBrush, BorderThickness = new Thickness(2), Child = grid } : grid;
        ApplyOwnGlow(tile, slot);
        return tile;
    }
    void ApplyOwnGlow(Border tile, int slot)
    {
        if (!_settings.GoldOnMyTurn || slot != _board.MySlot || slot != _board.Turn) return;
        var glow = new DropShadowEffect { Color = Colors.Gold, BlurRadius = 18, ShadowDepth = 0, Opacity = 1 };
        tile.Effect = glow;
        if (_settings.FlashGold)
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(.12, 1, TimeSpan.FromMilliseconds(520))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    static Brush OutlineBrush(string value, Brush fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        catch { return fallback; }
    }
    static ImageSource? LoadItemImage(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
        }
        catch { return null; }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
