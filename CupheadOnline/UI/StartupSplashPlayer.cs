using System;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Plays an optional studio-style intro before the title screen settles.
    /// The splash is deliberately fail-open: missing or unsupported video files
    /// should never block Cuphead from reaching the main menu.
    /// </summary>
    public sealed class StartupSplashPlayer : MonoBehaviour
    {
        private const string VideoFileName = "CupHeadsIntro.mp4";
        private const float PrepareTimeoutSeconds = 8f;
        private const float FadeOutSeconds = 0.35f;
        private const int NoiseWidth = 320;
        private const int NoiseHeight = 180;

        public static StartupSplashPlayer Instance { get; private set; }

        private CanvasGroup _canvasGroup;
        private RawImage _videoImage;
        private RawImage _noiseImage;
        private VideoPlayer _videoPlayer;
        private AudioSource _audioSource;
        private RenderTexture _renderTexture;
        private Texture2D _noiseTexture;
        private Color32[] _noisePixels;

        private string _videoPath;
        private float _createdAt;
        private float _nextNoiseAt;
        private float _closingStartedAt = -1f;
        private float _previousTimeScale = 1f;
        private bool _previousAudioListenerPause;
        private bool _prepared;
        private bool _closing;
        private bool _gateApplied;

        public static bool IsBlockingGame => Instance != null && Instance._gateApplied;

        public static void TryShow()
        {
            if (!Plugin.EnableStartupSplash)
            {
                Hide();
                return;
            }

            if (Instance != null)
                return;

            string path = ResolveVideoPath();
            if (string.IsNullOrEmpty(path))
            {
                Plugin.Log.LogInfo("[StartupSplash] No CupHeadsIntro.mp4 found; skipping splash.");
                return;
            }

            var go = new GameObject("CupHeads_StartupSplash");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<StartupSplashPlayer>();
            Instance.Begin(path);
        }

        public static void Hide()
        {
            if (Instance == null)
                return;

            Destroy(Instance.gameObject);
            Instance = null;
        }

        public static string ResolveVideoPath()
        {
            try
            {
                string pluginRoot = Path.Combine(Paths.PluginPath, "CupheadOnline");
                string candidate = Path.Combine(Path.Combine(pluginRoot, "Assets"), VideoFileName);
                if (File.Exists(candidate))
                    return candidate;

                string assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (!string.IsNullOrEmpty(assemblyDir))
                {
                    candidate = Path.Combine(Path.Combine(assemblyDir, "Assets"), VideoFileName);
                    if (File.Exists(candidate))
                        return candidate;

                    candidate = Path.Combine(Path.Combine(assemblyDir, "StartupSplash"), VideoFileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[StartupSplash] Could not resolve video path: " + ex.Message);
            }

            return null;
        }

        private void Begin(string videoPath)
        {
            _videoPath = videoPath;
            _createdAt = Time.unscaledTime;
            BuildCanvas();
            BuildStaticOverlay();
            ApplyGameGate();
            StartCoroutine(PrepareWhenSafe());
        }

        private System.Collections.IEnumerator PrepareWhenSafe()
        {
            // Let Unity finish the boot frame, but keep Cuphead paused behind the splash.
            yield return null;
            yield return null;

            if (_closing || string.IsNullOrEmpty(_videoPath))
                yield break;

            try
            {
                BuildVideoPlayer(_videoPath);
                _videoPlayer.Prepare();
                Plugin.Log.LogInfo("[StartupSplash] Preparing " + _videoPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[StartupSplash] Video prepare failed: " + ex.Message);
                BeginClose(true);
            }
        }

        private void BuildCanvas()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 1f;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = true;

            var background = new GameObject("BlackBackground");
            background.transform.SetParent(transform, false);
            var bgRect = background.AddComponent<RectTransform>();
            Stretch(bgRect);
            var bg = background.AddComponent<Image>();
            bg.color = Color.black;

            var video = new GameObject("Video");
            video.transform.SetParent(transform, false);
            var videoRect = video.AddComponent<RectTransform>();
            Stretch(videoRect);
            _videoImage = video.AddComponent<RawImage>();
            _videoImage.color = Color.white;

            var fitter = video.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = 16f / 9f;
        }

        private void BuildVideoPlayer(string videoPath)
        {
            _renderTexture = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
            _renderTexture.name = "CupHeads_StartupSplash_RT";
            _renderTexture.Create();

            if (_videoImage != null)
                _videoImage.texture = _renderTexture;

            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.ignoreListenerPause = true;
            _audioSource.volume = Mathf.Clamp01(Plugin.StartupSplashVolume);

            _videoPlayer = gameObject.AddComponent<VideoPlayer>();
            _videoPlayer.playOnAwake = false;
            _videoPlayer.waitForFirstFrame = true;
            _videoPlayer.isLooping = false;
            _videoPlayer.source = VideoSource.Url;
            _videoPlayer.url = videoPath;
            _videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            _videoPlayer.targetTexture = _renderTexture;
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _videoPlayer.controlledAudioTrackCount = 1;
            _videoPlayer.EnableAudioTrack(0, true);
            _videoPlayer.SetTargetAudioSource(0, _audioSource);
            _videoPlayer.prepareCompleted += OnPrepared;
            _videoPlayer.loopPointReached += OnFinished;
            _videoPlayer.errorReceived += OnError;
        }

        private void BuildStaticOverlay()
        {
            if (!Plugin.StartupSplashStaticOverlay)
                return;

            _noiseTexture = new Texture2D(NoiseWidth, NoiseHeight, TextureFormat.RGBA32, false);
            _noiseTexture.name = "CupHeads_StartupSplash_Static";
            _noiseTexture.filterMode = FilterMode.Point;
            _noiseTexture.wrapMode = TextureWrapMode.Repeat;
            _noisePixels = new Color32[NoiseWidth * NoiseHeight];

            var noise = new GameObject("FilmStatic");
            noise.transform.SetParent(transform, false);
            var noiseRect = noise.AddComponent<RectTransform>();
            Stretch(noiseRect);
            _noiseImage = noise.AddComponent<RawImage>();
            _noiseImage.texture = _noiseTexture;
            _noiseImage.raycastTarget = false;
            UpdateNoise(true);
        }

        private void ApplyGameGate()
        {
            if (_gateApplied)
                return;

            _previousTimeScale = Time.timeScale;
            _previousAudioListenerPause = AudioListener.pause;
            Time.timeScale = 0f;
            AudioListener.pause = true;
            _gateApplied = true;
        }

        private void ReleaseGameGate()
        {
            if (!_gateApplied)
                return;

            Time.timeScale = _previousTimeScale;
            AudioListener.pause = _previousAudioListenerPause;
            _gateApplied = false;
        }

        private void Update()
        {
            if (_closing)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - _closingStartedAt) / FadeOutSeconds);
                if (_canvasGroup != null)
                    _canvasGroup.alpha = 1f - t;
                if (t >= 1f)
                {
                    ReleaseGameGate();
                    Hide();
                }
                return;
            }

            if (!_prepared && Time.unscaledTime - _createdAt > PrepareTimeoutSeconds)
            {
                Plugin.Log.LogWarning("[StartupSplash] Prepare timeout; skipping splash.");
                BeginClose(true);
                return;
            }

            if (Plugin.StartupSplashStaticOverlay && Time.unscaledTime >= _nextNoiseAt)
                UpdateNoise(false);

            if (Plugin.StartupSplashAllowSkip && IsSkipPressed())
            {
                Plugin.Log.LogInfo("[StartupSplash] Skipped by player.");
                BeginClose(false);
            }
        }

        private void OnPrepared(VideoPlayer source)
        {
            if (_closing || source == null)
                return;

            _prepared = true;

            try
            {
                source.Play();
                Plugin.Log.LogInfo("[StartupSplash] Playing startup splash with audio.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[StartupSplash] Video play failed: " + ex.Message);
                BeginClose(true);
            }
        }

        private void OnFinished(VideoPlayer source)
        {
            BeginClose(false);
        }

        private void OnError(VideoPlayer source, string message)
        {
            Plugin.Log.LogWarning("[StartupSplash] Video error: " + message);
            BeginClose(true);
        }

        private void BeginClose(bool immediate)
        {
            if (_closing)
                return;

            StopPlayback();

            if (immediate)
            {
                if (_canvasGroup != null)
                    _canvasGroup.alpha = 0f;
                ReleaseGameGate();
                Hide();
                return;
            }

            _closing = true;
            _closingStartedAt = Time.unscaledTime;
        }

        private void StopPlayback()
        {
            try
            {
                if (_videoPlayer != null)
                {
                    _videoPlayer.Stop();
                    _videoPlayer.targetTexture = null;
                    _videoPlayer.enabled = false;
                }

                if (_audioSource != null)
                {
                    _audioSource.Stop();
                    _audioSource.clip = null;
                    _audioSource.enabled = false;
                }
            }
            catch
            {
                // Shutdown must never block the main menu.
            }
        }

        private void UpdateNoise(bool force)
        {
            if (!force && _noiseTexture == null)
                return;

            _nextNoiseAt = Time.unscaledTime + 0.035f;
            float intensity = Mathf.Clamp01(Plugin.StartupSplashStaticIntensity);
            byte maxAlpha = (byte)Mathf.RoundToInt(135f * intensity);

            for (int y = 0; y < NoiseHeight; y++)
            {
                bool scanline = (y % 7) == 0;
                for (int x = 0; x < NoiseWidth; x++)
                {
                    int index = y * NoiseWidth + x;
                    if (UnityEngine.Random.value > 0.67f || scanline)
                    {
                        byte value = (byte)UnityEngine.Random.Range(190, 256);
                        byte alpha = scanline
                            ? (byte)Mathf.RoundToInt(maxAlpha * 0.35f)
                            : (byte)UnityEngine.Random.Range(8, Mathf.Max(9, maxAlpha));
                        _noisePixels[index] = new Color32(value, value, value, alpha);
                    }
                    else
                    {
                        _noisePixels[index] = new Color32(0, 0, 0, 0);
                    }
                }
            }

            _noiseTexture.SetPixels32(_noisePixels);
            _noiseTexture.Apply(false);

            if (_noiseImage != null)
            {
                float flicker = UnityEngine.Random.Range(0.72f, 1.16f);
                _noiseImage.color = new Color(1f, 1f, 1f, Mathf.Clamp01(intensity * flicker));
            }
        }

        private static bool IsSkipPressed()
        {
            return Input.GetKeyDown(KeyCode.Escape)
                   || Input.GetKeyDown(KeyCode.Return)
                   || Input.GetKeyDown(KeyCode.Space)
                   || Input.GetKeyDown(KeyCode.Z)
                   || Input.GetKeyDown(KeyCode.JoystickButton0)
                   || Input.GetKeyDown(KeyCode.JoystickButton1)
                   || Input.GetKeyDown(KeyCode.JoystickButton7);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            try
            {
                ReleaseGameGate();
                StopPlayback();

                if (_videoPlayer != null)
                {
                    _videoPlayer.prepareCompleted -= OnPrepared;
                    _videoPlayer.loopPointReached -= OnFinished;
                    _videoPlayer.errorReceived -= OnError;
                }

                if (_renderTexture != null)
                {
                    _renderTexture.Release();
                    Destroy(_renderTexture);
                    _renderTexture = null;
                }

                if (_noiseTexture != null)
                {
                    Destroy(_noiseTexture);
                    _noiseTexture = null;
                }
            }
            catch
            {
                // Unity may already be tearing objects down.
            }
        }
    }
}
