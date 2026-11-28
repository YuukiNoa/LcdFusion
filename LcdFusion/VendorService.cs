using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace LcdFusion
{
    internal static class VendorService
    {
        public const string MythCoolExe = @"C:\Program Files (x86)\Myth.Cool\MythCool.exe";
        public const string MythCoolLauncher = @"C:\Program Files (x86)\Myth.Cool\MythCoolLauncher.exe";
        public const string TrccExe = @"C:\Program Files\TRCCCAP\TRCC.exe";

        private const long CacheTtlMs = 3000;
        private static readonly Stopwatch CacheClock = Stopwatch.StartNew();
        private static readonly object CacheSync = new object();
        private static long _mythCheckedMs = -CacheTtlMs;
        private static bool _mythRunning;
        private static long _trccCheckedMs = -CacheTtlMs;
        private static bool _trccRunning;

        public static bool IsMythCoolRunning(bool forceRefresh = false)
        {
            long now = CacheClock.ElapsedMilliseconds;
            lock (CacheSync)
            {
                if (!forceRefresh && now - _mythCheckedMs < CacheTtlMs)
                    return _mythRunning;
            }
            bool running = HasProcess("MythCool");
            lock (CacheSync)
            {
                _mythRunning = running;
                _mythCheckedMs = CacheClock.ElapsedMilliseconds;
            }
            return running;
        }

        public static bool IsTrccRunning(bool forceRefresh = false)
        {
            long now = CacheClock.ElapsedMilliseconds;
            lock (CacheSync)
            {
                if (!forceRefresh && now - _trccCheckedMs < CacheTtlMs)
                    return _trccRunning;
            }
            bool running = HasProcess("TRCC") || HasProcess("USBLCD") || HasProcess("USBLCDNEW");
            lock (CacheSync)
            {
                _trccRunning = running;
                _trccCheckedMs = CacheClock.ElapsedMilliseconds;
            }
            return running;
        }

        private static bool HasProcess(string name)
        {
            Process[] list = null;
            try
            {
                list = Process.GetProcessesByName(name);
                return list.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (list != null)
                {
                    for (int i = 0; i < list.Length; i++)
                    {
                        try { list[i].Dispose(); } catch { }
                    }
                }
            }
        }

        public static bool LaunchMythCool()
        {
            return Launch(File.Exists(MythCoolLauncher) ? MythCoolLauncher : MythCoolExe);
        }

        public static bool LaunchTrcc()
        {
            return Launch(TrccExe);
        }

        public static bool StopConflictingSoftware()
        {
            try
            {
                if (!IsMythCoolRunning(true) && !IsTrccRunning(true)) return true;
                var info = new ProcessStartInfo("taskkill.exe",
                    "/F /IM MythCool.exe /IM TRCC.exe /IM USBLCD.exe /IM USBLCDNEW.exe");
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;
                using (var process = Process.Start(info))
                {
                    if (process == null) return false;
                    process.WaitForExit();
                }
                System.Threading.Thread.Sleep(500);
                return !IsMythCoolRunning(true) && !IsTrccRunning(true);
            }
            catch { return false; }
        }

        public static string VersionOf(string path)
        {
            try
            {
                if (!File.Exists(path)) return "non installato";
                return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "installato";
            }
            catch { return "installato"; }
        }

        private static bool Launch(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }
    }
}
