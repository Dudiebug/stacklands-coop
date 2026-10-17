using System;
using UnityEngine;

namespace StacklandsCoop
{
    public class CoopMod : Mod
    {
        static bool started;

        public override void Ready()
        {
            if (started) return; // the game calls Ready() more than once
            started = true;
            CoopLog.Info("Stacklands Co-op " + Manifest.Version + " starting");
            Application.runInBackground = true; // an alt-tabbed host must keep simulating for everyone
            Harmony.PatchAll(typeof(CoopMod).Assembly);
            var go = new GameObject("StacklandsCoop");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var session = go.AddComponent<CoopSession>();
            go.AddComponent<CoopUI>();
            string test = Environment.GetEnvironmentVariable("COOP_TEST");
            if (!string.IsNullOrEmpty(test)) go.AddComponent<TestBot>().Mode = test;
            Logger.Log("Stacklands Co-op loaded. Press F8 for the co-op menu.");

            // Steam "Join game" / invite accept launches the game with +connect_lobby <id>
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong lobby))
                    session.PendingLobby = lobby;
        }
    }
}
