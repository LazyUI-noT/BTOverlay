using System.Text.Json;

namespace BToverlay;

public sealed class UserSettings
{
    public string Name { get; set; } = "참가자";
    public string HostAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 47801;
    public int MaxItems { get; set; } = 3;
    public string ItemImage { get; set; } = "";
    public double OverlayX { get; set; } = 80;
    public double OverlayY { get; set; } = 80;
    public double OverlayScale { get; set; } = 1;
    public double OverlayOpacity { get; set; } = .88;
    public double CurrentSize { get; set; } = 1;
    public double OtherSize { get; set; } = 1;
    public double MySize { get; set; } = 1;
    public double TeamBoxSize { get; set; } = 1;
    public bool CombineMySize { get; set; }
    public string Party1Color { get; set; } = "#32CD32";
    public string Party2Color { get; set; } = "#9370DB";
    public string CurrentOutlineColor { get; set; } = "#FFD700";
    public string MyOutlineColor { get; set; } = "#00D4FF";
    public bool EnlargeCurrentImage { get; set; } = true;
    public int LayoutStyle { get; set; } = 1;
    public bool GoldOnMyTurn { get; set; } = true;
    public bool FlashGold { get; set; }
    public bool ShowOverlay { get; set; } = true;
    public bool HideFromCapture { get; set; }
    public string SpendKey { get; set; } = "Ctrl+Alt+F1";
    public string AddKey { get; set; } = "Ctrl+Alt+F2";
    public string ResetCountsKey { get; set; } = "Ctrl+Alt+F3";
    public string ResetTurnKey { get; set; } = "Ctrl+Alt+F4";
    public string ToggleKey { get; set; } = "Ctrl+Alt+F5";
    public string EditKey { get; set; } = "Ctrl+Alt+F6";
    public Guid GuestId { get; set; } = Guid.NewGuid();
    public static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "Data");
    static readonly string PathName = Path.Combine(DataFolder, "settings.json");
    public static UserSettings Load()
    {
        try { return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(PathName)) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class Board
{
    readonly DateTimeOffset[] _offTurnUntil = new DateTimeOffset[8];
    public int[] Counts { get; set; } = Enumerable.Repeat(3, 8).ToArray();
    public string[] Names { get; set; } = Enumerable.Range(1, 8).Select(i => $"참가자 {i}").ToArray();
    public bool[] Included { get; set; } = Enumerable.Repeat(true, 8).ToArray();
    public int[] Order { get; set; } = Enumerable.Range(0, 8).ToArray();
    public int Turn { get; set; }
    public int StartSlot { get; set; }
    public int MySlot { get; set; }
    public long Version { get; private set; }
    public event Action? Changed;
    public void Refresh() { Version++; Changed?.Invoke(); }
    public bool HasOffTurnMarker(int slot) => slot is >= 0 and < 8 && _offTurnUntil[slot] > DateTimeOffset.UtcNow;
    public void MarkOffTurn(int slot)
    {
        if (slot is < 0 or > 7) return;
        _offTurnUntil[slot] = DateTimeOffset.UtcNow.AddSeconds(5);
        Refresh();
    }
    public void ClearOffTurnMarkers()
    {
        if (_offTurnUntil.All(until => until == default)) return;
        Array.Clear(_offTurnUntil);
        Refresh();
    }
    public void ExpireOffTurnMarkers()
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        for (var slot = 0; slot < 8; slot++)
            if (_offTurnUntil[slot] != default && _offTurnUntil[slot] <= now)
            { _offTurnUntil[slot] = default; changed = true; }
        if (changed) Refresh();
    }
    public void ResetTurn()
    {
        var startIndex = Array.IndexOf(Order, StartSlot);
        if (startIndex < 0) startIndex = 0;
        for (var step = 0; step < 8; step++)
        {
            var candidate = Order[(startIndex + step) % 8];
            if (Included[candidate]) { Turn = candidate; break; }
        }
        Refresh();
    }
    public bool SetCount(int slot, int count, bool advanceTurn = true)
    {
        if (slot is < 0 or > 7 || count is < 0 or > 10) return false;
        var old = Counts[slot];
        Counts[slot] = count;
        if (advanceTurn && slot == Turn && old > 0 && count == 0)
        {
            var index = Array.IndexOf(Order, slot);
            for (int step = 1; step <= 8; step++)
            {
                var next = Order[(index + step) % 8];
                if (Included[next]) { Turn = next; break; }
            }
        }
        Refresh();
        return true;
    }
}
