using UnityEngine;
using UnityEngine.InputSystem;

namespace StacklandsCoop
{
    /// <summary>F8 co-op window, remote cursors and the host's cutscene text (IMGUI, no game UI assets).</summary>
    public class CoopUI : MonoBehaviour
    {
        bool open;
        Rect win = new Rect(20, 80, 360, 300);
        string ip = "127.0.0.1";
        string port = "27777";
        static readonly Color[] colors = { new Color(1f, .45f, .3f), new Color(.3f, .7f, 1f), new Color(.5f, .9f, .4f), new Color(1f, .8f, .2f), new Color(.8f, .5f, 1f) };

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f8Key.wasPressedThisFrame) open = !open;
        }

        void OnGUI()
        {
            var s = CoopSession.I;
            if (s == null) return;
            GUI.depth = -1000;
            DrawCursors(s);
            DrawBanner(s);
            if (open) win = GUILayout.Window(0x5c00b, win, Window, "Stacklands Co-op  (F8)");
            else if (CoopSession.Role != Role.None)
                GUI.Label(new Rect(10, Screen.height - 26, 600, 24), "Co-op: " + s.Status + "  (F8)");
        }

        void Window(int id)
        {
            var s = CoopSession.I;
            GUILayout.Label("Status: " + s.Status);
            if (!string.IsNullOrEmpty(s.LastError)) GUILayout.Label("<color=#ff9080>" + s.LastError + "</color>");
            if (s.Roster.Count > 0) GUILayout.Label("Players: " + string.Join(", ", s.Roster));
            GUILayout.Space(6);
            if (CoopSession.Role == Role.None)
            {
                GUILayout.Label("Host: start/continue a run, then pick one:");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Host (Steam friends)")) { s.LastError = ""; s.StartHostSteam(); }
                if (GUILayout.Button("Host (LAN/IP)")) { s.LastError = ""; s.StartHostTcp(ParsePort()); }
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
                GUILayout.Label("Join (from the main menu): accept a Steam invite, or");
                GUILayout.BeginHorizontal();
                ip = GUILayout.TextField(ip, GUILayout.Width(170));
                port = GUILayout.TextField(port, GUILayout.Width(60));
                if (GUILayout.Button("Join IP")) { s.LastError = ""; s.JoinTcp(ip.Trim(), ParsePort()); }
                GUILayout.EndHorizontal();
            }
            else
            {
                if (s.Net != null) GUILayout.Label(s.Net.Describe());
                if (CoopSession.IsHost && s.Net is SteamTransport st && GUILayout.Button("Invite Steam friends")) st.InviteFriends();
                if (CoopSession.IsClient && s.ClientInGame && GUILayout.Button("Resync")) s.ClientRequestFull();
                if (GUILayout.Button(CoopSession.IsHost ? "Stop hosting" : "Leave")) s.Stop();
            }
            GUILayout.Space(4);
            GUILayout.Label("<size=11>Host's game is the real one; joiners' own saves are untouched.</size>");
            GUI.DragWindow();
        }

        int ParsePort() => int.TryParse(port, out int p) ? p : 27777;

        void DrawCursors(CoopSession s)
        {
            var cam = Camera.main;
            if (cam == null || CoopSession.Role == Role.None || WorldManager.instance == null || WorldManager.instance.CurrentGameState == WorldManager.GameState.InMenu) return;
            if (CoopSession.IsClient && !s.ClientInGame) return;
            foreach (var p in s.Peers.Values)
            {
                if (CoopSession.IsClient && p.Id == s.MyIndex) continue;
                if (Time.unscaledTime - p.CursorTime > 3f) continue;
                var sp = cam.WorldToScreenPoint(p.Cursor);
                if (sp.z < 0) continue;
                var c = colors[Mathf.Abs(p.Id) % colors.Length];
                var old = GUI.color;
                GUI.color = c;
                var y = Screen.height - sp.y;
                GUI.Box(new Rect(sp.x - 5, y - 5, 10, 10), GUIContent.none);
                GUI.Label(new Rect(sp.x + 8, y - 4, 200, 22), "<b>" + p.Name + "</b>");
                GUI.color = old;
            }
        }

        void DrawBanner(CoopSession s)
        {
            if (!CoopSession.IsClient || !s.ClientInGame || s.LastGlobals == null) return;
            var g = s.LastGlobals;
            string text = null;
            if (!string.IsNullOrEmpty(g.CutsceneTitle) || !string.IsNullOrEmpty(g.CutsceneText))
                text = "<b>" + g.CutsceneTitle + "</b>\n" + g.CutsceneText;
            else if (!g.CanInteract)
                text = "Waiting for the host...";
            if (text == null && !g.ShowContinue) return;
            var r = new Rect(Screen.width / 2 - 300, 60, 600, 170);
            GUI.Box(r, GUIContent.none);
            var st = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, richText = true, fontSize = 16 };
            GUI.Label(new Rect(r.x + 10, r.y + 5, r.width - 20, 120), text ?? "", st);
            if (!g.ShowContinue) return;
            // anyone can press the cutscene buttons; the host's game acts on the first press
            var opts = g.Options ?? new System.Collections.Generic.List<string>();
            if (opts.Count == 0)
            {
                if (GUI.Button(new Rect(r.x + r.width / 2 - 90, r.y + 130, 180, 30), string.IsNullOrEmpty(g.ContinueText) ? "Continue" : g.ContinueText)) s.ClientContinue(-1);
            }
            else
            {
                float w = (r.width - 20) / opts.Count;
                for (int i = 0; i < opts.Count; i++)
                    if (GUI.Button(new Rect(r.x + 10 + i * w, r.y + 130, w - 6, 30), opts[i])) s.ClientContinue(i);
            }
        }
    }
}
