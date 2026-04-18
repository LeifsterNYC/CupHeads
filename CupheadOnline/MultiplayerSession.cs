using System;
using UnityEngine;

namespace CupheadOnline
{
    /// <summary>
    /// Central session state — single source of truth for whether we are in a network game,
    /// which role we hold, and what the current tick counter is.
    /// All other systems query this rather than holding their own state.
    /// </summary>
    public static class MultiplayerSession
    {
        // ──────────────────────────────────────────────────────────────────────
        //  Session lifecycle
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>True while a network session (host OR client) is running.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>True when this instance is the authoritative host.</summary>
        public static bool IsHost { get; private set; }

        /// <summary>True when this instance is a connected client.</summary>
        public static bool IsClient => IsActive && !IsHost;

        /// <summary>Monotonically increasing FixedUpdate tick. Reset on session start.</summary>
        public static uint Tick { get; private set; }

        // ──────────────────────────────────────────────────────────────────────
        //  Player ID assignments
        //    Host   → always PlayerId.PlayerOne  (local)
        //    Client → always PlayerId.PlayerTwo  (local)
        // ──────────────────────────────────────────────────────────────────────

        public static PlayerId LocalId  => IsHost ? PlayerId.PlayerOne : PlayerId.PlayerTwo;
        public static PlayerId RemoteId => IsHost ? PlayerId.PlayerTwo : PlayerId.PlayerOne;
        public static int ActivePlayerCount
        {
            get
            {
                int count = 0;
                if (PlayerManager.GetPlayer(PlayerId.PlayerOne) != null) count++;
                if (PlayerManager.GetPlayer(PlayerId.PlayerTwo) != null) count++;

                if (count > 0)
                    return count;

                return IsActive || PlayerManager.Multiplayer ? 2 : 1;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Events
        // ──────────────────────────────────────────────────────────────────────

        public static event Action OnSessionStarted;
        public static event Action OnSessionEnded;

        // ──────────────────────────────────────────────────────────────────────
        //  Lifecycle helpers (called by NetManager)
        // ──────────────────────────────────────────────────────────────────────

        public static void StartAsHost()
        {
            IsActive = true;
            IsHost   = true;
            Tick     = 0;
            Plugin.Log.LogInfo("[Session] Started as HOST");
            OnSessionStarted?.Invoke();
        }

        public static void StartAsClient()
        {
            IsActive = true;
            IsHost   = false;
            Tick     = 0;
            Plugin.Log.LogInfo("[Session] Started as CLIENT");
            OnSessionStarted?.Invoke();
        }

        public static void End()
        {
            if (!IsActive) return;
            IsActive = false;
            IsHost   = false;
            Tick     = 0;
            Plugin.Log.LogInfo("[Session] Ended");
            OnSessionEnded?.Invoke();
        }

        /// <summary>Called every FixedUpdate from PlayerMotorPatch.</summary>
        public static void IncrementTick() => Tick++;

        // ──────────────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────────────

        public static bool IsLocalPlayer(PlayerId id)  => IsActive && id == LocalId;
        public static bool IsRemotePlayer(PlayerId id) => IsActive && id == RemoteId;

        /// <summary>
        /// Safe way to get a player controller — returns null if not yet spawned.
        /// </summary>
        public static LevelPlayerController GetLocalController()
        {
            var p = PlayerManager.GetPlayer(LocalId);
            return p as LevelPlayerController;
        }

        public static LevelPlayerController GetRemoteController()
        {
            var p = PlayerManager.GetPlayer(RemoteId);
            return p as LevelPlayerController;
        }

        public static string GetPrimaryCharacterName()
        {
            return GetCharacterName(PlayerId.PlayerOne);
        }

        public static string GetSecondaryCharacterName()
        {
            return GetCharacterName(PlayerId.PlayerTwo);
        }

        public static string GetLocalCharacterName()
        {
            return GetCharacterName(IsActive ? LocalId : PlayerId.PlayerOne);
        }

        public static string GetRemoteCharacterName()
        {
            return GetCharacterName(IsActive ? RemoteId : PlayerId.PlayerTwo);
        }

        public static string GetCharacterName(PlayerId id)
        {
            try
            {
                var player = PlayerManager.GetPlayer(id);
                if (player != null && player.stats != null && player.stats.isChalice)
                    return "Ms. Chalice";

                if (PlayerData.Data != null && PlayerData.Data.Loadouts != null)
                {
                    var loadout = PlayerData.Data.Loadouts.GetPlayerLoadout(id);
                    if (loadout.charm == Charm.charm_chalice)
                        return "Ms. Chalice";
                }
            }
            catch
            {
            }

            bool playerOneIsMugman = PlayerManager.player1IsMugman;
            if (id == PlayerId.PlayerOne)
                return playerOneIsMugman ? "Mugman" : "Cuphead";
            if (id == PlayerId.PlayerTwo)
                return playerOneIsMugman ? "Cuphead" : "Mugman";
            return "Unknown";
        }
    }
}
