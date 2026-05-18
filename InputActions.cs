using System.Runtime.InteropServices;

namespace GpdUiSnap;

internal static class InputActions
{
    public static void LeftClick()
    {
        var inputs = new NativeMethods.INPUT[2];
        inputs[0].type = NativeMethods.INPUT_MOUSE;
        inputs[0].union.mi.dwFlags = NativeMethods.MOUSEEVENTF_LEFTDOWN;
        inputs[1].type = NativeMethods.INPUT_MOUSE;
        inputs[1].union.mi.dwFlags = NativeMethods.MOUSEEVENTF_LEFTUP;
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    public static void KeyPress(int vk, bool shift = false)
    {
        var inputs = new NativeMethods.INPUT[shift ? 4 : 2];
        var index = 0;

        if (shift)
            FillKeyInput(ref inputs[index++], NativeMethods.VK_SHIFT, keyUp: false);

        FillKeyInput(ref inputs[index++], vk, keyUp: false);
        FillKeyInput(ref inputs[index++], vk, keyUp: true);

        if (shift)
            FillKeyInput(ref inputs[index], NativeMethods.VK_SHIFT, keyUp: true);

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void FillKeyInput(ref NativeMethods.INPUT input, int key, bool keyUp)
    {
        input.type = NativeMethods.INPUT_KEYBOARD;
        input.union.ki.wVk = (ushort)key;
        input.union.ki.dwFlags = keyUp ? (uint)NativeMethods.KEYEVENTF_KEYUP : 0U;
    }
}
