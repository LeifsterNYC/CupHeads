using System.Collections;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.UI;

namespace CupheadOnline.Patches
{
    // ────────────────────────────────────────────────────────────────────────────
    //  ARCHITECTURE
    //
    //  Main menu arrays are extended by TWO items:
    //    sentinel 99  → MULTIPLAYER (opens MP sub-menu)
    //    sentinel 100 → CREDITS     (opens Credits panel)
    //
    //  MultiplayerMenuChild
    //    ├─ Layout (VerticalLayoutGroup + ContentSizeFitter)
    //    │    ├─ HOST GAME  (sentinel 100)
    //    │    ├─ JOIN GAME  (sentinel 101)
    //    │    └─ BACK       (sentinel 102)
    //    ├─ StatusText    (animated dots while waiting)
    //    └─ PresenceText  (live lobby member list)
    //
    //  CreditsPanel
    //    └─ Layout (VerticalLayoutGroup)
    //         ├─ TitleText  ("CREDITS")
    //         ├─ BodyText   (content)
    //         └─ HintText   ("Press B / Back to return")
    //
    //  Prefix skips original UpdateMainMenu while InMpMenu or InCredits.
    // ────────────────────────────────────────────────────────────────────────────

    // ── MP state ─────────────────────────────────────────────────────────────────

    internal static class MpMenuState
    {
        internal static bool             InMpMenu;
        internal static int              MpSelection;        // 0=HOST 1=JOIN 2=BACK
        internal static int              SavedMainSel;
        internal static bool             InputLocked;        // blocks Accept during critical states

        internal static GameObject       MainContainer;
        internal static GameObject       MpContainer;
        internal static CanvasGroup      MpCanvasGroup;
        internal static Text[]           MpItems;            // [HOST GAME, JOIN GAME, BACK]
        internal static Text             StatusText;
        internal static Text             PresenceText;
        internal static SlotSelectScreen ScreenInstance;

        // Status animation
        internal static string           StatusBase  = "";
        internal static bool             StatusAnimate;

        // ── Reflection handles ────────────────────────────────────────────────
        internal static readonly BindingFlags BF =
            BindingFlags.NonPublic | BindingFlags.Instance;

        internal static readonly FieldInfo TextItemsField =
            typeof(SlotSelectScreen).GetField("mainMenuItems", BF);
        internal static readonly FieldInfo EnumItemsField =
            typeof(SlotSelectScreen).GetField("_availableMainMenuItems", BF);
        internal static readonly FieldInfo SelectionField =
            typeof(SlotSelectScreen).GetField("_mainMenuSelection", BF);
        internal static readonly FieldInfo TimeSinceStartField =
            typeof(SlotSelectScreen).GetField("timeSinceStart", BF);
        internal static readonly FieldInfo SelectedColorField =
            typeof(SlotSelectScreen).GetField("mainMenuSelectedColor", BF);
        internal static readonly FieldInfo UnselectedColorField =
            typeof(SlotSelectScreen).GetField("mainMenuUnselectedColor", BF);
        internal static readonly MethodInfo GetButtonDownMethod =
            typeof(SlotSelectScreen).GetMethod("GetButtonDown",
                BF, null, new[] { typeof(CupheadButton) }, null);

        internal static void Reset()
        {
            InMpMenu       = false;
            MpSelection    = 0;
            SavedMainSel   = 0;
            InputLocked    = false;
            MainContainer  = null;
            MpContainer    = null;
            MpCanvasGroup  = null;
            MpItems        = null;
            StatusText     = null;
            PresenceText   = null;
            ScreenInstance = null;
            StatusBase     = "";
            StatusAnimate  = false;
        }

        internal static void SetStatus(string msg, bool animate = false)
        {
            StatusBase    = msg;
            StatusAnimate = animate;
            if (StatusText != null) StatusText.text = msg;
        }

        internal static SlotSelectScreen Resolve(SlotSelectScreen inst) => inst ?? ScreenInstance;

        internal static Color SelColor(SlotSelectScreen inst)
        {
            var target = Resolve(inst);
            if (SelectedColorField == null || target == null) return Color.white;

            try { return (Color)SelectedColorField.GetValue(target); }
            catch { return Color.white; }
        }

        internal static Color UnselColor(SlotSelectScreen inst)
        {
            var target = Resolve(inst);
            if (UnselectedColorField == null || target == null) return Color.grey;

            try { return (Color)UnselectedColorField.GetValue(target); }
            catch { return Color.grey; }
        }

        internal static bool Btn(SlotSelectScreen inst, CupheadButton b)
        {
            var target = Resolve(inst);
            if (GetButtonDownMethod == null || target == null) return false;

            try { return (bool)GetButtonDownMethod.Invoke(target, new object[] { b }); }
            catch { return false; }
        }
    }

