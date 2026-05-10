using System.IO;

namespace CupheadOnline.Coop
{
    /// <summary>
    /// Wire format for the in-game gameplay-sync layer. Lives on its own Steam P2P
    /// channel (<see cref="ChannelIndex"/>), separate from the upstream lobby/session
    /// protocol on channel 0 — so we don't collide with their packet dispatcher.
    ///
    /// Phased layout (see plan):
    ///   v1 = PlayerState only (this file). 2-player puppet sync.
    ///   v2 = + InputFrame for host's P2 motor (Phase 2A).
    ///   v3 = + EntityState/Spawn/Despawn for boss/projectile sync (Phase 3).
    /// </summary>
    internal static class CoopProtocol
    {
        // Steam P2P channel for our packets. The upstream lobby/session protocol uses
        // channel 0; we pick something well above that. P2P sessions are at the user
        // level (not channel level) so once their lobby has established a session
        // between the two SteamIDs, packets on any channel between those two users
        // flow without additional accept handshakes.
        public const int ChannelIndex = 7;

        // Bumped whenever the wire format changes meaningfully. Both peers handshake
        // implicitly: a side that receives a packet with an unknown PacketType byte
        // just drops it (no negotiation packet exchange — keeps the floor simple).
        public const byte WireVersion = 1;
    }

    internal enum CoopPacketType : byte
    {
        PlayerState = 1,   // host- or client-broadcast: per-frame state of the LOCAL player
                           // on the sending side. Other side renders as puppet via motor bypass.
        // Reserved for future phases:
        // InputFrame = 2,
        // EntityState = 3,
        // EntitySpawn = 4,
        // EntityDespawn = 5,
        // WeaponFire = 6,
    }

    /// <summary>
    /// Per-frame snapshot of one player's motor state. Sent unreliably at ~30Hz from
    /// each side (capturing only the LOCAL player on that side). Fields kept tight
    /// — 24 bytes including the type byte — so 30Hz gives ~720 B/s per player.
    ///
    /// Flags layout (1 byte):
    ///   bit 0: Grounded
    ///   bit 1: Dashing
    ///   bit 2: Ducking
    ///   bit 3: GravityReversed
    ///   bit 4: IsHit
    ///   bit 5: IsUsingSuperOrEx
    ///   bit 6: IsDead
    ///   bit 7: reserved
    /// </summary>
    internal struct PlayerStatePacket : ICoopPacket
    {
        public byte PlayerId;       // 0 = PlayerOne, 1 = PlayerTwo (per Cuphead's PlayerId enum)
        public uint Tick;           // sender-monotonic counter for ordering / dedup
        public float PosX;
        public float PosY;
        public sbyte LookX;         // -1 / 0 / +1 (Trilean)
        public sbyte LookY;         // -1 / 0 / +1
        public byte Flags;
        public int AnimStateHash;   // Animator.GetCurrentAnimatorStateInfo(0).fullPathHash

        public bool Grounded => (Flags & 0x01) != 0;
        public bool Dashing  => (Flags & 0x02) != 0;
        public bool Ducking  => (Flags & 0x04) != 0;
        public bool GravReversed => (Flags & 0x08) != 0;
        public bool IsHit    => (Flags & 0x10) != 0;
        public bool IsSuper  => (Flags & 0x20) != 0;
        public bool IsDead   => (Flags & 0x40) != 0;

        public static byte BuildFlags(bool grounded, bool dashing, bool ducking,
                                      bool gravReversed, bool isHit, bool isSuper, bool isDead)
        {
            byte f = 0;
            if (grounded)    f |= 0x01;
            if (dashing)     f |= 0x02;
            if (ducking)     f |= 0x04;
            if (gravReversed)f |= 0x08;
            if (isHit)       f |= 0x10;
            if (isSuper)     f |= 0x20;
            if (isDead)      f |= 0x40;
            return f;
        }

        public void Write(BinaryWriter w)
        {
            w.Write((byte)CoopPacketType.PlayerState);
            w.Write(PlayerId);
            w.Write(Tick);
            w.Write(PosX);
            w.Write(PosY);
            w.Write(LookX);
            w.Write(LookY);
            w.Write(Flags);
            w.Write(AnimStateHash);
        }

        // Caller has already consumed the 1-byte type discriminator.
        public static PlayerStatePacket Read(BinaryReader r)
        {
            return new PlayerStatePacket
            {
                PlayerId = r.ReadByte(),
                Tick = r.ReadUInt32(),
                PosX = r.ReadSingle(),
                PosY = r.ReadSingle(),
                LookX = r.ReadSByte(),
                LookY = r.ReadSByte(),
                Flags = r.ReadByte(),
                AnimStateHash = r.ReadInt32()
            };
        }
    }
}
