using System.IO;
using CupheadOnline.Sync;
using CupheadOnline.UI;

namespace CupheadOnline.Net
{
    /// <summary>
    /// Routes incoming packets (already on main thread, called from NetManager.Poll)
    /// to the appropriate handler.
    /// </summary>
    public static class PacketDispatcher
    {
        public static void Dispatch(PacketType type, BinaryReader r)
        {
            switch (type)
            {
                // ── Per-frame unreliable ──────────────────────────────────────
                case PacketType.PlayerState:
                {
                    var pkt = new PlayerStatePacket();
                    pkt.Read(r);
                    RemotePlayer.OnStateReceived(pkt);
                    break;
                }

                case PacketType.InputFrame:
                {
                    // Only the HOST receives InputFrames from the client
                    if (!MultiplayerSession.IsHost) break;
                    var pkt = new InputFramePacket();
                    pkt.Read(r);
                    RemoteInputDriver.Apply(pkt);
                    break;
                }

                case PacketType.EnemyState:
                {
                    // Only the CLIENT receives enemy states from the host
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new EnemyStatePacket();
                    pkt.Read(r);
                    EnemySyncManager.OnEnemyStateReceived(pkt);
                    break;
                }

                // ── Reliable events ───────────────────────────────────────────
                case PacketType.WeaponEvent:
                {
                    var pkt = new WeaponEventPacket();
                    pkt.Read(r);
                    RemoteWeaponReplicator.Apply(pkt);
                    break;
                }

                case PacketType.DamageEvent:
                {
                    // Only CLIENT receives damage confirmations from host
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new DamageEventPacket();
                    pkt.Read(r);
                    DamageAuthority.ApplyAuthorized(pkt);
                    break;
                }

                case PacketType.SceneChange:
                {
                    var pkt = new SceneChangePacket();
                    pkt.Read(r);
                    RngSync.SetSeed(pkt.RngSeed);
                    if (!MultiplayerSession.IsHost)
                        SceneLoader.LoadLevel((Levels)pkt.LevelEnum, SceneLoader.Transition.Iris);
                    break;
                }

                case PacketType.MenuSceneChange:
                {
                    var pkt = new MenuSceneChangePacket();
                    pkt.Read(r);
                    RngSync.SetSeed(pkt.RngSeed);
                    if (!MultiplayerSession.IsHost)
                    {
                        SceneLoader.LoadScene(
                            (Scenes)pkt.SceneEnum,
                            (SceneLoader.Transition)pkt.TransitionStart,
                            (SceneLoader.Transition)pkt.TransitionEnd,
                            (SceneLoader.Icon)pkt.Icon,
                            null);
                    }
                    break;
                }

                case PacketType.LobbySync:
                {
                    var pkt = new LobbySyncPacket();
                    pkt.Read(r);
                    LoadoutReplicator.Apply(pkt);
                    break;
                }

                case PacketType.SaveSlotSync:
                {
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new SaveSlotSyncPacket();
                    pkt.Read(r);
                    SaveSlotReplicator.Apply(pkt);
                    break;
                }

                case PacketType.SaveProfile:
                {
                    var pkt = new SaveProfilePacket();
                    pkt.Read(r);
                    SessionSync.ApplyRemoteSaveProfile(pkt);
                    break;
                }

                case PacketType.SessionSnapshot:
                {
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new SessionSnapshotPacket();
                    pkt.Read(r);
                    SessionSync.ApplyHostSnapshot(pkt);
                    break;
                }

                case PacketType.SessionSignal:
                {
                    var pkt = new SessionSignalPacket();
                    pkt.Read(r);
                    SessionSync.ApplySessionSignal(pkt);
                    break;
                }

                case PacketType.SessionStart:
                {
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new SessionStartPacket();
                    pkt.Read(r);
                    RngSync.SetSeed(pkt.RngSeed);
                    if (!MultiplayerSession.IsActive || MultiplayerSession.IsHost)
                        MultiplayerSession.StartAsClient();
                    if (pkt.IsInLevel && pkt.CurrentLevel >= 0)
                        SceneLoader.LoadLevel((Levels)pkt.CurrentLevel, SceneLoader.Transition.Iris);
                    break;
                }

                // Handshake packets are consumed by SteamNetManager before reaching here
                case PacketType.Hello:
                case PacketType.Welcome:
                case PacketType.Ready:
                case PacketType.Ping:
                case PacketType.Pong:
                    break;

                default:
                    Plugin.Log.LogWarning("[Dispatcher] Unknown packet type: " + type);
                    break;
            }
        }
    }
}
