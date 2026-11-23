using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace WpeRendererMock
{
    internal static class ShmLayout
    {
        public const uint Magic = 0x4C434457; // "LCDW"
        public const uint Version = 1;
        public const int HeaderSize = 64;

        public const int OffsetMagic = 0;
        public const int OffsetVersion = 4;
        public const int OffsetWidth = 8;
        public const int OffsetHeight = 12;
        public const int OffsetStride = 16;
        public const int OffsetActiveSlot = 20;
        public const int OffsetFrameSeq = 24;
        public const int OffsetState = 32;

        public const uint StateRunning = 1;
        public const uint StatePaused = 2;
        public const uint StateError = 3;
    }

    internal static class Program
    {
        private static readonly object StateLock = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly StreamWriter Stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        private static volatile bool _running = true;
        private static volatile bool _paused;

        private static MemoryMappedFile _mmf;
        private static MemoryMappedViewAccessor _view;
        private static int _width;
        private static int _height;
        private static int _fps = 25;
        private static ulong _frameSeq;
        private static uint _activeSlot;

        private static string _title = "Wallpaper";
        private static string _type = "scene";
        private static Bitmap _previewImage;
        private static Bitmap _canvas;
        private static byte[] _pixelBuffer;

        private static int Main(string[] args)
        {
            var renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "MockRenderLoop" };
            renderThread.Start();

            try
            {
                using (var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                {
                    string line;
                    while (_running && (line = stdin.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        HandleCommand(line);
                    }
                }
            }
            catch (Exception ex)
            {
                EmitError("STDIN_ERROR", ex.Message);
            }
            finally
            {
                _running = false;
                renderThread.Join(1000);
                Cleanup();
            }

            return 0;
        }

        private static void HandleCommand(string line)
        {
            Dictionary<string, object> obj;
            try
            {
                obj = Json.DeserializeObject(line) as Dictionary<string, object>;
                if (obj == null) return;
            }
            catch (Exception ex)
            {
                EmitError("BAD_JSON", ex.Message);
                return;
            }

            string cmd = ReadString(obj, "cmd");
            switch (cmd)
            {
                case "load":
                    HandleLoad(obj);
                    break;
                case "resize":
                    HandleResize(obj);
                    break;
                case "pause":
                    HandlePause(obj);
                    break;
                case "props":
                    // Accepted for IPC parity with the real C++ renderer.
                    break;
                case "quit":
                    _running = false;
                    break;
            }
        }

        private static void HandleLoad(Dictionary<string, object> obj)
        {
            string dir = ReadString(obj, "dir");
            string shm = ReadString(obj, "shm");
            int width = ClampDim(ReadInt(obj, "width", 480));
            int height = ClampDim(ReadInt(obj, "height", 480));
            int fps = ClampFps(ReadInt(obj, "fps", 25));

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                EmitError("DIR_NOT_FOUND", "Wallpaper directory not found: " + dir);
                return;
            }

            string title = Path.GetFileName(dir.TrimEnd('\\', '/')) ?? "Wallpaper";
            string type = "scene";
            string previewPath = "";

            string projectJson = Path.Combine(dir, "project.json");
            if (File.Exists(projectJson))
            {
                try
                {
                    var proj = Json.DeserializeObject(File.ReadAllText(projectJson)) as Dictionary<string, object>;
                    if (proj != null)
                    {
                        string t = ReadString(proj, "title");
                        string tp = ReadString(proj, "type");
                        string pv = ReadString(proj, "preview");
                        if (!string.IsNullOrWhiteSpace(t)) title = t.Trim();
                        if (!string.IsNullOrWhiteSpace(tp)) type = tp.Trim().ToLowerInvariant();
                        if (!string.IsNullOrWhiteSpace(pv))
                        {
                            string candidate = Path.Combine(dir, pv.Replace('/', '\\'));
                            if (File.Exists(candidate)) previewPath = candidate;
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(previewPath))
            {
                foreach (string name in new[] { "preview.jpg", "preview.png", "preview.gif", "preview.jpeg" })
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) { previewPath = candidate; break; }
                }
            }

            Bitmap previewBmp = null;
            if (!string.IsNullOrEmpty(previewPath))
            {
                try
                {
                    byte[] raw = File.ReadAllBytes(previewPath);
                    using (var ms = new MemoryStream(raw))
                    using (Image img = Image.FromStream(ms))
                        previewBmp = new Bitmap(img);
                }
                catch { previewBmp = null; }
            }

            lock (StateLock)
            {
                _title = title;
                _type = type;
                if (_previewImage != null) { try { _previewImage.Dispose(); } catch { } }
                _previewImage = previewBmp;
                OpenSharedMemoryLocked(shm, width, height, fps);
            }
        }

        private static void HandleResize(Dictionary<string, object> obj)
        {
            string shm = ReadString(obj, "shm");
            int width = ClampDim(ReadInt(obj, "width", _width > 0 ? _width : 480));
            int height = ClampDim(ReadInt(obj, "height", _height > 0 ? _height : 480));
            int fps = ClampFps(ReadInt(obj, "fps", _fps));

            lock (StateLock)
            {
                OpenSharedMemoryLocked(shm, width, height, fps);
            }
        }

        private static void HandlePause(Dictionary<string, object> obj)
        {
            bool value = ReadBool(obj, "value", false);
            _paused = value;
            lock (StateLock)
            {
                if (_view != null)
                {
                    _view.Write(ShmLayout.OffsetState, value ? ShmLayout.StatePaused : ShmLayout.StateRunning);
                    Thread.MemoryBarrier();
                }
            }
            Emit(new Dictionary<string, object>
            {
                { "event", "status" },
                { "state", value ? "paused" : "running" }
            });
        }

        private static void OpenSharedMemoryLocked(string shmName, int width, int height, int fps)
        {
            if (string.IsNullOrWhiteSpace(shmName))
            {
                EmitError("BAD_SHM", "Missing shared memory name.");
                return;
            }

            DisposeMappingLocked();

            try
            {
                long size = ShmLayout.HeaderSize + (long)width * 4L * (long)height * 2L;
                _mmf = MemoryMappedFile.OpenExisting(shmName, MemoryMappedFileRights.ReadWrite);
                _view = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);

                uint magic = _view.ReadUInt32(ShmLayout.OffsetMagic);
                if (magic != ShmLayout.Magic)
                {
                    DisposeMappingLocked();
                    EmitError("BAD_MAGIC", "Unexpected shared memory magic.");
                    return;
                }

                _width = width;
                _height = height;
                _fps = fps;
                _frameSeq = 0;
                _activeSlot = 0;

                if (_canvas != null) { try { _canvas.Dispose(); } catch { } }
                _canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                _pixelBuffer = new byte[width * 4 * height];

                _view.Write(ShmLayout.OffsetWidth, (uint)width);
                _view.Write(ShmLayout.OffsetHeight, (uint)height);
                _view.Write(ShmLayout.OffsetStride, (uint)(width * 4));
                _view.Write(ShmLayout.OffsetActiveSlot, 0u);
                _view.Write(ShmLayout.OffsetFrameSeq, 0UL);
                _view.Write(ShmLayout.OffsetState, _paused ? ShmLayout.StatePaused : ShmLayout.StateRunning);
                Thread.MemoryBarrier();

                Emit(new Dictionary<string, object>
                {
                    { "event", "ready" },
                    { "width", width },
                    { "height", height },
                    { "backend", IntPtr.Size == 8 ? "mock-x64" : "mock-x86" }
                });
            }
            catch (Exception ex)
            {
                DisposeMappingLocked();
                EmitError("SHM_OPEN_FAILED", ex.Message);
            }
        }

        private static void RenderLoop()
        {
            var sw = Stopwatch.StartNew();
            while (_running)
            {
                int fps;
                bool canRender;
                lock (StateLock)
                {
                    fps = _fps;
                    canRender = !_paused && _view != null && _canvas != null;
                    if (canRender)
                    {
                        RenderAndPublishLocked(sw.Elapsed.TotalSeconds);
                    }
                }

                int sleepMs = canRender ? Math.Max(8, 1000 / Math.Max(1, fps)) : 40;
                Thread.Sleep(sleepMs);
            }
        }

        private static void RenderAndPublishLocked(double t)
        {
            int w = _width;
            int h = _height;
            int stride = w * 4;
            int slotBytes = stride * h;

            using (Graphics g = Graphics.FromImage(_canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.Bilinear;

                if (_previewImage != null)
                {
                    // Gentle breathing zoom + subtle drifting light sweep on top of the workshop preview image.
                    float breathe = 1.04f + 0.04f * (float)Math.Sin(t * 1.4);
                    float baseScale = Math.Max((float)w / _previewImage.Width, (float)h / _previewImage.Height) * breathe;
                    float dw = _previewImage.Width * baseScale;
                    float dh = _previewImage.Height * baseScale;
                    float dx = (w - dw) * 0.5f + (float)Math.Sin(t * 0.7) * (w * 0.015f);
                    float dy = (h - dh) * 0.5f + (float)Math.Cos(t * 0.5) * (h * 0.015f);
                    g.DrawImage(_previewImage, dx, dy, dw, dh);

                    float sweepX = (float)(((t * 0.28) % 1.6) - 0.3) * w;
                    using (var sweep = new LinearGradientBrush(
                        new PointF(sweepX - w * 0.25f, 0),
                        new PointF(sweepX + w * 0.25f, h * 0.4f),
                        Color.FromArgb(0, 110, 175, 255),
                        Color.FromArgb(0, 110, 175, 255)))
                    {
                        sweep.InterpolationColors = new ColorBlend
                        {
                            Colors = new[]
                            {
                                Color.FromArgb(0, 110, 175, 255),
                                Color.FromArgb(55, 130, 210, 255),
                                Color.FromArgb(0, 110, 175, 255)
                            },
                            Positions = new[] { 0f, 0.5f, 1f }
                        };
                        g.FillRectangle(sweep, 0, 0, w, h);
                    }
                }
                else
                {
                    // Procedural animated gradient + floating orb fallback.
                    Color c1 = ColorFromHsv((t * 24.0) % 360.0, 0.72, 0.22);
                    Color c2 = ColorFromHsv((t * 24.0 + 110.0) % 360.0, 0.78, 0.38);
                    using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), c1, c2, (float)((t * 20.0) % 360.0)))
                        g.FillRectangle(bg, 0, 0, w, h);

                    float cx = w * (0.5f + 0.28f * (float)Math.Sin(t * 1.3));
                    float cy = h * (0.5f + 0.24f * (float)Math.Cos(t * 0.9));
                    float rad = Math.Min(w, h) * 0.30f;
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(cx - rad, cy - rad, rad * 2f, rad * 2f);
                        using (var pgb = new PathGradientBrush(gp))
                        {
                            pgb.CenterColor = Color.FromArgb(190, 95, 220, 255);
                            pgb.SurroundColors = new[] { Color.FromArgb(0, 95, 220, 255) };
                            g.FillEllipse(pgb, cx - rad, cy - rad, rad * 2f, rad * 2f);
                        }
                    }
                }

                // Subtle animated mock indicator bar at the bottom edge so motion is unmistakable.
                float barH = Math.Max(3f, h * 0.015f);
                float phase = (float)((t * 0.45) % 1.0);
                using (var barBg = new SolidBrush(Color.FromArgb(90, 8, 12, 22)))
                    g.FillRectangle(barBg, 0, h - barH, w, barH);
                using (var barFg = new SolidBrush(Color.FromArgb(220, 108, 140, 255)))
                    g.FillRectangle(barFg, phase * w - w * 0.15f, h - barH, w * 0.30f, barH);

                if (_previewImage == null)
                {
                    float fontPx = Math.Max(11f, Math.Min(w, h) * 0.06f);
                    using (var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.FromArgb(220, 236, 240, 247)))
                    {
                        string text = string.Format("WPE MOCK ({0}) · {1}x{2} · #{3}", _type, w, h, _frameSeq + 1);
                        g.DrawString(text, font, brush, 10f, 10f);
                    }
                }
            }

            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData data = _canvas.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (data.Stride == stride)
                {
                    Marshal.Copy(data.Scan0, _pixelBuffer, 0, slotBytes);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _pixelBuffer, y * stride, stride);
                }
            }
            finally
            {
                _canvas.UnlockBits(data);
            }

            uint nextSlot = 1u - (_activeSlot & 1u);
            long offset = ShmLayout.HeaderSize + (long)nextSlot * slotBytes;
            _view.WriteArray(offset, _pixelBuffer, 0, slotBytes);
            Thread.MemoryBarrier();

            _activeSlot = nextSlot;
            _frameSeq++;
            _view.Write(ShmLayout.OffsetActiveSlot, _activeSlot);
            _view.Write(ShmLayout.OffsetFrameSeq, _frameSeq);
            Thread.MemoryBarrier();
        }

        private static Color ColorFromHsv(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(255, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        private static void Emit(Dictionary<string, object> payload)
        {
            try
            {
                lock (Stdout)
                {
                    Stdout.WriteLine(Json.Serialize(payload));
                }
            }
            catch { }
        }

        private static void EmitError(string code, string message)
        {
            Emit(new Dictionary<string, object>
            {
                { "event", "error" },
                { "code", code },
                { "message", message ?? "" }
            });
        }

        private static string ReadString(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
        }

        private static int ReadInt(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToInt32(v); } catch { return fallback; }
        }

        private static bool ReadBool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToBoolean(v); } catch { return fallback; }
        }

        private static int ClampDim(int v) { return v < 64 ? 64 : (v > 2048 ? 2048 : v); }
        private static int ClampFps(int v) { return v < 1 ? 1 : (v > 60 ? 60 : v); }

        private static void DisposeMappingLocked()
        {
            try { if (_view != null) _view.Dispose(); } catch { }
            try { if (_mmf != null) _mmf.Dispose(); } catch { }
            _view = null;
            _mmf = null;
        }

        private static void Cleanup()
        {
            lock (StateLock)
            {
                DisposeMappingLocked();
                if (_canvas != null) { try { _canvas.Dispose(); } catch { } _canvas = null; }
                if (_previewImage != null) { try { _previewImage.Dispose(); } catch { } _previewImage = null; }
            }
        }
    }
}
