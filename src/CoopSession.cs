using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace StacklandsCoop
{
    public enum Role { None, Host, Client }

    public class PeerInfo
    {
        public int Id;
        public string Name = "?";
        public bool Synced;
        public Vector3 Cursor;
        public float CursorTime;
        public Draggable Dragging;
    }

    /// <summary>
    /// Host-authoritative co-op. The host runs the real simulation and streams card state;
    /// clients freeze their own simulation, mirror the host's cards by UniqueId and forward
    /// their drags, drops, clicks and sells to the host.
    /// </summary>
    public class CoopSession : MonoBehaviour
    {
        public static CoopSession I;
        public static Role Role = Role.None;
        /// <summary>True while the mod itself is changing world state (lets blocked game calls through on clients).</summary>
        public static bool Applying;
        public static bool IsClient => Role == Role.Client;
        public static bool IsHost => Role == Role.Host;
        public const string ClientSaveId = "coop_client_mirror";

        public ITransport Net;
        public string Status = "Not connected";
        public string LastError = "";
        public readonly Dictionary<int, PeerInfo> Peers = new Dictionary<int, PeerInfo>();
        public string MyName = "Player";
        public int MyIndex;
        public CoopGlobals LastGlobals;
        public int DeltasReceived;
        public int LastSeq;

        const float TickRate = 1f / 15f;
        const float KeyframeEvery = 1f;
        const float CursorRate = 0.1f;
        const float DragRate = 0.05f;

        float tickTimer, keyframeTimer, cursorTimer, dragTimer, lastErrLog = -10f;
        int seq;

        // host state
        readonly Dictionary<string, string> lastSentCard = new Dictionary<string, string>();
        readonly Dictionary<Boosterpack, int> boosterIds = new Dictionary<Boosterpack, int>();
        int nextBoosterId = 1;
        int progressHash;
        bool hostWasInGame;

        // client state
        readonly Dictionary<int, Boosterpack> clientBoosters = new Dictionary<int, Boosterpack>();
        public bool ClientInGame;
        float lastAppliedSpeed = 1f;
        float speedOverrideUntil;
        public static bool HostCanInteract = true;
        /// <summary>Answers of the multiple-choice cutscene currently on the host screen (filled by a patch).</summary>
        public static readonly List<string> CutsceneOptions = new List<string>();
        public ulong PendingLobby;
        Draggable lastLocalDrag;

        static AccessTools.FieldRef<WorldManager, bool> loadingRef = AccessTools.FieldRefAccess<WorldManager, bool>("IsLoadingSaveRound");
        static readonly System.Reflection.MethodInfo setCurrentBoard = AccessTools.PropertySetter(typeof(WorldManager), "CurrentBoard");
        static readonly System.Reflection.MethodInfo dropCardMethod = AccessTools.Method(typeof(WorldManager), "DropCard");

        WorldManager WM => WorldManager.instance;

        void Awake()
        {
            I = this;
            try { if (SteamManager.Initialized) MyName = Steamworks.SteamFriends.GetPersonaName(); } catch { }
        }

        // ------------------------------------------------------------ lifecycle

        public void StartHostTcp(int port)
        {
            Stop();
            try
            {
                Net = TcpTransport.Host(port);
                BecomeHost();
            }
            catch (Exception e) { Fail("Could not host: " + e.Message); }
        }

        public void StartHostSteam()
        {
            Stop();
            if (!SteamManager.Initialized) { Fail("Steam is not running"); return; }
            Net = SteamTransport.Host(8);
            BecomeHost();
        }

        void BecomeHost()
        {
            Role = Role.Host;
            lastSentCard.Clear();
            hostWasInGame = false;
            Status = "Hosting";
            CoopLog.Info("hosting: " + Net.Describe());
        }

        public void JoinTcp(string host, int port)
        {
            if (!InMainMenu()) { Fail("Go back to the main menu before joining (your run is saved)."); return; }
            Stop();
            Net = TcpTransport.Connect(host, port);
            BecomeClient();
        }

        public void JoinSteam(Steamworks.CSteamID lobby)
        {
            if (!InMainMenu()) { Fail("Go back to the main menu before joining (your run is saved)."); return; }
            Stop();
            Net = SteamTransport.Join(lobby);
            BecomeClient();
        }

        void BecomeClient()
        {
            Role = Role.Client;
            ClientInGame = false;
            clientBoosters.Clear();
            Status = "Connecting...";
        }

        public static bool InMainMenu() => WorldManager.instance != null && WorldManager.instance.CurrentGameState == WorldManager.GameState.InMenu;

        public void Stop()
        {
            bool wasClientInGame = IsClient && ClientInGame;
            Net?.Shutdown();
            Net = null;
            Role = Role.None;
            Peers.Clear();
            ClientInGame = false;
            Status = "Not connected";
            if (wasClientInGame) LeaveMirroredGame();
        }

        void Fail(string msg)
        {
            LastError = msg;
            CoopLog.Warn(msg);
        }

        void LeaveMirroredGame()
        {
            // The mirrored run is never written to disk; going back to the menu reloads the player's own save.
            Applying = true;
            try { WorldManager.RestartGame(); }
            catch (Exception e) { CoopLog.Warn("restart failed: " + e); }
            finally { Applying = false; }
        }

        void OnApplicationQuit() => Net?.Shutdown();

        // ------------------------------------------------------------ main loop

        void Update()
        {
            if (PendingLobby != 0 && InMainMenu() && !TransitionScreen.InTransition)
            {
                var lobby = PendingLobby;
                PendingLobby = 0;
                if (SteamManager.Initialized) JoinSteam(new Steamworks.CSteamID(lobby));
            }
            KeepRunningInBackground();
            if (Net == null) return;
            if (IsClient && ClientInGame) CheckLocalSpeed(); // before applying host state, which overwrites SpeedUp
            while (Net != null && Net.Poll(out var ev))
            {
                try { Handle(ev); }
                catch (Exception e) { CoopLog.Warn("error handling " + ev.Type + ": " + e); }
            }
            if (Net == null) return;
            float dt = Time.unscaledDeltaTime;
            try
            {
                if (IsHost) HostUpdate(dt);
                else ClientUpdate(dt);
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - lastErrLog > 2f) { lastErrLog = Time.unscaledTime; CoopLog.Warn("update error: " + e); }
            }
        }

        void Handle(NetEvent ev)
        {
            switch (ev.Type)
            {
                case NetEventType.Connected:
                    if (IsHost)
                    {
                        Peers[ev.Peer] = new PeerInfo { Id = ev.Peer };
                        CoopLog.Info("peer " + ev.Peer + " connected");
                    }
                    else
                    {
                        Status = "Connected, waiting for host...";
                        Send(0, Protocol.Build(Msg.Hello, w =>
                        {
                            w.Write(Protocol.Version);
                            w.WriteStr(MyName);
                            w.WriteStr(ModList());
                        }));
                    }
                    break;
                case NetEventType.Disconnected:
                    if (IsHost)
                    {
                        if (Peers.TryGetValue(ev.Peer, out var p))
                        {
                            CoopLog.Info(p.Name + " left");
                            if (p.Dragging != null) ReleaseRemoteDrag(p);
                        }
                        Peers.Remove(ev.Peer);
                        BroadcastRoster();
                    }
                    else
                    {
                        Fail("Disconnected from host");
                        Stop();
                    }
                    break;
                case NetEventType.Data:
                    using (var r = new BinaryReader(new MemoryStream(ev.Data), Encoding.UTF8))
                    {
                        var type = (Msg)r.ReadByte();
                        if (IsHost) HostReceive(ev.Peer, type, r);
                        else ClientReceive(type, r);
                    }
                    break;
            }
        }

        public void Send(int peer, byte[] data) => Net?.Send(peer, data);

        void BroadcastSynced(byte[] data)
        {
            foreach (var p in Peers.Values)
                if (p.Synced) Send(p.Id, data);
        }

        public static string ModList() =>
            ModManager.LoadedMods == null ? "" :
            string.Join(",", ModManager.LoadedMods.Where(m => m.Manifest != null).Select(m => m.Manifest.Id + "@" + m.Manifest.Version).OrderBy(s => s));

        // ============================================================ HOST

        bool HostInGame()
        {
            var wm = WM;
            return wm != null && wm.CurrentBoard != null && wm.CurrentGameState != WorldManager.GameState.InMenu
                && wm.CurrentRunOptions != null;
        }

        void HostUpdate(float dt)
        {
            bool inGame = HostInGame();
            if (!inGame)
            {
                if (hostWasInGame)
                {
                    foreach (var p in Peers.Values) { p.Synced = false; Send(p.Id, Protocol.Build(Msg.HostInMenu)); }
                    lastSentCard.Clear();
                }
                hostWasInGame = false;
                Status = "Hosting - start or continue a run";
                return;
            }
            hostWasInGame = true;
            Status = "Hosting - " + Peers.Count + " friend(s) connected";

            foreach (var p in Peers.Values)
                if (!p.Synced && p.Name != "?") SendFull(p);

            // keep remotely dragged cards following their cursor
            foreach (var p in Peers.Values)
                if (p.Dragging != null && (p.Dragging is GameCard gc && gc.Destroyed)) p.Dragging = null;

            tickTimer += dt;
            keyframeTimer += dt;
            if (tickTimer >= TickRate)
            {
                tickTimer = 0f;
                bool key = keyframeTimer >= KeyframeEvery;
                if (key) keyframeTimer = 0f;
                if (Peers.Values.Any(p => p.Synced)) SendDelta(key);
            }

            cursorTimer += dt;
            if (cursorTimer >= CursorRate)
            {
                cursorTimer = 0f;
                var pos = WM.mouseWorldPosition;
                BroadcastSynced(Protocol.Build(Msg.Cursor, w => { w.Write(0); w.WriteVec(pos); }));
            }
        }

        public void SendFull(PeerInfo p)
        {
            var wm = WM;
            var save = wm.CurrentSave;
            var prev = save.LastPlayedRound;
            string json;
            try
            {
                var round = wm.GetSaveRound();
                round.SavedBoosters = new List<SavedBooster>(); // boosters travel with net ids in the globals
                save.LastPlayedRound = round;
                json = JsonUtility.ToJson(save);
            }
            finally { save.LastPlayedRound = prev; }
            var full = new FullPayload { SaveJson = json, GJson = JsonUtility.ToJson(BuildGlobals(true)) };
            var bytes = Protocol.Gzip(JsonUtility.ToJson(full));
            Send(p.Id, Protocol.Build(Msg.Full, w => w.WriteBlob(bytes)));
            p.Synced = true;
            CoopLog.Info("sent full snapshot to " + p.Name + " (" + bytes.Length + " bytes, " + wm.AllCards.Count + " cards)");
            // the next delta must carry every card so the new player gets anything that changed meanwhile
            keyframeTimer = KeyframeEvery;
        }

        int BoosterNetId(Boosterpack b)
        {
            if (!boosterIds.TryGetValue(b, out int id))
            {
                id = nextBoosterId++;
                boosterIds[b] = id;
            }
            return id;
        }

        CoopGlobals BuildGlobals(bool forceProgress)
        {
            var wm = WM;
            var g = new CoopGlobals
            {
                MonthTimer = wm.MonthTimer,
                Months = wm.BoardMonths?.ToSavedMonth(),
                SpeedUp = wm.SpeedUp,
                BoardId = wm.CurrentBoard != null ? wm.CurrentBoard.Id : "main",
                GameState = (int)wm.CurrentGameState,
                CanInteract = wm.CanInteract || (wm.IsPlaying && !wm.InAnimation),
                CutsceneTitle = wm.InAnimation ? wm.CutsceneTitle : "",
                CutsceneText = wm.InAnimation ? wm.CutsceneText : "",
                QuestsCompleted = wm.QuestsCompleted,
                NewCardsFound = wm.NewCardsFound,
                CitiesWellbeing = CitiesManager.instance != null ? CitiesManager.instance.Wellbeing : 0,
                CardCount = wm.AllCards.Count,
                ShowContinue = wm.InAnimation && (wm.ShowContinueButton || CutsceneOptions.Count > 0),
                ContinueText = wm.ContinueButtonText,
                Options = new List<string>(CutsceneOptions),
                BoughtBoosterIds = wm.BoughtBoosterIds,
            };
            foreach (var box in wm.AllBoosterBoxes)
                g.Boxes.Add(new SavedBoosterBox { BoosterId = box.BoosterId, StoredCostAmount = box.StoredCostAmount });
            var dead = boosterIds.Keys.Where(b => b == null).ToList();
            foreach (var d in dead) boosterIds.Remove(d);
            foreach (var b in wm.AllBoosters)
            {
                if (b == null) continue;
                g.AddBooster(new NetBooster
                {
                    Id = BoosterNetId(b),
                    BoosterId = b.BoosterId,
                    Pos = b.TargetPosition,
                    TimesOpened = b.TimesOpened,
                    BoardId = b.MyBoard != null ? b.MyBoard.Id : "main",
                    Dragged = b.BeingDragged,
                });
            }
            var save = wm.CurrentSave;
            int h = save.CompletedAchievementIds.Count * 1000003 ^ save.FoundCardIds.Count * 7919 ^ save.FoundBoosterIds.Count;
            if (forceProgress || h != progressHash)
            {
                progressHash = h;
                g.HasProgress = true;
                g.Completed = save.CompletedAchievementIds;
                g.FoundCards = save.FoundCardIds;
                g.FoundBoosters = save.FoundBoosterIds;
            }
            return g;
        }

        void SendDelta(bool keyframe)
        {
            var wm = WM;
            var d = new DeltaPayload { Seq = ++seq, Keyframe = keyframe };
            var seen = new HashSet<string>();
            foreach (var card in wm.AllCards)
            {
                if (card == null || card.Destroyed || card.CardData == null) continue;
                SavedCard sc;
                try { sc = card.ToSavedCard(); }
                catch (Exception) { continue; }
                string js = JsonUtility.ToJson(sc);
                seen.Add(sc.UniqueId);
                if (keyframe || !lastSentCard.TryGetValue(sc.UniqueId, out var prev) || prev != js)
                {
                    d.Cards.Add(sc);
                    lastSentCard[sc.UniqueId] = js;
                }
            }
            foreach (var k in lastSentCard.Keys.ToList())
                if (!seen.Contains(k)) { d.Removed.Add(k); lastSentCard.Remove(k); }
            if (keyframe) d.All = seen.ToList();
            d.GJson = JsonUtility.ToJson(BuildGlobals(keyframe));
            var bytes = Protocol.Gzip(JsonUtility.ToJson(d));
            if (seq % 30 == 1) CoopLog.Info("send delta seq=" + seq + " cards=" + d.Cards.Count + " bytes=" + bytes.Length + " fps=" + (1f / Time.unscaledDeltaTime).ToString("0"));
            BroadcastSynced(Protocol.Build(Msg.Delta, w => w.WriteBlob(bytes)));
        }

        void BroadcastRoster()
        {
            var names = new List<string> { MyName };
            names.AddRange(Peers.Values.Select(p => p.Name));
            var data = Protocol.Build(Msg.Roster, w => { w.Write(names.Count); foreach (var n in names) w.WriteStr(n); });
            foreach (var p in Peers.Values) Send(p.Id, data);
        }

        Draggable FindObj(ObjKind kind, string id)
        {
            if (kind == ObjKind.Card) return WM.GetCardWithUniqueId(id);
            if (int.TryParse(id, out int bid))
                foreach (var kv in boosterIds)
                    if (kv.Value == bid && kv.Key != null) return kv.Key;
            return null;
        }

        void HostReceive(int peer, Msg type, BinaryReader r)
        {
            if (!Peers.TryGetValue(peer, out var p)) return;
            var wm = WM;
            switch (type)
            {
                case Msg.Hello:
                {
                    int ver = r.ReadInt32();
                    p.Name = r.ReadString();
                    string mods = r.ReadString();
                    if (ver != Protocol.Version)
                    {
                        Send(peer, Protocol.Build(Msg.Reject, w => w.WriteStr("Version mismatch: host has co-op protocol " + Protocol.Version + ", you have " + ver)));
                        return;
                    }
                    if (mods != ModList()) CoopLog.Warn(p.Name + " has different mods: " + mods + " vs host " + ModList());
                    Send(peer, Protocol.Build(Msg.Welcome, w => { w.Write(peer); w.WriteStr(MyName); w.WriteStr(ModList()); }));
                    BroadcastRoster();
                    CoopLog.Info(p.Name + " joined as player " + (peer + 1));
                    break;
                }
                case Msg.Continue:
                {
                    int idx = r.ReadInt32();
                    if (!wm.InAnimation) return;
                    if (idx >= 0) wm.ContinueButtonIndex = idx;
                    wm.ContinueClicked = true;
                    CoopLog.Info(p.Name + " pressed continue " + idx);
                    break;
                }
                case Msg.RequestFull:
                    p.Synced = false;
                    break;
                case Msg.Cursor:
                {
                    r.ReadInt32();
                    p.Cursor = r.ReadVec();
                    p.CursorTime = Time.unscaledTime;
                    var c = p.Cursor;
                    var relay = Protocol.Build(Msg.Cursor, w => { w.Write(peer); w.WriteVec(c); });
                    foreach (var o in Peers.Values) if (o.Id != peer && o.Synced) Send(o.Id, relay);
                    break;
                }
                case Msg.SetSpeed:
                    { float sp = r.ReadSingle(); CoopLog.Info("speed request " + sp); if (HostInGame()) wm.SpeedUp = Mathf.Clamp(sp, 0f, 5f); }
                    break;
                case Msg.DragStart:
                {
                    var kind = (ObjKind)r.ReadByte();
                    string id = r.ReadString();
                    var obj = FindObj(kind, id);
                    if (p.Dragging != null) ReleaseRemoteDrag(p);
                    if (obj == null || !HostAcceptsInput()) { CoopLog.Info("drag start rejected: obj=" + (obj != null) + " state=" + wm.CurrentGameState + " anim=" + wm.InAnimation + " transition=" + TransitionScreen.InTransition); return; }
                    if (obj == wm.DraggingDraggable || obj.BeingDragged || !obj.CanBeDragged()) { CoopLog.Info("drag start rejected: busy " + id); return; } // someone else has it
                    Applying = true;
                    try
                    {
                        obj.DragStartPosition = obj.transform.position;
                        obj.StartDragging();
                        p.Dragging = obj;
                    }
                    finally { Applying = false; }
                    break;
                }
                case Msg.DragMove:
                {
                    var kind = (ObjKind)r.ReadByte();
                    string id = r.ReadString();
                    var pos = r.ReadVec();
                    if (p.Dragging != null && p.Dragging == FindObj(kind, id))
                    {
                        p.Dragging.TargetPosition = pos;
                    }
                    break;
                }
                case Msg.Drop:
                {
                    var kind = (ObjKind)r.ReadByte();
                    string id = r.ReadString();
                    var pos = r.ReadVec();
                    string targetKey = r.ReadString();
                    var obj = FindObj(kind, id);
                    if (obj == null || p.Dragging != obj) { CoopLog.Info("drop ignored " + id + " obj=" + (obj != null) + " dragging=" + (p.Dragging != null)); p.Dragging = null; return; }
                    Applying = true;
                    try
                    {
                        obj.transform.position = pos;
                        obj.TargetPosition = pos;
                        if (obj is GameCard card && !card.Destroyed)
                        {
                            var target = FindTarget(targetKey);
                            CoopLog.Info("drop " + card.CardData.Id + " target=" + targetKey + " found=" + (target != null) + " ok=" + (target != null && target.CanHaveCard(card)));
                            if (target != null && target.CanHaveCard(card)) target.CardDropped(card);
                            else wm.CheckIfCanAddOnStack(card);
                        }
                        // the client sends clicks separately; make sure StopDragging doesn't count this as one
                        obj.DragStartPosition = pos + new Vector3(100f, 0f, 0f);
                        obj.StopDragging();
                    }
                    finally { Applying = false; p.Dragging = null; }
                    break;
                }
                case Msg.Click:
                {
                    var kind = (ObjKind)r.ReadByte();
                    string id = r.ReadString();
                    string tag = r.ReadString();
                    var obj = FindObj(kind, id);
                    if (obj == null || !HostAcceptsInput()) return;
                    Applying = true;
                    try
                    {
                        obj.DragTag = string.IsNullOrEmpty(tag) ? null : tag;
                        obj.Clicked();
                    }
                    finally { Applying = false; obj.DragTag = null; }
                    break;
                }
                case Msg.Sell:
                {
                    string id = r.ReadString();
                    var card = wm.GetCardWithUniqueId(id);
                    if (card == null || !HostAcceptsInput()) return;
                    Applying = true;
                    try
                    {
                        if (wm.CardCanBeSold(card)) wm.SellCard(card.transform.position, card.GetRootCard());
                    }
                    finally { Applying = false; }
                    break;
                }
            }
        }

        bool HostAcceptsInput()
        {
            var wm = WM;
            return HostInGame() && wm.IsPlaying && !wm.InAnimation && !TransitionScreen.InTransition;
        }

        void ReleaseRemoteDrag(PeerInfo p)
        {
            var obj = p.Dragging;
            p.Dragging = null;
            if (obj == null) return;
            Applying = true;
            try
            {
                if (obj is GameCard gc && !gc.Destroyed) WM.CheckIfCanAddOnStack(gc);
                obj.DragStartPosition = obj.transform.position + new Vector3(100f, 0f, 0f);
                obj.StopDragging();
            }
            catch (Exception e) { CoopLog.Warn("release drag: " + e.Message); }
            finally { Applying = false; }
        }

        public static string TargetKey(CardTarget t)
        {
            if (t == null) return "";
            if (t is BuyBoosterBox b) return "buy:" + b.BoosterId;
            var p = t.transform.position;
            return t.GetType().Name + ":" + Mathf.RoundToInt(p.x * 10f) + "," + Mathf.RoundToInt(p.z * 10f);
        }

        CardTarget FindTarget(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return WM.CardTargets.FirstOrDefault(t => t != null && TargetKey(t) == key);
        }

        // ============================================================ CLIENT

        void ClientUpdate(float dt)
        {
            if (!ClientInGame) return;
            var wm = WM;
            if (wm.CurrentGameState == WorldManager.GameState.InMenu)
            {
                // the player quit to the menu themselves
                Fail("Left the co-op game");
                Stop();
                return;
            }


            var dd = wm.DraggingDraggable;
            if (dd != lastLocalDrag)
            {
                // the game dropped it without going through DropCard (e.g. a modal opened)
                if (lastLocalDrag != null) ClientDropped(lastLocalDrag, null);
                if (dd != null) ClientDragStarted(dd);
            }
            dragTimer += dt;
            if (dd != null && dragTimer >= DragRate && ObjRef(dd, out var kind, out var id))
            {
                dragTimer = 0f;
                var pos = dd.TargetPosition;
                Send(0, Protocol.Build(Msg.DragMove, w => { w.Write((byte)kind); w.WriteStr(id); w.WriteVec(pos); }));
            }

            cursorTimer += dt;
            if (cursorTimer >= CursorRate)
            {
                cursorTimer = 0f;
                var pos = wm.mouseWorldPosition;
                Send(0, Protocol.Build(Msg.Cursor, w => { w.Write(MyIndex); w.WriteVec(pos); }));
            }
            Status = "Playing with " + (Peers.TryGetValue(0, out var h) ? h.Name : "host");
        }

        void CheckLocalSpeed()
        {
            // player toggled the time speed (space bar / speed buttons): tell the host
            var wm = WM;
            if (Mathf.Approximately(wm.SpeedUp, lastAppliedSpeed)) return;
            float s = wm.SpeedUp;
            lastAppliedSpeed = s;
            speedOverrideUntil = Time.unscaledTime + 0.75f;
            Send(0, Protocol.Build(Msg.SetSpeed, w => w.Write(s)));
        }

        bool vsyncOverridden;
        int savedVsync, savedFps;

        void KeepRunningInBackground()
        {
            // vsync stalls an unfocused window to a few fps under some compositors; the other players would stall with it
            bool want = !Application.isFocused && Role != Role.None;
            if (want)
            {
                if (!vsyncOverridden)
                {
                    vsyncOverridden = true;
                    savedVsync = QualitySettings.vSyncCount;
                    savedFps = Application.targetFrameRate;
                }
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }
            else if (!want && vsyncOverridden)
            {
                vsyncOverridden = false;
                QualitySettings.vSyncCount = savedVsync;
                Application.targetFrameRate = savedFps;
            }
        }

        public bool ObjRef(Draggable d, out ObjKind kind, out string id)
        {
            kind = ObjKind.Card;
            id = null;
            if (d is GameCard gc && gc.CardData != null) { id = gc.CardData.UniqueId; return true; }
            if (d is Boosterpack b)
            {
                foreach (var kv in clientBoosters)
                    if (kv.Value == b) { kind = ObjKind.Booster; id = kv.Key.ToString(); return true; }
            }
            return false;
        }

        // called from patches
        public void ClientDragStarted(Draggable d)
        {
            lastLocalDrag = d;
            ClientDragStartedImpl(d);
        }

        void ClientDragStartedImpl(Draggable d)
        {
            if (!ObjRef(d, out var kind, out var id)) return;
            dragTimer = 0f;
            Send(0, Protocol.Build(Msg.DragStart, w => { w.Write((byte)kind); w.WriteStr(id); }));
        }

        public void ClientDropped(Draggable d, CardTarget nearby)
        {
            lastLocalDrag = null;
            ClientDroppedImpl(d, nearby);
        }

        void ClientDroppedImpl(Draggable d, CardTarget nearby)
        {
            if (!ObjRef(d, out var kind, out var id)) return;
            var pos = d.transform.position;
            if (nearby != null) pos = d.TargetPosition;
            string key = TargetKey(nearby);
            Send(0, Protocol.Build(Msg.Drop, w => { w.Write((byte)kind); w.WriteStr(id); w.WriteVec(pos); w.WriteStr(key); }));
        }

        public void ClientClicked(Draggable d)
        {
            if (!ObjRef(d, out var kind, out var id)) return;
            string tag = d.DragTag;
            Send(0, Protocol.Build(Msg.Click, w => { w.Write((byte)kind); w.WriteStr(id); w.WriteStr(tag); }));
        }

        public void ClientSell(GameCard c)
        {
            if (c?.CardData == null) return;
            Send(0, Protocol.Build(Msg.Sell, w => w.WriteStr(c.CardData.UniqueId)));
        }

        void ClientReceive(Msg type, BinaryReader r)
        {
            switch (type)
            {
                case Msg.Welcome:
                {
                    MyIndex = r.ReadInt32();
                    string hostName = r.ReadString();
                    string hostMods = r.ReadString();
                    Peers[0] = new PeerInfo { Id = 0, Name = hostName };
                    if (hostMods != ModList())
                        LastError = "Mods differ from the host's. Host: " + hostMods + " / you: " + ModList();
                    Status = "Joined " + hostName + ", waiting for their run...";
                    break;
                }
                case Msg.Reject:
                    Fail(r.ReadString());
                    Stop();
                    break;
                case Msg.Roster:
                {
                    int n = r.ReadInt32();
                    var names = new List<string>();
                    for (int i = 0; i < n; i++) names.Add(r.ReadString());
                    Roster = names;
                    break;
                }
                case Msg.HostInMenu:
                    if (ClientInGame)
                    {
                        ClientInGame = false;
                        LeaveMirroredGame();
                    }
                    Status = "Host is in the menu, waiting...";
                    break;
                case Msg.Full:
                    ApplyFull(JsonUtility.FromJson<FullPayload>(Protocol.Gunzip(r.ReadBlob())));
                    break;
                case Msg.Delta:
                {
                    var d = JsonUtility.FromJson<DeltaPayload>(Protocol.Gunzip(r.ReadBlob()));
                    if (ClientInGame) ApplyDelta(d);
                    break;
                }
                case Msg.Cursor:
                {
                    int who = r.ReadInt32();
                    var pos = r.ReadVec();
                    if (!Peers.TryGetValue(who, out var p)) Peers[who] = p = new PeerInfo { Id = who, Name = "Player " + (who + 1) };
                    p.Cursor = pos;
                    p.CursorTime = Time.unscaledTime;
                    break;
                }
            }
        }

        public List<string> Roster = new List<string>();

        void ApplyFull(FullPayload full)
        {
            var wm = WM;
            Applying = true;
            try
            {
                if (wm.CurrentGameState != WorldManager.GameState.InMenu)
                {
                    WorldManager.RestartGame();
                }
                var save = SaveGame.LoadFromString(full.SaveJson, ClientSaveId);
                SaveManager.instance.CurrentSave = save;
                foreach (var box in wm.AllBoosterBoxes) box.StoredCostAmount = 0;
                var fg = full.G();
                if (fg != null) CitiesManager.instance.Wellbeing = fg.CitiesWellbeing;
                clientBoosters.Clear();
                foreach (var b in wm.AllBoosters.ToList()) if (b != null) Destroy(b.gameObject);
                wm.AllBoosters.Clear();
                wm.LoadPreviousRound();
                wm.Play();
                QuestManager.instance.UpdateCurrentQuests();
                CardopediaScreen.instance?.RefreshCardopedia();
                ClientInGame = true;
                lastAppliedSpeed = wm.SpeedUp;
                if (fg != null) ApplyGlobals(fg);
                CoopLog.Info("loaded host snapshot: " + wm.AllCards.Count + " cards, G=" + (fg != null) + " patched=" + Harmony.HasAnyPatches("stacklands_coop") + " owners=" + string.Join(",", Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m).Owners).Distinct()));
            }
            catch (Exception e)
            {
                CoopLog.Warn("failed to load host snapshot: " + e);
                Fail("Could not load the host's game (missing DLC or mods?)");
            }
            finally { Applying = false; }
        }

        void ApplyDelta(DeltaPayload d)
        {
            var wm = WM;
            var dg = d.G();
            DeltasReceived++;
            if (DeltasReceived % 30 == 1) CoopLog.Info("delta #" + DeltasReceived + " seq=" + d.Seq + " cards=" + d.Cards.Count + " G=" + (dg != null));
            LastSeq = d.Seq;
            Applying = true;
            try
            {
                if (dg != null) ApplyGlobals(dg);

                var touched = new List<KeyValuePair<GameCard, SavedCard>>(d.Cards.Count);
                foreach (var sc in d.Cards)
                {
                    var card = wm.GetCardWithUniqueId(sc.UniqueId);
                    if (card != null && card.CardData.Id != sc.CardPrefabId)
                    {
                        DestroyMirror(card);
                        card = null;
                    }
                    if (card == null) card = CreateMirror(sc);
                    if (card == null) continue;
                    touched.Add(new KeyValuePair<GameCard, SavedCard>(card, sc));
                }
                foreach (var kv in touched) ApplyCardProps(kv.Key, kv.Value);
                // stacks: detach first, then attach, so swaps inside one delta work
                foreach (var kv in touched)
                {
                    var card = kv.Key;
                    if (LocallyHeld(card)) continue;
                    string want = kv.Value.ParentUniqueId ?? "";
                    string have = card.Parent != null ? card.Parent.CardData.UniqueId : "";
                    if (want != have && card.Parent != null) card.RemoveFromParent();
                }
                foreach (var kv in touched)
                {
                    var card = kv.Key;
                    if (LocallyHeld(card) || string.IsNullOrEmpty(kv.Value.ParentUniqueId)) continue;
                    var parent = wm.GetCardWithUniqueId(kv.Value.ParentUniqueId);
                    if (parent == null || parent == card || card.Parent == parent) continue;
                    if (parent.Child != null && parent.Child != card) parent.Child.SetParent(null);
                    if (card.Parent != null) card.RemoveFromParent();
                    card.SetParent(parent);
                }
                foreach (var kv in touched) ApplyHolders(kv.Key, kv.Value);

                foreach (var id in d.Removed)
                {
                    var card = wm.GetCardWithUniqueId(id);
                    if (card != null) DestroyMirror(card);
                }
                if (d.Keyframe && d.All != null && d.All.Count > 0)
                {
                    var keep = new HashSet<string>(d.All);
                    foreach (var card in wm.AllCards.ToList())
                        if (card != null && card.CardData != null && !keep.Contains(card.CardData.UniqueId)) DestroyMirror(card);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warn("delta apply error: " + e);
            }
            finally { Applying = false; }
        }

        static bool LocallyHeld(GameCard card)
        {
            // the local player is dragging this card (or the stack it is in): their hand wins until they drop it
            return card.BeingDragged || (card.GetRootCard() != null && card.GetRootCard().BeingDragged);
        }

        GameCard CreateMirror(SavedCard sc)
        {
            var wm = WM;
            ref bool loading = ref loadingRef(wm);
            bool was = loading;
            loading = true;
            CardData cd;
            try { cd = wm.CreateCard(sc.CardPosition, sc.CardPrefabId, sc.FaceUp, false, false); }
            finally { loading = was; }
            if (cd == null)
            {
                CoopLog.Warn("could not create card " + sc.CardPrefabId + " (missing DLC/mod?)");
                return null;
            }
            var card = cd.MyGameCard;
            cd.UniqueId = sc.UniqueId;
            wm.UniqueIdToCard[sc.UniqueId] = card;
            card.MyBoard = wm.GetBoardWithId(sc.BoardId) ?? wm.CurrentBoard;
            card.transform.position = sc.CardPosition;
            card.TargetPosition = sc.CardPosition;
            if (cd.WorkerAmount > 0) card.WorkerTransformHolder.UpdateWorkerAmount(cd.WorkerAmount);
            return card;
        }

        void DestroyMirror(GameCard card)
        {
            if (card == null || card.Destroyed) return;
            if (WM.DraggingDraggable == card) WM.SetDraggingDraggableToNull();
            if (card.EquipmentHolder != null) { card.EquipmentHolder.EquipmentChildren.Remove(card); card.EquipmentHolder = null; card.IsEquipped = false; }
            if (card.WorkerHolder != null) { card.WorkerHolder.WorkerChildren.Remove(card); card.WorkerHolder = null; card.IsWorking = false; }
            // children that are still alive on the host get re-sent; don't let the game cascade-destroy them
            if (card.CardData.HasInventory)
            {
                foreach (var e in card.EquipmentChildren.ToList()) { e.EquipmentHolder = null; e.IsEquipped = false; }
                card.EquipmentChildren.Clear();
            }
            if (card.CardData.WorkerAmount > 0)
            {
                foreach (var e in card.WorkerChildren.ToList()) { e.WorkerHolder = null; e.IsWorking = false; }
                card.WorkerChildren.Clear();
            }
            card.DestroyCard(false, false);
        }

        void ApplyCardProps(GameCard card, SavedCard sc)
        {
            var cd = card.CardData;
            var board = WM.GetBoardWithId(sc.BoardId);
            if (board != null) card.MyBoard = board;
            if (!LocallyHeld(card))
            {
                card.TargetPosition = sc.CardPosition;
                // big jumps (sent across the board, new board) snap instead of sliding
                if ((card.transform.position - sc.CardPosition).sqrMagnitude > 25f) card.transform.position = sc.CardPosition;
                card.Velocity = null;
            }
            if (card.FaceUp != sc.FaceUp) card.SetFaceUp(sc.FaceUp);
            if (sc.IsFoil && !cd.IsFoil) cd.SetFoil();
            cd.IsDamaged = sc.IsDamaged;
            cd.DamageType = sc.DamageType;
            if (sc.ExtraCardData != null) cd.SetExtraCardData(sc.ExtraCardData);

            // status effects
            string seNew = sc.StatusEffects == null ? "" : string.Join("|", sc.StatusEffects.Select(JsonUtility.ToJson));
            string seOld = cd.StatusEffects == null ? "" : string.Join("|", cd.StatusEffects.Select(x => JsonUtility.ToJson(x.ToSavedStatusEffect())));
            if (seNew != seOld)
            {
                var list = (sc.StatusEffects ?? new List<SavedStatusEffect>()).Select(StatusEffect.FromSavedStatusEffect).Where(x => x != null).ToList();
                foreach (var se in list) se.ParentCard = cd;
                cd.StatusEffects = list;
                card.StatusEffectsChanged();
            }

            // timer / progress bar
            if (sc.TimerRunning)
            {
                if (!card.TimerRunning || card.TimerActionId != sc.TimerActionId)
                {
                    if (card.TimerRunning) card.CancelAnyTimer();
                    TimerAction del = cd.GetDelegateForActionId(sc.TimerActionId);
                    if (del == null) del = () => { };
                    bool dragged = card.BeingDragged;
                    card.BeingDragged = false;
                    card.StartTimer(sc.TargetTimerTime, del, sc.Status, sc.TimerActionId, sc.WithStatusBar, true, true);
                    card.BeingDragged = dragged;
                }
                card.CurrentTimerTime = sc.CurrentTimerTime;
                card.TargetTimerTime = sc.TargetTimerTime;
                card.Status = sc.Status;
                card.TimerBlueprintId = sc.TimerBlueprintId;
                card.TimerSubprintIndex = sc.SubprintIndex;
                card.SkipCitiesChecks = sc.SkipCitiesChecks;
                if (card.CurrentStatusbar != null) card.CurrentStatusbar.StatusTime = sc.TargetTimerTime;
            }
            else if (card.TimerRunning)
            {
                card.CancelAnyTimer();
            }
        }

        void ApplyHolders(GameCard card, SavedCard sc)
        {
            var wm = WM;
            string wantEq = sc.EquipmentHolderUniqueId ?? "";
            string haveEq = card.EquipmentHolder != null ? card.EquipmentHolder.CardData.UniqueId : "";
            if (wantEq != haveEq)
            {
                if (card.EquipmentHolder != null) card.EquipmentHolder.EquipmentChildren.Remove(card);
                card.EquipmentHolder = null;
                card.IsEquipped = false;
                card.CardData.EquipmentHolderUniqueId = sc.EquipmentHolderUniqueId;
                var holder = string.IsNullOrEmpty(wantEq) ? null : wm.GetCardWithUniqueId(wantEq);
                if (holder != null)
                {
                    if (!holder.EquipmentChildren.Contains(card)) holder.EquipmentChildren.Add(card);
                    card.EquipmentHolder = holder;
                    card.IsEquipped = true;
                }
            }
            string wantW = sc.WorkerHolderUniqueId ?? "";
            string haveW = card.WorkerHolder != null ? card.WorkerHolder.CardData.UniqueId : "";
            if (wantW != haveW)
            {
                if (card.WorkerHolder != null) card.WorkerHolder.WorkerChildren.Remove(card);
                card.WorkerHolder = null;
                card.IsWorking = false;
                card.CardData.WorkerHolderUniqueId = sc.WorkerHolderUniqueId;
                card.CardData.WorkerIndex = sc.WorkerIndex;
                var holder = string.IsNullOrEmpty(wantW) ? null : wm.GetCardWithUniqueId(wantW);
                if (holder != null)
                {
                    if (!holder.WorkerChildren.Contains(card)) holder.WorkerChildren.Add(card);
                    card.WorkerHolder = holder;
                    card.IsWorking = true;
                }
            }
        }

        void ApplyGlobals(CoopGlobals g)
        {
            var wm = WM;
            LastGlobals = g;
            HostCanInteract = g.CanInteract;
            wm.MonthTimer = Mathf.Min(g.MonthTimer, wm.MonthTime - 0.05f);
            if (g.Months != null) wm.BoardMonths = new BoardMonths(g.Months);
            if (Time.unscaledTime > speedOverrideUntil)
            {
                wm.SpeedUp = g.SpeedUp;
                lastAppliedSpeed = g.SpeedUp;
            }
            wm.QuestsCompleted = g.QuestsCompleted;
            wm.NewCardsFound = g.NewCardsFound;
            if (g.BoughtBoosterIds != null) wm.BoughtBoosterIds = g.BoughtBoosterIds;
            if (CitiesManager.instance != null) CitiesManager.instance.Wellbeing = g.CitiesWellbeing;
            foreach (var sb in g.Boxes)
            {
                var box = wm.AllBoosterBoxes.Find(x => x.BoosterId == sb.BoosterId);
                if (box != null) box.StoredCostAmount = sb.StoredCostAmount;
            }

            // board switch (island, forest, spirit worlds...)
            if (wm.CurrentBoard == null || wm.CurrentBoard.Id != g.BoardId)
            {
                var board = wm.GetBoardWithId(g.BoardId);
                if (board != null)
                {
                    if (wm.DraggingDraggable != null) { wm.DraggingDraggable.StopDragging(); wm.SetDraggingDraggableToNull(); }
                    setCurrentBoard.Invoke(wm, new object[] { board });
                    wm.SetViewType(ViewType.Default);
                    GameCamera.instance.CenterOnBoard(board);
                    GameScreen.instance?.OnBoardChange();
                }
            }

            if (g.HasProgress)
            {
                var save = wm.CurrentSave;
                save.CompletedAchievementIds = g.Completed ?? new List<string>();
                save.FoundCardIds = g.FoundCards ?? new List<string>();
                save.FoundBoosterIds = g.FoundBoosters ?? new List<string>();
                QuestManager.instance.UpdateCurrentQuests();
                QuestManager.instance.CheckPacksUnlocked();
                wm.UpdateCardTargets();
                GameScreen.instance?.UpdateQuestLog();
                GameScreen.instance?.UpdateIdeasLog();
            }

            SyncBoosters(g.Boosters());
        }

        void SyncBoosters(List<NetBooster> list)
        {
            var wm = WM;
            var alive = new HashSet<int>();
            foreach (var nb in list)
            {
                alive.Add(nb.Id);
                if (!clientBoosters.TryGetValue(nb.Id, out var b) || b == null)
                {
                    b = wm.CreateBoosterpack(nb.Pos, nb.BoosterId);
                    if (b == null) continue;
                    clientBoosters[nb.Id] = b;
                    ConsumeBags(b, nb.TimesOpened);
                    b.TimesOpened = nb.TimesOpened;
                    b.WasClicked = nb.TimesOpened > 0;
                }
                b.MyBoard = wm.GetBoardWithId(nb.BoardId) ?? b.MyBoard;
                if (nb.TimesOpened > b.TimesOpened)
                {
                    ConsumeBags(b, nb.TimesOpened - b.TimesOpened);
                    b.TimesOpened = nb.TimesOpened;
                    b.WasClicked = true;
                }
                if (!b.BeingDragged)
                {
                    b.TargetPosition = nb.Pos;
                    b.Velocity = null;
                }
            }
            foreach (var id in clientBoosters.Keys.ToList())
            {
                if (alive.Contains(id)) continue;
                var b = clientBoosters[id];
                clientBoosters.Remove(id);
                if (b != null)
                {
                    if (wm.DraggingDraggable == b) wm.SetDraggingDraggableToNull();
                    wm.AllBoosters.Remove(b);
                    Destroy(b.gameObject);
                }
            }
        }

        static void ConsumeBags(Boosterpack b, int n)
        {
            foreach (var bag in b.CardBags)
            {
                if (n <= 0) break;
                int take = Mathf.Min(n, bag.CardsInPack);
                bag.CardsInPack -= take;
                n -= take;
            }
        }

        public void ClientRequestFull() => Send(0, Protocol.Build(Msg.RequestFull));

        public void ClientContinue(int index) => Send(0, Protocol.Build(Msg.Continue, w => w.Write(index)));

        /// <summary>Test hook: run the game's own private DropCard.</summary>
        public static void InvokeDropCard() => dropCardMethod.Invoke(WorldManager.instance, null);
    }
}
