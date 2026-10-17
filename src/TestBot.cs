using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace StacklandsCoop
{
    /// <summary>
    /// Automated two-instance test, enabled with env COOP_TEST=host|client (never on for players).
    /// Results go to the Unity log as "[COOPTEST] PASS/FAIL ..." lines and to COOP_TEST_OUT if set.
    /// </summary>
    public class TestBot : MonoBehaviour
    {
        public string Mode;
        WorldManager WM => WorldManager.instance;
        CoopSession S => CoopSession.I;
        int pass, fail;

        void Start() => StartCoroutine(Mode == "host" ? Host() : Client());

        void Report(bool ok, string what)
        {
            if (ok) pass++; else fail++;
            string line = "[COOPTEST] " + (ok ? "PASS " : "FAIL ") + what;
            CoopLog.Info(line);
            var outp = Environment.GetEnvironmentVariable("COOP_TEST_OUT");
            if (!string.IsNullOrEmpty(outp)) try { File.AppendAllText(outp, Mode + " " + line + "\n"); } catch { }
        }

        IEnumerator WaitMenu()
        {
            while (WM == null || WM.CurrentGameState != WorldManager.GameState.InMenu || TransitionScreen.InTransition) yield return null;
            yield return new WaitForSeconds(3f);
        }

        IEnumerator Host()
        {
            yield return WaitMenu();
            var wm = WM;
            wm.CurrentRunOptions = new RunOptions();
            foreach (var box in wm.AllBoosterBoxes) box.StoredCostAmount = 0;
            wm.CurrentRunVariables = new RunVariables();
            wm.RoundExtraKeyValues = new System.Collections.Generic.List<SerializedKeyValuePair>();
            CitiesManager.instance.Wellbeing = CitiesManager.instance.WellbeingStart;
            wm.StartNewRound();
            wm.Play();
            yield return new WaitForSeconds(1f);
            var mid = wm.MiddleOfBoard();
            wm.CreateCard(mid + new Vector3(-1.5f, 0, 0), "villager", true, false);
            wm.CreateCard(mid + new Vector3(1.5f, 0, 0), "berrybush", true, false);
            for (int i = 0; i < 6; i++) wm.CreateCard(mid + new Vector3(-1.5f, 0, -1.5f), "gold", true, true);
            wm.CreateCard(mid + new Vector3(3f, 0, -1.5f), "rock", true, false);
            S.StartHostTcp(27777);
            Report(CoopSession.IsHost, "host listening");
            // wait for the client, then move a card the host "owns" so the client can see it move
            while (!S.Peers.Values.Any(p => p.Synced)) yield return null;
            yield return new WaitForSeconds(8f);
            var rock = wm.AllCards.FirstOrDefault(c => c.CardData.Id == "rock");
            if (rock != null) rock.TargetPosition = mid + new Vector3(-3f, 0, 2f);
            CoopLog.Info("[COOPTEST] host moved rock");
            for (int t = 0; t < 120; t++)
            {
                yield return new WaitForSeconds(2f);
                CoopLog.Info("[COOPTEST] host cards=" + wm.AllCards.Count + " " + string.Join(",", wm.AllCards.Select(c => c.CardData.Id)));
            }
        }

        IEnumerator HostReady()
        {
            for (float t = 0; t < 30f; t += 0.5f)
            {
                var g = S.LastGlobals;
                if (g != null && g.CanInteract && !g.ShowContinue) break;
                if (g != null && g.ShowContinue) { CoopLog.Info("[COOPTEST] client presses continue: " + g.CutsceneTitle); continues++; S.ClientContinue(g.Options.Count > 0 ? 0 : -1); yield return new WaitForSeconds(1.5f); }
                yield return new WaitForSeconds(0.5f);
            }
            yield return new WaitForSeconds(0.5f);
        }
        int continues;

        GameCard Find(string id) => WM.AllCards.FirstOrDefault(c => c != null && !c.Destroyed && c.CardData.Id == id);

        void Drag(Draggable d, Vector3 to, CardTarget target = null)
        {
            var wm = WM;
            wm.DraggingDraggable = d;
            d.DragStartPosition = d.transform.position;
            d.StartDragging();
            S.ClientDragStarted(d);
            d.transform.position = to;
            d.TargetPosition = to;
            wm.NearbyCardTarget = target;
            CoopSession.InvokeDropCard();
        }

        IEnumerator Until(Func<bool> cond, float timeout)
        {
            float t = 0;
            while (t < timeout && !cond()) { t += Time.unscaledDeltaTime; yield return null; }
        }

        IEnumerator Client()
        {
            yield return WaitMenu();
            yield return new WaitForSeconds(4f); // let the host start its run
            for (int attempt = 0; attempt < 20 && !S.ClientInGame; attempt++)
            {
                if (CoopSession.Role == Role.None) S.JoinTcp("127.0.0.1", 27777);
                yield return Until(() => S.ClientInGame || CoopSession.Role == Role.None, 15f);
                if (!S.ClientInGame) yield return new WaitForSeconds(3f);
            }
            yield return Until(() => S.ClientInGame, 60f);
            Report(S.ClientInGame, "client loaded host run");
            if (!S.ClientInGame) yield break;
            yield return new WaitForSeconds(2f);
            var g = S.LastGlobals;
            Report(g != null && WM.AllCards.Count == g.CardCount, "card count matches host (" + WM.AllCards.Count + " vs " + (g?.CardCount ?? -1) + ")");
            Report(WM.AllBoosters.Count >= 1, "starter pack mirrored (" + WM.AllBoosters.Count + ")");

            yield return HostReady();
            // open the starter pack by clicking it
            int before = WM.AllCards.Count;
            var pack = WM.AllBoosters.FirstOrDefault();
            if (pack != null)
            {
                for (int i = 0; i < 6 && pack != null; i++)
                {
                    pack.Clicked();
                    yield return new WaitForSeconds(0.8f);
                }
            }
            yield return new WaitForSeconds(1.5f);
            Report(WM.AllCards.Count > before, "clicking the pack spawned cards on host and mirrored (" + before + " -> " + WM.AllCards.Count + ")");

            yield return HostReady();
            // drag villager onto berry bush
            var vill = WM.AllCards.FirstOrDefault(c => c.CardData.Id == "villager" && c.Parent == null && c.Child == null);
            var bush = WM.AllCards.FirstOrDefault(c => c.CardData.Id == "berrybush" && c.Child == null);
            Report(vill != null && bush != null, "found villager and berry bush");
            if (vill != null && bush != null)
            {
                Drag(vill, bush.transform.position + new Vector3(0, 0, -0.3f));
                yield return Until(() => vill.Parent == bush, 3f);
                Report(vill.Parent == bush, "villager stacked on bush (host confirmed)");
                yield return Until(() => bush.TimerRunning || vill.TimerRunning, 3f);
                Report(bush.TimerRunning || vill.TimerRunning || (vill.GetRootCard()?.TimerRunning ?? false), "harvest timer running on mirror");
                int berries = WM.AllCards.Count(c => c.CardData.Id == "berry");
                yield return Until(() => WM.AllCards.Count(c => c.CardData.Id == "berry") > berries, 40f);
                Report(WM.AllCards.Count(c => c.CardData.Id == "berry") > berries, "berry harvested and mirrored");
            }

            yield return HostReady();
            // sell a berry
            var berry = Find("berry");
            var sellBox = WM.CardTargets.OfType<SellBox>().FirstOrDefault(b => b.gameObject.activeInHierarchy);
            int gold = WM.AllCards.Count(c => c.CardData.Id == "gold");
            if (berry != null && sellBox != null)
            {
                if (berry.Parent != null) yield return new WaitForSeconds(0.5f);
                Drag(berry, sellBox.transform.position, sellBox);
                yield return Until(() => WM.AllCards.Count(c => c.CardData.Id == "gold") > gold, 4f);
                Report(WM.AllCards.Count(c => c.CardData.Id == "gold") > gold, "sold berry for a coin");
            }
            else Report(false, "sell test setup (berry=" + (berry != null) + " box=" + (sellBox != null) + ")");

            yield return HostReady();
            // buy a pack with coins
            var buy = WM.CardTargets.OfType<BuyBoosterBox>().FirstOrDefault(b => b.gameObject.activeInHierarchy && b.Booster != null && b.Booster.IsUnlocked);
            var coin = WM.AllCards.Where(c => c.CardData.Id == "gold").Select(c => c.GetRootCard()).FirstOrDefault();
            int packs = WM.AllBoosters.Count;
            if (buy != null && coin != null)
            {
                Drag(coin, buy.transform.position, buy);
                yield return Until(() => WM.AllBoosters.Count > packs, 4f);
                Report(WM.AllBoosters.Count > packs, "bought " + buy.BoosterId + " with coins (" + packs + " -> " + WM.AllBoosters.Count + ")");
            }
            else Report(false, "buy test setup");

            // host movement visible
            var rock = Find("rock");
            yield return new WaitForSeconds(1f);
            var mid = WM.MiddleOfBoard() + new Vector3(-3f, 0, 2f);
            // the rock started 3 units right of the middle and the host sent it 3 units left; the host's physics may nudge it
            Report(rock != null && rock.TargetPosition.x < mid.x + 3f, "host-moved rock seen on client (x=" + (rock != null ? rock.TargetPosition.x : 0) + " start=" + (mid.x + 6f) + ")");

            // speed change goes to host
            WM.SpeedUp = 5f;
            yield return new WaitForSeconds(1.5f);
            Report(S.LastGlobals != null && Mathf.Approximately(S.LastGlobals.SpeedUp, 5f), "speed change reached host");
            WM.SpeedUp = 1f;

            Report(true, "cutscene continues sent by client: " + continues);
            CoopLog.Info("[COOPTEST] DONE pass=" + pass + " fail=" + fail);
            var outp = Environment.GetEnvironmentVariable("COOP_TEST_OUT");
            if (!string.IsNullOrEmpty(outp)) File.AppendAllText(outp, "client DONE pass=" + pass + " fail=" + fail + "\n");
        }
    }
}
