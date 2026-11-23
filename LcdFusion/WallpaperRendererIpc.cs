using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LcdFusion
{
    internal static class WallpaperIpcProtocol
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

        public const uint StateInit = 0;
        public const uint StateRunning = 1;
        public const uint StatePaused = 2;
        public const uint StateError = 3;

        public static long CalculateMappingSize(int width, int height)
        {
            long slotBytes = (long)width * 4L * (long)height;
            return HeaderSize + slotBytes * 2L;
        }
    }

    // Ensures every spawned renderer child process is automatically terminated if LcdFusion exits or crashes.
    internal sealed class ChildProcessJob : IDisposable
    {
        private static readonly Lazy<ChildProcessJob> _shared = new Lazy<ChildProcessJob>(() => new ChildProcessJob());
        public static ChildProcessJob Shared { get { return _shared.Value; } }

        private IntPtr _handle;

        private ChildProcessJob()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Attach(Process process)
        {
            if (_handle == IntPtr.Zero || process == null) return;
            try { AssignProcessToJobObject(_handle, process.Handle); } catch { }
        }

        public void Attach(IntPtr processHandle)
        {
            if (_handle == IntPtr.Zero || processHandle == IntPtr.Zero) return;
            try { AssignProcessToJobObject(_handle, processHandle); } catch { }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    // Security helpers for cross-integrity IPC (High host -> Medium renderer) and
    // authentic Shell-token de-elevation via explorer.exe when LcdFusion runs elevated under UAC.
    internal static class RendererSecurity
    {
        public const uint IntegrityLowRid = 0x1000;
        public const uint IntegrityMediumRid = 0x2000;
        public const uint IntegrityHighRid = 0x3000;

        private const int TokenIntegrityLevel = 25;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;

        private const int SE_KERNEL_OBJECT = 6;
        private const uint LABEL_SECURITY_INFORMATION = 0x00000010;
        private const uint SDDL_REVISION_1 = 1;

        public static uint GetCurrentProcessIntegrityRid()
        {
            IntPtr hToken;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out hToken))
                return IntegrityMediumRid;
            try
            {
                return GetTokenIntegrityRid(hToken);
            }
            finally
            {
                CloseHandle(hToken);
            }
        }

        public static uint GetTokenIntegrityRid(IntPtr hToken)
        {
            uint len = 0;
            GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out len);
            if (len == 0) return IntegrityMediumRid;

            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetTokenInformation(hToken, TokenIntegrityLevel, buf, len, out len))
                    return IntegrityMediumRid;
                var tml = (TOKEN_MANDATORY_LABEL)Marshal.PtrToStructure(buf, typeof(TOKEN_MANDATORY_LABEL));
                if (tml.Label.Sid == IntPtr.Zero) return IntegrityMediumRid;
                IntPtr countPtr = GetSidSubAuthorityCount(tml.Label.Sid);
                if (countPtr == IntPtr.Zero) return IntegrityMediumRid;
                byte count = Marshal.ReadByte(countPtr);
                if (count == 0) return IntegrityMediumRid;
                IntPtr ridPtr = GetSidSubAuthority(tml.Label.Sid, (uint)(count - 1));
                return ridPtr == IntPtr.Zero ? IntegrityMediumRid : (uint)Marshal.ReadInt32(ridPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // Scopes the named section DACL to the current user (ReadWrite) + Administrators/SYSTEM (FullControl).
        public static MemoryMappedFileSecurity CreateSharedMemorySecurity()
        {
            try
            {
                var sec = new MemoryMappedFileSecurity();
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    if (id != null && id.User != null)
                        sec.AddAccessRule(new AccessRule<MemoryMappedFileRights>(id.User, MemoryMappedFileRights.ReadWrite, AccessControlType.Allow));
                }
                sec.AddAccessRule(new AccessRule<MemoryMappedFileRights>(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    MemoryMappedFileRights.FullControl,
                    AccessControlType.Allow));
                sec.AddAccessRule(new AccessRule<MemoryMappedFileRights>(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    MemoryMappedFileRights.FullControl,
                    AccessControlType.Allow));
                return sec;
            }
            catch
            {
                return null;
            }
        }

        // Scopes IPC named pipes for cross-integrity (High -> Medium) access when running elevated.
        // When not elevated, returns null to preserve default/sandbox inherited ACEs.
        public static PipeSecurity CreatePipeSecurity()
        {
            if (GetCurrentProcessIntegrityRid() < IntegrityHighRid)
                return null;
            try
            {
                var sec = new PipeSecurity();
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    if (id != null && id.User != null)
                        sec.AddAccessRule(new PipeAccessRule(
                            id.User,
                            PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
                            AccessControlType.Allow));
                }
                sec.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
                sec.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
                return sec;
            }
            catch
            {
                return null;
            }
        }

        // Stamps Medium Mandatory Level (NO_WRITE_UP) onto a High-IL host's kernel object (section or named pipe)
        // so a de-elevated Medium-IL renderer child process can open and communicate through it.
        public static bool ApplyMediumMandatoryLabelIfElevated(SafeHandle handle)
        {
            if (handle == null || handle.IsInvalid || handle.IsClosed) return false;
            if (GetCurrentProcessIntegrityRid() <= IntegrityMediumRid) return true;

            IntPtr pSD;
            uint sdSize;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NW;;;ME)", SDDL_REVISION_1, out pSD, out sdSize))
                return false;

            bool refAdded = false;
            try
            {
                bool present, defaulted;
                IntPtr pSacl;
                if (!GetSecurityDescriptorSacl(pSD, out present, out pSacl, out defaulted) || !present)
                    return false;

                handle.DangerousAddRef(ref refAdded);
                uint err = SetSecurityInfo(
                    handle.DangerousGetHandle(),
                    SE_KERNEL_OBJECT,
                    LABEL_SECURITY_INFORMATION,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    pSacl);
                return err == 0;
            }
            finally
            {
                if (refAdded) handle.DangerousRelease();
                LocalFree(pSD);
            }
        }

        // Duplicates the interactive shell (explorer.exe) Medium-Integrity primary token so the child
        // process retains the authentic interactive Logon SID (access to WinSta0\Default without 0xC0000142).
        public static bool TryDuplicateShellMediumToken(out IntPtr primaryToken)
        {
            primaryToken = IntPtr.Zero;
            IntPtr hwnd = GetShellWindow();
            if (hwnd == IntPtr.Zero) return false;

            uint shellPid = 0;
            GetWindowThreadProcessId(hwnd, out shellPid);
            if (shellPid == 0) return false;

            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
            if (hProc == IntPtr.Zero)
                hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, shellPid);
            if (hProc == IntPtr.Zero) return false;

            IntPtr hShellToken = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY, out hShellToken))
                    return false;

                // Only de-elevate when explorer.exe is genuinely running at Medium Integrity.
                if (GetTokenIntegrityRid(hShellToken) != IntegrityMediumRid)
                    return false;

                uint desired = TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
                if (!DuplicateTokenEx(hShellToken, desired, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
                    return false;

                return primaryToken != IntPtr.Zero;
            }
            finally
            {
                if (hShellToken != IntPtr.Zero) CloseHandle(hShellToken);
                CloseHandle(hProc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_MANDATORY_LABEL
        {
            public SID_AND_ATTRIBUTES Label;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int ImpersonationLevel,
            int TokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, out uint SecurityDescriptorSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorSacl(IntPtr pSecurityDescriptor, out bool lpbSaclPresent, out IntPtr pSacl, out bool lpbSaclDefaulted);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint SetSecurityInfo(IntPtr handle, int ObjectType, uint SecurityInfo, IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);
    }

    // Unified child process handle wrapper supporting both:
    //  1) De-elevated Medium-Integrity launch via explorer.exe token + CreateProcessWithTokenW + named pipes (--ipc-pipe)
    //  2) Standard Process.Start (--ipc over stdin/stdout)
    internal sealed class RendererChildProcess : IDisposable
    {
        private static int _pipeCounter;

        private Process _managedProcess;
        private IntPtr _nativeProcessHandle;
        private NamedPipeServerStream _pipeIn;
        private NamedPipeServerStream _pipeOut;
        private int _pid;
        private bool _disposed;

        public StreamWriter Stdin { get; private set; }
        public StreamReader Stdout { get; private set; }
        public StreamReader Stderr { get; private set; }
        public int ProcessId { get { return _pid; } }
        public bool IsDeElevated { get; private set; }
        public string LaunchMode { get; private set; }

        private RendererChildProcess() { }

        public static RendererChildProcess Start(string exePath, string arguments, string workingDir)
        {
            uint prevErrorMode = 0;
            bool changedErrorMode = SetThreadErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX, out prevErrorMode);
            try
            {
                if (RendererSecurity.GetCurrentProcessIntegrityRid() >= RendererSecurity.IntegrityHighRid)
                {
                    RendererChildProcess deElevated;
                    if (TryStartDeElevatedWithPipes(exePath, workingDir, out deElevated))
                        return deElevated;

                    RendererChildProcess fallback = StartStandard(exePath, arguments, workingDir);
                    fallback.LaunchMode = "standard-fallback";
                    return fallback;
                }

                return StartStandard(exePath, arguments, workingDir);
            }
            finally
            {
                if (changedErrorMode) SetThreadErrorMode(prevErrorMode, out prevErrorMode);
            }
        }

        // Helper for testing the --ipc-pipe named-pipe transport directly without requiring UAC elevation.
        internal static RendererChildProcess StartStandardWithNamedPipes(string exePath, string workingDir)
        {
            NamedPipeServerStream pipeIn = null;
            NamedPipeServerStream pipeOut = null;
            Process proc = null;
            try
            {
                string pipeBase = CreateIpcPipePair(out pipeIn, out pipeOut);
                IAsyncResult arIn = pipeIn.BeginWaitForConnection(null, null);
                IAsyncResult arOut = pipeOut.BeginWaitForConnection(null, null);

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--ipc-pipe " + pipeBase,
                    WorkingDirectory = workingDir ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8
                };

                proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("Failed to start wallpaper renderer process.");
                ChildProcessJob.Shared.Attach(proc);

                if (!WaitForPipesOrExit(arIn, arOut, () => proc.HasExited, 3000))
                    throw new TimeoutException("Renderer did not connect to IPC named pipes.");

                pipeIn.EndWaitForConnection(arIn);
                pipeOut.EndWaitForConnection(arOut);

                var child = new RendererChildProcess
                {
                    _managedProcess = proc,
                    _pipeIn = pipeIn,
                    _pipeOut = pipeOut,
                    _pid = proc.Id,
                    IsDeElevated = false,
                    LaunchMode = "standard-pipe",
                    Stdin = new StreamWriter(pipeIn, new UTF8Encoding(false)) { AutoFlush = true },
                    Stdout = new StreamReader(pipeOut, Encoding.UTF8, false),
                    Stderr = proc.StandardError
                };
                proc = null; pipeIn = null; pipeOut = null;
                return child;
            }
            finally
            {
                if (proc != null)
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                }
                if (pipeIn != null) { try { pipeIn.Dispose(); } catch { } }
                if (pipeOut != null) { try { pipeOut.Dispose(); } catch { } }
            }
        }

        private static RendererChildProcess StartStandard(string exePath, string arguments, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDir ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Process proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Failed to start wallpaper renderer process.");

            ChildProcessJob.Shared.Attach(proc);

            var child = new RendererChildProcess
            {
                _managedProcess = proc,
                _pid = proc.Id,
                IsDeElevated = false,
                LaunchMode = "standard",
                Stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true },
                Stdout = proc.StandardOutput,
                Stderr = proc.StandardError
            };
            return child;
        }

        private static string CreateIpcPipePair(out NamedPipeServerStream pipeIn, out NamedPipeServerStream pipeOut)
        {
            int seq = Interlocked.Increment(ref _pipeCounter);
            int pid = Process.GetCurrentProcess().Id;
            string pipeBase = string.Format("LcdFusion_WpeCtl_{0}_{1}", pid, seq);
            PipeSecurity sec = RendererSecurity.CreatePipeSecurity();

            pipeIn = sec != null
                ? new NamedPipeServerStream(pipeBase + "_in", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, sec)
                : new NamedPipeServerStream(pipeBase + "_in", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
            RendererSecurity.ApplyMediumMandatoryLabelIfElevated(pipeIn.SafePipeHandle);

            try
            {
                pipeOut = sec != null
                    ? new NamedPipeServerStream(pipeBase + "_out", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, sec)
                    : new NamedPipeServerStream(pipeBase + "_out", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
                RendererSecurity.ApplyMediumMandatoryLabelIfElevated(pipeOut.SafePipeHandle);
            }
            catch
            {
                pipeIn.Dispose();
                pipeIn = null;
                throw;
            }

            return pipeBase;
        }

        private static bool WaitForPipesOrExit(IAsyncResult arIn, IAsyncResult arOut, Func<bool> hasExited, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (arIn.IsCompleted && arOut.IsCompleted) return true;
                if (hasExited()) return false;
                Thread.Sleep(20);
            }
            return arIn.IsCompleted && arOut.IsCompleted;
        }

        private static bool TryStartDeElevatedWithPipes(string exePath, string workingDir, out RendererChildProcess child)
        {
            child = null;
            IntPtr token;
            if (!RendererSecurity.TryDuplicateShellMediumToken(out token))
                return false;

            IntPtr envBlock = IntPtr.Zero;
            NamedPipeServerStream pipeIn = null;
            NamedPipeServerStream pipeOut = null;
            var pi = new PROCESS_INFORMATION();

            try
            {
                string pipeBase = CreateIpcPipePair(out pipeIn, out pipeOut);
                IAsyncResult arIn = pipeIn.BeginWaitForConnection(null, null);
                IAsyncResult arOut = pipeOut.BeginWaitForConnection(null, null);

                uint flags = CREATE_NO_WINDOW | CREATE_SUSPENDED;
                if (CreateEnvironmentBlock(out envBlock, token, false) && envBlock != IntPtr.Zero)
                    flags |= CREATE_UNICODE_ENVIRONMENT;
                else
                    envBlock = IntPtr.Zero;

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.lpDesktop = @"winsta0\default";

                var cmdLine = new StringBuilder(string.Format("\"{0}\" --ipc-pipe {1}", exePath, pipeBase));
                bool ok = CreateProcessWithTokenW(
                    token,
                    0,
                    exePath,
                    cmdLine,
                    flags,
                    envBlock,
                    string.IsNullOrEmpty(workingDir) ? null : workingDir,
                    ref si,
                    out pi);

                if (!ok || pi.hProcess == IntPtr.Zero)
                    return false;

                // Attach suspended child to the kill-on-close Job Object before executing user code.
                ChildProcessJob.Shared.Attach(pi.hProcess);
                ResumeThread(pi.hThread);
                CloseHandle(pi.hThread);
                pi.hThread = IntPtr.Zero;

                if (!WaitForPipesOrExit(arIn, arOut, () => WaitForSingleObject(pi.hProcess, 0) == WAIT_OBJECT_0, 3000))
                {
                    try { TerminateProcess(pi.hProcess, 1); } catch { }
                    CloseHandle(pi.hProcess);
                    pi.hProcess = IntPtr.Zero;
                    return false;
                }

                pipeIn.EndWaitForConnection(arIn);
                pipeOut.EndWaitForConnection(arOut);

                child = new RendererChildProcess
                {
                    _nativeProcessHandle = pi.hProcess,
                    _pipeIn = pipeIn,
                    _pipeOut = pipeOut,
                    _pid = (int)pi.dwProcessId,
                    IsDeElevated = true,
                    LaunchMode = "de-elevated-pipe",
                    Stdin = new StreamWriter(pipeIn, new UTF8Encoding(false)) { AutoFlush = true },
                    Stdout = new StreamReader(pipeOut, Encoding.UTF8, false),
                    Stderr = null
                };
                pi.hProcess = IntPtr.Zero;
                pipeIn = null;
                pipeOut = null;
                return true;
            }
            catch
            {
                if (pi.hProcess != IntPtr.Zero)
                {
                    try { TerminateProcess(pi.hProcess, 1); } catch { }
                    CloseHandle(pi.hProcess);
                }
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                return false;
            }
            finally
            {
                if (pipeIn != null) { try { pipeIn.Dispose(); } catch { } }
                if (pipeOut != null) { try { pipeOut.Dispose(); } catch { } }
                if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        public bool HasExited
        {
            get
            {
                if (_disposed) return true;
                if (_managedProcess != null)
                {
                    try { return _managedProcess.HasExited; }
                    catch { return true; }
                }
                if (_nativeProcessHandle != IntPtr.Zero)
                    return WaitForSingleObject(_nativeProcessHandle, 0) == WAIT_OBJECT_0;
                return true;
            }
        }

        public int ExitCode
        {
            get
            {
                if (_managedProcess != null)
                {
                    try { return _managedProcess.ExitCode; }
                    catch { return -1; }
                }
                if (_nativeProcessHandle != IntPtr.Zero)
                {
                    uint code;
                    if (GetExitCodeProcess(_nativeProcessHandle, out code))
                        return unchecked((int)code);
                }
                return -1;
            }
        }

        public bool WaitForExit(int timeoutMs)
        {
            if (_managedProcess != null)
            {
                try { return _managedProcess.WaitForExit(timeoutMs); }
                catch { return true; }
            }
            if (_nativeProcessHandle != IntPtr.Zero)
                return WaitForSingleObject(_nativeProcessHandle, (uint)Math.Max(0, timeoutMs)) == WAIT_OBJECT_0;
            return true;
        }

        public void Kill()
        {
            if (_managedProcess != null)
            {
                try { if (!_managedProcess.HasExited) _managedProcess.Kill(); } catch { }
                return;
            }
            if (_nativeProcessHandle != IntPtr.Zero)
            {
                try { TerminateProcess(_nativeProcessHandle, 1); } catch { }
            }
        }

        public void CloseStdin()
        {
            try { if (Stdin != null) Stdin.Dispose(); } catch { }
            Stdin = null;
            try { if (_pipeIn != null) _pipeIn.Dispose(); } catch { }
            _pipeIn = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CloseStdin();
            try { if (Stdout != null) Stdout.Dispose(); } catch { }
            Stdout = null;
            try { if (_pipeOut != null) _pipeOut.Dispose(); } catch { }
            _pipeOut = null;
            try { if (Stderr != null) Stderr.Dispose(); } catch { }
            Stderr = null;

            if (_managedProcess != null)
            {
                try { _managedProcess.Dispose(); } catch { }
                _managedProcess = null;
            }
            if (_nativeProcessHandle != IntPtr.Zero)
            {
                CloseHandle(_nativeProcessHandle);
                _nativeProcessHandle = IntPtr.Zero;
            }
        }

        private const uint SEM_FAILCRITICALERRORS = 0x0001;
        private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
        private const uint CREATE_SUSPENDED = 0x00000004;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint WAIT_OBJECT_0 = 0x00000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadErrorMode(uint dwNewMode, out uint lpOldMode);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string lpApplicationName,
            StringBuilder lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    // Manages one out-of-process offscreen Wallpaper Engine renderer and reads its BGRA frames via shared memory.
    internal sealed class WallpaperFrameSource : IBackgroundSource
    {
        private static int _shmCounter;

        private const int MaxConsecutiveRestarts = 3;
        private static readonly int[] RestartBackoffMs = { 1000, 3000, 8000 };
        private const int HealthyResetMs = 15000;

        private readonly object _sync = new object();
        private readonly string _rendererExe;
        private readonly string _wallpaperDir;
        private readonly string _assetsDir;
        private readonly ScreenId _screen;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Stopwatch _healthyWatch = new Stopwatch();
        private readonly Dictionary<string, object> _properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, object> _lastSensors;

        private RendererChildProcess _child;
        private int _generation;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private string _shmName = "";
        private int _width;
        private int _height;
        private int _fps;
        private FitMode _fit = FitMode.Fill;
        private bool _paused;
        private volatile bool _disposed;

        private ulong _lastSeq;
        private bool _hasFrame;
        private byte[] _stagingBytes;
        private Bitmap _frameBitmap;

        private volatile string _lastError;
        private volatile string _lastErrorCode;
        private volatile string _backendName = "";
        private volatile bool _isDeElevated;
        private volatile string _launchMode = "";

        private bool _readyReceived;
        private bool _fatalProtocolError;
        private bool _pendingRestart;
        private bool _restartExhausted;
        private long _nextRestartMs;
        private int _consecutiveCrashes;
        private int _totalRestarts;

        public WallpaperFrameSource(ScreenId screen, string rendererExe, string wallpaperDir, string assetsDir, int width, int height, int fps, Dictionary<string, object> initialProps = null)
        {
            _screen = screen;
            _rendererExe = rendererExe ?? "";
            _wallpaperDir = wallpaperDir ?? "";
            _assetsDir = assetsDir ?? "";
            _width = ClampDim(width);
            _height = ClampDim(height);
            _fps = ClampFps(fps);

            if (initialProps != null)
            {
                foreach (KeyValuePair<string, object> kv in initialProps)
                    if (!string.IsNullOrEmpty(kv.Key)) _properties[kv.Key] = kv.Value;
            }

            if (string.IsNullOrWhiteSpace(_rendererExe) || !File.Exists(_rendererExe))
                throw new FileNotFoundException("External wallpaper renderer executable not found.", _rendererExe);

            if (string.IsNullOrWhiteSpace(_wallpaperDir) || !Directory.Exists(_wallpaperDir))
                throw new DirectoryNotFoundException("Wallpaper directory not found: " + _wallpaperDir);

            lock (_sync)
            {
                StartChildAndLoadLocked();
            }
        }

        public bool IsAnimated { get { return true; } }
        public string StatusError { get { return _lastError; } }
        public string StatusErrorCode { get { return _lastErrorCode; } }
        public string BackendName { get { return _backendName; } }
        public ulong LastFrameSequence { get { return _lastSeq; } }
        public int RenderWidth { get { return _width; } }
        public int RenderHeight { get { return _height; } }
        public bool IsDeElevated { get { return _isDeElevated; } }
        public string LaunchMode { get { return _launchMode; } }
        public int RestartCount { get { lock (_sync) { return _totalRestarts; } } }
        public bool IsRestartExhausted { get { lock (_sync) { return _restartExhausted; } } }
        public int ProcessId
        {
            get
            {
                lock (_sync)
                {
                    return (_child != null && !_child.HasExited) ? _child.ProcessId : 0;
                }
            }
        }

        public bool HasPendingFrame
        {
            get
            {
                lock (_sync)
                {
                    if (_disposed || _view == null) return false;
                    Thread.MemoryBarrier();
                    ulong seq = _view.ReadUInt64(WallpaperIpcProtocol.OffsetFrameSeq);
                    return seq > 0 && seq != _lastSeq;
                }
            }
        }

        public void Advance(int deltaMs)
        {
            lock (_sync)
            {
                if (_disposed) return;

                if (_child != null && _child.HasExited)
                {
                    HandleUnexpectedExitLocked();
                }

                // If the restarted renderer stays healthy and delivers frames long enough, reset crash streak.
                if (_child != null && _readyReceived && _consecutiveCrashes > 0 &&
                    _healthyWatch.IsRunning && _healthyWatch.ElapsedMilliseconds >= HealthyResetMs)
                {
                    _consecutiveCrashes = 0;
                }

                if (_pendingRestart && !_paused && !_fatalProtocolError && _clock.ElapsedMilliseconds >= _nextRestartMs)
                {
                    _pendingRestart = false;
                    try
                    {
                        StartChildAndLoadLocked();
                        _totalRestarts++;
                    }
                    catch (Exception ex)
                    {
                        ScheduleOrExhaustRestartLocked(-1, ex.Message);
                    }
                }
            }
        }

        public void EnsureSizeAndFps(int width, int height, int fps, FitMode fit = FitMode.Fill)
        {
            int w = ClampDim(width);
            int h = ClampDim(height);
            int f = ClampFps(fps);

            lock (_sync)
            {
                if (_disposed) return;
                if (w == _width && h == _height && f == _fps && fit == _fit) return;

                bool sizeChanged = (w != _width || h != _height);
                _width = w;
                _height = h;
                _fps = f;
                _fit = fit;

                if (_child == null || _child.HasExited)
                {
                    // Child is restarting or stopped; new dimensions will be applied when it reloads.
                    return;
                }

                MemoryMappedFile oldMmf = null;
                MemoryMappedViewAccessor oldView = null;
                if (sizeChanged)
                {
                    oldMmf = _mmf;
                    oldView = _view;
                    CreateSharedMemoryLocked(w, h);
                    _lastSeq = 0;
                }

                SendCommandLocked(new Dictionary<string, object>
                {
                    { "cmd", "resize" },
                    { "width", w },
                    { "height", h },
                    { "fps", f },
                    { "fit", FitModeToWire(fit) },
                    { "shm", _shmName }
                });

                if (sizeChanged)
                    DisposeMapping(oldMmf, oldView);
            }
        }

        private static string FitModeToWire(FitMode fit)
        {
            if (fit == FitMode.Fit) return "fit";
            if (fit == FitMode.Stretch) return "stretch";
            return "fill";
        }

        public void SetPaused(bool paused)
        {
            lock (_sync)
            {
                if (_disposed || _paused == paused) return;
                _paused = paused;
                if (_child != null && !_child.HasExited)
                {
                    SendCommandLocked(new Dictionary<string, object>
                    {
                        { "cmd", "pause" },
                        { "value", paused }
                    });
                }
            }
        }

        public void SetProperties(Dictionary<string, object> values)
        {
            if (values == null || values.Count == 0) return;
            lock (_sync)
            {
                if (_disposed) return;
                var copy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, object> kv in values)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    _properties[kv.Key] = kv.Value;
                    copy[kv.Key] = kv.Value;
                }
                if (copy.Count > 0 && _child != null && !_child.HasExited)
                {
                    SendCommandLocked(new Dictionary<string, object>
                    {
                        { "cmd", "props" },
                        { "values", copy }
                    });
                }
            }
        }

        public void SetSensors(Dictionary<string, object> data)
        {
            if (data == null || data.Count == 0) return;
            lock (_sync)
            {
                if (_disposed) return;
                _lastSensors = data;
                if (_child != null && !_child.HasExited)
                {
                    SendCommandLocked(new Dictionary<string, object>
                    {
                        { "cmd", "sensors" },
                        { "data", data }
                    });
                }
            }
        }

        public Image AcquireFrame()
        {
            lock (_sync)
            {
                if (_disposed || _view == null) return _hasFrame ? _frameBitmap : null;

                Thread.MemoryBarrier();
                uint magic = _view.ReadUInt32(WallpaperIpcProtocol.OffsetMagic);
                if (magic != WallpaperIpcProtocol.Magic) return _hasFrame ? _frameBitmap : null;

                ulong seq = _view.ReadUInt64(WallpaperIpcProtocol.OffsetFrameSeq);
                if (seq == 0 || seq == _lastSeq) return _hasFrame ? _frameBitmap : null;

                int w = (int)_view.ReadUInt32(WallpaperIpcProtocol.OffsetWidth);
                int h = (int)_view.ReadUInt32(WallpaperIpcProtocol.OffsetHeight);
                int stride = (int)_view.ReadUInt32(WallpaperIpcProtocol.OffsetStride);
                if (w != _width || h != _height || stride != w * 4) return _hasFrame ? _frameBitmap : null;

                int slotBytes = stride * h;
                if (_stagingBytes == null || _stagingBytes.Length != slotBytes)
                    _stagingBytes = new byte[slotBytes];

                // Double-buffered lock-free read with sequence check to prevent mid-copy tearing.
                ulong copiedSeq = seq;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    uint slot = _view.ReadUInt32(WallpaperIpcProtocol.OffsetActiveSlot) & 1u;
                    long offset = WallpaperIpcProtocol.HeaderSize + (long)slot * slotBytes;
                    _view.ReadArray(offset, _stagingBytes, 0, slotBytes);
                    Thread.MemoryBarrier();
                    ulong seqAfter = _view.ReadUInt64(WallpaperIpcProtocol.OffsetFrameSeq);
                    copiedSeq = seqAfter;
                    if (seqAfter == seq) break;
                    seq = seqAfter;
                }
                seq = copiedSeq;

                if (_frameBitmap == null || _frameBitmap.Width != w || _frameBitmap.Height != h)
                {
                    if (_frameBitmap != null) { try { _frameBitmap.Dispose(); } catch { } }
                    _frameBitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                }

                Rectangle rect = new Rectangle(0, 0, w, h);
                BitmapData data = _frameBitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    if (data.Stride == stride)
                    {
                        Marshal.Copy(_stagingBytes, 0, data.Scan0, slotBytes);
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                            Marshal.Copy(_stagingBytes, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
                    }
                }
                finally
                {
                    _frameBitmap.UnlockBits(data);
                }

                _lastSeq = seq;
                _hasFrame = true;
                return _frameBitmap;
            }
        }

        public void MarkFrameConsumed()
        {
            // _lastSeq is updated directly inside AcquireFrame when pixels are copied.
        }

        private void StartChildAndLoadLocked()
        {
            MemoryMappedFile oldMmf = _mmf;
            MemoryMappedViewAccessor oldView = _view;
            CreateSharedMemoryLocked(_width, _height);
            DisposeMapping(oldMmf, oldView);
            _lastSeq = 0;
            _readyReceived = false;
            _healthyWatch.Reset();

            int gen = ++_generation;
            string workDir = Path.GetDirectoryName(_rendererExe) ?? "";
            RendererChildProcess child = RendererChildProcess.Start(_rendererExe, "--ipc", workDir);
            _child = child;
            _isDeElevated = child.IsDeElevated;
            _launchMode = child.LaunchMode;

            var stdoutThread = new Thread(() => ReadStdoutLoop(child, gen))
            {
                IsBackground = true,
                Name = "WPE-Stdout-" + _screen + "-" + gen
            };
            stdoutThread.Start();

            var stderrThread = new Thread(() => ReadStderrLoop(child, gen))
            {
                IsBackground = true,
                Name = "WPE-Stderr-" + _screen + "-" + gen
            };
            stderrThread.Start();

            var loadCmd = new Dictionary<string, object>
            {
                { "cmd", "load" },
                { "dir", _wallpaperDir },
                { "assets", _assetsDir },
                { "width", _width },
                { "height", _height },
                { "fps", _fps },
                { "fit", FitModeToWire(_fit) },
                { "shm", _shmName }
            };
            if (_properties.Count > 0)
            {
                loadCmd["props"] = new Dictionary<string, object>(_properties, StringComparer.OrdinalIgnoreCase);
            }
            SendCommandLocked(loadCmd);

            if (_paused)
            {
                SendCommandLocked(new Dictionary<string, object>
                {
                    { "cmd", "pause" },
                    { "value", true }
                });
            }
            if (_lastSensors != null && _lastSensors.Count > 0)
            {
                SendCommandLocked(new Dictionary<string, object>
                {
                    { "cmd", "sensors" },
                    { "data", _lastSensors }
                });
            }
        }

        private void HandleUnexpectedExitLocked()
        {
            if (_disposed || _child == null) return;
            int exitCode = _child.ExitCode;
            try { _child.Dispose(); } catch { }
            _child = null;
            _readyReceived = false;
            _healthyWatch.Reset();

            if (_fatalProtocolError)
            {
                // Keep the original protocol error (e.g. UNSUPPORTED_TYPE) without restarting.
                return;
            }

            ScheduleOrExhaustRestartLocked(exitCode, null);
        }

        private void ScheduleOrExhaustRestartLocked(int exitCode, string extraError)
        {
            if (_consecutiveCrashes < MaxConsecutiveRestarts)
            {
                _consecutiveCrashes++;
                int delayMs = RestartBackoffMs[Math.Min(_consecutiveCrashes - 1, RestartBackoffMs.Length - 1)];
                _pendingRestart = true;
                _nextRestartMs = _clock.ElapsedMilliseconds + delayMs;
                _lastErrorCode = "RENDERER_RESTARTING";
                _lastError = Loc.T("err.wpeRestarting", exitCode, _consecutiveCrashes, MaxConsecutiveRestarts);
            }
            else
            {
                _pendingRestart = false;
                _restartExhausted = true;
                _lastErrorCode = "RENDERER_CRASHED";
                _lastError = string.IsNullOrEmpty(extraError)
                    ? Loc.T("err.wpeCrashed", exitCode, MaxConsecutiveRestarts)
                    : Loc.T("err.wpeCrashed", exitCode, MaxConsecutiveRestarts) + " (" + extraError + ")";
            }
        }

        private void CreateSharedMemoryLocked(int width, int height)
        {
            int seq = Interlocked.Increment(ref _shmCounter);
            int pid = Process.GetCurrentProcess().Id;
            _shmName = string.Format(@"Local\LcdFusion_Wpe_{0}_{1}_{2}", pid, (int)_screen, seq);

            long size = WallpaperIpcProtocol.CalculateMappingSize(width, height);
            MemoryMappedFileSecurity sec = RendererSecurity.CreateSharedMemorySecurity();
            _mmf = sec != null
                ? MemoryMappedFile.CreateNew(_shmName, size, MemoryMappedFileAccess.ReadWrite, MemoryMappedFileOptions.None, sec, HandleInheritability.None)
                : MemoryMappedFile.CreateNew(_shmName, size, MemoryMappedFileAccess.ReadWrite);

            RendererSecurity.ApplyMediumMandatoryLabelIfElevated(_mmf.SafeMemoryMappedFileHandle);
            _view = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);

            _view.Write(WallpaperIpcProtocol.OffsetMagic, WallpaperIpcProtocol.Magic);
            _view.Write(WallpaperIpcProtocol.OffsetVersion, WallpaperIpcProtocol.Version);
            _view.Write(WallpaperIpcProtocol.OffsetWidth, (uint)width);
            _view.Write(WallpaperIpcProtocol.OffsetHeight, (uint)height);
            _view.Write(WallpaperIpcProtocol.OffsetStride, (uint)(width * 4));
            _view.Write(WallpaperIpcProtocol.OffsetActiveSlot, 0u);
            _view.Write(WallpaperIpcProtocol.OffsetFrameSeq, 0UL);
            _view.Write(WallpaperIpcProtocol.OffsetState, WallpaperIpcProtocol.StateInit);
            Thread.MemoryBarrier();
        }

        private void SendCommandLocked(Dictionary<string, object> payload)
        {
            try
            {
                if (_child == null || _child.Stdin == null || _child.HasExited) return;
                string line = _json.Serialize(payload);
                _child.Stdin.WriteLine(line);
            }
            catch (Exception ex)
            {
                if (!_fatalProtocolError && !_pendingRestart)
                {
                    _lastErrorCode = "IPC_WRITE_ERROR";
                    _lastError = "Renderer IPC error: " + ex.Message;
                }
            }
        }

        private void ReadStdoutLoop(RendererChildProcess child, int generation)
        {
            try
            {
                StreamReader reader = child.Stdout;
                string line;
                while (!_disposed && reader != null && (line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    HandleEventLine(line, generation);
                }
            }
            catch { }

            // Detect unexpected EOF / crash promptly instead of waiting only for polling.
            lock (_sync)
            {
                if (!_disposed && generation == _generation && ReferenceEquals(_child, child))
                {
                    // Brief wait so OS finishes recording exit code if stdout closed right before termination.
                    if (!child.HasExited) child.WaitForExit(150);
                    if (child.HasExited)
                        HandleUnexpectedExitLocked();
                }
            }
        }

        private void ReadStderrLoop(RendererChildProcess child, int generation)
        {
            try
            {
                StreamReader reader = child.Stderr;
                while (!_disposed && reader != null && reader.ReadLine() != null) { }
            }
            catch { }
        }

        private void HandleEventLine(string line, int generation)
        {
            try
            {
                var obj = _json.DeserializeObject(line) as Dictionary<string, object>;
                if (obj == null) return;
                object evObj;
                if (!obj.TryGetValue("event", out evObj) || evObj == null) return;
                string ev = Convert.ToString(evObj);

                lock (_sync)
                {
                    if (_disposed || generation != _generation) return;

                    if (string.Equals(ev, "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastError = null;
                        _lastErrorCode = null;
                        _fatalProtocolError = false;
                        _readyReceived = true;
                        _healthyWatch.Restart();
                        object backendObj;
                        if (obj.TryGetValue("backend", out backendObj) && backendObj != null)
                            _backendName = Convert.ToString(backendObj);
                        if (_lastSensors != null && _lastSensors.Count > 0 && _child != null && !_child.HasExited)
                        {
                            SendCommandLocked(new Dictionary<string, object>
                            {
                                { "cmd", "sensors" },
                                { "data", _lastSensors }
                            });
                        }
                    }
                    else if (string.Equals(ev, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        object codeObj;
                        string code = (obj.TryGetValue("code", out codeObj) && codeObj != null)
                            ? Convert.ToString(codeObj)
                            : "RENDERER_ERROR";
                        object msgObj;
                        string msg = (obj.TryGetValue("message", out msgObj) && msgObj != null)
                            ? Convert.ToString(msgObj)
                            : "Unknown renderer error";
                        _lastErrorCode = code;
                        _lastError = msg;

                        if (IsNonRecoverableErrorCode(code))
                        {
                            _fatalProtocolError = true;
                            _pendingRestart = false;
                        }
                    }
                }
            }
            catch { }
        }

        private static bool IsNonRecoverableErrorCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            return string.Equals(code, "UNSUPPORTED_TYPE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "LOAD_FAILED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "VULKAN_INIT_FAILED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "WEBVIEW2_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "WEBVIEW2_INIT_FAILED", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _pendingRestart = false;

                RendererChildProcess child = _child;
                _child = null;

                if (child != null)
                {
                    try
                    {
                        if (child.Stdin != null && !child.HasExited)
                        {
                            string line = _json.Serialize(new Dictionary<string, object> { { "cmd", "quit" } });
                            child.Stdin.WriteLine(line);
                        }
                    }
                    catch { }

                    child.CloseStdin();

                    try
                    {
                        if (!child.HasExited && !child.WaitForExit(400))
                            child.Kill();
                    }
                    catch { }

                    try { child.Dispose(); } catch { }
                }

                DisposeMapping(_mmf, _view);
                _mmf = null;
                _view = null;

                if (_frameBitmap != null)
                {
                    try { _frameBitmap.Dispose(); } catch { }
                    _frameBitmap = null;
                }
                _stagingBytes = null;
            }
        }

        private static void DisposeMapping(MemoryMappedFile mmf, MemoryMappedViewAccessor view)
        {
            try { if (view != null) view.Dispose(); } catch { }
            try { if (mmf != null) mmf.Dispose(); } catch { }
        }

        private static int ClampDim(int value)
        {
            if (value < 64) return 64;
            if (value > 2048) return 2048;
            return value;
        }

        private static int ClampFps(int fps)
        {
            if (fps < 1) return 1;
            if (fps > 60) return 60;
            return fps;
        }
    }
}
