using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LcdFusion
{
    internal enum BackgroundKind { None, Color, Image, Gif, Wallpaper }
    internal enum OverlayKind { Text, CpuTemp, GpuTemp, CpuLoad, GpuLoad, Clock, Date, CpuClock, GpuClock, CpuPower, GpuPower, GpuVram, RamLoad, GpuFan, CpuCores }
    internal enum FitMode { Fit, Fill, Stretch }

    // A movable text/sensor layer drawn on top of the background.
    internal sealed class Overlay
    {
        public OverlayKind Kind = OverlayKind.CpuTemp;
        public string Text = "Testo";
        public double X = 0.5;     // center, fraction of width
        public double Y = 0.5;     // center, fraction of height
        public double Size = 0.20; // font height, fraction of canvas height
        public double Rotation;    // degrees
        public Color Color = Color.White;
        public bool ShowLabel = true;

        public Overlay Clone()
        {
            return new Overlay { Kind = Kind, Text = Text, X = X, Y = Y, Size = Size, Rotation = Rotation, Color = Color, ShowLabel = ShowLabel };
        }
    }

    // Everything shown on one LCD: a background plus movable overlays.
    internal sealed class LcdScene
    {
        public BackgroundKind Background = BackgroundKind.None;
        public string MediaName = "";
        public string MediaPath = "";
        public string WallpaperType = "";
        public readonly Dictionary<string, string> WallpaperProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] MediaBytes;
        public IBackgroundSource Source;
        public Color BgColor = Color.FromArgb(11, 16, 32);
        public double Rotation;    // degrees (free)
        public bool FlipH;
        public double Scale = 1.0;
        public double ScaleX = 1.0; // independent horizontal stretch factor (used in FitMode.Stretch)
        public double ScaleY = 1.0; // independent vertical stretch factor (used in FitMode.Stretch)
        public double PanX;        // fraction of width
        public double PanY;        // fraction of height
        public FitMode Fit = FitMode.Fit;
        public readonly List<Overlay> Overlays = new List<Overlay>();

        public bool HasTransformableMedia()
        {
            return Background == BackgroundKind.Image || Background == BackgroundKind.Gif || Background == BackgroundKind.Wallpaper;
        }

        public bool IsWebWallpaper()
        {
            return Background == BackgroundKind.Wallpaper &&
                   string.Equals(WallpaperType, "web", StringComparison.OrdinalIgnoreCase);
        }

        public bool NeedsSensorData()
        {
            if (IsWebWallpaper()) return true;
            foreach (Overlay o in Overlays)
                if (IsSensorOverlay(o.Kind)) return true;
            return false;
        }

        public int PreviewIntervalMs()
        {
            return RefreshIntervalMs(33);
        }

        public int StreamIntervalMs(ScreenId screen)
        {
            int dynamicMs = screen == ScreenId.BoWei ? 33 : (screen == ScreenId.Valkyrie ? 40 : 150);
            return RefreshIntervalMs(dynamicMs);
        }

        public int TargetRendererFps(ScreenId screen)
        {
            switch (screen)
            {
                case ScreenId.BoWei: return 30;
                case ScreenId.Valkyrie: return 25;
                case ScreenId.Thermalright: return 10;
                default: return 25;
            }
        }

        private int RefreshIntervalMs(int dynamicIntervalMs)
        {
            if (Background == BackgroundKind.Gif || Background == BackgroundKind.Wallpaper) return dynamicIntervalMs;
            bool hasDate = false;
            foreach (Overlay o in Overlays)
            {
                if (o.Kind == OverlayKind.Clock || IsSensorOverlay(o.Kind)) return 1000;
                if (o.Kind == OverlayKind.Date) hasDate = true;
            }
            return hasDate ? 60000 : int.MaxValue;
        }

        private static bool IsSensorOverlay(OverlayKind kind)
        {
            switch (kind)
            {
                case OverlayKind.CpuTemp:
                case OverlayKind.GpuTemp:
                case OverlayKind.CpuLoad:
                case OverlayKind.GpuLoad:
                case OverlayKind.CpuClock:
                case OverlayKind.GpuClock:
                case OverlayKind.CpuPower:
                case OverlayKind.GpuPower:
                case OverlayKind.GpuVram:
                case OverlayKind.RamLoad:
                case OverlayKind.GpuFan:
                case OverlayKind.CpuCores:
                    return true;
                default:
                    return false;
            }
        }

        public IBackgroundSource DetachSource()
        {
            IBackgroundSource old = Source;
            Source = null;
            return old;
        }

        public void DisposeSources()
        {
            IBackgroundSource old = DetachSource();
            if (old != null) { try { old.Dispose(); } catch { } }
        }
    }

    // Renders each LCD's scene independently and streams it, with a live preview
    // of whichever LCD is being edited.
    internal sealed class ContentEngine
    {
        private readonly object _lock = new object();
        private Thread _thread;
        private volatile bool _alive;
        private volatile bool _streaming;

        private readonly LcdScene _valk = new LcdScene();
        private readonly LcdScene _bowei = new LcdScene();
        private readonly LcdScene _thermal = new LcdScene();
        private readonly List<IBackgroundSource> _pendingDispose = new List<IBackgroundSource>();
        private string _wallpaperRendererPath = "";
        private string _wallpaperAssetsDir = "";
        private bool _targetValk = true;
        private bool _targetBoWei;
        private bool _targetThermal;
        private int _valkRotation = -1; // -1 = auto (90° for 360x960 landscape secondary screen)
        private int _boweiRotation;
        private int _thermalRotation;
        private ScreenId _previewScreen = ScreenId.Valkyrie;
        private bool _previewEnabled = true;

        private SensorReading _sensors = new SensorReading();
        private byte[] _previewPng;
        private bool _dirtyValk = true, _dirtyBoWei = true, _dirtyThermal = true, _dirtyPreview = true;

        private string _valkError, _boweiError, _thermalError;
        private string _valkWpeError, _boweiWpeError, _thermalWpeError;
        private string _status = "Pronto";
        private bool _statusOk = true;
        public Action<bool, string> StatusChanged;

        public ContentEngine(int valkRotation = -1, int boweiRotation = 0, int thermalRotation = 0)
        {
            _valkRotation = NormalizeRotationSetting(valkRotation, true);
            _boweiRotation = NormalizeRotationSetting(boweiRotation, false);
            _thermalRotation = NormalizeRotationSetting(thermalRotation, false);

            // Default each screen to a simple live dashboard sized to its canvas aspect ratio.
            int vw, vh;
            CanvasSize(ScreenId.Valkyrie, out vw, out vh);
            if (vw > vh * 2)
            {
                _valk.Overlays.Add(new Overlay { Kind = OverlayKind.CpuTemp, X = 0.22, Y = 0.50, Size = 0.36 });
                _valk.Overlays.Add(new Overlay { Kind = OverlayKind.GpuTemp, X = 0.52, Y = 0.50, Size = 0.36 });
                _valk.Overlays.Add(new Overlay { Kind = OverlayKind.Clock, X = 0.82, Y = 0.50, Size = 0.30, ShowLabel = false });
            }
            else if (vh > vw)
            {
                _valk.Overlays.Add(new Overlay { Kind = OverlayKind.CpuTemp, X = 0.5, Y = 0.25, Size = 0.09 });
                _valk.Overlays.Add(new Overlay { Kind = OverlayKind.GpuTemp, X = 0.5, Y = 0.50, Size = 0.09 });
                _valk.Overlays.Add(new Overlay { Kind = OverlayKind.Clock, X = 0.5, Y = 0.75, Size = 0.07, ShowLabel = false });
            }
            else
            {
                _valk.Overlays.Add(new Overlay { Kind = OverlayKind.CpuTemp, X = 0.5, Y = 0.30, Size = 0.26 });
                _valk.Overlays.Add(new Overlay { Kind = OverlayKind.GpuTemp, X = 0.5, Y = 0.70, Size = 0.26 });
            }

            _bowei.Overlays.Add(new Overlay { Kind = OverlayKind.CpuTemp, X = 0.5, Y = 0.32, Size = 0.18 });
            _bowei.Overlays.Add(new Overlay { Kind = OverlayKind.GpuTemp, X = 0.5, Y = 0.68, Size = 0.18 });

            _thermal.Overlays.Add(new Overlay { Kind = OverlayKind.CpuTemp, X = 0.20, Y = 0.5, Size = 0.5 });
            _thermal.Overlays.Add(new Overlay { Kind = OverlayKind.GpuTemp, X = 0.52, Y = 0.5, Size = 0.5 });
            _thermal.Overlays.Add(new Overlay { Kind = OverlayKind.Clock, X = 0.83, Y = 0.5, Size = 0.42, ShowLabel = false });
        }

        private static int NormalizeRotationSetting(int degrees, bool allowAuto)
        {
            if (allowAuto && degrees < 0) return -1;
            int r = ((degrees % 360) + 360) % 360;
            if (r == 90 || r == 180 || r == 270) return r;
            return 0;
        }

        public int EffectiveRotation(ScreenId screen)
        {
            int raw;
            lock (_lock)
            {
                switch (screen)
                {
                    case ScreenId.BoWei: raw = _boweiRotation; break;
                    case ScreenId.Thermalright: raw = _thermalRotation; break;
                    default: raw = _valkRotation; break;
                }
            }
            if (screen == ScreenId.Valkyrie && raw < 0)
            {
                // The 360x960 secondary display is physically mounted landscape (960x360, wire rotated 90° CCW).
                // Rotating the 960x360 canvas 90° CW before sending makes the physical display upright.
                return (ValkyrieDirectService.Width == 360 && ValkyrieDirectService.Height == 960) ? 90 : 0;
            }
            return raw < 0 ? 0 : raw;
        }

        public void SetScreenRotation(ScreenId screen, int degrees)
        {
            int norm = NormalizeRotationSetting(degrees, screen == ScreenId.Valkyrie);
            lock (_lock)
            {
                switch (screen)
                {
                    case ScreenId.BoWei: _boweiRotation = norm; break;
                    case ScreenId.Thermalright: _thermalRotation = norm; break;
                    default: _valkRotation = norm; break;
                }
                MarkDirty(screen);
            }
        }

        public void CanvasSize(ScreenId screen, out int width, out int height)
        {
            int wireW, wireH;
            NativeSize(screen, out wireW, out wireH);
            int rot = EffectiveRotation(screen);
            if (rot == 90 || rot == 270)
            {
                width = wireH;
                height = wireW;
            }
            else
            {
                width = wireW;
                height = wireH;
            }
        }

        private static void ApplyHardwareRotation(Bitmap bmp, int rotation)
        {
            switch (rotation)
            {
                case 90: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                case 180: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                case 270: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            }
        }

        // ---- lifecycle --------------------------------------------------------------

        public void Start()
        {
            if (_thread != null && _thread.IsAlive) return;
            _alive = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "LCD Fusion Engine" };
            _thread.Start();
        }

        public void Shutdown()
        {
            _alive = false; _streaming = false;
            try { ValkyrieDirectService.Stop(); } catch { }
            try { BoWeiDirectService.Stop(); } catch { }
            try { ThermalrightDirectService.Stop(); } catch { }
            Thread thread = _thread;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(2000);
            lock (_lock)
            {
                _valk.DisposeSources();
                _bowei.DisposeSources();
                _thermal.DisposeSources();
                DrainPendingDisposeLocked();
            }
        }

        public void ConfigureWallpaper(string rendererPath, string assetsDir)
        {
            lock (_lock)
            {
                _wallpaperRendererPath = rendererPath ?? "";
                _wallpaperAssetsDir = assetsDir ?? "";
            }
        }

        public string ResolvedWallpaperRendererPath()
        {
            string configured;
            lock (_lock) { configured = _wallpaperRendererPath; }
            return WallpaperCatalog.ResolveRendererExecutable(configured);
        }

        public string ResolvedWallpaperAssetsDir()
        {
            string configured;
            lock (_lock) { configured = _wallpaperAssetsDir; }
            return WallpaperCatalog.DetectWallpaperEngineAssetsDir(configured);
        }

        private void ReplaceSourceLocked(LcdScene scene, IBackgroundSource next)
        {
            IBackgroundSource old = scene.DetachSource();
            if (old != null) _pendingDispose.Add(old);
            scene.Source = next;
        }

        private void DrainPendingDisposeLocked()
        {
            if (_pendingDispose.Count == 0) return;
            for (int i = 0; i < _pendingDispose.Count; i++)
            {
                try { _pendingDispose[i].Dispose(); } catch { }
            }
            _pendingDispose.Clear();
        }

        // ---- editing API ------------------------------------------------------------

        public LcdScene Scene(ScreenId screen)
        {
            switch (screen)
            {
                case ScreenId.BoWei: return _bowei;
                case ScreenId.Thermalright: return _thermal;
                default: return _valk;
            }
        }

        public static void NativeSize(ScreenId screen, out int width, out int height)
        {
            switch (screen)
            {
                case ScreenId.BoWei:
                    width = BoWeiDirectService.Width;
                    height = BoWeiDirectService.Height;
                    break;
                case ScreenId.Thermalright:
                    width = ThermalrightDirectService.CanvasWidth;
                    height = ThermalrightDirectService.CanvasHeight;
                    break;
                default:
                    width = ValkyrieDirectService.Width;
                    height = ValkyrieDirectService.Height;
                    break;
            }
        }

        public void InvalidateScreen(ScreenId screen)
        {
            lock (_lock) { MarkDirty(screen); }
        }

        public void Edit(ScreenId screen, Action<LcdScene> mutation)
        {
            lock (_lock)
            {
                mutation(Scene(screen));
                MarkDirty(screen);
            }
        }

        public void LoadImage(ScreenId screen, string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                Image loaded;
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image tmp = Image.FromStream(ms)) loaded = new Bitmap(tmp);
                lock (_lock)
                {
                    LcdScene scene = Scene(screen);
                    ReplaceSourceLocked(scene, new StaticImageSource(loaded));
                    scene.MediaBytes = bytes;
                    scene.MediaName = Path.GetFileName(path);
                    scene.MediaPath = path;
                    scene.WallpaperType = "";
                    scene.WallpaperProps.Clear();
                    scene.Background = BackgroundKind.Image;
                    scene.Scale = 1.0; scene.ScaleX = 1.0; scene.ScaleY = 1.0; scene.PanX = 0; scene.PanY = 0; scene.Rotation = 0; scene.FlipH = false;
                    MarkDirty(screen);
                }
            }
            catch (Exception ex) { Report(false, Loc.T("err.image") + ex.Message); }
        }

        public void LoadGif(ScreenId screen, string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                GifState loaded = GifState.LoadBytes(bytes);
                lock (_lock)
                {
                    LcdScene scene = Scene(screen);
                    ReplaceSourceLocked(scene, loaded);
                    scene.MediaBytes = bytes;
                    scene.MediaName = Path.GetFileName(path);
                    scene.MediaPath = path;
                    scene.WallpaperType = "";
                    scene.WallpaperProps.Clear();
                    scene.Background = BackgroundKind.Gif;
                    scene.Scale = 1.0; scene.ScaleX = 1.0; scene.ScaleY = 1.0; scene.PanX = 0; scene.PanY = 0; scene.Rotation = 0; scene.FlipH = false;
                    MarkDirty(screen);
                }
            }
            catch (Exception ex) { Report(false, Loc.T("err.gif") + ex.Message); }
        }

        public bool LoadWallpaper(ScreenId screen, string wallpaperDir, Dictionary<string, string> initialProps = null)
        {
            try
            {
                WallpaperEntry entry = WallpaperCatalog.TryLoadWallpaperDir(wallpaperDir);
                if (entry == null)
                    throw new DirectoryNotFoundException(wallpaperDir ?? "");

                string rendererExe = ResolvedWallpaperRendererPath();
                if (string.IsNullOrEmpty(rendererExe))
                    throw new FileNotFoundException(Loc.T("err.wpeNoRenderer"));

                string assetsDir = ResolvedWallpaperAssetsDir();
                int cw, ch;
                CanvasSize(screen, out cw, out ch);
                int fps;
                lock (_lock) { fps = Scene(screen).TargetRendererFps(screen); }

                Dictionary<string, object> typedProps = WallpaperCatalog.ConvertPropsToTypedMap(initialProps, entry.Properties);
                WallpaperFrameSource source = new WallpaperFrameSource(screen, rendererExe, entry.Directory, assetsDir, cw, ch, fps, typedProps);
                SensorReading snapSensors = null;
                lock (_lock)
                {
                    LcdScene scene = Scene(screen);
                    ReplaceSourceLocked(scene, source);
                    scene.MediaBytes = null;
                    scene.MediaName = entry.DisplayTitle;
                    scene.MediaPath = entry.Directory;
                    scene.WallpaperType = entry.Type ?? "scene";
                    scene.WallpaperProps.Clear();
                    if (initialProps != null)
                    {
                        foreach (KeyValuePair<string, string> kv in initialProps)
                            if (!string.IsNullOrEmpty(kv.Key)) scene.WallpaperProps[kv.Key] = kv.Value ?? "";
                    }
                    scene.Background = BackgroundKind.Wallpaper;
                    scene.Scale = 1.0; scene.ScaleX = 1.0; scene.ScaleY = 1.0; scene.PanX = 0; scene.PanY = 0; scene.Rotation = 0; scene.FlipH = false;
                    scene.Fit = FitMode.Fill;
                    if (scene.IsWebWallpaper() && _sensors != null && _sensors.Available)
                        snapSensors = _sensors;
                    MarkDirty(screen);
                }
                if (snapSensors != null)
                    source.SetSensors(BuildSensorPayload(snapSensors));
                return true;
            }
            catch (Exception ex)
            {
                Report(false, Loc.T("err.wallpaper") + ex.Message);
                return false;
            }
        }

        public void SetWallpaperProperties(ScreenId screen, Dictionary<string, string> overrides, List<WallpaperPropertyDef> defs = null)
        {
            if (overrides == null) return;
            WallpaperFrameSource wpe;
            string mediaPath;
            lock (_lock)
            {
                LcdScene scene = Scene(screen);
                if (scene.Background != BackgroundKind.Wallpaper) return;
                foreach (KeyValuePair<string, string> kv in overrides)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    scene.WallpaperProps[kv.Key] = kv.Value ?? "";
                }
                wpe = scene.Source as WallpaperFrameSource;
                mediaPath = scene.MediaPath;
                MarkDirty(screen);
            }
            if (wpe == null) return;
            if (defs == null && !string.IsNullOrEmpty(mediaPath))
            {
                WallpaperEntry entry = WallpaperCatalog.TryLoadWallpaperDir(mediaPath);
                if (entry != null) defs = entry.Properties;
            }
            wpe.SetProperties(WallpaperCatalog.ConvertPropsToTypedMap(overrides, defs));
        }

        public void ResetWallpaperProperties(ScreenId screen, List<WallpaperPropertyDef> defs)
        {
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (defs != null)
            {
                foreach (WallpaperPropertyDef d in defs)
                    if (!string.IsNullOrEmpty(d.Key)) defaults[d.Key] = d.DefaultValue ?? "";
            }
            WallpaperFrameSource wpe;
            lock (_lock)
            {
                LcdScene scene = Scene(screen);
                scene.WallpaperProps.Clear();
                wpe = scene.Source as WallpaperFrameSource;
                MarkDirty(screen);
            }
            if (wpe != null && defaults.Count > 0)
                wpe.SetProperties(WallpaperCatalog.ConvertPropsToTypedMap(defaults, defs));
        }

        public void SetBackgroundKind(ScreenId screen, BackgroundKind kind)
        {
            lock (_lock)
            {
                LcdScene scene = Scene(screen);
                if (kind == BackgroundKind.None || kind == BackgroundKind.Color)
                {
                    ReplaceSourceLocked(scene, null);
                    scene.MediaBytes = null; scene.MediaName = ""; scene.MediaPath = ""; scene.WallpaperType = "";
                    scene.WallpaperProps.Clear();
                }
                scene.Background = kind;
                MarkDirty(screen);
            }
        }

        public void SetTargets(bool valkyrie, bool bowei, bool thermalright)
        {
            bool stopValk, stopBoWei, stopThermal;
            lock (_lock)
            {
                stopValk = _targetValk && !valkyrie;
                stopBoWei = _targetBoWei && !bowei;
                stopThermal = _targetThermal && !thermalright;
                _targetValk = valkyrie;
                _targetBoWei = bowei;
                _targetThermal = thermalright;
                if (!valkyrie) _valkError = null; else _dirtyValk = true;
                if (!bowei) _boweiError = null; else _dirtyBoWei = true;
                if (!thermalright) _thermalError = null; else _dirtyThermal = true;
            }
            if (stopValk) try { ValkyrieDirectService.Stop(); } catch { }
            if (stopBoWei) try { BoWeiDirectService.Stop(); } catch { }
            if (stopThermal) try { ThermalrightDirectService.Stop(); } catch { }
        }

        public void SetPreviewTarget(ScreenId screen)
        {
            lock (_lock) { _previewScreen = screen; _dirtyPreview = true; }
        }

        public void SetPreviewEnabled(bool enabled)
        {
            lock (_lock)
            {
                _previewEnabled = enabled;
                if (enabled) _dirtyPreview = true;
            }
        }

        public ScreenId PreviewScreen { get { lock (_lock) { return _previewScreen; } } }

        public void StartStreaming()
        {
            _streaming = true;
            lock (_lock)
            {
                _valkError = null; _boweiError = null; _thermalError = null;
                MarkDirty(ScreenId.Valkyrie);
                MarkDirty(ScreenId.BoWei);
                MarkDirty(ScreenId.Thermalright);
            }
            Start();
        }

        public void StopStreaming()
        {
            _streaming = false;
            lock (_lock) { _valkError = null; _boweiError = null; _thermalError = null; }
            try { ValkyrieDirectService.Stop(); } catch { }
            try { BoWeiDirectService.Stop(); } catch { }
            try { ThermalrightDirectService.Stop(); } catch { }
            Report(true, Loc.T("st.stopped"));
        }

        public bool IsStreaming { get { return _streaming; } }

        public byte[] PreviewPng() { lock (_lock) { return _previewPng; } }

        // ---- profiles ---------------------------------------------------------------

        public ProfileData Capture(bool targetValk, bool targetBoWei, bool targetThermal, ScreenId activeScreen)
        {
            lock (_lock)
            {
                return new ProfileData
                {
                    Valk = ToData(_valk),
                    BoWei = ToData(_bowei),
                    Thermal = ToData(_thermal),
                    TargetValk = targetValk,
                    TargetBoWei = targetBoWei,
                    TargetThermal = targetThermal,
                    ActiveValk = activeScreen == ScreenId.Valkyrie,
                    ActiveScreen = (int)activeScreen,
                    Streaming = _streaming
                };
            }
        }

        public void Apply(ProfileData p)
        {
            if (p == null) return;
            ApplyScene(ScreenId.Valkyrie, p.Valk);
            if (p.BoWei != null) ApplyScene(ScreenId.BoWei, p.BoWei);
            ApplyScene(ScreenId.Thermalright, p.Thermal);
        }

        public void CopyScene(ScreenId from, ScreenId to)
        {
            if (from == to) return;
            SceneData data;
            lock (_lock) { data = ToData(Scene(from)); }
            ApplyScene(to, data);
        }

        private static SceneData ToData(LcdScene s)
        {
            SceneData d = new SceneData
            {
                Background = (int)s.Background,
                MediaPath = s.MediaPath,
                MediaData = s.Background == BackgroundKind.Wallpaper ? null : s.MediaBytes,
                WallpaperType = s.WallpaperType ?? "",
                BgColorArgb = s.BgColor.ToArgb(),
                Rotation = s.Rotation,
                FlipH = s.FlipH,
                Scale = s.Scale,
                ScaleX = s.ScaleX,
                ScaleY = s.ScaleY,
                PanX = s.PanX,
                PanY = s.PanY,
                Fit = (int)s.Fit
            };
            foreach (KeyValuePair<string, string> kv in s.WallpaperProps)
                d.WallpaperProps.Add(new WallpaperPropEntry { Key = kv.Key, Value = kv.Value ?? "" });
            foreach (Overlay o in s.Overlays)
                d.Overlays.Add(new OverlayData
                {
                    Kind = (int)o.Kind, Text = o.Text, X = o.X, Y = o.Y, Size = o.Size,
                    Rotation = o.Rotation, ColorArgb = o.Color.ToArgb(), ShowLabel = o.ShowLabel
                });
            return d;
        }

        private void ApplyScene(ScreenId screen, SceneData d)
        {
            if (d == null) return;
            BackgroundKind bg = (BackgroundKind)d.Background;
            byte[] data = bg == BackgroundKind.Wallpaper ? null : d.MediaData;
            IBackgroundSource source = null;
            string mediaName = string.IsNullOrEmpty(d.MediaPath) ? "" : Path.GetFileName(d.MediaPath);
            string wallpaperType = d.WallpaperType ?? "";
            try
            {
                if (bg == BackgroundKind.Image && data != null && data.Length > 0)
                {
                    using (MemoryStream ms = new MemoryStream(data))
                    using (Image tmp = Image.FromStream(ms)) source = new StaticImageSource(new Bitmap(tmp));
                }
                else if (bg == BackgroundKind.Gif && data != null && data.Length > 0)
                {
                    source = GifState.LoadBytes(data);
                }
                else if (bg == BackgroundKind.Image && !string.IsNullOrEmpty(d.MediaPath) && File.Exists(d.MediaPath))
                {
                    data = File.ReadAllBytes(d.MediaPath);
                    using (MemoryStream ms = new MemoryStream(data))
                    using (Image tmp = Image.FromStream(ms)) source = new StaticImageSource(new Bitmap(tmp));
                }
                else if (bg == BackgroundKind.Gif && !string.IsNullOrEmpty(d.MediaPath) && File.Exists(d.MediaPath))
                {
                    data = File.ReadAllBytes(d.MediaPath);
                    source = GifState.LoadBytes(data);
                }
                else if (bg == BackgroundKind.Wallpaper && !string.IsNullOrEmpty(d.MediaPath) && Directory.Exists(d.MediaPath))
                {
                    WallpaperEntry entry = WallpaperCatalog.TryLoadWallpaperDir(d.MediaPath);
                    string rendererExe = ResolvedWallpaperRendererPath();
                    if (entry != null && !string.IsNullOrEmpty(rendererExe))
                    {
                        int cw, ch;
                        CanvasSize(screen, out cw, out ch);
                        int fps;
                        lock (_lock) { fps = Scene(screen).TargetRendererFps(screen); }
                        var propStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (d.WallpaperProps != null)
                        {
                            foreach (WallpaperPropEntry pe in d.WallpaperProps)
                                if (pe != null && !string.IsNullOrEmpty(pe.Key)) propStrings[pe.Key] = pe.Value ?? "";
                        }
                        Dictionary<string, object> typedProps = WallpaperCatalog.ConvertPropsToTypedMap(propStrings, entry.Properties);
                        source = new WallpaperFrameSource(screen, rendererExe, entry.Directory, ResolvedWallpaperAssetsDir(), cw, ch, fps, typedProps);
                        mediaName = entry.DisplayTitle;
                        wallpaperType = entry.Type ?? "scene";
                    }
                    else
                    {
                        bg = BackgroundKind.None;
                    }
                }
                else if (bg == BackgroundKind.Image || bg == BackgroundKind.Gif || bg == BackgroundKind.Wallpaper)
                {
                    bg = BackgroundKind.None;
                }
            }
            catch { bg = BackgroundKind.None; source = null; data = null; }

            SensorReading snapSensors = null;
            lock (_lock)
            {
                LcdScene s = Scene(screen);
                ReplaceSourceLocked(s, source);
                s.MediaBytes = data;
                s.Background = bg;
                s.MediaPath = d.MediaPath ?? "";
                s.MediaName = mediaName;
                s.WallpaperType = wallpaperType;
                s.WallpaperProps.Clear();
                if (bg == BackgroundKind.Wallpaper && d.WallpaperProps != null)
                {
                    foreach (WallpaperPropEntry pe in d.WallpaperProps)
                        if (pe != null && !string.IsNullOrEmpty(pe.Key)) s.WallpaperProps[pe.Key] = pe.Value ?? "";
                }
                s.BgColor = Color.FromArgb(d.BgColorArgb);
                s.Rotation = d.Rotation;
                s.FlipH = d.FlipH;
                s.Scale = d.Scale <= 0 ? 1.0 : d.Scale;
                s.ScaleX = d.ScaleX <= 0 ? 1.0 : d.ScaleX;
                s.ScaleY = d.ScaleY <= 0 ? 1.0 : d.ScaleY;
                s.PanX = d.PanX;
                s.PanY = d.PanY;
                s.Fit = Enum.IsDefined(typeof(FitMode), d.Fit) ? (FitMode)d.Fit : FitMode.Fit;
                s.Overlays.Clear();
                if (d.Overlays != null)
                    foreach (OverlayData od in d.Overlays)
                        s.Overlays.Add(new Overlay
                        {
                            Kind = (OverlayKind)od.Kind, Text = od.Text, X = od.X, Y = od.Y,
                            Size = od.Size, Rotation = od.Rotation, Color = Color.FromArgb(od.ColorArgb), ShowLabel = od.ShowLabel
                        });
                if (s.IsWebWallpaper() && _sensors != null && _sensors.Available)
                    snapSensors = _sensors;
                MarkDirty(screen);
            }
            WallpaperFrameSource wpeSource = source as WallpaperFrameSource;
            if (snapSensors != null && wpeSource != null)
                wpeSource.SetSensors(BuildSensorPayload(snapSensors));
        }

        private void MarkDirty(ScreenId screen)
        {
            switch (screen)
            {
                case ScreenId.BoWei: _dirtyBoWei = true; break;
                case ScreenId.Thermalright: _dirtyThermal = true; break;
                default: _dirtyValk = true; break;
            }
            _dirtyPreview = true;
        }

        // ---- engine loop ------------------------------------------------------------

        private void Loop()
        {
            long lastValk = 0, lastBoWei = 0, lastThermal = 0, lastPreview = 0, lastSensor = 0;
            long lastTick = Environment.TickCount;

            while (_alive)
            {
                long now = Environment.TickCount;
                int delta = (int)(now - lastTick);
                lastTick = now;

                bool needSensors;
                lock (_lock) { needSensors = _valk.NeedsSensorData() || _bowei.NeedsSensorData() || _thermal.NeedsSensorData(); }
                if (needSensors && (lastSensor == 0 || now - lastSensor >= 1000))
                {
                    SensorReading r = SensorService.Read();
                    Dictionary<string, object> webPayload = BuildSensorPayload(r);
                    WallpaperFrameSource vWpe = null, bWpe = null, tWpe = null;
                    lock (_lock)
                    {
                        _sensors = r;
                        if (_valk.NeedsSensorData()) _dirtyValk = true;
                        if (_bowei.NeedsSensorData()) _dirtyBoWei = true;
                        if (_thermal.NeedsSensorData()) _dirtyThermal = true;
                        if (Scene(_previewScreen).NeedsSensorData()) _dirtyPreview = true;
                        if (_valk.IsWebWallpaper()) vWpe = _valk.Source as WallpaperFrameSource;
                        if (_bowei.IsWebWallpaper()) bWpe = _bowei.Source as WallpaperFrameSource;
                        if (_thermal.IsWebWallpaper()) tWpe = _thermal.Source as WallpaperFrameSource;
                    }
                    if (vWpe != null) vWpe.SetSensors(webPayload);
                    if (bWpe != null) bWpe.SetSensors(webPayload);
                    if (tWpe != null) tWpe.SetSensors(webPayload);
                    lastSensor = now;
                }

                lock (_lock)
                {
                    DrainPendingDisposeLocked();
                    if (_valk.Source != null) _valk.Source.Advance(delta);
                    if (_bowei.Source != null) _bowei.Source.Advance(delta);
                    if (_thermal.Source != null) _thermal.Source.Advance(delta);
                }

                int valkInterval, boweiInterval, thermalInterval, previewInterval;
                ScreenId previewScreen;
                bool previewEnabled, streaming, tv, tb, tt;
                WallpaperFrameSource valkWpe, boweiWpe, thermalWpe;
                int valkFps, boweiFps, thermalFps;
                FitMode valkFit, boweiFit, thermalFit;
                lock (_lock)
                {
                    valkInterval = _valk.StreamIntervalMs(ScreenId.Valkyrie);
                    boweiInterval = _bowei.StreamIntervalMs(ScreenId.BoWei);
                    thermalInterval = _thermal.StreamIntervalMs(ScreenId.Thermalright);
                    previewScreen = _previewScreen; previewEnabled = _previewEnabled; streaming = _streaming;
                    tv = _targetValk; tb = _targetBoWei; tt = _targetThermal;
                    previewInterval = Scene(previewScreen).PreviewIntervalMs();
                    valkWpe = _valk.Source as WallpaperFrameSource;
                    boweiWpe = _bowei.Source as WallpaperFrameSource;
                    thermalWpe = _thermal.Source as WallpaperFrameSource;
                    valkFps = _valk.TargetRendererFps(ScreenId.Valkyrie);
                    boweiFps = _bowei.TargetRendererFps(ScreenId.BoWei);
                    thermalFps = _thermal.TargetRendererFps(ScreenId.Thermalright);
                    valkFit = _valk.Fit;
                    boweiFit = _bowei.Fit;
                    thermalFit = _thermal.Fit;
                }

                SyncWallpaperSource(ScreenId.Valkyrie, valkWpe, (streaming && tv) || (previewEnabled && previewScreen == ScreenId.Valkyrie), valkFps, valkFit);
                SyncWallpaperSource(ScreenId.BoWei, boweiWpe, (streaming && tb) || (previewEnabled && previewScreen == ScreenId.BoWei), boweiFps, boweiFit);
                SyncWallpaperSource(ScreenId.Thermalright, thermalWpe, (streaming && tt) || (previewEnabled && previewScreen == ScreenId.Thermalright), thermalFps, thermalFit);
                SyncWallpaperErrors(valkWpe, boweiWpe, thermalWpe, streaming, tv, tb, tt, previewEnabled, previewScreen);

                if (previewEnabled && (_dirtyPreview || ShouldRefresh(now, lastPreview, previewInterval)))
                {
                    RenderPreview(previewScreen);
                    lastPreview = now;
                    _dirtyPreview = false;
                }

                if (streaming)
                {
                    if (tv && (_dirtyValk || ShouldRefresh(now, lastValk, valkInterval)))
                    { PushValkyrie(); lastValk = now; _dirtyValk = false; }
                    if (tb && (_dirtyBoWei || ShouldRefresh(now, lastBoWei, boweiInterval)))
                    { PushBoWei(); lastBoWei = now; _dirtyBoWei = false; }
                    if (tt && (_dirtyThermal || ShouldRefresh(now, lastThermal, thermalInterval)))
                    { PushThermalright(); lastThermal = now; _dirtyThermal = false; }
                }

                Thread.Sleep(15);
            }
        }

        private static Dictionary<string, object> BuildSensorPayload(SensorReading r)
        {
            if (r == null) r = new SensorReading();
            var cpu = new Dictionary<string, object>
            {
                { "name", r.CpuName ?? "" },
                { "hasTemp", r.HasCpuTemp },
                { "tempC", Math.Round(r.CpuTempC, 1) },
                { "hasLoad", r.HasCpuLoad },
                { "load", Math.Round(r.CpuLoad, 1) },
                { "hasClock", r.HasCpuClock },
                { "clockMhz", Math.Round(r.CpuClockMhz, 0) },
                { "hasPower", r.HasCpuPower },
                { "powerW", Math.Round(r.CpuPowerW, 1) },
                { "coreLoads", r.CpuCoreLoads != null ? Array.ConvertAll(r.CpuCoreLoads, v => Math.Round(v, 1)) : new double[0] }
            };
            var gpu = new Dictionary<string, object>
            {
                { "name", r.GpuName ?? "" },
                { "hasTemp", r.HasGpuTemp },
                { "tempC", Math.Round(r.GpuTempC, 1) },
                { "hasHotSpot", r.HasGpuHotSpot },
                { "hotSpotC", Math.Round(r.GpuHotSpotC, 1) },
                { "hasLoad", r.HasGpuLoad },
                { "load", Math.Round(r.GpuLoad, 1) },
                { "hasClock", r.HasGpuClock },
                { "clockMhz", Math.Round(r.GpuClockMhz, 0) },
                { "hasMemClock", r.HasGpuMemClock },
                { "memClockMhz", Math.Round(r.GpuMemClockMhz, 0) },
                { "hasPower", r.HasGpuPower },
                { "powerW", Math.Round(r.GpuPowerW, 1) },
                { "hasVram", r.HasGpuVram },
                { "vramUsedMb", Math.Round(r.GpuVramUsedMb, 0) },
                { "vramTotalMb", Math.Round(r.GpuVramTotalMb, 0) },
                { "hasFan", r.HasGpuFan },
                { "fanRpm", Math.Round(r.GpuFanRpm, 0) },
                { "hasFanPct", r.HasGpuFanPct },
                { "fanPct", Math.Round(r.GpuFanPct, 0) }
            };
            var ram = new Dictionary<string, object>
            {
                { "hasLoad", r.HasRamLoad },
                { "load", Math.Round(r.RamLoad, 1) },
                { "usedGb", Math.Round(r.RamUsedGb, 2) },
                { "totalGb", Math.Round(r.RamTotalGb, 2) }
            };
            return new Dictionary<string, object>
            {
                { "available", r.Available },
                { "source", r.Source ?? "" },
                { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                { "cpu", cpu },
                { "gpu", gpu },
                { "ram", ram }
            };
        }

        private void SyncWallpaperSource(ScreenId screen, WallpaperFrameSource wpe, bool active, int targetFps, FitMode fit)
        {
            if (wpe == null) return;
            wpe.SetPaused(!active);
            if (active)
            {
                int cw, ch;
                CanvasSize(screen, out cw, out ch);
                wpe.EnsureSizeAndFps(cw, ch, targetFps, fit);
            }
        }

        private static bool ShouldRefresh(long now, long last, int intervalMs)
        {
            return intervalMs != int.MaxValue && now - last >= intervalMs;
        }

        private void PushValkyrie()
        {
            try
            {
                int wireW = ValkyrieDirectService.Width;
                int wireH = ValkyrieDirectService.Height;
                int rot = EffectiveRotation(ScreenId.Valkyrie);
                int canvasW = (rot == 90 || rot == 270) ? wireH : wireW;
                int canvasH = (rot == 90 || rot == 270) ? wireW : wireH;
                using (Bitmap bmp = RenderScene(ScreenId.Valkyrie, canvasW, canvasH))
                {
                    ApplyHardwareRotation(bmp, rot);
                    DirectSendResult result = ValkyrieDirectService.ShowBgra(BgraFromBitmap(bmp), wireW, wireH);
                    RecordScreenStatus(ScreenId.Valkyrie, result.Success ? null : "Valkyrie: " + result.Message);
                }
            }
            catch (Exception ex) { RecordScreenStatus(ScreenId.Valkyrie, "Valkyrie: " + ex.Message); }
        }

        private void PushBoWei()
        {
            try
            {
                int wireW = BoWeiDirectService.Width;
                int wireH = BoWeiDirectService.Height;
                int rot = EffectiveRotation(ScreenId.BoWei);
                int canvasW = (rot == 90 || rot == 270) ? wireH : wireW;
                int canvasH = (rot == 90 || rot == 270) ? wireW : wireH;
                using (Bitmap bmp = RenderScene(ScreenId.BoWei, canvasW, canvasH))
                {
                    ApplyHardwareRotation(bmp, rot);
                    DirectSendResult result = BoWeiDirectService.ShowBgra(BgraFromBitmap(bmp), wireW, wireH);
                    RecordScreenStatus(ScreenId.BoWei, result.Success ? null : "BoWei: " + result.Message);
                }
            }
            catch (Exception ex) { RecordScreenStatus(ScreenId.BoWei, "BoWei: " + ex.Message); }
        }

        private void PushThermalright()
        {
            try
            {
                int wireW = ThermalrightDirectService.CanvasWidth;
                int wireH = ThermalrightDirectService.CanvasHeight;
                int rot = EffectiveRotation(ScreenId.Thermalright);
                int canvasW = (rot == 90 || rot == 270) ? wireH : wireW;
                int canvasH = (rot == 90 || rot == 270) ? wireW : wireH;
                using (Bitmap bmp = RenderScene(ScreenId.Thermalright, canvasW, canvasH))
                {
                    ApplyHardwareRotation(bmp, rot);
                    DirectSendResult result = ThermalrightDirectService.ShowBitmap(bmp);
                    RecordScreenStatus(ScreenId.Thermalright, result.Success ? null : "Thermalright: " + result.Message);
                }
            }
            catch (Exception ex) { RecordScreenStatus(ScreenId.Thermalright, "Thermalright: " + ex.Message); }
        }

        private void RecordScreenStatus(ScreenId screen, string error)
        {
            lock (_lock)
            {
                switch (screen)
                {
                    case ScreenId.BoWei: _boweiError = error; break;
                    case ScreenId.Thermalright: _thermalError = error; break;
                    default: _valkError = error; break;
                }
            }
            PublishCombinedStatus();
        }

        private void SyncWallpaperErrors(
            WallpaperFrameSource valkWpe,
            WallpaperFrameSource boweiWpe,
            WallpaperFrameSource thermalWpe,
            bool streaming, bool tv, bool tb, bool tt,
            bool previewEnabled, ScreenId previewScreen)
        {
            bool valkActive = (streaming && tv) || (previewEnabled && previewScreen == ScreenId.Valkyrie);
            bool boweiActive = (streaming && tb) || (previewEnabled && previewScreen == ScreenId.BoWei);
            bool thermalActive = (streaming && tt) || (previewEnabled && previewScreen == ScreenId.Thermalright);

            string nextValk = (valkActive && valkWpe != null && !string.IsNullOrEmpty(valkWpe.StatusError)) ? "Valkyrie: " + valkWpe.StatusError : null;
            string nextBoWei = (boweiActive && boweiWpe != null && !string.IsNullOrEmpty(boweiWpe.StatusError)) ? "BoWei: " + boweiWpe.StatusError : null;
            string nextThermal = (thermalActive && thermalWpe != null && !string.IsNullOrEmpty(thermalWpe.StatusError)) ? "Thermalright: " + thermalWpe.StatusError : null;

            bool changed;
            lock (_lock)
            {
                changed = !string.Equals(_valkWpeError, nextValk, StringComparison.Ordinal)
                       || !string.Equals(_boweiWpeError, nextBoWei, StringComparison.Ordinal)
                       || !string.Equals(_thermalWpeError, nextThermal, StringComparison.Ordinal);
                if (changed)
                {
                    _valkWpeError = nextValk;
                    _boweiWpeError = nextBoWei;
                    _thermalWpeError = nextThermal;
                }
            }

            if (changed) PublishCombinedStatus();
        }

        private void PublishCombinedStatus()
        {
            string combined = null;
            bool isStreaming;
            lock (_lock)
            {
                isStreaming = _streaming;
                var errors = new List<string>(6);
                if (_targetValk && !string.IsNullOrEmpty(_valkError)) errors.Add(_valkError);
                if (_targetBoWei && !string.IsNullOrEmpty(_boweiError)) errors.Add(_boweiError);
                if (_targetThermal && !string.IsNullOrEmpty(_thermalError)) errors.Add(_thermalError);
                if (!string.IsNullOrEmpty(_valkWpeError)) errors.Add(_valkWpeError);
                if (!string.IsNullOrEmpty(_boweiWpeError)) errors.Add(_boweiWpeError);
                if (!string.IsNullOrEmpty(_thermalWpeError)) errors.Add(_thermalWpeError);
                if (errors.Count > 0) combined = string.Join(" | ", errors);
            }

            if (string.IsNullOrEmpty(combined))
                Report(true, isStreaming ? Loc.T("st.active") : Loc.T("st.ready"));
            else
                Report(false, combined);
        }

        private void RenderPreview(ScreenId screen)
        {
            try
            {
                int canvasW, canvasH;
                CanvasSize(screen, out canvasW, out canvasH);
                double sc = Math.Min(1.0, Math.Min(768.0 / canvasW, 768.0 / canvasH));
                int w = Math.Max(1, (int)Math.Round(canvasW * sc));
                int h = Math.Max(1, (int)Math.Round(canvasH * sc));
                using (Bitmap bmp = RenderScene(screen, w, h))
                using (MemoryStream stream = new MemoryStream())
                {
                    bmp.Save(stream, ImageFormat.Png);
                    byte[] png = stream.ToArray();
                    lock (_lock) { _previewPng = png; }
                }
            }
            catch { }
        }

        // ---- scene rendering --------------------------------------------------------

        private static readonly Color BgTop = Color.FromArgb(11, 16, 32);
        private static readonly Color BgBottom = Color.FromArgb(20, 27, 45);
        private static readonly Color Muted = Color.FromArgb(150, 165, 190);

        private Bitmap RenderScene(ScreenId screen, int w, int h)
        {
            // Snapshot scene fields + overlay copies under lock, then render unlocked.
            // Because old IBackgroundSource instances are disposed only on the Loop thread,
            // the captured source reference remains valid for the duration of this render call.
            BackgroundKind bg; Color bgColor; IBackgroundSource bgSource;
            double rotation; bool flip; double scale, scaleX, scaleY, panX, panY; FitMode fit;
            List<Overlay> overlays = new List<Overlay>();
            SensorReading sensors;
            lock (_lock)
            {
                LcdScene scene = Scene(screen);
                bg = scene.Background; bgColor = scene.BgColor;
                rotation = scene.Rotation; flip = scene.FlipH; scale = scene.Scale; scaleX = scene.ScaleX; scaleY = scene.ScaleY; panX = scene.PanX; panY = scene.PanY; fit = scene.Fit;
                bgSource = (bg == BackgroundKind.Image || bg == BackgroundKind.Gif || bg == BackgroundKind.Wallpaper) ? scene.Source : null;
                foreach (Overlay o in scene.Overlays) overlays.Add(o.Clone());
                sensors = _sensors;
            }

            Image sourceFrame = null;
            if (bgSource != null)
            {
                sourceFrame = bgSource.AcquireFrame();
                bgSource.MarkFrameConsumed();
            }

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;

                if (bg == BackgroundKind.Color)
                    using (SolidBrush b = new SolidBrush(bgColor)) g.FillRectangle(b, 0, 0, w, h);
                else
                    using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, w, h), BgTop, BgBottom, 90f))
                        g.FillRectangle(b, 0, 0, w, h);

                if (sourceFrame != null) DrawTransformed(g, sourceFrame, w, h, rotation, flip, scale, scaleX, scaleY, panX, panY, fit);

                foreach (Overlay o in overlays) DrawOverlay(g, o, sensors, w, h);
            }
            return bmp;
        }

        private static void DrawTransformed(Graphics g, Image src, int w, int h, double rotation, bool flip,
                                            double scale, double scaleX, double scaleY, double panX, double panY, FitMode fit)
        {
            double userScale = scale <= 0 ? 0.01 : scale;
            float dw, dh;
            if (fit == FitMode.Stretch)
            {
                double sx = scaleX <= 0 ? 1.0 : scaleX;
                double sy = scaleY <= 0 ? 1.0 : scaleY;
                dw = (float)(w * userScale * sx);
                dh = (float)(h * userScale * sy);
            }
            else
            {
                double baseScale = fit == FitMode.Fill
                    ? Math.Max((double)w / src.Width, (double)h / src.Height)
                    : Math.Min((double)w / src.Width, (double)h / src.Height);
                double sc = baseScale * userScale;
                dw = (float)(src.Width * sc);
                dh = (float)(src.Height * sc);
            }

            GraphicsState state = g.Save();
            g.TranslateTransform((float)(w / 2.0 + panX * w), (float)(h / 2.0 + panY * h));
            g.RotateTransform((float)rotation);
            g.ScaleTransform(flip ? -1f : 1f, 1f);
            g.DrawImage(src, -dw / 2f, -dh / 2f, dw, dh);
            g.Restore(state);
        }

        private static void DrawOverlay(Graphics g, Overlay o, SensorReading sensors, int w, int h)
        {
            string value = OverlayValue(o, sensors);
            string label = o.ShowLabel ? OverlayLabel(o) : "";
            float fontPx = Math.Max(8f, (float)(o.Size * h));
            float cx = (float)(o.X * w);
            float cy = (float)(o.Y * h);

            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy);
            if (o.Rotation != 0) g.RotateTransform((float)o.Rotation);
            if (label.Length > 0)
            {
                float labelPx = Math.Max(7f, fontPx * 0.32f);
                using (Font labelFont = new Font("Segoe UI", labelPx, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush labelBrush = new SolidBrush(Muted))
                    DrawCenteredAt(g, label, labelFont, labelBrush, 0f, -fontPx * 0.55f, w, h);
            }
            if (o.Kind == OverlayKind.CpuCores)
            {
                DrawCoreBars(g, o, sensors, h);
            }
            else
            {
                using (Font font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush brush = new SolidBrush(o.Color))
                    DrawCenteredAt(g, value, font, brush, 0f, 0f, w, h);
            }
            g.Restore(st);
        }

        private static void DrawCoreBars(Graphics g, Overlay o, SensorReading s, int h)
        {
            double[] loads = s != null ? s.CpuCoreLoads : null;
            int n = loads != null ? loads.Length : 0;
            float boxH = Math.Max(10f, (float)(o.Size * h));
            if (n == 0)
            {
                using (Font f = new Font("Segoe UI", Math.Max(8f, boxH * 0.5f), FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush b = new SolidBrush(Muted))
                    DrawCenteredAt(g, "CPU --", f, b, 0f, 0f, 4000, 4000);
                return;
            }
            float barW = boxH * 0.16f;
            float gap = barW * 0.45f;
            float totalW = n * barW + (n - 1) * gap;
            float left = -totalW / 2f;
            float bottom = boxH / 2f;
            using (Pen baseline = new Pen(Muted, 1f))
                g.DrawLine(baseline, left, bottom, left + totalW, bottom);
            for (int i = 0; i < n; i++)
            {
                double load = loads[i];
                if (load < 0) load = 0; else if (load > 100) load = 100;
                float bh = (float)(load / 100.0) * boxH;
                float x = left + i * (barW + gap);
                using (SolidBrush br = new SolidBrush(LoadColor(load)))
                    g.FillRectangle(br, x, bottom - bh, barW, bh);
            }
        }

        private static Color LoadColor(double v)
        {
            if (v < 50) return Color.FromArgb(63, 209, 139);
            if (v < 80) return Color.FromArgb(245, 200, 80);
            return Color.FromArgb(255, 107, 129);
        }

        private static void DrawCenteredAt(Graphics g, string text, Font font, Brush brush, float cx, float cy, int w, int h)
        {
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, new RectangleF(cx - w, cy - h, w * 2f, h * 2f), fmt);
            }
        }

        private static string OverlayValue(Overlay o, SensorReading s)
        {
            switch (o.Kind)
            {
                case OverlayKind.Text: return o.Text;
                case OverlayKind.CpuTemp: return s != null && s.HasCpuTemp ? Round(s.CpuTempC) + "°C" : "--";
                case OverlayKind.GpuTemp: return s != null && s.HasGpuTemp ? Round(s.GpuTempC) + "°C" : "--";
                case OverlayKind.CpuLoad: return s != null && s.HasCpuLoad ? Round(s.CpuLoad) + "%" : "--";
                case OverlayKind.GpuLoad: return s != null && s.HasGpuLoad ? Round(s.GpuLoad) + "%" : "--";
                case OverlayKind.CpuClock: return s != null && s.HasCpuClock ? (s.CpuClockMhz / 1000.0).ToString("0.0") + " GHz" : "--";
                case OverlayKind.GpuClock: return s != null && s.HasGpuClock ? Round(s.GpuClockMhz) + " MHz" : "--";
                case OverlayKind.CpuPower: return s != null && s.HasCpuPower ? Round(s.CpuPowerW) + " W" : "--";
                case OverlayKind.GpuPower: return s != null && s.HasGpuPower ? Round(s.GpuPowerW) + " W" : "--";
                case OverlayKind.GpuVram: return s != null && s.HasGpuVram ? (s.GpuVramUsedMb / 1024.0).ToString("0.0") + " GB" : "--";
                case OverlayKind.RamLoad: return s != null && s.HasRamLoad ? Round(s.RamLoad) + "%" : "--";
                case OverlayKind.GpuFan: return s != null && s.HasGpuFan ? Round(s.GpuFanRpm) + " RPM" : "--";
                case OverlayKind.CpuCores: return "";
                case OverlayKind.Clock: return DateTime.Now.ToString("HH:mm:ss");
                case OverlayKind.Date: return DateTime.Now.ToString("ddd dd MMM");
            }
            return "";
        }

        private static string OverlayLabel(Overlay o)
        {
            switch (o.Kind)
            {
                case OverlayKind.CpuTemp: return "CPU";
                case OverlayKind.GpuTemp: return "GPU";
                case OverlayKind.CpuLoad: return "CPU";
                case OverlayKind.GpuLoad: return "GPU";
                case OverlayKind.CpuClock: return "CPU CLOCK";
                case OverlayKind.GpuClock: return "GPU CLOCK";
                case OverlayKind.CpuPower: return "CPU";
                case OverlayKind.GpuPower: return "GPU";
                case OverlayKind.GpuVram: return "VRAM";
                case OverlayKind.RamLoad: return "RAM";
                case OverlayKind.GpuFan: return "VENTOLA";
                case OverlayKind.CpuCores: return "CORE CPU";
                default: return "";
            }
        }

        private static string Round(double v) { return ((int)Math.Round(v)).ToString(); }

        // ---- pixel access -----------------------------------------------------------

        private static byte[] BgraFromBitmap(Bitmap bmp)
        {
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = bmp.Width * 4;
                byte[] buffer = new byte[rowBytes * bmp.Height];
                for (int y = 0; y < bmp.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buffer, y * rowBytes, rowBytes);
                return buffer;
            }
            finally { bmp.UnlockBits(data); }
        }

        // ---- status -----------------------------------------------------------------

        private void Report(bool ok, string message)
        {
            bool changed;
            lock (_lock) { changed = message != _status || ok != _statusOk; _status = message; _statusOk = ok; }
            if (changed && StatusChanged != null) StatusChanged(ok, message);
        }
    }
}
