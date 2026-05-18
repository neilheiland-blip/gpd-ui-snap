using System.Runtime.InteropServices;

namespace GpdUiSnap;

internal sealed class RawInputService
{
    private readonly IntPtr _hwnd;
    private readonly Dictionary<IntPtr, string> _deviceNames = new();
    private readonly InputState _state;
    private readonly Action<string> _log;
    private readonly Action _changed;
    private readonly Action<ushort, string>? _mouseButtonChanged;

    public RawInputService(IntPtr hwnd, InputState state, Action<string> log, Action changed, Action<ushort, string>? mouseButtonChanged = null)
    {
        _hwnd = hwnd;
        _state = state;
        _log = log;
        _changed = changed;
        _mouseButtonChanged = mouseButtonChanged;
    }

    public void Register()
    {
        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _hwnd },
        };

        if (!NativeMethods.RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        _log("Raw Input registered for mouse, joystick/gamepad, and keyboard usages.");
    }

    public void HandleInput(IntPtr lParam)
    {
        uint size = 0;
        NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>());
        if (size == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var readSize = size;
            var result = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, buffer, ref readSize, Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>());
            if (result == uint.MaxValue)
                return;

            var raw = Marshal.PtrToStructure<NativeMethods.RAWINPUT>(buffer);
            var deviceName = GetDeviceName(raw.header.hDevice);
            _state.LastDeviceName = deviceName;
            _state.LastInputAt = DateTime.Now;

            if (raw.header.dwType == NativeMethods.RIM_TYPEMOUSE)
            {
                if (raw.data.mouse.ulButtons != 0)
                {
                    _state.SetMouseButtons(raw.data.mouse.ulButtons, deviceName);
                    _log($"Mouse buttons flags=0x{_state.LastMouseButtonFlags:X4} data=0x{_state.LastMouseButtonData:X4} dev={TrimDevice(deviceName)}");
                    _mouseButtonChanged?.Invoke(_state.LastMouseButtonFlags, deviceName);
                }

                if (raw.data.mouse.lLastX != 0 || raw.data.mouse.lLastY != 0)
                {
                    _state.AddMouseDelta(raw.data.mouse.lLastX, raw.data.mouse.lLastY, deviceName);
                }
            }
            else if (raw.header.dwType == NativeMethods.RIM_TYPEKEYBOARD)
            {
                _log($"Keyboard raw vk=0x{raw.data.keyboard.VKey:X2} msg=0x{raw.data.keyboard.Message:X4} dev={TrimDevice(deviceName)}");
            }
            else
            {
                _log($"HID raw type={raw.header.dwType} bytes={readSize} dev={TrimDevice(deviceName)}");
            }

            _changed();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string GetDeviceName(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero)
            return "";
        if (_deviceNames.TryGetValue(hDevice, out var cached))
            return cached;

        uint size = 0;
        NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0)
            return "";

        var buffer = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, buffer, ref size);
            var name = Marshal.PtrToStringUni(buffer) ?? "";
            _deviceNames[hDevice] = name;
            return name;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string TrimDevice(string device)
    {
        if (device.Length <= 90)
            return device;
        return device[..90] + "...";
    }
}
