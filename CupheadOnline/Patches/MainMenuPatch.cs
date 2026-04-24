using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CupheadOnline.UI;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Hooks StartScreen.Start(), then polls every 0.2 s until titleAnimation
    /// is active.  Only THEN does it attempt injection so we don't fire during
    /// the intro animation when the interactive menu isn't ready yet.
    /// </summary>
    [HarmonyPatch(typeof(StartScreen), "Start")]
    public static class MainMenuPatch
    {
        // Cached reflection handle (looked up once)
        static FieldInfo _titleAnimField;

        static MainMenuPatch()
        {
            _titleAnimField = typeof(StartScreen).GetField(
                "titleAnimation",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (_titleAnimField == null)
                Plugin.Log.LogWarning("[UI] Could not find StartScreen.titleAnimation field.");
            else
                Plugin.Log.LogInfo("[UI] Found StartScreen.titleAnimation field.");
        }

        static void Postfix(StartScreen __instance)
        {
            __instance.StartCoroutine(WaitAndInject(__instance));
        }

        static IEnumerator WaitAndInject(StartScreen instance)
        {
            // Poll until titleAnimation exists and is active in the hierarchy
            float waited = 0f;
            const float TIMEOUT = 15f;

            while (waited < TIMEOUT)
            {
                var titleAnim = GetTitleAnim(instance);
                if (titleAnim != null && titleAnim.activeInHierarchy)
                {
                    Plugin.Log.LogInfo("[UI] titleAnimation is active — injecting now.");
                    MultiplayerMenuInjector.Inject(titleAnim);
                    yield break;
                }
                yield return new WaitForSeconds(0.2f);
                waited += 0.2f;
            }

            // Timeout — inject anyway (will do fallback)
            Plugin.Log.LogWarning("[UI] titleAnimation never became active after "
                                  + TIMEOUT + "s — injecting fallback.");
            MultiplayerMenuInjector.Inject(null);
        }

        internal static GameObject GetTitleAnim(StartScreen instance)
        {
            if (_titleAnimField == null) return null;
            return _titleAnimField.GetValue(instance) as GameObject;
        }
    }

    [HarmonyPatch(typeof(StartScreen), "Update")]
    public static class StartScreenSplashGatePatch
    {
        static bool Prefix()
        {
            return !StartupSplashPlayer.IsBlockingGame;
        }
    }

    [HarmonyPatch(typeof(StartScreenAudio), "Update")]
    public static class StartScreenAudioSplashGatePatch
    {
        static bool Prefix()
        {
            return !StartupSplashPlayer.IsBlockingGame;
        }
    }
}
