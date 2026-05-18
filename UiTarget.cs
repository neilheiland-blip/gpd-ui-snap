using System.Windows.Automation;

namespace GpdUiSnap;

internal sealed record UiTarget(
    AutomationElement Element,
    string Name,
    string ControlType,
    Rectangle Bounds,
    Point ClickablePoint);
