using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AzimuthTelemetry.Desktop.Azimuth;
using AzimuthTelemetry.Desktop.Display;
using AzimuthTelemetry.Desktop.Input;
using AzimuthTelemetry.Desktop.Theming;
using AzimuthTelemetry.Games;

namespace AzimuthTelemetry.Desktop;

public partial class MainWindow : Window
{
    readonly AppSettings _settings;
    readonly ObservableCollection<string> _log = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly EngineStatus _status = new();
    readonly Voice _voice = new();
    readonly JoystickWatcher _joysticks = new();
    readonly string _probeFolder = Path.Combine(AppContext.BaseDirectory, "probes");

    GlobalHotkey? _hotkey;
    TelemetryEngine? _engine;
    ProbeRecorder? _probe;
    string? _probeResult;
    bool _probeResultIsError;
    string? _info;
    bool _infoIsError;
    bool _initializing = true;
    bool _capturing;
    string? _shimMessage;
    readonly List<BindingRow> _bindings = AzimuthControls.NewRows();
    readonly DispatcherTimer _captureTick = new() { Interval = TimeSpan.FromMilliseconds(25) };
    readonly DispatcherTimer _diagramTick = new() { Interval = TimeSpan.FromMilliseconds(40) };
    WheelInput? _wheels;
    BindingCapture? _bindCapture;
    BindingRow? _bindRow;
    DateTime _bindDeadline;
    bool _controlsLoaded, _controlsDirty;
    string? _controlsMessage;
    bool _controlsMessageIsError;
    bool _shimMessageIsError;
    DateTime _lastJoyToggle, _lastCameraJoy;
    (ushort, ushort, int) _lastCameraButton;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        ThemeBox.SelectedIndex = (int)settings.Theme;
        AnnounceCheck.IsChecked = settings.AnnounceToggle;
        LogList.ItemsSource = _log;
        HostBox.Text = settings.Host;
        PortBox.Text = settings.Port.ToString();
        FormatBox.SelectedIndex = settings.Format == PacketFormat.Dash ? 1 : 0;
        RateBox.SelectedItem = RateBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.Hz.ToString()) ?? RateBox.Items[1];
        StartAzimuthCheck.IsChecked = settings.StartAzimuth;

        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
        _joysticks.ButtonDown += (d, b) => Dispatcher.BeginInvoke(() => OnJoystickButton(d, b));
        BindingList.ItemsSource = _bindings;
        _captureTick.Tick += (_, _) => CaptureStep();
        _diagramTick.Tick += (_, _) => RefreshDiagram();
        InitCamera();
        _initializing = false;
        Refresh();
        RefreshScreens();
    }

    bool Running => _engine != null;

    void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ThemeManager.ApplyTitleBar(this);
        _hotkey = new GlobalHotkey(HwndSource.FromHwnd(new WindowInteropHelper(this).Handle));
        _hotkey.Pressed += () => Toggle(fromHotkey: true);
        ApplyHotkey();
        ApplyCameraKeys();
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _tick.Stop();
        _probe?.Dispose();
        StopEngine();
        DisposeCamera();
        _hotkey?.Dispose();
        _joysticks.Dispose();
        _captureTick.Stop();
        _diagramTick.Stop();
        _wheels?.Dispose();
        _settings.Save();
    }

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.Theme = (ThemeChoice)Math.Max(0, ThemeBox.SelectedIndex);
        ThemeManager.Apply(_settings.Theme);
        _settings.Save();
    }

    // ---- Where the telemetry goes ----------------------------------------

    void Target_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        string host = HostBox.Text.Trim();
        if (host.Length == 0) HostBox.Text = _settings.Host;
        else _settings.Host = host;
        if (int.TryParse(PortBox.Text.Trim(), out int port) && port is > 0 and < 65536) _settings.Port = port;
        else
        {
            PortBox.Text = _settings.Port.ToString();
            ShowInfo("The port must be a number from 1 to 65535.", error: true);
        }
        _settings.Save();
    }

    void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.Format = FormatBox.SelectedIndex == 1 ? PacketFormat.Dash : PacketFormat.Horizon;
        _settings.Save();
    }

    void RateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || RateBox.SelectedItem is not ComboBoxItem i) return;
        _settings.Hz = int.Parse((string)i.Tag);
        _settings.Save();
    }

    // ---- Engine ----------------------------------------------------------

    void Start_Click(object sender, RoutedEventArgs e)
    {
        _info = null;
        if (Running) StopEngine();
        else StartEngine();
        Refresh();
    }

    void Toggle(bool fromHotkey)
    {
        bool on;
        if (Running) { StopEngine(); on = false; }
        else on = StartEngine();
        if (fromHotkey && _settings.AnnounceToggle)
            _voice.Say(on ? "Telemetry on" : "Telemetry off", beep: false);
        Refresh();
    }

    // The game is detected once Azimuth runs it.
    bool StartEngine()
    {
        Target_LostFocus(this, new RoutedEventArgs());  // take any half-typed host/port
        var engine = new TelemetryEngine(null, _settings.ToOptions());
        engine.Log += line => Dispatcher.BeginInvoke(() => AddLog(line));
        _engine = engine;
        AddLog("Started.");
        engine.Start();
        if (_settings.StartAzimuth) LaunchAzimuth();
        return true;
    }

    void StopEngine()
    {
        if (_engine == null) return;
        _engine.Dispose();
        _engine = null;
        AddLog("Stopped.");
    }

    void NextCar_Click(object sender, RoutedEventArgs e) => _engine?.NextCar();
    void Rescan_Click(object sender, RoutedEventArgs e) => _engine?.Rescan();

    void AddLog(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_log.Count > 200) _log.RemoveAt(0);
        LogScroll.ScrollToEnd();
    }

    void ShowInfo(string text, bool error)
    {
        _info = text;
        _infoIsError = error;
        Refresh();
    }

    // ---- Periodic UI refresh ---------------------------------------------

    void Refresh()
    {
        if (_engine != null)
        {
            _engine.Snapshot(_status);
            if (_status.State == EngineState.Error)
            {
                // The engine gave up (bad address and so on): show why.
                string why = _status.Detail;
                StopEngine();
                ShowInfo(why, error: true);
                return;
            }
        }
        var game = _engine?.Game;

        // Header pill
        string pill = !Running ? "Stopped" : _status.State switch
        {
            EngineState.Sending => $"Sending · {game?.ShortName}",
            EngineState.Paused => "Game paused",
            EngineState.NoCar => "Looking for car",
            EngineState.WrongGame => "Unsupported game",
            _ => "Waiting for game",
        };
        RunPillText.Text = pill;
        RunDot.Fill = (Brush)FindResource(!Running ? "TextSecondaryBrush"
            : _status.State == EngineState.Sending ? "SoundingBrush"
            : _status.State == EngineState.WrongGame ? "ErrorTextBrush" : "AccentBrush");
        GameText.Text = game != null ? $"{game.Name} · {game.BuildDescription}"
            : "Forza Horizon 2 or 3, detected when it runs in Azimuth";

        // Primary button
        StartButton.Style = (Style)FindResource(Running ? "DangerButton" : "AccentButton");
        StartIcon.Text = Running ? "\uE71A" : "\uE768";
        StartLabel.Text = Running ? "Stop" : "Start";
        SettingsGrid.IsEnabled = !Running;
        SettingsHint.Text = Running ? "Stop telemetry to change these."
            : "In SimHub pick Forza Horizon 4 (or 5) and set its UDP port to match. 127.0.0.1 means this PC.";

        // Status line (priority: error/info, then live state, then hint)
        string text;
        bool error = false;
        if (_info != null) { text = _info; error = _infoIsError; }
        else if (Running)
        {
            var o = _engine!.Options;
            (text, error) = _status.State switch
            {
                EngineState.WaitingForGame => (_status.Detail.Length > 0 ? _status.Detail : "Waiting for azimuth.exe — start the game in Azimuth.", false),
                EngineState.WrongGame => (_status.Detail, true),
                EngineState.NoCar => ($"Connected to {game?.ShortName}. {_status.Detail}", false),
                EngineState.Paused => ("Game paused — sending idle packets until you resume.", false),
                EngineState.Sending => ($"Sending {game?.ShortName} telemetry to {o.Host}:{o.Port} at {o.Hz} Hz.", false),
                _ => ("Starting…", false),
            };
        }
        else text = "Ready. Start now or later; it waits for the game.";
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "ErrorTextBrush" : "TextBrush");

        RefreshLive(Running);
        RefreshProbe();
        RefreshCamera();
    }

    void RefreshLive(bool live)
    {
        var s = _status.Sample;
        bool show = live && _status.State is EngineState.Sending or EngineState.Paused && s.Driving;
        GearText.Text = !show ? "–" : s.Gear switch { -1 => "R", 0 => "N", _ => s.Gear.ToString() };
        RpmText.Text = show ? s.Rpm.ToString("N0") : "–";
        RedlineText.Text = show && s.RedlineRpm > 0 ? $"/ {s.RedlineRpm:N0}" : "";
        SpeedText.Text = show ? $"{s.SpeedMps * 2.23694f:F0} mph" : "–";
        SpeedAltText.Text = show ? $"{s.SpeedMps * 3.6f:F0} km/h" : "";
        ThrottleScale.ScaleX = show ? Math.Clamp(s.Throttle, 0, 1) : 0;
        BrakeScale.ScaleX = show ? Math.Clamp(s.Brake, 0, 1) : 0;
        ClutchScale.ScaleX = show ? Math.Clamp(s.Clutch, 0, 1) : 0;
        HandbrakeScale.ScaleX = show ? Math.Clamp(s.Handbrake, 0, 1) : 0;
        float steer = show ? Math.Clamp(s.Steer, -1, 1) : 0;
        SteerLeftScale.ScaleX = steer < 0 ? -steer : 0;
        SteerRightScale.ScaleX = steer > 0 ? steer : 0;

        LivePlaceholder.Visibility = Visibility.Collapsed;
        NextCarButton.IsEnabled = RescanButton.IsEnabled = live && _status.Cars > 0;
        CarInfoText.Text = !live ? "Not running."
            : _status.Car == 0 ? $"{_status.Cars} car(s) found · {_status.Sent:N0} packets sent"
            : $"{_status.Cars} car(s) found · reading {_status.Car:X}{(_status.Pinned ? " (picked by Next car)" : "")} · {_status.Sent:N0} packets sent";
    }

    // ---- Probe -----------------------------------------------------------

    void RefreshProbe()
    {
        if (_probe != null)
        {
            var (stage, message, elapsed, path) = _probe.Status();
            if (stage is ProbeStage.Done or ProbeStage.Failed or ProbeStage.Cancelled)
            {
                _probeResult = stage == ProbeStage.Done && path != null
                    ? $"{message} Saved {Path.GetFileName(path)} in the probes folder; send it over for analysis."
                    : message;
                _probeResultIsError = stage == ProbeStage.Failed;
                _probe.Dispose();
                _probe = null;
            }
            else
            {
                ProbeText.Text = stage == ProbeStage.Recording ? $"{elapsed:F0} of {_probe.Seconds} s · “{message}”" : message;
                ProbeText.Visibility = Visibility.Visible;
                ProbeText.Foreground = (Brush)FindResource("TextSecondaryBrush");
                ProbeMeter.Visibility = Visibility.Visible;
                ProbeScale.ScaleX = Math.Clamp(elapsed / _probe.Seconds, 0, 1);
                ProbeIcon.Text = "\uE711";
                ProbeLabel.Text = "Cancel";
                ProbeButton.IsEnabled = true;
                return;
            }
        }
        ProbeMeter.Visibility = Visibility.Collapsed;
        ProbeIcon.Text = "\uE7C8";
        ProbeLabel.Text = "Record probe";
        ProbeButton.IsEnabled = true;
        ProbeText.Text = _probeResult ?? "";
        ProbeText.Visibility = ProbeText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProbeText.Foreground = (Brush)FindResource(_probeResult != null && _probeResultIsError ? "ErrorTextBrush" : "TextSecondaryBrush");
    }

    void Probe_Click(object sender, RoutedEventArgs e)
    {
        if (_probe != null)
        {
            _probe.Cancel();
            return;
        }
        _probeResult = null;
        _probe = new ProbeRecorder(null, _probeFolder);
        _probe.Start();
        Refresh();
    }

    void OpenProbes_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_probeFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_probeFolder}\"") { UseShellExecute = true });
    }

    // ---- Hotkey ----------------------------------------------------------

    void SetHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) { EndCapture(); return; }
        _capturing = true;
        _hotkey?.Unregister();
        HotkeyText.Text = "Press a key or a wheel/controller button…";
        SetHotkeyButton.Content = "Cancel";
        HotkeyWarning.Visibility = Visibility.Collapsed;
        Focus();
    }

    void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) EndCapture();
        _settings.Hotkey = new HotkeyBinding();
        _settings.Save();
        ApplyHotkey();
    }

    void EndCapture()
    {
        _capturing = false;
        SetHotkeyButton.Content = "Set…";
        ApplyHotkey();
    }

    void ApplyHotkey()
    {
        var h = _settings.Hotkey;
        HotkeyText.Text = h.ToString();
        string? warning = null;
        _hotkey?.Unregister();
        if (h.Kind == HotkeyKind.Keyboard && _hotkey != null && !_hotkey.Register(h.VirtualKey, h.Modifiers))
            warning = $"Another app is already using {h}. Pick a different key.";
        else if (h.Kind == HotkeyKind.Joystick && _joysticks.Devices.Count > 0
                 && !_joysticks.Devices.Any(d => d.Vendor == h.JoyVendor && d.Product == h.JoyProduct))
            warning = $"{h.JoyName} isn't connected right now.";
        HotkeyWarning.Text = warning ?? "";
        HotkeyWarning.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key == Key.Escape) { EndCapture(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;
            _settings.Hotkey = new HotkeyBinding
            {
                Kind = HotkeyKind.Keyboard,
                VirtualKey = KeyInterop.VirtualKeyFromKey(key),
                Modifiers = Keyboard.Modifiers,
            };
            _settings.Save();
            EndCapture();
            return;
        }

        if (CameraCaptureKey(e)) return;

        // Space toggles the primary action, except where it means something else.
        if (_bindCapture != null && e.Key == Key.Escape)
        {
            e.Handled = true;
            EndBindCapture("Cancelled.", false);
            return;
        }

        if (e.Key == Key.Space && TelemetryTab.IsChecked == true && Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem or ButtonBase))
        {
            e.Handled = true;
            if (StartButton.IsEnabled) Start_Click(this, e);
        }
    }

    void OnJoystickButton(JoystickDevice d, int button)
    {
        var h = _settings.Hotkey;
        if (_capturing)
        {
            _settings.Hotkey = new HotkeyBinding
            {
                Kind = HotkeyKind.Joystick, JoyVendor = d.Vendor, JoyProduct = d.Product, JoyName = d.Name, Button = button,
            };
            _settings.Save();
            EndCapture();
            return;
        }
        if (_bindCapture != null) return;  // binding a control: buttons mean that, not start/stop
        // Some devices appear twice (e.g. a Fanatec base): one press, one step.
        if (DateTime.UtcNow - _lastCameraJoy < TimeSpan.FromMilliseconds(120) && _lastCameraButton == (d.Vendor, d.Product, button)) return;
        if (CameraJoystickButton(d, button))
        {
            _lastCameraJoy = DateTime.UtcNow;
            _lastCameraButton = (d.Vendor, d.Product, button);
            return;
        }
        if (h.Kind != HotkeyKind.Joystick || h.JoyVendor != d.Vendor || h.JoyProduct != d.Product || h.Button != button) return;
        // Some devices appear twice (e.g. a Fanatec base); one press, one toggle.
        if (DateTime.UtcNow - _lastJoyToggle < TimeSpan.FromMilliseconds(400)) return;
        _lastJoyToggle = DateTime.UtcNow;
        Toggle(fromHotkey: true);
    }

    void AnnounceCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AnnounceToggle = AnnounceCheck.IsChecked == true;
        _settings.Save();
    }

    // ---- Azimuth: its folder, Start Azimuth, Render Aspect Ratio -------------

    // Azimuth's folder: this app's own folder when azimuth.exe is beside it,
    // else the one picked with Browse (or found from a running azimuth.exe).
    string? AzimuthDir => WideScreen.FolderNextToApp ?? (WideScreen.IsAzimuthFolder(_settings.AzimuthFolder) ? _settings.AzimuthFolder : null);

    void Window_Activated(object? sender, EventArgs e) => RefreshScreens();

    void StartAzimuth_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.StartAzimuth = StartAzimuthCheck.IsChecked == true;
        _settings.Save();
    }

    void LaunchAzimuth()
    {
        if (AzimuthDir is not string folder)
        {
            AddLog("Start Azimuth: azimuth.exe isn't in this app's folder; put the app there or pick the folder under Render Aspect Ratio.");
            return;
        }
        if (WideScreen.AzimuthRunning(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(folder, "azimuth.exe")) { WorkingDirectory = folder, UseShellExecute = true });
            AddLog("Started azimuth.exe.");
        }
        catch (Exception ex)
        {
            AddLog("Couldn't start azimuth.exe: " + ex.Message);
        }
    }

    // Render Aspect Ratio: Set puts the shim (dinput8.dll) next to azimuth.exe
    // with the game resolution in azimuth-span.ini; Clear takes both away.
    void RefreshScreens()
    {
        if (WideScreen.FolderNextToApp == null && !WideScreen.IsAzimuthFolder(_settings.AzimuthFolder)
            && WideScreen.FindRunningAzimuth() is string found)
        {
            _settings.AzimuthFolder = found;
            _settings.Save();
        }
        string? folder = AzimuthDir;

        AzimuthFolderRow.Visibility = WideScreen.FolderNextToApp == null ? Visibility.Visible : Visibility.Collapsed;
        AzimuthFolderText.Text = folder == null ? "Azimuth's folder isn't known: put this app next to azimuth.exe, or" : $"Azimuth: {folder}";

        var state = WideScreen.State(folder);
        bool isSet = state is ShimState.UpToDate or ShimState.Outdated;
        var (w, h) = isSet && folder != null ? WideScreen.ReadSize(folder) : (_settings.AspectWidth, _settings.AspectHeight);
        if (!AspectWidthBox.IsKeyboardFocused && !AspectHeightBox.IsKeyboardFocused)
        {
            AspectWidthBox.Text = w?.ToString() ?? "";
            AspectHeightBox.Text = h?.ToString() ?? "";
        }

        string text = state == ShimState.Foreign ? "Azimuth's folder has a dinput8.dll from something else, so this is left alone."
            : isSet && w is int sw && h is int sh
                ? $"Set: {sw} × {sh} ({new ScreenRow(0, 0, sw, sh, Array.Empty<int>()).Aspect}). Pick it under Aspect in Azimuth's launcher, with Fullscreen."
            : "";
        bool error = state == ShimState.Foreign;
        if (_shimMessage != null) { text = _shimMessage; error = _shimMessageIsError; }
        ShimStatus.Text = text;
        ShimStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShimStatus.Foreground = (Brush)FindResource(error ? "ErrorTextBrush" : "TextBrush");

        SetAspectButton.IsEnabled = folder != null && state != ShimState.Foreign;
        ClearAspectButton.IsEnabled = isSet;
    }

    void SetAspect_Click(object sender, RoutedEventArgs e)
    {
        if (AzimuthDir is not string folder) return;
        bool ok = int.TryParse(AspectWidthBox.Text.Trim(), out int w) & int.TryParse(AspectHeightBox.Text.Trim(), out int h);
        if (!ok || w < 640 || h < 360 || w > 16384 || h > 8640)
        {
            ShowShimMessage("Enter the width and height in pixels, e.g. 10240 and 1440.", true);
            return;
        }
        _settings.AspectWidth = w;
        _settings.AspectHeight = h;
        _settings.Save();

        // A new DLL needs Azimuth closed; a new size alone doesn't.
        string? error = WideScreen.State(folder) == ShimState.UpToDate ? null : WideScreen.Install(folder);
        if (error == null)
        {
            try { WideScreen.WriteSize(folder, w, h); }
            catch (Exception ex) { error = "Couldn't save the resolution: " + ex.Message; }
        }
        if (error == null)
        {
            AddLog($"Render Aspect Ratio: set {w} × {h} in {folder}");
            bool running = WideScreen.AzimuthRunning(folder);
            ShowShimMessage(running ? $"Set: {w} × {h}. Restart Azimuth to use it." : null, false);
        }
        else ShowShimMessage(error, true);
    }

    void ClearAspect_Click(object sender, RoutedEventArgs e)
    {
        if (AzimuthDir is not string folder) return;
        string? error = WideScreen.Remove(folder);
        if (error == null)
        {
            try { WideScreen.WriteSize(folder, null, null); } catch { }
            AddLog("Render Aspect Ratio: cleared in " + folder);
        }
        ShowShimMessage(error ?? "Cleared. Set Azimuth's Aspect back to one it offers.", error != null);
    }

    void ShowShimMessage(string? text, bool error)
    {
        _shimMessage = text;
        _shimMessageIsError = error;
        RefreshScreens();
        _shimMessage = null;
    }

    void BrowseAzimuth_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with azimuth.exe" };
        if (AzimuthDir is string current) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) != true) return;
        if (!WideScreen.IsAzimuthFolder(dlg.FolderName))
        {
            ShowShimMessage("There's no azimuth.exe in that folder.", true);
            return;
        }
        _settings.AzimuthFolder = dlg.FolderName;
        _settings.Save();
        _controlsLoaded = false;
        RefreshScreens();
    }
}
