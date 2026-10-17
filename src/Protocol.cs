using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace StacklandsCoop
{
    public enum Msg : byte
    {
        // client -> host
        Hello = 1,
        DragStart = 2,
        DragMove = 3,
        Drop = 4,
        Click = 5,
        Sell = 6,
        SetSpeed = 7,
        RequestFull = 8,
        Continue = 9, // press the cutscene Continue button (or pick answer N)
        // both directions
        Cursor = 20,
        // host -> client
        Welcome = 40,
        Full = 41,
        Delta = 42,
        HostInMenu = 43,
        Roster = 44,
        Reject = 45,
    }

    /// <summary>What a drag/click refers to: a card (by UniqueId) or a booster pack (by host-assigned net id).</summary>
    public enum ObjKind : byte { Card = 0, Booster = 1 }

    public static class Protocol
    {
        public const int Version = 1;

        public static byte[] Build(Msg type, Action<BinaryWriter> body = null)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((byte)type);
                body?.Invoke(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static void WriteVec(this BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static Vector3 ReadVec(this BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static void WriteStr(this BinaryWriter w, string s) => w.Write(s ?? "");

        public static byte[] Gzip(string s)
        {
            var raw = Encoding.UTF8.GetBytes(s);
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                    gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        public static string Gunzip(byte[] b)
        {
            using (var ms = new MemoryStream(b))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                gz.CopyTo(o);
                return Encoding.UTF8.GetString(o.ToArray());
            }
        }

        public static void WriteBlob(this BinaryWriter w, byte[] b) { w.Write(b.Length); w.Write(b); }
        public static byte[] ReadBlob(this BinaryReader r) => r.ReadBytes(r.ReadInt32());
    }

    [Serializable]
    public class NetBooster
    {
        public int Id;
        public string BoosterId;
        public Vector3 Pos;
        public int TimesOpened;
        public string BoardId;
        public bool Dragged;
    }

    [Serializable]
    public class CoopGlobals
    {
        public float MonthTimer;
        public SavedMonth Months;
        public float SpeedUp;
        public string BoardId;
        public int GameState;
        public bool CanInteract;
        public string CutsceneTitle;
        public string CutsceneText;
        public int QuestsCompleted;
        public int NewCardsFound;
        public int CitiesWellbeing;
        public int CardCount;
        public bool ShowContinue;
        public string ContinueText;
        public List<string> Options = new List<string>();
        public List<SavedBoosterBox> Boxes = new List<SavedBoosterBox>();
        public List<string> BoughtBoosterIds = new List<string>();
        // Boosters as parallel lists: JsonUtility skips nested classes from mod assemblies.
        public List<int> BId = new List<int>();
        public List<string> BType = new List<string>();
        public List<Vector3> BPos = new List<Vector3>();
        public List<int> BOpened = new List<int>();
        public List<string> BBoard = new List<string>();

        public void AddBooster(NetBooster b) { BId.Add(b.Id); BType.Add(b.BoosterId); BPos.Add(b.Pos); BOpened.Add(b.TimesOpened); BBoard.Add(b.BoardId); }

        public List<NetBooster> Boosters()
        {
            var l = new List<NetBooster>();
            for (int i = 0; i < BId.Count; i++) l.Add(new NetBooster { Id = BId[i], BoosterId = BType[i], Pos = BPos[i], TimesOpened = BOpened[i], BoardId = BBoard[i] });
            return l;
        }
        // Save-wide progress (quests, ideas, packs). Only filled when it changed.
        public bool HasProgress;
        public List<string> Completed = new List<string>();
        public List<string> FoundCards = new List<string>();
        public List<string> FoundBoosters = new List<string>();
    }

    [Serializable]
    public class DeltaPayload
    {
        public int Seq;
        public bool Keyframe;
        public string GJson; // CoopGlobals as its own JSON document (see above)
        public CoopGlobals G() => string.IsNullOrEmpty(GJson) ? null : JsonUtility.FromJson<CoopGlobals>(GJson);
        public List<SavedCard> Cards = new List<SavedCard>();
        public List<string> Removed = new List<string>();
        // On keyframes: every live card id, so the client can drop anything it should not have.
        public List<string> All = new List<string>();
    }

    [Serializable]
    public class FullPayload
    {
        public string SaveJson;
        public string GJson; // CoopGlobals as its own JSON document (see above)
        public CoopGlobals G() => string.IsNullOrEmpty(GJson) ? null : JsonUtility.FromJson<CoopGlobals>(GJson);
    }
}
