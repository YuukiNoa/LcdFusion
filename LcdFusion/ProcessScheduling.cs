using System;
using System.Runtime.InteropServices;

namespace LcdFusion
{
    // Opts LcdFusion out of Windows 11 EcoQoS / Timer Resolution Throttling and requests 1 ms timer
    // granularity while actively streaming to an external USB LCD, preventing background stuttering.
    internal static class ProcessScheduling
    {
        private const int ProcessPowerThrottling = 4;
        private const uint ProcessPowerThrottlingCurrentVersion = 1;
        private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
        private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

        private static readonly object Sync = new object();
        private static bool _highPerfActive;
        private static bool _timerPeriodActive;

        public static void SetHighPerformanceMode(bool enabled)
        {
            lock (Sync)
            {
                if (_highPerfActive == enabled) return;
                _highPerfActive = enabled;

                if (enabled)
                {
                    ApplyPowerThrottlingOptOut(true);
                    if (!_timerPeriodActive)
                    {
                        try
                        {
                            if (timeBeginPeriod(1) == 0)
                                _timerPeriodActive = true;
                        }
                        catch { }
                    }
                }
                else
                {
                    if (_timerPeriodActive)
                    {
                        try { timeEndPeriod(1); } catch { }
                        _timerPeriodActive = false;
                    }
                    ApplyPowerThrottlingOptOut(false);
                }
            }
        }

        private static void ApplyPowerThrottlingOptOut(bool disableThrottling)
        {
            try
            {
                IntPtr self = GetCurrentProcess();
                int size = Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE));

                // Apply each flag separately so Windows 10 (which rejects IGNORE_TIMER_RESOLUTION)
                // still honors EXECUTION_SPEED opt-out.
                // ControlMask = flag, StateMask = 0 -> explicitly disable throttling.
                // ControlMask = 0,    StateMask = 0 -> restore OS default management.
                var execState = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = ProcessPowerThrottlingCurrentVersion,
                    ControlMask = disableThrottling ? ProcessPowerThrottlingExecutionSpeed : 0u,
                    StateMask = 0u
                };
                SetProcessInformation(self, ProcessPowerThrottling, ref execState, (uint)size);

                var timerState = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = ProcessPowerThrottlingCurrentVersion,
                    ControlMask = disableThrottling ? ProcessPowerThrottlingIgnoreTimerResolution : 0u,
                    StateMask = 0u
                };
                SetProcessInformation(self, ProcessPowerThrottling, ref timerState, (uint)size);
            }
            catch
            {
                // Older Windows versions without SetProcessInformation(ProcessPowerThrottling).
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess,
            int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation,
            uint processInformationSize);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);
    }
}
