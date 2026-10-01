using BToverlay;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var uiReady = new TaskCompletionSource<Dispatcher>();
var uiThread = new Thread(() =>
{
    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    uiReady.SetResult(Dispatcher.CurrentDispatcher);
    Dispatcher.Run();
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
var dispatcher = await uiReady.Task;

static async Task Until(Func<bool> condition, string name)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition())
    {
        if (timeout.IsCancellationRequested) throw new Exception($"Timed out: {name}");
        await Task.Delay(20);
    }
}
static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
static void Preview(int style)
{
    var board = new Board { MySlot = 2, StartSlot = 5, Turn = 2 };
    board.Counts = [0, 1, 3, 2, 3, 3, 0, 1];
    board.MarkOffTurn(4);
    var settings = new UserSettings { LayoutStyle = style, GoldOnMyTurn = true, FlashGold = false,
        CurrentOutlineColor = "#FF6600", MyOutlineColor = "#00FFFF", CurrentSize = 1.2,
        OtherSize = .8, MySize = 1.1, TeamBoxSize = 1.15, CombineMySize = true, ShowOverlay = false };
    var window = new OverlayWindow(board, settings);
    window.SetEditMode(true);
    Check(window.IsEditing && window.Visibility == Visibility.Visible, "edit mode reveals the overlay");
    window.SetEditMode(false);
    Check(!window.IsEditing && window.Visibility == Visibility.Hidden, "edit mode restores hidden state");
    settings.ShowOverlay = true;
    window.ApplyStyle();
    var content = (FrameworkElement)window.Content;
    content.Measure(new Size(1200, 400));
    content.Arrange(new Rect(content.DesiredSize));
    content.UpdateLayout();
    var width = (int)Math.Ceiling(content.ActualWidth);
    var height = (int)Math.Ceiling(content.ActualHeight);
    var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    image.Render(content);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
    var path = Path.Combine(Environment.CurrentDirectory, "reference", $"preview_style{style}.png");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var file = File.Create(path); encoder.Save(file);
    window.Close();
}

Session? host = null;
Session? guest = null;
Session? guest2 = null;
try
{
    Check(Hotkeys.TryParse("2", out var singleModifiers, out var singleKey) && singleModifiers == 0 && singleKey == 0x32,
        "single digit key parses");
    Check(Hotkeys.TryParse("Ctrl+Alt+F1", out var comboModifiers, out var comboKey) && comboModifiers == 3 && comboKey == 0x70,
        "modified combination parses");
    Check(!Hotkeys.TryParse("2+3", out _, out _), "two main keys are rejected");
    await dispatcher.InvokeAsync(() =>
    {
        using var keys = new Hotkeys();
        var settings = new UserSettings { SpendKey = "2" };
        var failed = keys.Apply(settings, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
        Check(!failed.Contains("사용 −1"), "single key listener installs");
    });
    Check(new UserSettings().EnlargeCurrentImage, "current image enlargement defaults on");
    var board = new Board();
    board.SetCount(0, 0);
    Check(board.Turn == 1, "turn advances on zero");
    board.Included[2] = false;
    board.Order = [0, 2, 3, 1, 4, 5, 6, 7];
    board.ResetTurn();
    Check(board.Turn == 0, "turn resets to first displayed slot");
    board.SetCount(0, 2);
    board.SetCount(0, 0);
    Check(board.Turn == 3, "turn skips excluded slot in host order");

    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
    var hs = new UserSettings { Name = "Host", HostAddress = "127.0.0.1", Port = port, MaxItems = 3 };
    var gs = new UserSettings { Name = "Guest", MaxItems = 5 };
    var hb = new Board(); var gb = new Board();
    host = await dispatcher.InvokeAsync(() => Session.HostAsync(hb, hs)).Task.Unwrap();
    host.ApproveJoin = (_, _) => Task.FromResult(true);
    var token = await dispatcher.InvokeAsync(() => host.CreateInvite(1));
    guest = await Session.JoinAsync(gb, gs, token);
    await Until(() => hb.Counts[1] == 5 && gb.MySlot == 1, "join and own max sync");
    await Task.Delay(3100); // host's per-address connection throttle
    var g2s = new UserSettings { Name = "Guest 2", MaxItems = 3 };
    var g2b = new Board();
    var token2 = await dispatcher.InvokeAsync(() => host.CreateInvite(2));
    guest2 = await Session.JoinAsync(g2b, g2s, token2);
    await Until(() => g2b.MySlot == 2, "second guest joins");
    await guest.UpdateMyCountAsync(4);
    await Until(() => hb.Counts[1] == 4 && g2b.Counts[1] == 4 && hb.HasOffTurnMarker(1) && g2b.HasOffTurnMarker(1), "off-turn spend and gold marker reach session");
    await guest.UpdateMyCountAsync(5);
    await Until(() => hb.Counts[1] == 5 && g2b.Counts[1] == 5, "off-turn add reaches session");
    await Task.Delay(5200);
    Check(!hb.HasOffTurnMarker(1) && !g2b.HasOffTurnMarker(1), "off-turn marker expires after five seconds");
    await dispatcher.InvokeAsync(() => host.MoveAsync(1, 1)).Task.Unwrap();
    await Until(() => gb.Order[2] == 1, "host order reaches guest");
    await dispatcher.InvokeAsync(() => host.SetStartAsync(3)).Task.Unwrap();
    await Until(() => gb.StartSlot == 3, "host start reaches guest");
    await dispatcher.InvokeAsync(() => host.ResetTurnAsync()).Task.Unwrap();
    await Until(() => gb.Turn == 3, "turn reset uses host start");
    await dispatcher.InvokeAsync(() => host.SetIncludedAsync(1, false)).Task.Unwrap();
    await Until(() => !gb.Included[1], "visibility reaches guest");
    await dispatcher.InvokeAsync(() => host.ResetCountsAsync()).Task.Unwrap();
    await Until(() => hb.Counts[1] == 5 && gb.Counts[1] == 5, "guest resets to local max");
    await dispatcher.InvokeAsync(() => { gs.MaxItems = 0; hb.Turn = 1; hb.SetCount(1, 2, false); });
    await dispatcher.InvokeAsync(() => host.ResetCountsAsync()).Task.Unwrap();
    await Until(() => hb.Counts[1] == 0 && gb.Counts[1] == 0, "zero maximum resets");
    Check(hb.Turn == 1, "item reset must not advance turn");
    await dispatcher.InvokeAsync(() => host.KickAsync(1)).Task.Unwrap();
    await Until(() => !guest.Connected, "kick disconnects guest");
    await Task.Delay(3100); // host's per-address connection throttle
    var reused = false;
    try { var again = await Session.JoinAsync(new Board(), gs, token); reused = true; await again.DisposeAsync(); }
    catch (Exception) { }
    Check(!reused, "used invite cannot reconnect after kick");
    await dispatcher.InvokeAsync(() => { Preview(1); Preview(2); });
    Console.WriteLine("PASS: turn order, join, off-turn spend marker, add sync, order, visibility, reset, kick, invite revocation");
}
finally
{
    if (guest is not null) await guest.DisposeAsync();
    if (guest2 is not null) await guest2.DisposeAsync();
    if (host is not null) await host.DisposeAsync();
    await dispatcher.InvokeAsync(() => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal));
    uiThread.Join(TimeSpan.FromSeconds(3));
}
