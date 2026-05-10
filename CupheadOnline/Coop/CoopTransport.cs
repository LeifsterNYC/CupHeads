using System;
using System.IO;
using Steamworks;

namespace CupheadOnline.Coop
{
    /// <summary>
    /// Thin wrapper over Steam P2P send/recv on <see cref="CoopProtocol.ChannelIndex"/>.
    /// Stateless — pulls peer SteamID from <c>Plugin.Net.RemoteSteamId</c> on each send;
    /// caller polls each Update tick.
    ///
    /// Why a separate channel from upstream's protocol: their <c>SteamNetManager.Poll</c>
    /// reads channel 0 only. By using channel 7 we have a private packet stream — no
    /// need to merge into their PacketDispatcher or worry about packet-type-byte
    /// collisions. P2P sessions are user-scoped, not channel-scoped, so once their
    /// lobby flow has authenticated the session on channel 0, our packets between the
    /// same two SteamIDs ride for free.
    /// </summary>
    internal static class CoopTransport
    {
        // 1 KiB is plenty for any single Phase-1 packet (PlayerStatePacket is ~24 B).
        // Future phases (entity-list state) may want larger; bump if needed.
        private static readonly byte[] _recvBuf = new byte[1024];

        /// <summary>True if a peer is connected and we can send.</summary>
        public static bool CanSend
        {
            get
            {
                var net = Plugin.Net;
                if (net == null || !net.IsConnected) return false;
                return net.RemoteSteamId != CSteamID.Nil;
            }
        }

        /// <summary>
        /// Serialize and send a packet that knows how to <c>Write(BinaryWriter)</c> itself
        /// (i.e., includes its own type-discriminator byte). Unreliable by default;
        /// reliable-with-buffering is appropriate for spawn/despawn events in later phases.
        /// </summary>
        public static void Send<T>(T packet, EP2PSend sendType = EP2PSend.k_EP2PSendUnreliable)
            where T : struct, ICoopPacket
        {
            if (!CanSend) return;
            var peer = Plugin.Net.RemoteSteamId;
            using (var ms = new MemoryStream(64))
            using (var bw = new BinaryWriter(ms))
            {
                packet.Write(bw);
                bw.Flush();
                var data = ms.GetBuffer();
                int len = (int)ms.Position;
                SteamNetworking.SendP2PPacket(peer, data, (uint)len, sendType, CoopProtocol.ChannelIndex);
            }
        }

        /// <summary>
        /// Drain incoming packets on our channel. <paramref name="onPacket"/> receives
        /// (sender, BinaryReader positioned at the type byte). Caller decides what to
        /// do with each packet by inspecting the first byte.
        /// </summary>
        public static void Poll(Action<CSteamID, BinaryReader> onPacket)
        {
            if (onPacket == null) return;
            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, CoopProtocol.ChannelIndex))
            {
                if (size > _recvBuf.Length)
                {
                    // Oversized packet — drain and drop. Should never happen with our wire
                    // format; if it does we want a log line, not a crash.
                    var oversize = new byte[size];
                    SteamNetworking.ReadP2PPacket(oversize, size, out _, out _, CoopProtocol.ChannelIndex);
                    Plugin.Log.LogWarning("[Coop] Dropped oversized packet, size=" + size);
                    continue;
                }
                uint actualSize;
                CSteamID remote;
                if (!SteamNetworking.ReadP2PPacket(_recvBuf, size, out actualSize, out remote, CoopProtocol.ChannelIndex))
                    continue;
                using (var ms = new MemoryStream(_recvBuf, 0, (int)actualSize, writable: false))
                using (var br = new BinaryReader(ms))
                {
                    try
                    {
                        onPacket(remote, br);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("[Coop] Packet handler threw: " + ex.GetType().Name + " " + ex.Message);
                    }
                }
            }
        }
    }

    /// <summary>Marker interface so <see cref="CoopTransport.Send{T}"/> is generic-constrained.
    /// Existing packet structs (e.g. <see cref="PlayerStatePacket"/>) implement this directly
    /// in their declaration in CoopProtocol.cs.</summary>
    internal interface ICoopPacket { void Write(BinaryWriter w); }
}
