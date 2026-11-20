// claude-trap: run Bun-compiled Claude Code natively on CPUs without SSE4.2/POPCNT (e.g. Penryn).
// - Starts claude-real.exe under a minimal debugger (DEBUG_ONLY_THIS_PROCESS).
// - In memory only, disables Bun's "CPU is missing SSE4.2" startup check (file on disk untouched).
// - Sets SIMDUTF_FORCE_IMPLEMENTATION=westmere (otherwise simdutf has no usable implementation on
//   pre-SSE4.2 CPUs and returns empty strings, e.g. "error: EISDIR reading """).
// - Emulates the few missing instructions when they fault (#UD -> STATUS_ILLEGAL_INSTRUCTION):
//   POPCNT, PCMPESTRI/ESTRM/ISTRI/ISTRM, PCMPGTQ, CRC32. Everything else runs at full native speed.
// - Hot POPCNT reg,reg sites are rewritten in memory into a jump to a native SWAR trampoline.
// Usage: rename the original claude.exe to claude-real.exe and put this program next to it as claude.exe,
// or point CLAUDE_TRAP_REAL at the real binary. All arguments, stdin/stdout/stderr and the exit code pass through.
// Build: build.cmd (uses the C# compiler that ships with Windows / .NET Framework 4.x).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

static class Trap
{
    // Win32
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic; public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr t);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WaitForDebugEvent(IntPtr ev, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ContinueDebugEvent(int pid, int tid, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadContext(IntPtr t, IntPtr ctx);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetThreadContext(IntPtr t, IntPtr ctx);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtectEx(IntPtr p, IntPtr addr, IntPtr size, uint prot, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FlushInstructionCache(IntPtr p, IntPtr addr, IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DebugSetProcessKillOnExit(bool kill);

    const uint DBG_CONTINUE = 0x00010002, DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
    const uint CTX_FLAGS = 0x10000B; // CONTEXT_AMD64 | CONTROL | INTEGER | FLOATING_POINT
    const int OFF_CTXFLAGS = 0x30, OFF_EFLAGS = 0x44, OFF_GPR = 0x78, OFF_RIP = 0xF8, OFF_XMM = 0x1A0;

    // logging / config
    static string logPath = Path.Combine(Path.GetTempPath(), "claude-trap.log");
    static void Log(string s)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" +
            System.Diagnostics.Process.GetCurrentProcess().Id + "] " + s + Environment.NewLine); } catch { }
    }
    static string Setting(string envName, string iniKey, string def, string iniPath)
    {
        string v = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(v)) return v;
        try
        {
            if (File.Exists(iniPath))
                foreach (string line in File.ReadAllLines(iniPath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0 && line.Substring(0, i).Trim().Equals(iniKey, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(i + 1).Trim();
                }
        }
        catch { }
        return def;
    }
    static string RawArgs()
    {
        string cl = Environment.CommandLine; int i = 0;
        while (i < cl.Length && cl[i] == ' ') i++;
        if (i < cl.Length && cl[i] == '"') { i++; while (i < cl.Length && cl[i] != '"') i++; if (i < cl.Length) i++; }
        else while (i < cl.Length && cl[i] != ' ' && cl[i] != '\t') i++;
        return cl.Substring(i).TrimStart(' ', '\t');
    }
    static string Q(string s) { return s.IndexOf(' ') >= 0 || s.Length == 0 ? "\"" + s + "\"" : s; }

    // locate Bun's SSE4.2 check in the PE file (cached)
    // Returns RVA of the conditional jump that leads to the "missing SSE4.2" error, and its length.
    static bool FindCheck(string exe, out long jrva, out int jlen)
    {
        jrva = 0; jlen = 0;
        var fi = new FileInfo(exe);
        string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-trap");
        string key = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        string cache = Path.Combine(cacheDir, "cache.txt");
        try
        {
            if (File.Exists(cache))
                foreach (string l in File.ReadAllLines(cache))
                {
                    var p = l.Split('|');
                    if (p.Length == 4 && p[0] + "|" + p[1] == key) { jrva = Convert.ToInt64(p[2], 16); jlen = int.Parse(p[3]); return jrva != 0; }
                }
        }
        catch { }

        byte[] b = File.ReadAllBytes(exe);
        int pe = BitConverter.ToInt32(b, 0x3C); int nsec = BitConverter.ToUInt16(b, pe + 6); int optsz = BitConverter.ToUInt16(b, pe + 20);
        int sh = pe + 24 + optsz;
        var secs = new List<long[]>();
        for (int i = 0; i < nsec; i++) { int o = sh + i * 40; secs.Add(new long[] { BitConverter.ToUInt32(b, o + 12), BitConverter.ToUInt32(b, o + 8), BitConverter.ToUInt32(b, o + 20), BitConverter.ToUInt32(b, o + 16), BitConverter.ToUInt32(b, o + 36) }); }
        Func<long, long> f2r = off => { foreach (var s in secs) if (off >= s[2] && off < s[2] + s[3]) return off - s[2] + s[0]; return -1; };
        Func<long, long> r2f = rva => { foreach (var s in secs) if (rva >= s[0] && rva < s[0] + s[3]) return rva - s[0] + s[2]; return -1; };

        byte[] needle = Encoding.ASCII.GetBytes("this CPU is missing SSE4.2");
        long strRva = -1;
        for (int i = 0; i < b.Length - needle.Length && strRva < 0; i++)
        {
            if (b[i] != needle[0]) continue;
            int k = 1; while (k < needle.Length && b[i + k] == needle[k]) k++;
            if (k == needle.Length) { int st = i; while (st > 0 && b[st - 1] >= 0x20 && b[st - 1] < 0x7f) st--; strRva = f2r(st); }
        }
        if (strRva < 0) { Log("check: string not found (maybe no check in this build)"); Save(cacheDir, cache, key, 0, 0); return false; }

        long[] text = null; foreach (var s in secs) if ((s[4] & 0x20000000) != 0) { text = s; break; } // IMAGE_SCN_MEM_EXECUTE
        if (text == null) return false;
        int t0 = (int)text[2], t1 = (int)(text[2] + text[3]);
        long d = text[0] - text[2];

        long xref = -1;
        for (int i = t0; i < t1 - 7 && xref < 0; i++)
            if ((b[i] == 0x48 || b[i] == 0x4C) && b[i + 1] == 0x8D && (b[i + 2] & 0xC7) == 0x05 && i + d + 7 + BitConverter.ToInt32(b, i + 3) == strRva) xref = i;
        if (xref < 0) { Log("check: xref not found"); return false; }
        // Function start from the exception directory (.pdata RUNTIME_FUNCTION entries).
        long xrefRva = xref + d, fnRva = -1;
        int dd = pe + 24 + 112 + 3 * 8; // optional header (PE32+) data directory #3
        long pRva = BitConverter.ToUInt32(b, dd), pSize = BitConverter.ToUInt32(b, dd + 4), pOff = r2f(pRva);
        for (long e = 0; pOff >= 0 && e + 12 <= pSize; e += 12)
        {
            long beg = BitConverter.ToUInt32(b, (int)(pOff + e)), end = BitConverter.ToUInt32(b, (int)(pOff + e + 4));
            if (xrefRva >= beg && xrefRva < end) { fnRva = beg; break; }
        }
        if (fnRva < 0) { Log("check: function start not found"); return false; }
        long callRva = -1;
        for (int i = t0; i < t1 - 5 && callRva < 0; i++)
            if (b[i] == 0xE8 && i + d + 5 + BitConverter.ToInt32(b, i + 1) == fnRva) callRva = i + d;
        if (callRva < 0) { Log("check: call to error fn not found"); return false; }
        for (int i = t0; i < t1 - 6; i++)
        {
            if (b[i] == 0x0F && b[i + 1] == 0x84 && i + d + 6 + BitConverter.ToInt32(b, i + 2) == callRva) { jrva = i + d; jlen = 6; break; }
            if (b[i] == 0x74 && i + d + 2 + (sbyte)b[i + 1] == callRva && i + 2 + 0x80 > (callRva - d)) { jrva = i + d; jlen = 2; break; }
        }
        if (jrva == 0) { Log("check: jz to error block not found"); return false; }
        Log(string.Format("check: string rva {0:X}, error fn {1:X}, call {2:X}, jz {3:X} len {4}", strRva, fnRva, callRva, jrva, jlen));
        Save(cacheDir, cache, key, jrva, jlen);
        return true;
    }
    static void Save(string dir, string cache, string key, long rva, int len)
    {
        try { Directory.CreateDirectory(dir); File.AppendAllText(cache, key + "|" + rva.ToString("X") + "|" + len + Environment.NewLine); } catch { }
    }

    // emulator
    static IntPtr hProc;
    static IntPtr ctxRaw, ctx;
    static Dictionary<int, IntPtr> threads = new Dictionary<int, IntPtr>();
    static Dictionary<long, long> hits = new Dictionary<long, long>();
    static long emulated, unknown;
    static bool traceNext;
    static Dictionary<long, string> siteBytes = new Dictionary<long, string>();

    static ulong Gpr(int i) { return (ulong)Marshal.ReadInt64(ctx, OFF_GPR + 8 * i); }
    static void SetGpr(int i, ulong v) { Marshal.WriteInt64(ctx, OFF_GPR + 8 * i, (long)v); }
    static byte[] ReadMem(long addr, int n)
    {
        var buf = new byte[n]; IntPtr r;
        if (!ReadProcessMemory(hProc, new IntPtr(addr), buf, new IntPtr(n), out r) || r.ToInt64() != n) return null;
        return buf;
    }

    // Decode ModRM at code[p]. Returns bytes consumed (modrm+sib+disp). For memory operands
    // the address is computed later (needs instruction length for RIP-relative): stores pieces.
    struct ModRM { public int mod, reg, rm; public bool isReg; public long disp; public bool ripRel; public int baseR, indexR, scale; public int len; }
    static ModRM Decode(byte[] c, int p, int rex)
    {
        var m = new ModRM(); byte mb = c[p];
        m.mod = mb >> 6; m.reg = ((mb >> 3) & 7) | ((rex & 4) << 1); m.rm = mb & 7; m.len = 1;
        m.baseR = -1; m.indexR = -1; m.scale = 1;
        if (m.mod == 3) { m.isReg = true; m.rm |= (rex & 1) << 3; return m; }
        if (m.rm == 4)
        {
            byte sib = c[p + 1]; m.len++;
            m.scale = 1 << (sib >> 6);
            int idx = ((sib >> 3) & 7) | ((rex & 2) << 2); if (idx != 4) m.indexR = idx;
            int bs = sib & 7;
            if (bs == 5 && m.mod == 0) { m.disp = BitConverter.ToInt32(c, p + 2); m.len += 4; }
            else m.baseR = bs | ((rex & 1) << 3);
        }
        else if (m.rm == 5 && m.mod == 0) { m.ripRel = true; m.disp = BitConverter.ToInt32(c, p + 1); m.len += 4; return m; }
        else m.baseR = m.rm | ((rex & 1) << 3);
        if (m.mod == 1) { m.disp = (sbyte)c[p + m.len]; m.len += 1; }
        else if (m.mod == 2) { m.disp = BitConverter.ToInt32(c, p + m.len); m.len += 4; }
        return m;
    }
    static long Addr(ModRM m, long rip, int insnLen)
    {
        if (m.ripRel) return rip + insnLen + m.disp;
        long a = m.disp;
        if (m.baseR >= 0) a += (long)Gpr(m.baseR);
        if (m.indexR >= 0) a += (long)Gpr(m.indexR) * m.scale;
        return a;
    }
    static ulong Crc32c(ulong crc, ulong data, int bytes)
    {
        uint c = (uint)crc;
        for (int i = 0; i < bytes; i++)
        {
            c ^= (byte)(data >> (8 * i));
            for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0x82F63B78u & (uint)-(int)(c & 1));
        }
        return c;
    }

    // Returns true if the instruction at rip was emulated and the context updated.
    static bool Emulate(IntPtr hThread, long rip)
    {
        byte[] c = ReadMem(rip, 16);
        if (c == null) return false;
        int p = 0, rex = 0; bool opsz = false, f3 = false, f2 = false, segOverride = false;
        for (; p < 15; p++)
        {
            byte x = c[p];
            if (x == 0x66) opsz = true; else if (x == 0xF3) f3 = true; else if (x == 0xF2) f2 = true;
            else if (x == 0x2E || x == 0x36 || x == 0x3E || x == 0x26 || x == 0xF0) { }
            else if (x == 0x64 || x == 0x65 || x == 0x67) segOverride = true;
            else break;
        }
        if ((c[p] & 0xF0) == 0x40) { rex = c[p] & 0xF; p++; }
        if (c[p] != 0x0F || segOverride) return false;

        Marshal.WriteInt32(ctx, OFF_CTXFLAGS, (int)CTX_FLAGS);
        if (!GetThreadContext(hThread, ctx)) { Log("GetThreadContext failed " + Marshal.GetLastWin32Error()); return false; }

        bool w = (rex & 8) != 0;
        if (f3 && c[p + 1] == 0xB8) // POPCNT r, r/m
        {
            int mp = p + 2; ModRM m = Decode(c, mp, rex); int len = mp + m.len;
            int size = w ? 8 : (opsz ? 2 : 4);
            // Rewrite the site to a native trampoline; the thread then simply re-executes from rip.
            if (m.isReg && size != 2 && rewrite && p == 1 + (rex != 0 ? 1 : 0) && m.reg != 4 && m.rm != 4 && TryRewritePopcnt(rip, len, m.reg, m.rm, size))
                return true;
            ulong src;
            if (m.isReg) src = Gpr(m.rm);
            else { byte[] mem = ReadMem(Addr(m, rip, len), size); if (mem == null) return false; src = BitConverter.ToUInt64(Pad(mem), 0); }
            if (size == 4) src &= 0xFFFFFFFF; else if (size == 2) src &= 0xFFFF;
            ulong cnt = 0; for (ulong v = src; v != 0; v &= v - 1) cnt++;
            if (size == 2) SetGpr(m.reg, (Gpr(m.reg) & ~0xFFFFUL) | cnt); else SetGpr(m.reg, cnt);
            if (traceNext) Log(string.Format("  popcnt {0} size={1} reg={2} isReg={3} rm={4} src={5:X} cnt={6} len={7}", BitConverter.ToString(c, 0, len), size, m.reg, m.isReg, m.rm, src, cnt, len));
            int fl = Marshal.ReadInt32(ctx, OFF_EFLAGS) & ~(0x1 | 0x4 | 0x10 | 0x40 | 0x80 | 0x800);
            if (src == 0) fl |= 0x40;
            Marshal.WriteInt32(ctx, OFF_EFLAGS, fl);
            return Finish(hThread, rip, len);
        }
        if (f2 && c[p + 1] == 0x38 && (c[p + 2] == 0xF0 || c[p + 2] == 0xF1)) // CRC32 r32/64, r/m8/16/32/64
        {
            int mp = p + 3; ModRM m = Decode(c, mp, rex); int len = mp + m.len;
            int size = c[p + 2] == 0xF0 ? 1 : (w ? 8 : (opsz ? 2 : 4));
            ulong src;
            if (m.isReg) { src = Gpr(m.rm); if (size == 1 && rex == 0 && m.rm >= 4 && m.rm < 8) src = Gpr(m.rm - 4) >> 8; }
            else { byte[] mem = ReadMem(Addr(m, rip, len), size); if (mem == null) return false; src = BitConverter.ToUInt64(Pad(mem), 0); }
            SetGpr(m.reg, Crc32c(Gpr(m.reg) & 0xFFFFFFFF, src, size));
            return Finish(hThread, rip, len);
        }
        if (opsz && c[p + 1] == 0x38 && c[p + 2] == 0x37) // PCMPGTQ xmm, xmm/m128
        {
            int mp = p + 3; ModRM m = Decode(c, mp, rex); int len = mp + m.len;
            byte[] src;
            if (m.isReg) { src = new byte[16]; Marshal.Copy(ctx + OFF_XMM + 16 * m.rm, src, 0, 16); }
            else { src = ReadMem(Addr(m, rip, len), 16); if (src == null) return false; }
            IntPtr dst = ctx + OFF_XMM + 16 * m.reg;
            for (int lane = 0; lane < 2; lane++)
            {
                long a = Marshal.ReadInt64(dst, 8 * lane), bb = BitConverter.ToInt64(src, 8 * lane);
                Marshal.WriteInt64(dst, 8 * lane, a > bb ? -1L : 0L);
            }
            return Finish(hThread, rip, len);
        }
        if (opsz && c[p + 1] == 0x3A && c[p + 2] >= 0x60 && c[p + 2] <= 0x63) // PCMPESTRM/ESTRI/ISTRM/ISTRI
        {
            int op = c[p + 2]; int mp = p + 3; ModRM m = Decode(c, mp, rex); int len = mp + m.len + 1;
            int imm = c[mp + m.len];
            byte[] a = new byte[16]; Marshal.Copy(ctx + OFF_XMM + 16 * m.reg, a, 0, 16);
            byte[] bsrc;
            if (m.isReg) { bsrc = new byte[16]; Marshal.Copy(ctx + OFF_XMM + 16 * m.rm, bsrc, 0, 16); }
            else { bsrc = ReadMem(Addr(m, rip, len), 16); if (bsrc == null) return false; }
            PcmpStr(op, imm, a, bsrc, w);
            return Finish(hThread, rip, len);
        }
        return false;
    }

    static int Elem(byte[] v, int i, int fmt)
    {
        switch (fmt) { case 0: return v[i]; case 1: return BitConverter.ToUInt16(v, 2 * i); case 2: return (sbyte)v[i]; default: return BitConverter.ToInt16(v, 2 * i); }
    }
    static void PcmpStr(int op, int imm, byte[] a, byte[] b, bool w)
    {
        int fmt = imm & 3, agg = (imm >> 2) & 3, pol = (imm >> 4) & 3; bool msb = (imm & 0x40) != 0;
        int n = (fmt & 1) != 0 ? 8 : 16;
        int la, lb;
        bool explicitLen = op == 0x60 || op == 0x61;
        if (explicitLen)
        {
            long ra = w ? (long)Gpr(0) : (int)Gpr(0), rd = w ? (long)Gpr(2) : (int)Gpr(2);
            ra = Math.Abs(ra); rd = Math.Abs(rd);
            la = (int)Math.Min(ra, n); lb = (int)Math.Min(rd, n);
        }
        else
        {
            la = n; for (int j = 0; j < n; j++) if (Elem(a, j, fmt) == 0) { la = j; break; }
            lb = n; for (int i = 0; i < n; i++) if (Elem(b, i, fmt) == 0) { lb = i; break; }
        }
        int res = 0;
        for (int i = 0; i < n; i++)
        {
            bool bv = i < lb; bool r = false;
            switch (agg)
            {
                case 0: // equal any
                    if (bv) for (int j = 0; j < la && !r; j++) r = Elem(b, i, fmt) == Elem(a, j, fmt);
                    break;
                case 1: // ranges
                    if (bv) for (int j = 0; j + 1 < la && !r; j += 2) { int x = Elem(b, i, fmt); r = Elem(a, j, fmt) <= x && x <= Elem(a, j + 1, fmt); }
                    break;
                case 2: // equal each
                {
                    bool av = i < la;
                    r = (av && bv) ? Elem(a, i, fmt) == Elem(b, i, fmt) : (!av && !bv);
                    break;
                }
                default: // equal ordered
                    r = true;
                    for (int k = 0; k < n - i && r; k++)
                    {
                        if (k >= la) break;
                        if (i + k >= lb) { r = false; break; }
                        r = Elem(a, k, fmt) == Elem(b, i + k, fmt);
                    }
                    break;
            }
            if (r) res |= 1 << i;
        }
        int all = (1 << n) - 1;
        if (pol == 1) res ^= all;
        else if (pol == 3) res ^= (1 << lb) - 1;
        res &= all;

        if (op == 0x61 || op == 0x63)
        {
            int idx = n;
            if (res != 0) { if (msb) { idx = 31; while ((res & (1 << idx)) == 0) idx--; } else { idx = 0; while ((res & (1 << idx)) == 0) idx++; } }
            SetGpr(1, (ulong)idx);
        }
        else
        {
            byte[] outv = new byte[16];
            if (!msb) { outv[0] = (byte)res; outv[1] = (byte)(res >> 8); }
            else for (int i = 0; i < n; i++) if ((res & (1 << i)) != 0) { if (n == 16) outv[i] = 0xFF; else { outv[2 * i] = 0xFF; outv[2 * i + 1] = 0xFF; } }
            Marshal.Copy(outv, 0, ctx + OFF_XMM, 16);
        }
        int fl = Marshal.ReadInt32(ctx, OFF_EFLAGS) & ~(0x1 | 0x4 | 0x10 | 0x40 | 0x80 | 0x800);
        if (res != 0) fl |= 0x1;          // CF
        if (lb < n) fl |= 0x40;           // ZF
        if (la < n) fl |= 0x80;           // SF
        if ((res & 1) != 0) fl |= 0x800;  // OF
        Marshal.WriteInt32(ctx, OFF_EFLAGS, fl);
    }
    // hot-site rewriting (POPCNT reg,reg -> jmp to native SWAR trampoline)
    static bool rewrite = true;
    static long imageBase, imageEnd, trampNext, trampEnd;
    static uint[] pdBeg = new uint[0], pdEnd = new uint[0];
    static HashSet<long> usedCaves = new HashSet<long>();
    static int rewritten, rewriteFailed;
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, IntPtr size, uint type, uint prot);

    static void InitImage(long baseAddr)
    {
        imageBase = baseAddr;
        byte[] h = ReadMem(baseAddr, 0x1000); if (h == null) return;
        int pe = BitConverter.ToInt32(h, 0x3C);
        long sizeOfImage = BitConverter.ToUInt32(h, pe + 24 + 56);
        imageEnd = baseAddr + sizeOfImage;
        int dd = pe + 24 + 112 + 3 * 8;
        long pRva = BitConverter.ToUInt32(h, dd), pSize = BitConverter.ToUInt32(h, dd + 4);
        byte[] pd = ReadMem(baseAddr + pRva, (int)pSize);
        if (pd != null)
        {
            int n = (int)(pSize / 12); pdBeg = new uint[n]; pdEnd = new uint[n];
            for (int i = 0; i < n; i++) { pdBeg[i] = BitConverter.ToUInt32(pd, 12 * i); pdEnd[i] = BitConverter.ToUInt32(pd, 12 * i + 4); }
        }
    }
    static bool EnsureTramp(int need)
    {
        if (trampNext != 0 && trampNext + need <= trampEnd) return true;
        const long chunk = 0x10000;
        long start = (imageEnd + 0xFFFF) & ~0xFFFFL;
        for (long a = start; a < imageBase + 0x7F000000L; a += chunk)
        {
            IntPtr r = VirtualAllocEx(hProc, new IntPtr(a), new IntPtr(chunk), 0x3000, 0x40); // COMMIT|RESERVE, RWX
            if (r != IntPtr.Zero) { trampNext = r.ToInt64(); trampEnd = trampNext + chunk; imageEnd = trampEnd; return true; }
        }
        return false;
    }
    // index of pdata function containing rva, or -1
    static int FuncAt(long rva)
    {
        int lo = 0, hi = pdBeg.Length - 1;
        while (lo <= hi) { int mid = (lo + hi) >> 1; if (rva < pdBeg[mid]) hi = mid - 1; else if (rva >= pdEnd[mid]) lo = mid + 1; else return mid; }
        return -1;
    }
    // Find >=5 bytes of inter-function int3 padding reachable by a rel8 jump placed at 'from'.
    static long FindCave(long from)
    {
        long lo = from + 2 - 128, hi = from + 2 + 127 - 5;
        byte[] buf = ReadMem(lo, (int)(hi - lo + 5 + 16)); if (buf == null) return 0;
        for (long a = lo; a <= hi; a++)
        {
            int i = (int)(a - lo);
            if (buf[i] != 0xCC || (i > 0 && buf[i - 1] == 0xCC)) continue; // start of a CC run
            int run = 0; while (i + run < buf.Length && buf[i + run] == 0xCC) run++;
            if (run < 5) continue;
            long runEnd = a + run;
            if ((runEnd & 15) != 0) continue;                    // padding ends at function alignment
            if (i == 0 || buf[i - 1] != 0xC3 && FuncAt(a - imageBase - 1) < 0) continue; // preceded by ret or a known function's code
            if (FuncAt(a - imageBase) >= 0) continue;            // not inside a function
            for (long c = a; c + 5 <= runEnd; c += 5) if (!usedCaves.Contains(c)) { usedCaves.Add(c); return c; }
        }
        return 0;
    }
    static void Put(List<byte> l, params byte[] b) { l.AddRange(b); }
    static void PutRip(List<byte> l, long codeAddr, long target, params byte[] op)
    {   // op + disp32 rip-relative (disp is last 4 bytes of the instruction)
        l.AddRange(op); long next = codeAddr + l.Count + 4; l.AddRange(BitConverter.GetBytes((int)(target - next)));
    }
    static bool WriteCode(long addr, byte[] code)
    {
        uint old; IntPtr wr;
        VirtualProtectEx(hProc, new IntPtr(addr), new IntPtr(code.Length), 0x40, out old);
        bool ok = WriteProcessMemory(hProc, new IntPtr(addr), code, new IntPtr(code.Length), out wr);
        VirtualProtectEx(hProc, new IntPtr(addr), new IntPtr(code.Length), old, out old);
        FlushInstructionCache(hProc, new IntPtr(addr), new IntPtr(code.Length));
        return ok;
    }
    static byte[] Jmp32(long from, long to) { var b = new byte[5]; b[0] = 0xE9; Array.Copy(BitConverter.GetBytes((int)(to - from - 5)), 0, b, 1, 4); return b; }

    static HashSet<long> noRewrite = new HashSet<long>();

    // Length of a simple, position-independent, non-branch instruction; -1 if not whitelisted.
    static int SimpleLen(byte[] b, int o)
    {
        int s = o;
        bool w = false;
        if ((b[o] & 0xF0) == 0x40) { w = (b[o] & 8) != 0; o++; }
        int op = b[o++];
        if (op >= 0x50 && op <= 0x5F) return o - s;                       // push/pop reg
        if (op == 0x90) return o - s;
        if (op >= 0xB8 && op <= 0xBF) return o - s + (w ? 8 : 4);          // mov reg, imm
        int imm = -1;
        if (op < 0x40 && (op & 7) < 4) imm = 0;                             // ALU r/m forms 00-03,08-0B,...,38-3B
        else if (op >= 0x84 && op <= 0x8B || op == 0x8D || op == 0xD1 || op == 0xD3) imm = 0;
        else if (op == 0x83 || op == 0xC1 || op == 0xC0 || op == 0x6B || op == 0x80) imm = 1;
        else if (op == 0x81 || op == 0x69 || op == 0xC7) imm = 4;
        else if (op == 0x0F)
        {
            int op2 = b[o++];
            if (op2 == 0xAF || op2 == 0xB6 || op2 == 0xB7 || op2 == 0xBE || op2 == 0xBF || (op2 >= 0x40 && op2 <= 0x4F)) imm = 0;
            else return -1;
        }
        else return -1;
        int mod = b[o] >> 6, rm = b[o] & 7; o++;
        if (mod != 3)
        {
            if (rm == 4) { int bs = b[o] & 7; o++; if (mod == 0 && bs == 5) o += 4; }
            else if (rm == 5 && mod == 0) return -1;                       // RIP-relative: not relocatable
            if (mod == 1) o += 1; else if (mod == 2) o += 4;
        }
        return o - s + imm;
    }
    // True if any direct branch in the surrounding function targets (site, site+len).
    static bool BranchInto(long site, int len)
    {
        int f = FuncAt(site - imageBase);
        long lo = f >= 0 ? imageBase + pdBeg[f] : site - 4096, hi = f >= 0 ? imageBase + pdEnd[f] : site + 4096;
        if (hi - lo > 1 << 20) { lo = site - (1 << 19); hi = site + (1 << 19); }
        byte[] b = ReadMem(lo, (int)(hi - lo + 8)); if (b == null) return true;
        for (int i = 0; i + 6 <= b.Length; i++)
        {
            long a = lo + i, t = -1;
            if (b[i] == 0xEB || (b[i] >= 0x70 && b[i] <= 0x7F)) t = a + 2 + (sbyte)b[i + 1];
            if (t > site && t < site + len) return true;
            t = -1;
            if (b[i] == 0xE9) t = a + 5 + BitConverter.ToInt32(b, i + 1);
            else if (b[i] == 0x0F && b[i + 1] >= 0x80 && b[i + 1] <= 0x8F) t = a + 6 + BitConverter.ToInt32(b, i + 2);
            if (t > site && t < site + len) return true;
        }
        return false;
    }

    static bool TryRewritePopcnt(long site, int len, int dst, int src, int size)
    {
        if (imageBase == 0 || site < imageBase || site >= imageBase + 0x7F000000L || noRewrite.Contains(site)) return false;
        long cave = 0;
        byte[] reloc = new byte[0];
        if (len < 5) cave = FindCave(site);
        if (len < 5 && cave == 0)
        {
            // Move the following simple instruction(s) into the trampoline as well.
            byte[] nb = ReadMem(site + len, 32);
            int extra = 0;
            while (nb != null && len + extra < 5)
            {
                int l2 = SimpleLen(nb, extra);
                if (l2 <= 0) { extra = -1; break; }
                extra += l2;
            }
            if (extra <= 0 || BranchInto(site, len + extra)) { noRewrite.Add(site); rewriteFailed++; return false; }
            reloc = new byte[extra]; Array.Copy(nb, reloc, extra);
        }
        if (!EnsureTramp(160)) { noRewrite.Add(site); rewriteFailed++; return false; }
        long t = trampNext;
        long consts = t + 128; // M1, M2, M4, H01
        var l = new List<byte>();
        Put(l, 0x50, 0x52);                                                     // push rax; push rdx
        if (size == 8) Put(l, (byte)(0x48 | (src >= 8 ? 1 : 0)), 0x8B, (byte)(0xC0 | (src & 7)));        // mov rax, src
        else { if (src >= 8) Put(l, 0x41); Put(l, 0x8B, (byte)(0xC0 | (src & 7))); }                      // mov eax, src32
        Put(l, 0x48, 0x89, 0xC2); Put(l, 0x48, 0xD1, 0xEA);                     // mov rdx,rax; shr rdx,1
        PutRip(l, t, consts, 0x48, 0x23, 0x15);                                 // and rdx,[M1]
        Put(l, 0x48, 0x29, 0xD0); Put(l, 0x48, 0x89, 0xC2);                     // sub rax,rdx; mov rdx,rax
        PutRip(l, t, consts + 8, 0x48, 0x23, 0x05);                             // and rax,[M2]
        Put(l, 0x48, 0xC1, 0xEA, 0x02);                                         // shr rdx,2
        PutRip(l, t, consts + 8, 0x48, 0x23, 0x15);                             // and rdx,[M2]
        Put(l, 0x48, 0x01, 0xD0); Put(l, 0x48, 0x89, 0xC2);                     // add rax,rdx; mov rdx,rax
        Put(l, 0x48, 0xC1, 0xEA, 0x04); Put(l, 0x48, 0x01, 0xD0);               // shr rdx,4; add rax,rdx
        PutRip(l, t, consts + 16, 0x48, 0x23, 0x05);                            // and rax,[M4]
        PutRip(l, t, consts + 24, 0x48, 0x0F, 0xAF, 0x05);                      // imul rax,[H01]
        Put(l, 0x48, 0xC1, 0xE8, 0x38);                                         // shr rax,56
        Put(l, 0x48, 0x85, 0xC0);                                               // test rax,rax (ZF, CF=OF=SF=0)
        if (dst == 0) Put(l, 0x48, 0x89, 0x44, 0x24, 0x08);                     // mov [rsp+8],rax
        else if (dst == 2) Put(l, 0x48, 0x89, 0x04, 0x24);                      // mov [rsp],rax
        else Put(l, (byte)(0x48 | (dst >= 8 ? 1 : 0)), 0x89, (byte)(0xC0 | (dst & 7))); // mov dst,rax
        Put(l, 0x5A, 0x58);                                                     // pop rdx; pop rax
        l.AddRange(reloc);                                                      // relocated following instruction(s)
        l.AddRange(Jmp32(t + l.Count, site + len + reloc.Length));              // jmp back
        if (l.Count > 128) { noRewrite.Add(site); rewriteFailed++; return false; }
        var code = new byte[160];
        l.CopyTo(code);
        for (int i = l.Count; i < 128; i++) code[i] = 0xCC;
        Array.Copy(BitConverter.GetBytes(0x5555555555555555UL), 0, code, 128, 8);
        Array.Copy(BitConverter.GetBytes(0x3333333333333333UL), 0, code, 136, 8);
        Array.Copy(BitConverter.GetBytes(0x0F0F0F0F0F0F0F0FUL), 0, code, 144, 8);
        Array.Copy(BitConverter.GetBytes(0x0101010101010101UL), 0, code, 152, 8);
        if (!WriteCode(t, code)) { noRewrite.Add(site); rewriteFailed++; return false; }
        trampNext += 160;

        bool ok;
        if (len + reloc.Length >= 5)
        {
            int total = len + reloc.Length;
            var patch = new byte[total]; Array.Copy(Jmp32(site, t), patch, 5);
            for (int i = 5; i < total; i++) patch[i] = 0x90;
            ok = WriteCode(site, patch);
        }
        else
        {
            ok = WriteCode(cave, Jmp32(cave, t));
            var patch = new byte[len]; patch[0] = 0xEB; patch[1] = (byte)(sbyte)(cave - (site + 2));
            for (int i = 2; i < len; i++) patch[i] = 0x90;
            ok = ok && WriteCode(site, patch);
        }
        noRewrite.Add(site); if (ok) rewritten++; else rewriteFailed++; return ok;
    }

    static byte[] Pad(byte[] m) { var r = new byte[8]; Array.Copy(m, r, m.Length); return r; }
    static bool Finish(IntPtr hThread, long rip, int len)
    {
        Marshal.WriteInt64(ctx, OFF_RIP, rip + len);
        if (!SetThreadContext(hThread, ctx)) { Log("SetThreadContext failed " + Marshal.GetLastWin32Error()); return false; }
        return true;
    }

    static IntPtr ThreadHandle(int tid)
    {
        IntPtr h;
        if (threads.TryGetValue(tid, out h)) return h;
        h = OpenThread(0x0008 | 0x0010 | 0x0040, false, tid); // GET_CONTEXT | SET_CONTEXT | QUERY_INFORMATION
        if (h == IntPtr.Zero) h = OpenThread(0x1FFFFF, false, tid);
        threads[tid] = h; return h;
    }

    static void PatchCheck(long imageBase, long jrva, int jlen)
    {
        IntPtr at = new IntPtr(imageBase + jrva);
        byte[] cur = ReadMem(imageBase + jrva, jlen);
        bool ok = cur != null && ((jlen == 6 && cur[0] == 0x0F && cur[1] == 0x84) || (jlen == 2 && cur[0] == 0x74));
        if (!ok) { Log("check: unexpected bytes at patch site, not patching"); return; }
        byte[] nop = jlen == 6 ? new byte[] { 0x66, 0x0F, 0x1F, 0x44, 0x00, 0x00 } : new byte[] { 0x66, 0x90 };
        uint old; IntPtr wr;
        VirtualProtectEx(hProc, at, new IntPtr(jlen), 0x40, out old);
        bool w = WriteProcessMemory(hProc, at, nop, new IntPtr(jlen), out wr);
        VirtualProtectEx(hProc, at, new IntPtr(jlen), old, out old);
        FlushInstructionCache(hProc, at, new IntPtr(jlen));
        Log("check: patched in memory: " + w);
    }

    static int Main()
    {
        string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string dir = Path.GetDirectoryName(self);
        string ini = Path.Combine(dir, "claude-trap.ini");
        if (!File.Exists(ini)) ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-trap", "claude-trap.ini");
        string realName = Setting("CLAUDE_TRAP_REAL", "real", "claude-real.exe", ini);
        string real = Path.IsPathRooted(realName) ? realName : Path.Combine(dir, realName);
        bool verbose = Setting("CLAUDE_TRAP_VERBOSE", "verbose", "0", ini) == "1";
        rewrite = Setting("CLAUDE_TRAP_REWRITE", "rewrite", "1", ini) != "0";

        string cmd = Q(real); string rest = RawArgs(); if (rest.Length > 0) cmd += " " + rest;
        Log("START " + cmd);
        if (!File.Exists(real)) { Log("ERROR real exe missing: " + real); Console.Error.WriteLine("claude-trap: missing " + real); return 127; }

        long jrva = 0; int jlen = 0;
        try { FindCheck(real, out jrva, out jlen); } catch (Exception e) { Log("check: scan failed " + e.Message); }

        SetConsoleCtrlHandler(IntPtr.Zero, true);
        // Debugged processes get the slow NT debug heap unless this is set.
        Environment.SetEnvironmentVariable("_NO_DEBUG_HEAP", "1");
        // simdutf finds no usable implementation on pre-SSE4.2 CPUs and silently returns empty
        // results; force its SSE4.2 kernels and let the emulator handle the few missing opcodes.
        Environment.SetEnvironmentVariable("SIMDUTF_FORCE_IMPLEMENTATION", Setting("SIMDUTF_FORCE_IMPLEMENTATION", "simdutf", "westmere", ini));

        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.dwFlags = 0x100;
        si.hStdInput = GetStdHandle(-10); si.hStdOutput = GetStdHandle(-11); si.hStdError = GetStdHandle(-12);
        foreach (IntPtr h in new[] { si.hStdInput, si.hStdOutput, si.hStdError })
            if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, 1, 1);

        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
        if (job != IntPtr.Zero) SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));

        PROCESS_INFORMATION pi;
        // DEBUG_ONLY_THIS_PROCESS | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT
        if (!CreateProcess(null, new StringBuilder(cmd, 32768), IntPtr.Zero, IntPtr.Zero, true,
                0x2 | 0x4 | 0x400, IntPtr.Zero, null, ref si, out pi))
        {
            int err = Marshal.GetLastWin32Error();
            Log("ERROR CreateProcess " + err); Console.Error.WriteLine("claude-trap: CreateProcess failed, error " + err); return 126;
        }
        DebugSetProcessKillOnExit(true);
        if (job != IntPtr.Zero && !AssignProcessToJobObject(job, pi.hProcess)) Log("WARN AssignProcessToJobObject " + Marshal.GetLastWin32Error());
        hProc = pi.hProcess;
        threads[pi.dwThreadId] = pi.hThread;
        ctxRaw = Marshal.AllocHGlobal(0x4D0 + 64);
        ctx = new IntPtr((ctxRaw.ToInt64() + 15) & ~15L);
        ResumeThread(pi.hThread);

        IntPtr ev = Marshal.AllocHGlobal(256);
        uint exitCode = 1; bool firstBp = true; int otherLogged = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (!WaitForDebugEvent(ev, 0xFFFFFFFF)) { Log("WaitForDebugEvent failed " + Marshal.GetLastWin32Error()); break; }
            int code = Marshal.ReadInt32(ev, 0), pid = Marshal.ReadInt32(ev, 4), tid = Marshal.ReadInt32(ev, 8);
            uint status = DBG_CONTINUE;
            switch (code)
            {
                case 1: // EXCEPTION
                {
                    uint exc = (uint)Marshal.ReadInt32(ev, 16);
                    long addr = Marshal.ReadInt64(ev, 32);
                    bool first = Marshal.ReadInt32(ev, 168) != 0;
                    if (exc == 0x80000003 && firstBp) { firstBp = false; break; }
                    if (exc == 0xC000001D && first)
                    {
                        traceNext = verbose && !hits.ContainsKey(addr);
                        if (Emulate(ThreadHandle(tid), addr))
                        {
                            emulated++; long n; hits.TryGetValue(addr, out n); hits[addr] = n + 1;
                            if (n == 0) { byte[] ib = ReadMem(addr, 12); siteBytes[addr] = ib == null ? "?" : BitConverter.ToString(ib); }
                            if (verbose && n == 0) Log(string.Format("emulated new site {0:X}", addr));
                            break;
                        }
                        unknown++;
                        byte[] bytes = ReadMem(addr, 16);
                        Log(string.Format("UNHANDLED #UD at {0:X}: {1}", addr, bytes == null ? "?" : BitConverter.ToString(bytes)));
                    }
                    else if (otherLogged < 20 && (verbose || !first))
                    { otherLogged++; Log(string.Format("exception {0:X8} at {1:X} first={2}", exc, addr, first)); }
                    status = DBG_EXCEPTION_NOT_HANDLED;
                    break;
                }
                case 2: threads[tid] = Marshal.ReadIntPtr(ev, 16); break; // CREATE_THREAD
                case 3: // CREATE_PROCESS
                {
                    IntPtr hf = Marshal.ReadIntPtr(ev, 16); if (hf != IntPtr.Zero) CloseHandle(hf);
                    long imageBase = Marshal.ReadInt64(ev, 40);
                    if (jrva != 0) PatchCheck(imageBase, jrva, jlen);
                    InitImage(imageBase);
                    break;
                }
                case 4: threads.Remove(tid); break; // EXIT_THREAD (handle owned by system)
                case 5: exitCode = (uint)Marshal.ReadInt32(ev, 16); break;
                case 6: { IntPtr hf = Marshal.ReadIntPtr(ev, 16); if (hf != IntPtr.Zero) CloseHandle(hf); break; }
            }
            ContinueDebugEvent(pid, tid, status);
            if (code == 5) break;
        }
        var top = new List<KeyValuePair<long, long>>(hits); top.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = new StringBuilder();
        for (int i = 0; i < top.Count && i < 12; i++) sb.AppendFormat("\n   {0:X} x{1}  {2}", top[i].Key, top[i].Value, siteBytes.ContainsKey(top[i].Key) ? siteBytes[top[i].Key] : "");
        Log(string.Format("EXIT {0} after {1:F1}s, emulated {2} ({3} sites), unhandled {4}, rewritten {5} (failed {6}); top:{7}",
            exitCode, sw.Elapsed.TotalSeconds, emulated, hits.Count, unknown, rewritten, rewriteFailed, sb));
        return unchecked((int)exitCode);
    }
}
