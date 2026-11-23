using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

internal static class ValkyrieHidInitializer
{
    private static readonly object HidIoLock = new object();
    private const int CommandMaxAttempts = 3;
    private static string _lastReply;
    private const uint DigcfPresent = 0x2;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(IntPtr device, ref HidAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(IntPtr device, byte[] report, int length);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(IntPtr device, byte[] report, int length);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsedData);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsedData, out HidCaps capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr infoSet, IntPtr deviceInfo, ref Guid classGuid,
                                                           uint index, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr infoSet, ref DeviceInterfaceData data,
                                                               IntPtr detail, uint detailSize,
                                                               out uint requiredSize, IntPtr deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr infoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
                                            uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // Opens the Valkyrie HID interface (MI_00). Caller must Close() the returned handle.
    public static IntPtr Open()
    {
        IntPtr hid = OpenValkyrieHid();
        if (hid == new IntPtr(-1))
            throw new InvalidOperationException("Interfaccia HID Valkyrie non trovata.");
        return hid;
    }

    public static void Close(IntPtr hid)
    {
        if (hid != IntPtr.Zero && hid != new IntPtr(-1)) CloseHandle(hid);
    }

    // Full cold-start initialization sequence for MacroSilicon / MSDisplay (345F:9132).
    // Each entry is an 8-byte HID feature report (SET_REPORT, interface 0); ReadReply mirrors
    // the GET_REPORT poll Myth.Cool performs after the command.
    public static string RunInit(IntPtr hid)
    {
        int w = 320, h = 240;
        byte vic = 0x94;
        string serial;
        return RunInit(hid, false, ref w, ref h, ref vic, out serial, 0x22);
    }

    public static string RunInit(IntPtr hid, int width, int height, byte vic, byte pixelFormat = 0x22)
    {
        string serial;
        return RunInit(hid, false, ref width, ref height, ref vic, out serial, pixelFormat);
    }

    public static string RunInit(IntPtr hid, bool autoDetect, ref int width, ref int height,
                                 ref byte vic, out string serial, byte pixelFormat = 0x22)
    {
        lock (HidIoLock)
        {
            int featureLength = GetFeatureLength(hid);
            int reportLength = featureLength >= 9 ? featureLength : 9;

            // (1) Power / source selection performed by Myth.Cool before reading device metadata.
            foreach (HidCommand command in BuildPowerSequence())
                Command(hid, command.Payload, command.ReadReply, reportLength);

            // (2) Panel controller programming block (required; without F5 + B5 C0 ramp the panel stays dark).
            // Capture the 4 Flash SN segments here so auto-detect works even from a cold start.
            serial = ReadFlashSerial(hid, reportLength);
            if (autoDetect)
            {
                int detW, detH;
                byte detVic;
                if (TryParsePanelFromSerial(serial, out detW, out detH, out detVic))
                {
                    width = detW;
                    height = detH;
                    vic = detVic;
                }
            }

            foreach (HidCommand command in BuildPanelProgramSequence())
                Command(hid, command.Payload, command.ReadReply, reportLength);

            // (3) Geometry / timing / output enable using the active (or auto-detected) resolution + VIC.
            foreach (HidCommand command in BuildGeometrySequence(width, height, vic, pixelFormat))
                Command(hid, command.Payload, command.ReadReply, reportLength);

            return "HID inizializzato (" + width + "x" + height + " VIC=0x" + vic.ToString("X2") +
                   ", feature " + featureLength + " B, reply " + _lastReply + ")";
        }
    }

    // Safely probes the 345F:9132 Flash SN via HID to identify panel resolution/VIC before streaming.
    public static bool TryProbePanel(out int width, out int height, out byte vic, out string serial)
    {
        width = 0; height = 0; vic = 0; serial = "";
        if (!Monitor.TryEnter(HidIoLock)) return false;
        IntPtr hid = new IntPtr(-1);
        try
        {
            hid = OpenValkyrieHid();
            if (hid == new IntPtr(-1)) return false;
            int featureLength = GetFeatureLength(hid);
            int reportLength = featureLength >= 9 ? featureLength : 9;
            serial = ReadFlashSerial(hid, reportLength);
            return TryParsePanelFromSerial(serial, out width, out height, out vic);
        }
        catch
        {
            return false;
        }
        finally
        {
            Close(hid);
            Monitor.Exit(HidIoLock);
        }
    }

