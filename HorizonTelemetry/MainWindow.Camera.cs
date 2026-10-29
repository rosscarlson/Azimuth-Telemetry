using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HorizonTelemetry.Desktop.Input;

namespace HorizonTelemetry.Desktop;

// One camera action's key or button in the In-car camera card.
public sealed class CameraKeyRow : INotifyPropertyChanged
{
    bool _capturing;
    string? _problem;

    public required CameraAction Action { get; init; }
    public required string Label { get; init; }
    public required HotkeyBinding Binding { get; set; }

    public bool IsCapturing
    {
        get => _capturing;
        set { _capturing = value; Changed(nameof(IsCapturing)); Changed(nameof(Display)); }
    }
    // Why the key can't be used (another app owns it), if so.
    public string? Problem
    {
        get => _problem;
        set { _problem = value; Changed(nameof(Problem)); Changed(nameof(HasProblem)); Changed(nameof(Display)); }
    }
    public bool HasProblem => _problem != null;
    public string Display => _capturing ? "Press a key or button…" : _problem ?? Binding.ToString();

    public void Refresh() => Changed(nameof(Display));

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// The Camera card: field of view (every driving view) and seat position
// (in-car views) while the game runs (CameraAdjuster), with a key or wheel button for each step.
public partial class MainWindow
{
    static readonly (CameraAction Action, string Label)[] CameraActions =
    {
        (CameraAction.FovUp, "FOV +1°"),
        (CameraAction.FovDown, "FOV −1°"),
        (CameraAction.SeatForward, "Forward"),
        (CameraAction.SeatBack, "Back"),
        (CameraAction.SeatUp, "Up"),
        (CameraAction.SeatDown, "Down"),
        (CameraAction.SeatLeft, "Left"),
        (CameraAction.SeatRight, "Right"),
        (CameraAction.Reset, "Reset"),
    };

    CameraAdjuster? _camera;
    readonly CameraStatus _cameraStatus = new();
    readonly List<CameraKeyRow> _cameraKeys = new();
    readonly List<GlobalHotkey> _cameraHotkeys = new();
    CameraKeyRow? _cameraCapture;

    void InitCamera()
    {
        foreach (var (action, label) in CameraActions)
            _cameraKeys.Add(new CameraKeyRow { Action = action, Label = label, Binding = _settings.CameraKeys![action] });
        CameraKeyList.ItemsSource = _cameraKeys;

        // Keys are "<game>/<view>"; 0.4.0 saved the cockpit view under the game id alone.
        _camera = new CameraAdjuster(key =>
            _settings.CameraAdjust.TryGetValue(key, out var a) ? a
            : key.EndsWith("/cockpit") && _settings.CameraAdjust.TryGetValue(key[..^"/cockpit".Length], out var old) ? old
            : new CameraAdjust());
        _camera.Changed += (id, a) => Dispatcher.BeginInvoke(() =>
        {
            _settings.CameraAdjust[id] = a;
            _settings.Save();
        });
        _camera.Start();
    }

    // Called once the window has a handle (global hotkeys need one).
    void ApplyCameraKeys()
    {
        foreach (var h in _cameraHotkeys) h.Dispose();
        _cameraHotkeys.Clear();
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source == null) return;
        for (int i = 0; i < _cameraKeys.Count; i++)
        {
            var row = _cameraKeys[i];
            row.Problem = null;
            var b = row.Binding;
            if (b.Kind != HotkeyKind.Keyboard || _cameraCapture != null) continue;
            var hk = new GlobalHotkey(source, 0x4860 + i, repeat: row.Action != CameraAction.Reset);
            if (hk.Register(b.VirtualKey, b.Modifiers))
            {
                var action = row.Action;
                hk.Pressed += () => _camera?.Do(action);
                _cameraHotkeys.Add(hk);
            }
            else
            {
                hk.Dispose();
                row.Problem = $"{b} is taken by another app";
            }
        }
    }

