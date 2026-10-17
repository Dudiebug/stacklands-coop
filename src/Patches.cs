using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace StacklandsCoop
{
    /// <summary>
    /// Client-side patches. On a client in a co-op game the local simulation is frozen and every
    /// action is forwarded to the host; only the mirror code (CoopSession.Applying) may change cards.
    /// </summary>
    static class Gate
    {
        /// <summary>True when a game-driven change must be blocked on this client.</summary>
        public static bool Block => CoopSession.IsClient && CoopSession.I != null && CoopSession.I.ClientInGame && !CoopSession.Applying;
        public static bool Mirroring => CoopSession.IsClient && CoopSession.I != null && CoopSession.I.ClientInGame;
    }

    [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.TimeScale), MethodType.Getter)]
    static class P_TimeScale
    {
        static void Postfix(ref float __result) { if (Gate.Mirroring) __result = 0f; }
    }

    [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.CanInteract), MethodType.Getter)]
    static class P_CanInteract
    {
        static void Postfix(ref bool __result) { if (Gate.Mirroring && !CoopSession.HostCanInteract) __result = false; }
    }

    [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.CreateCard), new[] { typeof(Vector3), typeof(CardData), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    static class P_CreateCard
    {
        static bool Prefix(ref CardData __result) { if (!Gate.Block) return true; __result = null; return false; }
    }

    [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.ChangeToCard))]
    static class P_ChangeToCard
    {
        static bool Prefix(ref CardData __result, GameCard card) { if (!Gate.Block) return true; __result = card?.CardData; return false; }
    }

    [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.CreateBoosterpack))]
    static class P_CreateBooster
    {
        static bool Prefix(ref Boosterpack __result) { if (!Gate.Block) return true; __result = null; return false; }
    }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.DestroyCard))]
    static class P_DestroyCard
    {
        static bool Prefix() => !Gate.Block;
    }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.StartTimer))]
    static class P_StartTimer { static bool Prefix() => !Gate.Block; }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.StartBlueprintTimer))]
    static class P_StartBlueprintTimer { static bool Prefix() => !Gate.Block; }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.CancelTimer))]
    static class P_CancelTimer { static bool Prefix() => !Gate.Block; }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.CancelAnyTimer))]
    static class P_CancelAnyTimer { static bool Prefix() => !Gate.Block; }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.UpdateTimer))]
    static class P_UpdateTimer { static bool Prefix() => !Gate.Mirroring; }

    [HarmonyPatch(typeof(WorldManager), "EndOfMonth")]
    static class P_EndOfMonth { static bool Prefix() => !Gate.Mirroring; }

    [HarmonyPatch(typeof(WorldManager), "IncrementMonth")]
    static class P_IncrementMonth { static bool Prefix() => !Gate.Mirroring; }

    [HarmonyPatch(typeof(WorldManager), "QueueAnimation")]
    static class P_QueueAnimation { static bool Prefix() => !Gate.Block; }

    [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.GoToBoard))]
    static class P_GoToBoard { static bool Prefix() => !Gate.Block; }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save), new[] { typeof(SaveGame) })]
    static class P_Save
    {
        // the mirrored run must never overwrite one of the player's own save slots
        static bool Prefix(SaveGame saveGame) => saveGame == null || saveGame.SaveId != CoopSession.ClientSaveId;
    }

    [HarmonyPatch(typeof(AchievementHelper), nameof(AchievementHelper.UnlockAchievement))]
    static class P_Achievement { static bool Prefix() => !Gate.Mirroring; }

    [HarmonyPatch(typeof(Draggable), "PushAwayFromOthers")]
    static class P_Push { static bool Prefix() => !Gate.Mirroring; }

    [HarmonyPatch(typeof(WorldManager), "DropCard")]
    static class P_DropCard
    {
        static bool Prefix(WorldManager __instance)
        {
            if (!Gate.Block) return true;
            var d = __instance.DraggingDraggable;
            if (d != null)
            {
                CoopSession.I.ClientDropped(d, __instance.NearbyCardTarget);
                d.StopDragging(); // may report a click (patched below)
                __instance.SetDraggingDraggableToNull();
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.Clicked))]
    static class P_CardClicked
    {
        static bool Prefix(GameCard __instance)
        {
            if (!Gate.Block) return true;
            CoopSession.I.ClientClicked(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(Boosterpack), nameof(Boosterpack.Clicked))]
    static class P_BoosterClicked
    {
        static bool Prefix(Boosterpack __instance)
        {
            if (!Gate.Block) return true;
            CoopSession.I.ClientClicked(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.SellCard))]
    static class P_SellCard
    {
        static bool Prefix(GameCard card, ref GameCard __result)
        {
            if (!Gate.Block) return true;
            CoopSession.I.ClientSell(card);
            __result = null;
            return false;
        }
    }
}

namespace StacklandsCoop
{
    [HarmonyLib.HarmonyPatch(typeof(CutsceneScreen), nameof(CutsceneScreen.CreateMultipleOptions))]
    static class P_Options
    {
        static void Postfix(string[] options)
        {
            CoopSession.CutsceneOptions.Clear();
            if (options != null) CoopSession.CutsceneOptions.AddRange(options);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(CutsceneScreen), nameof(CutsceneScreen.ClearMultipleOptions))]
    static class P_ClearOptions
    {
        static void Postfix() => CoopSession.CutsceneOptions.Clear();
    }
}