    public static byte VicForResolution(int width, int height)
    {
        if (width == 360 && height == 960) return 0xA0;
        if (width == 320 && height == 240) return 0x94;
        if (width == 480 && height == 480) return 0x8F;
        if (width == 240 && height == 320) return 0x93;
        if (width == 240 && height == 240) return 0x9C;
        if (width == 480 && height == 272) return 0x9F;
        return 0x94;
    }

    // Parses Myth.Cool / Valkyrie Flash SN strings such as "MCVKC030360*0960S251901783".
    public static bool TryParsePanelFromSerial(string serial, out int width, out int height, out byte vic)
    {
        width = 0; height = 0; vic = 0;
        if (string.IsNullOrEmpty(serial)) return false;

        int[,] known =
        {
            { 360, 960, 0xA0 },
            { 320, 240, 0x94 },
            { 480, 480, 0x8F },
            { 240, 320, 0x93 },
            { 240, 240, 0x9C },
            { 480, 272, 0x9F },
        };

        string upper = serial.ToUpperInvariant();
        for (int i = 0; i < known.GetLength(0); i++)
        {
            int w = known[i, 0];
            int h = known[i, 1];
            byte v = (byte)known[i, 2];
            string w4 = w.ToString("D4"), h4 = h.ToString("D4");
            string w3 = w.ToString(), h3 = h.ToString();
            if (upper.IndexOf(w4 + "*" + h4, StringComparison.Ordinal) >= 0 ||
                upper.IndexOf(w4 + "X" + h4, StringComparison.Ordinal) >= 0 ||
                upper.IndexOf(w3 + "*" + h3, StringComparison.Ordinal) >= 0 ||
                upper.IndexOf(w3 + "X" + h3, StringComparison.Ordinal) >= 0)
            {
                width = w;
                height = h;
                vic = v;
                return true;
            }
        }
        return false;
    }

    private static string ReadFlashSerial(IntPtr hid, int reportLength)
    {
        byte[] addrs = { 0xE0, 0xE8, 0xF0, 0xF8 };
        var sb = new System.Text.StringBuilder(32);
        bool stopped = false;

        for (int i = 0; i < addrs.Length; i++)
        {
            byte[] reply = CommandWithReply(hid, Payload(0xF5, 0x00, 0x1F, addrs[i]), true, reportLength);
            if (stopped || reply == null || reply.Length < 9) continue;

            // reply[0] is HID Report ID (0x00); reply[1..8] are the 8 response bytes.
            // First chunk (0xE0) begins with header 5A A5 1A followed by 5 ASCII bytes.
            int start = (i == 0 && reply[1] == 0x5A && reply[2] == 0xA5) ? 4 : 1;
            for (int j = start; j < 9; j++)
            {
                byte b = reply[j];
                if (b == 0x00 || b == 0xFF) { stopped = true; break; }
                if (b >= 0x20 && b <= 0x7E) sb.Append((char)b);
            }
        }
        return sb.ToString();
    }

    // Commands Myth.Cool issues immediately after the first bulk frame (start scan-out).
    public static void RunPostFrame(IntPtr hid)
    {
        lock (HidIoLock)
        {
            int featureLength = GetFeatureLength(hid);
            int reportLength = featureLength >= 9 ? featureLength : 9;
            Command(hid, Payload(0xB5, 0xF5, 0x07), true, reportLength);
            Command(hid, Payload(0xB6, 0xF5, 0x07, 0x00), false, reportLength);
            Command(hid, Payload(0xA6, 0x05, 0x01, 0x00), false, reportLength);
            Command(hid, Payload(0xB5, 0xC5, 0x55), true, reportLength);
        }
    }

