using System;
using System.Reflection;

namespace CupheadOnline.Coop
{
    /// <summary>
    /// Forces Cuphead's PlayerSlot[1] into the "Joined" state so P2 actually spawns
    /// when a network session is active. Without this, upstream's mod sets
    /// PlayerManager.Multiplayer = true but never advances P2 past the JoinPromptDisplayed
    /// state — Cuphead is waiting for a controller button-press that never comes (the
    /// "controller" is the remote player on the other PC). Result: only P1 ever spawns,
    /// the local user on client mode has no avatar to control via keyboard.
    ///
    /// Mechanism: reflect into PlayerManager's private state and apply the same
    /// transitions Cuphead's own Update would apply on a controller press:
    ///   playerSlots[1].canJoin         = true
    ///   playerSlots[1].joinState       = JoinState.Joined
    ///   playerSlots[1].controllerState = ControllerState.NoController
    ///   PlayerManager.Multiplayer      = true
    /// Then fire OnPlayerJoinedEvent(PlayerOne|PlayerTwo) via the backing delegate so
    /// level-end / level-load / spawn subscribers run their handlers.
    ///
    /// Idempotent: skips if P2 is already Joined. Hooked to MultiplayerSession.OnSessionStarted
    /// so it fires on host AND client when the lobby opens.
    /// </summary>
    internal static class P2AutoJoin
    {
        // Cached reflection handles. Resolved on first use, then reused.
        private static FieldInfo _slotsField;
        private static FieldInfo _multiplayerField;
        private static FieldInfo _eventField;
        private static FieldInfo _slotJoinStateField;
        private static FieldInfo _slotCanJoinField;
        private static FieldInfo _slotControllerStateField;
        private static object _joinStateJoinedValue;
        private static object _controllerStateNoController;
        private static bool _reflectionResolved;

        // Pending flag — the call to PlayerManager reflection has to run on the Unity
        // main thread; OnSessionStarted may fire from network callbacks. Set this and
        // let the next CoopBootstrap.Tick drain it.
        private static volatile bool _pending;

        public static void Trigger() { _pending = true; }

        public static void TickIfPending()
        {
            if (!_pending) return;
            _pending = false;

            try
            {
                if (!ResolveReflection()) return;

                var slots = (Array)_slotsField.GetValue(null);
                if (slots == null || slots.Length < 2) { _pending = true; return; } // retry

                var p2Slot = slots.GetValue(1);
                if (p2Slot == null) { _pending = true; return; }

                var current = _slotJoinStateField.GetValue(p2Slot);
                if (Equals(current, _joinStateJoinedValue))
                {
                    Plugin.Log.LogInfo("[P2AutoJoin] P2 already joined — nothing to do.");
                    return;
                }

                _slotCanJoinField.SetValue(p2Slot, true);
                _slotJoinStateField.SetValue(p2Slot, _joinStateJoinedValue);
                _slotControllerStateField.SetValue(p2Slot, _controllerStateNoController);
                _multiplayerField.SetValue(null, true);

                FireOnPlayerJoinedEvent();

                Plugin.Log.LogInfo("[P2AutoJoin] Forced P2 to Joined and fired OnPlayerJoinedEvent.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[P2AutoJoin] Failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool ResolveReflection()
        {
            if (_reflectionResolved) return true;

            var pmType = typeof(global::PlayerManager);
            _slotsField = pmType.GetField("playerSlots", BindingFlags.NonPublic | BindingFlags.Static);
            _multiplayerField = pmType.GetField("Multiplayer", BindingFlags.Public | BindingFlags.Static);
            _eventField = pmType.GetField("OnPlayerJoinedEvent", BindingFlags.NonPublic | BindingFlags.Static);

            if (_slotsField == null) { Plugin.Log.LogError("[P2AutoJoin] playerSlots field not found"); return false; }
            if (_multiplayerField == null) { Plugin.Log.LogError("[P2AutoJoin] Multiplayer field not found"); return false; }

            // PlayerSlot is a private nested type — probe via an actual instance.
            var slots = (Array)_slotsField.GetValue(null);
            if (slots == null || slots.Length == 0) return false;
            var slotType = slots.GetValue(0).GetType();

            _slotJoinStateField = slotType.GetField("joinState", BindingFlags.Public | BindingFlags.Instance);
            _slotCanJoinField = slotType.GetField("canJoin", BindingFlags.Public | BindingFlags.Instance);
            _slotControllerStateField = slotType.GetField("controllerState", BindingFlags.Public | BindingFlags.Instance);

            if (_slotJoinStateField == null || _slotCanJoinField == null || _slotControllerStateField == null)
            {
                Plugin.Log.LogError("[P2AutoJoin] PlayerSlot fields not found (joinState/canJoin/controllerState)");
                return false;
            }

            _joinStateJoinedValue = Enum.Parse(_slotJoinStateField.FieldType, "Joined");
            _controllerStateNoController = Enum.Parse(_slotControllerStateField.FieldType, "NoController");

            _reflectionResolved = true;
            return true;
        }

        private static void FireOnPlayerJoinedEvent()
        {
            if (_eventField == null) return;
            var del = _eventField.GetValue(null) as MulticastDelegate;
            if (del == null) return;
            try { del.DynamicInvoke(global::PlayerId.PlayerTwo); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[P2AutoJoin] OnPlayerJoinedEvent subscriber threw: " + ex.GetType().Name);
            }
        }
    }
}
