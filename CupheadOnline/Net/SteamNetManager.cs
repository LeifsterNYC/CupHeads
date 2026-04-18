using System;
using System.IO;
using Steamworks;
using UnityEngine;
using CupheadOnline.Sync;
using CupheadOnline.UI;

namespace CupheadOnline.Net
{
    // ────────────────────────────────────────────────────────────────────────────
    //  HANDSHAKE STATE MACHINE
    //
    //  HOST side:
    //    Idle
    //    → CreatingLobby   (CreateLobby call in flight)
    //    → WaitingInLobby  (lobby ready, invite overlay shown, waiting for knock)
    //    → WaitingHello    (P2PSessionRequest accepted)
    //    → WaitingReady    (Welcome sent, waiting for Ready)
    //    → Connected       (Ready received, game can sync)
    //
    //  CLIENT side:
    //    Idle
    //    → JoiningLobby    (JoinLobby call in flight)
    //    → WaitingWelcome  (Hello sent, waiting for Welcome)
    //    → Connected       (Ready sent and first game-packet ACKed)
    //
    //  Any failure/timeout at any state → Error state, status text updated.
    //  Disconnect while Connected → back to WaitingInLobby (host) or Error (client).
    // ────────────────────────────────────────────────────────────────────────────

    public sealed class SteamNetManager
    {
        // ── Public ────────────────────────────────────────────────────────────
        public bool IsSteamReady => _steamReady;
        public bool IsConnected  => _state == NetState.Connected;
        public bool IsHost       => _isHost;
        public int  Latency      { get; private set; }
        public string SteamUnavailableStatus => _steamUnavailableStatus;
        public string LastStatusMessage => _lastStatusMessage;
        public string LastFailureReason => _lastFailureReason;
        public string CurrentStateName => _state.ToString();
        public string CurrentPeerName => FriendName(_peerId);
        public string CurrentLobbyId => _lobbyId == CSteamID.Nil ? string.Empty : _lobbyId.m_SteamID.ToString();

        public bool CanInviteFriend => _steamReady && _isHost && _lobbyId != CSteamID.Nil;
        public bool CanCopyLobbyId => _steamReady && _lobbyId != CSteamID.Nil;
        public bool CanRetryLastAction => _lastRetryIntent != RetryIntent.None;
        public bool CanOpenSaveSlot => _steamReady && _state == NetState.Connected && _isHost;
        public bool CanRequestRecovery => _steamReady && _state == NetState.Connected;

        /// <summary>True while a critical async operation is in flight (host/join pending).</summary>
        public bool IsInputLocked => _state == NetState.CreatingLobby
                                  || _state == NetState.JoiningLobby
                                  || _state == NetState.WaitingHello
                                  || _state == NetState.WaitingWelcome
                                  || _state == NetState.WaitingReady;

        /// <summary>True when waiting in lobby indefinitely (no handshake timeout applies).</summary>
        public bool IsWaitingIndefinitely => _state == NetState.WaitingInLobby;

        /// <summary>True when we are in a Steam lobby (host or guest).</summary>
        public bool IsInLobby => _lobbyId != CSteamID.Nil;

        /// <summary>When the current state was entered (Time.realtimeSinceStartup).</summary>
        public float StateEnteredTime => _stateEnteredTime;

