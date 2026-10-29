using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HorizonTelemetry.Desktop.Input;

public sealed record JoystickDevice(int Id, ushort Vendor, ushort Product, string Name);

// Watches every game controller (wheel, button box, gamepad) for button
// presses through winmm, which keeps working while another app (the game)
// has focus and doesn't interfere with the game's own DirectInput access.
// winmm reports up to 32 buttons per device.
public sealed class JoystickWatcher : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOYINFOEX
    {
        public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }

    const uint JOY_RETURNBUTTONS = 0x80;

    [DllImport("winmm.dll")] static extern uint joyGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint joyGetDevCapsW(UIntPtr id, ref JOYCAPSW caps, uint size);
    [DllImport("winmm.dll")] static extern uint joyGetPosEx(uint id, ref JOYINFOEX info);

    readonly Thread _thread;
    volatile bool _stop;
    volatile JoystickDevice[] _devices = Array.Empty<JoystickDevice>();

    // Raised on the watcher thread with the device and the 0-based button.
    public event Action<JoystickDevice, int>? ButtonDown;

    public IReadOnlyList<JoystickDevice> Devices => _devices;

    public JoystickWatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Joysticks" };
        _thread.Start();
    }

    void Run()
    {
        var last = new Dictionary<int, uint>();
        DateTime nextEnumerate = DateTime.MinValue;
        while (!_stop)
        {
            // Asking winmm about unplugged ids is slow, so only re-list
            // devices every few seconds.
            if (DateTime.UtcNow >= nextEnumerate)
            {
                _devices = Enumerate();
                nextEnumerate = DateTime.UtcNow.AddSeconds(5);
            }
            foreach (var d in _devices)
            {
                var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
                if (joyGetPosEx((uint)d.Id, ref info) != 0) continue;
                uint was = last.TryGetValue(d.Id, out var w) ? w : info.dwButtons;  // no presses on first sight
                uint pressed = info.dwButtons & ~was;
                last[d.Id] = info.dwButtons;
                for (int b = 0; pressed != 0 && b < 32; b++, pressed >>= 1)
                    if ((pressed & 1) != 0) ButtonDown?.Invoke(d, b);
            }
            Thread.Sleep(25);
        }
    }

    static JoystickDevice[] Enumerate()
    {
        var list = new List<JoystickDevice>();
        uint n = Math.Min(joyGetNumDevs(), 16);
        for (uint id = 0; id < n; id++)
        {
            var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
            if (joyGetPosEx(id, ref info) != 0) continue;
            var caps = new JOYCAPSW();
            if (joyGetDevCapsW((UIntPtr)id, ref caps, (uint)Marshal.SizeOf<JOYCAPSW>()) != 0) continue;
            list.Add(new JoystickDevice((int)id, caps.wMid, caps.wPid, NameOf(caps.wMid, caps.wPid, caps.szPname)));
        }
        return list.ToArray();
    }

    // The product name Windows shows in "Set up USB game controllers".
    static string NameOf(ushort vid, ushort pid, string fallback)
    {
        string key = $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue("OEMName") is string s && s.Trim().Length > 0) return s.Trim();
            }
            catch { }
        }
        return string.IsNullOrWhiteSpace(fallback) || fallback.Contains("joystick driver", StringComparison.OrdinalIgnoreCase)
            ? $"Controller {vid:X4}:{pid:X4}"
            : fallback.Trim();
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }
}
