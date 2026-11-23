using Microsoft.Win32;
using LibUsbDotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace LcdFusion
{
    internal sealed class MainWindow : Window
    {
        private readonly Brush Bg = Br("#0A0D15");
        private readonly Brush Surface = Br("#141A28");
        private readonly Brush Surface2 = Br("#1B2336");
        private readonly Brush Panel = Br("#0E131F");
        private readonly Brush BorderBr = Br("#28324A");
        private readonly Brush SoftBr = Br("#1E2740");
        private readonly Brush TextBr = Br("#ECF0F7");
        private readonly Brush MutedBr = Br("#8B96AB");
        private readonly Brush AccentBr = Br("#6C8CFF");
        private readonly Brush AccentTextBr = Br("#0A0E18");
        private readonly Brush GreenBr = Br("#3FD18B");
        private readonly Brush RedBr = Br("#FF6B81");

        private enum DragMode { None, Overlay, Pan, StretchBg }
        private enum StretchHandle { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

        private static readonly string[] MetricKeys =
            { "metric.cpuTemp", "metric.gpuTemp", "metric.cpuLoad", "metric.gpuLoad",
              "metric.cpuClock", "metric.gpuClock", "metric.cpuPower", "metric.gpuPower",
              "metric.gpuVram", "metric.gpuFan", "metric.ramLoad", "metric.cpuCores",
              "metric.clock", "metric.date", "metric.text" };
        private static readonly OverlayKind[] MetricKinds =
            { OverlayKind.CpuTemp, OverlayKind.GpuTemp, OverlayKind.CpuLoad, OverlayKind.GpuLoad,
              OverlayKind.CpuClock, OverlayKind.GpuClock, OverlayKind.CpuPower, OverlayKind.GpuPower,
              OverlayKind.GpuVram, OverlayKind.GpuFan, OverlayKind.RamLoad, OverlayKind.CpuCores,
              OverlayKind.Clock, OverlayKind.Date, OverlayKind.Text };
        private static readonly string[] SwatchHex = { "#FFFFFF", "#5BE1FF", "#33D69F", "#F5C850", "#FF647C", "#6C8CFF" };
        private static readonly string[] SwatchKeys =
            { "color.white", "color.cyan", "color.green", "color.amber", "color.red", "color.blue" };
        private static string MetricName(int i) { return Loc.T(MetricKeys[i]); }
        private static string SwatchName(int i) { return Loc.T(SwatchKeys[i]); }

        private readonly ContentEngine _engine;
        private ScreenId _activeScreen = ScreenId.Valkyrie;
        private int _sel = -1;
        private bool _tgtValk = true;
        private bool _tgtBoWei;
        private bool _tgtThermal;

        private DragMode _drag = DragMode.None;
        private StretchHandle _stretchHandle = StretchHandle.None;
        private double _dragStartX, _dragStartY, _panOrigX, _panOrigY;
        private double _dragStartPxX, _dragStartPxY, _dragOrigScaleX, _dragOrigScaleY, _dragOrigUserScale, _dragOrigRotRad;

        private Border _windowShell;
        private Button _maxRestoreBtn;
        private Ellipse _valkDot, _boweiDot, _trDot;
        private TextBlock _valkState, _boweiState, _trState, _summary;
        private Border _valkPill, _boweiPill, _trPill;
        private Button _tabValk, _tabBoWei, _tabThermal;
        private StackPanel _editorPanel;
        private Border _previewFrame;
        private Grid _previewHost;
        private Border _previewBorder;
        private TextBlock _previewCaption;
        private TextBlock _previewHint;
        private Image _preview;
        private Canvas _previewOverlay;
        private Slider _stretchXSlider, _stretchYSlider;
        private TextBlock _stretchXText, _stretchYText;
        private bool _syncingStretchSliders;
        private TextBlock _statusText;
        private Ellipse _statusDot;
        private Button _startButton, _stopButton;
        private TextBlock _profileDirtyText;

        private DeviceSnapshot _snapshot;
        private byte[] _lastPng;
        private ComboBox _profileCombo;
        private CheckBox _autoStartCheck;
        private bool _autoStart;
        private string _currentProfile = "";
        private System.Windows.Forms.NotifyIcon _tray;
        private System.Drawing.Icon _trayIcon;
        private System.Windows.Forms.ToolStripMenuItem _trayOpen, _trayExit;
        private bool _trayHinted;
        private bool _profileDirty;
        private bool _populatingProfiles;
        private string _lastSeenVersion = "";
        private string _valkPanelMode = "auto";
        private string _lastDetectedValkPanel = "";
        private int _valkRotation = -1;
        private int _boweiRotation;
        private int _thermalRotation;
        private int _boweiBrightness = BoWeiDirectService.DefaultBrightness;
        private string _wallpaperAssetsDir = "";
        private string _wallpaperRendererPath = "";
        private volatile bool _valkProbedThisSession;
        private bool _startupChangelogChecked;
        private readonly List<System.Drawing.Color> _recentColors = new List<System.Drawing.Color>();
        private volatile bool _refreshing;
        private readonly DispatcherTimer _timer;

        public MainWindow()
        {
            Resources.MergedDictionaries.Add(Theme.Build());

            AppSettings settings = ProfileService.LoadSettings();
            if (!string.IsNullOrEmpty(settings.Lang) && Array.IndexOf(Loc.Codes, settings.Lang) >= 0) Loc.Lang = settings.Lang;
            LoadRecentColors(settings);
            _currentProfile = settings.LastProfile ?? "";
            _lastSeenVersion = settings.LastSeenVersion ?? "";
            _valkPanelMode = string.IsNullOrWhiteSpace(settings.ValkyriePanel) ? "auto" : settings.ValkyriePanel;
            _lastDetectedValkPanel = settings.LastDetectedValkyriePanel ?? "";
            _valkRotation = settings.ValkyrieRotation;
            _boweiRotation = settings.BoWeiRotation;
            _thermalRotation = settings.ThermalrightRotation;
            _boweiBrightness = settings.BoWeiBrightness >= 0 && settings.BoWeiBrightness <= 100
                ? settings.BoWeiBrightness
                : BoWeiDirectService.DefaultBrightness;
            _wallpaperAssetsDir = settings.WallpaperAssetsDir ?? "";
            _wallpaperRendererPath = settings.WallpaperRendererPath ?? "";
            _autoStart = AutoStartService.IsEnabled();

            ValkyrieDirectService.ConfigurePanel(_valkPanelMode, _lastDetectedValkPanel);
            BoWeiDirectService.SetBrightness(_boweiBrightness);
            if (ValkyrieDirectService.IsAutoMode && !VendorService.IsMythCoolRunning())
            {
                try
                {
                    if (ValkyrieDirectService.TryProbePanel())
                    {
                        _lastDetectedValkPanel = ValkyrieDirectService.CurrentPanelKey;
                        _valkProbedThisSession = true;
                    }
                    else if (!string.IsNullOrEmpty(ValkyrieDirectService.DetectedSerial))
                    {
                        _valkProbedThisSession = true;
                    }
                }
                catch { }
            }

            _engine = new ContentEngine(_valkRotation, _boweiRotation, _thermalRotation);
            _engine.ConfigureWallpaper(_wallpaperRendererPath, _wallpaperAssetsDir);

            Title = "LCD Fusion";
            Width = 1240; Height = 860; MinWidth = 1040; MinHeight = 740;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg; Foreground = TextBr;
            FontFamily = new FontFamily("Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            Icon = BuildWindowIcon();
            Theme.ApplyCustomChrome(this);

            // Prefer the last selected profile on startup; fall back to the last session for older installs.
            ProfileData startupProfile = string.IsNullOrEmpty(_currentProfile) ? null : ProfileService.Load(_currentProfile);
            if (startupProfile == null)
            {
                _currentProfile = "";
                startupProfile = ProfileService.LoadLast();
            }
            bool autoStream = startupProfile == null || startupProfile.Streaming;
            if (startupProfile != null)
            {
                _engine.Apply(startupProfile);
                _tgtValk = startupProfile.TargetValk;
                _tgtBoWei = startupProfile.TargetBoWei;
                _tgtThermal = startupProfile.TargetThermal;
                _activeScreen = ResolveActiveScreen(startupProfile);
            }
            else
            {
                _tgtValk = true;
                _tgtBoWei = BoWeiDirectService.IsDevicePresent();
                _tgtThermal = false;
            }

            Content = BuildUi();

            _engine.StatusChanged = OnEngineStatus;
            _engine.SetTargets(_tgtValk, _tgtBoWei, _tgtThermal);
            _engine.SetPreviewTarget(_activeScreen);
            _engine.Start();
            RebuildEditor();
            ApplyPreviewAspect();
            MarkProfileClean();
            SetStreaming(autoStream);

            ValkyrieDirectService.PanelChanged += OnHardwarePanelChanged;
            BoWeiDirectService.PanelChanged += OnHardwarePanelChanged;
            Loc.Changed += OnLanguageChanged;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _timer.Tick += delegate { RefreshStatus(); };
            _timer.Start();
            CompositionTarget.Rendering += OnRender;
            RefreshStatus();
            SetupTray();
            Loaded += OnWindowLoaded;
        }

        private void OnHardwarePanelChanged()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (ValkyrieDirectService.IsAutoMode)
                {
                    _lastDetectedValkPanel = ValkyrieDirectService.CurrentPanelKey;
                    SaveCurrentSettings();
                }
                _engine.InvalidateScreen(ScreenId.Valkyrie);
                _engine.InvalidateScreen(ScreenId.BoWei);
                UpdateDeviceTooltips();
                ApplyPreviewAspect();
                RebuildEditor();
            }));
        }

        private static ScreenId ResolveActiveScreen(ProfileData p)
        {
            if (p == null) return ScreenId.Valkyrie;
            if (p.ActiveScreen >= 0 && p.ActiveScreen <= 2) return (ScreenId)p.ActiveScreen;
            return p.ActiveValk ? ScreenId.Valkyrie : ScreenId.Thermalright;
        }

        private static string ScreenDisplayName(ScreenId screen)
        {
            switch (screen)
            {
                case ScreenId.BoWei: return "BoWei";
                case ScreenId.Thermalright: return "Thermalright";
                default: return "Valkyrie";
            }
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (_startupChangelogChecked) return;
            _startupChangelogChecked = true;
            ShowStartupChangelogIfNeeded();
        }

        private void OnLanguageChanged()
        {
            Content = BuildUi();
            RebuildEditor();
            ApplyPreviewAspect();
            _engine.SetPreviewTarget(_activeScreen);
            RefreshStatus();
            if (_startButton != null) _startButton.IsEnabled = !_engine.IsStreaming;
            if (_stopButton != null) _stopButton.IsEnabled = _engine.IsStreaming;
            if (_trayOpen != null) _trayOpen.Text = Loc.T("tray.open");
            if (_trayExit != null) _trayExit.Text = Loc.T("tray.exit");
        }

        // ---- system tray ------------------------------------------------------------

        private void SetupTray()
        {
            try { _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { _trayIcon = null; }

            _tray = new System.Windows.Forms.NotifyIcon { Text = "LCD Fusion", Visible = false };
            if (_trayIcon != null) _tray.Icon = _trayIcon;
            _tray.DoubleClick += delegate { RestoreFromTray(); };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            _trayOpen = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray.open"));
            _trayOpen.Click += delegate { RestoreFromTray(); };
            _trayExit = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray.exit"));
            _trayExit.Click += delegate { Close(); };
            menu.Items.Add(_trayOpen);
            menu.Items.Add(_trayExit);
            _tray.ContextMenuStrip = menu;

            StateChanged += OnStateChanged;
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            UpdateWindowChromeState();
            if (WindowState != WindowState.Minimized || _tray == null) return;
            _engine.SetPreviewEnabled(false);
            Hide();
            _tray.Visible = true;
            if (!_trayHinted)
            {
                _trayHinted = true;
                try { _tray.ShowBalloonTip(3000, "LCD Fusion", Loc.T("tray.hint"), System.Windows.Forms.ToolTipIcon.Info); }
                catch { }
            }
        }

        private void UpdateWindowChromeState()
        {
            if (_windowShell != null)
            {
                if (WindowState == WindowState.Maximized)
                {
                    Thickness pad = SystemParameters.WindowResizeBorderThickness;
                    _windowShell.Padding = new Thickness(pad.Left + 2, pad.Top + 2, pad.Right + 2, pad.Bottom + 2);
                }
                else
                {
                    _windowShell.Padding = new Thickness(0);
                }
            }
            if (_maxRestoreBtn != null)
            {
                _maxRestoreBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            }
        }

        private UIElement BuildTitleBar()
        {
            var bar = new Border
            {
                Height = 36,
                Background = Bg,
                BorderBrush = SoftBr,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
            };
            var miniIcon = new Image { Source = BuildWindowIcon(), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(miniIcon, BitmapScalingMode.HighQuality);
            left.Children.Add(miniIcon);
            left.Children.Add(new TextBlock
            {
                Text = "LCD Fusion",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBr,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0)
            });
            grid.Children.Add(left);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var minBtn = new Button { Content = "─", Style = (Style)FindResource("WinCaptionBtn") };
            WindowChrome.SetIsHitTestVisibleInChrome(minBtn, true);
            minBtn.Click += delegate { WindowState = WindowState.Minimized; };

            _maxRestoreBtn = new Button
            {
                Content = WindowState == WindowState.Maximized ? "❐" : "□",
                Style = (Style)FindResource("WinCaptionBtn")
            };
            WindowChrome.SetIsHitTestVisibleInChrome(_maxRestoreBtn, true);
            _maxRestoreBtn.Click += delegate
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            };

            var closeBtn = new Button { Content = "✕", Style = (Style)FindResource("WinCloseBtn") };
            WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            closeBtn.Click += delegate { Close(); };

            controls.Children.Add(minBtn);
            controls.Children.Add(_maxRestoreBtn);
            controls.Children.Add(closeBtn);
            Grid.SetColumn(controls, 1);
            grid.Children.Add(controls);

            bar.Child = grid;
            return bar;
        }

        private void RestoreFromTray()
        {
            if (_tray != null) _tray.Visible = false;
            _engine.SetPreviewEnabled(true);
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
        }

        // ---- layout -----------------------------------------------------------------

        private UIElement BuildUi()
        {
            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition());

            UIElement titleBar = BuildTitleBar();
            Grid.SetRow(titleBar, 0);
            outerGrid.Children.Add(titleBar);

            var rootGrid = new Grid { Margin = new Thickness(28, 18, 28, 22) };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition());

            // header
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var brand = new StackPanel { Orientation = Orientation.Horizontal };
            var mark = new Image { Source = BuildWindowIcon(), Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
            brand.Children.Add(mark);
            var brandText = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            brandText.Children.Add(new TextBlock { Text = "LCD Fusion", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = TextBr });
            brandText.Children.Add(new TextBlock { Text = Loc.T("app.subtitle"), FontSize = 12, Foreground = MutedBr, Margin = new Thickness(0, 1, 0, 0) });
            brand.Children.Add(brandText);
            header.Children.Add(brand);

            var headerRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerRight.Children.Add(DevicePill(ScreenId.Valkyrie));
            headerRight.Children.Add(DevicePill(ScreenId.BoWei));
            headerRight.Children.Add(DevicePill(ScreenId.Thermalright));
            headerRight.Children.Add(LangSelector());
            var about = Btn(Loc.T("btn.about"), delegate { ShowAboutDialog(); }, "BtnGhost");
            about.Margin = new Thickness(8, 0, 0, 0);
            headerRight.Children.Add(about);
            var free = Btn(Loc.T("btn.free"), delegate { FreeDevices(); }, "BtnGhost");
            free.Margin = new Thickness(8, 0, 0, 0);
            headerRight.Children.Add(free);
            Grid.SetColumn(headerRight, 1);
            header.Children.Add(headerRight);
            Grid.SetRow(header, 0);
            rootGrid.Children.Add(header);

            _summary = new TextBlock { FontSize = 12, Foreground = MutedBr, Margin = new Thickness(2, 12, 0, 0) };
            Grid.SetRow(_summary, 1);
            rootGrid.Children.Add(_summary);

            var toolbar = BuildToolbar();
            Grid.SetRow(toolbar, 2);
            rootGrid.Children.Add(toolbar);

            // body fills the remaining height; only the editor scrolls internally
            var body = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.18, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(EditorColumn());
            var right = PreviewColumn();
            right.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(right, 2);
            body.Children.Add(right);
            Grid.SetRow(body, 3);
            rootGrid.Children.Add(body);

            Grid.SetRow(rootGrid, 1);
            outerGrid.Children.Add(rootGrid);

            _windowShell = new Border { Background = Bg, Child = outerGrid };
            UpdateWindowChromeState();
            return _windowShell;
        }

        private UIElement BuildToolbar()
        {
            var bar = new Border { Background = Surface, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 9, 14, 9), BorderBrush = SoftBr, BorderThickness = new Thickness(1), Margin = new Thickness(0, 14, 0, 0) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = Loc.T("prof.label"), Foreground = MutedBr, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            _profileCombo = new ComboBox { Width = 220, VerticalAlignment = VerticalAlignment.Center };
            PopulateProfiles();
            _profileCombo.SelectionChanged += OnProfileSelectionChanged;
            left.Children.Add(_profileCombo);
            var load = Btn(Loc.T("prof.load"), delegate { LoadSelectedProfile(); }, "BtnGhost"); load.Margin = new Thickness(10, 0, 0, 0); left.Children.Add(load);
            var save = Btn(Loc.T("prof.save"), delegate { SaveCurrentProfile(); }, "BtnPrimary"); save.Margin = new Thickness(8, 0, 0, 0); left.Children.Add(save);
            var saveAs = Btn(Loc.T("prof.saveAs"), delegate { SaveProfileAs(); }, "BtnGhost"); saveAs.Margin = new Thickness(8, 0, 0, 0); left.Children.Add(saveAs);
            var del = Btn(Loc.T("prof.delete"), delegate { DeleteSelectedProfile(); }, "BtnGhost"); del.Margin = new Thickness(8, 0, 0, 0); left.Children.Add(del);
            _profileDirtyText = new TextBlock { Foreground = Br("#F5C850"), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            left.Children.Add(_profileDirtyText);
            UpdateProfileDirtyText();
            grid.Children.Add(left);

            _autoStartCheck = new CheckBox { Content = Loc.T("prof.autostart"), IsChecked = _autoStart, Foreground = TextBr, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            _autoStartCheck.Checked += delegate { ToggleAutostart(true); };
            _autoStartCheck.Unchecked += delegate { ToggleAutostart(false); };
            Grid.SetColumn(_autoStartCheck, 2);
            grid.Children.Add(_autoStartCheck);

            bar.Child = grid;
            return bar;
        }

        private void PopulateProfiles()
        {
            if (_profileCombo == null) return;
            _populatingProfiles = true;
            try
            {
                _profileCombo.Items.Clear();
                string[] names = ProfileService.List();
                foreach (string n in names) _profileCombo.Items.Add(n);
                _profileCombo.IsEnabled = names.Length > 0;
                if (names.Length > 0)
                {
                    int idx = Array.IndexOf(names, _currentProfile);
                    _profileCombo.SelectedIndex = idx >= 0 ? idx : 0;
                }
            }
            finally { _populatingProfiles = false; }
        }

        private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_populatingProfiles || _profileCombo == null || _profileCombo.SelectedItem == null) return;
            LoadSelectedProfile();
        }

        private void LoadSelectedProfile()
        {
            if (_profileCombo.SelectedItem == null) return;
            string name = _profileCombo.SelectedItem.ToString();
            ProfileData p = ProfileService.Load(name);
            if (p == null) return;
            ApplyProfile(p);
            _currentProfile = name;
            MarkProfileClean();
            SaveCurrentSettings();
            SetStatus(true, Loc.T("prof.loaded") + ": " + name);
        }

        private void ApplyProfile(ProfileData p)
        {
            _engine.Apply(p);
            _tgtValk = p.TargetValk; _tgtBoWei = p.TargetBoWei; _tgtThermal = p.TargetThermal;
            _activeScreen = ResolveActiveScreen(p);
            _engine.SetTargets(_tgtValk, _tgtBoWei, _tgtThermal);
            _sel = -1;
            _engine.SetPreviewTarget(_activeScreen);
            ApplyPreviewAspect();
            RebuildEditor();
            SetStreaming(p.Streaming);
        }

        private void SaveCurrentProfile()
        {
            if (string.IsNullOrEmpty(_currentProfile))
            {
                SaveProfileAs();
                return;
            }
            ProfileService.Save(_currentProfile, _engine.Capture(_tgtValk, _tgtBoWei, _tgtThermal, _activeScreen));
            PopulateProfiles();
            MarkProfileClean();
            SetStatus(true, Loc.T("prof.saved") + ": " + _currentProfile);
        }

        private void SaveProfileAs()
        {
            string def = string.IsNullOrEmpty(_currentProfile) ? "Profile 1" : _currentProfile;
            string name = PromptName(Loc.T("prof.nameTitle"), def);
            if (string.IsNullOrEmpty(name)) return;
            ProfileService.Save(name, _engine.Capture(_tgtValk, _tgtBoWei, _tgtThermal, _activeScreen));
            _currentProfile = name;
            PopulateProfiles();
            MarkProfileClean();
            SetStatus(true, Loc.T("prof.saved") + ": " + name);
        }

        private void DeleteSelectedProfile()
        {
            if (_profileCombo.SelectedItem == null) return;
            string name = _profileCombo.SelectedItem.ToString();
            MessageBoxResult confirm = MessageBox.Show(this, Loc.T("prof.deleteConfirm", name), Loc.T("prof.deleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            ProfileService.Delete(name);
            if (_currentProfile == name) _currentProfile = "";
            PopulateProfiles();
            SetStatus(true, Loc.T("prof.deleted") + ": " + name);
        }

        private void MarkProfileDirty()
        {
            _profileDirty = true;
            UpdateProfileDirtyText();
        }

        private void MarkProfileClean()
        {
            _profileDirty = false;
            UpdateProfileDirtyText();
        }

        private void UpdateProfileDirtyText()
        {
            if (_profileDirtyText != null) _profileDirtyText.Text = _profileDirty ? Loc.T("prof.unsaved") : "";
        }

        private void LoadRecentColors(AppSettings settings)
        {
            _recentColors.Clear();
            if (settings == null || settings.RecentColors == null) return;
            for (int i = settings.RecentColors.Count - 1; i >= 0; i--)
                RememberColor(System.Drawing.Color.FromArgb(settings.RecentColors[i]), false);
        }

        private List<int> RecentColorArgb()
        {
            var values = new List<int>();
            foreach (System.Drawing.Color color in _recentColors) values.Add(color.ToArgb());
            return values;
        }

        private void RememberColor(System.Drawing.Color color) { RememberColor(color, true); }
        private void RememberColor(System.Drawing.Color color, bool rebuild)
        {
            for (int i = _recentColors.Count - 1; i >= 0; i--)
                if (ColorsEqual(_recentColors[i], color)) _recentColors.RemoveAt(i);
            _recentColors.Insert(0, System.Drawing.Color.FromArgb(255, color.R, color.G, color.B));
            while (_recentColors.Count > 8) _recentColors.RemoveAt(_recentColors.Count - 1);
            if (rebuild) RebuildEditor();
        }

        private void ToggleAutostart(bool on)
        {
            bool ok = on ? AutoStartService.Enable() : AutoStartService.Disable();
            if (ok) { _autoStart = on; return; }
            _autoStart = AutoStartService.IsEnabled();
            if (_autoStartCheck != null && _autoStartCheck.IsChecked != _autoStart) _autoStartCheck.IsChecked = _autoStart;
        }

        private string PromptName(string title, string def)
        {
            var dlg = new Window
            {
                Title = title, Width = 400, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
                ResizeMode = ResizeMode.NoResize, Background = Surface, Foreground = TextBr,
                WindowStyle = WindowStyle.ToolWindow, FontFamily = new FontFamily("Segoe UI"),
                Icon = BuildWindowIcon()
            };
            dlg.Resources.MergedDictionaries.Add(Theme.Build());
            Theme.ApplyDarkTitleBar(dlg);
            var panel = new StackPanel { Margin = new Thickness(20) };
            var tb = new TextBox { Text = def, FontSize = 14 };
            panel.Children.Add(tb);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            string result = null;
            var ok = new Button { Content = Loc.T("dlg.ok"), IsDefault = true };
            ok.Style = (Style)dlg.FindResource("BtnPrimary");
            ok.Click += delegate { result = tb.Text; dlg.DialogResult = true; };
            var cancel = new Button { Content = Loc.T("dlg.cancel"), IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
            cancel.Style = (Style)dlg.FindResource("BtnGhost");
            row.Children.Add(ok);
            row.Children.Add(cancel);
            panel.Children.Add(row);
            dlg.Content = panel;
            dlg.Loaded += delegate { tb.Focus(); tb.SelectAll(); };
            bool? r = dlg.ShowDialog();
            if (r != true || string.IsNullOrEmpty(result)) return null;
            return result.Trim();
        }

        private Border DevicePill(ScreenId screen)
        {
            var pill = new Border { Background = Surface2, CornerRadius = new CornerRadius(20), Padding = new Thickness(10, 6, 12, 6), Margin = new Thickness(0, 0, 7, 0), BorderBrush = SoftBr, BorderThickness = new Thickness(1) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = RedBr, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            row.Children.Add(dot);
            var name = new TextBlock { Text = ScreenDisplayName(screen), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = TextBr, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            var state = new TextBlock { Text = "—", FontSize = 11.5, Foreground = MutedBr, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            row.Children.Add(state);
            pill.Child = row;

            switch (screen)
            {
                case ScreenId.BoWei:
                    _boweiPill = pill; _boweiDot = dot; _boweiState = state;
                    break;
                case ScreenId.Thermalright:
                    _trPill = pill; _trDot = dot; _trState = state;
                    break;
                default:
                    _valkPill = pill; _valkDot = dot; _valkState = state;
                    break;
            }
            UpdateDeviceTooltips();
            return pill;
        }

        private void UpdateDeviceTooltips()
        {
            if (_valkPill != null)
            {
                string sn = ValkyrieDirectService.DetectedSerial;
                _valkPill.ToolTip = ProductCatalog.Valkyrie.Model + " · " +
                    ValkyrieDirectService.Width + "×" + ValkyrieDirectService.Height +
                    (string.IsNullOrEmpty(sn) ? "" : " · SN " + sn);
            }
            if (_boweiPill != null)
            {
                string sn = BoWeiDirectService.DetectedSerial;
                _boweiPill.ToolTip = ProductCatalog.BoWei.Model + " · " +
                    BoWeiDirectService.Width + "×" + BoWeiDirectService.Height +
                    (string.IsNullOrEmpty(sn) ? "" : " · SN " + sn);
            }
            if (_trPill != null)
            {
                _trPill.ToolTip = ProductCatalog.Thermalright.Model + " · " +
                    ThermalrightDirectService.CanvasWidth + "×" + ThermalrightDirectService.CanvasHeight;
            }
        }

        private UIElement LangSelector()
        {
            var combo = new ComboBox { Width = 112, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("lang.label") };
            foreach (string n in Loc.Names) combo.Items.Add(n);
            combo.SelectedIndex = Loc.CurrentIndex();
            combo.SelectionChanged += delegate { if (combo.SelectedIndex >= 0) Loc.Set(Loc.Codes[combo.SelectedIndex]); };
            return combo;
        }

        private Border EditorColumn()
        {
            var card = Card();
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());

            var seg = new Border { Background = Panel, CornerRadius = new CornerRadius(11), Padding = new Thickness(4), BorderBrush = SoftBr, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            var segRow = new StackPanel { Orientation = Orientation.Horizontal };
            _tabValk = SegButton("Valkyrie", ScreenId.Valkyrie);
            _tabBoWei = SegButton("BoWei", ScreenId.BoWei);
            _tabThermal = SegButton("Thermalright", ScreenId.Thermalright);
            segRow.Children.Add(_tabValk);
            segRow.Children.Add(_tabBoWei);
            segRow.Children.Add(_tabThermal);
            seg.Child = segRow;
            Grid.SetRow(seg, 0);
            grid.Children.Add(seg);

            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 16, 0, 0) };
            _editorPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            sv.Content = _editorPanel;
            Grid.SetRow(sv, 1);
            grid.Children.Add(sv);

            card.Child = grid;
            return card;
        }

        private Border PreviewColumn()
        {
            var card = Card();
            var stack = new StackPanel();
            stack.Children.Add(SectionLabel(Loc.T("sec.preview")));

            _previewFrame = new Border { Background = Panel, CornerRadius = new CornerRadius(12), BorderBrush = SoftBr, BorderThickness = new Thickness(1), Padding = new Thickness(6), Margin = new Thickness(0, 12, 0, 0) };
            _previewFrame.SizeChanged += delegate { ApplyPreviewAspect(); };
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            _previewHost = new Grid { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            _previewBorder = new Border { Background = Br("#05070C"), CornerRadius = new CornerRadius(8), BorderBrush = BorderBr, BorderThickness = new Thickness(1), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14) };
            _preview = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
            _previewBorder.Child = _preview;
            _previewBorder.SizeChanged += delegate { RefreshPreviewOverlay(); };
            _previewOverlay = new Canvas { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14), ClipToBounds = false };
            _previewOverlay.SizeChanged += delegate { RefreshPreviewOverlay(); };
            _previewHost.Children.Add(_previewBorder);
            _previewHost.Children.Add(_previewOverlay);
            _previewHost.MouseLeftButtonDown += PreviewDown;
            _previewHost.MouseMove += PreviewMove;
            _previewHost.MouseLeftButtonUp += PreviewUp;
            _previewHost.MouseLeave += PreviewLeave;
            center.Children.Add(_previewHost);
            _previewCaption = new TextBlock { Text = "", Foreground = MutedBr, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 8) };
            center.Children.Add(_previewCaption);
            _previewFrame.Child = center;
            stack.Children.Add(_previewFrame);

            _previewHint = new TextBlock { Text = Loc.T("preview.hint"), Foreground = MutedBr, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 12, 0, 0) };
            stack.Children.Add(_previewHint);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            _startButton = Btn(Loc.T("btn.start"), delegate { StartStreaming(); }, "BtnPrimary");
            actions.Children.Add(_startButton);
            _stopButton = Btn(Loc.T("btn.stop"), delegate { StopStreaming(); }, "BtnGhost");
            _stopButton.Margin = new Thickness(10, 0, 0, 0); _stopButton.IsEnabled = false;
            actions.Children.Add(_stopButton);
            stack.Children.Add(actions);

            var statusRow = new Border { Background = Panel, CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 16, 0, 0), BorderBrush = SoftBr, BorderThickness = new Thickness(1) };
            var sr = new StackPanel { Orientation = Orientation.Horizontal };
            _statusDot = new Ellipse { Width = 9, Height = 9, Fill = GreenBr, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
            sr.Children.Add(_statusDot);
            _statusText = new TextBlock { Text = Loc.T("st.ready"), Foreground = TextBr, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            sr.Children.Add(_statusText);
            statusRow.Child = sr;
            stack.Children.Add(statusRow);

            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            sv.Content = stack;
            card.Child = sv;
            return card;
        }

        private void ShowAboutDialog()
        {
            var dlg = new Window
            {
                Title = Loc.T("about.title"),
                Width = 780,
                Height = 620,
                MinWidth = 620,
                MinHeight = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = Bg,
                Foreground = TextBr,
                FontFamily = new FontFamily("Segoe UI"),
                Icon = BuildWindowIcon()
            };
            dlg.Resources.MergedDictionaries.Add(Theme.Build());
            Theme.ApplyDarkTitleBar(dlg);

            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = "LCD Fusion", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = TextBr });
            head.Children.Add(new TextBlock { Text = Loc.T("about.subtitle"), FontSize = 12.5, Foreground = MutedBr, Margin = new Thickness(0, 4, 0, 0) });
            root.Children.Add(head);

            var body = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            body.ColumnDefinitions.Add(new ColumnDefinition());

            var nav = new StackPanel();
            TextBox content = LegalTextBox("");
            Button projectBtn = null, legalBtn = null, licensesBtn = null;
            Action<Button, string> show = delegate(Button active, string text)
            {
                content.Text = text;
                StyleAboutNav(projectBtn, active == projectBtn);
                StyleAboutNav(legalBtn, active == legalBtn);
                StyleAboutNav(licensesBtn, active == licensesBtn);
            };

            projectBtn = AboutNavButton(Loc.T("about.tab.project"), delegate { show(projectBtn, AboutProjectText()); });
            legalBtn = AboutNavButton(Loc.T("about.tab.legal"), delegate { show(legalBtn, LegalNoticeText()); });
            licensesBtn = AboutNavButton(Loc.T("about.tab.licenses"), delegate { show(licensesBtn, LicenseNoticeText()); });
            nav.Children.Add(projectBtn);
            nav.Children.Add(legalBtn);
            nav.Children.Add(licensesBtn);
            body.Children.Add(nav);

            Grid.SetColumn(content, 2);
            body.Children.Add(content);
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            show(projectBtn, AboutProjectText());

            var close = Btn(Loc.T("dlg.close"), delegate { dlg.Close(); }, "BtnPrimary");
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 18, 0, 0);
            Grid.SetRow(close, 2);
            root.Children.Add(close);

            dlg.Content = root;
            dlg.ShowDialog();
        }

        private void ShowStartupChangelogIfNeeded()
        {
            string version = AppVersionText();
            if (string.IsNullOrEmpty(version) || string.Equals(_lastSeenVersion, version, StringComparison.OrdinalIgnoreCase)) return;

            string changelog = ReadOptionalFile("CHANGELOG.md", "RELEASE_CHANGELOG.md");
            if (string.IsNullOrWhiteSpace(changelog))
            {
                _lastSeenVersion = version;
                SaveCurrentSettings();
                return;
            }

            ShowChangelogDialog(version, changelog);
            _lastSeenVersion = version;
            SaveCurrentSettings();
        }

        private void ShowChangelogDialog(string version, string changelog)
        {
            var dlg = new Window
            {
                Title = Loc.T("changelog.title"),
                Width = 720,
                Height = 540,
                MinWidth = 560,
                MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = Bg,
                Foreground = TextBr,
                FontFamily = new FontFamily("Segoe UI"),
                Icon = BuildWindowIcon()
            };
            dlg.Resources.MergedDictionaries.Add(Theme.Build());
            Theme.ApplyDarkTitleBar(dlg);

            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = Loc.T("changelog.title"), FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = TextBr });
            head.Children.Add(new TextBlock { Text = Loc.T("changelog.version", version), FontSize = 12.5, Foreground = MutedBr, Margin = new Thickness(0, 4, 0, 0) });
            root.Children.Add(head);

            TextBox content = LegalTextBox(changelog);
            content.Margin = new Thickness(0, 18, 0, 0);
            Grid.SetRow(content, 1);
            root.Children.Add(content);

            var close = Btn(Loc.T("dlg.close"), delegate { dlg.Close(); }, "BtnPrimary");
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 18, 0, 0);
            Grid.SetRow(close, 2);
            root.Children.Add(close);

            dlg.Content = root;
            dlg.ShowDialog();
        }

        private Button AboutNavButton(string text, RoutedEventHandler handler)
        {
            var b = Btn(text, handler, "BtnGhost");
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 0, 0, 8);
            b.Padding = new Thickness(14, 10, 14, 10);
            return b;
        }

        private void StyleAboutNav(Button b, bool active)
        {
            if (b == null) return;
            b.Background = active ? AccentBr : Surface2;
            b.Foreground = active ? AccentTextBr : TextBr;
            b.BorderBrush = active ? AccentBr : SoftBr;
        }

        private TextBox LegalTextBox(string text)
        {
            return new TextBox
            {
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Panel,
                Foreground = TextBr,
                BorderBrush = SoftBr,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14),
                FontSize = 13
            };
        }

        private string AboutProjectText()
        {
            string versionText = AppVersionText();
            return
                "LCD Fusion" + Environment.NewLine +
                Loc.T("about.subtitle") + Environment.NewLine + Environment.NewLine +
                Loc.T("about.version") + ": " + versionText + Environment.NewLine +
                Loc.T("about.developer") + ": itsmylife44" + Environment.NewLine +
                Loc.T("about.license") + ": MIT" + Environment.NewLine +
                Loc.T("about.repository") + ": https://github.com/itsmylife44/LcdFusion" + Environment.NewLine + Environment.NewLine +
                Loc.T("about.opensource") + Environment.NewLine + Environment.NewLine +
                Loc.T("about.vendorDisclaimer");
        }

        private string AppVersionText()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            object[] attrs = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            if (attrs.Length > 0)
            {
                string info = ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                if (!string.IsNullOrEmpty(info)) return info;
            }
            Version version = assembly.GetName().Version;
            if (version == null) return "1.0.0";
            return version.Revision > 0 ? version.ToString() : version.Major + "." + version.Minor + "." + version.Build;
        }

        private string LegalNoticeText()
        {
            return
                Loc.T("legal.unofficial") + Environment.NewLine + Environment.NewLine +
                Loc.T("legal.interop") + Environment.NewLine + Environment.NewLine +
                Loc.T("legal.trademarks") + Environment.NewLine + Environment.NewLine +
                Loc.T("legal.noTelemetry") + Environment.NewLine + Environment.NewLine +
                Loc.T("legal.risk");
        }

        private string LicenseNoticeText()
        {
            string thirdParty = ReadLegalFile("THIRD_PARTY_NOTICES.md", System.IO.Path.Combine("LcdFusion", "THIRD_PARTY_NOTICES.md"));
            string license = ReadLegalFile("LICENSE.txt", "LICENSE");
            string bundled = ReadBundledLicenseIndex();
            return
                Loc.T("about.tab.licenses") + Environment.NewLine + Environment.NewLine +
                "Third-party notices" + Environment.NewLine +
                thirdParty + Environment.NewLine + Environment.NewLine +
                "Project license" + Environment.NewLine +
                license + Environment.NewLine + Environment.NewLine +
                bundled;
        }

        private string ReadLegalFile(params string[] relativePaths)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string rel in relativePaths)
            {
                string path = System.IO.Path.Combine(baseDir, rel);
                try { if (File.Exists(path)) return File.ReadAllText(path); } catch { }
            }
            string cwd = Directory.GetCurrentDirectory();
            foreach (string rel in relativePaths)
            {
                string path = System.IO.Path.Combine(cwd, rel);
                try { if (File.Exists(path)) return File.ReadAllText(path); } catch { }
            }
            return Loc.T("legal.fileMissing");
        }

        private string ReadOptionalFile(params string[] relativePaths)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string rel in relativePaths)
            {
                string path = System.IO.Path.Combine(baseDir, rel);
                try { if (File.Exists(path)) return File.ReadAllText(path); } catch { }
            }
            string cwd = Directory.GetCurrentDirectory();
            foreach (string rel in relativePaths)
            {
                string path = System.IO.Path.Combine(cwd, rel);
                try { if (File.Exists(path)) return File.ReadAllText(path); } catch { }
            }
            return "";
        }

        private string ReadBundledLicenseIndex()
        {
            string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "licenses");
            if (!Directory.Exists(dir)) dir = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "LcdFusion", "licenses");
            if (!Directory.Exists(dir)) return "";
            try
            {
                string[] files = Directory.GetFiles(dir, "*.txt");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                var text = new System.Text.StringBuilder();
                text.AppendLine("Bundled license files");
                foreach (string file in files) text.AppendLine("- " + System.IO.Path.GetFileName(file));
                return text.ToString();
            }
            catch { return ""; }
        }

        // ---- editor rebuild ---------------------------------------------------------

        private void RebuildEditor()
        {
            StyleTab(_tabValk, _activeScreen == ScreenId.Valkyrie);
            StyleTab(_tabBoWei, _activeScreen == ScreenId.BoWei);
            StyleTab(_tabThermal, _activeScreen == ScreenId.Thermalright);

            LcdScene scene = _engine.Scene(_activeScreen);
            _editorPanel.Children.Clear();

            var target = new CheckBox { Content = Loc.T("tgt.stream"), IsChecked = GetTarget(_activeScreen), Foreground = TextBr, FontSize = 13.5, FontWeight = FontWeights.SemiBold };
            target.Checked += delegate { SetTarget(true); };
            target.Unchecked += delegate { SetTarget(false); };
            _editorPanel.Children.Add(target);

            if (_activeScreen == ScreenId.Valkyrie)
            {
                _editorPanel.Children.Add(BuildValkyriePanelSelector());
            }
            else if (_activeScreen == ScreenId.BoWei)
            {
                _editorPanel.Children.Add(BuildBoWeiBrightnessControl());
            }

            _editorPanel.Children.Add(BuildOrientationControl());

            var screenRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (ScreenId other in new[] { ScreenId.Valkyrie, ScreenId.BoWei, ScreenId.Thermalright })
            {
                if (other == _activeScreen) continue;
                ScreenId dest = other;
                screenRow.Children.Add(Chip(Loc.T("screen.copyTo", ScreenDisplayName(dest)), delegate { CopyActiveSceneTo(dest); }));
            }
            _editorPanel.Children.Add(screenRow);

            _editorPanel.Children.Add(SectionLabel(Loc.T("sec.background"), 18));
            var bgRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            bgRow.Children.Add(Chip(Loc.T("bg.image"), delegate { PickMedia(false); }, scene.Background == BackgroundKind.Image));
            bgRow.Children.Add(Chip("GIF", delegate { PickMedia(true); }, scene.Background == BackgroundKind.Gif));
            bgRow.Children.Add(Chip(Loc.T("bg.wallpaper"), delegate { PickWallpaper(); }, scene.Background == BackgroundKind.Wallpaper));
            bgRow.Children.Add(Chip(Loc.T("bg.color"), delegate { PickBackgroundColor(); }, scene.Background == BackgroundKind.Color));
            bgRow.Children.Add(Chip(Loc.T("bg.none"), delegate { _engine.SetBackgroundKind(_activeScreen, BackgroundKind.None); MarkProfileDirty(); RebuildEditor(); }, scene.Background == BackgroundKind.None));
            _editorPanel.Children.Add(bgRow);

            if (scene.Background == BackgroundKind.Color)
            {
                _editorPanel.Children.Add(new TextBlock { Text = Loc.T("color.palette"), Foreground = MutedBr, FontSize = 12, Margin = new Thickness(2, 8, 0, 0) });
                _editorPanel.Children.Add(ColorPalette(scene.BgColor, color => SetBackgroundColor(color)));
            }

            _stretchXSlider = null; _stretchYSlider = null;
            _stretchXText = null; _stretchYText = null;
            if (scene.HasTransformableMedia())
            {
                string mediaCaption = scene.Background == BackgroundKind.Wallpaper && !string.IsNullOrEmpty(scene.WallpaperType)
                    ? scene.MediaName + "  [" + scene.WallpaperType + "]"
                    : scene.MediaName;
                var captionRow = new DockPanel { Margin = new Thickness(2, 8, 0, 0) };
                if (scene.Background == BackgroundKind.Wallpaper &&
                    (string.Equals(scene.WallpaperType, "scene", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(scene.WallpaperType, "web", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrEmpty(scene.MediaPath))
                {
                    WallpaperEntry wpMeta = WallpaperCatalog.TryLoadWallpaperDir(scene.MediaPath);
                    if (wpMeta != null && wpMeta.Properties.Count > 0)
                    {
                        var propsBtn = Chip(Loc.T("wpe.propsBtn") + " (" + wpMeta.Properties.Count + ")", delegate { EditWallpaperProperties(); });
                        propsBtn.Margin = new Thickness(8, 0, 0, 0);
                        DockPanel.SetDock(propsBtn, Dock.Right);
                        captionRow.Children.Add(propsBtn);
                    }
                }
                captionRow.Children.Add(new TextBlock { Text = mediaCaption, Foreground = MutedBr, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                _editorPanel.Children.Add(captionRow);
                var tr = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                tr.Children.Add(Chip("↺ 90°", delegate { _engine.Edit(_activeScreen, s => s.Rotation = (s.Rotation + 270) % 360); MarkProfileDirty(); RebuildEditor(); }));
                tr.Children.Add(Chip("↻ 90°", delegate { _engine.Edit(_activeScreen, s => s.Rotation = (s.Rotation + 90) % 360); MarkProfileDirty(); RebuildEditor(); }));
                tr.Children.Add(Chip(Loc.T("tf.mirror"), delegate { _engine.Edit(_activeScreen, s => s.FlipH = !s.FlipH); MarkProfileDirty(); RebuildEditor(); }, scene.FlipH));
                tr.Children.Add(Chip(Loc.T("tf.fit"), delegate { _engine.Edit(_activeScreen, s => s.Fit = FitMode.Fit); MarkProfileDirty(); RebuildEditor(); }, scene.Fit == FitMode.Fit));
                tr.Children.Add(Chip(Loc.T("tf.fill"), delegate { _engine.Edit(_activeScreen, s => s.Fit = FitMode.Fill); MarkProfileDirty(); RebuildEditor(); }, scene.Fit == FitMode.Fill));
                tr.Children.Add(Chip(Loc.T("tf.stretch"), delegate { _engine.Edit(_activeScreen, s => s.Fit = FitMode.Stretch); MarkProfileDirty(); RebuildEditor(); }, scene.Fit == FitMode.Stretch));
                tr.Children.Add(Chip(Loc.T("tf.reset"), delegate { _engine.Edit(_activeScreen, s => { s.Rotation = 0; s.FlipH = false; s.Scale = 1; s.ScaleX = 1; s.ScaleY = 1; s.PanX = 0; s.PanY = 0; }); MarkProfileDirty(); RebuildEditor(); }));
                _editorPanel.Children.Add(tr);
                _editorPanel.Children.Add(Slider(Loc.T("sl.zoom"), 0.2, 3.0, scene.Scale, v => _engine.Edit(_activeScreen, s => s.Scale = v), v => v.ToString("0.00") + "×"));
                if (scene.Fit == FitMode.Stretch)
                {
                    _editorPanel.Children.Add(BuildStretchSlider(Loc.T("sl.stretchX"), scene.ScaleX, true));
                    _editorPanel.Children.Add(BuildStretchSlider(Loc.T("sl.stretchY"), scene.ScaleY, false));
                }
                _editorPanel.Children.Add(Slider(Loc.T("sl.rotation"), 0, 360, scene.Rotation, v => _engine.Edit(_activeScreen, s => s.Rotation = v), v => ((int)v) + "°"));
            }

            if (_previewHint != null)
            {
                _previewHint.Text = (scene.HasTransformableMedia() && scene.Fit == FitMode.Stretch)
                    ? Loc.T("preview.hintStretch")
                    : Loc.T("preview.hint");
            }

            _editorPanel.Children.Add(SectionLabel(Loc.T("sec.layers"), 20));
            var add = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
            add.Children.Add(Chip(Loc.T("ly.addSensor"), delegate { AddOverlay(OverlayKind.CpuTemp); }));
            add.Children.Add(Chip(Loc.T("ly.addText"), delegate { AddOverlay(OverlayKind.Text); }));
            _editorPanel.Children.Add(add);

            for (int i = 0; i < scene.Overlays.Count; i++)
            {
                int index = i;
                _editorPanel.Children.Add(LayerRow(scene.Overlays[i], index));
            }

            if (scene.Overlays.Count == 0)
                _editorPanel.Children.Add(new TextBlock { Text = Loc.T("ly.empty"), Foreground = MutedBr, FontSize = 12, Margin = new Thickness(2, 6, 0, 0) });

            if (_sel >= 0 && _sel < scene.Overlays.Count)
                _editorPanel.Children.Add(OverlayEditor(scene.Overlays[_sel]));

            RefreshPreviewOverlay();
        }

        private UIElement BuildValkyriePanelSelector()
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock
            {
                Text = Loc.T("panel.label"),
                Foreground = MutedBr,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            });

            var combo = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
            string autoLabel = ValkyrieDirectService.IsAutoMode
                ? Loc.T("panel.autoDetected", ValkyrieDirectService.Width + "×" + ValkyrieDirectService.Height)
                : Loc.T("panel.auto");
            combo.Items.Add(autoLabel);
            int selected = 0;
            for (int i = 0; i < ProductCatalog.ValkyriePanels.Length; i++)
            {
                ValkyriePanelSpec spec = ProductCatalog.ValkyriePanels[i];
                combo.Items.Add(spec.DisplayName);
                if (!ValkyrieDirectService.IsAutoMode &&
                    string.Equals(_valkPanelMode, spec.Key, StringComparison.OrdinalIgnoreCase))
                    selected = i + 1;
            }
            combo.SelectedIndex = selected;
            combo.SelectionChanged += delegate
            {
                int idx = combo.SelectedIndex;
                if (idx < 0) return;
                string nextMode = idx == 0 ? "auto" : ProductCatalog.ValkyriePanels[idx - 1].Key;
                if (string.Equals(nextMode, _valkPanelMode, StringComparison.OrdinalIgnoreCase)) return;
                _valkPanelMode = nextMode;
                ValkyrieDirectService.ConfigurePanel(_valkPanelMode, _lastDetectedValkPanel);
                if (ValkyrieDirectService.IsAutoMode && !VendorService.IsMythCoolRunning())
                {
                    try { ValkyrieDirectService.TryProbePanel(); } catch { }
                }
                _engine.InvalidateScreen(ScreenId.Valkyrie);
                SaveCurrentSettings();
                UpdateDeviceTooltips();
                ApplyPreviewAspect();
                RebuildEditor();
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            return row;
        }

        private UIElement BuildBoWeiBrightnessControl()
        {
            var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = Loc.T("sl.brightness"), Foreground = MutedBr, FontSize = 12 });
            var valueText = new TextBlock { Text = _boweiBrightness + "%", Foreground = TextBr, FontSize = 12, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(valueText, 1);
            head.Children.Add(valueText);
            stack.Children.Add(head);

            var slider = new Slider { Minimum = 5, Maximum = 100, Value = _boweiBrightness, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 3, 0, 0) };
            slider.ValueChanged += delegate
            {
                int val = (int)Math.Round(slider.Value);
                valueText.Text = val + "%";
                _boweiBrightness = val;
                BoWeiDirectService.SetBrightness(val);
            };
            slider.PreviewMouseUp += delegate { SaveCurrentSettings(); };
            stack.Children.Add(slider);
            return stack;
        }

        private UIElement BuildOrientationControl()
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            wrap.Children.Add(new TextBlock { Text = Loc.T("screen.orientation"), Foreground = MutedBr, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
            var row = new WrapPanel();
            int current = _engine.EffectiveRotation(_activeScreen);
            foreach (int deg in new[] { 0, 90, 180, 270 })
            {
                int d = deg;
                row.Children.Add(Chip(d + "°", delegate { SetScreenOrientation(d); }, current == d));
            }
            wrap.Children.Add(row);
            return wrap;
        }

        private void SetScreenOrientation(int degrees)
        {
            switch (_activeScreen)
            {
                case ScreenId.BoWei: _boweiRotation = degrees; break;
                case ScreenId.Thermalright: _thermalRotation = degrees; break;
                default: _valkRotation = degrees; break;
            }
            _engine.SetScreenRotation(_activeScreen, degrees);
            SaveCurrentSettings();
            ApplyPreviewAspect();
            RebuildEditor();
        }

        private UIElement LayerRow(Overlay o, int index)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = WpfColor(o.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock { Text = LayerLabel(o), Foreground = TextBr, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis });

            var sel = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch };
            sel.Style = (Style)FindResource("BtnGhost");
            sel.Click += delegate { _sel = index; RebuildEditor(); };
            if (index == _sel) { sel.Background = Br("#1E2D49"); sel.BorderBrush = AccentBr; }
            Grid.SetColumn(sel, 0);
            grid.Children.Add(sel);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            actions.Children.Add(MiniLayerButton("↑", Loc.T("ov.moveUp"), "BtnChip", index > 0, delegate { MoveOverlay(index, -1); }));
            actions.Children.Add(MiniLayerButton("↓", Loc.T("ov.moveDown"), "BtnChip", index < _engine.Scene(_activeScreen).Overlays.Count - 1, delegate { MoveOverlay(index, 1); }));
            actions.Children.Add(MiniLayerButton("⧉", Loc.T("ov.duplicate"), "BtnChip", true, delegate { DuplicateOverlayAt(index); }));
            actions.Children.Add(MiniLayerButton("×", Loc.T("ov.remove"), "BtnDanger", true, delegate { RemoveOverlayAt(index); }));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            return grid;
        }

        private UIElement OverlayEditor(Overlay o)
        {
            var box = new Border { Background = Panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 6, 0, 0), BorderBrush = SoftBr, BorderThickness = new Thickness(1) };
            var stack = new StackPanel();

            stack.Children.Add(new TextBlock { Text = Loc.T("ov.properties") + " - " + LayerLabel(o), Foreground = TextBr, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = Loc.T("ov.type"), Foreground = MutedBr, FontSize = 12 });
            var combo = new ComboBox { Margin = new Thickness(0, 5, 0, 0) };
            for (int i = 0; i < MetricKinds.Length; i++) combo.Items.Add(MetricName(i));
            combo.SelectedIndex = KindIndex(o.Kind);
            combo.SelectionChanged += delegate
            {
                int idx = combo.SelectedIndex;
                if (idx < 0) return;
                _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].Kind = MetricKinds[idx]; });
                MarkProfileDirty();
                RebuildEditor();
            };
            stack.Children.Add(combo);

            if (o.Kind == OverlayKind.Text)
            {
                var tb = new TextBox { Text = o.Text, Margin = new Thickness(0, 10, 0, 0) };
                tb.TextChanged += delegate { _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].Text = tb.Text; }); MarkProfileDirty(); };
                stack.Children.Add(tb);
            }

            stack.Children.Add(Slider(Loc.T("sl.size"), 0.02, 0.9, o.Size, v => _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].Size = v; }), v => (int)(v * 100) + "%"));
            stack.Children.Add(Slider(Loc.T("sl.rotation"), 0, 360, o.Rotation, v => _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].Rotation = v; }), v => ((int)v) + "°"));
            stack.Children.Add(Slider(Loc.T("sl.x"), 0, 1, o.X, v => _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].X = v; }), v => (int)(v * 100) + "%"));
            stack.Children.Add(Slider(Loc.T("sl.y"), 0, 1, o.Y, v => _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].Y = v; }), v => (int)(v * 100) + "%"));

            stack.Children.Add(new TextBlock { Text = Loc.T("ov.color"), Foreground = MutedBr, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(ColorPalette(o.Color, color =>
            {
                _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].Color = color; });
                MarkProfileDirty();
                RebuildEditor();
            }));

            var bottom = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            var labelToggle = new CheckBox { Content = Loc.T("ov.label"), IsChecked = o.ShowLabel, Foreground = TextBr, FontSize = 13, Margin = new Thickness(0, 4, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            labelToggle.Checked += delegate { _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].ShowLabel = true; }); MarkProfileDirty(); RefreshPreviewOverlay(); };
            labelToggle.Unchecked += delegate { _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) s.Overlays[_sel].ShowLabel = false; }); MarkProfileDirty(); RefreshPreviewOverlay(); };
            bottom.Children.Add(labelToggle);
            bottom.Children.Add(Chip(Loc.T("ov.center"), delegate { _engine.Edit(_activeScreen, s => { if (_sel < s.Overlays.Count) { s.Overlays[_sel].X = 0.5; s.Overlays[_sel].Y = 0.5; } }); MarkProfileDirty(); RefreshPreviewOverlay(); }));
            stack.Children.Add(bottom);

            box.Child = stack;
            return box;
        }

        // ---- actions ----------------------------------------------------------------

        private void PickMedia(bool gif)
        {
            var dialog = new OpenFileDialog
            {
                Title = gif ? Loc.T("dlg.pickGif") : Loc.T("dlg.pickImage"),
                Filter = gif ? "GIF|*.gif" : Loc.T("dlg.images") + "|*.png;*.jpg;*.jpeg;*.bmp"
            };
            if (dialog.ShowDialog(this) != true) return;
            if (gif) _engine.LoadGif(_activeScreen, dialog.FileName);
            else _engine.LoadImage(_activeScreen, dialog.FileName);
            MarkProfileDirty();
            RebuildEditor();
        }

        private void PickWallpaper()
        {
            List<WallpaperEntry> items = WallpaperCatalog.DiscoverWorkshopWallpapers();
            string chosenDir = null;

            var dlg = new Window
            {
                Title = Loc.T("wpe.title"),
                Owner = this,
                Width = 760,
                Height = 600,
                MinWidth = 600,
                MinHeight = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Bg,
                Foreground = TextBr,
                FontFamily = new FontFamily("Segoe UI"),
                Icon = BuildWindowIcon()
            };
            dlg.Resources.MergedDictionaries.Add(Theme.Build());
            Theme.ApplyDarkTitleBar(dlg);

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // renderer & assets paths
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // search box
            root.RowDefinitions.Add(new RowDefinition());                            // wallpaper list
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // footer

            // Renderer & assets config bar
            var cfgPanel = new Border { Background = Panel, BorderBrush = SoftBr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12) };
            var cfgStack = new StackPanel();

            var rendererPathText = new TextBlock { Foreground = MutedBr, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var assetsPathText = new TextBlock { Foreground = MutedBr, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

            Action refreshPathLabels = delegate
            {
                string r = _engine.ResolvedWallpaperRendererPath();
                string a = _engine.ResolvedWallpaperAssetsDir();
                rendererPathText.Text = string.IsNullOrEmpty(r) ? Loc.T("wpe.notFound") : r;
                rendererPathText.Foreground = string.IsNullOrEmpty(r) ? RedBr : MutedBr;
                assetsPathText.Text = string.IsNullOrEmpty(a) ? Loc.T("wpe.notFound") : a;
            };
            refreshPathLabels();

            Func<string, TextBlock, Action, UIElement> makePathRow = (label, valueText, onBrowse) =>
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = label, Foreground = TextBr, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(valueText, 1);
                valueText.Margin = new Thickness(8, 0, 10, 0);
                g.Children.Add(valueText);
                var btn = new Button { Content = Loc.T("wpe.browse"), Style = (Style)FindResource("BtnChip"), Padding = new Thickness(10, 4, 10, 4) };
                btn.Click += delegate { onBrowse(); };
                Grid.SetColumn(btn, 2);
                g.Children.Add(btn);
                return g;
            };

            cfgStack.Children.Add(makePathRow(Loc.T("wpe.renderer"), rendererPathText, delegate
            {
                var ofd = new OpenFileDialog { Title = "wpe-renderer.exe", Filter = "Executable|*.exe" };
                if (ofd.ShowDialog(dlg) != true) return;
                _wallpaperRendererPath = ofd.FileName;
                _engine.ConfigureWallpaper(_wallpaperRendererPath, _wallpaperAssetsDir);
                SaveCurrentSettings();
                refreshPathLabels();
            }));

            cfgStack.Children.Add(makePathRow(Loc.T("wpe.assets"), assetsPathText, delegate
            {
                using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
                {
                    fbd.Description = "Wallpaper Engine assets/";
                    if (!string.IsNullOrEmpty(_wallpaperAssetsDir) && Directory.Exists(_wallpaperAssetsDir))
                        fbd.SelectedPath = _wallpaperAssetsDir;
                    if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    _wallpaperAssetsDir = fbd.SelectedPath;
                    _engine.ConfigureWallpaper(_wallpaperRendererPath, _wallpaperAssetsDir);
                    SaveCurrentSettings();
                    refreshPathLabels();
                }
            }));

            cfgPanel.Child = cfgStack;
            Grid.SetRow(cfgPanel, 0);
            root.Children.Add(cfgPanel);

            // Search input
            var searchBox = new TextBox { Margin = new Thickness(0, 0, 0, 10), ToolTip = Loc.T("wpe.search") };
            Grid.SetRow(searchBox, 1);
            root.Children.Add(searchBox);

            // Workshop list
            var wrap = new WrapPanel { Margin = new Thickness(2) };
            var scroll = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Panel };
            var listBorder = new Border { Child = scroll, Background = Panel, BorderBrush = SoftBr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(8) };
            Grid.SetRow(listBorder, 2);
            root.Children.Add(listBorder);

            WallpaperEntry selectedEntry = null;
            var applyBtn = new Button { Content = Loc.T("wpe.apply"), Style = (Style)FindResource("BtnPrimary"), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };

            Action rebuildCards = null;
            rebuildCards = delegate
            {
                wrap.Children.Clear();
                string filter = (searchBox.Text ?? "").Trim();
                int shown = 0;

                foreach (WallpaperEntry entry in items)
                {
                    if (filter.Length > 0 &&
                        entry.DisplayTitle.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        entry.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        entry.Type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    shown++;
                    WallpaperEntry current = entry;
                    bool isSel = selectedEntry != null && string.Equals(selectedEntry.Directory, current.Directory, StringComparison.OrdinalIgnoreCase);

                    var cardStack = new StackPanel { Width = 152 };
                    var thumbBox = new Border { Width = 152, Height = 96, CornerRadius = new CornerRadius(6), Background = Surface, ClipToBounds = true };
                    BitmapImage thumb = TryLoadThumbnail(current.PreviewPath);
                    if (thumb != null)
                        thumbBox.Child = new Image { Source = thumb, Stretch = Stretch.UniformToFill };
                    else
                        thumbBox.Child = new TextBlock { Text = (current.Type ?? "scene").ToUpperInvariant(), Foreground = MutedBr, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };

                    cardStack.Children.Add(thumbBox);
                    cardStack.Children.Add(new TextBlock { Text = current.DisplayTitle, Foreground = TextBr, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 1) });
                    cardStack.Children.Add(new TextBlock { Text = current.Type + " · " + current.Id, Foreground = MutedBr, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });

                    var card = new Border
                    {
                        Child = cardStack,
                        Background = isSel ? Br("#1E2D49") : Surface2,
                        BorderBrush = isSel ? AccentBr : SoftBr,
                        BorderThickness = new Thickness(isSel ? 2 : 1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 0, 10, 10),
                        Cursor = Cursors.Hand
                    };

                    card.MouseLeftButtonDown += (s, ev) =>
                    {
                        selectedEntry = current;
                        applyBtn.IsEnabled = true;
                        if (ev.ClickCount >= 2)
                        {
                            chosenDir = current.Directory;
                            dlg.DialogResult = true;
                            dlg.Close();
                            return;
                        }
                        rebuildCards();
                    };

                    wrap.Children.Add(card);
                }

                if (shown == 0)
                {
                    wrap.Children.Add(new TextBlock
                    {
                        Text = Loc.T("wpe.empty"),
                        Foreground = MutedBr,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(10)
                    });
                }
            };

            searchBox.TextChanged += delegate { rebuildCards(); };
            rebuildCards();

            // Footer buttons
            var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var browseDirBtn = new Button { Content = Loc.T("wpe.browseFolder"), Style = (Style)FindResource("BtnGhost"), HorizontalAlignment = HorizontalAlignment.Left };
            browseDirBtn.Click += delegate
            {
                using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
                {
                    fbd.Description = Loc.T("wpe.browseFolder");
                    if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    chosenDir = fbd.SelectedPath;
                    dlg.DialogResult = true;
                    dlg.Close();
                }
            };
            footer.Children.Add(browseDirBtn);

            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
            var cancelBtn = new Button { Content = Loc.T("dlg.cancel"), Style = (Style)FindResource("BtnGhost") };
            cancelBtn.Click += delegate { dlg.DialogResult = false; dlg.Close(); };
            applyBtn.Click += delegate
            {
                if (selectedEntry == null) return;
                chosenDir = selectedEntry.Directory;
                dlg.DialogResult = true;
                dlg.Close();
            };
            rightBtns.Children.Add(cancelBtn);
            rightBtns.Children.Add(applyBtn);
            Grid.SetColumn(rightBtns, 1);
            footer.Children.Add(rightBtns);

            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            dlg.Content = root;
            if (dlg.ShowDialog() != true || string.IsNullOrEmpty(chosenDir)) return;

            if (_engine.LoadWallpaper(_activeScreen, chosenDir))
            {
                MarkProfileDirty();
                RebuildEditor();
            }
        }

        private void EditWallpaperProperties()
        {
            LcdScene scene = _engine.Scene(_activeScreen);
            if (scene.Background != BackgroundKind.Wallpaper || string.IsNullOrEmpty(scene.MediaPath)) return;

            WallpaperEntry entry = WallpaperCatalog.TryLoadWallpaperDir(scene.MediaPath);
            if (entry == null) return;

            var dlg = new Window
            {
                Title = Loc.T("wpe.propsTitle") + " — " + entry.DisplayTitle,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 580,
                Height = 600,
                MinWidth = 460,
                MinHeight = 400,
                Background = Bg,
                Foreground = TextBr,
                FontFamily = new FontFamily("Segoe UI"),
                Icon = BuildWindowIcon()
            };
            dlg.Resources.MergedDictionaries.Add(Theme.Build());
            Theme.ApplyDarkTitleBar(dlg);

            var root = new Grid { Margin = new Thickness(22, 18, 22, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            head.Children.Add(new TextBlock
            {
                Text = entry.DisplayTitle,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBr,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            string subtitle = Loc.T("wpe.propsTitle") +
                (string.IsNullOrEmpty(entry.Type) ? "" : " · " + entry.Type) +
                " (" + entry.Properties.Count + ")";
            head.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = MutedBr,
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var listStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = listStack
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            // Debounce continuous slider / text edits so we don't spam scene rebuilds per pixel.
            var pendingUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            Action flushPending = () =>
            {
                debounceTimer.Stop();
                if (pendingUpdates.Count == 0) return;
                var batch = new Dictionary<string, string>(pendingUpdates, StringComparer.OrdinalIgnoreCase);
                pendingUpdates.Clear();
                _engine.SetWallpaperProperties(_activeScreen, batch, entry.Properties);
                MarkProfileDirty();
            };
            debounceTimer.Tick += delegate { flushPending(); };

            Action<string, string, bool> emitProp = (key, val, immediate) =>
            {
                pendingUpdates[key] = val ?? "";
                if (immediate)
                {
                    flushPending();
                }
                else
                {
                    debounceTimer.Stop();
                    debounceTimer.Start();
                }
            };

            Action populateControls = null;
            populateControls = () =>
            {
                listStack.Children.Clear();
                if (entry.Properties.Count == 0)
                {
                    listStack.Children.Add(new TextBlock { Text = Loc.T("wpe.propsEmpty"), Foreground = MutedBr, FontSize = 13, Margin = new Thickness(4, 12, 4, 0) });
                    return;
                }

                var currentVals = new Dictionary<string, string>(scene.WallpaperProps, StringComparer.OrdinalIgnoreCase);

                foreach (WallpaperPropertyDef def in entry.Properties)
                {
                    WallpaperPropertyDef prop = def;
                    string curStr;
                    if (!currentVals.TryGetValue(prop.Key, out curStr) || curStr == null)
                        curStr = prop.DefaultValue ?? "";

                    var card = new Border
                    {
                        Background = Surface,
                        BorderBrush = SoftBr,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(14, 11, 14, 11),
                        Margin = new Thickness(0, 0, 0, 9)
                    };

                    if (prop.Type == "bool")
                    {
                        bool isChecked = ParseBoolString(curStr, false);
                        var cb = new CheckBox
                        {
                            Content = prop.Label,
                            IsChecked = isChecked,
                            Foreground = TextBr,
                            FontSize = 13
                        };
                        cb.Checked += delegate { emitProp(prop.Key, "true", true); };
                        cb.Unchecked += delegate { emitProp(prop.Key, "false", true); };
                        card.Child = cb;
                    }
                    else if (prop.Type == "slider")
                    {
                        double val = ParseDoubleString(curStr, prop.Min);
                        val = Math.Max(prop.Min, Math.Min(prop.Max, val));
                        var box = new StackPanel();
                        var hdr = new DockPanel();
                        var valTxt = new TextBlock
                        {
                            Text = val.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                            Foreground = TextBr,
                            FontSize = 12,
                            FontWeight = FontWeights.SemiBold,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        var valBadge = new Border
                        {
                            Background = Panel,
                            BorderBrush = SoftBr,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(8, 2, 8, 2),
                            Child = valTxt
                        };
                        DockPanel.SetDock(valBadge, Dock.Right);
                        hdr.Children.Add(valBadge);
                        hdr.Children.Add(new TextBlock { Text = prop.Label, Foreground = TextBr, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                        box.Children.Add(hdr);

                        var sl = new Slider
                        {
                            Minimum = prop.Min,
                            Maximum = prop.Max,
                            Value = val,
                            SmallChange = prop.Step,
                            LargeChange = Math.Max(prop.Step * 5.0, (prop.Max - prop.Min) / 10.0),
                            Margin = new Thickness(0, 6, 0, 0)
                        };
                        sl.ValueChanged += delegate
                        {
                            string sVal = sl.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                            valTxt.Text = sl.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                            emitProp(prop.Key, sVal, false);
                        };
                        box.Children.Add(sl);
                        card.Child = box;
                    }
                    else if (prop.Type == "color")
                    {
                        System.Drawing.Color initColor = ParseWpeColor(curStr, System.Drawing.Color.White);
                        var row = new DockPanel();
                        System.Drawing.Color liveColor = initColor;

                        var swatch = new Border
                        {
                            Width = 20,
                            Height = 20,
                            CornerRadius = new CornerRadius(5),
                            BorderBrush = BorderBr,
                            BorderThickness = new Thickness(1),
                            Background = WpfColor(initColor),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        var hexTxt = new TextBlock
                        {
                            Text = string.Format("#{0:X2}{1:X2}{2:X2}", initColor.R, initColor.G, initColor.B),
                            Foreground = TextBr,
                            FontSize = 12,
                            FontFamily = new FontFamily("Consolas"),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(8, 0, 0, 0)
                        };
                        var colorBtnContent = new StackPanel { Orientation = Orientation.Horizontal };
                        colorBtnContent.Children.Add(swatch);
                        colorBtnContent.Children.Add(hexTxt);

                        var colorBtn = new Button
                        {
                            Content = colorBtnContent,
                            Style = (Style)dlg.FindResource("BtnChip"),
                            Padding = new Thickness(8, 5, 10, 5)
                        };
                        colorBtn.Click += delegate
                        {
                            using (var cd = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = liveColor })
                            {
                                if (cd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                                liveColor = cd.Color;
                                swatch.Background = WpfColor(liveColor);
                                hexTxt.Text = string.Format("#{0:X2}{1:X2}{2:X2}", liveColor.R, liveColor.G, liveColor.B);
                                string wpeColor = string.Format(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    "{0:0.###} {1:0.###} {2:0.###}",
                                    liveColor.R / 255.0, liveColor.G / 255.0, liveColor.B / 255.0);
                                emitProp(prop.Key, wpeColor, true);
                            }
                        };
                        DockPanel.SetDock(colorBtn, Dock.Right);
                        row.Children.Add(colorBtn);
                        row.Children.Add(new TextBlock { Text = prop.Label, Foreground = TextBr, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                        card.Child = row;
                    }
                    else if (prop.Type == "combo")
                    {
                        var box = new StackPanel();
                        box.Children.Add(new TextBlock { Text = prop.Label, Foreground = MutedBr, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
                        var combo = new ComboBox();
                        int selIdx = -1;
                        for (int i = 0; i < prop.Options.Count; i++)
                        {
                            combo.Items.Add(prop.Options[i].Label);
                            if (string.Equals(prop.Options[i].Value, curStr, StringComparison.OrdinalIgnoreCase))
                                selIdx = i;
                        }
                        if (selIdx >= 0) combo.SelectedIndex = selIdx;
                        combo.SelectionChanged += delegate
                        {
                            int idx = combo.SelectedIndex;
                            if (idx >= 0 && idx < prop.Options.Count)
                                emitProp(prop.Key, prop.Options[idx].Value, true);
                        };
                        box.Children.Add(combo);
                        card.Child = box;
                    }
                    else if (prop.Type == "textinput")
                    {
                        var box = new StackPanel();
                        box.Children.Add(new TextBlock { Text = prop.Label, Foreground = MutedBr, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
                        var tb = new TextBox { Text = curStr };
                        tb.TextChanged += delegate { emitProp(prop.Key, tb.Text, false); };
                        box.Children.Add(tb);
                        card.Child = box;
                    }

                    listStack.Children.Add(card);
                }
            };

            populateControls();

            var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var resetBtn = new Button { Content = Loc.T("wpe.propsReset"), Style = (Style)dlg.FindResource("BtnGhost") };
            resetBtn.Click += delegate
            {
                debounceTimer.Stop();
                pendingUpdates.Clear();
                _engine.ResetWallpaperProperties(_activeScreen, entry.Properties);
                MarkProfileDirty();
                populateControls();
            };
            DockPanel.SetDock(resetBtn, Dock.Left);
            footer.Children.Add(resetBtn);

            var closeBtn = new Button { Content = Loc.T("dlg.close"), Style = (Style)dlg.FindResource("BtnPrimary"), HorizontalAlignment = HorizontalAlignment.Right };
            closeBtn.Click += delegate { flushPending(); dlg.Close(); };
            footer.Children.Add(closeBtn);

            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            dlg.Closed += delegate { flushPending(); };
            dlg.Content = root;
            dlg.ShowDialog();
        }

        private static bool ParseBoolString(string s, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            bool b;
            if (bool.TryParse(s.Trim(), out b)) return b;
            if (s.Trim() == "1") return true;
            if (s.Trim() == "0") return false;
            return fallback;
        }

        private static double ParseDoubleString(string s, double fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            double d;
            return double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : fallback;
        }

        private static System.Drawing.Color ParseWpeColor(string s, System.Drawing.Color fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            string[] parts = s.Trim().Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return fallback;
            double r, g, b;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r)) return fallback;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g)) return fallback;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b)) return fallback;
            int ri = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, r)) * 255.0);
            int gi = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, g)) * 255.0);
            int bi = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, b)) * 255.0);
            return System.Drawing.Color.FromArgb(255, ri, gi, bi);
        }

        private static BitmapImage TryLoadThumbnail(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 180;
                img.UriSource = new Uri(path, UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        private void CopyActiveSceneTo(ScreenId targetScreen)
        {
            _engine.CopyScene(_activeScreen, targetScreen);
            MarkProfileDirty();
            SetStatus(true, Loc.T("screen.copiedTo", ScreenDisplayName(targetScreen)));
            RefreshPreviewOverlay();
        }

        private bool PickColor(System.Drawing.Color initial, out System.Drawing.Color picked)
        {
            picked = initial;
            using (var dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.AllowFullOpen = true;
                dialog.FullOpen = true;
                dialog.Color = System.Drawing.Color.FromArgb(initial.R, initial.G, initial.B);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
                picked = System.Drawing.Color.FromArgb(255, dialog.Color.R, dialog.Color.G, dialog.Color.B);
                RememberColor(picked, false);
                return true;
            }
        }

        private void PickBackgroundColor()
        {
            System.Drawing.Color picked;
            System.Drawing.Color initial = _engine.Scene(_activeScreen).BgColor;
            if (!PickColor(initial, out picked)) return;
            _engine.Edit(_activeScreen, s => { s.Background = BackgroundKind.Color; s.BgColor = picked; });
            MarkProfileDirty();
            RebuildEditor();
        }

        private void SetBackgroundColor(System.Drawing.Color color)
        {
            RememberColor(color, false);
            _engine.Edit(_activeScreen, s => { s.Background = BackgroundKind.Color; s.BgColor = color; });
            MarkProfileDirty();
            RebuildEditor();
        }

        private void AddOverlay(OverlayKind kind)
        {
            int nw, nh;
            _engine.CanvasSize(_activeScreen, out nw, out nh);
            double defaultSize = nh > nw ? 0.08 : (nw == nh ? 0.16 : (nw > nh * 2 ? 0.36 : 0.22));
            _engine.Edit(_activeScreen, s =>
            {
                Overlay o = new Overlay { Kind = kind, X = 0.5, Y = 0.5, Size = defaultSize };
                if (kind == OverlayKind.Text) o.Text = Loc.T("text.default");
                s.Overlays.Add(o);
                _sel = s.Overlays.Count - 1;
            });
            MarkProfileDirty();
            RebuildEditor();
        }

        private void RemoveOverlayAt(int index)
        {
            _engine.Edit(_activeScreen, s => { if (index >= 0 && index < s.Overlays.Count) s.Overlays.RemoveAt(index); });
            if (_sel == index) _sel = -1;
            else if (_sel > index) _sel--;
            MarkProfileDirty();
            RebuildEditor();
        }

        private void DuplicateOverlayAt(int index)
        {
            _engine.Edit(_activeScreen, s =>
            {
                if (index < 0 || index >= s.Overlays.Count) return;
                Overlay copy = s.Overlays[index].Clone();
                copy.X = Clamp01(copy.X + 0.06);
                copy.Y = Clamp01(copy.Y + 0.06);
                s.Overlays.Insert(index + 1, copy);
                _sel = index + 1;
            });
            MarkProfileDirty();
            RebuildEditor();
        }

        private void MoveOverlay(int index, int delta)
        {
            _engine.Edit(_activeScreen, s =>
            {
                int next = index + delta;
                if (index < 0 || index >= s.Overlays.Count || next < 0 || next >= s.Overlays.Count) return;
                Overlay item = s.Overlays[index];
                s.Overlays.RemoveAt(index);
                s.Overlays.Insert(next, item);
                _sel = next;
            });
            MarkProfileDirty();
            RebuildEditor();
        }

        private bool GetTarget(ScreenId screen)
        {
            switch (screen)
            {
                case ScreenId.BoWei: return _tgtBoWei;
                case ScreenId.Thermalright: return _tgtThermal;
                default: return _tgtValk;
            }
        }

        private void SetTarget(bool on)
        {
            switch (_activeScreen)
            {
                case ScreenId.BoWei: _tgtBoWei = on; break;
                case ScreenId.Thermalright: _tgtThermal = on; break;
                default: _tgtValk = on; break;
            }
            _engine.SetTargets(_tgtValk, _tgtBoWei, _tgtThermal);
            MarkProfileDirty();
        }

        private void SwitchTab(ScreenId screen)
        {
            if (_activeScreen == screen) return;
            _activeScreen = screen;
            _sel = -1;
            _engine.SetPreviewTarget(screen);
            ApplyPreviewAspect();
            RebuildEditor();
        }

        private void StartStreaming()
        {
            if (!_tgtValk && !_tgtBoWei && !_tgtThermal)
            {
                MessageBox.Show(this, Loc.T("msg.noTarget"), "LCD Fusion");
                return;
            }
            SetStreaming(true);
        }

        private void StopStreaming() { SetStreaming(false); }

        private void SetStreaming(bool on)
        {
            if (on && !_tgtValk && !_tgtBoWei && !_tgtThermal) on = false;
            if (on)
            {
                _engine.SetTargets(_tgtValk, _tgtBoWei, _tgtThermal);
                _engine.StartStreaming();
                SetStatus(true, Loc.T("st.starting"));
            }
            else
            {
                _engine.StopStreaming();
            }
            if (_startButton != null) _startButton.IsEnabled = !on;
            if (_stopButton != null) _stopButton.IsEnabled = on;
        }

        private void FreeDevices()
        {
            SetStatus(true, Loc.T("st.freeing"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool stopped = VendorService.StopConflictingSoftware();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    RefreshStatus();
                    SetStatus(stopped, stopped ? Loc.T("st.freed") : Loc.T("st.freeFail"));
                }));
            });
        }

        // ---- preview drag & stretch -------------------------------------------------

        private UIElement BuildStretchSlider(string label, double value, bool isX)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = label, Foreground = MutedBr, FontSize = 12 });
            double v0 = ClampStretch(value);
            var valueText = new TextBlock { Text = ((int)Math.Round(v0 * 100)) + "%", Foreground = TextBr, FontSize = 12, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(valueText, 1);
            head.Children.Add(valueText);
            stack.Children.Add(head);

            var slider = new Slider { Minimum = 0.1, Maximum = 4.0, Value = v0, Margin = new Thickness(0, 3, 0, 0) };
            slider.ValueChanged += delegate
            {
                if (_syncingStretchSliders) return;
                double nv = ClampStretch(slider.Value);
                valueText.Text = ((int)Math.Round(nv * 100)) + "%";
                _engine.Edit(_activeScreen, s => { if (isX) s.ScaleX = nv; else s.ScaleY = nv; });
                MarkProfileDirty();
                RefreshPreviewOverlay();
            };
            stack.Children.Add(slider);

            if (isX) { _stretchXSlider = slider; _stretchXText = valueText; }
            else { _stretchYSlider = slider; _stretchYText = valueText; }
            return stack;
        }

        private void SyncStretchSliders(double sx, double sy)
        {
            _syncingStretchSliders = true;
            try
            {
                if (_stretchXSlider != null) _stretchXSlider.Value = ClampStretch(sx);
                if (_stretchXText != null) _stretchXText.Text = ((int)Math.Round(ClampStretch(sx) * 100)) + "%";
                if (_stretchYSlider != null) _stretchYSlider.Value = ClampStretch(sy);
                if (_stretchYText != null) _stretchYText.Text = ((int)Math.Round(ClampStretch(sy) * 100)) + "%";
            }
            finally { _syncingStretchSliders = false; }
        }

        private static double ClampStretch(double v)
        {
            if (v <= 0) return 1.0;
            if (v < 0.1) return 0.1;
            if (v > 4.0) return 4.0;
            return v;
        }

        private void PreviewDown(object sender, MouseButtonEventArgs e)
        {
            double fx, fy; Frac(e, out fx, out fy);
            Point pt = e.GetPosition(_previewBorder);
            LcdScene scene = _engine.Scene(_activeScreen);

            if (scene.HasTransformableMedia() && scene.Fit == FitMode.Stretch)
            {
                StretchHandle handle = HitTestStretchHandle(scene, pt.X, pt.Y);
                if (handle != StretchHandle.None)
                {
                    _drag = DragMode.StretchBg;
                    _stretchHandle = handle;
                    _dragStartPxX = pt.X;
                    _dragStartPxY = pt.Y;
                    _panOrigX = scene.PanX;
                    _panOrigY = scene.PanY;
                    _dragOrigScaleX = ClampStretch(scene.ScaleX);
                    _dragOrigScaleY = ClampStretch(scene.ScaleY);
                    _dragOrigUserScale = scene.Scale <= 0 ? 0.01 : scene.Scale;
                    _dragOrigRotRad = scene.Rotation * Math.PI / 180.0;
                    _previewHost.CaptureMouse();
                    return;
                }
            }

            int hit = HitTest(scene, fx, fy);
            if (hit >= 0) { _sel = hit; _drag = DragMode.Overlay; RebuildEditor(); }
            else if (scene.HasTransformableMedia())
            { _drag = DragMode.Pan; _dragStartX = fx; _dragStartY = fy; _panOrigX = scene.PanX; _panOrigY = scene.PanY; }
            else _drag = DragMode.None;
            _previewHost.CaptureMouse();
        }

        private void PreviewMove(object sender, MouseEventArgs e)
        {
            Point pt = e.GetPosition(_previewBorder);
            if (_drag == DragMode.None || e.LeftButton != MouseButtonState.Pressed)
            {
                UpdatePreviewCursor(pt);
                return;
            }

            double fx, fy; Frac(e, out fx, out fy);
            if (_drag == DragMode.Overlay)
            {
                double cx = Clamp01(fx), cy = Clamp01(fy);
                _engine.Edit(_activeScreen, s => { if (_sel >= 0 && _sel < s.Overlays.Count) { s.Overlays[_sel].X = cx; s.Overlays[_sel].Y = cy; } });
                MarkProfileDirty();
                RefreshPreviewOverlay();
            }
            else if (_drag == DragMode.Pan)
            {
                double nx = _panOrigX + (fx - _dragStartX);
                double ny = _panOrigY + (fy - _dragStartY);
                _engine.Edit(_activeScreen, s => { s.PanX = nx; s.PanY = ny; });
                MarkProfileDirty();
                RefreshPreviewOverlay();
            }
            else if (_drag == DragMode.StretchBg)
            {
                double w = _previewBorder.ActualWidth, h = _previewBorder.ActualHeight;
                if (w <= 0 || h <= 0) return;

                double dx = pt.X - _dragStartPxX;
                double dy = pt.Y - _dragStartPxY;
                double cos = Math.Cos(_dragOrigRotRad);
                double sin = Math.Sin(_dragOrigRotRad);
                double dxLocal = dx * cos + dy * sin;
                double dyLocal = -dx * sin + dy * cos;

                int dirX = 0, dirY = 0;
                GetHandleDir(_stretchHandle, out dirX, out dirY);

                double origDw = w * _dragOrigUserScale * _dragOrigScaleX;
                double origDh = h * _dragOrigUserScale * _dragOrigScaleY;
                double newDw = dirX != 0 ? origDw + dirX * dxLocal : origDw;
                double newDh = dirY != 0 ? origDh + dirY * dyLocal : origDh;

                double newSx = dirX != 0 ? newDw / (w * _dragOrigUserScale) : _dragOrigScaleX;
                double newSy = dirY != 0 ? newDh / (h * _dragOrigUserScale) : _dragOrigScaleY;

                if (dirX != 0 && dirY != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    double rx = origDw > 1e-6 ? newDw / origDw : 1.0;
                    double ry = origDh > 1e-6 ? newDh / origDh : 1.0;
                    double r = Math.Abs(rx - 1.0) >= Math.Abs(ry - 1.0) ? rx : ry;
                    newSx = _dragOrigScaleX * r;
                    newSy = _dragOrigScaleY * r;
                }

                newSx = ClampStretch(newSx);
                newSy = ClampStretch(newSy);

                double actualDeltaW = newSx * w * _dragOrigUserScale - origDw;
                double actualDeltaH = newSy * h * _dragOrigUserScale - origDh;
                double shiftLocalX = dirX * actualDeltaW * 0.5;
                double shiftLocalY = dirY * actualDeltaH * 0.5;
                double shiftCanvasX = shiftLocalX * cos - shiftLocalY * sin;
                double shiftCanvasY = shiftLocalX * sin + shiftLocalY * cos;

                double newPanX = _panOrigX + shiftCanvasX / w;
                double newPanY = _panOrigY + shiftCanvasY / h;

                _engine.Edit(_activeScreen, s =>
                {
                    s.ScaleX = newSx;
                    s.ScaleY = newSy;
                    s.PanX = newPanX;
                    s.PanY = newPanY;
                });
                SyncStretchSliders(newSx, newSy);
                MarkProfileDirty();
                RefreshPreviewOverlay();
            }
        }

        private void PreviewUp(object sender, MouseButtonEventArgs e)
        {
            _drag = DragMode.None;
            _stretchHandle = StretchHandle.None;
            if (_previewHost != null && _previewHost.IsMouseCaptured)
                _previewHost.ReleaseMouseCapture();
            UpdatePreviewCursor(e.GetPosition(_previewBorder));
        }

        private void PreviewLeave(object sender, MouseEventArgs e)
        {
            if (_drag == DragMode.None && _previewHost != null)
                _previewHost.Cursor = Cursors.Arrow;
        }

        private void UpdatePreviewCursor(Point pt)
        {
            if (_previewHost == null) return;
            LcdScene scene = _engine.Scene(_activeScreen);
            if (scene.HasTransformableMedia() && scene.Fit == FitMode.Stretch)
            {
                StretchHandle handle = HitTestStretchHandle(scene, pt.X, pt.Y);
                if (handle != StretchHandle.None)
                {
                    _previewHost.Cursor = CursorForHandle(handle, scene.Rotation);
                    return;
                }
            }
            _previewHost.Cursor = scene.HasTransformableMedia() || scene.Overlays.Count > 0 ? Cursors.SizeAll : Cursors.Arrow;
        }

        private static void GetHandleDir(StretchHandle handle, out int dirX, out int dirY)
        {
            dirX = 0; dirY = 0;
            switch (handle)
            {
                case StretchHandle.Left: dirX = -1; break;
                case StretchHandle.Right: dirX = 1; break;
                case StretchHandle.Top: dirY = -1; break;
                case StretchHandle.Bottom: dirY = 1; break;
                case StretchHandle.TopLeft: dirX = -1; dirY = -1; break;
                case StretchHandle.TopRight: dirX = 1; dirY = -1; break;
                case StretchHandle.BottomLeft: dirX = -1; dirY = 1; break;
                case StretchHandle.BottomRight: dirX = 1; dirY = 1; break;
            }
        }

        private static Cursor CursorForHandle(StretchHandle handle, double rotationDeg)
        {
            int dirX, dirY;
            GetHandleDir(handle, out dirX, out dirY);
            if (dirX == 0 && dirY == 0) return Cursors.Arrow;
            double angle = Math.Atan2(dirY, dirX) * 180.0 / Math.PI + rotationDeg;
            angle = ((angle % 180.0) + 180.0) % 180.0;
            if (angle < 22.5 || angle >= 157.5) return Cursors.SizeWE;
            if (angle < 67.5) return Cursors.SizeNWSE;
            if (angle < 112.5) return Cursors.SizeNS;
            return Cursors.SizeNESW;
        }

        private static Point StretchPoint(double cx, double cy, double lx, double ly, double cos, double sin)
        {
            return new Point(cx + lx * cos - ly * sin, cy + lx * sin + ly * cos);
        }

        private void GetStretchGeometry(LcdScene scene, double w, double h,
            out double cx, out double cy, out double halfW, out double halfH, out double cos, out double sin)
        {
            double userScale = scene.Scale <= 0 ? 0.01 : scene.Scale;
            double sx = ClampStretch(scene.ScaleX);
            double sy = ClampStretch(scene.ScaleY);
            cx = w * (0.5 + scene.PanX);
            cy = h * (0.5 + scene.PanY);
            halfW = w * userScale * sx * 0.5;
            halfH = h * userScale * sy * 0.5;
            double rad = scene.Rotation * Math.PI / 180.0;
            cos = Math.Cos(rad);
            sin = Math.Sin(rad);
        }

        private StretchHandle HitTestStretchHandle(LcdScene scene, double px, double py)
        {
            double w = _previewBorder.ActualWidth, h = _previewBorder.ActualHeight;
            if (w <= 0 || h <= 0) return StretchHandle.None;

            double cx, cy, halfW, halfH, cos, sin;
            GetStretchGeometry(scene, w, h, out cx, out cy, out halfW, out halfH, out cos, out sin);

            StretchHandle[] handles =
            {
                StretchHandle.TopLeft, StretchHandle.TopRight, StretchHandle.BottomLeft, StretchHandle.BottomRight,
                StretchHandle.Left, StretchHandle.Right, StretchHandle.Top, StretchHandle.Bottom
            };

            const double hitRadius = 11.0;
            StretchHandle best = StretchHandle.None;
            double bestDist = hitRadius;
            foreach (StretchHandle handle in handles)
            {
                int dirX, dirY;
                GetHandleDir(handle, out dirX, out dirY);
                Point hp = StretchPoint(cx, cy, dirX * halfW, dirY * halfH, cos, sin);
                double d = Math.Sqrt((px - hp.X) * (px - hp.X) + (py - hp.Y) * (py - hp.Y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = handle;
                }
            }
            return best;
        }

        private void RefreshPreviewOverlay()
        {
            if (_previewOverlay == null || _previewBorder == null) return;
            _previewOverlay.Children.Clear();
            double w = (!double.IsNaN(_previewBorder.Width) && _previewBorder.Width > 0) ? _previewBorder.Width : _previewBorder.ActualWidth;
            double h = (!double.IsNaN(_previewBorder.Height) && _previewBorder.Height > 0) ? _previewBorder.Height : _previewBorder.ActualHeight;
            if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return;

            LcdScene scene = _engine.Scene(_activeScreen);

            if (scene.HasTransformableMedia() && scene.Fit == FitMode.Stretch)
            {
                double cx, cy, halfW, halfH, cos, sin;
                GetStretchGeometry(scene, w, h, out cx, out cy, out halfW, out halfH, out cos, out sin);

                Point tl = StretchPoint(cx, cy, -halfW, -halfH, cos, sin);
                Point tr = StretchPoint(cx, cy, halfW, -halfH, cos, sin);
                Point br = StretchPoint(cx, cy, halfW, halfH, cos, sin);
                Point bl = StretchPoint(cx, cy, -halfW, halfH, cos, sin);

                var poly = new Polygon
                {
                    Points = new PointCollection { tl, tr, br, bl },
                    Stroke = GreenBr,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 5, 3 },
                    Fill = Brushes.Transparent
                };
                _previewOverlay.Children.Add(poly);

                AddStretchHandle(tl.X, tl.Y, true);
                AddStretchHandle(tr.X, tr.Y, true);
                AddStretchHandle(br.X, br.Y, true);
                AddStretchHandle(bl.X, bl.Y, true);

                Point ml = StretchPoint(cx, cy, -halfW, 0, cos, sin);
                Point mr = StretchPoint(cx, cy, halfW, 0, cos, sin);
                Point mt = StretchPoint(cx, cy, 0, -halfH, cos, sin);
                Point mb = StretchPoint(cx, cy, 0, halfH, cos, sin);

                AddStretchHandle(ml.X, ml.Y, false);
                AddStretchHandle(mr.X, mr.Y, false);
                AddStretchHandle(mt.X, mt.Y, false);
                AddStretchHandle(mb.X, mb.Y, false);
            }

            for (int i = 0; i < scene.Overlays.Count; i++)
            {
                Overlay o = scene.Overlays[i];
                double x = Clamp01(o.X) * w;
                double y = Clamp01(o.Y) * h;
                bool selected = i == _sel;

                if (selected)
                {
                    double boxW = Math.Max(46, o.Size * h * (o.Kind == OverlayKind.CpuCores ? 2.0 : 3.3));
                    double boxH = Math.Max(30, o.Size * h * (o.ShowLabel ? 1.65 : 1.15));
                    var box = new Rectangle
                    {
                        Width = boxW,
                        Height = boxH,
                        RadiusX = 8,
                        RadiusY = 8,
                        Fill = Br("#226C8CFF"),
                        Stroke = AccentBr,
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 4, 3 }
                    };
                    Canvas.SetLeft(box, x - boxW / 2);
                    Canvas.SetTop(box, y - boxH / 2);
                    _previewOverlay.Children.Add(box);

                    AddHandle(x - boxW / 2, y - boxH / 2);
                    AddHandle(x + boxW / 2, y - boxH / 2);
                    AddHandle(x - boxW / 2, y + boxH / 2);
                    AddHandle(x + boxW / 2, y + boxH / 2);
                }

                var dot = new Ellipse
                {
                    Width = selected ? 12 : 9,
                    Height = selected ? 12 : 9,
                    Fill = WpfColor(o.Color),
                    Stroke = selected ? AccentBr : Br("#0A0D15"),
                    StrokeThickness = selected ? 2 : 1,
                    Opacity = selected ? 1.0 : 0.75
                };
                Canvas.SetLeft(dot, x - dot.Width / 2);
                Canvas.SetTop(dot, y - dot.Height / 2);
                _previewOverlay.Children.Add(dot);
            }
        }

        private void AddStretchHandle(double x, double y, bool corner)
        {
            double size = corner ? 10 : 8;
            var handle = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(corner ? 3 : 4),
                Background = corner ? GreenBr : Br("#ECF0F7"),
                BorderBrush = Br("#0A0D15"),
                BorderThickness = new Thickness(1.5)
            };
            Canvas.SetLeft(handle, x - size / 2);
            Canvas.SetTop(handle, y - size / 2);
            _previewOverlay.Children.Add(handle);
        }

        private void AddHandle(double x, double y)
        {
            var handle = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(3),
                Background = AccentBr,
                BorderBrush = Br("#0A0D15"),
                BorderThickness = new Thickness(1)
            };
            Canvas.SetLeft(handle, x - 4);
            Canvas.SetTop(handle, y - 4);
            _previewOverlay.Children.Add(handle);
        }

        private void Frac(MouseEventArgs e, out double fx, out double fy)
        {
            Point p = e.GetPosition(_previewBorder);
            double aw = _previewBorder.ActualWidth, ah = _previewBorder.ActualHeight;
            fx = aw > 0 ? p.X / aw : 0;
            fy = ah > 0 ? p.Y / ah : 0;
        }

        private static int HitTest(LcdScene scene, double fx, double fy)
        {
            int best = -1; double bestDist = 0.13;
            for (int i = 0; i < scene.Overlays.Count; i++)
            {
                Overlay o = scene.Overlays[i];
                double d = Math.Max(Math.Abs(o.X - fx), Math.Abs(o.Y - fy));
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        // ---- timers / status --------------------------------------------------------

        private void OnEngineStatus(bool ok, string message)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                SetStatus(ok, message);
                if (!ok) { _startButton.IsEnabled = !_engine.IsStreaming; _stopButton.IsEnabled = _engine.IsStreaming; }
            }));
        }

        private void SetStatus(bool ok, string message)
        {
            _statusText.Text = message;
            _statusText.ToolTip = message;
            _statusDot.Fill = ok ? GreenBr : RedBr;
        }

        private void OnRender(object sender, EventArgs e)
        {
            byte[] png = _engine.PreviewPng();
            if (png == null || ReferenceEquals(png, _lastPng)) return;
            _lastPng = png;
            try
            {
                using (var ms = new MemoryStream(png))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze();
                    _preview.Source = image;
                }
            }
            catch { }
        }

        private void ApplyPreviewAspect()
        {
            if (_previewBorder == null) return;
            int nw, nh;
            _engine.CanvasSize(_activeScreen, out nw, out nh);
            double availW = (_previewFrame != null && _previewFrame.ActualWidth > 80)
                ? _previewFrame.ActualWidth - 44.0
                : 460.0;
            double maxW = Math.Max(160.0, Math.Min(560.0, availW));
            double maxH = nh > nw ? 400.0 : (nw == nh ? 360.0 : 320.0);
            double scale = Math.Min(1.2, Math.Min(maxW / nw, maxH / nh));
            double pw = Math.Max(60, Math.Round(nw * scale));
            double ph = Math.Max(60, Math.Round(nh * scale));
            if (double.IsNaN(_previewBorder.Width) || Math.Abs(_previewBorder.Width - pw) >= 1.0)
                _previewBorder.Width = pw;
            if (double.IsNaN(_previewBorder.Height) || Math.Abs(_previewBorder.Height - ph) >= 1.0)
                _previewBorder.Height = ph;
            if (_previewOverlay != null)
            {
                if (double.IsNaN(_previewOverlay.Width) || Math.Abs(_previewOverlay.Width - pw) >= 1.0)
                    _previewOverlay.Width = pw;
                if (double.IsNaN(_previewOverlay.Height) || Math.Abs(_previewOverlay.Height - ph) >= 1.0)
                    _previewOverlay.Height = ph;
            }
            if (_previewCaption != null)
                _previewCaption.Text = ScreenDisplayName(_activeScreen) + " · " + nw + " × " + nh;
            RefreshPreviewOverlay();
        }

        // Device status uses a slow WMI query (Win32_PnPEntity) plus process lookups.
        // Run it on a background thread so dragging/resizing the window never blocks the UI.
        private void RefreshStatus()
        {
            if (_refreshing) return;
            _refreshing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DeviceSnapshot snap = null;
                bool myth = false, trcc = false;
                try
                {
                    snap = DeviceService.Read();
                    myth = VendorService.IsMythCoolRunning();
                    trcc = VendorService.IsTrccRunning();
                    if (snap != null && snap.Valkyrie.IsHealthy && !myth && !_valkProbedThisSession && ValkyrieDirectService.IsAutoMode)
                    {
                        ValkyrieDirectService.TryProbePanel();
                        _valkProbedThisSession = true;
                    }
                    else if (snap != null && !snap.Valkyrie.Present)
                    {
                        _valkProbedThisSession = false;
                    }
                }
                catch { }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try { if (snap != null) ApplyStatus(snap, myth, trcc); }
                    finally { _refreshing = false; }
                }));
            });
        }

        private void ApplyStatus(DeviceSnapshot snap, bool myth, bool trcc)
        {
            _snapshot = snap;
            SetDevice(snap.Valkyrie, _valkDot, _valkState);
            SetDevice(snap.BoWei, _boweiDot, _boweiState);
            SetDevice(snap.Thermalright, _trDot, _trState);
            int healthy = (snap.Valkyrie.IsHealthy ? 1 : 0) + (snap.BoWei.IsHealthy ? 1 : 0) + (snap.Thermalright.IsHealthy ? 1 : 0);
            string summary = healthy == 0 ? Loc.T("sum.none") : (healthy == 3 ? Loc.T("sum.both") : Loc.T("sum.count", healthy));
            if (myth || trcc)
                summary = Loc.T("sum.busy", myth && trcc ? "Myth.Cool + TRCC" : myth ? "Myth.Cool" : "TRCC");
            _summary.Text = summary + "   ·   " + Loc.T("sum.updated") + " " + snap.CheckedAt.ToString("HH:mm:ss");
        }

        private void SetDevice(DeviceInfo info, Ellipse dot, TextBlock state)
        {
            dot.Fill = info.IsHealthy ? GreenBr : RedBr;
            state.Foreground = info.IsHealthy ? GreenBr : MutedBr;
            state.Text = info.IsHealthy ? Loc.T("dev.ready") : info.Present ? Loc.T("dev.error") + " " + info.ErrorCode : Loc.T("dev.absent");
        }

        // ---- widgets ----------------------------------------------------------------

        private string LayerLabel(Overlay o)
        {
            if (o.Kind == OverlayKind.Text) return Loc.T("layer.textPrefix") + (o.Text ?? "");
            return MetricName(KindIndex(o.Kind));
        }

        private static int KindIndex(OverlayKind kind)
        {
            for (int i = 0; i < MetricKinds.Length; i++) if (MetricKinds[i] == kind) return i;
            return 0;
        }

        private UIElement Slider(string label, double min, double max, double value, Action<double> onChange, Func<double, string> fmt)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = label, Foreground = MutedBr, FontSize = 12 });
            double v0 = Math.Min(max, Math.Max(min, value));
            var valueText = new TextBlock { Text = fmt(v0), Foreground = TextBr, FontSize = 12, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(valueText, 1);
            head.Children.Add(valueText);
            stack.Children.Add(head);
            var slider = new Slider { Minimum = min, Maximum = max, Value = v0, Margin = new Thickness(0, 3, 0, 0) };
            slider.ValueChanged += delegate { valueText.Text = fmt(slider.Value); onChange(slider.Value); MarkProfileDirty(); RefreshPreviewOverlay(); };
            stack.Children.Add(slider);
            return stack;
        }

        private Button SegButton(string text, ScreenId screen)
        {
            var b = new Button { Content = text, MinWidth = 108 };
            b.Style = (Style)FindResource("SegBtn");
            b.Click += delegate { SwitchTab(screen); };
            return b;
        }

        private void StyleTab(Button b, bool active)
        {
            b.Background = active ? AccentBr : Brushes.Transparent;
            b.Foreground = active ? AccentTextBr : MutedBr;
        }

        private Button Btn(string text, RoutedEventHandler handler, string styleKey)
        {
            var b = new Button { Content = text };
            b.Style = (Style)FindResource(styleKey);
            b.Click += handler;
            return b;
        }

        private Button MiniLayerButton(string text, string tooltip, string styleKey, bool enabled, RoutedEventHandler handler)
        {
            var b = new Button { Content = text, ToolTip = tooltip, MinWidth = 34, Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled };
            b.Style = (Style)FindResource(styleKey);
            b.Click += handler;
            return b;
        }

        private UIElement ColorPalette(System.Drawing.Color selected, Action<System.Drawing.Color> apply)
        {
            var panel = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            for (int i = 0; i < SwatchHex.Length; i++)
            {
                string hex = SwatchHex[i];
                panel.Children.Add(ColorSwatch(Sd(hex), SameColor(selected, hex), SwatchName(i), apply));
            }
            foreach (System.Drawing.Color recent in _recentColors)
                panel.Children.Add(ColorSwatch(recent, ColorsEqual(selected, recent), Loc.T("color.recent"), apply));

            var custom = new Button { Content = "+", ToolTip = Loc.T("color.custom"), Width = 28, Height = 28, Margin = new Thickness(0, 0, 9, 8), Cursor = Cursors.Hand };
            custom.Style = (Style)FindResource("BtnChip");
            custom.Padding = new Thickness(0);
            custom.Click += delegate
            {
                System.Drawing.Color picked;
                if (PickColor(selected, out picked)) apply(picked);
            };
            panel.Children.Add(custom);
            return panel;
        }

        private Button ColorSwatch(System.Drawing.Color color, bool selected, string tooltip, Action<System.Drawing.Color> apply)
        {
            var sw = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 9, 8),
                Background = WpfColor(color),
                BorderBrush = selected ? AccentBr : SoftBr,
                BorderThickness = new Thickness(selected ? 2 : 1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            sw.Template = SwatchTemplate();
            sw.Click += delegate { RememberColor(color, false); apply(color); };
            return sw;
        }

        private Button Chip(string text, RoutedEventHandler handler)
        {
            return Chip(text, handler, false);
        }

        private Button Chip(string text, RoutedEventHandler handler, bool active)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
            b.Style = (Style)FindResource("BtnChip");
            if (active)
            {
                b.Background = AccentBr;
                b.Foreground = AccentTextBr;
                b.BorderBrush = AccentBr;
                b.FontWeight = FontWeights.SemiBold;
            }
            b.Click += handler;
            return b;
        }

        private ControlTemplate SwatchTemplate()
        {
            var ct = new ControlTemplate(typeof(Button));
            var border = new System.Windows.FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            ct.VisualTree = border;
            return ct;
        }

        private Border Card()
        {
            return new Border { Background = Surface, CornerRadius = new CornerRadius(16), Padding = new Thickness(22), BorderBrush = SoftBr, BorderThickness = new Thickness(1) };
        }

        private TextBlock SectionLabel(string text) { return SectionLabel(text, 0); }
        private TextBlock SectionLabel(string text, double topMargin)
        {
            return new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = MutedBr, Margin = new Thickness(0, topMargin, 0, 0) };
        }

        private bool SameColor(System.Drawing.Color c, string hex) { return ColorsEqual(c, Sd(hex)); }
        private static bool ColorsEqual(System.Drawing.Color a, System.Drawing.Color b) { return a.R == b.R && a.G == b.G && a.B == b.B; }

        private Brush WpfColor(System.Drawing.Color c)
        {
            var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        private ImageSource BuildWindowIcon()
        {
            const int s = 64;
            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawRoundedRectangle(AccentBr, null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);
                var screen = new Rect(s * 0.17, s * 0.19, s * 0.66, s * 0.62);
                dc.DrawRoundedRectangle(Br("#0E131F"), null, screen, s * 0.10, s * 0.10);
                double bx = screen.X + screen.Width * 0.16, bw = screen.Width * 0.68;
                double bh = screen.Height * 0.15, gap = screen.Height * 0.20;
                double by = screen.Y + screen.Height * 0.26;
                dc.DrawRoundedRectangle(GreenBr, null, new Rect(bx, by, bw, bh), bh / 2, bh / 2);
                dc.DrawRoundedRectangle(Br("#F5C850"), null, new Rect(bx, by + bh + gap, bw * 0.62, bh), bh / 2, bh / 2);
            }
            var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        private static SolidColorBrush Br(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static System.Drawing.Color Sd(string hex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(hex);
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; }
            try { ProfileService.SaveLast(_engine.Capture(_tgtValk, _tgtBoWei, _tgtThermal, _activeScreen)); } catch { }
            SaveCurrentSettings();
            ValkyrieDirectService.PanelChanged -= OnHardwarePanelChanged;
            BoWeiDirectService.PanelChanged -= OnHardwarePanelChanged;
            Loc.Changed -= OnLanguageChanged;
            CompositionTarget.Rendering -= OnRender;
            _engine.Shutdown();
            SensorService.Close();
            UsbDevice.Exit();
            base.OnClosed(e);
        }

        private void SaveCurrentSettings()
        {
            try
            {
                ProfileService.SaveSettings(new AppSettings
                {
                    Lang = Loc.Lang,
                    LastProfile = _currentProfile,
                    LastSeenVersion = _lastSeenVersion,
                    ValkyriePanel = _valkPanelMode,
                    LastDetectedValkyriePanel = _lastDetectedValkPanel,
                    ValkyrieRotation = _valkRotation,
                    BoWeiRotation = _boweiRotation,
                    ThermalrightRotation = _thermalRotation,
                    BoWeiBrightness = _boweiBrightness,
                    WallpaperAssetsDir = _wallpaperAssetsDir,
                    WallpaperRendererPath = _wallpaperRendererPath,
                    RecentColors = RecentColorArgb()
                });
            }
            catch { }
        }
    }
}