        public bool ShouldForceUnlockUi(float now)
        {
            if (!_steamReady) return false;

            switch (_state)
            {
                case NetState.CreatingLobby:
                case NetState.JoiningLobby:
                    return now - _stateEnteredTime > LOBBY_TIMEOUT + 5f;

                case NetState.WaitingHello:
                case NetState.WaitingWelcome:
                case NetState.WaitingReady:
                    return now - _stateEnteredTime > HANDSHAKE_TIMEOUT + 5f;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns a multi-line string with lobby member names and connection state,
        /// suitable for the presence panel in the MP menu.
        /// Returns empty string when not in a lobby.
        /// </summary>
        public string GetLobbyPresence()
        {
            if (!_steamReady || _lobbyId == CSteamID.Nil) return string.Empty;
            int n = SteamMatchmaking.GetNumLobbyMembers(_lobbyId);
            if (n <= 0) return string.Empty;

            CSteamID localId = SteamUser.GetSteamID();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("LOBBY #" + _lobbyId.m_SteamID);
            for (int i = 0; i < n; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, i);
                string name = (member == localId)
                    ? "You"
                    : SteamFriends.GetFriendPersonaName(member);
                string role   = (i == 0) ? "Host" : "Guest";
                string suffix = string.Empty;

                // Annotate the remote peer with handshake progress
                if (member != localId && member == _peerId)
                {
                    switch (_state)
                    {
                        case NetState.WaitingHello:
                        case NetState.WaitingWelcome:
                        case NetState.WaitingReady:
                            suffix = " (Connecting\u2026)";
                            break;
                        case NetState.Connected:
                            suffix = " (Connected)";
                            break;
                    }
                }
                sb.AppendLine(role + ": " + name + suffix);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Fired on the main thread with human-readable status.</summary>
        public Action<string> OnStatusChanged;

        /// <summary>Fired on the main thread when the Steam overlay is closed by the user.</summary>
        public Action OnOverlayClosed;

        public string GetSteamBadgeText()
        {
            if (!_steamReady)
            {
                if (_steamUnavailableStatus.IndexOf("steam_appid.txt", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "NOT VIA STEAM";
                return "STEAM OFFLINE";
            }

            if (!IsOverlayEnabled())
                return "OVERLAY OFF";
            if (_state == NetState.Connected)
                return "CONNECTED";
            if (_lobbyId != CSteamID.Nil)
                return _isHost ? "HOSTING LOBBY" : "IN LOBBY";
            return "STEAM READY";
        }

        public string GetRetryActionLabel()
        {
            switch (_lastRetryIntent)
            {
                case RetryIntent.Host:
                    return _lastDisconnectWasConnected ? "REOPEN LOBBY" : "RETRY HOST";
                case RetryIntent.JoinLobby:
                    if (_lastDisconnectWasConnected)
                        return _lastRetryLobbyId == CSteamID.Nil ? "RECONNECT" : "REJOIN RUN";
                    return _lastRetryLobbyId == CSteamID.Nil ? "RETRY JOIN" : "REJOIN LOBBY";
                default:
                    return "RETRY LAST";
            }
        }

        public bool OpenInviteDialog(out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }
            if (!_isHost || _lobbyId == CSteamID.Nil)
            {
                status = "Host a lobby first, then invite a friend.";
                return false;
            }
            if (!IsOverlayEnabled())
            {
                status = "Steam overlay is unavailable.\nEnable the overlay and try again.";
                return false;
            }

            SteamFriends.ActivateGameOverlayInviteDialog(_lobbyId);
            status = "Invite dialog opened for lobby #" + _lobbyId.m_SteamID + ".";
            Plugin.LogVerbose("[SteamNet] Invite dialog opened.");
            return true;
        }

        public bool OpenFriendsOverlay(out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }
            if (!IsOverlayEnabled())
            {
                status = "Steam overlay is unavailable.\nEnable the overlay and try again.";
                return false;
            }

            SteamFriends.ActivateGameOverlay("Friends");
            status = "Steam overlay opened.\nWaiting for invite...";
            Plugin.LogVerbose("[SteamNet] Friends overlay opened.");
            return true;
        }

        public bool TryRetryLastAction(out string status)
        {
            status = string.Empty;

            switch (_lastRetryIntent)
            {
                case RetryIntent.Host:
                    if (StartHost())
                    {
                        status = "Retrying host setup...";
                        return true;
                    }
                    status = _steamUnavailableStatus;
                    return false;

                case RetryIntent.JoinLobby:
                    if (_lastRetryLobbyId == CSteamID.Nil)
                    {
                    status = "No previous lobby is available to rejoin yet.";
                    return false;
                    }
                    if (JoinLobby(_lastRetryLobbyId))
                    {
                        status = "Retrying lobby join...";
                        return true;
                    }
                    status = _steamUnavailableStatus;
                    return false;

                default:
                    status = "No previous action is available to retry.";
                    return false;
            }
        }

        public bool TryJoinLobbyById(string rawLobbyId, out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }

            CSteamID lobbyId;
            if (!TryParseLobbyId(rawLobbyId, out lobbyId))
            {
                status = "No Steam lobby ID was found in the clipboard.";
                return false;
            }

            if (JoinLobby(lobbyId))
            {
                status = "Joining lobby #" + lobbyId.m_SteamID + "...";
                return true;
            }

            status = _steamUnavailableStatus;
            return false;
        }

        public bool TryCopyLobbyId(out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }
            if (_lobbyId == CSteamID.Nil)
            {
                status = "Host or join a lobby first.";
                return false;
            }

            GUIUtility.systemCopyBuffer = "Lobby ID: " + _lobbyId.m_SteamID;
            status = "Lobby ID copied to clipboard.";
            return true;
        }

        public bool TryRequestRecovery(out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }
            if (_state != NetState.Connected)
            {
                status = "Connect first before requesting a resync.";
                return false;
            }

            status = SessionSync.RequestRecovery();
            return true;
        }

