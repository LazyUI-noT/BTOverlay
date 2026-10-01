using Microsoft.Win32;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BToverlay;

public partial class MainWindow : Window
{
    readonly UserSettings _settings = UserSettings.Load();
    readonly Board _board = new();
    OverlayWindow _overlay = null!;
    Hotkeys _hotkeys = null!;
    Session? _session;
    public MainWindow()
    {
        InitializeComponent();
        _board.Names[0] = _settings.Name;
        _board.Counts[0] = _settings.MaxItems;
        NameBox.Text = _settings.Name; AddressBox.Text = _settings.HostAddress; PortBox.Text = _settings.Port.ToString();
        MaxBox.Text = _settings.MaxItems.ToString(); ImageBox.Text = _settings.ItemImage;
        XBox.Text = _settings.OverlayX.ToString(); YBox.Text = _settings.OverlayY.ToString();
        ScaleBox.Text = _settings.OverlayScale.ToString("0.00"); OpacityBox.Text = _settings.OverlayOpacity.ToString("0.00");
        ShowBox.IsChecked = _settings.ShowOverlay; CaptureBox.IsChecked = _settings.HideFromCapture;
        Party1ColorBox.Text = _settings.Party1Color; Party2ColorBox.Text = _settings.Party2Color;
        CurrentOutlineBox.Text = _settings.CurrentOutlineColor; MyOutlineBox.Text = _settings.MyOutlineColor;
        EnlargeCurrentBox.IsChecked = _settings.EnlargeCurrentImage;
        StyleBox.SelectedIndex = _settings.LayoutStyle == 2 ? 1 : 0;
        GoldBox.IsChecked = _settings.GoldOnMyTurn; FlashBox.IsChecked = _settings.FlashGold;
        SpendKeyBox.Text = _settings.SpendKey; AddKeyBox.Text = _settings.AddKey;
        ResetCountsKeyBox.Text = _settings.ResetCountsKey; ResetTurnKeyBox.Text = _settings.ResetTurnKey; ToggleKeyBox.Text = _settings.ToggleKey;
        _board.Changed += RenderPlayers;
        Loaded += (_, _) => { _overlay = new OverlayWindow(_board, _settings); _overlay.Show(); _hotkeys = new Hotkeys(); ApplyHotkeys(); RenderPlayers(); };
        Closing += (_, _) => { _settings.Save(); _hotkeys?.Dispose(); _ = _session?.DisposeAsync(); _overlay?.Close(); };
    }
    void Status(string text) { StatusText.Text = text; }
    void SaveBasic()
    {
        _settings.Name = NameBox.Text.Trim();
        if (_settings.Name.Length is < 1 or > 24) throw new InvalidOperationException("이름은 1~24자로 입력하세요.");
        _settings.HostAddress = AddressBox.Text.Trim();
        if (_settings.HostAddress.Length is < 1 or > 255) throw new InvalidOperationException("호스트 IP 주소 또는 도메인을 입력하세요.");
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) throw new InvalidOperationException("포트는 1~65535 사이여야 합니다.");
        _settings.Port = port;
        _settings.Save();
    }
    async void HostClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveBasic();
            if (_session is not null) await _session.DisposeAsync();
            _session = await Session.HostAsync(_board, _settings);
            _session.ApproveJoin = (slot, name) => Task.FromResult(MessageBox.Show(this,
                $"{name}님이 참가자 {slot + 1}번으로 참가하려고 합니다. 허용할까요?", "참가 요청", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            _session.Notice += Status; _session.Changed += RenderPlayers;
            Status($"TCP {_settings.Port} 포트에서 호스트 중입니다. 참가자별 초대 토큰을 만드세요.");
            RenderPlayers(); _board.Refresh();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "호스트 시작 실패"); }
    }
    async void JoinClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveBasic();
            if (_session is not null) await _session.DisposeAsync();
            Status("연결 중입니다. 호스트의 승인을 기다리는 중...");
            _session = await Session.JoinAsync(_board, _settings, TokenBox.Text);
            _session.Notice += Status; _session.Changed += RenderPlayers;
            Status($"참가자 {_board.MySlot + 1}번으로 연결되었습니다.");
            RenderPlayers(); _board.Refresh();
        }
        catch (Exception ex) { Status("연결되지 않음"); MessageBox.Show(this, ex.Message, "참가 실패"); }
    }
    async void LeaveClick(object sender, RoutedEventArgs e)
    {
        if (_session is not null) await _session.DisposeAsync();
        _session = null; Status("연결되지 않음"); RenderPlayers();
    }
    void RenderPlayers()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RenderPlayers); return; }
        PlayersPanel.Children.Clear();
        for (var position = 0; position < 8; position++)
        {
            var slot = _board.Order[position];
            var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            var markers = (slot == _board.StartSlot ? " 시작" : "") + (slot == _board.Turn ? " 현재" : "");
            row.Children.Add(new TextBlock { Text = $"{position + 1}. {_board.Names[slot]} [{_board.Counts[slot]}]{markers}", Width = 177,
                VerticalAlignment = VerticalAlignment.Center, Foreground = slot == _board.Turn ? Brushes.Gold : Brushes.White });
            var enabled = new CheckBox { Content = "표시", Width = 55, IsChecked = _board.Included[slot], IsEnabled = _session?.IsHost == true };
            enabled.Click += async (_, _) => { if (_session is not null) await _session.SetIncludedAsync(slot, enabled.IsChecked == true); };
            row.Children.Add(enabled);
            if (_session?.IsHost == true)
            {
                var start = new Button { Content = "시작 지정", Width = 67, Margin = new Thickness(2, 0, 2, 0),
                    IsEnabled = _board.Included[slot] };
                start.Click += async (_, _) => await _session.SetStartAsync(slot);
                row.Children.Add(start);
            }
            if (_session?.IsHost == true && slot > 0)
            {
                var invite = new Button { Content = "초대 복사", Width = 81, Margin = new Thickness(2, 0, 2, 0) };
                invite.Click += (_, _) => { try { Clipboard.SetText(_session.CreateInvite(slot)); Status($"참가자 {slot + 1}번의 초대 토큰을 복사했습니다. 개인적으로 전달하세요."); } catch (Exception ex) { Status(ex.Message); } };
                row.Children.Add(invite);
                var kick = new Button { Content = "내보내기", Width = 55, Margin = new Thickness(2, 0, 2, 0) };
                kick.Click += async (_, _) => await _session.KickAsync(slot);
                row.Children.Add(kick);
            }
            if (_session?.IsHost == true)
            {
                var up = new Button { Content = "↑", Width = 26, Margin = new Thickness(2, 0, 2, 0) };
                up.Click += async (_, _) => await _session.MoveAsync(slot, -1);
                var down = new Button { Content = "↓", Width = 26, Margin = new Thickness(2, 0, 2, 0) };
                down.Click += async (_, _) => await _session.MoveAsync(slot, 1);
                row.Children.Add(up); row.Children.Add(down);
            }
            PlayersPanel.Children.Add(row);
        }
    }
    async void SpendClick(object sender, RoutedEventArgs e) => await ChangeCount(-1);
    async void AddClick(object sender, RoutedEventArgs e) => await ChangeCount(1);
    async Task ChangeCount(int delta)
    {
        var value = Math.Clamp(_board.Counts[_board.MySlot] + delta, 0, 10);
        if (_session is null || !_session.Connected)
        {
            var offTurn = value < _board.Counts[_board.MySlot] && _board.MySlot != _board.Turn;
            _board.SetCount(_board.MySlot, value);
            if (offTurn) _board.MarkOffTurn(_board.MySlot);
        }
        else await _session.UpdateMyCountAsync(value);
    }
    async void ResetCountsClick(object sender, RoutedEventArgs e) => await ResetCounts();
    async Task ResetCounts()
    {
        if (_session?.IsHost == true) await _session.ResetCountsAsync();
        else if (_session is null || !_session.Connected)
        { _board.ClearOffTurnMarkers(); _board.SetCount(_board.MySlot, _settings.MaxItems, false); }
    }
    async void ResetTurnClick(object sender, RoutedEventArgs e) => await ResetTurn();
    async Task ResetTurn()
    {
        if (_session?.IsHost == true) await _session.ResetTurnAsync();
        else if (_session is null) _board.ResetTurn();
    }
    void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
        if (picker.ShowDialog(this) == true) ImageBox.Text = picker.FileName;
    }
    void ApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(MaxBox.Text, out var max) || max is < 0 or > 10) throw new InvalidOperationException("최대 아이템 개수는 0~10 사이여야 합니다.");
            if (!double.TryParse(XBox.Text, out var x) || !double.TryParse(YBox.Text, out var y) ||
                !double.TryParse(ScaleBox.Text, out var scale) || scale is < .4 or > 3 ||
                !double.TryParse(OpacityBox.Text, out var opacity) || opacity is < .1 or > 1)
                throw new InvalidOperationException("위치 X/Y, 크기(0.4~3), 불투명도(0.1~1)를 올바르게 입력하세요.");
            if (ImageBox.Text.Length > 0 && !File.Exists(ImageBox.Text)) throw new InvalidOperationException("이미지 파일을 찾을 수 없습니다.");
            if (ImageBox.Text.Length > 0 && new FileInfo(ImageBox.Text).Length > 10 * 1024 * 1024) throw new InvalidOperationException("이미지는 10MB 이하여야 합니다.");
            try
            {
                _ = (Color)ColorConverter.ConvertFromString(Party1ColorBox.Text);
                _ = (Color)ColorConverter.ConvertFromString(Party2ColorBox.Text);
                _ = (Color)ColorConverter.ConvertFromString(CurrentOutlineBox.Text);
                _ = (Color)ColorConverter.ConvertFromString(MyOutlineBox.Text);
            }
            catch { throw new InvalidOperationException("색상은 #32CD32와 같은 값으로 입력하세요."); }
            _settings.MaxItems = max;
            if (ImageBox.Text.Length > 0)
            {
                var source = Path.GetFullPath(ImageBox.Text);
                var folder = Path.Combine(UserSettings.DataFolder, "items");
                Directory.CreateDirectory(folder);
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
                var target = Path.Combine(folder, hash + Path.GetExtension(source).ToLowerInvariant());
                if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, true);
                _settings.ItemImage = target;
                ImageBox.Text = target;
            }
            else _settings.ItemImage = "";
            _settings.OverlayX = x; _settings.OverlayY = y; _settings.OverlayScale = scale; _settings.OverlayOpacity = opacity;
            _settings.ShowOverlay = ShowBox.IsChecked == true; _settings.HideFromCapture = CaptureBox.IsChecked == true;
            _settings.Party1Color = Party1ColorBox.Text; _settings.Party2Color = Party2ColorBox.Text;
            _settings.CurrentOutlineColor = CurrentOutlineBox.Text; _settings.MyOutlineColor = MyOutlineBox.Text;
            _settings.EnlargeCurrentImage = EnlargeCurrentBox.IsChecked == true;
            _settings.LayoutStyle = StyleBox.SelectedIndex == 1 ? 2 : 1;
            _settings.GoldOnMyTurn = GoldBox.IsChecked == true;
            _settings.FlashGold = FlashBox.IsChecked == true;
            _settings.Save(); _overlay.ApplyStyle();
            Status(_settings.HideFromCapture && !_overlay.CaptureExclusionApplied ? "Windows가 오버레이 캡처 숨기기를 적용하지 못했습니다." : "표시 설정을 저장했습니다.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "설정 확인"); }
    }
    void HotkeysClick(object sender, RoutedEventArgs e)
    {
        _settings.SpendKey = SpendKeyBox.Text; _settings.AddKey = AddKeyBox.Text;
        _settings.ResetCountsKey = ResetCountsKeyBox.Text; _settings.ResetTurnKey = ResetTurnKeyBox.Text; _settings.ToggleKey = ToggleKeyBox.Text;
        _settings.Save(); ApplyHotkeys();
    }
    void ApplyHotkeys()
    {
        var failed = _hotkeys.Apply(_settings, () => _ = ChangeCount(-1), () => _ = ChangeCount(1),
            () => _ = ResetCounts(), () => _ = ResetTurn(), ToggleOverlay);
        Status(failed.Count == 0 ? "단축키를 적용했습니다." : $"등록하지 못한 단축키: {string.Join(", ", failed)}.");
    }
    void ToggleOverlay()
    {
        _settings.ShowOverlay = !_settings.ShowOverlay;
        ShowBox.IsChecked = _settings.ShowOverlay;
        _settings.Save(); _overlay.ApplyStyle();
    }
}
