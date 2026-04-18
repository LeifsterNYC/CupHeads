using System.Collections;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.UI;
using CupheadOnline.Sync;
using CupheadOnline.Diagnostics;

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
        internal const int HostIndex       = 0;
        internal const int JoinIndex       = 1;
        internal const int InviteIndex     = 2;
        internal const int RetryIndex      = 3;
        internal const int CopyLobbyIndex  = 4;
        internal const int DiagnosticsIndex= 5;
        internal const int BackIndex       = 6;

        internal static bool             InMpMenu;
        internal static int              MpSelection;
        internal static int              SavedMainSel;
        internal static int              MainMenuMpIndex;
        internal static int              MainMenuCreditsIndex;
        internal static bool             InputLocked;        // blocks Accept during critical states

        internal static GameObject       MainContainer;
        internal static GameObject       MpContainer;
        internal static RectTransform    MpLayoutRoot;
        internal static CanvasGroup      MpCanvasGroup;
        internal static Text[]           MpItems;
        internal static Text             SteamBadgeText;
        internal static Text             StatusText;
        internal static Text             PresenceText;
        internal static Text             HintText;
        internal static Text             BackHintText;
        internal static Text             MainFooterText;
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
        internal static readonly FieldInfo SlotSelectionField =
            typeof(SlotSelectScreen).GetField("_slotSelection", BF);
        internal static readonly FieldInfo SlotsField =
            typeof(SlotSelectScreen).GetField("slots", BF);
        internal static readonly FieldInfo SelectedColorField =
            typeof(SlotSelectScreen).GetField("mainMenuSelectedColor", BF);
        internal static readonly FieldInfo UnselectedColorField =
            typeof(SlotSelectScreen).GetField("mainMenuUnselectedColor", BF);
        internal static readonly MethodInfo SetStateMethod =
            typeof(SlotSelectScreen).GetMethod("SetState",
                BF, null, new[] { typeof(SlotSelectScreen.State) }, null);
        internal static readonly MethodInfo GetButtonDownMethod =
            typeof(SlotSelectScreen).GetMethod("GetButtonDown",
                BF, null, new[] { typeof(CupheadButton) }, null);

        internal static void Reset()
        {
            InMpMenu       = false;
            MpSelection    = 0;
            SavedMainSel   = 0;
            MainMenuMpIndex = -1;
            MainMenuCreditsIndex = -1;
            InputLocked    = false;
            MainContainer  = null;
            MpContainer    = null;
            MpLayoutRoot   = null;
            MpCanvasGroup  = null;
            MpItems        = null;
            SteamBadgeText = null;
            StatusText     = null;
            PresenceText   = null;
            HintText       = null;
            BackHintText   = null;
            MainFooterText = null;
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

            var mpLayout   = BuildLayout(mpRoot, new Vector2(0f, 54f), 12f);
            MpMenuState.MpLayoutRoot = mpLayout.GetComponent<RectTransform>();
            var hostGO     = CloneItem(exitGO, mpLayout, "HOST GAME");
            var joinGO     = CloneItem(exitGO, mpLayout, "JOIN GAME");
            var inviteGO   = CloneItem(exitGO, mpLayout, "INVITE FRIEND");
            var retryGO    = CloneItem(exitGO, mpLayout, "RETRY LAST");
            var copyLobbyGO= CloneItem(exitGO, mpLayout, "COPY LOBBY ID");
            var diagGO     = CloneItem(exitGO, mpLayout, "COPY DIAGNOSTICS");
            var backGO     = CloneItem(exitGO, mpLayout, "BACK");

            MpMenuState.MpItems = new[]
            {
                hostGO.GetComponent<Text>(),
                joinGO.GetComponent<Text>(),
                inviteGO.GetComponent<Text>(),
                retryGO.GetComponent<Text>(),
                copyLobbyGO.GetComponent<Text>(),
                diagGO.GetComponent<Text>(),
                backGO.GetComponent<Text>(),
            };

            var badgeGO = BuildText(mpRoot, "SteamBadgeText",
                new Vector2(0f, 244f), new Vector2(440f, 28f),
                sample.font, Mathf.Max(13, sample.fontSize - 5),
                new Color(0.95f, 0.85f, 0.40f, 0.95f));
            MpMenuState.SteamBadgeText = badgeGO.GetComponent<Text>();

            var statusGO = BuildText(mpRoot, "StatusText",
                new Vector2(0f, -154f), new Vector2(760f, 72f),
                sample.font, Mathf.Max(15, sample.fontSize - 4),
                new Color(0.9f, 0.85f, 0.5f, 1f));
            MpMenuState.StatusText = statusGO.GetComponent<Text>();

            var presenceGO = BuildText(mpRoot, "PresenceText",
                new Vector2(0f, -240f), new Vector2(760f, 96f),
                sample.font, Mathf.Max(13, sample.fontSize - 6),
                new Color(0.75f, 0.75f, 0.75f, 0.85f));
            MpMenuState.PresenceText = presenceGO.GetComponent<Text>();
            if (MpMenuState.PresenceText != null)
            {
                MpMenuState.PresenceText.alignment = TextAnchor.UpperCenter;
                MpMenuState.PresenceText.lineSpacing = 1.04f;
            }

            var mpHintGO = BuildText(mpRoot, "MpHintText",
                new Vector2(0f, -320f), new Vector2(760f, 48f),
                sample.font, Mathf.Max(12, sample.fontSize - 6),
                new Color(0.6f, 0.6f, 0.6f, 0.8f));
            MpMenuState.HintText = mpHintGO.GetComponent<Text>();
            if (MpMenuState.HintText != null)
                MpMenuState.HintText.text = "[ Accept to choose. ]";

            var mpBackHintGO = BuildText(mpRoot, "MpBackHintText",
                new Vector2(0f, -360f), new Vector2(760f, 30f),
                sample.font, Mathf.Max(12, sample.fontSize - 6),
                new Color(0.68f, 0.68f, 0.68f, 0.82f));
            MpMenuState.BackHintText = mpBackHintGO.GetComponent<Text>();
            if (MpMenuState.BackHintText != null)
                MpMenuState.BackHintText.text = "[ Press Escape or Controller B to go back ]";
            var footerGO = BuildText(MpMenuState.MainContainer, "MainFooterText",
                Vector2.zero, new Vector2(820f, 24f),
                sample.font, Mathf.Max(11, sample.fontSize - 8),
                new Color(0.74f, 0.74f, 0.70f, 0.88f));
            var footerRT = footerGO.GetComponent<RectTransform>();
            if (footerRT != null)
            {
                footerRT.anchorMin = footerRT.anchorMax = new Vector2(0f, 0f);
                footerRT.pivot = new Vector2(0f, 0f);
                footerRT.anchoredPosition = new Vector2(18f, 18f);
            }
            MpMenuState.MainFooterText = footerGO.GetComponent<Text>();
            if (MpMenuState.MainFooterText != null)
            {
                MpMenuState.MainFooterText.alignment = TextAnchor.MiddleLeft;
                MpMenuState.MainFooterText.text = "CupheadOnline v" + PluginInfo.VERSION;
            }

            // ── Build CreditsPanel ────────────────────────────────────────────
            var credRoot = BuildPanel("CreditsPanel", containerParent,
                                      MpMenuState.MainContainer.GetComponent<RectTransform>());
            var credCg   = credRoot.AddComponent<CanvasGroup>();
            credCg.alpha = 0f;
            credRoot.SetActive(false);
            CreditsState.CreditsContainer   = credRoot;
            CreditsState.CreditsCanvasGroup = credCg;

            // Credits are positioned explicitly instead of using a layout group.
            // Unity 2017 occasionally mangles multiline menu text when it is
            // nested under dynamic layout components, so we keep each line fixed.
            var titleGO = BuildText(credRoot, "CreditsTitle",
                new Vector2(0f, 176f), new Vector2(880f, 54f),
                sample.font, sample.fontSize + 4,
                MpMenuState.SelColor(__instance));
            var titleT  = titleGO.GetComponent<Text>();
            if (titleT != null)
            {
                titleT.text     = "CREDITS";
                titleT.fontSize = sample.fontSize + 4;
                titleT.color    = MpMenuState.SelColor(__instance);
                titleT.horizontalOverflow = HorizontalWrapMode.Overflow;
                titleT.verticalOverflow   = VerticalWrapMode.Overflow;
                titleT.resizeTextForBestFit = false;
                titleT.lineSpacing        = 1.05f;
                titleT.alignment          = TextAnchor.MiddleCenter;
            }
            BuildCreditsLine(
                credRoot,
                "CreditsLine1",
                "Multiplayer Mod",
                new Vector2(0f, 86f),
                new Vector2(900f, 44f),
                sample.font,
                sample.fontSize,
                new Color(0.92f, 0.92f, 0.92f, 1f));

            BuildCreditsLine(
                credRoot,
                "CreditsLine2",
                "Made by Germanized / Sh0kr",
                new Vector2(0f, 38f),
                new Vector2(980f, 44f),
                sample.font,
                Mathf.Max(18, sample.fontSize - 1),
                new Color(0.92f, 0.92f, 0.92f, 1f));

            BuildCreditsLine(
                credRoot,
                "CreditsLine3",
                "Made for Daniel",
                new Vector2(0f, -40f),
                new Vector2(860f, 42f),
                sample.font,
                Mathf.Max(18, sample.fontSize - 1),
                new Color(0.92f, 0.92f, 0.92f, 1f));

            BuildCreditsLine(
                credRoot,
                "CreditsLine4",
                "cuz me and him wanna play.",
                new Vector2(0f, -86f),
                new Vector2(980f, 40f),
                sample.font,
                Mathf.Max(15, sample.fontSize - 4),
                new Color(0.80f, 0.80f, 0.80f, 0.95f));

            var hintGO = BuildText(credRoot, "HintText",
                new Vector2(0f, -198f), new Vector2(700f, 30f),
                sample.font, Mathf.Max(12, sample.fontSize - 6),
                new Color(0.6f, 0.6f, 0.6f, 0.8f));
            var hintT = hintGO.GetComponent<Text>();
            if (hintT != null) hintT.text = "[ Press Escape to go back ]";

            // ── Append MULTIPLAYER + CREDITS to main-menu arrays ──────────────
            var mpLabelGO  = CloneItem(exitGO, MpMenuState.MainContainer, "MULTIPLAYER");
            PositionBelow(textItems, mpLabelGO);
            GameObject credLabelGO = null;
            if (Plugin.ShowCreditsMenu)
            {
                credLabelGO = CloneItem(exitGO, MpMenuState.MainContainer, "CREDITS");
                PositionBelow(textItems, credLabelGO, mpLabelGO);
            }

            int extraCount = Plugin.ShowCreditsMenu ? 2 : 1;
            var newTexts  = new Text[textItems.Length + extraCount];
            textItems.CopyTo(newTexts, 0);
            MpMenuState.MainMenuMpIndex = textItems.Length;
            newTexts[MpMenuState.MainMenuMpIndex] = mpLabelGO.GetComponent<Text>();
            MpMenuState.MainMenuCreditsIndex = -1;
            if (Plugin.ShowCreditsMenu && credLabelGO != null)
            {
                MpMenuState.MainMenuCreditsIndex = textItems.Length + 1;
                newTexts[MpMenuState.MainMenuCreditsIndex] = credLabelGO.GetComponent<Text>();
            }
            fi.SetValue(__instance, newTexts);

            var newEnums = new SlotSelectScreen.MainMenuItem[enumItems.Length + extraCount];
            enumItems.CopyTo(newEnums, 0);
            newEnums[MpMenuState.MainMenuMpIndex] = (SlotSelectScreen.MainMenuItem)99;
            if (MpMenuState.MainMenuCreditsIndex >= 0)
                newEnums[MpMenuState.MainMenuCreditsIndex] = (SlotSelectScreen.MainMenuItem)100;
            ei.SetValue(__instance, newEnums);

            if (Plugin.ShowCreditsMenu && credLabelGO != null)
            {
                __instance.StartCoroutine(EnforceLabels(
                    mpLabelGO.GetComponent<Text>(),   "MULTIPLAYER",
                    credLabelGO.GetComponent<Text>(), "CREDITS",
                    hostGO.GetComponent<Text>(),      "HOST GAME",
                    inviteGO.GetComponent<Text>(),    "INVITE FRIEND",
                    diagGO.GetComponent<Text>(),      "COPY DIAGNOSTICS",
                    backGO.GetComponent<Text>(),      "BACK",
                    titleT,                           "CREDITS"));
            }
            else
            {
                __instance.StartCoroutine(EnforceLabels(
                    mpLabelGO.GetComponent<Text>(),   "MULTIPLAYER",
                    hostGO.GetComponent<Text>(),      "HOST GAME",
                    inviteGO.GetComponent<Text>(),    "INVITE FRIEND",
                    diagGO.GetComponent<Text>(),      "COPY DIAGNOSTICS",
                    backGO.GetComponent<Text>(),      "BACK",
                    titleT,                           "CREDITS"));
            }

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

        static GameObject BuildLayout(GameObject parent, Vector2 offset, float spacing = 24f)
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
            vlg.spacing               = spacing;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight= false;
            vlg.childControlWidth     = false;
            vlg.childControlHeight    = true;
            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            return go;
        }

        static GameObject CloneItem(GameObject source, GameObject newParent, string label)
        {
            var go = UnityEngine.Object.Instantiate(source, newParent.transform, false);
            go.name = label;
            foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
            {
                if (b == null || b is Text || b is LayoutElement) continue;
                b.enabled = false;
            }
            var t = go.GetComponent<Text>();
            if (t != null)
            {
                t.text = label;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.resizeTextForBestFit = false;
                t.alignment = TextAnchor.MiddleCenter;
            }
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.enabled = true;
            le.ignoreLayout = false;
            // Use a fixed height — Cuphead text items have sizeDelta.y = 0 (auto-sized),
            // which would make VLG stack everything at the same position.
            le.preferredHeight = 40f;
            le.minHeight       = 40f;
            le.flexibleHeight  = 0f;
            le.preferredWidth  = 420f;
            le.minWidth        = 320f;

            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(le.preferredWidth, le.preferredHeight);
                rt.anchoredPosition = Vector2.zero;
            }
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

        static Text BuildCreditsLine(GameObject parent, string name, string text,
                                     Vector2 pos, Vector2 size,
                                     Font font, int fontSize, Color color)
        {
            var go = BuildText(parent, name, pos, size, font, fontSize, color);
            var txt = go.GetComponent<Text>();
            if (txt != null)
            {
                txt.text                 = text;
                txt.resizeTextForBestFit = false;
                txt.supportRichText      = false;
                txt.horizontalOverflow   = HorizontalWrapMode.Overflow;
                txt.verticalOverflow     = VerticalWrapMode.Truncate;
                txt.lineSpacing          = 1f;
                txt.alignByGeometry      = true;
            }

            return txt;
        }

        static void BuildSpacer(GameObject parent, float height)
        {
            var go = new GameObject("Spacer");
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.preferredWidth  = 1f;
        }

        internal static void RefreshMpLayout()
        {
            Canvas.ForceUpdateCanvases();

            if (MpMenuState.MpLayoutRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(MpMenuState.MpLayoutRoot);

            if (MpMenuState.MpContainer != null)
            {
                var rt = MpMenuState.MpContainer.GetComponent<RectTransform>();
                if (rt != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            }
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

            // Prevent the same Accept press used to leave a sub-menu from
            // immediately re-opening it on the restored main-menu selection.
            if (Time.time - _lastBackTime < 0.2f) return;

            if (MpMenuState.TimeSinceStartField != null)
            {
                float t = (float)MpMenuState.TimeSinceStartField.GetValue(__instance);
                if (t < 0.75f) return;
            }

            if (LobbyScreen.Instance != null) return;
            UpdateMainFooterText();

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

        static void UpdateMainFooterText()
        {
            if (MpMenuState.MainFooterText == null) return;

            string footer = "CupheadOnline v" + PluginInfo.VERSION;
            if (Plugin.Net == null)
            {
                MpMenuState.MainFooterText.text = footer;
                return;
            }

            if (Plugin.Net.IsConnected)
            {
                string sessionHint = SessionSync.GetFooterHint();
                if (!string.IsNullOrEmpty(sessionHint))
                    footer += "  |  " + sessionHint;
            }
            else if (Plugin.Net.IsInLobby)
            {
                string sessionHint = SessionSync.GetFooterHint();
                if (!string.IsNullOrEmpty(sessionHint))
                    footer += "  |  " + sessionHint;
            }
            else if (!Plugin.Net.IsSteamReady)
            {
                footer += "  |  Steam unavailable outside Steam";
            }

            if (MpMenuState.MainFooterText.text != footer)
                MpMenuState.MainFooterText.text = footer;
        }

        // ── Credits ──────────────────────────────────────────────────────────

        static void RunCredits(SlotSelectScreen inst)
        {
            if (!BackPressed(inst)) return;
            PlayMenuSound("level_menu_confirm");
            ExitCredits(inst);
        }

        static void EnterCredits(SlotSelectScreen inst)
        {
            if (!Plugin.ShowCreditsMenu) return;
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
            if (sf != null)
                sf.SetValue(inst, MpMenuState.MainMenuCreditsIndex >= 0
                    ? MpMenuState.MainMenuCreditsIndex
                    : MpMenuState.SavedMainSel);

            Plugin.Log.LogInfo("[Menu] Exited Credits.");
        }

        static bool TryGetClipboardLobbyId(out string rawText, out string lobbyId)
        {
            rawText = GUIUtility.systemCopyBuffer;
            lobbyId = string.Empty;
            if (string.IsNullOrEmpty(rawText) || rawText.Trim().Length == 0)
                return false;

            string trimmed = rawText.Trim();
            ulong numericId;
            if (ulong.TryParse(trimmed, out numericId) && numericId != 0UL)
            {
                rawText = trimmed;
                lobbyId = numericId.ToString();
                return true;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"Lobby ID:\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            rawText = trimmed;
            lobbyId = match.Groups[1].Value;
            return !string.IsNullOrEmpty(lobbyId);
        }

        static void SetMpItemLabel(int index, string label)
        {
            if (MpMenuState.MpItems == null || index < 0 || index >= MpMenuState.MpItems.Length)
                return;

            var item = MpMenuState.MpItems[index];
            if (item != null && item.text != label)
                item.text = label;
        }

        static void UpdateDynamicMenuLabels()
        {
            string clipboardRaw;
            string clipboardLobbyId;

            SetMpItemLabel(
                MpMenuState.HostIndex,
                Plugin.Net.IsConnected
                    ? (Plugin.Net.IsHost ? "OPEN SAVE SLOT" : "WAIT FOR HOST")
                    : "HOST GAME");
            SetMpItemLabel(
                MpMenuState.JoinIndex,
                Plugin.Net.IsConnected
                    ? (Plugin.Net.IsHost
                        ? (SessionSync.HasTrackedSave
                            ? (SessionSync.IsRemoteReady ? "GUEST READY" : "WAIT READY")
                            : "WAIT FOR SAVE")
                        : (SessionSync.CanGuestToggleReady
                            ? (SessionSync.IsLocalReady ? "UNREADY" : "READY UP")
                            : "WAIT FOR SAVE"))
                    : _joinOverlayReady
                        ? "OPEN FRIENDS"
                        : TryGetClipboardLobbyId(out clipboardRaw, out clipboardLobbyId)
                            ? "JOIN CLIPBOARD"
                            : "JOIN GAME");
            SetMpItemLabel(
                MpMenuState.InviteIndex,
                Plugin.Net.IsConnected
                    ? (Plugin.Net.IsHost ? "SEND RESYNC" : "REQUEST RESYNC")
                    : "INVITE FRIEND");
            SetMpItemLabel(MpMenuState.RetryIndex, Plugin.Net.GetRetryActionLabel());
            SetMpItemLabel(MpMenuState.CopyLobbyIndex, "COPY LOBBY ID");
            SetMpItemLabel(MpMenuState.DiagnosticsIndex, "EXPORT BUG REPORT");
            SetMpItemLabel(MpMenuState.BackIndex,
                Plugin.Net.IsConnected || Plugin.Net.IsInLobby ? "DISCONNECT" : "BACK");
        }

        static void UpdateSteamBadge()
        {
            if (MpMenuState.SteamBadgeText == null) return;

            string badge = Plugin.Net.GetSteamBadgeText();
            MpMenuState.SteamBadgeText.text = "[ " + badge + " ]";

            if (badge.IndexOf("OFFLINE", System.StringComparison.OrdinalIgnoreCase) >= 0
             || badge.IndexOf("NOT VIA STEAM", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MpMenuState.SteamBadgeText.color = new Color(0.92f, 0.42f, 0.35f, 0.95f);
            }
            else if (badge.IndexOf("OVERLAY", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MpMenuState.SteamBadgeText.color = new Color(0.95f, 0.72f, 0.30f, 0.95f);
            }
            else if (badge.IndexOf("CONNECTED", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MpMenuState.SteamBadgeText.color = new Color(0.50f, 0.90f, 0.60f, 0.95f);
            }
            else if (badge.IndexOf("IN LOBBY", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MpMenuState.SteamBadgeText.color = new Color(0.55f, 0.82f, 0.95f, 0.95f);
            }
            else
            {
                MpMenuState.SteamBadgeText.color = new Color(0.95f, 0.85f, 0.40f, 0.95f);
            }
        }

        static string GetSelectionHint()
        {
            string clipboardRaw;
            string clipboardLobbyId;

            switch (MpMenuState.MpSelection)
            {
                case MpMenuState.HostIndex:
                    if (Plugin.Net.IsConnected)
                    {
                        if (SessionSync.CompatibilitySeverity >= SessionIssueSeverity.Warning)
                            return SessionSync.CompatibilitySummary;

                        return Plugin.Net.IsHost
                            ? "Open Cuphead's normal save slots and choose the file you want to play together."
                            : "Connected. Wait for the host to choose a save slot.";
                    }
                    if (Plugin.Net.IsConnected || Plugin.Net.IsInLobby)
                        return "Leave the current session before starting a fresh host lobby.";
                    return "Create a friends-only Steam lobby for one guest.";

                case MpMenuState.JoinIndex:
                    if (Plugin.Net.IsConnected)
                    {
                        if (Plugin.Net.IsHost)
                            return SessionSync.HasTrackedSave
                                ? (SessionSync.IsRemoteReady
                                    ? "Guest is ready. Start the run whenever you want."
                                    : "Guest still needs to ready up for the selected save.")
                                : "Pick a save first so the guest can review it.";

                        if (SessionSync.DesyncSeverity >= SessionIssueSeverity.Warning)
                            return SessionSync.DesyncSummary;
                        if (SessionSync.CompatibilitySeverity >= SessionIssueSeverity.Error)
                            return SessionSync.CompatibilitySummary;

                        if (!SessionSync.HasTrackedSave)
                            return "Stay here while the host picks a save.";

                        return SessionSync.IsLocalReady
                            ? "You are ready. Wait for the host to start the run."
                            : "Confirm you are ready for the selected save and loadout.";
                    }
                    if (_joinOverlayReady)
                        return "Open Steam Friends and wait for the host invite.";
                    if (TryGetClipboardLobbyId(out clipboardRaw, out clipboardLobbyId))
                        return "Join lobby #" + clipboardLobbyId + " straight from the clipboard.";
                    if (Plugin.AutoOpenSteamFriends)
                        return "Wait for a Steam invite. The Friends overlay opens automatically.";
                    return "Wait for a Steam invite, or copy a lobby ID to the clipboard to join directly.";

                case MpMenuState.InviteIndex:
                    if (Plugin.Net.IsConnected)
                        return Plugin.Net.IsHost
                            ? "Send a fresh sync bundle and boss-priority burst to the guest."
                            : "Ask the host to resend the current session state.";
                    return Plugin.Net.CanInviteFriend
                        ? "Open Steam's invite dialog for the current lobby."
                        : "Available once you host a lobby.";

                case MpMenuState.RetryIndex:
                    return Plugin.Net.CanRetryLastAction
                        ? "Retry the last host or join action without leaving the menu."
                        : "Becomes available after a host or join attempt.";

                case MpMenuState.CopyLobbyIndex:
                    return Plugin.Net.CanCopyLobbyId
                        ? "Copy the current Steam lobby ID so someone can join from the clipboard."
                        : "Available once a lobby exists.";

                case MpMenuState.DiagnosticsIndex:
                    return "Export a bug report folder with diagnostics, logs, and config files.";

                default:
                    return Plugin.Net.IsConnected || Plugin.Net.IsInLobby
                        ? "Disconnect the current Steam session and return to the main menu."
                        : "Return to the main menu.";
            }
        }

        static void UpdateHintText()
        {
            if (MpMenuState.HintText == null) return;
            MpMenuState.HintText.text = "[ " + GetSelectionHint() + " ]";
        }

        static bool IsItemAvailable(int index)
        {
            switch (index)
            {
                case MpMenuState.HostIndex:
                    if (Plugin.Net.IsConnected)
                        return Plugin.Net.IsHost;
                    return !_waitingForInvite
                        && !Plugin.Net.IsInputLocked
                        && !Plugin.Net.IsConnected
                        && !Plugin.Net.IsInLobby;

                case MpMenuState.JoinIndex:
                    if (Plugin.Net.IsConnected)
                        return !Plugin.Net.IsHost && SessionSync.CanGuestToggleReady;
                    return !_waitingForInvite
                        && !Plugin.Net.IsInputLocked
                        && !Plugin.Net.IsConnected
                        && !Plugin.Net.IsInLobby;

                case MpMenuState.InviteIndex:
                    return Plugin.Net.IsConnected ? Plugin.Net.CanRequestRecovery : Plugin.Net.CanInviteFriend;

                case MpMenuState.RetryIndex:
                    return !_waitingForInvite
                        && !Plugin.Net.IsInputLocked
                        && !Plugin.Net.IsConnected
                        && Plugin.Net.CanRetryLastAction;

                case MpMenuState.CopyLobbyIndex:
                    return Plugin.Net.CanCopyLobbyId;

                case MpMenuState.DiagnosticsIndex:
                case MpMenuState.BackIndex:
                    return true;

                default:
                    return false;
            }
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

            UpdateDynamicMenuLabels();
            UpdateSteamBadge();
            UpdatePresenceText();
            UpdateHintText();

            int count = MpMenuState.MpItems.Length;
            int prev  = MpMenuState.MpSelection;

            if (MpMenuState.Btn(inst, CupheadButton.MenuDown))
                MpMenuState.MpSelection = (MpMenuState.MpSelection + 1) % count;
            if (MpMenuState.Btn(inst, CupheadButton.MenuUp))
            {
                MpMenuState.MpSelection--;
                if (MpMenuState.MpSelection < 0) MpMenuState.MpSelection = count - 1;
            }

            if (MpMenuState.MpSelection != prev)
            {
                ApplyColors(inst);
                UpdateHintText();
                PlayMenuSound("level_menu_move");
            }

            bool cancel = BackPressed(inst);
            bool accept = MpMenuState.Btn(inst, CupheadButton.Accept);

            if (cancel || (accept && MpMenuState.MpSelection == MpMenuState.BackIndex))
            {
                // Debounce: ignore rapid double-presses (prevents double-shutdown)
                if (Time.time - _lastBackTime < 0.2f) return;
                _lastBackTime = Time.time;

                PlayMenuSound("level_menu_confirm");
                ExitMpMenu(inst);
                return;
            }

            if (accept)
            {
                PlayMenuSound("level_menu_confirm");
                switch (MpMenuState.MpSelection)
                {
                    case MpMenuState.HostIndex:
                        if (IsItemAvailable(MpMenuState.HostIndex))
                        {
                            if (Plugin.Net.IsConnected && Plugin.Net.IsHost)
                                OpenHostSaveSelect(inst);
                            else
                                OnHostGame();
                        }
                        else
                            MpMenuState.SetStatus(
                                Plugin.Net.IsConnected
                                    ? (Plugin.Net.IsHost
                                        ? "Open the save slots when you are ready."
                                        : "Waiting for the host to choose a save slot.")
                                    : Plugin.Net.IsInLobby
                                        ? "Leave the current session before hosting again."
                                    : "Steam is still busy. Please wait.",
                                animate: false);
                        break;

                    case MpMenuState.JoinIndex:
                        if (IsItemAvailable(MpMenuState.JoinIndex))
                        {
                            if (Plugin.Net.IsConnected)
                            {
                                MpMenuState.SetStatus(SessionSync.ToggleGuestReady(), animate: false);
                            }
                            else if (!HandleJoinAccept())
                            {
                                OnJoinGame(inst);
                            }
                        }
                        else
                        {
                            MpMenuState.SetStatus(
                                Plugin.Net.IsConnected
                                    ? (Plugin.Net.IsHost
                                        ? (SessionSync.HasTrackedSave
                                            ? (SessionSync.IsRemoteReady
                                                ? "Guest is already ready."
                                                : "Guest still needs to ready up.")
                                            : "Pick a save first.")
                                        : SessionSync.CompatibilitySummary)
                                : _waitingForInvite
                                    ? "Waiting for a Steam invite..."
                                    : Plugin.Net.IsInLobby || Plugin.Net.IsConnected
                                        ? "Leave the current session before joining another lobby."
                                        : "Steam is still busy. Please wait.",
                                animate: false);
                        }
                        break;

                    case MpMenuState.InviteIndex:
                        if (Plugin.Net.IsConnected)
                        {
                            string status;
                            Plugin.Net.TryRequestRecovery(out status);
                            MpMenuState.SetStatus(status, animate: false);
                        }
                        else
                        {
                            OnInviteFriend();
                        }
                        break;

                    case MpMenuState.RetryIndex:
                        OnRetryLast();
                        break;

                    case MpMenuState.CopyLobbyIndex:
                        OnCopyLobbyId();
                        break;

                    case MpMenuState.DiagnosticsIndex:
                        OnExportBugReport();
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
                Plugin.Net.IsConnected
                    ? (Plugin.Net.IsHost
                        ? "Guest connected.\nSelect OPEN SAVE SLOT to choose a file."
                        : "Connected.\nWaiting for the host to choose a save slot.")
                    : Plugin.Net.IsSteamReady ? "Select an option." : Plugin.Net.SteamUnavailableStatus,
                animate: false);
            if (MpMenuState.PresenceText != null) MpMenuState.PresenceText.text = "";
            UpdateDynamicMenuLabels();
            UpdateSteamBadge();
            UpdateHintText();
            SlotSelectAwakePatch.RefreshMpLayout();

            ApplyColors(inst);

            if (MpMenuState.ScreenInstance != null)
            {
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine,
                    FadeCanvas(MpMenuState.MpCanvasGroup, 0f, 1f, 0.2f));
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine, AnimateDots());
            }

            Plugin.Log.LogInfo("[Menu] Entered MP menu.");
        }

        static void ExitMpMenu(
            SlotSelectScreen inst,
            bool preserveConnection = false,
            bool restoreMainMenuSelection = true,
            bool showMainMenu = true)
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

            if (MpMenuState.MainContainer != null)
                MpMenuState.MainContainer.SetActive(showMainMenu);

            var sf = MpMenuState.SelectionField;
            if (restoreMainMenuSelection && sf != null)
                sf.SetValue(inst, MpMenuState.MainMenuMpIndex >= 0
                    ? MpMenuState.MainMenuMpIndex
                    : MpMenuState.SavedMainSel);

            LobbyScreen.Hide();
            if (!preserveConnection)
                Plugin.Net.Shutdown();
            Plugin.Log.LogInfo("[Menu] Exited MP menu.");
        }

        static void OpenHostSaveSelect(SlotSelectScreen inst)
        {
            if (!Plugin.Net.CanOpenSaveSlot)
            {
                MpMenuState.SetStatus("Connect a guest before opening the save slots.", animate: false);
                return;
            }

            ExitMpMenu(inst, preserveConnection: true, restoreMainMenuSelection: false, showMainMenu: false);

            try
            {
                int slotSelection = Mathf.Clamp(PlayerData.CurrentSaveFileIndex, 0, 2);
                if (MpMenuState.SlotSelectionField != null)
                    MpMenuState.SlotSelectionField.SetValue(inst, slotSelection);

                if (MpMenuState.SetStateMethod != null)
                    MpMenuState.SetStateMethod.Invoke(inst, new object[] { SlotSelectScreen.State.SlotSelect });

                var slots = MpMenuState.SlotsField != null
                    ? MpMenuState.SlotsField.GetValue(inst) as SlotSelectScreenSlot[]
                    : null;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Length; i++)
                    {
                        if (slots[i] != null)
                            slots[i].Init(i);
                    }
                }

                Plugin.Log.LogInfo("[Menu] Host opened save slot select.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Menu] Failed to open save slot select: " + ex.Message);
                if (MpMenuState.MainContainer != null)
                    MpMenuState.MainContainer.SetActive(true);
                MpMenuState.SetStatus("Could not open the save slots.", animate: false);
            }
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
            UpdateDynamicMenuLabels();
            UpdateSteamBadge();
            UpdateHintText();
            Plugin.Log.LogInfo("[Menu] Network operation cancelled.");
        }

        // ── Colors ────────────────────────────────────────────────────────────

        static void ApplyColors(SlotSelectScreen inst)
        {
            if (MpMenuState.MpItems == null) return;
            Color sel    = MpMenuState.SelColor(inst);
            Color unsel  = MpMenuState.UnselColor(inst);
            Color locked = new Color(unsel.r, unsel.g, unsel.b, unsel.a * 0.45f);
            Color dimSel = new Color(sel.r, sel.g, sel.b, sel.a * 0.6f);

            for (int i = 0; i < MpMenuState.MpItems.Length; i++)
            {
                var t = MpMenuState.MpItems[i];
                if (t == null) continue;
                bool available = IsItemAvailable(i);
                t.color = !available
                    ? (i == MpMenuState.MpSelection ? dimSel : locked)
                    : (i == MpMenuState.MpSelection ? sel : unsel);
            }
        }

        // ── Presence display ──────────────────────────────────────────────────

        static void UpdatePresenceText()
        {
            if (MpMenuState.PresenceText == null) return;
            string p = Plugin.Net.GetLobbyPresence();
            if (string.IsNullOrEmpty(p))
            {
                string peerName = Plugin.Net.CurrentPeerName;
                if (!string.IsNullOrEmpty(peerName) && peerName != "Unknown Player")
                    p = "Peer: " + peerName + "\nState: " + Plugin.Net.CurrentStateName;
            }

            string sessionSummary = SessionSync.GetMenuPresenceSummary();
            if (!string.IsNullOrEmpty(sessionSummary))
                p = string.IsNullOrEmpty(p) ? sessionSummary : p + "\n" + sessionSummary;

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
            string clipboardRaw;
            string clipboardLobbyId;
            if (!Plugin.Net.IsSteamReady)
            {
                _waitingForInvite      = false;
                MpMenuState.InputLocked = false;
                ApplyColors(null);
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus, animate: false);
                return;
            }

            if (TryGetClipboardLobbyId(out clipboardRaw, out clipboardLobbyId))
            {
                string status;
                if (Plugin.Net.TryJoinLobbyById(clipboardRaw, out status))
                {
                    _waitingForInvite = false;
                    _joinOverlayReady = false;
                    MpMenuState.InputLocked = true;
                    ApplyColors(null);
                    MpMenuState.SetStatus(status, animate: false);
                }
                else
                {
                    MpMenuState.InputLocked = false;
                    ApplyColors(null);
                    MpMenuState.SetStatus(status, animate: false);
                }
                return;
            }

            _joinGeneration++;   // invalidate any previous JoinTimeoutRoutine
            _waitingForInvite = true;
            MpMenuState.InputLocked = true;
            ApplyColors(null);

            if (Plugin.AutoOpenSteamFriends)
            {
                string overlayStatus;
                if (Plugin.Net.OpenFriendsOverlay(out overlayStatus))
                {
                    _lastOverlayOpenTime = Time.time;
                    MpMenuState.SetStatus(overlayStatus, animate: true);
                }
                else
                {
                    _waitingForInvite = false;
                    MpMenuState.InputLocked = false;
                    ApplyColors(null);
                    MpMenuState.SetStatus(overlayStatus, animate: false);
                    return;
                }
            }
            else
            {
                MpMenuState.SetStatus(
                    "Waiting for a Steam invite...\n"
                    + "Press Shift+Tab to open Steam overlay.",
                    animate: true);
            }

            if (MpMenuState.ScreenInstance != null)
                MpMenuState.ScreenInstance.StartCoroutine(JoinTimeoutRoutine(_joinGeneration));
        }

        static void OnInviteFriend()
        {
            string status;
            Plugin.Net.OpenInviteDialog(out status);
            MpMenuState.SetStatus(status, animate: false);
        }

        static void OnRetryLast()
        {
            string status;
            if (Plugin.Net.TryRetryLastAction(out status))
            {
                _waitingForInvite = false;
                _joinOverlayReady = false;
                MpMenuState.InputLocked = true;
                ApplyColors(null);
                MpMenuState.SetStatus(status, animate: false);
            }
            else
            {
                MpMenuState.SetStatus(status, animate: false);
            }
        }

        static void OnCopyLobbyId()
        {
            string status;
            if (!Plugin.Net.TryCopyLobbyId(out status))
            {
                MpMenuState.SetStatus(status, animate: false);
                return;
            }

            MpMenuState.SetStatus(status, animate: false);
        }

        static void OnExportBugReport()
        {
            string folder = BugReportExporter.Export();
            GUIUtility.systemCopyBuffer = folder;
            MpMenuState.SetStatus("Bug report exported to:\n" + folder, animate: false);
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
            string status;
            if (Plugin.Net.OpenFriendsOverlay(out status))
                MpMenuState.SetStatus(status, animate: true);
            else
            {
                _waitingForInvite = false;
                MpMenuState.InputLocked = false;
                ApplyColors(null);
                MpMenuState.SetStatus(status, animate: false);
            }
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
