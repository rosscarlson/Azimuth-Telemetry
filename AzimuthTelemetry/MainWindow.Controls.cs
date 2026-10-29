using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AzimuthTelemetry.Desktop.Azimuth;
using AzimuthTelemetry.Desktop.Display;
using AzimuthTelemetry.Desktop.Input;

namespace AzimuthTelemetry.Desktop;

// The Controls tab: Azimuth's racing wheel bindings (data\azimuth.ini).
public partial class MainWindow
{
    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ControlsPanel == null) return;  // while the window loads
        bool controls = sender == ControlsTab;
        if (!controls && _bindCapture != null) EndBindCapture(null, false);
        TelemetryPanel.Visibility = controls ? Visibility.Collapsed : Visibility.Visible;
        ControlsPanel.Visibility = controls ? Visibility.Visible : Visibility.Collapsed;
        if (controls) ShowControls();
        else _diagramTick.Stop();
    }

    void ShowControls()
    {
        if (ControlsTab.IsChecked != true) return;
        string? folder = AzimuthDir;
        bool usable = WideScreen.IsAzimuthFolder(folder);

        ControlsIntro.Text = !usable ? "Put this app in Azimuth's folder, or pick the folder under Render Aspect Ratio on the Telemetry tab."
            : $"Azimuth's racing wheel bindings in {folder}, one set for every game it runs. Bind… listens for the control; Save writes them with Azimuth closed.";
        var show = usable ? Visibility.Visible : Visibility.Collapsed;
        DiagramBox.Visibility = ControlsSettingsRow.Visibility = BindingBox.Visibility = ControlsActions.Visibility = CaptureText.Visibility = show;
        if (!usable) { _diagramTick.Stop(); return; }

        if (!_controlsLoaded) LoadControls();
        else if (_wheels == null) EnsureWheels();
        RefreshControlsStatus();
        _diagramTick.Start();
        RefreshDiagram();
    }

    // The picture at the top: each binding (as edited, saved or not) against
    // the controllers right now.
    void RefreshDiagram()
    {
        if (_wheels == null || !IsVisible) return;
        List<WheelSnapshot> shots;
        try { shots = _wheels.Read(); }
        catch { return; }
        var values = new Dictionary<string, double>();
        foreach (var row in _bindings)
        {
            if (!row.IsSet) continue;
            double rest = row.Kind == CaptureKind.Steering ? 0.5 : 0;
            values[row.Key] = AzimuthControls.Evaluate(row.Value, shots, rest) ?? rest;
        }
        int rotation = int.TryParse(RotationBox.Text.Trim(), out int r) ? Math.Clamp(r, 180, 2520) : 900;
        Diagram.Show(values, rotation);
    }

    void EnsureWheels()
    {
        _wheels ??= new WheelInput(new WindowInteropHelper(this).Handle);
        try { _wheels.Refresh(); }
        catch (Exception ex) { ShowControlsMessage("Couldn't read the controllers: " + ex.Message, true); }
        var wheels = _wheels;
        foreach (var row in _bindings) row.SetDescriber(v => AzimuthControls.Describe(v, wheels.Devices));
    }

    void LoadControls()
    {
        if (AzimuthDir is not string folder) return;
        EnsureWheels();
        AzimuthControls.Loaded? loaded;
        try { loaded = AzimuthControls.Load(folder); }
        catch (Exception ex) { ShowControlsMessage("Couldn't read azimuth.ini: " + ex.Message, true); return; }
        if (loaded == null) { ShowControlsMessage("There's no data\\azimuth.ini yet; start Azimuth once first.", true); return; }
        foreach (var row in _bindings) row.Value = loaded.Bindings.GetValueOrDefault(row.Key, "");
        _initializing = true;
        RotationBox.Text = loaded.Rotation ?? "900";
        ForceBox.Text = loaded.Force ?? "100";
        _initializing = false;
        _controlsLoaded = true;
        _controlsDirty = false;
        _controlsMessage = null;
    }

    void RefreshControlsStatus()
    {
        bool running = AzimuthDir is string f && WideScreen.AzimuthRunning(f);
        string text;
        bool error = false;
        if (_controlsMessage != null) { text = _controlsMessage; error = _controlsMessageIsError; }
        else if (_controlsDirty && running) { text = "Unsaved changes. Close Azimuth to save them (it rewrites its settings when it closes)."; error = true; }
        else if (_controlsDirty) text = "Unsaved changes.";
        else text = $"{_wheels?.Devices.Count ?? 0} controllers connected.";
        ControlsStatus.Text = text;
        ControlsStatus.Foreground = (Brush)FindResource(error ? "ErrorTextBrush" : "TextBrush");
        SaveControlsButton.IsEnabled = _controlsDirty && _bindCapture == null;
        if (_bindCapture == null)
            CaptureText.Text = "Steering: turn right. Pedals: press fully, then let go. Buttons: press one (a hat or D-pad direction works too). Esc cancels. Don't re-detect on Azimuth's Controls page afterwards, or it may replace these.";
    }

    void ShowControlsMessage(string text, bool error)
    {
        _controlsMessage = text;
        _controlsMessageIsError = error;
        RefreshControlsStatus();
    }

    void Bind_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BindingRow row) return;
        if (_bindCapture != null) EndBindCapture(null, false);
        EnsureWheels();
        if (_wheels == null || _wheels.Devices.Count == 0)
        {
            ShowControlsMessage("No controllers found. Plug the wheel in and try again.", true);
            _controlsMessage = null;
            return;
        }
        _controlsMessage = null;
        _bindRow = row;
        _bindCapture = new BindingCapture(row.Kind, _wheels.Read());
        _bindDeadline = DateTime.UtcNow.AddSeconds(15);
        row.IsCapturing = true;
        RefreshControlsStatus();
        CaptureText.Text = $"{row.Label}: {_bindCapture.Prompt}";
        _captureTick.Start();
    }

    void CaptureStep()
    {
        if (_bindCapture == null || _bindRow == null || _wheels == null) { _captureTick.Stop(); return; }
        string? result = _bindCapture.Feed(_wheels.Read());
        CaptureText.Text = $"{_bindRow.Label}: {_bindCapture.Prompt}";
        if (result != null)
        {
            var row = _bindRow;
            row.Value = result;
            _controlsDirty = true;
            EndBindCapture(null, false);
            ShowControlsMessage($"{row.Label}: {row.Display}. Save when you're done.", false);
            _controlsMessage = null;
        }
        else if (DateTime.UtcNow > _bindDeadline) EndBindCapture("Nothing was moved or pressed; Bind… to try again.", true);
    }

    void EndBindCapture(string? message, bool error)
    {
        _captureTick.Stop();
        if (_bindRow != null) _bindRow.IsCapturing = false;
        _bindRow = null;
        _bindCapture = null;
        if (message != null) ShowControlsMessage(message, error);
        else RefreshControlsStatus();
        _controlsMessage = null;  // shown once; the next refresh shows the usual status
    }

    void ClearBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BindingRow row) return;
        row.Value = "";
        _controlsDirty = true;
        RefreshControlsStatus();
    }

    void ControlsNumber_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not TextBox box) return;
        bool rotation = box == RotationBox;
        if (!int.TryParse(box.Text.Trim(), out int v)) v = rotation ? 900 : 100;
        v = rotation ? Math.Clamp(v, 180, 2520) : Math.Clamp(v, 0, 200);
        string was = box.Text;
        box.Text = v.ToString();
        var loaded = AzimuthDir is string f ? AzimuthControls.Load(f) : null;
        if (loaded == null || (rotation ? loaded.Rotation : loaded.Force) != box.Text || was != box.Text) _controlsDirty = true;
        RefreshControlsStatus();
    }

    void SaveControls_Click(object sender, RoutedEventArgs e)
    {
        if (AzimuthDir is not string folder) return;
        if (WideScreen.AzimuthRunning(folder))
        {
            ShowControlsMessage("Close Azimuth first; it rewrites its settings when it closes, which would undo these.", true);
            _controlsMessage = null;
            return;
        }
        var keys = _bindings.ToDictionary(r => "wheel_" + r.Key, r => r.Value);
        keys["wheel_rotation"] = RotationBox.Text.Trim();
        keys["wheel_force"] = ForceBox.Text.Trim();
        try
        {
            AzimuthControls.Save(folder, keys);
            _controlsDirty = false;
            ShowControlsMessage("Saved to Azimuth. Start the game to use them.", false);
            AddLog("Controls: saved Azimuth's wheel bindings in " + folder);
        }
        catch (Exception ex)
        {
            ShowControlsMessage("Couldn't save azimuth.ini: " + ex.Message, true);
        }
        _controlsMessage = null;
    }

    void ReloadControls_Click(object sender, RoutedEventArgs e)
    {
        if (_bindCapture != null) EndBindCapture(null, false);
        _controlsLoaded = false;
        ShowControls();
    }
}