    // Periodic refresh/keepalive Myth.Cool sends between frames during streaming.
    public static void Commit(IntPtr hid)
    {
        lock (HidIoLock)
        {
            int featureLength = GetFeatureLength(hid);
            int reportLength = featureLength >= 9 ? featureLength : 9;
            Command(hid, Payload(0xB5, 0x00, 0x32), true, reportLength);
        }
    }

    private struct HidCommand
    {
        public byte[] Payload;
        public bool ReadReply;
    }

    private static byte[] Payload(params byte[] head)
    {
        byte[] full = new byte[8];
        Buffer.BlockCopy(head, 0, full, 0, Math.Min(head.Length, 8));
        return full;
    }

    private static HidCommand Hc(bool readReply, params byte[] head)
    {
        return new HidCommand { Payload = Payload(head), ReadReply = readReply };
    }

    private static List<HidCommand> BuildPowerSequence()
    {
        return new List<HidCommand>
        {
            Hc(false, 0xA6, 0x03, 0x03),
            Hc(true,  0xB5, 0xC5, 0x58),
            Hc(false, 0xB6, 0xDE, 0xEE, 0x01),
            Hc(false, 0xA6, 0x07, 0x01, 0x02),
            Hc(true,  0xB5, 0xC4, 0x54),
            Hc(false, 0xA6, 0x05, 0x00),
            Hc(true,  0xB5, 0xC5, 0x55),
            Hc(true,  0xB5, 0xF0, 0x04),
            Hc(false, 0xB6, 0xF0, 0x04, 0x6D),
        };
    }

    private static List<HidCommand> BuildPanelProgramSequence()
    {
        List<HidCommand> seq = new List<HidCommand>
        {
            Hc(true,  0xB5, 0xC4, 0x24),
            Hc(true,  0xB5, 0xC6, 0x50),
            Hc(true,  0xB5, 0xC3, 0x0F),
            Hc(true,  0xB5, 0xFF, 0x00),
            Hc(true,  0xB5, 0xF0, 0x00),
            Hc(true,  0xB5, 0x00, 0x31),
            Hc(true,  0xB5, 0x00, 0x30),
            Hc(true,  0xB5, 0x00, 0x32),
            Hc(true,  0xB5, 0x00, 0x32),
        };
        for (int value = 0x00; value <= 0x7C; value += 0x04)
            seq.Add(Hc(true, 0xB5, 0xC0, (byte)value));
        seq.Add(Hc(true,  0xF5, 0x00, 0x1F, 0xE0));
        seq.Add(Hc(true,  0xF5, 0x00, 0x1F, 0xE8));
        seq.Add(Hc(true,  0xF5, 0x00, 0x1F, 0xF0));
        seq.Add(Hc(true,  0xF5, 0x00, 0x1F, 0xF8));
        seq.Add(Hc(true,  0xB5, 0x00, 0x32));
        return seq;
    }

    private static List<HidCommand> BuildGeometrySequence(int width, int height, byte vic, byte pixelFormat)
    {
        byte wHi = (byte)((width >> 8) & 0xFF);
        byte wLo = (byte)(width & 0xFF);
        byte hHi = (byte)((height >> 8) & 0xFF);
        byte hLo = (byte)(height & 0xFF);

        return new List<HidCommand>
        {
            Hc(false, 0xA6, 0x05, 0x00),
            Hc(true,  0xB5, 0xC5, 0x55),
            Hc(true,  0xB5, 0xF5, 0x07),
            Hc(false, 0xB6, 0xF5, 0x07, 0x02),
            Hc(false, 0xA6, 0x04, 0x00),
            Hc(true,  0xB5, 0xC5, 0x55),
            Hc(true,  0xB5, 0x00, 0x32),
            Hc(false, 0xA6, 0x03, 0x03),
            Hc(true,  0xB5, 0xC5, 0x58),
            // Input resolution + pixel format (0x22 = UYVY)
            Hc(false, 0xA6, 0x01, wHi, wLo, hHi, hLo, pixelFormat, 0x00),
            Hc(true,  0xB5, 0xC5, 0x55),
            // Output timing VIC + resolution
            Hc(false, 0xA6, 0x02, vic, 0x00, wHi, wLo, hHi, hLo),
            Hc(true,  0xB5, 0xC5, 0x57),
            Hc(false, 0xA6, 0x04, 0x01, 0x00),
            Hc(true,  0xB5, 0xC5, 0x55),
            Hc(false, 0xB6, 0xF2, 0x4E, 0x00),
            Hc(true,  0xB5, 0x00, 0x32),
            Hc(true,  0xB5, 0xF2, 0x42, 0x00),
        };
    }

