using System.Windows.Automation;

namespace GpdUiSnap;

internal sealed class UiAutomationTargetProvider
{
    private static readonly HashSet<ControlType> CandidateTypes = new()
    {
        ControlType.Button,
        ControlType.CheckBox,
        ControlType.ComboBox,
        ControlType.Custom,
        ControlType.DataItem,
        ControlType.DataGrid,
        ControlType.Document,
        ControlType.Edit,
        ControlType.Group,
        ControlType.Header,
        ControlType.HeaderItem,
        ControlType.Hyperlink,
        ControlType.Image,
        ControlType.ListItem,
        ControlType.MenuItem,
        ControlType.Pane,
        ControlType.RadioButton,
        ControlType.SplitButton,
        ControlType.TabItem,
        ControlType.Table,
        ControlType.Text,
        ControlType.TreeItem,
    };

    private readonly Action<string> _log;
    private readonly Dictionary<IntPtr, CacheEntry> _cache = new();

    public UiAutomationTargetProvider(Action<string> log)
    {
        _log = log;
    }

    public IReadOnlyList<UiTarget> GetCachedActiveWindowTargets(TimeSpan maxAge)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return Array.Empty<UiTarget>();

        return _cache.TryGetValue(hwnd, out var entry) && DateTime.Now - entry.CapturedAt <= maxAge
            ? entry.Targets
            : Array.Empty<UiTarget>();
    }

    public IReadOnlyList<UiTarget> GetActiveWindowTargets()
    {
        try
        {
            var start = Environment.TickCount64;
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return Array.Empty<UiTarget>();

            var targets = new List<UiTarget>(768);
            AddTargetsFromHandle(hwnd, targets);
            foreach (var shellHwnd in GetShellSurfaceWindows(hwnd))
                AddTargetsFromHandle(shellHwnd, targets);

            var cleaned = CleanTargets(targets);
            _cache[hwnd] = new CacheEntry(cleaned, DateTime.Now);
            _log($"UIA scan completed in {Environment.TickCount64 - start}ms targets={cleaned.Count}");
            return cleaned;
        }
        catch (Exception ex)
        {
            _log($"UIA enumerate failed: {ex.Message}");
            return Array.Empty<UiTarget>();
        }
    }

    public void PrimeActiveWindowCache()
    {
        _ = Task.Run(GetActiveWindowTargets);
    }

    private static void AddTargetsFromHandle(IntPtr hwnd, List<UiTarget> targets)
    {
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is not null)
                targets.AddRange(FindTargetsFast(root));
        }
        catch
        {
            // Some shell surfaces are not always available through UIA.
        }
    }

    private static IEnumerable<IntPtr> GetShellSurfaceWindows(IntPtr activeHwnd)
    {
        foreach (var hwnd in EnumerateWindowsByClass("Shell_TrayWnd"))
        {
            if (hwnd != activeHwnd)
                yield return hwnd;
        }

        foreach (var hwnd in EnumerateWindowsByClass("Shell_SecondaryTrayWnd"))
        {
            if (hwnd != activeHwnd)
                yield return hwnd;
        }

        // Do not scan Progman/WorkerW here: those are desktop surfaces and can
        // expose icons or widgets hidden behind the foreground window.
    }

    private static IEnumerable<IntPtr> EnumerateWindowsByClass(string className)
    {
        var hwnd = IntPtr.Zero;
        while (true)
        {
            hwnd = FindWindowEx(IntPtr.Zero, hwnd, className, null);
            if (hwnd == IntPtr.Zero)
                yield break;
            yield return hwnd;
        }
    }

    private static List<UiTarget> FindTargetsFast(AutomationElement root)
    {
        var targets = new List<UiTarget>(512);
        var conditions = CandidateTypes
            .Select(type => new PropertyCondition(AutomationElement.ControlTypeProperty, type))
            .Cast<Condition>()
            .ToArray();
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new OrCondition(conditions));

        AutomationElementCollection elements;
        try
        {
            var cache = new CacheRequest
            {
                TreeScope = TreeScope.Element,
                AutomationElementMode = AutomationElementMode.Full,
            };
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsEnabledProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.IsKeyboardFocusableProperty);
            cache.Add(AutomationElement.IsTextPatternAvailableProperty);
            cache.Add(AutomationElement.IsValuePatternAvailableProperty);

            using (cache.Activate())
                elements = root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            Walk(root, targets, 0, 10);
            return targets;
        }

        var limit = Math.Min(elements.Count, 900);
        for (var i = 0; i < limit; i++)
            TryAddTarget(elements[i], targets, preferCached: true);
        return targets;
    }

    private static void Walk(AutomationElement element, List<UiTarget> targets, int depth, int maxDepth)
    {
        if (depth > maxDepth || targets.Count >= 800)
            return;

        TryAddTarget(element, targets);

        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(element);
        }
        catch
        {
            return;
        }

        while (child is not null)
        {
            Walk(child, targets, depth + 1, maxDepth);
            try
            {
                child = walker.GetNextSibling(child);
            }
            catch
            {
                break;
            }
        }
    }

    private static void TryAddTarget(AutomationElement element, List<UiTarget> targets, bool preferCached = false)
    {
        try
        {
            var controlType = GetProperty(element, AutomationElement.ControlTypeProperty, preferCached, ControlType.Custom);
            var name = GetProperty(element, AutomationElement.NameProperty, preferCached, "") ?? "";
            var isEnabled = GetProperty(element, AutomationElement.IsEnabledProperty, preferCached, true);
            var isOffscreen = GetProperty(element, AutomationElement.IsOffscreenProperty, preferCached, false);
            var isKeyboardFocusable = GetProperty(element, AutomationElement.IsKeyboardFocusableProperty, preferCached, false);
            var hasTextPattern = GetProperty(element, AutomationElement.IsTextPatternAvailableProperty, preferCached, false);
            var hasValuePattern = GetProperty(element, AutomationElement.IsValuePatternAvailableProperty, preferCached, false);
            if (!isEnabled || isOffscreen)
                return;
            if (!CandidateTypes.Contains(controlType))
                return;

            var rect = GetProperty(element, AutomationElement.BoundingRectangleProperty, preferCached, System.Windows.Rect.Empty);
            if (rect.IsEmpty || rect.Width < 4 || rect.Height < 4)
                return;
            var isEditableSurface = IsEditableSurface(controlType, rect, isKeyboardFocusable, hasTextPattern, hasValuePattern);
            if (!LooksUseful(controlType, name, rect, isEditableSurface))
                return;

            var clickable = new Point((int)Math.Round(rect.Left + rect.Width / 2), (int)Math.Round(rect.Top + rect.Height / 2));
            var controlTypeName = isEditableSurface
                ? "Edit"
                : controlType.ProgrammaticName.Replace("ControlType.", "");

            targets.Add(new UiTarget(
                element,
                name,
                controlTypeName,
                Rectangle.FromLTRB(
                    (int)Math.Round(rect.Left),
                    (int)Math.Round(rect.Top),
                    (int)Math.Round(rect.Right),
                    (int)Math.Round(rect.Bottom)),
                clickable));
        }
        catch
        {
            // Accessibility trees can contain stale or privileged nodes; ignore them.
        }
    }

    private static T GetProperty<T>(AutomationElement element, AutomationProperty property, bool preferCached, T fallback)
    {
        try
        {
            if (preferCached)
                return (T)element.GetCachedPropertyValue(property);
        }
        catch
        {
            // Fall through to live read.
        }

        try
        {
            return (T)element.GetCurrentPropertyValue(property);
        }
        catch
        {
            return fallback;
        }
    }

    private static IReadOnlyList<UiTarget> CleanTargets(List<UiTarget> targets)
    {
        if (targets.Count == 0)
            return targets;

        var screen = SystemInformation.VirtualScreen;
        var screenArea = Math.Max(1, screen.Width * screen.Height);
        var hasSpecificTargets = targets.Any(t => IsSpecificTarget(t, screenArea));

        var primary = targets
            .Where(t => !IsBadLargeFallback(t, screenArea, hasSpecificTargets))
            .Where(t => !IsLowValueTarget(t, screenArea))
            .GroupBy(t => $"{t.ClickablePoint.X / 8}:{t.ClickablePoint.Y / 8}:{t.ControlType}")
            .Select(g => g.OrderBy(TargetSortKey).First())
            .OrderBy(TargetSortKey)
            .ToArray();

        var collapsed = RemoveContainedSubTargets(primary);

        var cleaned = collapsed
            .Where(IsPrimaryTarget)
            .Concat(collapsed.Where(t => !IsPrimaryTarget(t)).Take(55))
            .Take(260)
            .ToArray();

        return cleaned.Length == 0 ? targets.Take(40).ToArray() : cleaned;
    }

    private static bool IsSpecificTarget(UiTarget target, int screenArea)
    {
        var area = Math.Max(1, target.Bounds.Width * target.Bounds.Height);
        return target.ControlType != "Document" &&
               target.ControlType != "Pane" &&
               area < screenArea / 4;
    }

    private static bool IsBadLargeFallback(UiTarget target, int screenArea, bool hasSpecificTargets)
    {
        if (!hasSpecificTargets)
            return false;

        var area = Math.Max(1, target.Bounds.Width * target.Bounds.Height);
        return (target.ControlType is "Document" or "Pane" or "Group" or "Custom") &&
               area > screenArea / 5 &&
               string.IsNullOrWhiteSpace(target.Name);
    }

    private static bool IsLowValueTarget(UiTarget target, int screenArea)
    {
        var area = Math.Max(1, target.Bounds.Width * target.Bounds.Height);
        var name = target.Name.Trim();
        if (name.Length == 0 && target.ControlType is "Text" or "Image" or "Custom" or "Group" or "Pane")
            return true;
        if (name.Length <= 1 && target.ControlType is "Text" or "Image")
            return true;
        if (area < 80 && target.ControlType is "Text" or "Image" or "Custom")
            return true;
        if (area > screenArea / 8 && target.ControlType is "Text" or "Image" or "Custom" or "Group" or "Pane")
            return true;
        if (IsLikelyReadOnlyMessage(target, area))
            return true;

        var lower = name.ToLowerInvariant();
        if (lower is "close" or "minimize" or "maximize" or "restore")
            return false;
        if (lower.Contains("tooltip") || lower.Contains("advertisement") || lower.Contains("sponsored"))
            return true;

        return false;
    }

    private static bool IsLikelyReadOnlyMessage(UiTarget target, int area)
    {
        var name = target.Name.Trim();
        if (name.Length == 0)
            return false;

        var lineLikeText = name.Count(char.IsWhiteSpace) >= 14 || name.Contains(". ") || name.Contains("? ") || name.Contains("! ");
        return target.ControlType switch
        {
            "Text" => name.Length > 80 && area > 900,
            "Custom" or "Group" or "Pane" => name.Length > 140 && area > 4_000 && lineLikeText,
            "ListItem" or "DataItem" => name.Length > 220 && area > 8_000 && lineLikeText,
            _ => false,
        };
    }

    private static bool IsPrimaryTarget(UiTarget target)
    {
        return target.ControlType is
            "Button" or "SplitButton" or "Hyperlink" or "MenuItem" or
            "Edit" or "ComboBox" or "CheckBox" or "RadioButton" or
            "TabItem" or "ListItem" or "TreeItem" or "DataItem";
    }

    private static UiTarget[] RemoveContainedSubTargets(UiTarget[] targets)
    {
        return targets
            .Where(candidate => !targets.Any(container => IsRedundantChild(candidate, container)))
            .ToArray();
    }

    private static bool IsRedundantChild(UiTarget candidate, UiTarget container)
    {
        if (ReferenceEquals(candidate, container))
            return false;
        if (!ContainsWithTolerance(container.Bounds, candidate.Bounds, 3))
            return false;

        var candidateArea = Math.Max(1, candidate.Bounds.Width * candidate.Bounds.Height);
        var containerArea = Math.Max(1, container.Bounds.Width * container.Bounds.Height);
        if (containerArea <= candidateArea)
            return false;

        if (IsSecondaryTarget(candidate) && (IsPrimaryTarget(container) || container.ControlType is "Custom" or "Group" or "Pane"))
            return true;

        if (IsNestedCardSubcomponent(candidate, container, candidateArea, containerArea))
            return true;

        if (IsContainedSecondaryAction(candidate, container, candidateArea, containerArea))
            return true;

        if (IsPrimaryTarget(candidate) && IsPrimaryTarget(container))
        {
            var sameName = !string.IsNullOrWhiteSpace(candidate.Name) &&
                           string.Equals(candidate.Name, container.Name, StringComparison.OrdinalIgnoreCase);
            var sameCenter = Distance(candidate.ClickablePoint, container.ClickablePoint) <= 10;
            var similarArea = containerArea <= candidateArea * 6;
            if (sameName || sameCenter || similarArea)
                return TargetSortKey(container) <= TargetSortKey(candidate);
        }

        return candidateArea < 500 && containerArea <= candidateArea * 10 && IsPrimaryTarget(container);
    }

    private static bool IsNestedCardSubcomponent(UiTarget candidate, UiTarget container, int candidateArea, int containerArea)
    {
        if (!IsCardLikeContainer(container))
            return false;
        if (IsProtectedNestedTarget(candidate))
            return false;
        if (containerArea < candidateArea * 2)
            return false;
        if (candidate.Bounds.Width > container.Bounds.Width * 0.86 && candidate.Bounds.Height > container.Bounds.Height * 0.72)
            return false;

        return candidate.ControlType is
            "Button" or "SplitButton" or "Hyperlink" or "MenuItem" or
            "Text" or "Image" or "Header" or "HeaderItem" or
            "Custom" or "Group" or "Pane";
    }

    private static bool IsCardLikeContainer(UiTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Name))
            return false;

        return target.ControlType is "ListItem" or "DataItem" or "Custom" or "Group" or "Pane";
    }

    private static bool IsProtectedNestedTarget(UiTarget target)
    {
        if (target.ControlType is "Edit" or "ComboBox" or "CheckBox" or "RadioButton" or "TabItem" or "TreeItem")
            return true;

        var name = target.Name.Trim().ToLowerInvariant();
        return name is "close" or "minimize" or "maximize" or "restore" or "start";
    }

    private static bool IsContainedSecondaryAction(UiTarget candidate, UiTarget container, int candidateArea, int containerArea)
    {
        if (candidate.ControlType is not ("Button" or "SplitButton" or "MenuItem"))
            return false;
        if (container.ControlType is not ("ListItem" or "DataItem" or "Custom" or "Group" or "Pane"))
            return false;
        if (containerArea < candidateArea * 4)
            return false;

        var name = candidate.Name.Trim().ToLowerInvariant();
        if (name.Length == 0)
            return false;

        return name is "archive" or "delete" or "remove" or "dismiss" or "hide" or "more" or "more actions" or "options" ||
               name.Contains("archive") ||
               name.Contains("delete") ||
               name.Contains("remove") ||
               name.Contains("dismiss") ||
               name.Contains("more actions");
    }

    private static bool IsSecondaryTarget(UiTarget target)
    {
        return target.ControlType is "Text" or "Image" or "Header" or "HeaderItem" or "Custom" or "Group" or "Pane";
    }

    private static bool ContainsWithTolerance(Rectangle outer, Rectangle inner, int tolerance)
    {
        return inner.Left >= outer.Left - tolerance &&
               inner.Top >= outer.Top - tolerance &&
               inner.Right <= outer.Right + tolerance &&
               inner.Bottom <= outer.Bottom + tolerance;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static int TargetSortKey(UiTarget target)
    {
        var area = Math.Max(1, target.Bounds.Width * target.Bounds.Height);
        var priority = target.ControlType switch
        {
            "Button" or "SplitButton" or "Hyperlink" or "MenuItem" => 0,
            "Edit" or "ComboBox" or "CheckBox" or "RadioButton" => 1,
            "TabItem" or "ListItem" or "TreeItem" or "DataItem" => 2,
            "Header" or "HeaderItem" => 4,
            "Custom" or "Group" or "Pane" => 5,
            "Image" or "Text" => 7,
            "DataGrid" or "Table" => 6,
            "Document" => 9,
            _ => 5,
        };

        return priority * 1_000_000 + area;
    }

    private static bool LooksUseful(ControlType controlType, string name, System.Windows.Rect rect, bool isEditableSurface)
    {
        var hasName = !string.IsNullOrWhiteSpace(name);
        var area = rect.Width * rect.Height;
        if (area > SystemInformation.VirtualScreen.Width * SystemInformation.VirtualScreen.Height / 3.0 && !hasName)
            return false;
        if (isEditableSurface)
            return true;

        return controlType.ProgrammaticName.Replace("ControlType.", "") switch
        {
            "Text" => hasName && rect.Width >= 24 && rect.Height >= 10,
            "Image" => hasName && rect.Width >= 16 && rect.Height >= 16,
            "Header" or "HeaderItem" => hasName && rect.Width >= 12 && rect.Height >= 8,
            "Custom" or "Group" or "Pane" => hasName && rect.Width >= 18 && rect.Height >= 14,
            "Document" => false,
            _ => true,
        };
    }

    private static bool IsEditableSurface(
        ControlType controlType,
        System.Windows.Rect rect,
        bool isKeyboardFocusable,
        bool hasTextPattern,
        bool hasValuePattern)
    {
        if (controlType == ControlType.Edit)
            return true;
        if (controlType != ControlType.Document &&
            controlType != ControlType.Custom &&
            controlType != ControlType.Group &&
            controlType != ControlType.Pane)
            return false;
        if (!isKeyboardFocusable || (!hasTextPattern && !hasValuePattern))
            return false;
        if (rect.Width < 80 || rect.Height < 22)
            return false;
        if (rect.Height > 360)
            return false;

        var screen = SystemInformation.VirtualScreen;
        var area = rect.Width * rect.Height;
        return area < screen.Width * screen.Height / 5.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? className, string? windowName);

    private sealed record CacheEntry(IReadOnlyList<UiTarget> Targets, DateTime CapturedAt);
}
