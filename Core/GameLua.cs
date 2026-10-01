using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XorWoWLauncher.Core
{
    /// <summary>
    /// Reads a global variable of the running game's Lua from outside (ReadProcessMemory on the
    /// player's own process, like GameLogin) - how the radio button tells the launcher what it wants
    /// without any visible trace, in any window mode, minimized or covered.
    ///
    /// Wow.exe 3.3.5a (12340) only. Measured on the live client (2026-09-30):
    ///   [0x00D3F78C]            the lua_State (type tag 8 at +8)
    ///   lua_State + 0x48        l_gt, the globals table (a TValue: pointer, then its type at +8)
    ///   Table + 11              lsizenode (log2 of the hash part), Table + 20 the node array
    ///   Node, 40 bytes          +0 value (8), +8 value type, +16 key (8), +24 key type, +32 next
    ///   TString                 +8 type 4, +12 hash, +16 length, +20 the characters
    /// Blizzard's Lua 5.1 adds a 4-byte field to every object header and a taint word to every value,
    /// hence the offsets; the string hash is stock Lua's (luaS_newlstr), so a name is found at its main
    /// position plus a few hops along the chain - a handful of small reads, every time from the
    /// lua_State on, since a /reload or a trip through the character screen builds a new one.
    /// </summary>
    public sealed class GameLua : IDisposable
    {
        const uint LuaStateAddress = 0x00D3F78C;
        const int TypeNumber = 3, TypeString = 4, TypeTable = 5, TypeThread = 8;
        const int NodeSize = 40;

        readonly IntPtr _process;

        public GameLua(int pid)
        {
            _process = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, pid);
        }

        public bool Open => _process != IntPtr.Zero;

        /// <summary>The global's value when it is a number; null when it is missing, another type, or unreadable.</summary>
        public double? Number(string name)
        {
            try
            {
                var value = Find(name);
                if (value == null || BitConverter.ToInt32(value, 8) != TypeNumber) return null;
                return BitConverter.ToDouble(value, 0);
            }
            catch { return null; }   // the game changing its state under us: next time
        }

        /// <summary>The 40-byte node holding the global, or null.</summary>
        byte[] Find(string name)
        {
            var state = U32(LuaStateAddress);
            if (state == 0 || Byte(state + 8) != TypeThread) return null;
            var gt = Read(state + 0x48, 12);
            if (gt == null || BitConverter.ToInt32(gt, 8) != TypeTable) return null;
            var table = Read(BitConverter.ToUInt32(gt, 0), 24);
            if (table == null || table[8] != TypeTable) return null;
            int size = 1 << table[11];
            uint nodes = BitConverter.ToUInt32(table, 20);

            var bytes = Encoding.ASCII.GetBytes(name);
            uint node = nodes + (Hash(bytes) & (uint)(size - 1)) * NodeSize;
            for (int hops = 0; node != 0 && hops < 64; hops++)
            {
                var n = Read(node, NodeSize);
                if (n == null) return null;
                if (BitConverter.ToInt32(n, 24) == TypeString && KeyIs(BitConverter.ToUInt32(n, 16), bytes)) return n;
                node = BitConverter.ToUInt32(n, 32);
            }
            return null;
        }

        bool KeyIs(uint tstring, byte[] name)
        {
            var header = Read(tstring, 20);
            if (header == null || header[8] != TypeString || BitConverter.ToInt32(header, 16) != name.Length) return false;
            var text = Read(tstring + 20, name.Length);
            if (text == null) return false;
            for (int i = 0; i < name.Length; i++) if (text[i] != name[i]) return false;
            return true;
        }

        // lstring.c, luaS_newlstr
        static uint Hash(byte[] s)
        {
            uint h = (uint)s.Length;
            int step = (s.Length >> 5) + 1;
            for (int l1 = s.Length; l1 >= step; l1 -= step) h ^= (h << 5) + (h >> 2) + s[l1 - 1];
            return h;
        }

        byte[] Read(uint address, int size)
        {
            if (address == 0) return null;
            var buffer = new byte[size];
            return ReadProcessMemory(_process, new IntPtr(address), buffer, size, out var read) && read.ToInt64() == size ? buffer : null;
        }

        uint U32(uint address) { var b = Read(address, 4); return b == null ? 0 : BitConverter.ToUInt32(b, 0); }
        int Byte(uint address) { var b = Read(address, 1); return b == null ? -1 : b[0]; }

        public void Dispose()
        {
            if (_process != IntPtr.Zero) CloseHandle(_process);
        }

        const int ProcessVmRead = 0x0010, ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
