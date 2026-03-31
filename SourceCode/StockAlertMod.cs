using MelonLoader;
using HarmonyLib;
using ModSettingsMenu;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;
using Il2CppInterop.Runtime;

[assembly: MelonInfo(typeof(StockAlertMod.StockAlertMod), "Stock Alert Mod", "1.0.0", "Author")]
[assembly: MelonGame("IncontinentCell", "My Dystopian Robot Girlfriend")]

namespace StockAlertMod
{
    // Game state guard
    public static class GameStateTracker
    {
        private static string currentStateName = "Unknown";
        private static bool isSleeping = false;

        // Suppress alerts for 10s after returning to RoomState (sleep, PC, outside, etc.)
        private static float _transitionCooldownUntil = 0f;

        public static bool IsSleeping => isSleeping;

        public static void SetSleeping(bool sleeping)
        {
            isSleeping = sleeping;
        }

        public static void PollGameState()
        {
            try
            {
                var gs = Il2Cpp.GameScript.Instance?.GameState;
                string name;
                if (gs == null)
                    name = "Unknown";
                else if (gs.TryCast<Il2Cpp.GameScript.RoomState>() != null)
                    name = "RoomState";
                else
                    name = "Other";

                if (name != currentStateName)
                {
                    if (name == "RoomState" && currentStateName == "Other")
                        _transitionCooldownUntil = UnityEngine.Time.realtimeSinceStartup + 10f;

                    currentStateName = name;
                }
            }
            catch { }
        }

        public static bool IsSafeToTrigger()
        {
            if (isSleeping) return false;
            if (UnityEngine.Time.realtimeSinceStartup < _transitionCooldownUntil) return false;
            return currentStateName == "RoomState" || currentStateName == "Unknown";
        }
    }

    public class StockAlertMod : MelonMod
    {
        internal static float ALERT_COOLDOWN = 45f;

        // ---- Preferences MSM ----
        private static MelonPreferences_Category prefCategory;

        // Gain thresholds
        private static MelonPreferences_Entry<bool> prefGain10;
        private static MelonPreferences_Entry<bool> prefGain25;
        private static MelonPreferences_Entry<bool> prefGain50;
        private static MelonPreferences_Entry<bool> prefGain75;
        private static MelonPreferences_Entry<bool> prefGain100;

        // Loss thresholds
        private static MelonPreferences_Entry<bool> prefLoss10;
        private static MelonPreferences_Entry<bool> prefLoss20;
        private static MelonPreferences_Entry<bool> prefLoss50;

        // General
        private static MelonPreferences_Entry<bool> prefEnabled;
        private static MelonPreferences_Entry<int>  prefAlertCooldown;
        private static MelonPreferences_Entry<int>  prefQueueCap;

        // Feature toggles
        private static MelonPreferences_Entry<bool> prefLossAlertsEnabled;
        private static MelonPreferences_Entry<bool> prefAthAlertsEnabled;
        private static MelonPreferences_Entry<bool> prefAtlAlertsEnabled;
        private static MelonPreferences_Entry<bool> prefPlainAlerts;
        private static MelonPreferences_Entry<bool> prefFacialExpressions;

        internal static float[] Thresholds = new float[] { 0.10f, 0.25f, 0.50f, 1.00f };
        internal static float[] LossThresholds = new float[] { 0.10f, 0.20f, 0.50f };

        // Queue
        private static readonly Queue<(string text, string expression)> alertQueue = new Queue<(string, string)>();
        private static readonly object alertLock = new object();

        private static void EnqueueAlert(string text, string expression = null)
        {
            if (GameStateTracker.IsSleeping) return;
            lock (alertLock)
            {
                int cap = prefQueueCap?.Value ?? 3;
                if (alertQueue.Count < cap)
                    alertQueue.Enqueue((text, expression));
            }
        }

        private static bool TryDequeueAlert(out string text, out string expression)
        {
            lock (alertLock)
            {
                if (alertQueue.Count > 0)
                {
                    var item = alertQueue.Dequeue();
                    text = item.text; expression = item.expression;
                    return true;
                }
                text = null; expression = null;
                return false;
            }
        }

        private static readonly ConcurrentDictionary<string, float> stockCooldowns = new ConcurrentDictionary<string, float>();
        private static readonly ConcurrentDictionary<string, int> alertLevel = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, int> lossLevel = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, float> allTimeHigh = new ConcurrentDictionary<string, float>();
        private static readonly ConcurrentDictionary<string, float> allTimeLow = new ConcurrentDictionary<string, float>();

        private Il2Cpp.ModelBrain brain;

        // Website display
        private static Il2Cpp.StockWebsite _stockWebsite;
        private static Il2Cpp.StockManager _stockManager;
        private float _websiteCheckTimer = 0f;
        private static bool _layoutApplied = false;
        private static GameObject _athAtlObject = null;
        private static GameObject _forecastObject = null;

        // Banners
        private static readonly Dictionary<string, Texture2D> _bannerTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite> _bannerSprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static bool _bannersLoaded = false;
        private static GameObject _bannerContainer = null;

        // Trigger after inventory
        private static bool _pendingInstallConfirm = false;

        // Install dialogue
        private static readonly string[] _installLines = {
            "New module detected... <color=#FFD700>Stock Alert Module</color> found!  Checking compatibility... <color=#008000>COMPATIBLE</color> Checking firmware...",
            "Installing new module... <color=#FFD700>Stock Alert Module</color> V1.0 Installing... Installation <color=#008000>Complete</color>.",
            "Unpacking Stock.ic I/O module... Installing <color=#FFD700>Completed</color> <color=#008000>Integration complete!</color> found!",
            "I'll now monitor the stock market for you. :)"
        };

        // Dialogue JSON
        private static Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>> _dialogueLines;
        private static readonly System.Random _rng = new System.Random();

