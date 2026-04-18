using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// HOST: broadcasts enemy / boss state at 20 Hz (every 3rd FixedUpdate at 60 Hz).
    /// CLIENT: receives EnemyStatePacket and snaps enemy positions + HP + animation.
    ///
    /// The boss AI continues to run independently on the client; we correct it every
    /// ~50 ms so minor divergences are continuously repaired without hard-snapping
    /// which would cause visual pops.
    ///
    /// HP reflection cache: The game does not expose a public HP getter on enemies.
    /// We locate the first 'float hp' (or 'HP', 'health') field on the DamageReceiver
    /// and use it.  If the field is not found, HP sync is silently skipped.
    /// </summary>
    public static class EnemySyncManager
    {
        private static int   _broadcastCounter;
        private const  int   BROADCAST_EVERY = 3; // frames (= 20 Hz at 60 Hz FixedUpdate)
        private static int   _recoveryBurstFrames;

        // Reflection cache for enemy HP field
        private static FieldInfo _hpField;
        private static bool      _hpFieldSearched;
        private static readonly Dictionary<int, EnemySnapshotState> _lastSent = new Dictionary<int, EnemySnapshotState>();

        private struct EnemySnapshotState
        {
            public float Hp;
            public byte Phase;
            public Vector3 Position;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  HOST side: called every FixedUpdate from Plugin.Update indirectly via
        //  a MonoBehaviour we attach, or from the PlayerMotorPatch tick.
        //  We call it from Plugin.Update to keep it off the physics thread.
        // ──────────────────────────────────────────────────────────────────────

        public static void HostTick()
        {
            if (!MultiplayerSession.IsHost || !Plugin.Net.IsConnected) return;

            var enemies = Object.FindObjectsOfType<DamageReceiver>();
            int enemyCount = CountEnemies(enemies);
            bool bossPriorityMode = _recoveryBurstFrames > 0 || enemyCount <= 3;

            _broadcastCounter++;
            int broadcastEvery = bossPriorityMode ? 1 : BROADCAST_EVERY;
            if (_broadcastCounter < broadcastEvery) return;
            _broadcastCounter = 0;
            if (_recoveryBurstFrames > 0)
                _recoveryBurstFrames--;

            foreach (var dr in enemies)
            {
                if (dr.type != DamageReceiver.Type.Enemy) continue;
                var go = dr.gameObject;

                float hp   = GetEnemyHp(dr);
                byte  phase = GetEnemyPhase(dr);
                int   hash  = 0;
                var   anim  = go.GetComponentInChildren<Animator>();
                if (anim != null)
                    hash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;

                bool priority = bossPriorityMode || IsBossPriority(go, phase, enemyCount);

                var pkt = new EnemyStatePacket
                {
                    InstanceId = go.GetInstanceID(),
                    PosX       = go.transform.position.x,
                    PosY       = go.transform.position.y,
                    Hp         = hp,
                    Phase      = phase,
                    AnimHash   = hash,
                    Tick       = MultiplayerSession.Tick,
                };

                bool reliable = priority && ShouldSendReliableDelta(pkt);
                Plugin.Net.SendEnemyState(ref pkt, reliable);
                _lastSent[pkt.InstanceId] = new EnemySnapshotState
                {
                    Hp = pkt.Hp,
                    Phase = pkt.Phase,
                    Position = go.transform.position,
                };
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  CLIENT side: correct enemy state
        // ──────────────────────────────────────────────────────────────────────

        public static void OnEnemyStateReceived(EnemyStatePacket pkt)
        {
            if (!EnemyRegistry.TryGet(pkt.InstanceId, out var dr))
            {
                EnemyRegistry.MarkDirty(); // trigger a rescan next query
                if (!EnemyRegistry.TryGet(pkt.InstanceId, out dr)) return;
            }

            var go = dr.gameObject;

            // ── Position: gentle lerp to avoid visual snap ────────────────────
            var targetPos = new Vector3(pkt.PosX, pkt.PosY, go.transform.position.z);
            float distance = Vector3.Distance(go.transform.position, targetPos);
            go.transform.position = distance > 6f
                ? targetPos
                : Vector3.Lerp(go.transform.position, targetPos, 0.3f);

            // ── HP correction ─────────────────────────────────────────────────
            SetEnemyHp(dr, pkt.Hp);

            // ── Animation: play the host's animator state ─────────────────────
            var anim = go.GetComponentInChildren<Animator>();
            if (anim != null && pkt.AnimHash != 0)
            {
                // Only force the state if there's a significant divergence;
                // avoid overriding transition logic every single frame.
                int localHash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
                if (localHash != pkt.AnimHash)
                    anim.Play(pkt.AnimHash, 0, -1f);
            }
        }

        public static void Reset()
        {
            _broadcastCounter = 0;
            _recoveryBurstFrames = 0;
            _lastSent.Clear();
        }

        public static void TriggerRecoveryBurst(int frames = 150)
        {
            _recoveryBurstFrames = Mathf.Max(_recoveryBurstFrames, frames);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  HP reflection
        // ──────────────────────────────────────────────────────────────────────

        static float GetEnemyHp(DamageReceiver dr)
        {
            var fi = FindHpField(dr);
            if (fi == null) return -1f;
            try { return (float)fi.GetValue(dr); }
            catch { return -1f; }
        }

        static void SetEnemyHp(DamageReceiver dr, float hp)
        {
            if (hp < 0f) return;
            var fi = FindHpField(dr);
            if (fi == null) return;
            try { fi.SetValue(dr, hp); }
            catch { /* field type mismatch — silently skip */ }
        }

        static FieldInfo FindHpField(DamageReceiver dr)
        {
            if (_hpFieldSearched) return _hpField;
            _hpFieldSearched = true;

            var t = dr.GetType();
            const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            foreach (var name in new[] { "hp", "HP", "health", "_hp", "currentHp", "currentHealth" })
            {
                var fi = t.GetField(name, bf);
                if (fi != null && fi.FieldType == typeof(float))
                {
                    _hpField = fi;
                    return fi;
                }
            }
            Plugin.Log.LogWarning("[EnemySync] Could not find HP field on DamageReceiver — HP sync disabled.");
            return null;
        }

        static byte GetEnemyPhase(DamageReceiver dr)
        {
            // Try to read a common 'phase' or 'currentPhase' int field
            var t  = dr.GetType();
            const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            foreach (var name in new[] { "phase", "currentPhase", "_phase", "Phase" })
            {
                var fi = t.GetField(name, bf);
                if (fi != null && fi.FieldType == typeof(int))
                {
                    try { return (byte)(int)fi.GetValue(dr); }
                    catch { }
                }
            }
            return 0;
        }

        static int CountEnemies(DamageReceiver[] receivers)
        {
            int count = 0;
            for (int i = 0; i < receivers.Length; i++)
            {
                if (receivers[i] != null && receivers[i].type == DamageReceiver.Type.Enemy)
                    count++;
            }
            return count;
        }

        static bool ShouldSendReliableDelta(EnemyStatePacket pkt)
        {
            EnemySnapshotState previous;
            if (!_lastSent.TryGetValue(pkt.InstanceId, out previous))
                return true;

            if (previous.Phase != pkt.Phase)
                return true;
            if (Mathf.Abs(previous.Hp - pkt.Hp) >= 0.5f)
                return true;

            var prevPos = previous.Position;
            float dx = prevPos.x - pkt.PosX;
            float dy = prevPos.y - pkt.PosY;
            return (dx * dx + dy * dy) >= 16f;
        }

        static bool IsBossPriority(GameObject go, byte phase, int enemyCount)
        {
            if (phase > 0)
                return true;
            if (enemyCount <= 3)
                return true;
            if (go == null)
                return false;

            string name = go.name.ToLowerInvariant();
            return name.Contains("boss")
                || name.Contains("baroness")
                || name.Contains("dragon")
                || name.Contains("robot")
                || name.Contains("saltbaker")
                || name.Contains("dice")
                || name.Contains("devil")
                || name.Contains("pirate")
                || name.Contains("train")
                || name.Contains("genie")
                || name.Contains("clown")
                || name.Contains("flower")
                || name.Contains("blimp")
                || name.Contains("bee");
        }
    }
}
