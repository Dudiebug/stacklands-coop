using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;

namespace StacklandsCoop
{
    public enum NetEventType { Connected, Disconnected, Data }

    public struct NetEvent
    {
        public NetEventType Type;
        public int Peer;
        public byte[] Data;
    }

    /// <summary>Reliable, ordered message transport. Peer 0 is always the host.</summary>
    public interface ITransport
    {
        bool IsHost { get; }
        string Describe();
        void Send(int peer, byte[] data);
        bool Poll(out NetEvent ev);
        void Shutdown();
    }

    // ---------------------------------------------------------------- TCP

    /// <summary>Length-prefixed frames over TCP. Used for LAN / direct IP play and for automated tests.</summary>
    public class TcpTransport : ITransport
    {
        readonly ConcurrentQueue<NetEvent> events = new ConcurrentQueue<NetEvent>();
        readonly Dictionary<int, TcpClient> peers = new Dictionary<int, TcpClient>();
        readonly object peersLock = new object();
        TcpListener listener;
        int nextPeer = 1;
        volatile bool running = true;
        readonly string desc;

        public bool IsHost { get; }

        public string Describe() => desc;

        TcpTransport(bool host, string d) { IsHost = host; desc = d; }

        public static TcpTransport Host(int port)
        {
            var t = new TcpTransport(true, "LAN/IP host on port " + port);
            t.listener = new TcpListener(IPAddress.Any, port);
            t.listener.Start();
            var th = new Thread(t.AcceptLoop) { IsBackground = true, Name = "coop-accept" };
            th.Start();
            return t;
        }