    void RefreshCamera()
    {
        if (_camera == null) return;
        _camera.Snapshot(_cameraStatus);
        var s = _cameraStatus;
        var a = s.Adjust;
        if (s.Game == null || s.View is not { } view || s.GameFovDeg is not float fov)
        {
            CameraText.Text = s.Text.Length > 0 ? s.Text : "Waiting for the game.";
            CameraSubText.Text = "Changes apply to the view you're driving in, each view its own.";
            return;
        }
        CameraText.Text = $"{view.Name}: FOV {fov + a.FovDeg:0}°" + (a.FovDeg == 0 ? " (the game's own)" : $" (game {fov:0}°, {a.FovDeg:+0;−0}°)");
        CameraSubText.Text = (view.Seat == null ? "Seat keys work in the cockpit, dashboard, hood and bumper views"
            : a.X == 0 && a.Y == 0 && a.Z == 0 ? "Seat: the game's own position" : "Seat: " + DescribeSeat(a)) + $" · {s.Game.ShortName}";
    }

    static string DescribeSeat(CameraAdjust a)
    {
        var parts = new List<string>();
        if (a.Z != 0) parts.Add($"{Math.Abs(a.Z) * 100:0} cm {(a.Z > 0 ? "forward" : "back")}");
        if (a.Y != 0) parts.Add($"{Math.Abs(a.Y) * 100:0} cm {(a.Y > 0 ? "up" : "down")}");
        if (a.X != 0) parts.Add($"{Math.Abs(a.X) * 100:0} cm {(a.X > 0 ? "right" : "left")}");
        return string.Join(", ", parts);
    }

    void CameraReset_Click(object sender, RoutedEventArgs e) => _camera?.Do(CameraAction.Reset);

    void CameraKeySet_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CameraKeyRow row) return;
        if (_capturing) EndCapture();
        if (_cameraCapture != null) _cameraCapture.IsCapturing = false;
        _cameraCapture = row;
        row.IsCapturing = true;
        ApplyCameraKeys();  // unregistered while listening, so the key reaches this window
        Focus();
    }

    void CameraKeyClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CameraKeyRow row) return;
        SetCameraKey(row, new HotkeyBinding());
    }

    void SetCameraKey(CameraKeyRow row, HotkeyBinding b)
    {
        row.Binding = b;
        row.IsCapturing = false;
        if (_cameraCapture == row) _cameraCapture = null;
        _settings.CameraKeys![row.Action] = b;
        _settings.Save();
        row.Refresh();
        ApplyCameraKeys();
    }

    // A key pressed in this window while a camera key is being set.
    bool CameraCaptureKey(KeyEventArgs e)
    {
        if (_cameraCapture == null) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape)
        {
            _cameraCapture.IsCapturing = false;
            _cameraCapture = null;
            ApplyCameraKeys();
            return true;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return true;
        SetCameraKey(_cameraCapture, new HotkeyBinding
        {
            Kind = HotkeyKind.Keyboard,
            VirtualKey = KeyInterop.VirtualKeyFromKey(key),
            Modifiers = Keyboard.Modifiers,
        });
        return true;
    }

    // A wheel or button-box button: sets a camera key, or runs its action.
    bool CameraJoystickButton(JoystickDevice d, int button)
    {
        if (_cameraCapture != null)
        {
            SetCameraKey(_cameraCapture, new HotkeyBinding
            {
                Kind = HotkeyKind.Joystick, JoyVendor = d.Vendor, JoyProduct = d.Product, JoyName = d.Name, Button = button,
            });
            return true;
        }
        foreach (var row in _cameraKeys)
        {
            var b = row.Binding;
            if (b.Kind == HotkeyKind.Joystick && b.JoyVendor == d.Vendor && b.JoyProduct == d.Product && b.Button == button)
            {
                _camera?.Do(row.Action);
                return true;
            }
        }
        return false;
    }

    void DisposeCamera()
    {
        foreach (var h in _cameraHotkeys) h.Dispose();
        _cameraHotkeys.Clear();
        _camera?.Dispose();
        _camera = null;
    }
}
