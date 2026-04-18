using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Optional host/local balance helper that scales battle-level boss health
    /// by a configurable amount for each active player. Disabled by default.
    /// </summary>
    public static class BossHealthScaler
    {
        private static readonly BindingFlags Binding =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<Type, FieldInfo[]> PropertyFieldCache =
            new Dictionary<Type, FieldInfo[]>();

        private static readonly HashSet<int> ScaledPropertyInstances = new HashSet<int>();
        private static readonly HashSet<int> ScaledDamageReceivers = new HashSet<int>();

        private static readonly string[] BossKeywords =
        {
            "boss",
            "baroness",
            "dragon",
            "robot",
            "saltbaker",
            "dice",
            "devil",
            "pirate",
            "train",
            "genie",
            "clown",
            "flower",
            "blimp",
            "bee",
            "king",
            "queen",
            "knight",
            "rook",
            "bishop",
            "pawn",
            "moon",
            "mermaid",
            "briney",
            "howling",
            "glumstone",
            "mortimer",
            "esther",
            "chef",
            "salt",
        };

        private static float _nextScanAt;
        private static string _lastSceneName = string.Empty;
        private static int _scaledBossCount;
        private static int _lastPlayerCount = 1;
        private static float _lastMultiplier = 1f;
        private static FieldInfo _damageReceiverHpField;
        private static bool _damageReceiverHpFieldSearched;

        public static bool IsActive =>
            Plugin.BossHpScalingEnabled && CurrentMultiplier > 1.0001f;

        public static int EffectivePlayerCount
        {
            get
            {
                int count = MultiplayerSession.ActivePlayerCount;
                return Mathf.Max(1, count);
            }
        }

        public static float CurrentMultiplier =>
            1f + Mathf.Max(0, EffectivePlayerCount - 1) * Plugin.BossHpPerExtraPlayer;

        public static string GetStatusSummary()
        {
            if (!Plugin.BossHpScalingEnabled)
                return "Boss HP scaling: OFF";

            return "Boss HP scaling: ON | "
                + EffectivePlayerCount
                + " players | x"
                + CurrentMultiplier.ToString("0.00")
                + " | targets "
                + _scaledBossCount
                + " bosses";
        }

        public static void Update()
        {
            string sceneName = GetActiveSceneName();
            if (!string.Equals(sceneName, _lastSceneName, StringComparison.OrdinalIgnoreCase))
                ResetForScene(sceneName);

            _lastPlayerCount = EffectivePlayerCount;
            _lastMultiplier = CurrentMultiplier;

            if (!Plugin.BossHpScalingEnabled)
                return;
            if (Level.Current == null || Level.Current.LevelType != Level.Type.Battle)
                return;
            if (_lastMultiplier <= 1.0001f)
                return;
            if (Time.unscaledTime < _nextScanAt)
                return;

            _nextScanAt = Time.unscaledTime + 0.75f;
            ScanBattleProperties(_lastMultiplier);
            ScanBossDamageReceivers(_lastMultiplier);
        }

        public static void Reset()
        {
            ResetForScene(string.Empty);
        }

        private static void ResetForScene(string sceneName)
        {
            _lastSceneName = sceneName ?? string.Empty;
            _nextScanAt = 0f;
            _scaledBossCount = 0;
            ScaledPropertyInstances.Clear();
            ScaledDamageReceivers.Clear();
        }

        private static void ScanBattleProperties(float multiplier)
        {
            var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null || !behaviour.gameObject.activeInHierarchy)
                    continue;

                var fields = GetPropertyFields(behaviour.GetType());
                for (int j = 0; j < fields.Length; j++)
                {
                    var field = fields[j];
                    object value = null;
                    try { value = field.GetValue(behaviour); }
                    catch { }

                    if (value == null)
                        continue;

                    int instanceKey = RuntimeHelpers.GetHashCode(value);
                    if (ScaledPropertyInstances.Contains(instanceKey))
                        continue;

                    if (TryScaleLevelProperties(value, behaviour, field, multiplier))
                    {
                        ScaledPropertyInstances.Add(instanceKey);
                        _scaledBossCount++;
                    }
                }
            }
        }

        private static void ScanBossDamageReceivers(float multiplier)
        {
            if (ScaledPropertyInstances.Count > 0)
                return;

            var receivers = UnityEngine.Object.FindObjectsOfType<DamageReceiver>();
            for (int i = 0; i < receivers.Length; i++)
            {
                var receiver = receivers[i];
                if (receiver == null
                 || receiver.type != DamageReceiver.Type.Enemy
                 || receiver.gameObject == null
                 || !receiver.gameObject.activeInHierarchy)
                    continue;

                int id = receiver.gameObject.GetInstanceID();
                if (ScaledDamageReceivers.Contains(id))
                    continue;

                if (!IsBossLike(receiver.gameObject.name))
                    continue;

                if (TryScaleDamageReceiverHp(receiver, multiplier))
                {
                    ScaledDamageReceivers.Add(id);
                    _scaledBossCount++;
                }
            }
        }

        private static FieldInfo[] GetPropertyFields(Type type)
        {
            FieldInfo[] cached;
            if (PropertyFieldCache.TryGetValue(type, out cached))
                return cached;

            var found = new List<FieldInfo>();
            var fields = type.GetFields(Binding);
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field == null || !IsLevelPropertiesType(field.FieldType))
                    continue;
                found.Add(field);
            }

            cached = found.ToArray();
            PropertyFieldCache[type] = cached;
            return cached;
        }

        private static bool IsLevelPropertiesType(Type type)
        {
            while (type != null)
            {
                if (type.IsGenericType
                 && type.GetGenericTypeDefinition() == typeof(AbstractLevelProperties<,,>))
                    return true;
                type = type.BaseType;
            }

            return false;
        }

        private static bool TryScaleLevelProperties(
            object properties,
            MonoBehaviour owner,
            FieldInfo field,
            float multiplier)
        {
            if (properties == null || owner == null || field == null)
                return false;

            Type type = properties.GetType();
            var totalField = type.GetField("TotalHealth", Binding);
            var currentProperty = type.GetProperty("CurrentHealth", Binding);
            if (totalField == null || currentProperty == null || !currentProperty.CanRead || !currentProperty.CanWrite)
                return false;

            float totalHealth;
            float currentHealth;
            try
            {
                totalHealth = (float)totalField.GetValue(properties);
                currentHealth = (float)currentProperty.GetValue(properties, null);
            }
            catch
            {
                return false;
            }

            if (totalHealth <= 1f || currentHealth <= 0f)
                return false;
            if (!ShouldScaleProperty(owner, field, type, totalHealth))
                return false;

            float scaledTotal = totalHealth * multiplier;
            float scaledCurrent = currentHealth * multiplier;

            try
            {
                totalField.SetValue(properties, scaledTotal);
                currentProperty.SetValue(properties, scaledCurrent, null);
                Plugin.Log.LogInfo(
                    "[BossScale] "
                    + owner.GetType().Name
                    + "."
                    + field.Name
                    + " scaled from "
                    + totalHealth.ToString("0.##")
                    + " to "
                    + scaledTotal.ToString("0.##")
                    + " (x"
                    + multiplier.ToString("0.00")
                    + ").");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogVerbose("[BossScale] Failed scaling " + owner.GetType().Name + "." + field.Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool ShouldScaleProperty(MonoBehaviour owner, FieldInfo field, Type propertyType, float totalHealth)
        {
            if (Level.Current == null || Level.Current.LevelType != Level.Type.Battle)
                return false;

            string descriptor = (owner.GetType().FullName ?? string.Empty)
                + " "
                + owner.gameObject.name
                + " "
                + field.Name
                + " "
                + (propertyType.FullName ?? string.Empty);

            if (IsBossLike(descriptor))
                return true;

            return totalHealth >= 10f;
        }

        private static bool TryScaleDamageReceiverHp(DamageReceiver receiver, float multiplier)
        {
            var hpField = FindDamageReceiverHpField(receiver);
            if (hpField == null)
                return false;

            float currentHp;
            try { currentHp = (float)hpField.GetValue(receiver); }
            catch { return false; }

            if (currentHp <= 1f)
                return false;

            try
            {
                hpField.SetValue(receiver, currentHp * multiplier);
                Plugin.LogVerbose(
                    "[BossScale] Fallback HP scale on "
                    + receiver.name
                    + " from "
                    + currentHp.ToString("0.##")
                    + " to "
                    + (currentHp * multiplier).ToString("0.##")
                    + ".");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static FieldInfo FindDamageReceiverHpField(DamageReceiver receiver)
        {
            if (_damageReceiverHpFieldSearched)
                return _damageReceiverHpField;

            _damageReceiverHpFieldSearched = true;
            var type = receiver.GetType();
            string[] names = { "hp", "HP", "health", "_hp", "currentHp", "currentHealth" };
            for (int i = 0; i < names.Length; i++)
            {
                var field = type.GetField(names[i], Binding);
                if (field != null && field.FieldType == typeof(float))
                {
                    _damageReceiverHpField = field;
                    break;
                }
            }

            return _damageReceiverHpField;
        }

        private static bool IsBossLike(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string name = value.ToLowerInvariant();
            for (int i = 0; i < BossKeywords.Length; i++)
            {
                if (name.Contains(BossKeywords[i]))
                    return true;
            }

            return false;
        }

        private static string GetActiveSceneName()
        {
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                return string.IsNullOrEmpty(scene.name) ? string.Empty : scene.name;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
