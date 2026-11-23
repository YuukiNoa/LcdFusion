using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace LcdFusion
{
    internal sealed class DirectSendResult
    {
        public DirectSendResult(bool success, string message)
        {
            Success = success;
            Message = message ?? "";
        }

        public bool Success { get; }
        public string Message { get; }
    }

    internal static class ValkyrieDirectService
    {
        // Myth.Cool sends the b5 00 32 heartbeat roughly every 250 ms (~4 Hz) during streaming.
        private const int KeepaliveIntervalMs = 250;
        private const int BulkChunkSize = 65536;
        private static readonly byte[] ZlpBuffer = new byte[1];

        private static readonly object Sync = new object();
        private static readonly ManualResetEvent FrameSent = new ManualResetEvent(false);
        private static readonly Stopwatch BackoffClock = Stopwatch.StartNew();
        private static Thread _streamThread;
        private static byte[] _currentFrame;
        private static int _currentFrameWidth;
        private static int _currentFrameHeight;
        private static bool _stopRequested;
        private static bool _probing;
        private static string _streamError;
        private static int _consecutiveFailures;
        private static long _nextRetryMs;

        private static string _panelMode = "auto";
        private static int _width = ProductCatalog.DefaultValkyriePanel.Width;
        private static int _height = ProductCatalog.DefaultValkyriePanel.Height;
        private static byte _vic = ProductCatalog.DefaultValkyriePanel.Vic;
        private static string _detectedSerial = "";

        public static event Action PanelChanged;

        public static int Width { get { lock (Sync) { return _width; } } }
        public static int Height { get { lock (Sync) { return _height; } } }
        public static byte Vic { get { lock (Sync) { return _vic; } } }
        public static string PanelMode { get { lock (Sync) { return _panelMode; } } }
        public static string DetectedSerial { get { lock (Sync) { return _detectedSerial; } } }
        public static bool IsAutoMode { get { lock (Sync) { return string.Equals(_panelMode, "auto", StringComparison.OrdinalIgnoreCase); } } }
        public static string CurrentPanelKey { get { lock (Sync) { return _width + "x" + _height; } } }

        // Configures either "auto" (probe Flash SN) or an explicit panel key like "360x960".
        // Restarts the active stream if resolution/VIC changed so A6 01 / A6 02 are re-sent.
        public static void ConfigurePanel(string mode, string fallbackKey = null)
        {
            string normalized = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim();
            ValkyriePanelSpec spec = string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
                ? (ProductCatalog.FindValkyriePanel(fallbackKey) ?? ProductCatalog.DefaultValkyriePanel)
                : (ProductCatalog.FindValkyriePanel(normalized) ?? ProductCatalog.DefaultValkyriePanel);

            bool changed;
            bool wasRunning;
            lock (Sync)
            {
                _panelMode = string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : spec.Key;
                changed = _width != spec.Width || _height != spec.Height || _vic != spec.Vic;
                if (changed)
                {
                    _width = spec.Width;
                    _height = spec.Height;
                    _vic = spec.Vic;
                    _currentFrame = null;
                    _currentFrameWidth = 0;
                    _currentFrameHeight = 0;
                    _consecutiveFailures = 0;
                    _nextRetryMs = 0;
                }
                wasRunning = _streamThread != null && _streamThread.IsAlive;
            }

            if (changed)
            {
                if (wasRunning) Stop();
                NotifyPanelChanged();
            }
        }

        // Probes the connected 345F:9132 HID interface for its Flash SN and updates resolution/VIC when in Auto mode.
        public static bool TryProbePanel()
        {
            if (!IsAutoMode || VendorService.IsMythCoolRunning()) return false;
            lock (Sync)
            {
                // Avoid colliding with an already running stream thread or another probe.
                if (_probing || (_streamThread != null && _streamThread.IsAlive)) return false;
                _probing = true;
            }

            try
            {
                int w, h;
                byte vic;
                string serial;
                if (!global::ValkyrieHidInitializer.TryProbePanel(out w, out h, out vic, out serial))
                    return false;

                return ApplyDetectedPanel(w, h, vic, serial);
            }
            finally
            {
                lock (Sync) { _probing = false; }
            }
        }

        private static bool ApplyDetectedPanel(int width, int height, byte vic, string serial)
        {
            bool changed;
            lock (Sync)
            {
                if (!string.IsNullOrEmpty(serial)) _detectedSerial = serial;
                if (!string.Equals(_panelMode, "auto", StringComparison.OrdinalIgnoreCase)) return false;
                changed = _width != width || _height != height || _vic != vic;
                if (changed)
                {
                    _width = width;
                    _height = height;
                    _vic = vic;
                    if (_currentFrameWidth != width || _currentFrameHeight != height)
                    {
                        _currentFrame = null;
                        _currentFrameWidth = 0;
                        _currentFrameHeight = 0;
                    }
                }
            }
            if (changed) NotifyPanelChanged();
            return changed;
        }

        private static void NotifyPanelChanged()
        {
            Action handler = PanelChanged;
            if (handler != null) handler();
        }

        // bgra: width*height*4 bytes, top-down, channel order B,G,R,A.
        public static DirectSendResult ShowBgra(byte[] bgra)
        {
            int w, h;
            lock (Sync) { w = _width; h = _height; }
            return ShowBgra(bgra, w, h);
        }

        public static DirectSendResult ShowBgra(byte[] bgra, int width, int height)
        {
            if (bgra == null || width <= 0 || height <= 0 || bgra.Length < width * height * 4)
                return Fail("Invalid Valkyrie/MSDisplay frame.");
            if (VendorService.IsMythCoolRunning())
                return Fail(Loc.T("err.closeMythCool"));

            try { return Push(BuildFrame(bgra, width, height), width, height); }
            catch (Exception ex) { return Fail("Valkyrie/MSDisplay send failed: " + ex.Message); }
        }

        public static void Stop()
        {
            Thread thread;
            lock (Sync)
            {
                _stopRequested = true;
                _consecutiveFailures = 0;
                _nextRetryMs = 0;
                thread = _streamThread;
            }
            if (thread != null && thread != Thread.CurrentThread) thread.Join(4000);
        }

        private static DirectSendResult Push(byte[] frame, int width, int height)
        {
            bool firstStart;
            lock (Sync)
            {
                _currentFrame = frame;
                _currentFrameWidth = width;
                _currentFrameHeight = height;
                firstStart = _streamThread == null || !_streamThread.IsAlive;
                if (firstStart)
                {
                    if (_nextRetryMs > 0 && BackoffClock.ElapsedMilliseconds < _nextRetryMs)
                        return Fail(_streamError ?? "Valkyrie/MSDisplay unavailable.");
                    _streamError = null;
                    _stopRequested = false;
                    FrameSent.Reset();
                    _streamThread = new Thread(StreamLoop) { IsBackground = true, Name = "LCD Fusion Valkyrie" };
                    _streamThread.Start();
                }
                else if (!string.IsNullOrEmpty(_streamError))
                {
                    return Fail(_streamError);
                }
            }

            if (!firstStart)
                return new DirectSendResult(true, Loc.T("st.active"));

            if (!FrameSent.WaitOne(8000))
                return Fail("Timeout starting Valkyrie/MSDisplay stream.");
            lock (Sync)
            {
                return string.IsNullOrEmpty(_streamError)
                    ? new DirectSendResult(true, Loc.T("st.active"))
                    : Fail(_streamError);
            }
        }

        private static void StreamLoop()
        {
            UsbDevice device = null;
            IUsbDevice wholeDevice = null;
            IntPtr hid = IntPtr.Zero;
            try
            {
                int activeW, activeH;
                byte activeVic;
                bool autoDetect;
                lock (Sync)
                {
                    activeW = _width;
                    activeH = _height;
                    activeVic = _vic;
                    autoDetect = string.Equals(_panelMode, "auto", StringComparison.OrdinalIgnoreCase);
                }

                // Full HID init must complete before the first bulk frame. The handle stays
                // open for the lifetime of the stream so we can interleave the b5 00 32 refresh.
                hid = global::ValkyrieHidInitializer.Open();
                string serial;
                global::ValkyrieHidInitializer.RunInit(hid, autoDetect, ref activeW, ref activeH, ref activeVic, out serial, 0x22);

                // If auto-detection discovered a different panel resolution during init, update immediately.
                ApplyDetectedPanel(activeW, activeH, activeVic, serial);
                int expectedLen = 8 + activeW * activeH * 2 + 8;
                int targetFrameMs = (activeW * activeH > 100000) ? 40 : 33; // ~25 fps for 360x960, ~30 fps for 320x240

                device = UsbDevice.OpenUsbDevice(new UsbDeviceFinder(0x345F, 0x9132));
                if (device == null)
                    throw new InvalidOperationException("Valkyrie/MSDisplay USB device unavailable: " + UsbDevice.LastErrorString);
                wholeDevice = device as IUsbDevice;
                if (wholeDevice != null && !wholeDevice.ClaimInterface(3))
                    throw new InvalidOperationException("Valkyrie/MSDisplay interface 3 busy: " + UsbDevice.LastErrorString);

                UsbEndpointWriter writer = device.OpenEndpointWriter(WriteEndpointID.Ep04);
                writer.Reset();
                writer.Flush();

                Stopwatch sw = Stopwatch.StartNew();
                bool firstFrameDone = false;
                long lastCommitMs = 0;

                while (true)
                {
                    byte[] frame;
                    lock (Sync)
                    {
                        if (_stopRequested) break;
                        frame = (_currentFrame != null && _currentFrame.Length == expectedLen) ? _currentFrame : null;
                    }
                    if (frame == null)
                    {
                        // Panel resolution may have just been auto-detected; wait briefly for engine re-render.
                        FrameSent.Set();
                        Thread.Sleep(20);
                        continue;
                    }

                    long frameStart = sw.ElapsedMilliseconds;
                    WriteFrameChunked(writer, frame);

                    // Mirror Myth.Cool: start scan-out right after the first frame is buffered,
                    // then issue periodic B5 00 32 keepalive every <= 250 ms between frames.
                    if (!firstFrameDone)
                    {
                        global::ValkyrieHidInitializer.RunPostFrame(hid);
                        firstFrameDone = true;
                        lastCommitMs = sw.ElapsedMilliseconds;
                        lock (Sync)
                        {
                            _consecutiveFailures = 0;
                            _nextRetryMs = 0;
                        }
                    }
                    else if (sw.ElapsedMilliseconds - lastCommitMs >= KeepaliveIntervalMs)
                    {
                        global::ValkyrieHidInitializer.Commit(hid);
                        lastCommitMs = sw.ElapsedMilliseconds;
                    }

                    FrameSent.Set();
                    int elapsed = (int)(sw.ElapsedMilliseconds - frameStart);
                    int sleepMs = targetFrameMs - elapsed;
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                }
            }
            catch (Exception ex)
            {
                string message = FormatStreamException(ex);
                lock (Sync)
                {
                    _streamError = message;
                    _consecutiveFailures++;
                    int delayMs = Math.Min(5000, 1000 * (1 << Math.Min(_consecutiveFailures - 1, 3)));
                    _nextRetryMs = BackoffClock.ElapsedMilliseconds + delayMs;
                }
                FrameSent.Set();
            }
            finally
            {
                if (wholeDevice != null) try { wholeDevice.ReleaseInterface(3); } catch { }
                if (device != null) try { device.Close(); } catch { }
                if (hid != IntPtr.Zero) try { global::ValkyrieHidInitializer.Close(hid); } catch { }
                lock (Sync)
                {
                    _streamThread = null;
                    _currentFrame = null;
                    _currentFrameWidth = 0;
                    _currentFrameHeight = 0;
                }
            }
        }

        private static void WriteFrameChunked(UsbEndpointWriter writer, byte[] frame)
        {
            for (int offset = 0; offset < frame.Length; offset += BulkChunkSize)
            {
                int len = Math.Min(BulkChunkSize, frame.Length - offset);
                int transferred;
                ErrorCode code = writer.Write(frame, offset, len, 3000, out transferred);
                if (code != ErrorCode.None || transferred != len)
                {
                    code = writer.Write(frame, offset, len, 3000, out transferred);
                    if (code != ErrorCode.None || transferred != len)
                        throw new IOException("USB Valkyrie/MSDisplay error " + code + " (" + transferred + "/" + len + " bytes).");
                }
            }

            // End-of-frame ZLP (matches Myth.Cool ETW capture: e.g. 10*65536 + 35856 + ZLP).
            int zlpTransferred;
            writer.Write(ZlpBuffer, 0, 0, 3000, out zlpTransferred);
        }

        private static byte[] BuildFrame(byte[] bgra, int width, int height)
        {
            byte[] frame = new byte[8 + width * height * 2 + 8];
            // 8-byte header: FF 00 00 00 00 (W>>4) (((W&0xF)<<4)|((H>>8)&0xF)) (H&0xFF)
            //   320x240 -> FF 00 00 00 00 14 00 F0
            //   360x960 -> FF 00 00 00 00 16 83 C0
            frame[0] = 0xFF;
            frame[5] = (byte)((width >> 4) & 0xFF);
            frame[6] = (byte)(((width & 0x0F) << 4) | ((height >> 8) & 0x0F));
            frame[7] = (byte)(height & 0xFF);

            int destination = 8;
            int totalBytes = width * height * 4;
            for (int p0 = 0; p0 < totalBytes; p0 += 8)
            {
                int b0 = bgra[p0];
                int g0 = bgra[p0 + 1];
                int r0 = bgra[p0 + 2];
                int b1 = bgra[p0 + 4];
                int g1 = bgra[p0 + 5];
                int r1 = bgra[p0 + 6];

                int y0 = ((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16;
                int y1 = ((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16;
                int rs = r0 + r1;
                int gs = g0 + g1;
                int bs = b0 + b1;
                int u = ((-38 * rs - 74 * gs + 112 * bs + 256) >> 9) + 128;
                int v = ((112 * rs - 94 * gs - 18 * bs + 256) >> 9) + 128;

                frame[destination++] = ClampByte(u);
                frame[destination++] = ClampByte(y0);
                frame[destination++] = ClampByte(v);
                frame[destination++] = ClampByte(y1);
            }

            // 8-byte footer: FF C0 00 00 00 00 00 00
            frame[destination] = 0xFF;
            frame[destination + 1] = 0xC0;
            return frame;
        }

        private static byte ClampByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        private static string FormatStreamException(Exception ex)
        {
            var win32 = ex as Win32Exception;
            if (win32 != null && (win32.NativeErrorCode == 31 || win32.NativeErrorCode == 1167 || win32.NativeErrorCode == 121))
            {
                return ex.Message + " — USB device is unresponsive; reconnect or power-cycle the cooler USB connection if this persists.";
            }
            return ex.Message;
        }

        private static DirectSendResult Fail(string message)
        {
            return new DirectSendResult(false, message);
        }
    }
}
