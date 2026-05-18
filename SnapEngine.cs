using System.Windows.Automation;

namespace GpdUiSnap;

internal sealed class SnapEngine
{
    private readonly UiAutomationTargetProvider _targets;
    private readonly Action<string> _log;
    private DateTime _lastSnap = DateTime.MinValue;
    private UiTarget? _lastTarget;

    public SnapEngine(UiAutomationTargetProvider targets, Action<string> log)
    {
        _targets = targets;
        _log = log;
    }

    public UiTarget? LastTarget => _lastTarget;

    public IReadOnlyList<UiTarget> GetTargets() => _targets.GetActiveWindowTargets();

    public UiTarget? PickTarget(Point cursor, int vectorX, int vectorY)
    {
        var magnitude = Math.Sqrt((double)vectorX * vectorX + (double)vectorY * vectorY);
        if (magnitude < 9000)
            return null;

        var ux = vectorX / magnitude;
        var uy = vectorY / magnitude;
        UiTarget? best = null;
        var bestScore = double.MaxValue;

        foreach (var target in _targets.GetActiveWindowTargets())
        {
            var vx = target.ClickablePoint.X - cursor.X;
            var vy = target.ClickablePoint.Y - cursor.Y;
            var forward = vx * ux + vy * uy;
            if (forward <= 12)
                continue;

            var perpendicular = Math.Abs(vx * uy - vy * ux);
            var distance = Math.Sqrt((double)vx * vx + (double)vy * vy);
            if (distance > 2200)
                continue;

            var score = forward + perpendicular * 1.75;
            if (score < bestScore)
            {
                best = target;
                bestScore = score;
            }
        }

        return best;
    }

    public bool Snap(Point cursor, int vectorX, int vectorY, SnapAction action)
    {
        if ((DateTime.Now - _lastSnap).TotalMilliseconds < 220)
            return false;

        var target = PickTarget(cursor, vectorX, vectorY);
        if (target is null)
            return false;

        _lastTarget = target;
        _lastSnap = DateTime.Now;
        NativeMethods.SetCursorPos(target.ClickablePoint.X, target.ClickablePoint.Y);

        if (action is SnapAction.Focus or SnapAction.ClickOnSnap)
            TryFocus(target);
        if (action == SnapAction.ClickOnSnap)
            SendLeftClick();

        _log($"Snap target: {target.ControlType} \"{target.Name}\" at {target.ClickablePoint.X},{target.ClickablePoint.Y}");
        return true;
    }

    public bool FocusOnly(UiTarget target)
    {
        if ((DateTime.Now - _lastSnap).TotalMilliseconds < 160)
            return false;

        try
        {
            target.Element.SetFocus();
            _lastTarget = target;
            _lastSnap = DateTime.Now;
            _log($"Focus target: {target.ControlType} \"{target.Name}\" at {target.ClickablePoint.X},{target.ClickablePoint.Y}");
            return true;
        }
        catch
        {
            _log($"Focus failed: {target.ControlType} \"{target.Name}\"");
            return false;
        }
    }

    private void TryFocus(UiTarget target)
    {
        try
        {
            target.Element.SetFocus();
        }
        catch
        {
            // Some controls are clickable but not focusable.
        }

        try
        {
            if (target.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) && pattern is InvokePattern invoke)
                invoke.Invoke();
        }
        catch
        {
            // Invoke is optional; cursor movement is the primary behavior.
        }
    }

    private static void SendLeftClick()
    {
        const uint down = 0x0002;
        const uint up = 0x0004;
        var inputs = new NativeMethods.INPUT[2];
        inputs[0].type = NativeMethods.INPUT_MOUSE;
        inputs[0].union.mi.dwFlags = down;
        inputs[1].type = NativeMethods.INPUT_MOUSE;
        inputs[1].union.mi.dwFlags = up;
        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }
}

internal enum SnapAction
{
    MoveOnly,
    Focus,
    ClickOnSnap,
    ClickOnL2Release,
}