        public string BuildDiagnosticsReport()
        {
            var nl = Environment.NewLine;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Steam Ready: " + _steamReady);
            sb.AppendLine("State: " + _state);
            sb.AppendLine("Is Host: " + _isHost);
            sb.AppendLine("Overlay Enabled: " + IsOverlayEnabled());
            sb.AppendLine("Latency: " + Latency + "ms");
            sb.AppendLine("Lobby ID: " + (_lobbyId == CSteamID.Nil ? "(none)" : _lobbyId.m_SteamID.ToString()));
            sb.AppendLine("Peer: " + (_peerId == CSteamID.Nil ? "(none)" : FriendName(_peerId) + " [" + _peerId.m_SteamID + "]"));
            sb.AppendLine("Retry Action: " + GetRetryActionLabel());
            sb.AppendLine("Reconnect Available: " + _lastDisconnectWasConnected);
            sb.AppendLine("Last Status: " + (_lastStatusMessage ?? string.Empty));
            sb.AppendLine("Last Failure: " + (_lastFailureReason ?? string.Empty));

            if (!_steamReady)
                sb.AppendLine("Steam Status: " + _steamUnavailableStatus.Replace(nl, " | "));

            string presence = GetLobbyPresence();
            if (!string.IsNullOrEmpty(presence))
            {
                sb.AppendLine("Presence:");
                sb.AppendLine(presence);
            }

            string sessionDiagnostics = SessionSync.BuildDiagnosticsSection();
            if (!string.IsNullOrEmpty(sessionDiagnostics))
            {
                sb.AppendLine("Session:");
                sb.AppendLine(sessionDiagnostics);
            }

            return sb.ToString().TrimEnd();
        }

        // ── State ─────────────────────────────────────────────────────────────
        enum NetState
        {
            Idle,
            CreatingLobby,
            WaitingInLobby,   // host: invite overlay shown
            JoiningLobby,     // client: JoinLobby call in flight
            WaitingHello,     // host: session accepted, waiting Hello
            WaitingWelcome,   // client: Hello sent, waiting Welcome
            WaitingReady,     // host: Welcome sent, waiting Ready
            Connected,
            Error,
        }

        enum RetryIntent
        {
            None,
            Host,
            JoinLobby,
        }

        NetState _state  = NetState.Idle;
        bool     _isHost;
        CSteamID _peerId  = CSteamID.Nil;
        CSteamID _lobbyId = CSteamID.Nil;
        RetryIntent _lastRetryIntent = RetryIntent.None;
        CSteamID    _lastRetryLobbyId = CSteamID.Nil;
        string      _lastRetryPeerName = string.Empty;
        bool        _lastDisconnectWasConnected;

        // ── Callbacks (hold references — prevent GC) ──────────────────────────
        Callback<P2PSessionRequest_t>      _cbP2PReq;
        Callback<P2PSessionConnectFail_t>  _cbP2PFail;
        Callback<GameLobbyJoinRequested_t> _cbLobbyJoinReq;
        Callback<LobbyChatUpdate_t>        _cbLobbyChatUpd;
        Callback<GameOverlayActivated_t>   _cbOverlay;
        CallResult<LobbyCreated_t>         _crLobbyCreated;
        CallResult<LobbyEnter_t>           _crLobbyEntered;

        // ── Buffers ───────────────────────────────────────────────────────────
        readonly byte[]       _recvBuf    = new byte[65536];
        readonly MemoryStream _sendBuf    = new MemoryStream(256);
        readonly System.IO.BinaryWriter _sendWriter;

        // ── Timing ────────────────────────────────────────────────────────────
        const float HANDSHAKE_TIMEOUT = 12f;   // seconds
        const float LOBBY_TIMEOUT     = 20f;
        const int   PEER_TIMEOUT_MS   = 30_000;
        const float PING_INTERVAL     = 3f;

        float    _stateEnteredTime;            // Time.realtimeSinceStartup at state change
        DateTime _lastReceive  = DateTime.UtcNow;
        float    _nextPingTime;
        DateTime _pingSentAt;
        bool     _pingSentPending;
        bool     _steamInitAttempted;
        bool     _steamReady;
        string   _steamUnavailableStatus = "Steam is unavailable.\nLaunch Cuphead through Steam.";
        string   _lastStatusMessage;
        string   _lastFailureReason;

        // ── Constructor ───────────────────────────────────────────────────────

        public SteamNetManager()
        {
            _sendWriter = new System.IO.BinaryWriter(_sendBuf);
            Plugin.Log.LogInfo("[SteamNet] Created.");
        }

