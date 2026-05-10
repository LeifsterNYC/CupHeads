using System;
using System.Collections.Generic;
using HarmonyLib;

namespace CupheadOnline.Coop
{
    /// <summary>
    /// Single entry point Plugin.cs calls into for the gameplay-sync layer. Keeps
    /// Plugin.cs free of per-sync-component plumbing.
    /// </summary>
    internal static class CoopBootstrap
    {
        public static void RegisterPatches(Harmony harmony, HashSet<Type> registeredPatchTypes)
        {
            // Single Harmony patch class covering all three motor types' FixedUpdate.
            registeredPatchTypes?.Add(typeof(RemotePlayerMotorBypass));
            try
            {
                harmony.CreateClassProcessor(typeof(RemotePlayerMotorBypass)).Patch();
                Plugin.Log.LogInfo("[Coop] Registered RemotePlayerMotorBypass.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Coop] Patch failed: " + ex.Message);
            }

            // Reset state on session end so we don't carry stale buffers into the next lobby.
            MultiplayerSession.OnSessionEnded += PlayerStateSync.Reset;
        }

        public static void Tick(float dt)
        {
            PlayerStateSync.Tick(dt);
        }
    }
}
