using System.Runtime.InteropServices;

namespace GpdUiSnap;

internal sealed class MouseConfirmClickService : IDisposable
{
    private const uint LlmhfInjected = 0x00000001;

    private readonly Func<bool> _isNavigationActive;
    private readonly Action _confirm;
    private readonly Action<string> _log;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private bool _suppressPhysicalLeftUp;

    public MouseConfirmClickService(Func<bool> isNavigationActive, Action confirm, Action<string> log)
    {
        _isNavigationActive = isNavigationActive;
        _confirm = confirm;
        _log = log;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
            return;

        var module = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, module, 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        _log("Mouse confirm hook installed.");
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

        var message = wParam.ToInt32();
        if (message is not (NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP))
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        if ((data.flags & LlmhfInjected) != 0)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        if (message == NativeMethods.WM_LBUTTONDOWN && _isNavigationActive())
        {
            _suppressPhysicalLeftUp = true;
            _confirm();
            return new IntPtr(1);
        }

        if (message == NativeMethods.WM_LBUTTONUP && (_suppressPhysicalLeftUp || _isNavigationActive()))
        {
            _suppressPhysicalLeftUp = false;
            return new IntPtr(1);
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
