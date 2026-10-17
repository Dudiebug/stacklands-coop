using System;
using System.IO;
using UnityEngine;

namespace StacklandsCoop
{
    /// <summary>The game turns Unity logging off in release builds, so the mod keeps its own log file.</summary>
    public static class CoopLog
    {
        static string path;
        static readonly object gate = new object();

        static string LogPath
        {
            get
            {
                if (path == null)
                {
                    try { path = Path.Combine(Application.persistentDataPath, "coop_log.txt"); File.WriteAllText(path, ""); }
                    catch { path = ""; }
                }
                return path;
            }
        }

        static void Write(string level, string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + s;
            try { Debug.Log("[Coop] " + s); } catch { }
            lock (gate)
            {
                try { if (LogPath != "") File.AppendAllText(LogPath, line + "\n"); } catch { }
            }
        }

        public static void Info(string s) => Write("INFO", s);
        public static void Warn(string s) => Write("WARN", s);
    }
}
