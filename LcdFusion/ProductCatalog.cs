using System;

namespace LcdFusion
{
    internal enum ScreenId
    {
        Valkyrie = 0,
        BoWei = 1,
        Thermalright = 2
    }

    // Supported panel timings reported by the MacroSilicon / MSDisplay (345F:9132) firmware.
    internal sealed class ValkyriePanelSpec
    {
        public readonly string Key;
        public readonly int Width;
        public readonly int Height;
        public readonly byte Vic;

        public ValkyriePanelSpec(int width, int height, byte vic)
        {
            Width = width;
            Height = height;
            Vic = vic;
            Key = width + "x" + height;
        }

        public string DisplayName
        {
            get { return Width + " × " + Height + " (VIC 0x" + Vic.ToString("X2") + ")"; }
        }
    }

    // Identity + panel specs for a supported LCD. New products from the same brands plug
    // in here: add an entry. If a product shares an existing protocol (same framing/format,
    // different VID/PID or resolution) it can reuse that direct service with parameters; a
    // genuinely new protocol needs its own *DirectService implementation.
    internal sealed class LcdProduct
    {
        public string Brand;      // e.g. "Valkyrie / Myth.Cool", "Thermalright / TRCC"
        public string Model;      // human-readable model name
        public ushort Vid;
        public ushort Pid;
        public int Width;
        public int Height;
        public string Protocol;   // "valkyrie-hid-uyvy" | "bowei-winusb-rgb565" | "thermalright-winusb-jpeg"
        public bool Implemented;  // driven by an existing DirectService today
    }

    internal static class ProductCatalog
    {
        public static readonly ValkyriePanelSpec[] ValkyriePanels =
        {
            new ValkyriePanelSpec(360, 960, 0xA0),
            new ValkyriePanelSpec(320, 240, 0x94),
        };

        public static readonly LcdProduct[] Products =
        {
            new LcdProduct
            {
                Brand = "Valkyrie / Myth.Cool", Model = "Valkyrie / MSDisplay LCD (345F:9132)",
                Vid = 0x345F, Pid = 0x9132, Width = 320, Height = 240,
                Protocol = "valkyrie-hid-uyvy", Implemented = true
            },
            new LcdProduct
            {
                Brand = "Valkyrie / Myth.Cool (BoWei)", Model = "Vk-Copl-08 Waterblock LCD (374A:A005)",
                Vid = 0x374A, Pid = 0xA005, Width = 480, Height = 480,
                Protocol = "bowei-winusb-rgb565", Implemented = true
            },
            new LcdProduct
            {
                Brand = "Thermalright / TRCC", Model = "Thermalright AIO LCD (0416:5408)",
                Vid = 0x0416, Pid = 0x5408, Width = 1920, Height = 462,
                Protocol = "thermalright-winusb-jpeg", Implemented = true
            },
        };

        public static LcdProduct Find(ushort vid, ushort pid)
        {
            foreach (LcdProduct p in Products)
                if (p.Vid == vid && p.Pid == pid) return p;
            return null;
        }

        public static ValkyriePanelSpec FindValkyriePanel(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (ValkyriePanelSpec spec in ValkyriePanels)
                if (string.Equals(spec.Key, key, StringComparison.OrdinalIgnoreCase)) return spec;
            return null;
        }

        public static ValkyriePanelSpec FindValkyriePanel(int width, int height)
        {
            foreach (ValkyriePanelSpec spec in ValkyriePanels)
                if (spec.Width == width && spec.Height == height) return spec;
            return null;
        }

        public static ValkyriePanelSpec DefaultValkyriePanel { get { return ValkyriePanels[1]; } } // 320x240

        public static LcdProduct Valkyrie { get { return Products[0]; } }
        public static LcdProduct BoWei { get { return Products[1]; } }
        public static LcdProduct Thermalright { get { return Products[2]; } }
    }
}
