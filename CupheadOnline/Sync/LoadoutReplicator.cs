using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Applies the remote player's loadout (weapons / super / charm / character)
    /// so the correct sprites, animations, and mechanics load for their avatar.
    ///
    /// Flow:
    ///   1. During the lobby both sides call SendLobbySync with their local loadout.
    ///   2. PacketDispatcher calls Apply() with the received packet.
    ///   3. We store it in _pending; the PlayerManagerPatch (StatsLevelInitPatch)
    ///      reads it when the remote player's stats are initialised.
    /// </summary>
    public static class LoadoutReplicator
    {
        private static LobbySyncPacket? _pending;

        public static void Apply(LobbySyncPacket pkt)
        {
            _pending = pkt;
            Plugin.Log.LogInfo(
                $"[Loadout] Received remote loadout for Player {pkt.PlayerId}: " +
                $"W1={pkt.Weapon1} W2={pkt.Weapon2} Super={pkt.Super} Charm={pkt.Charm} Chalice={pkt.IsChalice}");
        }

        public static void ApplyPending(PlayerId id)
        {
            if (!_pending.HasValue) return;
            var pkt = _pending.Value;
            if (pkt.PlayerId != (byte)id) return;

            var player = PlayerManager.GetPlayer(id);
            if (player == null) return;
            var stats = player.stats;
            if (stats == null) return;

            // Apply loadout to the remote player's stats
            var loadout = stats.Loadout;
            loadout.primaryWeapon   = (Weapon)pkt.Weapon1;
            loadout.secondaryWeapon = (Weapon)pkt.Weapon2;
            loadout.super           = (Super)pkt.Super;
            loadout.charm           = (Charm)pkt.Charm;
            // Loadout setter may be non-public; use Traverse to bypass access check
            HarmonyLib.Traverse.Create(stats).Property("Loadout").SetValue(loadout);

            _pending = null;
        }

        /// <summary>
        /// Call this from the lobby screen (before level load) to send our loadout.
        /// </summary>
        public static void BroadcastLocalLoadout()
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var player = MultiplayerSession.GetLocalController();
            if (player == null) return;
            var stats = player.stats;
            if (stats == null) return;

            var pkt = new LobbySyncPacket
            {
                PlayerId  = (byte)MultiplayerSession.LocalId,
                Weapon1   = (byte)stats.Loadout.primaryWeapon,
                Weapon2   = (byte)stats.Loadout.secondaryWeapon,
                Super     = (byte)stats.Loadout.super,
                Charm     = (byte)stats.Loadout.charm,
                IsChalice = (byte)(stats.isChalice ? 1 : 0),
            };
            Plugin.Net.SendLobbySync(ref pkt);
        }
    }
}