    private static int GetFeatureLength(IntPtr hid)
    {
        IntPtr data;
        if (!HidD_GetPreparsedData(hid, out data)) return -1;
        try
        {
            HidCaps caps;
            return HidP_GetCaps(data, out caps) >= 0 ? caps.FeatureReportByteLength : -2;
        }
        finally { HidD_FreePreparsedData(data); }
    }

    private static void Command(IntPtr hid, byte[] payload, bool readReply, int reportLength)
    {
        CommandWithReply(hid, payload, readReply, reportLength);
    }

    private static byte[] CommandWithReply(IntPtr hid, byte[] payload, bool readReply, int reportLength)
    {
        int len = reportLength >= 9 ? reportLength : 9;
        byte[] report = new byte[len];
        Buffer.BlockCopy(payload, 0, report, 1, 8);

        int lastError = 0;
        for (int attempt = 1; attempt <= CommandMaxAttempts; attempt++)
        {
            if (HidD_SetFeature(hid, report, report.Length))
            {
                lastError = 0;
                break;
            }
            lastError = Marshal.GetLastWin32Error();
            if (attempt < CommandMaxAttempts)
                Thread.Sleep(attempt * 50);
        }
        if (lastError != 0)
            throw new Win32Exception(lastError, FormatHidError("HidD_SetFeature", payload, lastError));

        if (readReply)
        {
            byte[] reply = new byte[len];
            for (int attempt = 1; attempt <= CommandMaxAttempts; attempt++)
            {
                Array.Clear(reply, 0, reply.Length);
                if (HidD_GetFeature(hid, reply, reply.Length))
                {
                    _lastReply = BitConverter.ToString(reply);
                    return reply;
                }
                lastError = Marshal.GetLastWin32Error();
                if (attempt < CommandMaxAttempts)
                    Thread.Sleep(attempt * 50);
            }
            throw new Win32Exception(lastError, FormatHidError("HidD_GetFeature", payload, lastError));
        }
        return null;
    }

    private static string FormatHidError(string api, byte[] payload, int win32Error)
    {
        string detail = new Win32Exception(win32Error).Message;
        return api + " " + BitConverter.ToString(payload) + " (Win32=" + win32Error + ": " + detail + ")";
    }

    private static IntPtr OpenValkyrieHid()
    {
        Guid guid;
        HidD_GetHidGuid(out guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return new IntPtr(-1);

        try
        {
            for (uint index = 0; ; index++)
            {
                DeviceInterfaceData data = new DeviceInterfaceData { cbSize = Marshal.SizeOf(typeof(DeviceInterfaceData)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) break;

                uint required;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringAuto(IntPtr.Add(detail, 4));
                    IntPtr handle = CreateFile(path, GenericRead | GenericWrite, ShareRead | ShareWrite,
                                               IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (handle == new IntPtr(-1)) continue;

                    HidAttributes attributes = new HidAttributes { Size = Marshal.SizeOf(typeof(HidAttributes)) };
                    if (HidD_GetAttributes(handle, ref attributes) &&
                        attributes.VendorID == 0x345F &&
                        attributes.ProductID == 0x9132 &&
                        GetFeatureLength(handle) >= 9)
                        return handle;
                    CloseHandle(handle);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return new IntPtr(-1);
    }
}
