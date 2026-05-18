using System.Runtime.InteropServices;

namespace GpdUiSnap;

internal sealed class XInputService : IDisposable
{
    private readonly InputState _state;
    private readonly Action<string> _log;
    private readonly Action _changed;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _wasConnected;

    public XInputService(InputState state, Action<string> log, Action changed)
    {
        _state = state;
        _log = log;
        _changed = changed;
        _timer = new System.Windows.Forms.Timer { Interval = 25 };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        for (var i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var state) != 0)
                continue;

            if (!_wasConnected)
            {
                _wasConnected = true;
                _log($"XInput controller connected at slot {i}.");
            }

            var gamepad = state.Gamepad;
            _state.L2Held = gamepad.bLeftTrigger > 80;
            _state.RightX = ApplyDeadzone(gamepad.sThumbRX, 6500);
            _state.RightY = -ApplyDeadzone(gamepad.sThumbRY, 6500);
            _state.LastDeviceName = $"XInput slot {i}";
            _state.LastInputAt = DateTime.Now;
            _changed();
            return;
        }

        if (_wasConnected)
        {
            _wasConnected = false;
            _state.ClearController();
            _log("XInput controller disconnected or unavailable.");
            _changed();
        }
    }

    private static int ApplyDeadzone(short value, int deadzone)
    {
        if (Math.Abs(value) < deadzone)
            return 0;
        return value;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }
}
