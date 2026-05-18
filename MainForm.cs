using System.IO;

namespace GpdUiSnap;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly InputState _state = new();
    private readonly UiAutomationTargetProvider _targetProvider;
    private readonly TargetOverlayForm _overlay = new();
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly Label _statusLabel = new() { Dock = DockStyle.Top, Height = 40 };
    private readonly Label _inputLabel = new() { Dock = DockStyle.Top, Height = 40 };
    private readonly CheckBox _enableBox = new() { Text = "Enabled", Checked = true, AutoSize = true };
    private readonly CheckBox _clickOnReleaseBox = new() { Text = "Release confirms", Checked = true, Enabled = false, AutoSize = true };
    private readonly ComboBox _modeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _scanButton = new() { Text = "Scan Active Window" };
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    private readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GpdUiSnap",
        "gpd-ui-snap.log");

    private RawInputService? _rawInput;
    private ActivationKeyService? _activation;
    private MouseConfirmClickService? _mouseConfirm;
    private NavigationSession? _session;
    private bool _activationHeld;
    private bool _mouseButtonActivationHeld;
    private DateTime _lastNavigateAt = DateTime.MinValue;
    private DateTime _lastFallbackAt = DateTime.MinValue;
    private DateTime _lastCachePrimeAt = DateTime.MinValue;
    private DateTime _lastSessionRefreshAt = DateTime.MinValue;
    private bool _sessionRefreshInFlight;
    private int _sessionGeneration;

    public MainForm()
    {
        Text = "GPD UI Navigator";
        Width = 920;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;

        _targetProvider = new UiAutomationTargetProvider(Log);

        _modeCombo.Items.AddRange(Enum.GetNames<NavigationMode>());
        _modeCombo.SelectedItem = _settings.NavigationMode.ToString();

        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8), AutoSize = false };
        controls.Controls.Add(_enableBox);
        controls.Controls.Add(_clickOnReleaseBox);
        controls.Controls.Add(new Label { Text = "Mode:", AutoSize = true, Padding = new Padding(12, 7, 0, 0) });
        controls.Controls.Add(_modeCombo);
        controls.Controls.Add(_scanButton);

        Controls.Add(_log);
        Controls.Add(_inputLabel);
        Controls.Add(_statusLabel);
        Controls.Add(controls);

        _enableBox.CheckedChanged += (_, _) => _state.Enabled = _enableBox.Checked;
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (Enum.TryParse<NavigationMode>(_modeCombo.SelectedItem?.ToString(), out var mode))
                _settings.NavigationMode = mode;
        };
        _scanButton.Click += (_, _) => ScanTargets();
        _tick.Tick += (_, _) => OnTick();
        _tick.Tick += (_, _) => RefreshSessionTargetsWhenActive();
        _tick.Tick += (_, _) => PrimeTargetCacheWhenIdle();

        ConfigureTray();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _rawInput = new RawInputService(Handle, _state, Log, UpdateLabels, OnMouseButtonChanged);
        _rawInput.Register();
        _activation = new ActivationKeyService(_settings.ActivationVk, OnActivationChanged, Log);
        _activation.Start();
        _mouseConfirm = new MouseConfirmClickService(IsNavigationClickActive, ConfirmSelectionFromLeftClick, Log);
        _mouseConfirm.Start();
        NativeMethods.RegisterHotKey(Handle, 1, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_F12);
        _tick.Start();
        Log($"Started. Hold {KeyName(_settings.ActivationVk)} to navigate. Ctrl+Alt+F12 toggles enabled.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        EndSession(confirm: false);
        _tick.Stop();
        _mouseConfirm?.Dispose();
        _activation?.Dispose();
        NativeMethods.UnregisterHotKey(Handle, 1);
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        _settings.Save();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_INPUT)
        {
            _rawInput?.HandleInput(m.LParam);
            return;
        }

        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            _state.Enabled = !_state.Enabled;
            _enableBox.Checked = _state.Enabled;
            if (!_state.Enabled)
                EndSession(confirm: false);
            Log(_state.Enabled ? "Enabled." : "Disabled.");
            return;
        }

        base.WndProc(ref m);
    }

    private void OnActivationChanged(bool held)
    {
        if (IsDisposed)
            return;

        BeginInvoke(() =>
        {
            _activationHeld = held;
            if (!_state.Enabled)
                return;

            if (held)
                BeginSession();
            else
                EndSession(confirm: _settings.ClickOnRelease);
        });
    }

    private void OnMouseButtonChanged(ushort flags, string deviceName)
    {
        if (_settings.ActivationSource != ActivationSource.KeyboardOrGpdMouseButton)
            return;
        if (!IsGpdMouseDevice(deviceName))
            return;

        var down = (flags & _settings.ActivationMouseDownFlag) != 0;
        var up = (flags & _settings.ActivationMouseUpFlag) != 0;
        if (!down && !up)
            return;

        BeginInvoke(() =>
        {
            if (!_state.Enabled)
                return;

            if (down && !_mouseButtonActivationHeld)
            {
                _mouseButtonActivationHeld = true;
                _activationHeld = true;
                Log($"Mouse-button activation down flags=0x{flags:X4}");
                BeginSession();
            }
            else if (up && _mouseButtonActivationHeld)
            {
                _mouseButtonActivationHeld = false;
                _activationHeld = false;
                Log($"Mouse-button activation up flags=0x{flags:X4}");
                EndSession(confirm: false);
            }
        });
    }

    private void BeginSession()
    {
        if (_session is not null)
            return;

        if (!NativeMethods.GetCursorPos(out var cursorNative))
            return;

        var anchor = new Point(cursorNative.X, cursorNative.Y);
        var session = new NavigationSession(anchor);
        var generation = ++_sessionGeneration;
        _session = session;
        var cachedTargets = _targetProvider.GetCachedActiveWindowTargets(TimeSpan.FromSeconds(1));
        if (cachedTargets.Count > 0)
        {
            session.SetTargets(cachedTargets);
            _overlay.ShowTargets(cachedTargets, selected: null);
            Log($"Navigation active immediately from cache. targets={cachedTargets.Count} anchor={anchor.X},{anchor.Y}");
        }
        else
        {
            _overlay.ShowMessage("Navigating...");
            Log($"Navigation active immediately. anchor={anchor.X},{anchor.Y}");
        }
        _lastNavigateAt = DateTime.MinValue;
        _lastSessionRefreshAt = DateTime.MinValue;
        UpdateLabels();

        _ = Task.Run(() => _targetProvider.GetActiveWindowTargets()).ContinueWith(task =>
        {
            if (IsDisposed)
                return;

            BeginInvoke(() =>
            {
                if (_session != session || generation != _sessionGeneration)
                    return;

                var targets = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : Array.Empty<UiTarget>();
                session.SetTargets(targets);
                _overlay.ShowTargets(targets, selected: null);
                Log($"Navigation targets ready. targets={targets.Count}");
                UpdateLabels();
            });
        });
    }

    private void EndSession(bool confirm)
    {
        if (_session is null)
            return;

        var selected = _session.SelectedTarget;
        _overlay.HideTargets();
        _session = null;
        _sessionGeneration++;
        _state.ConsumeMouseDelta();

        if (confirm && selected is not null)
        {
            NativeMethods.SetCursorPos(selected.ClickablePoint.X, selected.ClickablePoint.Y);
            TryFocus(selected);
            InputActions.LeftClick();
            Log($"Clicked {DescribeTarget(selected)}");
        }
        else
        {
            Log("Navigation inactive.");
        }

        UpdateLabels();
    }

    private void OnTick()
    {
        var (dx, dy) = _state.PeekMouseDelta();
        UpdateLabels();

        if (!_state.Enabled || !_activationHeld || _session is null)
        {
            _state.ConsumeMouseDelta();
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor) && new Point(cursor.X, cursor.Y) != _session.Anchor)
            NativeMethods.SetCursorPos(_session.Anchor.X, _session.Anchor.Y);

        var magnitude = Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (magnitude < _settings.MouseVectorThreshold)
            return;

        var repeatMs = IsDiagonalVector(dx, dy)
            ? Math.Min(_settings.NavigateRepeatMs, 48)
            : _settings.NavigateRepeatMs;
        if ((DateTime.Now - _lastNavigateAt).TotalMilliseconds < repeatMs)
            return;

        (dx, dy) = _state.ConsumeMouseDelta();
        _lastNavigateAt = DateTime.Now;
        switch (_settings.NavigationMode)
        {
            case NavigationMode.OverlayThenKeyboard:
                if (!NavigateOverlay(dx, dy))
                    SendKeyboardFallback(dx, dy);
                break;
            case NavigationMode.KeyboardTab:
                SendTabFallback(dx, dy);
                break;
            case NavigationMode.KeyboardArrows:
                SendArrowFallback(dx, dy);
                break;
        }
    }

    private void PrimeTargetCacheWhenIdle()
    {
        if (_session is not null || !_state.Enabled)
            return;
        if ((DateTime.Now - _lastCachePrimeAt).TotalMilliseconds < 1000)
            return;

        _lastCachePrimeAt = DateTime.Now;
        _targetProvider.PrimeActiveWindowCache();
    }

    private static bool IsDiagonalVector(int dx, int dy)
    {
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        if (ax == 0 || ay == 0)
            return false;

        var ratio = Math.Min(ax, ay) / (double)Math.Max(ax, ay);
        return ratio >= 0.35;
    }

    private void RefreshSessionTargetsWhenActive()
    {
        if (!_state.Enabled || !_activationHeld || _session is null)
            return;
        if (_sessionRefreshInFlight)
            return;
        if ((DateTime.Now - _lastSessionRefreshAt).TotalMilliseconds < 700)
            return;

        var session = _session;
        var generation = _sessionGeneration;
        var oldSignature = TargetsSignature(session.Targets);
        _lastSessionRefreshAt = DateTime.Now;
        _sessionRefreshInFlight = true;

        _ = Task.Run(() => _targetProvider.GetActiveWindowTargets()).ContinueWith(task =>
        {
            if (IsDisposed)
                return;

            BeginInvoke(() =>
            {
                _sessionRefreshInFlight = false;
                if (_session != session || generation != _sessionGeneration)
                    return;
                if (task.Status != TaskStatus.RanToCompletion)
                    return;

                var targets = task.Result;
                var newSignature = TargetsSignature(targets);
                if (newSignature == oldSignature)
                    return;

                var selected = FindEquivalentTarget(session.SelectedTarget, targets);
                session.SelectedTarget = selected;
                session.SetTargets(targets);
                _overlay.ShowTargets(targets, selected);
                Log($"Navigation targets refreshed. targets={targets.Count}");
                UpdateLabels();
            });
        });
    }

    private bool IsNavigationClickActive()
    {
        return _state.Enabled && _activationHeld && _session is not null;
    }

    private void ConfirmSelectionFromLeftClick()
    {
        if (IsDisposed)
            return;

        BeginInvoke(() => EndSession(confirm: true));
    }

    private bool NavigateOverlay(int dx, int dy)
    {
        if (_session is null)
            return false;
        if (!_session.TargetsLoaded)
            return true;

        var origin = _session.SelectedTarget?.ClickablePoint ?? _session.Anchor;
        var next = TargetSelector.Pick(origin, dx, dy, _session.Targets);
        if (next is null)
        {
            LogThrottled("No directional UI target; using keyboard fallback.");
            return false;
        }

        _session.SelectedTarget = next;
        _overlay.UpdateSelection(next);
        _statusLabel.Text = $"Selected: {DescribeTarget(next)}";
        Log($"Selected {DescribeTarget(next)}");
        return true;
    }

    private void SendKeyboardFallback(int dx, int dy)
    {
        if (_settings.KeyboardFallback == KeyboardFallback.Arrows)
            SendArrowFallback(dx, dy);
        else
            SendTabFallback(dx, dy);
    }

    private void SendTabFallback(int dx, int dy)
    {
        if ((DateTime.Now - _lastFallbackAt).TotalMilliseconds < _settings.KeyboardRepeatMs)
            return;

        _lastFallbackAt = DateTime.Now;
        var forward = Math.Abs(dx) >= Math.Abs(dy) ? dx >= 0 : dy >= 0;
        InputActions.KeyPress(NativeMethods.VK_TAB, shift: !forward);
        _statusLabel.Text = forward ? "Fallback: Tab" : "Fallback: Shift+Tab";
    }

    private void SendArrowFallback(int dx, int dy)
    {
        if ((DateTime.Now - _lastFallbackAt).TotalMilliseconds < _settings.KeyboardRepeatMs)
            return;

        _lastFallbackAt = DateTime.Now;
        var horizontal = Math.Abs(dx) >= Math.Abs(dy);
        var key = horizontal
            ? (dx >= 0 ? NativeMethods.VK_RIGHT : NativeMethods.VK_LEFT)
            : (dy >= 0 ? NativeMethods.VK_DOWN : NativeMethods.VK_UP);
        InputActions.KeyPress(key);
        _statusLabel.Text = $"Fallback: {KeyName(key)}";
    }

    private void ScanTargets()
    {
        var targets = _targetProvider.GetActiveWindowTargets();
        Log($"UIA targets in active window: {targets.Count}");
        foreach (var target in targets.Take(40))
            Log($"  {DescribeTarget(target)}");
    }

    private static string TargetsSignature(IReadOnlyList<UiTarget> targets)
    {
        unchecked
        {
            var hash = 17;
            foreach (var target in targets.Take(180))
            {
                hash = hash * 31 + target.ControlType.GetHashCode();
                hash = hash * 31 + target.Name.GetHashCode();
                hash = hash * 31 + target.Bounds.Left;
                hash = hash * 31 + target.Bounds.Top;
                hash = hash * 31 + target.Bounds.Width;
                hash = hash * 31 + target.Bounds.Height;
            }

            return $"{targets.Count}:{hash}";
        }
    }

    private static UiTarget? FindEquivalentTarget(UiTarget? selected, IReadOnlyList<UiTarget> targets)
    {
        if (selected is null)
            return null;

        return targets.FirstOrDefault(target =>
            target.ControlType == selected.ControlType &&
            string.Equals(target.Name, selected.Name, StringComparison.OrdinalIgnoreCase) &&
            Distance(target.ClickablePoint, selected.ClickablePoint) <= 28);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
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
    }

    private void UpdateLabels()
    {
        _statusLabel.Text = _session is null
            ? $"Idle. Hold {KeyName(_settings.ActivationVk)} to navigate."
            : $"Navigating. targets={(_session.TargetsLoaded ? _session.Targets.Count : "loading")} selected={(_session.SelectedTarget is null ? "none" : DescribeTarget(_session.SelectedTarget))}";
        _inputLabel.Text =
            $"Enabled={_state.Enabled} Held={_activationHeld} MouseDelta=({_state.LastMouseDx},{_state.LastMouseDy}) " +
            $"MouseButtons=0x{_state.LastMouseButtonFlags:X4} Mode={_settings.NavigationMode} Fallback={_settings.KeyboardFallback}";
    }

    private void ConfigureTray()
    {
        _tray.Text = "GPD UI Navigator";
        _tray.Icon = SystemIcons.Application;
        _tray.Visible = true;
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.Add("Show", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        _tray.ContextMenuStrip.Items.Add("Toggle Enabled", null, (_, _) => { _enableBox.Checked = !_enableBox.Checked; });
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Close());
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        _log.Items.Insert(0, line);
        while (_log.Items.Count > 500)
            _log.Items.RemoveAt(_log.Items.Count - 1);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // File logging is diagnostic only.
        }
    }

    private void LogThrottled(string message)
    {
        if ((DateTime.Now - _lastFallbackAt).TotalMilliseconds > 650)
            Log(message);
    }

    private static string DescribeTarget(UiTarget target)
    {
        var name = string.IsNullOrWhiteSpace(target.Name) ? "(unnamed)" : target.Name;
        return $"{target.ControlType} \"{name}\"";
    }

    private static string KeyName(int vk)
    {
        return vk switch
        {
            NativeMethods.VK_TAB => "Tab",
            NativeMethods.VK_LEFT => "Left",
            NativeMethods.VK_UP => "Up",
            NativeMethods.VK_RIGHT => "Right",
            NativeMethods.VK_DOWN => "Down",
            NativeMethods.VK_SCROLL => "Scroll Lock",
            NativeMethods.VK_F9 => "F9",
            NativeMethods.VK_F12 => "F12",
            NativeMethods.VK_F13 => "F13",
            NativeMethods.VK_F15 => "F15",
            _ => $"VK 0x{vk:X2}",
        };
    }

    private static bool IsGpdMouseDevice(string deviceName)
    {
        return deviceName.Contains("VID_045E&PID_0009", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class NavigationSession
{
    public NavigationSession(Point anchor)
    {
        Anchor = anchor;
    }

    public Point Anchor { get; }
    public IReadOnlyList<UiTarget> Targets { get; private set; } = Array.Empty<UiTarget>();
    public bool TargetsLoaded { get; private set; }
    public UiTarget? SelectedTarget { get; set; }

    public void SetTargets(IReadOnlyList<UiTarget> targets)
    {
        Targets = targets;
        TargetsLoaded = true;
    }
}
