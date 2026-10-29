using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzimuthTelemetry.Desktop.Input;
using AzimuthTelemetry.Desktop.Theming;

namespace AzimuthTelemetry.Desktop;

// Only read from older settings files (0.2 had a profile per game); the
// active one's values become the settings below.
public sealed class Profile
{
    public string Id { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8000;
    public PacketFormat Format { get; set; } = PacketFormat.Horizon;
    public int Hz { get; set; } = 60;
    public bool StartAzimuth { get; set; }
}

public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Dark;

    // Where the telemetry goes. The game is detected when it runs.
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8000;
    public PacketFormat Format { get; set; } = PacketFormat.Horizon;
    public int Hz { get; set; } = 60;
    public bool StartAzimuth { get; set; }      // Start also launches azimuth.exe

    public HotkeyBinding Hotkey { get; set; } = new();

    // In-car camera: the change per game id, and a key or button per action.
    public Dictionary<string, CameraAdjust> CameraAdjust { get; set; } = new();
    public Dictionary<CameraAction, HotkeyBinding>? CameraKeys { get; set; }
    public bool AnnounceToggle { get; set; } = true;
    public string? AzimuthFolder { get; set; }  // where azimuth.exe is, when this app isn't beside it
    public int? AspectWidth { get; set; }       // Render Aspect Ratio
    public int? AspectHeight { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Profile>? Profiles { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveProfileId { get; set; }

    public TelemetryOptions ToOptions() => new(Host, Port, Format, Hz);

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzimuthTelemetry");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        AppSettings s;
        // 0.1 was called Horizon Telemetry: carry its settings over once.
        string oldFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HorizonTelemetry", "settings.json");
        if (!File.Exists(FilePath) && File.Exists(oldFile))
        {
            try { Directory.CreateDirectory(Folder); File.Copy(oldFile, FilePath); } catch { }
        }
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch { s = new AppSettings(); /* corrupt settings: fall back to defaults */ }

        if (s.Profiles is { Count: > 0 } old)
        {
            var p = old.FirstOrDefault(x => x.Id == s.ActiveProfileId) ?? old[0];
            (s.Host, s.Port, s.Format, s.Hz, s.StartAzimuth) = (p.Host, p.Port, p.Format, p.Hz, p.StartAzimuth);
        }
        s.Profiles = null;
        s.ActiveProfileId = null;
        s.Hotkey ??= new HotkeyBinding();
        s.CameraAdjust ??= new();
        s.CameraKeys ??= DefaultCameraKeys();
        foreach (var a in Enum.GetValues<CameraAction>()) s.CameraKeys.TryAdd(a, new HotkeyBinding());
        return s;
    }

    // Ctrl + the number pad: + and - for FOV, 8/2 forward/back, 9/3 up/down,
    // 4/6 left/right, 5 reset.
    static Dictionary<CameraAction, HotkeyBinding> DefaultCameraKeys()
    {
        HotkeyBinding K(System.Windows.Input.Key k) => new()
        {
            Kind = HotkeyKind.Keyboard,
            VirtualKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k),
            Modifiers = System.Windows.Input.ModifierKeys.Control,
        };
        return new()
        {
            [CameraAction.FovUp] = K(System.Windows.Input.Key.Add),
            [CameraAction.FovDown] = K(System.Windows.Input.Key.Subtract),
            [CameraAction.SeatForward] = K(System.Windows.Input.Key.NumPad8),
            [CameraAction.SeatBack] = K(System.Windows.Input.Key.NumPad2),
            [CameraAction.SeatUp] = K(System.Windows.Input.Key.NumPad9),
            [CameraAction.SeatDown] = K(System.Windows.Input.Key.NumPad3),
            [CameraAction.SeatLeft] = K(System.Windows.Input.Key.NumPad4),
            [CameraAction.SeatRight] = K(System.Windows.Input.Key.NumPad6),
            [CameraAction.Reset] = K(System.Windows.Input.Key.NumPad5),
        };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { /* non-fatal */ }
    }
}
