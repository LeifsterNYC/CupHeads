using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Coop
{
    /// <summary>
    /// Phase 1 player sync. Each side runs its own LOCAL player's motor with real input
    /// (responsive). Each side BYPASSES the remote player's motor (Harmony prefix in
    /// <see cref="RemotePlayerMotorBypass"/>) and renders that cup as a pure puppet
    /// driven by snapshots from the other side.
    ///
    /// Symmetric:
    ///   - Host's local = PlayerOne. Host captures P1 state, sends. Client renders host's
    ///     P1 as a puppet at host's authoritative position.
    ///   - Client's local = PlayerTwo. Client captures P2 state, sends. Host renders P2
    ///     as a puppet (since host's P2 motor is bypassed too — Phase 1 does NOT yet
    ///     forward client's input into host's P2 motor; that's Phase 2A).
    ///
    /// On host: P1 motor runs normal. P2 motor bypassed → puppeted from client's stream.
    /// On client: P2 motor runs normal. P1 motor bypassed → puppeted from host's stream.
    /// </summary>
    internal static class PlayerStateSync
    {
        // Send rate. 30Hz is the sweet spot: visible smoothness with cheap interpolation,
        // bandwidth ~720 B/s per player (~1.4 KB/s total in 2-player).
        const float SendIntervalSec = 1f / 30f;

        // Buffers indexed by PlayerId byte. Holds the latest snapshot received from the
        // network (one per player). The motor-bypass patch reads these and applies.
        private static readonly Dictionary<byte, PlayerStatePacket> _latest = new Dictionary<byte, PlayerStatePacket>();
        // Track tick-per-player so we ignore reordered/duplicate UDP packets.
        private static readonly Dictionary<byte, uint> _lastTick = new Dictionary<byte, uint>();

        private static float _sinceLastSend;
        private static uint _localTickCounter;

        // Diagnostic counters surfaced to the BepInEx log every N seconds so we can tell
        // whether the sync layer is actually firing in-game.
        private static int _sentCount;
        private static int _recvCount;
        private static float _sinceLastDiag;
        private static bool _firstTickLogged;

        public static bool TryGetLatest(byte playerId, out PlayerStatePacket pkt)
            => _latest.TryGetValue(playerId, out pkt);

        public static void Reset()
        {
            _latest.Clear();
            _lastTick.Clear();
            _sinceLastSend = 0f;
            _localTickCounter = 0;
        }

        /// <summary>
        /// Drive both the outbound capture+send AND the inbound poll. Called once per Update
        /// from <see cref="CoopBootstrap"/>. Steam P2P delivery is reliable enough that we
        /// don't need a separate fast-poll loop; once-per-frame is fine for 30Hz traffic.
        /// </summary>
        public static void Tick(float dt)
        {
            // Throttled diagnostic — fires once per second whether or not we're in a
            // session, so a "no logs" symptom is distinguishable from "Tick never runs".
            _sinceLastDiag += dt;
            if (_sinceLastDiag >= 1f)
            {
                _sinceLastDiag = 0f;
                Plugin.Log.LogInfo(string.Format(
                    "[Coop] diag: sessionActive={0} isHost={1} netConnected={2} canSend={3} peer={4} sent/s={5} recv/s={6}",
                    MultiplayerSession.IsActive,
                    MultiplayerSession.IsHost,
                    Plugin.Net != null && Plugin.Net.IsConnected,
                    CoopTransport.CanSend,
                    (Plugin.Net != null && Plugin.Net.RemoteSteamId != Steamworks.CSteamID.Nil)
                        ? Plugin.Net.RemoteSteamId.m_SteamID.ToString() : "(none)",
                    _sentCount, _recvCount));
                _sentCount = 0;
                _recvCount = 0;
            }

            if (!MultiplayerSession.IsActive) return;
            if (!CoopTransport.CanSend) return;

            if (!_firstTickLogged)
            {
                _firstTickLogged = true;
                Plugin.Log.LogInfo(string.Format(
                    "[Coop] sync ACTIVE — local={0} remote={1} channel={2}",
                    LocalPlayerId, RemotePlayerId, CoopProtocol.ChannelIndex));
            }

            // 1. Inbound — drain any received state packets.
            CoopTransport.Poll(OnPacket);

            // 2. Outbound — capture + send the LOCAL player's state at fixed interval.
            _sinceLastSend += dt;
            if (_sinceLastSend < SendIntervalSec) return;
            _sinceLastSend = 0f;
            CaptureAndSendLocal();
        }

        // Each side's "local" PlayerId. Per upstream's MultiplayerSession convention:
        //   Host => local=PlayerOne, remote=PlayerTwo
        //   Client => local=PlayerTwo, remote=PlayerOne
        public static global::PlayerId LocalPlayerId =>
            MultiplayerSession.IsHost ? global::PlayerId.PlayerOne : global::PlayerId.PlayerTwo;
        public static global::PlayerId RemotePlayerId =>
            MultiplayerSession.IsHost ? global::PlayerId.PlayerTwo : global::PlayerId.PlayerOne;

        private static void CaptureAndSendLocal()
        {
            var id = LocalPlayerId;
            // PlayerManager.GetPlayer can return null between scene transitions.
            global::AbstractPlayerController controller = null;
            try { controller = global::PlayerManager.GetPlayer(id); } catch { return; }
            if (controller == null) return;

            var t = controller.transform;
            if (t == null) return;
            var pos = t.position;

            // Pull motor + animator state via reflection so this works for whichever
            // motor subclass (Level / Arcade / Map / Plane) is on this controller.
            sbyte lookX = 0, lookY = 0;
            byte flags = 0;
            int animHash = 0;

            var motor = controller.GetComponentInChildren<MonoBehaviour>(); // placeholder; refined below
            // Try the common motor types in order.
            var levelMotor = controller.GetComponentInChildren<LevelPlayerMotor>();
            var arcadeMotor = controller.GetComponentInChildren<ArcadePlayerMotor>();
            var mapMotor = controller.GetComponentInChildren<MapPlayerMotor>();

            if (levelMotor != null)
            {
                lookX = (sbyte)levelMotor.LookDirection.x.Value;
                lookY = (sbyte)levelMotor.LookDirection.y.Value;
                flags = PlayerStatePacket.BuildFlags(
                    grounded: levelMotor.Grounded,
                    dashing: levelMotor.Dashing,
                    ducking: levelMotor.Ducking,
                    gravReversed: levelMotor.GravityReversed,
                    isHit: levelMotor.IsHit,
                    isSuper: levelMotor.IsUsingSuperOrEx,
                    isDead: controller.IsDead);
            }
            else if (arcadeMotor != null)
            {
                lookX = (sbyte)arcadeMotor.LookDirection.x.Value;
                lookY = (sbyte)arcadeMotor.LookDirection.y.Value;
                flags = PlayerStatePacket.BuildFlags(
                    grounded: arcadeMotor.Grounded,
                    dashing: arcadeMotor.Dashing,
                    ducking: false,                        // ArcadePlayerMotor has no Ducking property
                    gravReversed: false,
                    isHit: false,
                    isSuper: false,
                    isDead: controller.IsDead);
            }
            else if (mapMotor != null)
            {
                // Map motor is much simpler — direction-only, no jump/dash combat state.
                lookX = (sbyte)Mathf.Sign(t.localScale.x);
                flags = PlayerStatePacket.BuildFlags(true, false, false, false, false, false, false);
            }

            // Animator state — first child Animator with a runtime controller bound.
            var animator = controller.GetComponentInChildren<Animator>();
            if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
            {
                animHash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
            }

            var pkt = new PlayerStatePacket
            {
                PlayerId = (byte)id,
                Tick = ++_localTickCounter,
                PosX = pos.x,
                PosY = pos.y,
                LookX = lookX,
                LookY = lookY,
                Flags = flags,
                AnimStateHash = animHash
            };
            CoopTransport.Send(pkt);
            _sentCount++;
        }

        private static void OnPacket(Steamworks.CSteamID sender, BinaryReader r)
        {
            // Empty body shouldn't happen but defend against it.
            if (r.BaseStream.Length < 1) return;
            var type = (CoopPacketType)r.ReadByte();
            switch (type)
            {
                case CoopPacketType.PlayerState:
                {
                    var p = PlayerStatePacket.Read(r);
                    // Drop stale or duplicate packets.
                    uint last;
                    if (_lastTick.TryGetValue(p.PlayerId, out last) && p.Tick != 0 && p.Tick <= last)
                        return;
                    _lastTick[p.PlayerId] = p.Tick;
                    _latest[p.PlayerId] = p;
                    _recvCount++;
                    break;
                }
                default:
                    // Unknown / future packet type — silently drop. Upgrade-friendly.
                    break;
            }
        }
    }

    /// <summary>
    /// Harmony prefix on the three motor subclasses' FixedUpdate. When the player whose
    /// motor is ticking is the REMOTE player on this side, skip the original body and
    /// drive the cup from the latest <see cref="PlayerStatePacket"/>.
    ///
    /// Position is lerped (smooth catchup, no snap). Direction-state properties are
    /// force-set via <see cref="Traverse"/> so animator + weapon manager + aim logic
    /// see coherent values without us reaching into private fields one-by-one.
    /// </summary>
    [HarmonyPatch]
    internal static class RemotePlayerMotorBypass
    {
        // Per-FixedUpdate lerp factor. 20 unit/sec catches up in ~3 ticks at 60Hz; visually
        // smooth, no rubber-band feel for typical gameplay positions.
        const float LerpRate = 20f;

        // ── LevelPlayerMotor (boss + run-and-gun gameplay) ────────────────────
        [HarmonyPatch(typeof(LevelPlayerMotor), "FixedUpdate")]
        [HarmonyPrefix]
        static bool LevelMotor_Prefix(LevelPlayerMotor __instance)
        {
            if (!ShouldBypass(__instance.player?.id)) return true;
            byte remoteId = (byte)PlayerStateSync.RemotePlayerId;
            PlayerStatePacket s;
            if (!PlayerStateSync.TryGetLatest(remoteId, out s)) return false; // bypass anyway, no source
            ApplyToLevel(__instance, s);
            return false;
        }

        static void ApplyToLevel(LevelPlayerMotor m, PlayerStatePacket s)
        {
            try
            {
                var t = m.transform;
                var target = new Vector3(s.PosX, s.PosY, t.position.z);
                t.position = Vector3.Lerp(t.position, target, Mathf.Min(1f, LerpRate * Time.fixedDeltaTime));

                var trav = Traverse.Create(m);
                trav.Property("LookDirection").SetValue(new global::Trilean2(s.LookX, s.LookY));
                trav.Property("TrueLookDirection").SetValue(new global::Trilean2(s.LookX, s.LookY));
                trav.Property("MoveDirection").SetValue(new global::Trilean2(s.LookX, 0));
                trav.Property("Grounded").SetValue(s.Grounded);
                trav.Property("GravityReversed").SetValue(s.GravReversed);
            }
            catch { /* mid-scene-transition reflection failures swallowed */ }
        }

        // ── ArcadePlayerMotor (some arcade scenes) ────────────────────────────
        [HarmonyPatch(typeof(ArcadePlayerMotor), "FixedUpdate")]
        [HarmonyPrefix]
        static bool ArcadeMotor_Prefix(ArcadePlayerMotor __instance)
        {
            if (!ShouldBypass(__instance.player?.id)) return true;
            byte remoteId = (byte)PlayerStateSync.RemotePlayerId;
            PlayerStatePacket s;
            if (!PlayerStateSync.TryGetLatest(remoteId, out s)) return false;
            ApplyToArcade(__instance, s);
            return false;
        }

        static void ApplyToArcade(ArcadePlayerMotor m, PlayerStatePacket s)
        {
            try
            {
                var t = m.transform;
                var target = new Vector3(s.PosX, s.PosY, t.position.z);
                t.position = Vector3.Lerp(t.position, target, Mathf.Min(1f, LerpRate * Time.fixedDeltaTime));

                var trav = Traverse.Create(m);
                trav.Property("LookDirection").SetValue(new global::Trilean2(s.LookX, s.LookY));
                trav.Property("TrueLookDirection").SetValue(new global::Trilean2(s.LookX, s.LookY));
                trav.Property("MoveDirection").SetValue(new global::Trilean2(s.LookX, 0));
                trav.Property("Grounded").SetValue(s.Grounded);
            }
            catch { }
        }

        // ── MapPlayerMotor (overworld) ────────────────────────────────────────
        [HarmonyPatch(typeof(MapPlayerMotor), "FixedUpdate")]
        [HarmonyPrefix]
        static bool MapMotor_Prefix(MapPlayerMotor __instance)
        {
            if (!ShouldBypass(__instance.player?.id)) return true;
            byte remoteId = (byte)PlayerStateSync.RemotePlayerId;
            PlayerStatePacket s;
            if (!PlayerStateSync.TryGetLatest(remoteId, out s)) return false;
            ApplyToMap(__instance, s);
            return false;
        }

        static void ApplyToMap(MapPlayerMotor m, PlayerStatePacket s)
        {
            try
            {
                var t = m.transform;
                var target = new Vector3(s.PosX, s.PosY, t.position.z);
                t.position = Vector3.Lerp(t.position, target, Mathf.Min(1f, LerpRate * Time.fixedDeltaTime));
                if (s.LookX != 0)
                {
                    var sc = t.localScale;
                    sc.x = Mathf.Abs(sc.x) * s.LookX;
                    t.localScale = sc;
                }
            }
            catch { }
        }

        // Bypass condition: in an active session, AND the player whose motor is ticking
        // is the remote player on this side, AND a session peer exists. Skip otherwise.
        static bool ShouldBypass(global::PlayerId? id)
        {
            if (!id.HasValue) return false;
            if (!MultiplayerSession.IsActive) return false;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return false;
            return id.Value == PlayerStateSync.RemotePlayerId;
        }
    }
}
