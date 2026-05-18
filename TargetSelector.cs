namespace GpdUiSnap;

internal static class TargetSelector
{
    private const double MaxTargetDistance = 3200;
    private const double MinForwardDistance = 8;

    public static UiTarget? Pick(Point origin, int vectorX, int vectorY, IReadOnlyList<UiTarget> targets)
    {
        var magnitude = Math.Sqrt((double)vectorX * vectorX + (double)vectorY * vectorY);
        if (magnitude < 1 || targets.Count == 0)
            return null;

        var ux = vectorX / magnitude;
        var uy = vectorY / magnitude;
        var diagonal = IsDiagonal(ux, uy);
        UiTarget? best = null;
        var bestScore = double.MaxValue;

        foreach (var target in targets)
        {
            var aim = MeasureAim(origin, ux, uy, target, diagonal);
            if (aim is null)
                continue;

            var missWeight = diagonal ? 2.25 : 3.4;
            var score = aim.Value.Forward +
                        aim.Value.Miss * missWeight -
                        PriorityBonus(target) -
                        SizeBonus(target) +
                        SecondaryPenalty(target);
            if (score < bestScore)
            {
                best = target;
                bestScore = score;
            }
        }

        return best;
    }

    private static AimScore? MeasureAim(Point origin, double ux, double uy, UiTarget target, bool diagonal)
    {
        var distanceToClick = Distance(origin, target.ClickablePoint);
        if (distanceToClick < 14 || distanceToClick > MaxTargetDistance)
            return null;

        var margin = AimMargin(target);
        if (diagonal)
            margin = (int)Math.Round(margin * 1.55);

        var rect = Rectangle.Inflate(target.Bounds, margin, margin);
        if (rect.Contains(origin))
            return null;

        var intersection = RayRectangleIntersection(origin, ux, uy, rect);
        if (intersection is not null && intersection.Value >= MinForwardDistance)
            return new AimScore(intersection.Value, 0);

        var centerX = target.Bounds.Left + target.Bounds.Width / 2.0;
        var centerY = target.Bounds.Top + target.Bounds.Height / 2.0;
        var vx = centerX - origin.X;
        var vy = centerY - origin.Y;
        var forward = vx * ux + vy * uy;
        if (forward <= MinForwardDistance || forward > MaxTargetDistance)
            return null;

        var rayX = origin.X + ux * forward;
        var rayY = origin.Y + uy * forward;
        var miss = DistanceToRectangle(rayX, rayY, rect);
        var anglePenalty = miss / Math.Max(forward, 1);
        var maxMiss = diagonal ? 270 : 180;
        var maxAnglePenalty = diagonal ? 1.15 : 0.85;
        if (miss > maxMiss || anglePenalty > maxAnglePenalty)
            return null;

        return new AimScore(forward, miss);
    }

    private static double? RayRectangleIntersection(Point origin, double ux, double uy, Rectangle rect)
    {
        var minT = 0.0;
        var maxT = double.PositiveInfinity;

        if (!ClipRayAxis(origin.X, ux, rect.Left, rect.Right, ref minT, ref maxT))
            return null;
        if (!ClipRayAxis(origin.Y, uy, rect.Top, rect.Bottom, ref minT, ref maxT))
            return null;

        return maxT >= Math.Max(minT, MinForwardDistance) ? Math.Max(minT, MinForwardDistance) : null;
    }

    private static bool ClipRayAxis(double origin, double direction, double min, double max, ref double minT, ref double maxT)
    {
        if (Math.Abs(direction) < 0.0001)
            return origin >= min && origin <= max;

        var t1 = (min - origin) / direction;
        var t2 = (max - origin) / direction;
        if (t1 > t2)
            (t1, t2) = (t2, t1);

        minT = Math.Max(minT, t1);
        maxT = Math.Min(maxT, t2);
        return minT <= maxT;
    }

    private static double DistanceToRectangle(double x, double y, Rectangle rect)
    {
        var dx = Math.Max(Math.Max(rect.Left - x, 0), x - rect.Right);
        var dy = Math.Max(Math.Max(rect.Top - y, 0), y - rect.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static int PriorityBonus(UiTarget target)
    {
        return target.ControlType switch
        {
            "Button" or "SplitButton" or "Hyperlink" or "MenuItem" => 180,
            "TabItem" or "ListItem" or "TreeItem" => 120,
            "Edit" or "ComboBox" or "CheckBox" or "RadioButton" => 90,
            "Document" => -240,
            _ => 0,
        };
    }

    private static int SizeBonus(UiTarget target)
    {
        var area = Math.Max(1, target.Bounds.Width * target.Bounds.Height);
        return Math.Min(140, (int)Math.Sqrt(area));
    }

    private static int AimMargin(UiTarget target)
    {
        return target.ControlType switch
        {
            "Button" or "SplitButton" or "Hyperlink" or "MenuItem" => 18,
            "Edit" or "ComboBox" or "CheckBox" or "RadioButton" => 16,
            "TabItem" or "ListItem" or "TreeItem" or "DataItem" => 24,
            "Text" or "Image" or "Header" or "HeaderItem" => 8,
            _ => 12,
        };
    }

    private static int SecondaryPenalty(UiTarget target)
    {
        var area = Math.Max(1, target.Bounds.Width * target.Bounds.Height);
        return target.ControlType switch
        {
            "Text" or "Image" => 260,
            "Custom" or "Group" or "Pane" => 160,
            "Header" or "HeaderItem" => 120,
            "DataGrid" or "Table" or "Document" => 300,
            _ => area < 100 ? 70 : 0,
        };
    }

    private static bool IsDiagonal(double ux, double uy)
    {
        var ax = Math.Abs(ux);
        var ay = Math.Abs(uy);
        return ax > 0.36 && ay > 0.36;
    }

    private readonly record struct AimScore(double Forward, double Miss);

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
