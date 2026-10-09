using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MW2RoR2
{
    /// Finds the player's MW2 (2009) common_mp.ff through Steam when the configured path doesn't
    /// exist: every Steam library (the Steam install, libraryfolders.vdf's entries, the library RoR2
    /// itself is in), MW2 multiplayer's or campaign's app manifest there, and the install's zone
    /// language (english first). Mod-manager installs never ran the friends' installer, which did
    /// this. Plain .NET (no Unity) so it can be checked outside the game.
    static class Mw2Locate
    {
        static readonly string[] Apps = { "10190", "10180" }; // MW2 multiplayer, then campaign (same folder)

        /// The first common_mp.ff found, or null.
        public static string Find(IEnumerable<string> steamRoots, IEnumerable<string> extraLibraries)
        {
            var libraries = new List<string>();
            foreach (var root in steamRoots.Where(r => !string.IsNullOrEmpty(r)))
                foreach (var lib in Libraries(root))
                    Add(libraries, lib);
            foreach (var lib in extraLibraries.Where(l => !string.IsNullOrEmpty(l)))
                Add(libraries, lib);
            foreach (var app in Apps)
                foreach (var lib in libraries)
                {
                    var found = InLibrary(lib, app);
                    if (found != null) return found;
                }
            return null;
        }

        static void Add(List<string> list, string dir)
        {
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\', '/'); }
            catch (Exception) { return; }
            if (!list.Any(d => string.Equals(d, full, StringComparison.OrdinalIgnoreCase))) list.Add(full);
        }

        /// A Steam install's own library plus the ones its libraryfolders.vdf lists.
        public static List<string> Libraries(string steamRoot)
        {
            var libs = new List<string> { steamRoot };
            try
            {
                var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (Exception) { }
            return libs;
        }

        /// The app's common_mp.ff in one library, if its manifest and files are there.
        public static string InLibrary(string library, string app)
        {
            try
            {
                var acf = Path.Combine(library, "steamapps", "appmanifest_" + app + ".acf");
                if (!File.Exists(acf)) return null;
                var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                if (!m.Success) return null;
                return CommonMp(Path.Combine(library, "steamapps", "common", m.Groups[1].Value));
            }
            catch (Exception) { return null; }
        }

        /// <mw2>/zone/<language>/common_mp.ff: english first, then any other language folder.
        public static string CommonMp(string mw2)
        {
            var zone = Path.Combine(mw2, "zone");
            if (!Directory.Exists(zone)) return null;
            var english = Path.Combine(zone, "english", "common_mp.ff");
            if (File.Exists(english)) return english;
            foreach (var dir in Directory.GetDirectories(zone).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var ff = Path.Combine(dir, "common_mp.ff");
                if (File.Exists(ff)) return ff;
            }
            return null;
        }

        /// Steam's install folder from the registry (HKCU\Software\Valve\Steam, SteamPath), then
        /// Steam's default folder.
        public static IEnumerable<string> SteamRoots()
        {
            var reg = RegistrySteamPath();
            if (!string.IsNullOrEmpty(reg)) yield return reg.Replace('/', '\\');
            yield return @"C:\Program Files (x86)\Steam";
            yield return @"C:\Program Files\Steam";
        }

        /// The library a game folder sits in: <library>\steamapps\common\<game>.
        public static string LibraryOf(string gameFolder)
        {
            try
            {
                var common = Directory.GetParent(gameFolder);
                var steamapps = common?.Parent;
                return steamapps != null && string.Equals(steamapps.Name, "steamapps", StringComparison.OrdinalIgnoreCase)
                    ? steamapps.Parent?.FullName : null;
            }
            catch (Exception) { return null; }
        }

        static readonly UIntPtr HKEY_CURRENT_USER = new UIntPtr(0x80000001u);
        const uint RRF_RT_REG_SZ = 0x2;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegGetValueW(UIntPtr hkey, string subKey, string value, uint flags, IntPtr type, StringBuilder data, ref uint size);

        static string RegistrySteamPath()
        {
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)(buffer.Capacity * 2);
                return RegGetValueW(HKEY_CURRENT_USER, @"Software\Valve\Steam", "SteamPath", RRF_RT_REG_SZ, IntPtr.Zero, buffer, ref size) == 0
                    ? buffer.ToString() : null;
            }
            catch (Exception) { return null; }
        }
    }
}
