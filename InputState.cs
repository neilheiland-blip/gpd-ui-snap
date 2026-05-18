namespace GpdUiSnap;

internal sealed class InputState
{
    public bool Enabled { get; set; } = true;
    public bool L2Held { get; set; }
    public int RightX { get; set; }
    public int RightY { get; set; }
    public int LastMouseDx { get; set; }
    public int LastMouseDy { get; set; }
    public ushort LastMouseButtonFlags { get; private set; }
    public ushort LastMouseButtonData { get; private set; }
    public DateTime LastMouseButtonAt { get; private set; } = DateTime.MinValue;
    public int AccumulatedMouseDx { get; private set; }
    public int AccumulatedMouseDy { get; private set; }
    public string LastDeviceName { get; set; } = "";
    public string LastMouseDeviceName { get; set; } = "";
    public DateTime LastInputAt { get; set; } = DateTime.MinValue;
    public DateTime LastMouseMoveAt { get; private set; } = DateTime.MinValue;
    public DateTime LastNamedMouseMoveAt { get; private set; } = DateTime.MinValue;

    public double RightMagnitude => Math.Sqrt((double)RightX * RightX + (double)RightY * RightY);

    public void ClearController()
    {
        L2Held = false;
        RightX = 0;
        RightY = 0;
    }

    public void AddMouseDelta(int dx, int dy, string deviceName)
    {
        LastMouseDx = dx;
        LastMouseDy = dy;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            LastMouseDeviceName = deviceName;
            LastNamedMouseMoveAt = DateTime.Now;
        }
        AccumulatedMouseDx += dx;
        AccumulatedMouseDy += dy;
        LastMouseMoveAt = DateTime.Now;
    }

    public void SetMouseButtons(uint buttons, string deviceName)
    {
        LastMouseButtonFlags = (ushort)(buttons & 0xFFFF);
        LastMouseButtonData = (ushort)(buttons >> 16);
        LastMouseButtonAt = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            LastMouseDeviceName = deviceName;
            LastNamedMouseMoveAt = DateTime.Now;
        }
    }

    public (int dx, int dy) ConsumeMouseDelta()
    {
        var delta = (AccumulatedMouseDx, AccumulatedMouseDy);
        AccumulatedMouseDx = 0;
        AccumulatedMouseDy = 0;
        return delta;
    }

    public (int dx, int dy) PeekMouseDelta()
    {
        return (AccumulatedMouseDx, AccumulatedMouseDy);
    }
}
