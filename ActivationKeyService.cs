using System.Runtime.InteropServices;

namespace GpdUiSnap;

internal sealed class ActivationKeyService : IDisposable
{
    private readonly int _activationVk;
    private readonly Action<bool> _changed;
    private readonly Action<string> _log;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private bool _held;

    public ActivationKeyService(int activationVk, Action<bool> changed, Action<string> log)
    {
        _activationVk = activationVk;
        _changed = changed;
        _log = log;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
            return;

        var module = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        _log($"Activation key hook installed for VK 0x{_activationVk:X2}.");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (data.vkCode != _activationVk)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var message = wParam.ToInt32();
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        if (isDown && !_held)
        {
            _held = true;
            _log($"Activation down VK 0x{_activationVk:X2}");
            _changed(true);
        }
        else if (isUp && _held)
        {
            _held = false;
            _log($"Activation up VK 0x{_activationVk:X2}");
            _changed(false);
        }

        return new IntPtr(1);
    }
}