        public static TcpTransport Connect(string host, int port)
        {
            var t = new TcpTransport(false, "connected to " + host + ":" + port);
            var th = new Thread(() =>
            {
                try
                {
                    var c = new TcpClient();
                    c.NoDelay = true;
                    c.Connect(host, port);
                    lock (t.peersLock) t.peers[0] = c;
                    t.events.Enqueue(new NetEvent { Type = NetEventType.Connected, Peer = 0 });
                    t.ReadLoop(0, c);
                }
                catch (Exception e)
                {
                    CoopLog.Warn("connect failed: " + e.Message);
                    t.events.Enqueue(new NetEvent { Type = NetEventType.Disconnected, Peer = 0 });
                }
            }) { IsBackground = true, Name = "coop-connect" };
            th.Start();
            return t;
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var c = listener.AcceptTcpClient();
                    c.NoDelay = true;
                    int id;
                    lock (peersLock) { id = nextPeer++; peers[id] = c; }
                    events.Enqueue(new NetEvent { Type = NetEventType.Connected, Peer = id });
                    var th = new Thread(() => ReadLoop(id, c)) { IsBackground = true, Name = "coop-read-" + id };
                    th.Start();
                }
                catch (Exception)
                {
                    if (!running) return;
                }
            }
        }

        void ReadLoop(int id, TcpClient c)
        {
            try
            {
                var s = c.GetStream();
                var hdr = new byte[4];
                while (running)
                {
                    ReadExact(s, hdr, 4);
                    int len = BitConverter.ToInt32(hdr, 0);
                    if (len < 0 || len > 64 * 1024 * 1024) throw new IOException("bad frame length " + len);
                    var buf = new byte[len];
                    ReadExact(s, buf, len);
                    events.Enqueue(new NetEvent { Type = NetEventType.Data, Peer = id, Data = buf });
                }
            }
            catch (Exception)
            {
            }
            lock (peersLock) peers.Remove(id);
            try { c.Close(); } catch { }
            if (running) events.Enqueue(new NetEvent { Type = NetEventType.Disconnected, Peer = id });
        }

        static void ReadExact(Stream s, byte[] buf, int len)
        {
            int off = 0;
            while (off < len)
            {
                int n = s.Read(buf, off, len - off);
                if (n <= 0) throw new IOException("closed");
                off += n;
            }
        }

        public void Send(int peer, byte[] data)
        {
            TcpClient c;
            lock (peersLock) peers.TryGetValue(peer, out c);
            if (c == null) return;
            try
            {
                var s = c.GetStream();
                lock (c)
                {
                    s.Write(BitConverter.GetBytes(data.Length), 0, 4);
                    s.Write(data, 0, data.Length);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warn("send failed: " + e.Message);
                try { c.Close(); } catch { }
            }
        }

        public bool Poll(out NetEvent ev) => events.TryDequeue(out ev);

        public void Shutdown()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            lock (peersLock)
            {
                foreach (var c in peers.Values) { try { c.Close(); } catch { } }
                peers.Clear();
            }
        }
    }

    // ---------------------------------------------------------------- Steam

    /// <summary>
    /// Steam friends-only lobby for discovery and invites, SteamNetworkingMessages for data
    /// (goes through Valve's relay, so nobody needs to forward ports).
    /// </summary>
    public class SteamTransport : ITransport
    {
        const int Channel = 0;
        const int ChunkSize = 256 * 1024;
        const int SendFlags = Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

        readonly Queue<NetEvent> events = new Queue<NetEvent>();
        readonly Dictionary<ulong, int> steamToPeer = new Dictionary<ulong, int>();
        readonly Dictionary<int, CSteamID> peerToSteam = new Dictionary<int, CSteamID>();
        readonly Dictionary<int, MemoryStream> partial = new Dictionary<int, MemoryStream>();
        readonly Dictionary<int, int> partialTotal = new Dictionary<int, int>();
        readonly IntPtr[] msgPtrs = new IntPtr[64];
        int nextPeer = 1;
        bool closed;

        public CSteamID Lobby { get; private set; }
        CSteamID hostId;

        Callback<LobbyCreated_t> cbLobbyCreated;
        Callback<LobbyEnter_t> cbLobbyEnter;
        Callback<LobbyChatUpdate_t> cbChatUpdate;
        Callback<SteamNetworkingMessagesSessionRequest_t> cbSessionReq;
        Callback<SteamNetworkingMessagesSessionFailed_t> cbSessionFailed;

        public bool IsHost { get; }

        public string Describe() => IsHost
            ? (Lobby.IsValid() ? "Steam lobby " + Lobby.m_SteamID : "creating Steam lobby...")
            : (hostId.IsValid() ? "Steam: joined " + SteamFriends.GetFriendPersonaName(hostId) : "joining Steam lobby...");

        SteamTransport(bool host)
        {
            IsHost = host;
            cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            cbChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
            cbSessionReq = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            cbSessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
        }

        public static SteamTransport Host(int maxPlayers)
        {
            var t = new SteamTransport(true);
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            return t;
        }

        public static SteamTransport Join(CSteamID lobby)
        {
            var t = new SteamTransport(false);
            SteamMatchmaking.JoinLobby(lobby);
            return t;
        }

        public void InviteFriends()
        {
            if (Lobby.IsValid()) SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        void OnLobbyCreated(LobbyCreated_t r)
        {
            if (!IsHost || closed) return;
            if (r.m_eResult != EResult.k_EResultOK)
            {
                CoopLog.Warn("lobby create failed: " + r.m_eResult);
                return;
            }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, "stacklands_coop", Protocol.Version.ToString());
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
            CoopLog.Info("lobby created " + Lobby.m_SteamID);
        }

        void OnLobbyEnter(LobbyEnter_t r)
        {
            if (IsHost || closed) return;
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            hostId = SteamMatchmaking.GetLobbyOwner(Lobby);
            if (!hostId.IsValid() || hostId == SteamUser.GetSteamID())
            {
                CoopLog.Warn("lobby has no usable host");
                events.Enqueue(new NetEvent { Type = NetEventType.Disconnected, Peer = 0 });
                return;
            }
            steamToPeer[hostId.m_SteamID] = 0;
            peerToSteam[0] = hostId;
            events.Enqueue(new NetEvent { Type = NetEventType.Connected, Peer = 0 });
        }

        void OnChatUpdate(LobbyChatUpdate_t r)
        {
            if (closed || r.m_ulSteamIDLobby != Lobby.m_SteamID) return;
            var who = r.m_ulSteamIDUserChanged;
            bool left = (r.m_rgfChatMemberStateChange & (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                | EChatMemberStateChange.k_EChatMemberStateChangeBanned)) != 0;
            if (left && steamToPeer.TryGetValue(who, out int peer))
                DropPeer(peer);
        }

        void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t r)
        {
            var id = r.m_identityRemote.GetSteamID();
            bool ok = IsHost ? IsLobbyMember(id) : id == hostId;
            if (ok) SteamNetworkingMessages.AcceptSessionWithUser(ref r.m_identityRemote);
        }

        void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t r)
        {
            var id = r.m_info.m_identityRemote.GetSteamID();
            CoopLog.Warn("steam session failed with " + id.m_SteamID + ": " + r.m_info.m_eEndReason);
            if (steamToPeer.TryGetValue(id.m_SteamID, out int peer)) DropPeer(peer);
        }

        bool IsLobbyMember(CSteamID id)
        {
            if (!Lobby.IsValid()) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == id) return true;
            return false;
        }

        void DropPeer(int peer)
        {
            if (!peerToSteam.TryGetValue(peer, out var sid)) return;
            peerToSteam.Remove(peer);
            steamToPeer.Remove(sid.m_SteamID);
            partial.Remove(peer);
            var ident = new SteamNetworkingIdentity();
            ident.SetSteamID(sid);
            SteamNetworkingMessages.CloseSessionWithUser(ref ident);
            events.Enqueue(new NetEvent { Type = NetEventType.Disconnected, Peer = peer });
        }

        public string PeerName(int peer) =>
            peerToSteam.TryGetValue(peer, out var sid) ? SteamFriends.GetFriendPersonaName(sid) : "?";

        public void Send(int peer, byte[] data)
        {
            if (closed || !peerToSteam.TryGetValue(peer, out var sid)) return;
            var ident = new SteamNetworkingIdentity();
            ident.SetSteamID(sid);
            if (data.Length + 1 <= ChunkSize)
            {
                SendRaw(ref ident, 0, data, 0, data.Length, -1);
                return;
            }
            // big payloads (initial snapshot) are split; reliable+ordered keeps the parts in order
            int off = 0;
            bool first = true;
            while (off < data.Length)
            {
                int n = Math.Min(ChunkSize - 8, data.Length - off);
                SendRaw(ref ident, first ? (byte)1 : (byte)2, data, off, n, first ? data.Length : -1);
                first = false;
                off += n;
            }
        }

        void SendRaw(ref SteamNetworkingIdentity ident, byte kind, byte[] data, int off, int len, int total)
        {
            int hdr = kind == 1 ? 5 : 1;
            var buf = new byte[len + hdr];
            buf[0] = kind;
            if (kind == 1) BitConverter.GetBytes(total).CopyTo(buf, 1);
            Buffer.BlockCopy(data, off, buf, hdr, len);
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                var res = SteamNetworkingMessages.SendMessageToUser(ref ident, h.AddrOfPinnedObject(), (uint)buf.Length, SendFlags, Channel);
                if (res != EResult.k_EResultOK) CoopLog.Warn("steam send result " + res);
            }
            finally { h.Free(); }
        }

        public bool Poll(out NetEvent ev)
        {
            if (!closed) Pump();
            if (events.Count > 0) { ev = events.Dequeue(); return true; }
            ev = default;
            return false;
        }

        void Pump()
        {
            int n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, msgPtrs, msgPtrs.Length);
            for (int i = 0; i < n; i++)
            {
                var m = Marshal.PtrToStructure<SteamNetworkingMessage_t>(msgPtrs[i]);
                var sid = m.m_identityPeer.GetSteamID();
                var buf = new byte[m.m_cbSize];
                Marshal.Copy(m.m_pData, buf, 0, m.m_cbSize);
                SteamNetworkingMessage_t.Release(msgPtrs[i]);
                if (!steamToPeer.TryGetValue(sid.m_SteamID, out int peer))
                {
                    if (!IsHost || !IsLobbyMember(sid)) continue;
                    peer = nextPeer++;
                    steamToPeer[sid.m_SteamID] = peer;
                    peerToSteam[peer] = sid;
                    events.Enqueue(new NetEvent { Type = NetEventType.Connected, Peer = peer });
                }
                Receive(peer, buf);
            }
        }

        void Receive(int peer, byte[] buf)
        {
            if (buf.Length == 0) return;
            byte kind = buf[0];
            if (kind == 0)
            {
                var d = new byte[buf.Length - 1];
                Buffer.BlockCopy(buf, 1, d, 0, d.Length);
                events.Enqueue(new NetEvent { Type = NetEventType.Data, Peer = peer, Data = d });
                return;
            }
            if (kind == 1)
            {
                partialTotal[peer] = BitConverter.ToInt32(buf, 1);
                partial[peer] = new MemoryStream();
                partial[peer].Write(buf, 5, buf.Length - 5);
            }
            else if (partial.TryGetValue(peer, out var ms))
            {
                ms.Write(buf, 1, buf.Length - 1);
            }
            if (partial.TryGetValue(peer, out var acc) && acc.Length >= partialTotal[peer])
            {
                partial.Remove(peer);
                events.Enqueue(new NetEvent { Type = NetEventType.Data, Peer = peer, Data = acc.ToArray() });
            }
        }

        public void Shutdown()
        {
            if (closed) return;
            closed = true;
            foreach (var sid in peerToSteam.Values)
            {
                var ident = new SteamNetworkingIdentity();
                ident.SetSteamID(sid);
                SteamNetworkingMessages.CloseSessionWithUser(ref ident);
            }
            peerToSteam.Clear();
            steamToPeer.Clear();
            if (Lobby.IsValid()) SteamMatchmaking.LeaveLobby(Lobby);
            if (IsHost) SteamFriends.SetRichPresence("connect", null);
            cbLobbyCreated?.Dispose();
            cbLobbyEnter?.Dispose();
            cbChatUpdate?.Dispose();
            cbSessionReq?.Dispose();
            cbSessionFailed?.Dispose();
        }
    }
}