        public bool TryInitializeSteam()
        {
            if (_steamReady) return true;
            if (_steamInitAttempted) return false;

            _steamInitAttempted     = true;
            _steamUnavailableStatus = BuildSteamUnavailableStatus();

            try
            {
                if (!SteamAPI.Init())
                {
                    Plugin.Log.LogWarning("[SteamNet] SteamAPI.Init() returned false.");
                    Plugin.Log.LogWarning("[SteamNet] " + _steamUnavailableStatus.Replace('\n', ' '));
                    return false;
                }

                _steamReady      = true;
                _cbP2PReq        = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
                _cbP2PFail       = Callback<P2PSessionConnectFail_t>.Create(OnP2PConnectFailRaw);
                _cbLobbyJoinReq  = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequestedRaw);
                _cbLobbyChatUpd  = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdateRaw);
                _cbOverlay       = Callback<GameOverlayActivated_t>.Create(OnGameOverlayActivated);
                Plugin.Log.LogInfo("[SteamNet] Steam initialized.");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                Plugin.Log.LogError("[SteamNet] Steam DLL missing: " + ex.Message);
                Plugin.Log.LogWarning("[SteamNet] " + _steamUnavailableStatus.Replace('\n', ' '));
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[SteamNet] Steam initialization failed: " + ex);
                Plugin.Log.LogWarning("[SteamNet] " + _steamUnavailableStatus.Replace('\n', ' '));
                return false;
            }
        }

        // ── Host flow ─────────────────────────────────────────────────────────

        public bool StartHost()
        {
            if (!EnsureSteamReady()) return false;

            _lastRetryIntent = RetryIntent.Host;
            _lastRetryLobbyId = CSteamID.Nil;
            _lastRetryPeerName = string.Empty;
            _lastDisconnectWasConnected = false;
            Shutdown();
            _isHost = true;
            SteamNetworking.AllowP2PPacketRelay(true);
            MultiplayerSession.StartAsHost();

            SetState(NetState.CreatingLobby, "Creating lobby...");
            _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _crLobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2));
            return true;
        }

        void OnLobbyCreated(LobbyCreated_t r, bool ioFail)
        {
            if (!_steamReady) return;

            if (ioFail || r.m_eResult != EResult.k_EResultOK)
            {
                MultiplayerSession.End();
                SetState(NetState.Error, DescribeLobbyCreateFailure(r.m_eResult, ioFail));
                Plugin.Log.LogWarning("[SteamNet] Lobby creation failed: " + r.m_eResult);
                return;
            }
            _lobbyId = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(_lobbyId, "game", "CupheadOnline");

            string waitStatus =
                "Waiting for player...\n"
                + "Use Invite Friend to send another Steam invite.";
            string copyStatus;
            if (TryCopyLobbyId(out copyStatus))
                waitStatus += "\n" + copyStatus;

            SetState(NetState.WaitingInLobby, waitStatus);
            Plugin.Log.LogInfo("[SteamNet] Lobby: " + _lobbyId);
            string inviteStatus;
            if (!OpenInviteDialog(out inviteStatus))
                FireStatus(inviteStatus);
        }

        void OnP2PSessionRequest(P2PSessionRequest_t req)
        {
            if (!_steamReady) return;

            if (_lobbyId != CSteamID.Nil && !IsLobbyMember(req.m_steamIDRemote))
            {
                Plugin.Log.LogWarning("[SteamNet] Rejected P2P from non-lobby peer.");
                return;
            }
            SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
            _peerId = req.m_steamIDRemote;
            SetState(NetState.WaitingHello,
                "Player connecting...");
            Plugin.Log.LogInfo("[SteamNet] P2P accepted from " + FriendName(_peerId));
        }

        // ── Client flow ───────────────────────────────────────────────────────

        public bool JoinLobby(CSteamID lobbyId)
        {
            if (!EnsureSteamReady()) return false;

            _lastRetryIntent = RetryIntent.JoinLobby;
            _lastRetryLobbyId = lobbyId;
            _lastDisconnectWasConnected = false;
            Shutdown();
            _isHost = false;
            SteamNetworking.AllowP2PPacketRelay(true);
            SetState(NetState.JoiningLobby, "Joining lobby #" + lobbyId.m_SteamID + "...");
            _crLobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            _crLobbyEntered.Set(SteamMatchmaking.JoinLobby(lobbyId));
            return true;
        }

        // Thread-safe wrapper — Steam callback may arrive on any thread
        void OnLobbyJoinRequestedRaw(GameLobbyJoinRequested_t req)
        {
            var id = req.m_steamIDLobby;
            MainThreadQueue.Enqueue(() =>
            {
                Plugin.Log.LogInfo("[SteamNet] Invite accepted — joining " + id);
                JoinLobby(id);
            });
        }

        void OnLobbyEntered(LobbyEnter_t r, bool ioFail)
        {
            if (!_steamReady) return;

            if (ioFail || r.m_EChatRoomEnterResponse
                       != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                SetState(NetState.Error,
                    DescribeLobbyJoinFailure((EChatRoomEnterResponse)r.m_EChatRoomEnterResponse, ioFail));
                return;
            }
            _lobbyId = new CSteamID(r.m_ulSteamIDLobby);
            _peerId  = SteamMatchmaking.GetLobbyOwner(_lobbyId);
            _lastRetryPeerName = FriendName(_peerId);

            MultiplayerSession.StartAsClient();
            SetState(NetState.WaitingWelcome,
                "Connecting to " + FriendName(_peerId) + "...");

            // Knock: sending Hello triggers P2PSessionRequest on host side
            RawSend(new[] { (byte)PacketType.Hello }, reliable: true);
            Plugin.Log.LogInfo("[SteamNet] Hello sent to " + FriendName(_peerId));
        }

        // ── Handshake message handlers ────────────────────────────────────────

        void OnHelloReceived()
        {
            // host ← Hello
            RawSend(new[] { (byte)PacketType.Welcome }, reliable: true);
            SetState(NetState.WaitingReady,
                "Almost there\u2026");
            Plugin.Log.LogInfo("[SteamNet] Hello received, Welcome sent.");
        }

        void OnWelcomeReceived()
        {
            // client ← Welcome
            RawSend(new[] { (byte)PacketType.Ready }, reliable: true);
            FinishConnect();
            Plugin.Log.LogInfo("[SteamNet] Welcome received, Ready sent.");
        }

        void OnReadyReceived()
        {
            // host ← Ready
            FinishConnect();
            Plugin.Log.LogInfo("[SteamNet] Ready received — fully connected.");
        }

        void FinishConnect()
        {
            if (_isHost)
            {
                if (!MultiplayerSession.IsActive || !MultiplayerSession.IsHost)
                    MultiplayerSession.StartAsHost();
            }
            else if (!MultiplayerSession.IsActive || MultiplayerSession.IsHost)
            {
                MultiplayerSession.StartAsClient();
            }

            _lastReceive = DateTime.UtcNow;
            _lastDisconnectWasConnected = false;
            string name  = FriendName(_peerId);
            SetState(
                NetState.Connected,
                _isHost
                    ? "Guest connected.\nSelect OPEN SAVE SLOT to choose a file."
                    : "Connected.\nWaiting for the host to choose a save slot.");

            ConnectionHUD.Show("Connected - " + name);
            SessionSync.OnConnected(_isHost);

            if (_isHost)
                SessionSync.BroadcastRecoveryBundle("Peer connected or reconnected.");
        }

        // ── Disconnect / failure ──────────────────────────────────────────────

        // Thread-safe wrapper
        void OnP2PConnectFailRaw(P2PSessionConnectFail_t cb)
        {
            var err = cb.m_eP2PSessionError;
            MainThreadQueue.Enqueue(() =>
            {
                Plugin.Log.LogWarning("[SteamNet] P2P fail, error=" + err);
                HandleDisconnect(DescribeP2PFailure(err));
            });
        }

        // Thread-safe wrapper
        void OnLobbyChatUpdateRaw(LobbyChatUpdate_t cb)
        {
            bool left = (cb.m_rgfChatMemberStateChange &
                         (uint)EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0
                     || (cb.m_rgfChatMemberStateChange &
                         (uint)EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0;
            if (!left) return;

            var changed = new CSteamID(cb.m_ulSteamIDUserChanged);
            if (changed != _peerId) return;

            string name = FriendName(changed);
            MainThreadQueue.Enqueue(() =>
            {
                Plugin.Log.LogInfo("[SteamNet] " + name + " left the lobby.");
                HandleDisconnect(name + " left the lobby.");
            });
        }

        // Thread-safe wrapper — fired when the Steam overlay opens or closes
        void OnGameOverlayActivated(GameOverlayActivated_t cb)
        {
            if (cb.m_bActive != 0) return;   // 0 = overlay closed
            MainThreadQueue.Enqueue(() => OnOverlayClosed?.Invoke());
        }

        void HandleDisconnect(string reason)
        {
            bool wasConnected = _state == NetState.Connected;
            string friendlyReason = string.IsNullOrEmpty(reason) ? "Connection closed." : reason;
            _lastFailureReason = friendlyReason;
            _lastDisconnectWasConnected = wasConnected;
            if (_steamReady && _peerId != CSteamID.Nil)
                SteamNetworking.CloseP2PSessionWithUser(_peerId);
            _peerId          = CSteamID.Nil;
            _pingSentPending = false;
            MultiplayerSession.End();

            if (_isHost && _lobbyId != CSteamID.Nil)
            {
                // Host stays in lobby — reset to WaitingInLobby so another player can join
                SetState(NetState.WaitingInLobby,
                    friendlyReason + "\n\nWaiting for next player...\n"
                    + "Use Invite Friend to send another Steam invite.");
                if (wasConnected) ConnectionHUD.ShowDisconnected(friendlyReason);
            }
            else
            {
                SetState(NetState.Error,
                    friendlyReason + "\n\nUse Retry Last or Join Game to try again.");
                if (wasConnected) ConnectionHUD.ShowDisconnected(friendlyReason);
            }
        }

        // ── Poll (every frame from Plugin.Update) ─────────────────────────────

        public void Poll()
        {
            if (!_steamReady) return;

            try
            {
                SteamAPI.RunCallbacks();
                DrainP2PPackets();
            }
            catch (InvalidOperationException ex)
            {
                HandleSteamRuntimeFailure("polling", ex);
                return;
            }

            if (_state == NetState.Idle || _state == NetState.Error) return;

            float now     = Time.realtimeSinceStartup;
            float elapsed = now - _stateEnteredTime;

            // Handshake / lobby timeouts
            switch (_state)
            {
                case NetState.CreatingLobby:
                    if (elapsed > LOBBY_TIMEOUT)
                    {
                        MultiplayerSession.End();
                        SetState(NetState.Error,
                            "Steam took too long to create the lobby.\nUse Retry Last or Host Game to try again.");
                    }
                    return;

                case NetState.JoiningLobby:
                    if (elapsed > LOBBY_TIMEOUT)
                    {
                        MultiplayerSession.End();
                        SetState(NetState.Error,
                            "Steam took too long to join the lobby.\nUse Retry Last or Join Game to try again.");
                    }
                    return;

                case NetState.WaitingHello:
                case NetState.WaitingWelcome:
                case NetState.WaitingReady:
                    if (elapsed > HANDSHAKE_TIMEOUT)
                        HandleDisconnect("The handshake with " + FriendName(_peerId) + " timed out.");
                    return;

                case NetState.WaitingInLobby:
                    return;   // no timeout — host waits indefinitely
            }

            // Connected: keepalive + peer timeout check
            if ((DateTime.UtcNow - _lastReceive).TotalMilliseconds > PEER_TIMEOUT_MS)
            {
                HandleDisconnect(FriendName(_peerId) + " stopped responding.");
                return;
            }

            if (now > _nextPingTime)
            {
                _nextPingTime    = now + PING_INTERVAL;
                _pingSentAt      = DateTime.UtcNow;
                _pingSentPending = true;
                RawSend(new[] { (byte)PacketType.Ping }, reliable: false);
            }
        }

        void DrainP2PPackets()
        {
            if (!_steamReady) return;

            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size))
            {
                if (size > (uint)_recvBuf.Length)
                {
                    uint d1; CSteamID d2;
                    SteamNetworking.ReadP2PPacket(_recvBuf, (uint)_recvBuf.Length, out d1, out d2);
                    continue;
                }
                uint     read;
                CSteamID sender;
                if (SteamNetworking.ReadP2PPacket(_recvBuf, size, out read, out sender) && read > 0)
                    ProcessPacket(_recvBuf, (int)read, sender);
            }
        }

        void ProcessPacket(byte[] buf, int length, CSteamID sender)
        {
            if (length == 0) return;
            _lastReceive = DateTime.UtcNow;

            byte type = buf[0];

            // ── Handshake ─────────────────────────────────────────────────────
            if (type == (byte)PacketType.Hello)
            {
                if (_isHost && (_state == NetState.WaitingHello || _state == NetState.WaitingInLobby))
                {
                    if (_peerId == CSteamID.Nil) _peerId = sender;
                    OnHelloReceived();
                }
                return;
            }
            if (type == (byte)PacketType.Welcome)
            {
                if (!_isHost && _state == NetState.WaitingWelcome) OnWelcomeReceived();
                return;
            }
            if (type == (byte)PacketType.Ready)
            {
                if (_isHost && _state == NetState.WaitingReady) OnReadyReceived();
                return;
            }

            // ── Ping / Pong ───────────────────────────────────────────────────
            if (type == (byte)PacketType.Ping)
            {
                RawSend(new[] { (byte)PacketType.Pong }, reliable: false);
                return;
            }
            if (type == (byte)PacketType.Pong && _pingSentPending)
            {
                _pingSentPending = false;
                Latency = (int)(DateTime.UtcNow - _pingSentAt).TotalMilliseconds;
                ConnectionHUD.UpdatePing(Latency);
                return;
            }

            // ── Graceful disconnect ───────────────────────────────────────────
            if (type == (byte)PacketType.Disconnect)
            {
                HandleDisconnect(FriendName(sender) + " disconnected.");
                return;
            }

            // ── Game packets — only accepted when fully connected ─────────────
            if (_state != NetState.Connected) return;
            if (_peerId != CSteamID.Nil && sender != _peerId) return;

            using (var ms = new MemoryStream(buf, 1, length - 1, false))
            using (var r  = new BinaryReader(ms))
                PacketDispatcher.Dispatch((PacketType)type, r);
        }

        // ── Send helpers ──────────────────────────────────────────────────────

        public void SendPlayerState (ref PlayerStatePacket  p) => Send(PacketType.PlayerState,  ref p, false);
        public void SendInputFrame  (ref InputFramePacket   p) => Send(PacketType.InputFrame,   ref p, false);
        public void SendEnemyState  (ref EnemyStatePacket   p, bool reliable = false) => Send(PacketType.EnemyState,   ref p, reliable);
        public void SendWeaponEvent (ref WeaponEventPacket  p) => Send(PacketType.WeaponEvent,  ref p, true);
        public void SendDamageEvent (ref DamageEventPacket  p) => Send(PacketType.DamageEvent,  ref p, true);
        public void SendSceneChange (ref SceneChangePacket  p) => Send(PacketType.SceneChange,  ref p, true);
        public void SendMenuSceneChange(ref MenuSceneChangePacket p) => Send(PacketType.MenuSceneChange, ref p, true);
        public void SendSaveSlotSync(ref SaveSlotSyncPacket p) => Send(PacketType.SaveSlotSync, ref p, true);
        public void SendSaveProfile(ref SaveProfilePacket p) => Send(PacketType.SaveProfile, ref p, true);
        public void SendLobbySync   (ref LobbySyncPacket    p) => Send(PacketType.LobbySync,    ref p, true);
        public void SendSessionSnapshot(ref SessionSnapshotPacket p, bool reliable = true) => Send(PacketType.SessionSnapshot, ref p, reliable);
        public void SendSessionSignal(ref SessionSignalPacket p) => Send(PacketType.SessionSignal, ref p, true);
        public void SendSessionStart(ref SessionStartPacket p) => Send(PacketType.SessionStart, ref p, true);

        void Send<T>(PacketType type, ref T pkt, bool reliable) where T : struct, IPacket
        {
            if (!_steamReady) return;
            if (_state != NetState.Connected && type != PacketType.SessionStart) return;
            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)type);
            pkt.Write(_sendWriter);
            _sendWriter.Flush();
            int len  = (int)_sendBuf.Length;
            var data = new byte[len];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, len);
            RawSend(data, reliable);
        }

        void RawSend(byte[] data, bool reliable)
        {
            if (!_steamReady) return;
            if (_peerId == CSteamID.Nil) return;
            var mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable;
            SteamNetworking.SendP2PPacket(_peerId, data, (uint)data.Length, mode);
        }

        // ── Shutdown ──────────────────────────────────────────────────────────

        public void Shutdown()
        {
            bool hadState = _state != NetState.Idle
                         || _peerId != CSteamID.Nil
                         || _lobbyId != CSteamID.Nil;

            if (_steamReady && _peerId != CSteamID.Nil)
            {
                try { SteamNetworking.SendP2PPacket(_peerId, new[] { (byte)PacketType.Disconnect }, 1, EP2PSend.k_EP2PSendReliable); } catch { }
                SteamNetworking.CloseP2PSessionWithUser(_peerId);
            }
            if (_steamReady && _lobbyId != CSteamID.Nil)
            {
                SteamMatchmaking.LeaveLobby(_lobbyId);
            }
            _peerId          = CSteamID.Nil;
            _lobbyId         = CSteamID.Nil;
            _pingSentPending = false;
            _isHost          = false;
            Latency          = 0;
            _state           = NetState.Idle;
            ConnectionHUD.Hide();
            MultiplayerSession.End();
            if (hadState)
                Plugin.Log.LogInfo("[SteamNet] Shutdown.");
        }

        public void Dispose()
        {
            Shutdown();
            if (!_steamReady) return;

            try
            {
                SteamAPI.Shutdown();
                Plugin.Log.LogInfo("[SteamNet] Steam shutdown.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[SteamNet] Steam shutdown failed: " + ex.Message);
            }
            finally
            {
                _steamReady      = false;
                _cbP2PReq        = null;
                _cbP2PFail       = null;
                _cbLobbyJoinReq  = null;
                _cbLobbyChatUpd  = null;
                _cbOverlay       = null;
                _crLobbyCreated  = null;
                _crLobbyEntered  = null;
            }
        }

        /// <summary>Kept for source compat — Steam flow uses JoinLobby/invite.</summary>
        public void Connect(string ip, int port = 0) =>
            Plugin.Log.LogWarning("[SteamNet] Connect(ip) ignored — use Steam invite.");

        // ── Helpers ──────────────────────────────────────────────────────────

        void SetState(NetState s, string status)
        {
            _state            = s;
            _stateEnteredTime = Time.realtimeSinceStartup;
            _lastStatusMessage = status;
            if (s == NetState.Error)
                _lastFailureReason = status;
            FireStatus(status);
            Plugin.Log.LogInfo("[SteamNet] → " + s + ": " + status);
        }

        void FireStatus(string msg) => OnStatusChanged?.Invoke(msg);

        string FriendName(CSteamID id)
        {
            if (!_steamReady || id == CSteamID.Nil) return "Unknown Player";
            return SteamFriends.GetFriendPersonaName(id);
        }

        bool IsLobbyMember(CSteamID id)
        {
            if (!_steamReady) return false;
            if (_lobbyId == CSteamID.Nil) return true;
            int n = SteamMatchmaking.GetNumLobbyMembers(_lobbyId);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, i) == id) return true;
            return false;
        }

        bool EnsureSteamReady()
        {
            if (_steamReady) return true;

            if (!_steamInitAttempted)
                TryInitializeSteam();

            if (_steamReady) return true;

            SetState(NetState.Error, _steamUnavailableStatus);
            return false;
        }

        bool IsOverlayEnabled()
        {
            if (!_steamReady) return false;
            try { return SteamUtils.IsOverlayEnabled(); }
            catch { return false; }
        }

        string BuildSteamUnavailableStatus()
        {
            string status = "Steam is unavailable.\nLaunch Cuphead through Steam.";

            try
            {
                string gameRoot = Path.GetDirectoryName(Application.dataPath);
                if (!string.IsNullOrEmpty(gameRoot))
                {
                    string appIdPath = Path.Combine(gameRoot, "steam_appid.txt");
                    if (!File.Exists(appIdPath))
                        status += "\nIf testing outside Steam, add steam_appid.txt next to Cuphead.exe.";
                }
            }
            catch
            {
                // Best-effort hint only.
            }

            return status;
        }

        bool TryParseLobbyId(string rawLobbyId, out CSteamID lobbyId)
        {
            lobbyId = CSteamID.Nil;
            if (string.IsNullOrEmpty(rawLobbyId) || rawLobbyId.Trim().Length == 0)
                return false;

            string raw = rawLobbyId.Trim();
            ulong value;
            if (ulong.TryParse(raw, out value) && value != 0UL)
            {
                lobbyId = new CSteamID(value);
                return true;
            }

            var match = System.Text.RegularExpressions.Regex.Match(raw, @"Lobby ID:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && ulong.TryParse(match.Groups[1].Value, out value) && value != 0UL)
            {
                lobbyId = new CSteamID(value);
                return true;
            }

            match = System.Text.RegularExpressions.Regex.Match(raw, @"\b(\d{8,})\b");
            if (match.Success && ulong.TryParse(match.Groups[1].Value, out value) && value != 0UL)
            {
                lobbyId = new CSteamID(value);
                return true;
            }

            return false;
        }

        string DescribeLobbyCreateFailure(EResult result, bool ioFail)
        {
            if (ioFail)
                return "Steam could not create the lobby.\nCheck Steam and try again.";

            switch (result)
            {
                case EResult.k_EResultNoConnection:
                    return "Steam is offline.\nReconnect Steam and try again.";
                case EResult.k_EResultTimeout:
                    return "Steam timed out while creating the lobby.\nUse Retry Last.";
                case EResult.k_EResultAccessDenied:
                    return "Steam blocked lobby creation.\nCheck the overlay and privacy settings.";
                default:
                    return "Steam could not create the lobby (" + result + ").\nUse Retry Last or Host Game to try again.";
            }
        }

        string DescribeLobbyJoinFailure(EChatRoomEnterResponse response, bool ioFail)
        {
            if (ioFail)
                return "Steam could not join the lobby.\nCheck Steam and try again.";

            switch (response)
            {
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist:
                    return "That Steam lobby no longer exists.\nAsk the host for a fresh invite.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseFull:
                    return "That Steam lobby is already full.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned:
                    return "Steam reported that this account is blocked from the lobby.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited:
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseCommunityBan:
                    return "Steam account restrictions prevented the lobby join.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed:
                    return "Steam blocked the lobby join.\nCheck your invite and privacy settings.";
                default:
                    return "Steam could not join the lobby (" + response + ").\nUse Retry Last or Join Game to try again.";
            }
        }

        string DescribeP2PFailure(byte err)
        {
            switch ((EP2PSessionError)err)
            {
                case EP2PSessionError.k_EP2PSessionErrorTimeout:
                    return "Steam P2P timed out while contacting the other player.";
                case EP2PSessionError.k_EP2PSessionErrorNotRunningApp:
                    return "The other player is not running Cuphead with the mod yet.";
                case EP2PSessionError.k_EP2PSessionErrorNoRightsToApp:
                    return "Steam denied the P2P session for this app.";
                case EP2PSessionError.k_EP2PSessionErrorDestinationNotLoggedIn:
                    return "The other player's Steam session went offline.";
                case EP2PSessionError.k_EP2PSessionErrorMax:
                    return "Steam P2P reported an unknown error.";
                default:
                    return "Steam P2P failed (" + err + ").";
            }
        }

        void HandleSteamRuntimeFailure(string context, Exception ex)
        {
            if (!_steamReady) return;

            _steamReady = false;
            _peerId = CSteamID.Nil;
            _lobbyId = CSteamID.Nil;
            _isHost = false;
            _pingSentPending = false;
            _steamUnavailableStatus = BuildSteamUnavailableStatus();
            Plugin.Log.LogError("[SteamNet] Steam runtime failure while " + context + ": " + ex.Message);
            ConnectionHUD.Hide();
            MultiplayerSession.End();

            if (_state != NetState.Idle && _state != NetState.Error)
                SetState(NetState.Error, _steamUnavailableStatus);
        }
    }
}
