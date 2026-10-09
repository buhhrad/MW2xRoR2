using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MW2RoR2
{
    /// A freeze leaves no trace: Windows closes the game as "not responding", BepInEx starts its log
    /// over on the next launch and OnApplicationQuit never runs (playtest 10-04-26: froze leaving
    /// Create-a-Class in character select, nothing to go on). A background thread watches the main
    /// thread's heartbeat; after 15 s without one it copies the log to BepInEx\hangs and samples the
    /// main thread itself (suspend, registers, raw stack -> module+offset, three times a second
    /// apart: spinning or blocked), then tries a minidump. Once per freeze; collect-logs takes the
    /// folder along. (dbghelp's in-process minidump of a real freeze came out empty: it needs locks
    /// the frozen thread can hold, so the stack sample comes first and needs none of them.)
    static class Mw2Watchdog
    {
        const int StallMs = 15000;
        static int beat = Environment.TickCount;
        static Thread thread;
        static string dir, logPath;
        static IntPtr mainThread;
        static ulong stackLow, stackHigh;
        // Allocated up front: nothing may allocate while the main thread is suspended (it may hold
        // the heap or GC lock).
        static readonly byte[] stackCopy = new byte[1 << 20];
        static IntPtr context;

        public static void Start()
        {
            if (thread != null) return;
            dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "hangs");
            logPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
            try
            {
                // Called on the main thread: keep a real handle to it and its stack bounds.
                IntPtr proc = GetCurrentProcess();
                DuplicateHandle(proc, GetCurrentThread(), proc, out mainThread, 0, false, DuplicateSameAccess);
                GetCurrentThreadStackLimits(out var lo, out var hi);
                stackLow = (ulong)lo; stackHigh = (ulong)hi;
                context = Marshal.AllocHGlobal(ContextSize + 16);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2 watchdog: main thread handle: {e.Message}"); }
            thread = new Thread(Watch) { IsBackground = true, Name = "MW2 watchdog", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        static volatile bool focused = true;
        /// Pilot freeze test: count the stall even if the game window isn't focused.
        public static volatile bool Armed;

        /// Main thread, every frame.
        public static void Beat()
        {
            Volatile.Write(ref beat, Environment.TickCount);
            focused = UnityEngine.Application.isFocused || Armed;
        }

        static void Watch()
        {
            bool dumped = false;
            while (true)
            {
                Thread.Sleep(1000);
                // Alt-tabbed away Unity may stop updating: only a stall that began with the game in
                // focus counts (the focus it last saw: a hung window's ghost isn't the game's).
                if (!focused) { Volatile.Write(ref beat, Environment.TickCount); continue; }
                int since = unchecked(Environment.TickCount - Volatile.Read(ref beat));
                if (since < StallMs) { dumped = false; continue; }
                if (dumped) continue;
                dumped = true;
                try { Dump(since); } catch { } // never take the game down from here
            }
        }

        static void Dump(int since)
        {
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyMMdd-HHmmss");
            string log = Path.Combine(dir, $"hang-{stamp}.log");
            using (var from = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var to = File.Create(log))
                from.CopyTo(to);
            var report = new StringBuilder($"[watchdog] main thread silent for {since} ms\n");
            var modules = Modules(report);
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) Thread.Sleep(1000);
                Sample(i, modules, report);
            }
            File.WriteAllText(Path.Combine(dir, $"hang-{stamp}.stack.txt"), report.ToString());
            File.AppendAllText(log, $"\n[watchdog] main thread silent for {since} ms; stack in hang-{stamp}.stack.txt\n");
            using (var f = File.Create(Path.Combine(dir, $"hang-{stamp}.dmp")))
            {
                var p = System.Diagnostics.Process.GetCurrentProcess();
                MiniDumpWriteDump(p.Handle, (uint)p.Id, f.SafeFileHandle.DangerousGetHandle(), MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            foreach (var old in new DirectoryInfo(dir).GetFiles("hang-*"))
                if (old.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) old.Delete();
        }

        static List<(ulong lo, ulong hi, string name)> Modules(StringBuilder report)
        {
            var list = new List<(ulong, ulong, string)>();
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                ulong b = (ulong)m.BaseAddress.ToInt64();
                list.Add((b, b + (ulong)m.ModuleMemorySize, m.ModuleName));
                if (m.ModuleName.StartsWith("mw2sim", StringComparison.OrdinalIgnoreCase) || m.ModuleName.StartsWith("mono", StringComparison.OrdinalIgnoreCase) || m.ModuleName.StartsWith("UnityPlayer", StringComparison.OrdinalIgnoreCase))
                    report.Append($"module {m.ModuleName} base 0x{b:x} size 0x{m.ModuleMemorySize:x}\n");
            }
            return list;
        }

        static string Where(List<(ulong lo, ulong hi, string name)> modules, ulong a)
        {
            foreach (var (lo, hi, name) in modules)
                if (a >= lo && a < hi) return $"{name}+0x{a - lo:x}";
            return null;
        }

        static void Sample(int i, List<(ulong lo, ulong hi, string name)> modules, StringBuilder report)
        {
            if (mainThread == IntPtr.Zero || context == IntPtr.Zero) { report.Append("no main thread handle\n"); return; }
            IntPtr ctx = new IntPtr((context.ToInt64() + 15) & ~15L);
            ulong rip = 0, rsp = 0;
            int copied = 0;
            if (SuspendThread(mainThread) == uint.MaxValue) { report.Append($"suspend failed {Marshal.GetLastWin32Error()}\n"); return; }
            try
            {
                Marshal.WriteInt32(ctx, 0x30, ContextControlInteger);
                if (GetThreadContext(mainThread, ctx))
                {
                    rsp = (ulong)Marshal.ReadInt64(ctx, 0x98);
                    rip = (ulong)Marshal.ReadInt64(ctx, 0xF8);
                    if (rsp >= stackLow && rsp < stackHigh)
                    {
                        copied = (int)Math.Min((ulong)stackCopy.Length, stackHigh - rsp);
                        Marshal.Copy(new IntPtr((long)rsp), stackCopy, 0, copied);
                    }
                }
            }
            finally { ResumeThread(mainThread); }
            report.Append($"\n== sample {i}: rip {Where(modules, rip) ?? $"0x{rip:x}"} rsp 0x{rsp:x}, {copied} stack bytes\n");
            int shown = 0;
            for (int k = 0; k + 8 <= copied && shown < 80; k += 8)
            {
                ulong v = BitConverter.ToUInt64(stackCopy, k);
                string w = Where(modules, v);
                if (w == null) continue;
                report.Append($"  [rsp+0x{k:x}] {w}\n");
                shown++;
            }
        }

        const int ContextSize = 0x4D0, ContextControlInteger = 0x100003;
        const uint DuplicateSameAccess = 2;
        const uint MiniDumpWithUnloadedModules = 0x20, MiniDumpWithThreadInfo = 0x1000;

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DuplicateHandle(IntPtr srcProcess, IntPtr src, IntPtr dstProcess, out IntPtr dst, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] static extern void GetCurrentThreadStackLimits(out UIntPtr low, out UIntPtr high);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint SuspendThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("dbghelp.dll", SetLastError = true)]
        static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint type, IntPtr exception, IntPtr userStream, IntPtr callback);
    }
}