        private static void LoadDialogueLines()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "StockAlertAssets", "StockAlertLines.json");
                if (!File.Exists(path))
                {
                    MelonLogger.Warning($"[StockAlert] StockAlertLines.json not found at {path} — using fallback lines.");
                    return;
                }
                string json = File.ReadAllText(path);
                _dialogueLines = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>>>(json);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[StockAlert] Failed to load StockAlertLines.json: {ex.Message} — using fallback lines.");
            }
        }

        private static string GetLine(string type, int sympathy, float mood, string stockName, int gainPct, int lossPct)
        {
            if (prefPlainAlerts?.Value == true)
            {
                return type == "gain" ? $"{stockName} +{gainPct}% above average."
                     : type == "loss" ? $"{stockName} -{lossPct}% below average."
                     : type == "ath"  ? $"{stockName} new all-time high (+{gainPct}% above average)."
                     :                  $"{stockName} new all-time low (-{lossPct}% below average).";
            }

            string tier = sympathy >= 1000 ? "madlyinlove"
                        : sympathy >= 700  ? "infatuated"
                        : sympathy >= 500  ? "high"
                        : sympathy >= 100  ? "friendly"
                        : sympathy >= 0    ? "neutral"
                        : sympathy >= -49  ? "ignore"
                        : sympathy >= -99  ? "no_respond"
                        :                    "hate";
            string moodKey = mood >= 0.6f ? "good" : mood < 0.3f ? "bad" : "base";
            try
            {
                if (_dialogueLines != null && _dialogueLines.TryGetValue(type, out var tiers)
                    && tiers.TryGetValue(tier, out var moods))
                {
                    if (!moods.TryGetValue(moodKey, out var lines) || lines == null || lines.Count == 0)
                        moods.TryGetValue("base", out lines);

                    if (lines != null && lines.Count > 0)
                    {
                        string line = lines[_rng.Next(lines.Count)];
                        return line.Replace("{name}", stockName).Replace("{gainPct}", gainPct.ToString()).Replace("{lossPct}", lossPct.ToString());
                    }
                }
            }
            catch { }

            return type == "gain" ? $"{stockName} is up {gainPct}% from your average."
                 : type == "loss" ? $"{stockName} is down {lossPct}% from your average."
                 : type == "ath"  ? $"{stockName} just hit a new all-time high."
                 :                  $"{stockName} just hit a new all-time low.";
        }

        public override void OnInitializeMelon()
        {
            prefCategory = MelonPreferences.CreateCategory("StockAlertMod");

            prefGain10  = prefCategory.CreateEntry("GainThreshold_10",  true);
            prefGain25  = prefCategory.CreateEntry("GainThreshold_25",  true);
            prefGain50  = prefCategory.CreateEntry("GainThreshold_50",  true);
            prefGain75  = prefCategory.CreateEntry("GainThreshold_75",  false);
            prefGain100 = prefCategory.CreateEntry("GainThreshold_100", true);

            prefLoss10 = prefCategory.CreateEntry("LossThreshold_10", true);
            prefLoss20 = prefCategory.CreateEntry("LossThreshold_20", true);
            prefLoss50 = prefCategory.CreateEntry("LossThreshold_50", false);

            prefEnabled        = prefCategory.CreateEntry("Enabled",         true);
            prefAlertCooldown  = prefCategory.CreateEntry("AlertCooldown",   45);
            prefQueueCap       = prefCategory.CreateEntry("QueueCap",        3);

            prefLossAlertsEnabled = prefCategory.CreateEntry("LossAlertsEnabled", true);
            prefAthAlertsEnabled  = prefCategory.CreateEntry("AthAlertsEnabled",  true);
            prefAtlAlertsEnabled  = prefCategory.CreateEntry("AtlAlertsEnabled",  true);
            prefPlainAlerts        = prefCategory.CreateEntry("PlainAlerts",        false);
            prefFacialExpressions  = prefCategory.CreateEntry("FacialExpressions",  true);

            RebuildThresholds();
            RebuildLossThresholds();
            ALERT_COOLDOWN = prefAlertCooldown.Value;

            LoadDialogueLines();
            InitMSM();

            HarmonyInstance.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());

            MelonLogger.Msg("[StockAlert] Loaded.");
        }

        private static void RebuildThresholds()
        {
            var list = new List<float>();
            if (prefGain10.Value)  list.Add(0.10f);
            if (prefGain25.Value)  list.Add(0.25f);
            if (prefGain50.Value)  list.Add(0.50f);
            if (prefGain75.Value)  list.Add(0.75f);
            if (prefGain100.Value) list.Add(1.00f);
            Thresholds = list.Count > 0 ? list.ToArray() : new float[] { 0.10f };
            MelonLogger.Msg($"[StockAlert] Gain thresholds: {string.Join(", ", Thresholds.Select(t => $"+{t * 100:F0}%"))}");
        }

        private static void RebuildLossThresholds()
        {
            var list = new List<float>();
            if (prefLoss10.Value) list.Add(0.10f);
            if (prefLoss20.Value) list.Add(0.20f);
            if (prefLoss50.Value) list.Add(0.50f);
            LossThresholds = list.Count > 0 ? list.ToArray() : new float[] { 0.10f };
            MelonLogger.Msg($"[StockAlert] Loss thresholds: {string.Join(", ", LossThresholds.Select(t => $"-{t * 100:F0}%"))}");
        }

        private static void InitMSM()
        {
            try
            {
                const string modId = "stockAlertMod";
                var L = PanelSide.LeftPanel;
                var R = PanelSide.RightPanel;

                MSM.RegisterMod(modId, "Stock Alert Mod");

                MSM.AddLabel(modId, L, "Gain Thresholds");
                MSM.AddCheckbox(modId, L, "+10%",  () => prefGain10.Value,  (Action<bool>)(v => { prefGain10.Value  = v; prefCategory.SaveToFile(false); RebuildThresholds(); }));
                MSM.AddCheckbox(modId, L, "+25%",  () => prefGain25.Value,  (Action<bool>)(v => { prefGain25.Value  = v; prefCategory.SaveToFile(false); RebuildThresholds(); }));
                MSM.AddCheckbox(modId, L, "+50%",  () => prefGain50.Value,  (Action<bool>)(v => { prefGain50.Value  = v; prefCategory.SaveToFile(false); RebuildThresholds(); }));
                MSM.AddCheckbox(modId, L, "+75%",  () => prefGain75.Value,  (Action<bool>)(v => { prefGain75.Value  = v; prefCategory.SaveToFile(false); RebuildThresholds(); }));
                MSM.AddCheckbox(modId, L, "+100%", () => prefGain100.Value, (Action<bool>)(v => { prefGain100.Value = v; prefCategory.SaveToFile(false); RebuildThresholds(); }));

                MSM.AddPadding(modId, L);

                MSM.AddLabel(modId, L, "Price Records");
                MSM.AddCheckbox(modId, L, "All-Time High Alert", () => prefAthAlertsEnabled.Value, (Action<bool>)(v => { prefAthAlertsEnabled.Value = v; prefCategory.SaveToFile(false); }));
                MSM.AddCheckbox(modId, L, "All-Time Low Alert",  () => prefAtlAlertsEnabled.Value, (Action<bool>)(v => { prefAtlAlertsEnabled.Value = v; prefCategory.SaveToFile(false); }));

                MSM.AddPadding(modId, L);

                MSM.AddLabel(modId, L, "Loss Thresholds");
                MSM.AddCheckbox(modId, L, "Loss Alerts Enabled", () => prefLossAlertsEnabled.Value, (Action<bool>)(v => { prefLossAlertsEnabled.Value = v; prefCategory.SaveToFile(false); }));
                MSM.AddCheckbox(modId, L, "-10%", () => prefLoss10.Value, (Action<bool>)(v => { prefLoss10.Value = v; prefCategory.SaveToFile(false); RebuildLossThresholds(); }));
                MSM.AddCheckbox(modId, L, "-20%", () => prefLoss20.Value, (Action<bool>)(v => { prefLoss20.Value = v; prefCategory.SaveToFile(false); RebuildLossThresholds(); }));
                MSM.AddCheckbox(modId, L, "-50%", () => prefLoss50.Value, (Action<bool>)(v => { prefLoss50.Value = v; prefCategory.SaveToFile(false); RebuildLossThresholds(); }));

                MSM.AddLabel(modId, R, "General");
                MSM.AddCheckbox(modId, R, "Mod Enabled", () => prefEnabled.Value, (Action<bool>)(v => { prefEnabled.Value = v; prefCategory.SaveToFile(false); }));
                MSM.AddSlider(modId, R, "Alert Cooldown (seconds)", 15, 120, () => prefAlertCooldown.Value, (Action<int>)(v => { prefAlertCooldown.Value = v; prefCategory.SaveToFile(false); ALERT_COOLDOWN = v; }));
                MSM.AddSlider(modId, R, "Alert Queue Cap", 1, 5, () => prefQueueCap.Value, (Action<int>)(v => { prefQueueCap.Value = v; prefCategory.SaveToFile(false); }));

                MSM.AddPadding(modId, R);
                MSM.AddCheckbox(modId, R, "Plain Alerts (stats only)",         () => prefPlainAlerts.Value,       (Action<bool>)(v => { prefPlainAlerts.Value       = v; prefCategory.SaveToFile(false); }));
                MSM.AddCheckbox(modId, R, "Facial Expressions [Experimental]", () => prefFacialExpressions.Value, (Action<bool>)(v => { prefFacialExpressions.Value = v; prefCategory.SaveToFile(false); }));

                MelonLogger.Msg("[StockAlert] MSM registered.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[StockAlert] ModSettingsMenu not found — in-game config unavailable. ({ex.Message})");
            }
        }

        private static bool IsModuleOwned()
        {
            try
            {
                var gv = Il2Cpp.GameScript.Instance?.GameVariables;
                if (gv == null) return false;
                return gv.CheckFlag("stock_alert_module_installed");
            }
            catch { }
            return false;
        }

        public override void OnUpdate()
        {
            GameStateTracker.PollGameState();
            RefreshWebsiteStatus();

            if (prefEnabled?.Value == false) return;

            float now = Time.realtimeSinceStartup;
            if (now - _lastAlertRealTime < ALERT_COOLDOWN) return;

            if (!GameStateTracker.IsSafeToTrigger()) return;

            if (brain == null || brain.WasCollected)
            {
                brain = UnityEngine.Object.FindObjectOfType<Il2Cpp.ModelBrain>();
            }

            if (brain != null && !brain.WasCollected)
            {
                try { if (brain.IsTalkingWithOverlay) return; }
                catch { brain = null; return; }
            }

            if (TryDequeueAlert(out string text, out string expression))
            {
                TriggerDialog(text, expression);
                _lastAlertRealTime = now;
            }
        }

        private static float _lastAlertRealTime = 0f;

        public static void ActivateTheDialogue()
        {
            if (prefEnabled?.Value == false) return;

            float now = Time.realtimeSinceStartup;
            if (now - _lastAlertRealTime < ALERT_COOLDOWN) return;

            var brain = UnityEngine.Object.FindObjectOfType<Il2Cpp.ModelBrain>();
            if (brain != null && !brain.WasCollected)
            {
                try { if (brain.IsTalkingWithOverlay) return; }
                catch { return; }
            }

            if (TryDequeueAlert(out string text, out string expression))
            {
                MelonCoroutines.Start(TriggerDialogCoroutine(text, expression));
                _lastAlertRealTime = now;
            }
        }

        private void RefreshWebsiteStatus()
        {
            _websiteCheckTimer -= Time.deltaTime;
            if (_websiteCheckTimer > 0f) return;
            _websiteCheckTimer = 0.5f;

            try
            {
                if (_stockWebsite == null || _stockWebsite.WasCollected) return;
                if (!_stockWebsite.gameObject.activeInHierarchy) return;
                if (!IsModuleOwned()) return;

                if (!_layoutApplied)
                {
                    _layoutApplied = true;
                    MelonCoroutines.Start(ApplyLayoutAfterFrame(_stockWebsite));
                }

                UpdatePortfolioDisplay(_stockWebsite.portfolioInfo);
                UpdateAthAtlDisplay(_stockWebsite);
                UpdateForecastDisplay(_stockWebsite);
                UpdateBannerDisplay(_stockWebsite);
            }
            catch { }
        }

        private static void RearrangeLayout(Il2Cpp.StockWebsite ws)
        {
            try
            {
                var root = ws.transform;
                var plotArea = root.Find("Plot Area");
                if (plotArea != null)
                {
                    var vlg = plotArea.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                    if (vlg != null) vlg.enabled = false;
                    SetRect(root, "Plot Area",              15f,  -175f, 825f, 665f);
                    SetRect(plotArea, "Dropdown title",     950f,    75f, 120f, 38f);
                    SetRect(plotArea, "Dropdown",           945f,    50f, 170f, 28f);
                    SetRect(plotArea, "Plot",                50f,  -163f, 820f, 450f);
                }

                SetRect(root, "Portfolio Info", 955f, -175f, 410f, 490f);

                var buttonArea = root.Find("Button Area");
                if (buttonArea != null)
                {
                    var vlg = buttonArea.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                    if (vlg != null) vlg.enabled = false;
                    SetRect(root, "Button Area",                 960f, -615f, 390f, 280f);
                    SetRect(buttonArea, "Buy title",               5f,  -90f, 125f, 43f);
                    SetRect(buttonArea, "Buy Scrollbar",           0f, -127f, 405f, 18f);
                    SetRect(buttonArea, "Buy Button",              0f, -150f, 400f, 93f);
                    SetRect(buttonArea, "Sell title",             425f,  -90f, 120f, 28f);
                    SetRect(buttonArea, "Sell Scrollbar",         415f, -127f, 435f, 18f);
                    SetRect(buttonArea, "Sell Button",            415f, -150f, 207f, 93f);
                    SetRect(buttonArea, "Sell All Button",        625f, -150f, 232f, 93f);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[StockAlert] RearrangeLayout: {ex.Message}");
            }
        }

        private static void SetRect(UnityEngine.Transform parent, string childName,
                                    float x, float y, float w, float h)
        {
            var child = parent.Find(childName);
            if (child == null) return;
            var rt = child.GetComponent<UnityEngine.RectTransform>();
            if (rt == null) return;
            rt.anchorMin        = new UnityEngine.Vector2(0f, 1f);
            rt.anchorMax        = new UnityEngine.Vector2(0f, 1f);
            rt.pivot            = new UnityEngine.Vector2(0f, 1f);
            rt.anchoredPosition = new UnityEngine.Vector2(x, y);
            rt.sizeDelta        = new UnityEngine.Vector2(w, h);
        }

        private static System.Collections.IEnumerator ApplyLayoutAfterFrame(Il2Cpp.StockWebsite ws)
        {
            yield return null;
            yield return null;
            RearrangeLayout(ws);
            yield return null;
            try { ws.SyncAll(); } catch { }
            yield return null;
            var cg = ws.gameObject.GetComponent<CanvasGroup>();
            if (cg != null) cg.alpha = 1f;
        }

        private void TriggerDialog(string text, string expression)
        {
            MelonCoroutines.Start(TriggerDialogCoroutine(text, expression));
        }

        private static System.Collections.IEnumerator SetEmoteDelayed(string expression, float delaySec)
        {
            yield return new UnityEngine.WaitForSeconds(delaySec);
            SetBotEmote("Expression", expression);
        }

        private static void SetBotEmote(string slotId, string itemId)
        {
            try
            {
                var live2D = Il2Cpp.Live2DControllerSingleton.Instance;
                var character = live2D?.GetReadyController("bot");
                if (character == null) return;

                var emote = new Il2Cpp.EmoteData(slotId, itemId) { instant = false };
                var emotesList = new Il2CppSystem.Collections.Generic.List<Il2Cpp.EmoteData>(1);
                emotesList.Add(emote);
                var emotes = emotesList.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Il2Cpp.EmoteData>>();
                character.SetEmote(emotes);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[StockAlert] SetBotEmote failed: {ex.Message}");
            }
        }

        private static System.Collections.IEnumerator TriggerDialogCoroutine(string text, string expression)
        {
            Il2Cpp.Live2DController botController = null;
            try
            {
                var live2D = Il2Cpp.Live2DControllerSingleton.Instance;
                botController = live2D?.GetReadyController("bot");
                if (botController != null)
                {
                    botController.PrepareForDialogue();
                    Il2Cpp.GameScript.Instance.ChangeToStoryState();
                    var brain = botController.CurrentBrain;
                    brain?.EnableBrain();
                    brain?.ChangeState(new Il2Cpp.StoryBrainState());
                    botController.SetEnabled(true);
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[StockAlert] EnterStoryState failed: {ex.Message}");
                EnqueueAlert(text, expression);
                yield break;
            }

            bool viewReady = false;
            bool cameraFailed = false;
            try
            {
                var view = Il2Cpp.ViewSingleton.Instance?.RoomCharacterView;
                if (view != null)
                    Il2Cpp.GameUtilities.Instance.FadeOrMoveToView(view, new System.Action(() => viewReady = true));
                else
                    viewReady = true;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[StockAlert] FadeOrMoveToView failed: {ex.Message}");
                cameraFailed = true;
                viewReady = true;
            }

            float timeout = 3f;
            while (!viewReady && timeout > 0f)
            {
                timeout -= UnityEngine.Time.deltaTime;
                yield return null;
            }

            Il2CppSystem.Collections.IEnumerator conv = null;
            if (!cameraFailed)
            {
                try
                {
                    conv = Il2Cpp.BetterConversationManager.DoConversation("Bot: " + text);
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Error($"[StockAlert] DoConversation failed: {ex.Message}");
                }
            }
            else
            {
                EnqueueAlert(text, expression);
            }

            if (conv != null)
            {
                if (expression != null)
                {
                    MelonCoroutines.Start(SetEmoteDelayed(expression, 0.15f));
                }
                while (conv.MoveNext())
                    yield return conv.Current;
            }

            if (!cameraFailed)
            {
                try
                {
                    Il2Cpp.GameScript.Instance.ChangeToRoomState();
                    var roomView = Il2Cpp.ViewSingleton.Instance?.RoomView;
                    if (roomView != null)
                        Il2Cpp.GameUtilities.Instance.FadeOrMoveToView(roomView, null);
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Warning($"[StockAlert] ExitStoryState failed: {ex.Message}");
                }

                if (expression != null)
                {
                    yield return null;
                    SetBotEmote("Expression", expression);
                    yield return new UnityEngine.WaitForSeconds(2f);
                    SetBotEmote("Expression", "Clear");
                    SetBotEmote("ArmBoth", "DownNormal");
                }
            }
        }

        // =========================================================================
        [HarmonyPatch(typeof(Il2Cpp.StockManager), "MinutePassed")]
        static class StockManagerMinutePassedPatch
        {
            private static int lastProcessedTime = -1;

            static void Postfix(Il2Cpp.StockManager __instance, int time)
            {
                _stockManager = __instance;
                if (time == lastProcessedTime) return;
                lastProcessedTime = time;
                if (prefEnabled?.Value == false) return;
                if (!IsModuleOwned()) return;

                try
                {
                    var companies = __instance.StockCompanies;
                    if (companies == null)
                    {
                        MelonLogger.Warning("[StockAlert] StockCompanies is null.");
                        return;
                    }

                    float[] thresholds = Thresholds;
                    float now = UnityEngine.Time.time;

                    var tickAlerts = new List<(int priority, string msg, string expression)>();

                    for (int i = 0; i < companies.Count; i++)
                    {
                        var company = companies[i];
                        if (company == null) continue;

                        var info = company.GetOwnedStockInfo();
                        string name = company.Name;

                        if (info == null)
                        {
                            alertLevel.TryRemove(name, out _);
                            continue;
                        }

                        if (info.count == 0)
                        {
                            alertLevel.TryRemove(name, out _);
                            lossLevel.TryRemove(name, out _);
                            stockCooldowns.TryRemove(name, out _);
                            allTimeHigh.TryRemove(name, out _);
                            allTimeLow.TryRemove(name, out _);
                            continue;
                        }

                        float avg = info.AveragePrice;
                        if (avg <= 0f) continue;

                        int current = company.CurrentPriceRounded();
                        float pct = (current - avg) / avg;

                        int newLevel = 0;
                        for (int t = 0; t < thresholds.Length; t++)
                        {
                            if (pct >= thresholds[t]) newLevel = t + 1;
                            else break;
                        }

                        int level = alertLevel.GetOrAdd(name, 0);

                        if (newLevel > level)
                        {
                            alertLevel[name] = newLevel;

                            float cooldownUntil = stockCooldowns.GetOrAdd(name, 0f);
                            if (now >= cooldownUntil)
                            {
                                stockCooldowns[name] = now + ALERT_COOLDOWN;

                                int gainPct = (int)(pct * 100f);
                                int sympathy = 0; float mood = 0.5f;
                                try { var vars = Il2Cpp.GameScript.Instance?.GameVariables; if (vars != null) { sympathy = vars.sympathy; mood = vars.Mood; } } catch { }
                                string msg = GetLine("gain", sympathy, mood, name, gainPct, 0);
                                string gainExpr = gainPct >= 50 ? "VeryHappy" : "Happy";
                                tickAlerts.Add((gainPct, msg, gainExpr));
                            }
                        }
                        else if (newLevel < level)
                        {
                            alertLevel[name] = newLevel;
                        }

                        if (prefLossAlertsEnabled?.Value != false)
                        {
                            int newLossLevel = 0;
                            for (int t = 0; t < LossThresholds.Length; t++)
                            {
                                if (-pct >= LossThresholds[t]) newLossLevel = t + 1;
                                else break;
                            }

                            int curLossLevel = lossLevel.GetOrAdd(name, 0);

                            if (newLossLevel > curLossLevel)
                            {
                                lossLevel[name] = newLossLevel;

                                float cooldownUntil = stockCooldowns.GetOrAdd(name, 0f);
                                if (now >= cooldownUntil)
                                {
                                    stockCooldowns[name] = now + ALERT_COOLDOWN;

                                    int lossPct = (int)(-pct * 100f);
                                    int sympathyL = 0; float moodL = 0.5f;
                                    try { var vars = Il2Cpp.GameScript.Instance?.GameVariables; if (vars != null) { sympathyL = vars.sympathy; moodL = vars.Mood; } } catch { }
                                    string msg = GetLine("loss", sympathyL, moodL, name, 0, lossPct);
                                    string lossExpr = lossPct >= 50 ? "VerySad" : "Sad";
                                    tickAlerts.Add((1000 + lossPct, msg, lossExpr));
                                }
                            }
                            else if (newLossLevel < curLossLevel)
                            {
                                lossLevel[name] = newLossLevel;
                            }
                        }

                        bool athEnabled = prefAthAlertsEnabled?.Value != false;
                        bool atlEnabled = prefAtlAlertsEnabled?.Value != false;

                        if (athEnabled || atlEnabled)
                        {
                            float currentF = (float)current;

                            if (!allTimeHigh.ContainsKey(name))
                            {
                                allTimeHigh[name] = currentF;
                                allTimeLow[name]  = currentF;
                            }
                            else
                            {
                                int sympA = 0; float moodA = 0.5f;
                                try { var vars = Il2Cpp.GameScript.Instance?.GameVariables; if (vars != null) { sympA = vars.sympathy; moodA = vars.Mood; } } catch { }

                                if (athEnabled && currentF > allTimeHigh[name])
                                {
                                    allTimeHigh[name] = currentF;
                                    float cooldownUntil = stockCooldowns.GetOrAdd(name, 0f);
                                    if (now >= cooldownUntil)
                                    {
                                        stockCooldowns[name] = now + ALERT_COOLDOWN;
                                        int gainPct = (int)(pct * 100f);
                                        string msg = GetLine("ath", sympA, moodA, name, gainPct, 0);
                                        tickAlerts.Add((500 + gainPct, msg, "VeryHappy"));
                                    }
                                }

                                if (atlEnabled && currentF < allTimeLow[name])
                                {
                                    allTimeLow[name] = currentF;
                                    float cooldownUntil = stockCooldowns.GetOrAdd(name, 0f);
                                    if (now >= cooldownUntil)
                                    {
                                        stockCooldowns[name] = now + ALERT_COOLDOWN;
                                        int lossPct = (int)(-pct * 100f);
                                        string msg = GetLine("atl", sympA, moodA, name, 0, lossPct);
                                        tickAlerts.Add((2000 + lossPct, msg, "VeryShock"));
                                    }
                                }
                            }
                        }
                    }

                    tickAlerts.Sort((a, b) => b.priority.CompareTo(a.priority));
                    foreach (var alert in tickAlerts)
                        EnqueueAlert(alert.msg, alert.expression);
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Error($"[StockAlert] MinutePassed patch error: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.GameVariables), "AddFlag")]
        static class AddFlagPatch
        {
            static void Postfix(string flag)
            {
                if (flag == "stock_alert_module_installed")
                    _pendingInstallConfirm = true;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.PopupCloseScript), "Close")]
        static class PopupClosePatch
        {
            static void Postfix()
            {
                if (_pendingInstallConfirm)
                {
                    _pendingInstallConfirm = false;
                    string combined = string.Join("\nBot: ", _installLines);
                    EnqueueAlert(combined, "Happy");
                }
            }
        }

        // =========================================================================
        // STOCK WEBSITE DISPLAY
        // =========================================================================
        private const string PortfolioMarker = "\u25cf Stock Alert Module";

        [HarmonyPatch(typeof(Il2Cpp.StockWebsite), "Open")]
        static class StockWebsiteOpenPatch
        {
            static void Prefix(Il2Cpp.StockWebsite __instance)
            {
                _stockWebsite = __instance;
                _layoutApplied = false;

                if (!IsModuleOwned()) return;
                var cg = __instance.gameObject.GetComponent<CanvasGroup>();
                if (cg == null)
                    cg = __instance.gameObject.AddComponent<CanvasGroup>();
                cg.alpha = 0f;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.StockWebsite), "SyncAll")]
        static class StockWebsiteStatusPatch
        {
            static void Postfix(Il2Cpp.StockWebsite __instance)
            {
                _stockWebsite = __instance;
                if (!IsModuleOwned()) return;
                UpdatePortfolioDisplay(__instance.portfolioInfo);
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.StockWebsite), "SyncCompany")]
        static class StockWebsiteSyncCompanyPatch
        {
            static void Postfix(Il2Cpp.StockWebsite __instance)
            {
                if (!IsModuleOwned()) return;

                UpdatePortfolioDisplay(__instance.portfolioInfo);

                try
                {
                    var ci = __instance.companyInfo;
                    if (ci == null) return;
                    var cg = ci.gameObject.GetComponent<CanvasGroup>();
                    if (cg == null)
                        cg = ci.gameObject.AddComponent<CanvasGroup>();
                    cg.alpha = 0f;
                }
                catch { }
            }
        }

        private static void UpdatePortfolioDisplay(Il2CppTMPro.TextMeshProUGUI tmp)
        {
            try
            {
                if (tmp == null) return;
                string built = BuildPortfolioText();
                if (built != null) tmp.text = built;
            }
            catch { }
        }

        private static void LoadBanners()
        {
            if (_bannersLoaded && _bannerTextures.Count > 0) return;
            _bannersLoaded = true;
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "StockAlertAssets", "Banners");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    return;
                }
                foreach (var file in Directory.GetFiles(dir, "*.png"))
                {
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        byte[] data = File.ReadAllBytes(file);
                        var tex = new Texture2D(2, 2);
                        if (UnityEngine.ImageConversion.LoadImage(tex, data))
                        {
                            tex.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset;
                            _bannerTextures[name] = tex;
                            var sprite = Sprite.Create(tex,
                                new Rect(0, 0, tex.width, tex.height),
                                new Vector2(0.5f, 0.5f));
                            _bannerSprites[name] = sprite;
                        }
                    }
                    catch (Exception ex) { MelonLogger.Warning($"[StockAlert] Failed to load banner {file}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[StockAlert] Banner load error: {ex.Message}"); }
        }

        private static void UpdateBannerDisplay(Il2Cpp.StockWebsite ws)
        {
            try
            {
                LoadBanners();
                if (_bannerTextures.Count == 0) return;

                var existingBanner = ws.transform.Find("BannerContainer");
                if (existingBanner == null)
                {
                    _bannerContainer = new GameObject("BannerContainer");
                    _bannerContainer.transform.SetParent(ws.transform, false);

                    var newRawImg = _bannerContainer.AddComponent<UnityEngine.UI.RawImage>();
                    newRawImg.color = Color.white;
                    newRawImg.enabled = false;

                    var rt = _bannerContainer.GetComponent<RectTransform>();
                    rt.anchorMin        = new Vector2(0f, 1f);
                    rt.anchorMax        = new Vector2(0f, 1f);
                    rt.pivot            = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(45f, -100f);
                    rt.sizeDelta        = new Vector2(840f, 215f);

                    _bannerContainer.transform.SetAsLastSibling();
                }
                else
                {
                    _bannerContainer = existingBanner.gameObject;
                }

                var currentCompany = ws.CurrentCompany;
                var rawImg = _bannerContainer.GetComponent<UnityEngine.UI.RawImage>();
                if (currentCompany == null || rawImg == null) return;
                string name = currentCompany.Name;
                if (_bannerTextures.ContainsKey(name))
                {
                    rawImg.texture = _bannerTextures[name];
                    rawImg.enabled = true;
                }
                else
                {
                    rawImg.enabled = false;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[StockAlert] Banner display error: {ex.Message}\n{ex.StackTrace}"); }
        }

        private static void UpdateAthAtlDisplay(Il2Cpp.StockWebsite ws)
        {
            try
            {
                if (_stockManager == null || _stockManager.WasCollected)
                    _stockManager = UnityEngine.Object.FindObjectOfType<Il2Cpp.StockManager>();
                if (_stockManager == null) return;

                if (_athAtlObject == null || _athAtlObject.WasCollected)
                {
                    var titleObj = new GameObject("ATH_ATL_Title");
                    titleObj.transform.SetParent(ws.transform, false);
                    var titleTmp = titleObj.AddComponent<Il2CppTMPro.TextMeshProUGUI>();
                    var titleSrc = ws.portfolioInfo;
                    if (titleSrc != null)
                    {
                        titleTmp.font = titleSrc.font;
                        titleTmp.fontSize = 22f;
                        titleTmp.color = new Color(0.9f, 0.9f, 0.9f, 1f);
                        titleTmp.alignment = Il2CppTMPro.TextAlignmentOptions.BottomLeft;
                        titleTmp.text = "Session Highs & Lows";
                    }
                    var titleRt = titleObj.GetComponent<RectTransform>();
                    titleRt.anchorMin        = new Vector2(0f, 1f);
                    titleRt.anchorMax        = new Vector2(0f, 1f);
                    titleRt.pivot            = new Vector2(0f, 1f);
                    titleRt.anchoredPosition = new Vector2(1385f, -175f);
                    titleRt.sizeDelta        = new Vector2(400f, 30f);

                    _athAtlObject = new GameObject("ATH_ATL_Display");
                    _athAtlObject.transform.SetParent(ws.transform, false);

                    var bg = _athAtlObject.AddComponent<UnityEngine.UI.Image>();
                    bg.color = new Color(0.05f, 0.05f, 0.1f, 0.85f);

                    var outline = _athAtlObject.AddComponent<UnityEngine.UI.Outline>();
                    outline.effectColor = new Color(0f, 1f, 0.8f, 0.9f);
                    outline.effectDistance = new Vector2(2f, 2f);

                    var outline2 = _athAtlObject.AddComponent<UnityEngine.UI.Outline>();
                    outline2.effectColor = new Color(0f, 1f, 0.8f, 0.4f);
                    outline2.effectDistance = new Vector2(4f, 4f);

                    var rt = _athAtlObject.GetComponent<RectTransform>();
                    rt.anchorMin        = new Vector2(0f, 1f);
                    rt.anchorMax        = new Vector2(0f, 1f);
                    rt.pivot            = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(1390f, -220f);
                    rt.sizeDelta        = new Vector2(795f, 95f);

                    var textObj = new GameObject("ATH_ATL_Text");
                    textObj.transform.SetParent(_athAtlObject.transform, false);

                    var tmp = textObj.AddComponent<Il2CppTMPro.TextMeshProUGUI>();
                    var srcTmp = ws.portfolioInfo;
                    if (srcTmp != null)
                    {
                        tmp.font = srcTmp.font;
                        tmp.fontSize = 16f;
                        tmp.color = new Color(0.9f, 0.9f, 0.9f, 1f);
                        tmp.alignment = Il2CppTMPro.TextAlignmentOptions.TopLeft;
                        tmp.richText = true;
                        tmp.overflowMode = Il2CppTMPro.TextOverflowModes.Overflow;
                        tmp.enableWordWrapping = false;
                    }

                    var textRt = textObj.GetComponent<RectTransform>();
                    textRt.anchorMin = Vector2.zero;
                    textRt.anchorMax = Vector2.one;
                    textRt.offsetMin = new Vector2(10f, 8f);
                    textRt.offsetMax = new Vector2(-10f, -8f);
                }

                var sb = new StringBuilder();
                var companies = _stockManager.StockCompanies;
                if (companies != null)
                {
                    for (int i = 0; i < companies.Count; i++)
                    {
                        var company = companies[i];
                        if (company == null) continue;
                        string name = company.Name;
                        int current = company.CurrentPriceRounded();

                        float ath = allTimeHigh.GetOrAdd(name, current);
                        float atl = allTimeLow.GetOrAdd(name, current);

                        string athColor = current >= ath ? "#00FF88" : "#AAAAAA";
                        string atlColor = current <= atl ? "#FF4444" : "#AAAAAA";

                        sb.Append($"<b>{name}</b>  ");
                        sb.Append($"ATH: <color={athColor}>{ath:F0}$</color>  ");
                        sb.Append($"ATL: <color={atlColor}>{atl:F0}$</color>  ");
                        sb.AppendLine($"Now: {current}$");
                    }
                }

                var athTmp = _athAtlObject.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>();
                if (athTmp != null) athTmp.text = sb.ToString();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[StockAlert] ATH/ATL display error: {ex.Message}");
            }
        }

        private static void UpdateForecastDisplay(Il2Cpp.StockWebsite ws)
        {
            try
            {
                if (_stockManager == null || _stockManager.WasCollected)
                    _stockManager = UnityEngine.Object.FindObjectOfType<Il2Cpp.StockManager>();
                if (_stockManager == null) return;

                if (_forecastObject == null || _forecastObject.WasCollected)
                {
                    var titleObj = new GameObject("Forecast_Title");
                    titleObj.transform.SetParent(ws.transform, false);
                    var titleTmp = titleObj.AddComponent<Il2CppTMPro.TextMeshProUGUI>();
                    var titleSrc = ws.portfolioInfo;
                    if (titleSrc != null)
                    {
                        titleTmp.font = titleSrc.font;
                        titleTmp.fontSize = 22f;
                        titleTmp.color = new Color(0.9f, 0.9f, 0.9f, 1f);
                        titleTmp.alignment = Il2CppTMPro.TextAlignmentOptions.BottomLeft;
                        titleTmp.text = "Forecast";
                    }
                    var titleRt = titleObj.GetComponent<RectTransform>();
                    titleRt.anchorMin        = new Vector2(0f, 1f);
                    titleRt.anchorMax        = new Vector2(0f, 1f);
                    titleRt.pivot            = new Vector2(0f, 1f);
                    titleRt.anchoredPosition = new Vector2(1390f, -340f);
                    titleRt.sizeDelta        = new Vector2(425f, 30f);

                    _forecastObject = new GameObject("Forecast_Display");
                    _forecastObject.transform.SetParent(ws.transform, false);

                    var bg = _forecastObject.AddComponent<UnityEngine.UI.Image>();
                    bg.color = new Color(0.05f, 0.05f, 0.1f, 0.85f);

                    var outline = _forecastObject.AddComponent<UnityEngine.UI.Outline>();
                    outline.effectColor = new Color(1f, 0.8f, 0f, 0.9f);
                    outline.effectDistance = new Vector2(2f, 2f);

                    var outline2 = _forecastObject.AddComponent<UnityEngine.UI.Outline>();
                    outline2.effectColor = new Color(1f, 0.8f, 0f, 0.4f);
                    outline2.effectDistance = new Vector2(4f, 4f);

                    var rt = _forecastObject.GetComponent<RectTransform>();
                    rt.anchorMin        = new Vector2(0f, 1f);
                    rt.anchorMax        = new Vector2(0f, 1f);
                    rt.pivot            = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(1390f, -380f);
                    rt.sizeDelta        = new Vector2(820f, 245f);

                    var textObj = new GameObject("Forecast_Text");
                    textObj.transform.SetParent(_forecastObject.transform, false);

                    var tmp = textObj.AddComponent<Il2CppTMPro.TextMeshProUGUI>();
                    var srcTmp = ws.portfolioInfo;
                    if (srcTmp != null)
                    {
                        tmp.font = srcTmp.font;
                        tmp.fontSize = 16f;
                        tmp.color = new Color(0.9f, 0.9f, 0.9f, 1f);
                        tmp.alignment = Il2CppTMPro.TextAlignmentOptions.TopLeft;
                        tmp.richText = true;
                        tmp.overflowMode = Il2CppTMPro.TextOverflowModes.Overflow;
                        tmp.enableWordWrapping = false;
                    }

                    var textRt = textObj.GetComponent<RectTransform>();
                    textRt.anchorMin = Vector2.zero;
                    textRt.anchorMax = Vector2.one;
                    textRt.offsetMin = new Vector2(10f, 8f);
                    textRt.offsetMax = new Vector2(-10f, -8f);
                }

                var sb = new StringBuilder();
                var companies = _stockManager.StockCompanies;
                if (companies != null)
                {
                    for (int i = 0; i < companies.Count; i++)
                    {
                        var company = companies[i];
                        if (company == null) continue;
                        string name = company.Name;
                        int current = company.CurrentPriceRounded();
                        float target = company.TargetPrice;
                        float diff = target - current;
                        float diffPct = current > 0 ? (diff / current) * 100f : 0f;

                        string arrow, color, signal, detail;
                        if (diffPct > 10f)       { arrow = "▲▲"; color = "#00FF88"; signal = "STRONG BUY"; detail = "Price expected to rise significantly"; }
                        else if (diffPct > 2f)   { arrow = "▲";  color = "#88FF88"; signal = "BUY"; detail = "Upward trend detected"; }
                        else if (diffPct > -2f)  { arrow = "►";  color = "#FFFF88"; signal = "HOLD"; detail = "Price is stable, no clear movement"; }
                        else if (diffPct > -10f) { arrow = "▼";  color = "#FF8888"; signal = "SELL"; detail = "Downward trend detected"; }
                        else                     { arrow = "▼▼"; color = "#FF4444"; signal = "STRONG SELL"; detail = "Price expected to drop significantly"; }

                        if (i > 0) sb.AppendLine("<color=#555555>────────────────────────────────────────</color>");
                        sb.AppendLine($"<b>{name}</b>  —  <color={color}>{arrow} {signal}</color>");
                        sb.AppendLine($"  <color=#AAAAAA>{detail}  ({diffPct:+0.0;-0.0}%)</color>");
                    }
                }

                var fTmp = _forecastObject.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>();
                if (fTmp != null) fTmp.text = sb.ToString();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[StockAlert] Forecast display error: {ex.Message}");
            }
        }

        private static string BuildPortfolioText()
        {
            if (_stockManager == null || _stockManager.WasCollected)
                _stockManager = UnityEngine.Object.FindObjectOfType<Il2Cpp.StockManager>();
            if (_stockManager == null) return null;

            var companies = _stockManager.StockCompanies;
            if (companies == null) return null;

            var sb = new System.Text.StringBuilder();
            long totalInvested = 0;
            long totalValue    = 0;
            bool hasPositions  = false;

            for (int i = 0; i < companies.Count; i++)
            {
                var company = companies[i];
                if (company == null) continue;

                int current = company.CurrentPriceRounded();
                var info = company.GetOwnedStockInfo();
                bool owned = info != null && info.count > 0;

                if (owned)
                {
                    hasPositions = true;

                    int   count = info.count;
                    long  cost  = info.sum;
                    float avg   = info.AveragePrice;
                    long  value = (long)count * current;
                    long  pl    = value - cost;
                    float pct   = cost > 0 ? (float)pl / cost * 100f : 0f;

                    totalInvested += cost;
                    totalValue    += value;

                    string gainColor = pct  >= 0 ? "#00FF88" : "#FF4444";
                    string plColor   = pl   >= 0 ? "#00FF88" : "#FF4444";
                    string pctStr    = (pct >= 0 ? "+" : "") + $"{pct:F1}%";
                    string plStr     = (pl  >= 0 ? "+" : "") + $"{pl:N0}$";

                    sb.AppendLine($"<b>{company.Name}</b>");
                    sb.AppendLine($"  {count:N0} shares · avg <b>{avg:F0}$</b>");
                    sb.Append    ($"  Now: <color={gainColor}><b>{current}$</b> ({pctStr})</color>");
                    sb.AppendLine($"  ·  P&L: <color={plColor}>{plStr}</color>");
                }
                else
                {
                    sb.AppendLine($"<b>{company.Name}</b>");
                    sb.AppendLine($"  <color=#888888>No position · Price: {current}$</color>");
                }
                sb.AppendLine();
            }

            if (hasPositions)
            {
                long  totalPL  = totalValue - totalInvested;
                float totalPct = totalInvested > 0 ? (float)totalPL / totalInvested * 100f : 0f;
                string tColor  = totalPL  >= 0 ? "#00FF88" : "#FF4444";
                string tPLStr  = (totalPL  >= 0 ? "+" : "") + $"{totalPL:N0}$";
                string tPctStr = (totalPct >= 0 ? "+" : "") + $"{totalPct:F1}%";

                sb.AppendLine("──────────────────────────────");
                sb.AppendLine($"Invested:  {totalInvested:N0}$");
                sb.AppendLine($"Value:     {totalValue:N0}$");
                sb.AppendLine($"P&L: <color={tColor}><b>{tPLStr}</b> ({tPctStr})</color>");
            }

            if (IsModuleOwned())
            {
                sb.AppendLine("──────────────────────────────");
                sb.Append($"<color=#00FFcc>{PortfolioMarker}: Active</color>");
            }

            return sb.ToString();
        }

        [HarmonyPatch(typeof(Il2Cpp.GameScript), "GoToSleep")]
        static class GoToSleepPatch
        {
            static void Prefix()
            {
                GameStateTracker.SetSleeping(true);
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.GameScript), "ChangeToRoomState")]
        static class ChangeToRoomStatePatch
        {
            static void Postfix()
            {
                GameStateTracker.SetSleeping(false);
            }
        }


        [HarmonyPatch(typeof(Il2Cpp.GameVariables), "MinutePassed")]
        static class GameVariablesMinutePassedPatch
        {
            static void Postfix(bool suppressEvents)
            {
                if (!suppressEvents) ActivateTheDialogue();
            }
        }
    }
}