using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LcdFusion
{
    // Direct WinUSB driver for BoWei V2 waterblock LCDs (e.g. Vk-Copl-08, VID 0x374A : PID 0xA005).
    // Uses system in-box WinUSB (no libusb0 required), BGR565 LE raw frames, continuous ~30 fps streaming.
    internal static class BoWeiDirectService
    {
        public const ushort Vid = 0x374A;
        public const ushort Pid = 0xA005;
        public const int DefaultWidth = 480;
        public const int DefaultHeight = 480;
        public const byte DefaultBrightness = 0x50;

        private static readonly Guid InterfaceGuid = new Guid("D6A0CF3F-3825-4BD5-BB19-AAE43828058C");
        private const byte PipeOut = 0x02;
        private const byte PipeIn = 0x81;
        private const uint PipeTransferTimeoutPolicy = 0x03;
        private const uint TimeoutMs = 2000;
        private const int BulkChunkSize = 0x20000; // 131072 bytes
        private const int ZlpAlignment = 4096;
        private const int TargetFrameMs = 33;      // ~30 fps

        private const ushort CmdGetInfo = 0x0001;
        private const ushort CmdSetBrightness = 0x0002;
        private const ushort CmdGetAttr = 0x0004;
        private const ushort CmdStartFrame = 0x1001;
        private const ushort CmdHandshake = 0x1003;

        private const uint DigcfPresent = 0x02;
        private const uint DigcfDeviceInterface = 0x10;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x01;
        private const uint FileShareWrite = 0x02;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;

        private static readonly object Sync = new object();
        private static readonly ManualResetEvent FrameSent = new ManualResetEvent(false);
        private static Thread _streamThread;
        private static byte[] _currentFrame;
        private static bool _stopRequested;
        private static string _streamError;

        private static int _width = DefaultWidth;
        private static int _height = DefaultHeight;
        private static byte _brightness = DefaultBrightness;
        private static bool _brightnessDirty;
        private static string _detectedSerial = "";

        public static event Action PanelChanged;

        public static int Width { get { lock (Sync) { return _width; } } }
        public static int Height { get { lock (Sync) { return _height; } } }
        public static byte Brightness { get { lock (Sync) { return _brightness; } } }
        public static string DetectedSerial { get { lock (Sync) { return _detectedSerial; } } }

        public static bool IsDevicePresent()
        {
            try { return !string.IsNullOrEmpty(FindDevicePath()); }
            catch { return false; }
        }

        public static void SetBrightness(int value)
        {
            byte clamped = (byte)Math.Max(0, Math.Min(100, value));
            lock (Sync)
            {
                if (_brightness == clamped) return;
                _brightness = clamped;
                _brightnessDirty = true;
            }
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
                return Fail("Invalid BoWei frame.");
            if (VendorService.IsMythCoolRunning())
                return Fail(Loc.T("err.closeMythCool"));

            try { return Push(BuildRgb565Frame(bgra, width, height)); }
            catch (Exception ex) { return Fail("BoWei send failed: " + ex.Message); }
        }

        public static void Stop()
        {
            Thread thread;
            lock (Sync)
            {
                _stopRequested = true;
                thread = _streamThread;
            }
            if (thread != null && thread != Thread.CurrentThread) thread.Join(4000);
        }

        private static DirectSendResult Push(byte[] frame)
        {
            bool firstStart;
            lock (Sync)
            {
                _currentFrame = frame;
                firstStart = _streamThread == null || !_streamThread.IsAlive;
                if (firstStart)
                {
                    _streamError = null;
                    _stopRequested = false;
                    FrameSent.Reset();
                    _streamThread = new Thread(StreamLoop) { IsBackground = true, Name = "LCD Fusion BoWei" };
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
                return Fail("Timeout starting BoWei stream.");
            lock (Sync)
            {
                return string.IsNullOrEmpty(_streamError)
                    ? new DirectSendResult(true, Loc.T("st.active"))
                    : Fail(_streamError);
            }
        }

        private static void StreamLoop()
        {
            IntPtr hFile = new IntPtr(-1);
            IntPtr hWinUsb = IntPtr.Zero;
            try
            {
                string path = FindDevicePath();
                if (string.IsNullOrEmpty(path))
                    throw new InvalidOperationException("BoWei device not found (374A:A005).");

                hFile = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                                    IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                if (hFile == IntPtr.Zero || hFile == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW failed for BoWei device");

                if (!WinUsb_Initialize(hFile, out hWinUsb))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_Initialize failed for BoWei device");

                uint timeout = TimeoutMs;
                WinUsb_SetPipePolicy(hWinUsb, PipeOut, PipeTransferTimeoutPolicy, 4, ref timeout);
                WinUsb_SetPipePolicy(hWinUsb, PipeIn, PipeTransferTimeoutPolicy, 4, ref timeout);

                byte[] readBuf = new byte[512];
                ResetAndDrainPipes(hWinUsb, readBuf);
                int smemLen = Negotiate(hWinUsb, readBuf);

                byte[] startFrameCmd = new byte[10];
                startFrameCmd[0] = (byte)(CmdStartFrame & 0xFF);
                startFrameCmd[1] = (byte)(CmdStartFrame >> 8);
                startFrameCmd[2] = 4; // payload_len = 4 (u32 frame byte count)

                Stopwatch sw = Stopwatch.StartNew();

                while (true)
                {
                    byte[] frame;
                    bool sendBrightness = false;
                    byte brightnessValue = DefaultBrightness;

                    lock (Sync)
                    {
                        if (_stopRequested) break;
                        frame = (_currentFrame != null && _currentFrame.Length == smemLen) ? _currentFrame : null;
                        if (_brightnessDirty)
                        {
                            sendBrightness = true;
                            brightnessValue = _brightness;
                            _brightnessDirty = false;
                        }
                    }

                    if (sendBrightness)
                        SendCommand(hWinUsb, CmdSetBrightness, new[] { brightnessValue }, readBuf);

                    if (frame == null)
                    {
                        FrameSent.Set();
                        Thread.Sleep(20);
                        continue;
                    }

                    long frameStart = sw.ElapsedMilliseconds;

                    // 1) Frame start command (0x1001, u32 LE byte length) + read reply
                    PutU32(startFrameCmd, 6, (uint)frame.Length);
                    WritePipeWithZlp(hWinUsb, startFrameCmd, 0, startFrameCmd.Length);
                    ReadCommandReply(hWinUsb, CmdStartFrame, readBuf);

                    // 2) Raw RGB565 LE pixels in 131072-byte chunks (ZLP automatically appended on 4K-aligned chunks)
                    for (int offset = 0; offset < frame.Length; offset += BulkChunkSize)
                    {
                        int len = Math.Min(BulkChunkSize, frame.Length - offset);
                        WritePipeWithZlp(hWinUsb, frame, offset, len);
                    }

                    FrameSent.Set();

                    int elapsed = (int)(sw.ElapsedMilliseconds - frameStart);
                    int sleepMs = TargetFrameMs - elapsed;
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                }
            }
            catch (Exception ex)
            {
                lock (Sync) { _streamError = ex.Message; }
                FrameSent.Set();
            }
            finally
            {
                if (hWinUsb != IntPtr.Zero) try { WinUsb_Free(hWinUsb); } catch { }
                if (hFile != IntPtr.Zero && hFile != new IntPtr(-1)) try { CloseHandle(hFile); } catch { }
                lock (Sync)
                {
                    _streamThread = null;
                    _currentFrame = null;
                }
            }
        }

        private static int Negotiate(IntPtr hWinUsb, byte[] readBuf)
        {
            // 1) Handshake: 0x1003, payload = u32 LE 0x00012345
            SendCommand(hWinUsb, CmdHandshake, BitConverter.GetBytes((uint)0x00012345), readBuf);

            // 2) Read screen parameters: 0x0001 -> 28 bytes (+0x0C width, +0x10 height, +0x18 smem_len)
            byte[] info = SendCommand(hWinUsb, CmdGetInfo, null, readBuf);
            if (info.Length < 28)
                throw new IOException("BoWei 0x0001 response too short (" + info.Length + " bytes).");

            int width = (int)BitConverter.ToUInt32(info, 0x0C);
            int height = (int)BitConverter.ToUInt32(info, 0x10);
            int smemLen = (int)BitConverter.ToUInt32(info, 0x18);
            if (width <= 0 || height <= 0 || smemLen <= 0)
                throw new IOException("Invalid BoWei panel geometry: " + width + "x" + height + " smem=" + smemLen);

            // 3) Read optional device attributes (SN at +0x06..+0x20)
            string serial = "";
            try
            {
                byte[] attr = SendCommand(hWinUsb, CmdGetAttr, null, readBuf);
                if (attr.Length > 6)
                {
                    int end = 6;
                    while (end < attr.Length && attr[end] != 0) end++;
                    serial = Encoding.ASCII.GetString(attr, 6, end - 6).Trim();
                }
            }
            catch { }

            byte initialBrightness;
            bool dimsChanged;
            lock (Sync)
            {
                if (!string.IsNullOrEmpty(serial)) _detectedSerial = serial;
                dimsChanged = _width != width || _height != height;
                _width = width;
                _height = height;
                initialBrightness = _brightness;
                _brightnessDirty = false;
            }
            if (dimsChanged)
            {
                Action handler = PanelChanged;
                if (handler != null) handler();
            }

            // 4) Set brightness (Myth.Cool default 0x50)
            SendCommand(hWinUsb, CmdSetBrightness, new[] { initialBrightness }, readBuf);

            return smemLen;
        }

        private static byte[] SendCommand(IntPtr hWinUsb, ushort cmdId, byte[] payload, byte[] readBuf)
        {
            int payloadLen = payload != null ? payload.Length : 0;
            byte[] packet = new byte[6 + payloadLen];
            packet[0] = (byte)(cmdId & 0xFF);
            packet[1] = (byte)(cmdId >> 8);
            PutU32(packet, 2, (uint)payloadLen);
            if (payloadLen > 0) Buffer.BlockCopy(payload, 0, packet, 6, payloadLen);

            WritePipeWithZlp(hWinUsb, packet, 0, packet.Length);
            return ReadCommandReply(hWinUsb, cmdId, readBuf);
        }

        private static void ResetAndDrainPipes(IntPtr hWinUsb, byte[] readBuf)
        {
            try { WinUsb_ResetPipe(hWinUsb, PipeOut); } catch { }
            try { WinUsb_ResetPipe(hWinUsb, PipeIn); } catch { }
            try { WinUsb_FlushPipe(hWinUsb, PipeIn); } catch { }

            // Drain any stale replies left in the device's IN FIFO from an interrupted previous session
            // (e.g. abrupt app termination or stopping the debugger mid-command).
            uint drainTimeout = 60;
            WinUsb_SetPipePolicy(hWinUsb, PipeIn, PipeTransferTimeoutPolicy, 4, ref drainTimeout);
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    uint got;
                    if (!WinUsb_ReadPipe(hWinUsb, PipeIn, readBuf, (uint)readBuf.Length, out got, IntPtr.Zero) || got == 0)
                        break;
                }
            }
            finally
            {
                uint normalTimeout = TimeoutMs;
                WinUsb_SetPipePolicy(hWinUsb, PipeIn, PipeTransferTimeoutPolicy, 4, ref normalTimeout);
            }
        }

        private static byte[] ReadCommandReply(IntPtr hWinUsb, ushort cmdId, byte[] readBuf)
        {
            ushort expectedCmd = (ushort)(cmdId | 0x8000);
            ushort lastMismatchCmd = 0;
            uint lastMismatchGot = 0;
            uint lastMismatchLen = 0;
            bool shortenedTimeout = false;

            try
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    uint got;
                    if (!WinUsb_ReadPipe(hWinUsb, PipeIn, readBuf, (uint)readBuf.Length, out got, IntPtr.Zero))
                    {
                        if (attempt > 0 && lastMismatchCmd != 0)
                            throw new IOException(string.Format("BoWei reply mismatch: 0x{0:X4} != 0x{1:X4} (got={2}, len={3})", lastMismatchCmd, expectedCmd, lastMismatchGot, lastMismatchLen));
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_ReadPipe failed for cmd 0x" + cmdId.ToString("X4"));
                    }

                    if (got < 6)
                        throw new IOException("BoWei reply too short for cmd 0x" + cmdId.ToString("X4") + " (" + got + " bytes).");

                    ushort respCmd = BitConverter.ToUInt16(readBuf, 0);
                    uint respLen = BitConverter.ToUInt32(readBuf, 2);
                    if (respCmd != expectedCmd)
                    {
                        // Skip stale queued reply from an earlier interrupted command and read the next packet.
                        lastMismatchCmd = respCmd;
                        lastMismatchGot = got;
                        lastMismatchLen = respLen;
                        if (!shortenedTimeout)
                        {
                            uint shortTimeout = 300;
                            WinUsb_SetPipePolicy(hWinUsb, PipeIn, PipeTransferTimeoutPolicy, 4, ref shortTimeout);
                            shortenedTimeout = true;
                        }
                        continue;
                    }

                    int available = (int)Math.Min(respLen, got - 6);
                    byte[] result = new byte[available];
                    if (available > 0) Buffer.BlockCopy(readBuf, 6, result, 0, available);
                    return result;
                }

                throw new IOException(string.Format("BoWei reply mismatch: 0x{0:X4} != 0x{1:X4} (got={2}, len={3})", lastMismatchCmd, expectedCmd, lastMismatchGot, lastMismatchLen));
            }
            finally
            {
                if (shortenedTimeout)
                {
                    uint normalTimeout = TimeoutMs;
                    WinUsb_SetPipePolicy(hWinUsb, PipeIn, PipeTransferTimeoutPolicy, 4, ref normalTimeout);
                }
            }
        }

        private static void WritePipeWithZlp(IntPtr hWinUsb, byte[] data, int offset, int length)
        {
            if (length > 0)
            {
                GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    IntPtr ptr = IntPtr.Add(pinned.AddrOfPinnedObject(), offset);
                    uint sent;
                    if (!WinUsb_WritePipe(hWinUsb, PipeOut, ptr, (uint)length, out sent, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_WritePipe failed (" + length + " bytes)");
                    if (sent != (uint)length)
                        throw new IOException("WinUsb_WritePipe short write: " + sent + "/" + length);
                }
                finally
                {
                    pinned.Free();
                }

                // BoWei V2 protocol requires a ZLP after every 4096-byte aligned transfer (test edi, 0xfff).
                if ((length % ZlpAlignment) == 0)
                {
                    uint zlpSent;
                    if (!WinUsb_WritePipe(hWinUsb, PipeOut, IntPtr.Zero, 0, out zlpSent, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_WritePipe ZLP failed");
                }
            }
        }

        private static byte[] BuildRgb565Frame(byte[] bgra, int width, int height)
        {
            int pixelCount = width * height;
            byte[] frame = new byte[pixelCount * 2];
            int dst = 0;
            int totalBytes = pixelCount * 4;
            for (int p = 0; p < totalBytes; p += 4)
            {
                int b = bgra[p];
                int g = bgra[p + 1];
                int r = bgra[p + 2];
                // BoWei V2 480x480 panel expects BGR565 LE (bits 15..11 = B, 10..5 = G, 4..0 = R).
                int v = ((b & 0xF8) << 8) | ((g & 0xFC) << 3) | (r >> 3);
                frame[dst++] = (byte)(v & 0xFF);
                frame[dst++] = (byte)(v >> 8);
            }
            return frame;
        }

        private static string FindDevicePath()
        {
            List<string> paths = EnumerateInterfacePaths(InterfaceGuid);
            if (paths.Count == 0) return null;
            foreach (string p in paths)
            {
                if (p.IndexOf("vid_374a&pid_a005", StringComparison.OrdinalIgnoreCase) >= 0)
                    return p;
            }
            return paths[0];
        }

        private static List<string> EnumerateInterfacePaths(Guid guid)
        {
            var result = new List<string>();
            IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf(typeof(SpDeviceInterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) break;

                    uint required;
                    SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 4) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out required, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (!string.IsNullOrEmpty(path)) result.Add(path);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        private static void PutU32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static DirectSendResult Fail(string message)
        {
            return new DirectSendResult(false, message);
        }

        // ---- Win32 / SetupAPI / WinUSB P/Invoke -------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr infoSet, IntPtr deviceInfo, ref Guid classGuid, uint index, ref SpDeviceInterfaceData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr infoSet, ref SpDeviceInterfaceData data, IntPtr detail, uint detailSize,
            out uint requiredSize, IntPtr deviceInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr infoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(IntPtr deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(
            IntPtr interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(
            IntPtr interfaceHandle, byte pipeId, IntPtr buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(
            IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ResetPipe(IntPtr interfaceHandle, byte pipeId);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_FlushPipe(IntPtr interfaceHandle, byte pipeId);
    }
}
