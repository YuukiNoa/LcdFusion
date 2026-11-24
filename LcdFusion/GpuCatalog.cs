using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LcdFusion
{
    internal sealed class GpuAdapterInfo
    {
        public string Key = "";         // Stable across reboots: "pci:VVVV:DDDD:ORDINAL"
        public string Name = "";
        public uint VendorId;
        public uint DeviceId;
        public int Ordinal;
        public bool HasLuid;
        public int LuidHigh;
        public uint LuidLow;
        public long DedicatedVramBytes;

        public string DisplayName
        {
            get
            {
                string label = string.IsNullOrWhiteSpace(Name) ? Key : Name.Trim();
                if (DedicatedVramBytes >= 1024L * 1024L * 1024L)
                {
                    double gb = DedicatedVramBytes / (1024.0 * 1024.0 * 1024.0);
                    return string.Format(CultureInfo.InvariantCulture, "{0} ({1:0.#} GB)", label, gb);
                }
                if (DedicatedVramBytes >= 64L * 1024L * 1024L)
                {
                    long mb = DedicatedVramBytes / (1024L * 1024L);
                    return string.Format(CultureInfo.InvariantCulture, "{0} ({1} MB)", label, mb);
                }
                return label;
            }
        }
    }

    // Enumerates hardware GPUs via Windows in-box DXGI (dxgi.dll) with zero external dependencies.
    internal static class GpuCatalog
    {
        public const string AutoKey = "auto";
        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        public static List<GpuAdapterInfo> EnumerateGpus()
        {
            var result = new List<GpuAdapterInfo>();
            IDXGIFactory1 factory = null;
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                object obj;
                int hr = CreateDXGIFactory1(ref iid, out obj);
                if (hr < 0 || obj == null) return result;
                factory = obj as IDXGIFactory1;
                if (factory == null) return result;

                var ordinals = new Dictionary<ulong, int>();
                var seenLuids = new HashSet<ulong>();

                for (uint i = 0; ; i++)
                {
                    IDXGIAdapter1 adapter;
                    int enumHr = factory.EnumAdapters1(i, out adapter);
                    if (enumHr == DXGI_ERROR_NOT_FOUND || enumHr < 0 || adapter == null) break;

                    try
                    {
                        DXGI_ADAPTER_DESC1 desc;
                        if (adapter.GetDesc1(out desc) >= 0)
                        {
                            bool isSoftware = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0
                                || (desc.VendorId == 0x1414 && desc.DeviceId == 0x008C);
                            if (isSoftware) continue;

                            ulong luidKey = (((ulong)(uint)desc.AdapterLuidHigh) << 32) | desc.AdapterLuidLow;
                            if (luidKey != 0 && !seenLuids.Add(luidKey)) continue;

                            ulong pciKey = (((ulong)desc.VendorId) << 32) | desc.DeviceId;
                            int ord;
                            if (!ordinals.TryGetValue(pciKey, out ord)) ord = 0;
                            ordinals[pciKey] = ord + 1;

                            string name = (desc.Description ?? "").Trim();
                            long vram = unchecked((long)desc.DedicatedVideoMemory.ToUInt64());
                            if (vram < 0) vram = 0;

                            result.Add(new GpuAdapterInfo
                            {
                                Key = string.Format(CultureInfo.InvariantCulture, "pci:{0:X4}:{1:X4}:{2}", desc.VendorId, desc.DeviceId, ord),
                                Name = name,
                                VendorId = desc.VendorId,
                                DeviceId = desc.DeviceId,
                                Ordinal = ord,
                                HasLuid = (desc.AdapterLuidHigh != 0 || desc.AdapterLuidLow != 0),
                                LuidHigh = desc.AdapterLuidHigh,
                                LuidLow = desc.AdapterLuidLow,
                                DedicatedVramBytes = vram
                            });
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            catch { }
            finally
            {
                if (factory != null)
                {
                    try { Marshal.ReleaseComObject(factory); } catch { }
                }
            }

            return result;
        }

        public static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return AutoKey;
            string trimmed = key.Trim();
            if (string.Equals(trimmed, AutoKey, StringComparison.OrdinalIgnoreCase)) return AutoKey;
            return trimmed;
        }

        public static GpuAdapterInfo Resolve(string savedKey, List<GpuAdapterInfo> available = null)
        {
            string norm = NormalizeKey(savedKey);
            if (string.Equals(norm, AutoKey, StringComparison.OrdinalIgnoreCase)) return null;

            List<GpuAdapterInfo> list = available ?? EnumerateGpus();
            if (list == null || list.Count == 0) return null;

            foreach (GpuAdapterInfo g in list)
            {
                if (string.Equals(g.Key, norm, StringComparison.OrdinalIgnoreCase))
                    return g;
            }

            // Fallback: match same vendorId:deviceId ignoring ordinal
            if (norm.StartsWith("pci:", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = norm.Split(':');
                uint vid, did;
                if (parts.Length >= 3 &&
                    uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
                    uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out did))
                {
                    foreach (GpuAdapterInfo g in list)
                    {
                        if (g.VendorId == vid && g.DeviceId == did)
                            return g;
                    }
                }
            }

            // Fallback: match by adapter name
            foreach (GpuAdapterInfo g in list)
            {
                if (!string.IsNullOrEmpty(g.Name) &&
                    string.Equals(g.Name, norm, StringComparison.OrdinalIgnoreCase))
                    return g;
            }

            return null;
        }

        public static Dictionary<string, object> ToIpcPayload(GpuAdapterInfo gpu)
        {
            if (gpu == null) return null;
            return new Dictionary<string, object>
            {
                { "key", gpu.Key ?? "" },
                { "name", gpu.Name ?? "" },
                { "vendorId", gpu.VendorId },
                { "deviceId", gpu.DeviceId },
                { "ordinal", gpu.Ordinal },
                { "hasLuid", gpu.HasLuid },
                { "luidHigh", gpu.LuidHigh },
                { "luidLow", gpu.LuidLow }
            };
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppFactory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public uint AdapterLuidLow;
            public int AdapterLuidHigh;
            public uint Flags;
        }

        [ComImport]
        [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            // IDXGIFactory
            [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
            // IDXGIFactory1
            [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 ppAdapter);
            [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsCurrent();
        }

        [ComImport]
        [Guid("29038f61-3839-4626-91fd-086879011a05")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            // IDXGIAdapter
            [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
            [PreserveSig] int GetDesc(IntPtr pDesc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
            // IDXGIAdapter1
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
        }
    }
}
