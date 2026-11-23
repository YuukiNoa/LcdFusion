using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LcdFusion
{
    internal sealed class WallpaperPropertyOption
    {
        public string Label = "";
        public string Value = "";
    }

    internal sealed class WallpaperPropertyDef
    {
        public string Key = "";
        public string Label = "";
        public string Type = ""; // slider | bool | color | combo | textinput
        public string DefaultValue = "";
        public double Min;
        public double Max = 1.0;
        public double Step = 0.01;
        public int Order;
        public readonly List<WallpaperPropertyOption> Options = new List<WallpaperPropertyOption>();
    }

    internal sealed class WallpaperEntry
    {
        public string Directory = "";
        public string Id = "";
        public string Title = "";
        public string Type = "scene";
        public string File = "";
        public string PreviewPath = "";
        public readonly List<WallpaperPropertyDef> Properties = new List<WallpaperPropertyDef>();

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                if (!string.IsNullOrWhiteSpace(Id)) return Id;
                return Path.GetFileName(Directory) ?? Directory;
            }
        }
    }

    // Discovers Steam libraries, Wallpaper Engine shared assets, workshop items, and the external renderer binary.
    internal static class WallpaperCatalog
    {
        public const string WorkshopAppId = "431960";

        private static readonly Regex VdfPathRegex =
            new Regex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlTagRegex =
            new Regex("<[^>]+>", RegexOptions.Compiled);

        public static string DetectSteamPath()
        {
            string[] candidates =
            {
                ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe"),
                ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")
            };

            foreach (string raw in candidates)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string normalized = raw.Replace('/', '\\').Trim();
                if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    normalized = Path.GetDirectoryName(normalized);
                if (!string.IsNullOrEmpty(normalized) && Directory.Exists(normalized))
                    return normalized;
            }
            return "";
        }

        public static List<string> DetectSteamLibraryRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string steamPath = DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return roots;

            AddUniqueDir(roots, seen, steamPath);

            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                try
                {
                    string text = File.ReadAllText(vdfPath);
                    foreach (Match m in VdfPathRegex.Matches(text))
                    {
                        string lib = m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\').Trim();
                        AddUniqueDir(roots, seen, lib);
                    }
                }
                catch { }
            }

            return roots;
        }

        public static string DetectWallpaperEngineAssetsDir(string configuredDir = null)
        {
            if (!string.IsNullOrWhiteSpace(configuredDir) && Directory.Exists(configuredDir))
                return configuredDir.Trim();

            foreach (string root in DetectSteamLibraryRoots())
            {
                string assets = Path.Combine(root, "steamapps", "common", "wallpaper_engine", "assets");
                if (Directory.Exists(assets)) return assets;
            }

            return "";
        }

        public static string ResolveRendererExecutable(string configuredPath = null)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath.Trim();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            string[] candidates =
            {
                Path.Combine(baseDir, "renderer", "wpe-renderer.exe"),
                Path.Combine(baseDir, "wpe-renderer.exe"),
                // Dev-tree fallback to the built mock renderer
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "tools", "wpe-renderer-mock", "bin", "Debug", "wpe-renderer.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "tools", "wpe-renderer-mock", "bin", "Release", "wpe-renderer.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "tools", "wpe-renderer-mock", "bin", "Debug", "wpe-renderer.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "tools", "wpe-renderer-mock", "bin", "Release", "wpe-renderer.exe")),
            };

            foreach (string path in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch { }
            }

            return "";
        }

        public static List<WallpaperEntry> DiscoverWorkshopWallpapers()
        {
            var results = new List<WallpaperEntry>();
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string root in DetectSteamLibraryRoots())
            {
                string[] scanRoots =
                {
                    Path.Combine(root, "steamapps", "workshop", "content", WorkshopAppId),
                    Path.Combine(root, "steamapps", "common", "wallpaper_engine", "projects", "myprojects")
                };

                foreach (string scanRoot in scanRoots)
                {
                    if (!Directory.Exists(scanRoot)) continue;

                    string[] dirs;
                    try { dirs = Directory.GetDirectories(scanRoot); }
                    catch { continue; }

                    foreach (string dir in dirs)
                    {
                        if (!seenDirs.Add(dir)) continue;
                        WallpaperEntry entry = TryLoadWallpaperDir(dir);
                        if (entry != null) results.Add(entry);
                    }
                }
            }

            results.Sort((a, b) => string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase));
            return results;
        }

        public static WallpaperEntry TryLoadWallpaperDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

            string projectJson = Path.Combine(dir, "project.json");
            var entry = new WallpaperEntry
            {
                Directory = dir,
                Id = Path.GetFileName(dir.TrimEnd('\\', '/')) ?? "",
                Title = Path.GetFileName(dir.TrimEnd('\\', '/')) ?? "",
                Type = "scene"
            };

            if (File.Exists(projectJson))
            {
                try
                {
                    string json = File.ReadAllText(projectJson);
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                    var obj = serializer.DeserializeObject(json) as Dictionary<string, object>;
                    if (obj != null)
                    {
                        string title = ReadJsonString(obj, "title");
                        string type = ReadJsonString(obj, "type");
                        string file = ReadJsonString(obj, "file");
                        string preview = ReadJsonString(obj, "preview");

                        if (!string.IsNullOrWhiteSpace(title)) entry.Title = title.Trim();
                        if (!string.IsNullOrWhiteSpace(type)) entry.Type = type.Trim().ToLowerInvariant();
                        if (!string.IsNullOrWhiteSpace(file)) entry.File = file.Trim();
                        if (!string.IsNullOrWhiteSpace(preview))
                        {
                            string candidate = Path.Combine(dir, preview.Replace('/', '\\'));
                            if (File.Exists(candidate)) entry.PreviewPath = candidate;
                        }
                        ParseGeneralProperties(obj, entry.Properties);
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(entry.PreviewPath))
            {
                string[] previewFallbacks = { "preview.jpg", "preview.png", "preview.gif", "preview.jpeg" };
                foreach (string name in previewFallbacks)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        entry.PreviewPath = candidate;
                        break;
                    }
                }
            }

            return entry;
        }

        private static void ParseGeneralProperties(Dictionary<string, object> root, List<WallpaperPropertyDef> dest)
        {
            if (root == null || dest == null) return;
            object genObj;
            if (!TryGetIgnoreCase(root, "general", out genObj)) return;
            var gen = genObj as Dictionary<string, object>;
            if (gen == null) return;

            object propsObj;
            if (!TryGetIgnoreCase(gen, "properties", out propsObj)) return;
            var props = propsObj as Dictionary<string, object>;
            if (props == null) return;

            int fallbackOrder = 1000;
            foreach (KeyValuePair<string, object> kv in props)
            {
                var p = kv.Value as Dictionary<string, object>;
                if (p == null) continue;

                string type = ReadJsonString(p, "type").Trim().ToLowerInvariant();
                if (type != "slider" && type != "bool" && type != "color" && type != "combo" && type != "textinput")
                    continue;

                string rawText = ReadJsonString(p, "text");
                string cleanLabel = StripHtml(rawText);
                if (string.IsNullOrWhiteSpace(cleanLabel)) cleanLabel = kv.Key;

                var def = new WallpaperPropertyDef
                {
                    Key = kv.Key,
                    Label = cleanLabel,
                    Type = type,
                    DefaultValue = ReadJsonScalar(p, "value"),
                    Order = (int)Math.Round(ReadJsonDouble(p, "order", fallbackOrder++))
                };

                if (type == "slider")
                {
                    def.Min = ReadJsonDouble(p, "min", 0.0);
                    def.Max = ReadJsonDouble(p, "max", 1.0);
                    if (def.Max <= def.Min) def.Max = def.Min + 1.0;
                    double precision = ReadJsonDouble(p, "precision", 2.0);
                    double step = ReadJsonDouble(p, "step", 0.0);
                    def.Step = step > 0 ? step : Math.Pow(10.0, -Math.Max(0, Math.Min(4, (int)precision)));
                }
                else if (type == "combo")
                {
                    object optsObj;
                    if (TryGetIgnoreCase(p, "options", out optsObj))
                    {
                        var arr = optsObj as object[];
                        if (arr != null)
                        {
                            foreach (object item in arr)
                            {
                                var od = item as Dictionary<string, object>;
                                if (od == null) continue;
                                string ol = StripHtml(ReadJsonString(od, "label"));
                                string ov = ReadJsonScalar(od, "value");
                                if (string.IsNullOrWhiteSpace(ol)) ol = ov;
                                def.Options.Add(new WallpaperPropertyOption { Label = ol, Value = ov });
                            }
                        }
                    }
                }

                dest.Add(def);
            }

            dest.Sort((a, b) =>
            {
                int c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
        }

        public static Dictionary<string, object> ConvertPropsToTypedMap(
            Dictionary<string, string> stringProps,
            List<WallpaperPropertyDef> defs)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (stringProps == null || stringProps.Count == 0) return result;

            var defMap = new Dictionary<string, WallpaperPropertyDef>(StringComparer.OrdinalIgnoreCase);
            if (defs != null)
            {
                foreach (WallpaperPropertyDef d in defs)
                    if (!string.IsNullOrEmpty(d.Key)) defMap[d.Key] = d;
            }

            foreach (KeyValuePair<string, string> kv in stringProps)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                WallpaperPropertyDef def;
                if (defMap.TryGetValue(kv.Key, out def))
                {
                    if (def.Type == "bool")
                    {
                        bool b;
                        if (bool.TryParse(kv.Value, out b)) { result[kv.Key] = b; continue; }
                        if (kv.Value == "1") { result[kv.Key] = true; continue; }
                        if (kv.Value == "0") { result[kv.Key] = false; continue; }
                    }
                    else if (def.Type == "slider")
                    {
                        double d;
                        if (double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                        {
                            result[kv.Key] = d;
                            continue;
                        }
                    }
                }
                result[kv.Key] = kv.Value ?? "";
            }
            return result;
        }

        private static string StripHtml(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = HtmlTagRegex.Replace(raw, " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        private static bool TryGetIgnoreCase(Dictionary<string, object> dict, string key, out object val)
        {
            if (dict.TryGetValue(key, out val)) return true;
            foreach (KeyValuePair<string, object> kv in dict)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    val = kv.Value;
                    return true;
                }
            }
            val = null;
            return false;
        }

        private static string ReadJsonScalar(Dictionary<string, object> dict, string key)
        {
            object val;
            if (!TryGetIgnoreCase(dict, key, out val) || val == null) return "";
            if (val is bool) return ((bool)val) ? "true" : "false";
            if (val is IFormattable) return ((IFormattable)val).ToString(null, CultureInfo.InvariantCulture);
            return Convert.ToString(val) ?? "";
        }

        private static double ReadJsonDouble(Dictionary<string, object> dict, string key, double fallback)
        {
            object val;
            if (!TryGetIgnoreCase(dict, key, out val) || val == null) return fallback;
            try { return Convert.ToDouble(val, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string ReadJsonString(Dictionary<string, object> dict, string key)
        {
            object val;
            if (dict.TryGetValue(key, out val) && val != null) return Convert.ToString(val);
            foreach (KeyValuePair<string, object> kv in dict)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                    return Convert.ToString(kv.Value);
            }
            return "";
        }

        private static string ReadRegistryString(RegistryKey hive, string subKey, string valueName)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subKey))
                    return key == null ? "" : Convert.ToString(key.GetValue(valueName) ?? "");
            }
            catch { return ""; }
        }

        private static void AddUniqueDir(List<string> list, HashSet<string> seen, string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                string full = Path.GetFullPath(dir);
                if (Directory.Exists(full) && seen.Add(full)) list.Add(full);
            }
            catch { }
        }
    }
}