    // ── Credits state ─────────────────────────────────────────────────────────────

    internal static class CreditsState
    {
        internal static bool             InCredits;
        internal static GameObject       CreditsContainer;
        internal static CanvasGroup      CreditsCanvasGroup;
        internal static GameObject       MainContainer;   // pointer to main menu root
        internal static SlotSelectScreen ScreenInstance;

        internal static void Reset()
        {
            InCredits          = false;
            CreditsContainer   = null;
            CreditsCanvasGroup = null;
            MainContainer      = null;
            ScreenInstance     = null;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    //  AWAKE PATCH — builds MP container + Credits panel, extends arrays
    // ────────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(SlotSelectScreen), "Awake")]
    public static class SlotSelectAwakePatch
    {
        static void Postfix(SlotSelectScreen __instance)
        {
            MpMenuState.Reset();
            MpMenuState.ScreenInstance    = __instance;
            CreditsState.Reset();
            CreditsState.ScreenInstance   = __instance;

            var fi = MpMenuState.TextItemsField;
            var ei = MpMenuState.EnumItemsField;
            if (fi == null || ei == null) { Plugin.Log.LogWarning("[Menu] Reflection fields missing."); return; }

            var textItems = fi.GetValue(__instance) as Text[];
            var enumItems = ei.GetValue(__instance) as SlotSelectScreen.MainMenuItem[];
            if (textItems == null || textItems.Length == 0 || enumItems == null)
            { Plugin.Log.LogWarning("[Menu] Arrays null."); return; }

            MpMenuState.MainContainer    = textItems[0].transform.parent.gameObject;
            CreditsState.MainContainer   = MpMenuState.MainContainer;
            var containerParent          = MpMenuState.MainContainer.transform.parent;

            var exitGO = textItems[textItems.Length - 1].gameObject;
            var sample = textItems[0];

            // ── Build MultiplayerMenuChild ────────────────────────────────────
            var mpRoot = BuildPanel("MultiplayerMenuChild", containerParent,
                                    MpMenuState.MainContainer.GetComponent<RectTransform>());
            var mpCg   = mpRoot.AddComponent<CanvasGroup>();
            mpCg.alpha = 0f;
            mpRoot.SetActive(false);
            MpMenuState.MpContainer   = mpRoot;
            MpMenuState.MpCanvasGroup = mpCg;

            var mpLayout   = BuildLayout(mpRoot, new Vector2(0f, 20f));
            var hostGO     = CloneItem(exitGO, mpLayout, "HOST GAME");
            var joinGO     = CloneItem(exitGO, mpLayout, "JOIN GAME");
            var backGO     = CloneItem(exitGO, mpLayout, "BACK");

            MpMenuState.MpItems = new[]
            {
                hostGO.GetComponent<Text>(),
                joinGO.GetComponent<Text>(),
                backGO.GetComponent<Text>(),
            };

            var statusGO = BuildText(mpRoot, "StatusText",
                new Vector2(0f, -80f), new Vector2(700f, 70f),
                sample.font, Mathf.Max(15, sample.fontSize - 4),
                new Color(0.9f, 0.85f, 0.5f, 1f));
            MpMenuState.StatusText = statusGO.GetComponent<Text>();

            var presenceGO = BuildText(mpRoot, "PresenceText",
                new Vector2(0f, -150f), new Vector2(600f, 60f),
                sample.font, Mathf.Max(13, sample.fontSize - 6),
                new Color(0.75f, 0.75f, 0.75f, 0.85f));
            MpMenuState.PresenceText = presenceGO.GetComponent<Text>();

            var mpHintGO = BuildText(mpRoot, "MpHintText",
                new Vector2(0f, -220f), new Vector2(600f, 30f),
                sample.font, Mathf.Max(12, sample.fontSize - 6),
                new Color(0.6f, 0.6f, 0.6f, 0.8f));
            var mpHintT = mpHintGO.GetComponent<Text>();
            if (mpHintT != null) mpHintT.text = "[ Press Escape to go back to menu ]";

            // ── Build CreditsPanel ────────────────────────────────────────────
            var credRoot = BuildPanel("CreditsPanel", containerParent,
                                      MpMenuState.MainContainer.GetComponent<RectTransform>());
            var credCg   = credRoot.AddComponent<CanvasGroup>();
            credCg.alpha = 0f;
            credRoot.SetActive(false);
            CreditsState.CreditsContainer   = credRoot;
            CreditsState.CreditsCanvasGroup = credCg;

            // All credits content lives inside one VLG so nothing overlaps.
            // Offset moves the whole block up from screen centre.
            var credLayout = BuildLayout(credRoot, new Vector2(0f, 50f));

            // Title
            var titleGO = CloneItem(exitGO, credLayout, "CREDITS");
            var titleT  = titleGO.GetComponent<Text>();
            if (titleT != null)
            {
                titleT.fontSize = sample.fontSize + 4;
                titleT.color    = MpMenuState.SelColor(__instance);
                titleT.horizontalOverflow = HorizontalWrapMode.Wrap;
                titleT.verticalOverflow   = VerticalWrapMode.Overflow;
                titleT.lineSpacing        = 1.1f;
            }
            var titleLE = titleGO.GetComponent<LayoutElement>();
            if (titleLE != null)
            {
                titleLE.preferredHeight = 90f;
                titleLE.preferredWidth  = 700f;
            }

            // Body — child of the VLG, not absolute
            const string BODY =
                "Multiplayer Mod\n" +
                "Made by Germanized / Sh0kr\n\n" +
                "Built for Daniel\u2014\n" +
                "cuz me and him wanna play.";

            var bodyGO = BuildText(credLayout, "BodyText",
                Vector2.zero, new Vector2(700f, 110f),
                sample.font, Mathf.Max(15, sample.fontSize - 3),
                new Color(0.92f, 0.92f, 0.92f, 1f));
            var bodyT = bodyGO.GetComponent<Text>();
            if (bodyT != null)
            {
                bodyT.text = BODY;
                bodyT.horizontalOverflow = HorizontalWrapMode.Wrap;
                bodyT.verticalOverflow   = VerticalWrapMode.Overflow;
                bodyT.lineSpacing        = 1.2f;
            }
            var bodyLE = bodyGO.AddComponent<LayoutElement>();
            bodyLE.preferredHeight = 160f;
            bodyLE.preferredWidth  = 700f;

            // Hint — child of the VLG
            var hintGO = BuildText(credLayout, "HintText",
                Vector2.zero, new Vector2(600f, 30f),
                sample.font, Mathf.Max(12, sample.fontSize - 6),
                new Color(0.6f, 0.6f, 0.6f, 0.8f));
            var hintT = hintGO.GetComponent<Text>();
            if (hintT != null) hintT.text = "[ Press Escape to go back ]";
            var hintLE = hintGO.AddComponent<LayoutElement>();
            hintLE.preferredHeight = 30f;
            hintLE.preferredWidth  = 600f;

            // ── Append MULTIPLAYER + CREDITS to main-menu arrays ──────────────
            var mpLabelGO  = CloneItem(exitGO, MpMenuState.MainContainer, "MULTIPLAYER");
            var credLabelGO = CloneItem(exitGO, MpMenuState.MainContainer, "CREDITS");
            PositionBelow(textItems, mpLabelGO);
            PositionBelow(textItems, credLabelGO, mpLabelGO);  // credits one step below MP

            var newTexts  = new Text[textItems.Length + 2];
            textItems.CopyTo(newTexts, 0);
            newTexts[newTexts.Length - 2] = mpLabelGO.GetComponent<Text>();
            newTexts[newTexts.Length - 1] = credLabelGO.GetComponent<Text>();
            fi.SetValue(__instance, newTexts);

            var newEnums = new SlotSelectScreen.MainMenuItem[enumItems.Length + 2];
            enumItems.CopyTo(newEnums, 0);
            newEnums[newEnums.Length - 2] = (SlotSelectScreen.MainMenuItem)99;
            newEnums[newEnums.Length - 1] = (SlotSelectScreen.MainMenuItem)100;
            ei.SetValue(__instance, newEnums);

            __instance.StartCoroutine(EnforceLabels(
                mpLabelGO.GetComponent<Text>(),   "MULTIPLAYER",
                credLabelGO.GetComponent<Text>(), "CREDITS",
                hostGO.GetComponent<Text>(),      "HOST GAME",
                joinGO.GetComponent<Text>(),      "JOIN GAME",
                backGO.GetComponent<Text>(),      "BACK",
                titleT,                            "CREDITS"));

            Plugin.Log.LogInfo("[Menu] Setup complete.");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        static GameObject BuildPanel(string name, Transform parent, RectTransform srcRT)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            if (srcRT != null)
            {
                rt.anchorMin        = srcRT.anchorMin;
                rt.anchorMax        = srcRT.anchorMax;
                rt.pivot            = srcRT.pivot;
                rt.anchoredPosition = srcRT.anchoredPosition;
                rt.sizeDelta        = srcRT.sizeDelta;
            }
            return go;
        }

        static GameObject BuildLayout(GameObject parent, Vector2 offset)
        {
            var go   = new GameObject("Layout");
            go.transform.SetParent(parent.transform, false);
            var rt   = go.AddComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = offset;
            rt.sizeDelta        = Vector2.zero;
            var vlg  = go.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment        = TextAnchor.MiddleCenter;
            vlg.spacing               = 8f;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight= false;
            vlg.childControlWidth     = true;
            vlg.childControlHeight    = true;
            go.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            return go;
        }

        static GameObject CloneItem(GameObject source, GameObject newParent, string label)
        {
            var go = UnityEngine.Object.Instantiate(source, newParent.transform, false);
            go.name = label;
            foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
            {
                if (b == null || b is Text) continue;
                b.enabled = false;
            }
            var t = go.GetComponent<Text>();
            if (t != null) { t.text = label; t.horizontalOverflow = HorizontalWrapMode.Overflow; }
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            // Use a fixed height — Cuphead text items have sizeDelta.y = 0 (auto-sized),
            // which would make VLG stack everything at the same position.
            le.preferredHeight = 40f;
            le.preferredWidth  = 300f;
            return go;
        }

        static GameObject BuildText(GameObject parent, string name,
                                    Vector2 pos, Vector2 size,
                                    Font font, int fontSize, Color color)
        {
            var go  = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rt  = go.AddComponent<RectTransform>();
            rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta        = size;
            var txt = go.AddComponent<Text>();
            txt.font               = font;
            txt.fontSize           = fontSize;
            txt.color              = color;
            txt.alignment          = TextAnchor.MiddleCenter;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow   = VerticalWrapMode.Overflow;
            txt.text               = "";
            return go;
        }

        // Place target one menu-step below 'above' (or below the last item if above is null).
        // Step is always derived from the spacing between existing menu items so it matches
        // the game's layout regardless of individual item sizeDelta values.
        static void PositionBelow(Text[] items, GameObject target, GameObject above = null)
        {
            if (items.Length < 1) return;

            // Derive the natural step from existing item spacing
            float step;
            int last = items.Length - 1;
            if (items.Length >= 2)
            {
                float lastY = items[last].GetComponent<RectTransform>().anchoredPosition.y;
                float prevY = items[last - 1].GetComponent<RectTransform>().anchoredPosition.y;
                step = lastY - prevY;   // negative (downward)
            }
            else
            {
                float h = items[last].GetComponent<RectTransform>().rect.height;
                step = -(Mathf.Max(h, 40f) + 5f);
            }

            var refGO = above != null ? above : items[last].gameObject;
            RectTransform refRT = refGO.GetComponent<RectTransform>();
            var tgtRT = target.GetComponent<RectTransform>();
            if (refRT == null || tgtRT == null) return;

            tgtRT.anchoredPosition = new Vector2(refRT.anchoredPosition.x,
                                                  refRT.anchoredPosition.y + step);
        }

        static IEnumerator EnforceLabels(params object[] pairs)
        {
            for (int f = 0; f < 30; f++)
            {
                yield return null;
                for (int i = 0; i + 1 < pairs.Length; i += 2)
                {
                    var t = pairs[i]   as Text;
                    var s = pairs[i+1] as string;
                    if (t != null && s != null && t.text != s) t.text = s;
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    //  UPDATE PATCH
    // ────────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(SlotSelectScreen), "UpdateMainMenu")]
    public static class SlotSelectUpdatePatch
    {
        static bool      _joinOverlayReady;
        static bool      _waitingForInvite;
        static float     _lastOverlayOpenTime = -999f;
        static float     _lastBackTime        = -999f;
        static int       _joinGeneration;
        static Coroutine _mpFadeRoutine;
        static Coroutine _creditsFadeRoutine;
        static Coroutine _dotsRoutine;
        static string    _lastPresence;

        static bool BackPressed(SlotSelectScreen inst) =>
            MpMenuState.Btn(inst, CupheadButton.Cancel)
         || MpMenuState.Btn(inst, CupheadButton.Pause);

        static void StopTrackedCoroutine(SlotSelectScreen inst, ref Coroutine routine)
        {
            if (inst != null && routine != null)
                inst.StopCoroutine(routine);

            routine = null;
        }

        static void StartTrackedCoroutine(SlotSelectScreen inst, ref Coroutine routine, IEnumerator body)
        {
            if (inst == null) return;
            StopTrackedCoroutine(inst, ref routine);
            routine = inst.StartCoroutine(body);
        }

        static void SetCanvasVisible(CanvasGroup cg, GameObject go, bool visible, float alpha = 1f)
        {
            if (go != null) go.SetActive(visible);
            if (cg == null) return;

            cg.alpha          = visible ? alpha : 0f;
            cg.interactable   = visible;
            cg.blocksRaycasts = visible;
        }

        static void HideCreditsImmediate()
        {
            CreditsState.InCredits = false;
            StopTrackedCoroutine(CreditsState.ScreenInstance, ref _creditsFadeRoutine);
            SetCanvasVisible(CreditsState.CreditsCanvasGroup, CreditsState.CreditsContainer, false);
        }

        static void HideMpMenuImmediate()
        {
            MpMenuState.InMpMenu    = false;
            MpMenuState.InputLocked = false;
            _joinOverlayReady       = false;
            _waitingForInvite       = false;
            if (Plugin.Net != null) Plugin.Net.OnOverlayClosed -= HandleOverlayClosed;
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine);
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine);
            SetCanvasVisible(MpMenuState.MpCanvasGroup, MpMenuState.MpContainer, false);
        }

        static void HandleOverlayClosed()
        {
            if (!_waitingForInvite) return;
            Plugin.Log.LogInfo("[Menu] Steam overlay closed — cancelling join wait.");
            CancelNetworkOperation("Overlay closed. Select an option.");
        }

        static bool Prefix(SlotSelectScreen __instance)
        {
            // Credits panel blocks all normal menu processing
            if (CreditsState.InCredits)
            {
                RunCredits(__instance);
                return false;
            }

            if (!MpMenuState.InMpMenu) return true;

            if (MpMenuState.TimeSinceStartField != null)
            {
                float t = (float)MpMenuState.TimeSinceStartField.GetValue(__instance);
                if (t < 0.75f) return false;
            }
            RunMpMenu(__instance);
            return false;
        }

        static void Postfix(SlotSelectScreen __instance)
        {
            if (MpMenuState.InMpMenu || CreditsState.InCredits) return;

            if (MpMenuState.TimeSinceStartField != null)
            {
                float t = (float)MpMenuState.TimeSinceStartField.GetValue(__instance);
                if (t < 0.75f) return;
            }

            if (LobbyScreen.Instance != null) return;

            var ef = MpMenuState.EnumItemsField;
            var sf = MpMenuState.SelectionField;
            if (ef == null || sf == null) return;

            int sel   = (int)sf.GetValue(__instance);
            var items = ef.GetValue(__instance) as SlotSelectScreen.MainMenuItem[];
            if (items == null || sel < 0 || sel >= items.Length) return;

            int sentinel = (int)items[sel];
            if (sentinel == 99 && MpMenuState.Btn(__instance, CupheadButton.Accept))
                EnterMpMenu(__instance);
            else if (sentinel == 100 && MpMenuState.Btn(__instance, CupheadButton.Accept))
                EnterCredits(__instance);
        }

        // ── Credits ──────────────────────────────────────────────────────────

        static void RunCredits(SlotSelectScreen inst)
        {
            bool exit = BackPressed(inst) || MpMenuState.Btn(inst, CupheadButton.Accept);
            if (exit)
            {
                PlayMenuSound("level_menu_confirm");
                ExitCredits(inst);
            }
        }

        static void EnterCredits(SlotSelectScreen inst)
        {
            if (CreditsState.CreditsContainer == null) return;
            if (CreditsState.InCredits) return;

            var sf = MpMenuState.SelectionField;
            if (sf != null && MpMenuState.MainContainer != null)
                MpMenuState.SavedMainSel = (int)sf.GetValue(inst);

            HideMpMenuImmediate();
            if (CreditsState.MainContainer != null) CreditsState.MainContainer.SetActive(false);
            SetCanvasVisible(CreditsState.CreditsCanvasGroup, CreditsState.CreditsContainer, true, 0f);
            CreditsState.InCredits = true;

            PlayMenuSound("level_menu_confirm");

            if (CreditsState.ScreenInstance != null && CreditsState.CreditsCanvasGroup != null)
                StartTrackedCoroutine(CreditsState.ScreenInstance, ref _creditsFadeRoutine,
                    FadeCanvas(CreditsState.CreditsCanvasGroup, 0f, 1f, 0.2f));

            Plugin.Log.LogInfo("[Menu] Entered Credits.");
        }

        static void ExitCredits(SlotSelectScreen inst)
        {
            CreditsState.InCredits = false;
            StopTrackedCoroutine(CreditsState.ScreenInstance, ref _creditsFadeRoutine);

            if (CreditsState.MainContainer != null) CreditsState.MainContainer.SetActive(true);
            if (CreditsState.ScreenInstance != null && CreditsState.CreditsCanvasGroup != null)
                StartTrackedCoroutine(CreditsState.ScreenInstance, ref _creditsFadeRoutine,
                    FadeAndHide(CreditsState.CreditsCanvasGroup, CreditsState.CreditsContainer, 0.15f));
            else
                SetCanvasVisible(CreditsState.CreditsCanvasGroup, CreditsState.CreditsContainer, false);

            // Restore menu selection to CREDITS item
            var sf   = MpMenuState.SelectionField;
            var fi   = MpMenuState.TextItemsField;
            var txts = fi?.GetValue(inst) as Text[];
            if (sf != null && txts != null)
                sf.SetValue(inst, txts.Length - 1);

            Plugin.Log.LogInfo("[Menu] Exited Credits.");
        }

        // ── MP menu loop ──────────────────────────────────────────────────────

        static void RunMpMenu(SlotSelectScreen inst)
        {
            if (MpMenuState.MpItems == null) return;

            // Sync InputLocked with live network state each frame.
            // Covers the case where SteamNetManager transitions to a new state
            // (e.g. invite arrives while _joinOverlayReady was pending).
            bool netLocked = Plugin.Net.IsInputLocked || Plugin.Net.IsConnected;
            if (netLocked)
                _waitingForInvite = false;

            bool desiredLock = _waitingForInvite || netLocked;
            if (desiredLock != MpMenuState.InputLocked)
            {
                MpMenuState.InputLocked = desiredLock;
                if (netLocked)
                    _joinOverlayReady = false;
                ApplyColors(inst);
            }

            if (Plugin.Net.ShouldForceUnlockUi(Time.realtimeSinceStartup))
            {
                Plugin.Log.LogWarning("[Menu] Input-lock watchdog fired - force unlock.");
                CancelNetworkOperation("Operation timed out.\nPlease try again.");
                return;
            }

            // Watchdog: force-unlock if a non-indefinite state has stalled > 15 s.
            // Prevents rare soft-lock if a callback is never fired.
            if (false && MpMenuState.InputLocked
                && !Plugin.Net.IsWaitingIndefinitely
                && !Plugin.Net.IsConnected
                && Time.realtimeSinceStartup - Plugin.Net.StateEnteredTime > 15f)
            {
                Plugin.Log.LogWarning("[Menu] Input-lock watchdog fired — force unlock.");
                MpMenuState.InputLocked = false;
                MpMenuState.SetStatus("Operation timed out.\nPlease try again.", animate: false);
                ApplyColors(inst);
            }

            UpdatePresenceText();

            int count = MpMenuState.MpItems.Length;
            int prev  = MpMenuState.MpSelection;

            if (!MpMenuState.InputLocked && MpMenuState.Btn(inst, CupheadButton.MenuDown))
                MpMenuState.MpSelection = (MpMenuState.MpSelection + 1) % count;
            if (!MpMenuState.InputLocked && MpMenuState.Btn(inst, CupheadButton.MenuUp))
            {
                MpMenuState.MpSelection--;
                if (MpMenuState.MpSelection < 0) MpMenuState.MpSelection = count - 1;
            }

            if (MpMenuState.MpSelection != prev)
            {
                ApplyColors(inst);
                PlayMenuSound("level_menu_move");
            }

            bool cancel = BackPressed(inst);
            bool accept = MpMenuState.Btn(inst, CupheadButton.Accept);

            if (cancel || (accept && MpMenuState.MpSelection == 2 /* BACK */))
            {
                // Debounce: ignore rapid double-presses (prevents double-shutdown)
                if (Time.time - _lastBackTime < 0.2f) return;
                _lastBackTime = Time.time;

                PlayMenuSound("level_menu_confirm");
                // State-aware back:
                //   - Any active network operation → cancel it, stay in MP menu
                //   - Idle / error → exit to main menu
                if (MpMenuState.InputLocked || Plugin.Net.IsConnected || Plugin.Net.IsInLobby)
                    CancelNetworkOperation();
                else
                    ExitMpMenu(inst);
                return;
            }

            if (accept && MpMenuState.InputLocked) return;

            if (accept)
            {
                PlayMenuSound("level_menu_confirm");
                switch (MpMenuState.MpSelection)
                {
                    case 0: OnHostGame(); break;
                    case 1:
                        // After join-timeout: second press opens the overlay.
                        // First press: show "waiting for invite" and start timeout.
                        if (!HandleJoinAccept())
                            OnJoinGame(inst);
                        break;
                }
            }
        }

        // ── Enter / exit MP ───────────────────────────────────────────────────

        static void EnterMpMenu(SlotSelectScreen inst)
        {
            if (MpMenuState.MpContainer == null || MpMenuState.MainContainer == null)
            { Plugin.Log.LogWarning("[Menu] Containers not ready."); return; }

            var sf = MpMenuState.SelectionField;
            if (sf != null) MpMenuState.SavedMainSel = (int)sf.GetValue(inst);

            HideCreditsImmediate();
            if (Plugin.Net != null) Plugin.Net.OnOverlayClosed += HandleOverlayClosed;
            MpMenuState.MainContainer.SetActive(false);
            SetCanvasVisible(MpMenuState.MpCanvasGroup, MpMenuState.MpContainer, true, 0f);
            MpMenuState.MpSelection  = 0;
            MpMenuState.InMpMenu     = true;
            MpMenuState.InputLocked  = false;
            _joinOverlayReady        = false;
            _waitingForInvite        = false;
            _lastPresence            = null;   // force fresh presence render on entry
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine);
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine);
            MpMenuState.SetStatus(
                Plugin.Net.IsSteamReady ? "Select an option." : Plugin.Net.SteamUnavailableStatus,
                animate: false);
            if (MpMenuState.PresenceText != null) MpMenuState.PresenceText.text = "";

            ApplyColors(inst);

            if (MpMenuState.ScreenInstance != null)
            {
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine,
                    FadeCanvas(MpMenuState.MpCanvasGroup, 0f, 1f, 0.2f));
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine, AnimateDots());
            }

            Plugin.Log.LogInfo("[Menu] Entered MP menu.");
        }

