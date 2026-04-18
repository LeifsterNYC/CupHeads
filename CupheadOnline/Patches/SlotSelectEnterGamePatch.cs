using System.Reflection;
using HarmonyLib;
using CupheadOnline.Net;
using CupheadOnline.UI;

namespace CupheadOnline.Patches
{
    [HarmonyPatch(typeof(SlotSelectScreen), "EnterGame")]
    public static class SlotSelectEnterGamePatch
    {
        static readonly BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly FieldInfo SlotSelectionField =
            typeof(SlotSelectScreen).GetField("_slotSelection", BF);
        static readonly FieldInfo SlotsField =
            typeof(SlotSelectScreen).GetField("slots", BF);

        static bool Prefix(SlotSelectScreen __instance)
        {
            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsHost) return true;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return true;
            if (SlotSelectionField == null || SlotsField == null) return true;

            var slots = SlotsField.GetValue(__instance) as SlotSelectScreenSlot[];
            if (slots == null || slots.Length == 0) return true;

            int slotIndex = (int)SlotSelectionField.GetValue(__instance);
            if (slotIndex < 0 || slotIndex >= slots.Length) return true;

            var slot = slots[slotIndex];
            if (slot == null) return true;

            bool isEmpty = slot.IsEmpty;
            Scenes currentMap = Scenes.scene_map_world_1;
            if (!isEmpty)
            {
                var data = PlayerData.GetDataForSlot(slotIndex);
                if (data != null)
                    currentMap = data.CurrentMap;

                if (!DLCManager.DLCEnabled() && currentMap == Scenes.scene_map_world_DLC)
                    currentMap = Scenes.scene_map_world_1;
            }

            var pkt = new SaveSlotSyncPacket
            {
                SlotIndex       = (byte)slotIndex,
                Flags           = (byte)((isEmpty ? 1 : 0) | (slot.isPlayer1Mugman ? 2 : 0)),
                SaveRevision    = 0,
                CurrentMapScene = (int)currentMap,
            };
            Sync.SessionSync.RecordSelectedSave(ref pkt);
            Plugin.Net.SendSaveSlotSync(ref pkt);
            Sync.SessionSync.BroadcastSelectedSaveProfile();
            Sync.SessionSync.BroadcastSessionSnapshot(true);

            string gateReason;
            if (!Sync.SessionSync.CanHostStartRun(out gateReason))
            {
                ConnectionHUD.Show(gateReason);
                Plugin.Log.LogInfo("[SaveSync] Host start blocked: " + gateReason);
                return false;
            }

            Plugin.Log.LogInfo(
                "[SaveSync] Broadcast host slot "
                + slotIndex
                + " (map="
                + currentMap
                + ", empty="
                + isEmpty
                + ", rev="
                + pkt.SaveRevision
                + ", mugman="
                + slot.isPlayer1Mugman
                + ").");
            return true;
        }
    }
}