        static void ExitMpMenu(SlotSelectScreen inst)
        {
            MpMenuState.InMpMenu    = false;
            MpMenuState.InputLocked = false;
            _joinOverlayReady       = false;
            _waitingForInvite       = false;
            if (Plugin.Net != null) Plugin.Net.OnOverlayClosed -= HandleOverlayClosed;
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine);
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine);

            if (MpMenuState.ScreenInstance != null && MpMenuState.MpCanvasGroup != null)
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine,
                    FadeAndHide(MpMenuState.MpCanvasGroup, MpMenuState.MpContainer, 0.15f));
            else
                SetCanvasVisible(MpMenuState.MpCanvasGroup, MpMenuState.MpContainer, false);

            if (MpMenuState.MainContainer != null) MpMenuState.MainContainer.SetActive(true);

            var sf   = MpMenuState.SelectionField;
            var fi   = MpMenuState.TextItemsField;
            var txts = fi?.GetValue(inst) as Text[];
            if (sf != null && txts != null)
                sf.SetValue(inst, txts.Length - 2);   // land on MULTIPLAYER item

            LobbyScreen.Hide();
            Plugin.Net.Shutdown();
            Plugin.Log.LogInfo("[Menu] Exited MP menu.");
        }

        /// <summary>
        /// Cancels the current network operation but stays in the MP sub-menu.
        /// Called when Back is pressed while a network op is in flight.
        /// Order matters: invalidate coroutines first, then shutdown, then unlock UI.
        /// </summary>
        static void CancelNetworkOperation(string status = "Select an option.")
        {
            _joinGeneration++;        // invalidates any running JoinTimeoutRoutine
            _joinOverlayReady = false;
            _waitingForInvite = false;
            Plugin.Net.Shutdown();
            MpMenuState.InputLocked = false;
            _lastPresence           = null;   // force presence refresh after cancel
            ApplyColors(null);
            MpMenuState.SetStatus(status, animate: false);
            if (MpMenuState.PresenceText != null) MpMenuState.PresenceText.text = "";
            Plugin.Log.LogInfo("[Menu] Network operation cancelled.");
        }

        // ── Colors ────────────────────────────────────────────────────────────

        static void ApplyColors(SlotSelectScreen inst)
        {
            if (MpMenuState.MpItems == null) return;
            Color sel    = MpMenuState.SelColor(inst);
            Color unsel  = MpMenuState.UnselColor(inst);
            Color locked = new Color(unsel.r, unsel.g, unsel.b, unsel.a * 0.45f);

            for (int i = 0; i < MpMenuState.MpItems.Length; i++)
            {
                var t = MpMenuState.MpItems[i];
                if (t == null) continue;
                bool isLocked = MpMenuState.InputLocked && i < 2;
                t.color = isLocked ? locked
                        : (i == MpMenuState.MpSelection) ? sel : unsel;
            }
        }

        // ── Presence display ──────────────────────────────────────────────────

        static void UpdatePresenceText()
        {
            if (MpMenuState.PresenceText == null) return;
            string p = Plugin.Net.GetLobbyPresence();
            if (p == _lastPresence) return;   // only assign when string actually changes
            _lastPresence = p;
            MpMenuState.PresenceText.text = p;
        }

        // ── Actions ──────────────────────────────────────────────────────────

        static void OnHostGame()
        {
            if (!Plugin.Net.StartHost())
            {
                _waitingForInvite     = false;
                MpMenuState.InputLocked = false;
                ApplyColors(null);
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus, animate: false);
                return;
            }

            MpMenuState.InputLocked = true;
            ApplyColors(null);
        }

        static void OnJoinGame(SlotSelectScreen inst)
        {
            if (!Plugin.Net.IsSteamReady)
            {
                _waitingForInvite      = false;
                MpMenuState.InputLocked = false;
                ApplyColors(null);
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus, animate: false);
                return;
            }

            _joinGeneration++;   // invalidate any previous JoinTimeoutRoutine
            _waitingForInvite = true;
            MpMenuState.InputLocked = true;
            ApplyColors(null);
            MpMenuState.SetStatus(
                "Waiting for a Steam invite\u2026\n"
                + "Press Shift\u202FTab to open Steam overlay.",
                animate: true);

            if (MpMenuState.ScreenInstance != null)
                MpMenuState.ScreenInstance.StartCoroutine(JoinTimeoutRoutine(_joinGeneration));
        }

        // ── Join timeout + second-press overlay flow ──────────────────────────

        static IEnumerator JoinTimeoutRoutine(int gen)
        {
            const float TIMEOUT = 12f;
            float waited = 0f;

            while (waited < TIMEOUT && MpMenuState.InMpMenu && !Plugin.Net.IsConnected)
            {
                // Invalidated by cancel or a new join op — abort silently
                if (gen != _joinGeneration) yield break;
                if (!_waitingForInvite) yield break;

                // Invite arrived mid-wait — net callbacks handle UI
                if (Plugin.Net.IsInputLocked || Plugin.Net.IsConnected)
                {
                    _waitingForInvite = false;
                    _joinOverlayReady = false;
                    yield break;
                }
                yield return null;
                waited += Time.deltaTime;
            }

            // Bail if cancelled, exited, or already connected
            if (gen != _joinGeneration || !MpMenuState.InMpMenu || Plugin.Net.IsConnected)
                yield break;

            _waitingForInvite = false;
            MpMenuState.InputLocked = false;
            ApplyColors(null);
            MpMenuState.SetStatus(
                "No invite received yet.\n"
                + "Press Join Game again to open your Friends list.",
                animate: false);
            _joinOverlayReady = true;
        }

        static bool HandleJoinAccept()
        {
            if (!_joinOverlayReady) return false;
            if (!Plugin.Net.IsSteamReady)
            {
                _joinOverlayReady       = false;
                _waitingForInvite       = false;
                MpMenuState.InputLocked = false;
                ApplyColors(null);
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus, animate: false);
                return true;
            }

            // Spam guard: don't re-open overlay within 2 s
            if (Time.time - _lastOverlayOpenTime < 2f) return true; // swallow input, no re-open

            _joinOverlayReady     = false;
            _lastOverlayOpenTime  = Time.time;
            _waitingForInvite     = true;
            MpMenuState.InputLocked = true;
            ApplyColors(null);
            SteamFriends.ActivateGameOverlay("Friends");
            MpMenuState.SetStatus(
                "Steam overlay opened.\nWaiting for invite\u2026",
                animate: true);
            return true;
        }

        // ── Coroutines ────────────────────────────────────────────────────────

        static IEnumerator AnimateDots()
        {
            string[] suffixes = { "", ".", "..", "..." };
            int i = 0;
            while (MpMenuState.InMpMenu)
            {
                yield return new WaitForSecondsRealtime(0.35f);
                if (!MpMenuState.InMpMenu) yield break;
                if (MpMenuState.StatusText == null) yield break;

                if (MpMenuState.StatusAnimate)
                {
                    i = (i + 1) % suffixes.Length;
                    MpMenuState.StatusText.text = MpMenuState.StatusBase + suffixes[i];
                }
                else
                {
                    i = 0;
                    MpMenuState.StatusText.text = MpMenuState.StatusBase;
                }
            }
        }

        static IEnumerator FadeCanvas(CanvasGroup cg, float from, float to, float duration)
        {
            if (cg == null) yield break;
            float elapsed = 0f;
            cg.alpha = from;
            cg.interactable = to > 0f;
            cg.blocksRaycasts = to > 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                cg.alpha = Mathf.Lerp(from, to, elapsed / duration);
                yield return null;
            }
            cg.alpha = to;
            cg.interactable = to > 0f;
            cg.blocksRaycasts = to > 0f;
        }

        static IEnumerator FadeAndHide(CanvasGroup cg, GameObject go, float duration)
        {
            if (cg == null)
            {
                if (go != null) go.SetActive(false);
                yield break;
            }

            yield return FadeCanvas(cg, cg.alpha, 0f, duration);
            SetCanvasVisible(cg, go, false);
        }

        // ── Audio ─────────────────────────────────────────────────────────────

        static readonly MethodInfo _audioPlay =
            System.Type.GetType("AudioManager, Assembly-CSharp")
                  ?.GetMethod("Play",
                              BindingFlags.Public | BindingFlags.Static,
                              null, new[] { typeof(string) }, null);

        static void PlayMenuSound(string name)
        {
            try { _audioPlay?.Invoke(null, new object[] { name }); }
            catch { }
        }
    }
}
