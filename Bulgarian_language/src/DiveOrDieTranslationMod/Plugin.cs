using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.Playables;
using TMPro;
using UnityEngine.Localization;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.Metadata;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace DiveOrDieTranslationMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.diveordie.translationbulgarian";
    public const string PluginName = "Dive or Die Bulgarian Translation";
    public const string PluginVersion = "0.1.0";

    private ConfigEntry<bool> dumpLoadedBundles;
    private ConfigEntry<bool> dumpLocalizationTables;
    private ConfigEntry<bool> traceUiEvents;
    private ConfigEntry<bool> enableTranslationOverrides;
    private ConfigEntry<string> translationLocaleCode;
    private string dumpPath = string.Empty;
    private string localizationDumpPath = string.Empty;
    private static readonly string[] BulgarianLanguageOptionLabels =
    {
        "Английски",
        "Френски",
        "Немски",
        "Японски",
        "Руски",
        "Испански",
        "Китайски (опростен)",
        "Китайски (традиционен)",
        "Полски",
        "Португалски (Бразилия)",
        "Български"
    };
    private readonly HashSet<string> dumpedTables = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> startupTraceStates = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> languageMenuTraceStates = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> settingsTextTraceStates = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> localizationProbeStates = new HashSet<string>(StringComparer.Ordinal);
    private TMP_Dropdown tracedLanguageDropdown;
    private Component tracedSettingsMenu;
    private float nextLanguageTraceTime;
    private readonly Dictionary<string, string> translations = new Dictionary<string, string>(StringComparer.Ordinal);
    private Harmony harmony;
    private Locale bulgarianLocale;
    private bool bulgarianLocaleRegistered;
    private bool restoreBulgarianAfterGameStateAwake;
    private bool replacementActivationLogged;
    private bool numericKeyActivationLogged;
    private bool translationSelfTestLogged;
    private bool escapeHeldLastFrame;
    private int replacementsApplied;
    internal static Plugin Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
        dumpLoadedBundles = Config.Bind("Probe", "DumpLoadedBundles", false,
            "Write loaded AssetBundle names and asset paths to the local dumps folder.");
        dumpLocalizationTables = Config.Bind("Probe", "DumpLocalizationTables", false,
            "Load localization assets and write StringTable keys/values to a local dump.");
        traceUiEvents = Config.Bind("Probe", "TraceUiEvents", false,
            "Log Unity UI button clicks and settings/dropdown diagnostics.");
        enableTranslationOverrides = Config.Bind("Translation", "EnableTranslationOverrides", true,
            "Replace matching Unity Localization table values from translations/labels.txt.");
        translationLocaleCode = Config.Bind("Translation", "TranslationLocaleCode", "bg",
            "Existing or custom locale code that activates the Bulgarian translation. Use bg for the custom locale.");
        dumpPath = Path.Combine(Paths.PluginPath, "DiveOrDieTranslationMod", "dumps", "loaded-bundles.tsv");
        localizationDumpPath = Path.Combine(Paths.PluginPath, "DiveOrDieTranslationMod", "dumps", "localization.tsv");
        LoadTranslations();
        harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        PatchGameStateAwake();
        PatchSettingsLanguageInitialization();
        PatchLocalizedStringGeneration();
        SceneManager.sceneLoaded += OnSceneLoaded;
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded; translation entries: {translations.Count}");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var assemblyFile = string.IsNullOrWhiteSpace(assemblyPath) ? null : new FileInfo(assemblyPath);
        Logger.LogInfo($"Loaded plugin assembly: {assemblyPath}; size={assemblyFile?.Length ?? 0}; modifiedUtc={assemblyFile?.LastWriteTimeUtc:o}");
        StartCoroutine(RegisterLocaleAsEarlyAsPossible());
        StartCoroutine(ProbeAfterStartup());
        StartCoroutine(TraceStartupPlayback());
        StartCoroutine(TraceLanguageMenuState());
    }

    private void Update()
    {
        foreach (var liveDropdown in Resources.FindObjectsOfTypeAll<TMP_Dropdown>()
                     .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid()))
        {
            TryAddBulgarianWhenRendered(liveDropdown);
        }

        if (tracedLanguageDropdown != null)
            TryAddBulgarianWhenRendered(tracedLanguageDropdown);

        if (!traceUiEvents.Value || Time.unscaledTime < nextLanguageTraceTime)
            return;

        nextLanguageTraceTime = Time.unscaledTime + 0.25f;
        TraceCurrentLanguageMenuState();
        if (tracedSettingsMenu != null && tracedSettingsMenu.gameObject.activeInHierarchy)
            TraceSettingsTextComponents();
    }

    private void TraceCurrentLanguageMenuState()
    {
        if (tracedLanguageDropdown != null)
        {
            var values = tracedLanguageDropdown.options == null
                ? "<null>"
                : string.Join(" | ", tracedLanguageDropdown.options.Select(option => option?.text ?? "<null>"));
            var state = $"LanguageDropdown|active={tracedLanguageDropdown.gameObject.activeInHierarchy}|interactable={tracedLanguageDropdown.interactable}|value={tracedLanguageDropdown.value}|count={tracedLanguageDropdown.options?.Count ?? -1}|values=[{values}]";
            if (languageMenuTraceStates.Add("update:" + state))
                Logger.LogInfo($"Language menu Update trace: {state}");
        }

        if (tracedSettingsMenu != null)
        {
            var state = $"SettingsMenu|active={tracedSettingsMenu.gameObject.activeInHierarchy}";
            if (languageMenuTraceStates.Add("update:" + state))
                Logger.LogInfo($"Language menu Update trace: {state}");
        }
    }

    private IEnumerator RegisterLocaleAsEarlyAsPossible()
    {
        // SettingsMenu can read the saved language index before the first scene
        // finishes loading. Register bg as soon as Unity Localization is ready.
        for (var attempt = 0; attempt < 240; attempt++)
        {
            if (LocalizationSettings.Instance != null &&
                LocalizationSettings.AvailableLocales?.Locales != null &&
                LocalizationSettings.AvailableLocales.Locales.Count > 0)
            {
                RegisterBulgarianLocale();
                yield break;
            }

            yield return null;
        }

        Logger.LogWarning("Could not register the Bulgarian locale during early startup.");
    }

    private void LoadTranslations()
    {
        translations.Clear();
        var labelsPath = Path.Combine(Paths.PluginPath, "DiveOrDieTranslationMod", "labels.txt");
        if (!File.Exists(labelsPath))
        {
            Logger.LogWarning($"Translation file not found: {labelsPath}");
            return;
        }

        foreach (var rawLine in File.ReadAllLines(labelsPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = line.Substring(0, separator).Trim();
            var value = DecodeValue(line.Substring(separator + 1));
            if (key.Length > 0)
                translations[key] = value;
        }
    }

    private static string DecodeValue(string value)
    {
        return value
            .Replace("\\\\r\\\\n", "\n")
            .Replace("\\\\n", "\n")
            .Replace("\\r\\n", "\n")
            .Replace("\\n", "\n")
            .Replace("\\r", "\n")
            .Replace("\\t", "\t");
    }

    internal static void ApplyTranslation(StringTableEntry entry, ref string value)
    {
        if (ReferenceEquals(Instance, null) || !Instance.enableTranslationOverrides.Value || entry == null)
            return;

        var selectedLocale = LocalizationSettings.SelectedLocale;
        if (selectedLocale == null || !string.Equals(selectedLocale.Identifier.Code,
            Instance.translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        ApplyTranslationForKey(entry.Key, ref value);
    }

    internal static void ApplyTranslationForKey(string key, ref string value)
    {
        if (ReferenceEquals(Instance, null) || !Instance.enableTranslationOverrides.Value || string.IsNullOrWhiteSpace(key))
            return;

        var selectedLocale = LocalizationSettings.SelectedLocale;
        if (selectedLocale == null || !string.Equals(selectedLocale.Identifier.Code,
            Instance.translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        if (Instance.translations.TryGetValue(key, out var replacement))
        {
            value = replacement;
            Instance.replacementsApplied++;
            if (!Instance.replacementActivationLogged)
            {
                Instance.replacementActivationLogged = true;
                Instance.Logger.LogInfo($"Bulgarian translation replacement active for locale {selectedLocale.Identifier.Code}; first key: {key}; total replacements so far: {Instance.replacementsApplied}");
            }
        }
    }

    private IEnumerator ProbeAfterStartup()
    {
        yield return new WaitForSeconds(3f);
        RegisterBulgarianLocale();
        ProbeBundles("startup");
    }

    private void RegisterBulgarianLocale()
    {
        if (bulgarianLocaleRegistered)
            return;

        var configuredLocaleCode = translationLocaleCode.Value.Trim();
        if (!string.Equals(configuredLocaleCode, "bg", StringComparison.OrdinalIgnoreCase))
        {
            bulgarianLocaleRegistered = true;
            Logger.LogInfo($"Using existing locale slot for Bulgarian translation: {configuredLocaleCode}");
            return;
        }

        var settings = LocalizationSettings.Instance;
        if (settings == null)
        {
            Logger.LogWarning("Unity Localization settings are not available yet.");
            return;
        }
        var availableLocales = LocalizationSettings.AvailableLocales;
        if (availableLocales == null)
        {
            Logger.LogWarning("Unity Localization has no available locale provider.");
            return;
        }

        bulgarianLocale = availableLocales.GetLocale("bg");
        if (bulgarianLocale == null)
        {
            bulgarianLocale = Locale.CreateLocale("bg");
            bulgarianLocale.LocaleName = "Български";
            availableLocales.AddLocale(bulgarianLocale);
            Logger.LogInfo("Registered custom locale: bg (Български)");
        }
        else
        {
            bulgarianLocale.LocaleName = "Български";
            Logger.LogInfo("Custom locale already exists: bg (Български)");
        }

        // A runtime-created locale has no Addressables tables of its own. Route
        // missing bg tables through English so Unity reaches a real StringTableEntry;
        // our key-based override can then replace translated entries while an
        // incomplete translation safely keeps the English source text.
        var englishLocale = availableLocales.GetLocale("en");
        if (englishLocale == null)
        {
            Logger.LogWarning("English fallback locale is unavailable; Bulgarian string tables cannot resolve.");
        }
        else
        {
            var fallbacks = bulgarianLocale.Metadata.GetMetadatas<FallbackLocale>();
            if (!fallbacks.Any(metadata => metadata != null && metadata.Locale == englishLocale))
                bulgarianLocale.Metadata.AddMetadata(new FallbackLocale(englishLocale));

            LocalizationSettings.StringDatabase.UseFallback = true;
            LocalizationSettings.AssetDatabase.UseFallback = true;
            Logger.LogInfo("Configured bg fallback to en and enabled string/asset database fallback.");
        }

        bulgarianLocaleRegistered = true;
        Logger.LogInfo($"Available locales: {string.Join(", ", availableLocales.Locales.Select(locale => locale.Identifier.Code))}");
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RegisterBulgarianLocale();
        RestorePendingBulgarianLocale();
        RestoreSavedBulgarianLocale(scene.name);
        RefreshLocalizedStringsAfterSceneLoad(scene.name);
        ProbeTranslationLookup();
        ProbeBundles("scene:" + scene.name);
        TraceSceneObjects(scene);
    }

    private void RefreshLocalizedStringsAfterSceneLoad(string sceneName)
    {
        // Cross-scene UI objects may have attempted localization before the
        // runtime bg locale and its fallback metadata were registered. SceneLoaded
        // runs after the scene is activated, so enabled references can be refreshed
        // directly without starting a coroutine on a loader object being replaced.

        if (!string.Equals(LocalizationSettings.SelectedLocale?.Identifier.Code,
                translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        var refreshed = 0;
        var keys = new List<string>();
        foreach (var localizeEvent in Resources.FindObjectsOfTypeAll<LocalizeStringEvent>())
        {
            if (localizeEvent == null || !localizeEvent.isActiveAndEnabled ||
                !localizeEvent.gameObject.scene.IsValid() || localizeEvent.StringReference == null)
                continue;

            var key = localizeEvent.StringReference.TableEntryReference.Key;
            if (!string.IsNullOrWhiteSpace(key))
                keys.Add(key);
            if (localizeEvent.StringReference.RefreshString())
                refreshed++;
        }

        Logger.LogInfo($"Refreshed {refreshed} active localized string references after scene {sceneName}; keys=[{string.Join(",", keys.Distinct())}].");
    }

    private void ProbeTranslationLookup()
    {
        if (translationSelfTestLogged || !string.Equals(
                LocalizationSettings.SelectedLocale?.Identifier.Code,
                translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        translationSelfTestLogged = true;
        const long gameplayKeyId = 24577159601623040L;
        var probe = new LocalizedString((TableReference)"Base", (TableEntryReference)gameplayKeyId);
        var value = probe.GetLocalizedString();
        var replaced = TryResolveAndFormatTranslation(probe, ref value);
        Logger.LogInfo($"Bulgarian numeric lookup self-test: Base/{gameplayKeyId} -> [{value}]; replaced={replaced}.");
    }

    private IEnumerator TraceStartupPlayback()
    {
        for (var frame = 0; frame < 1800; frame++)
        {
            if (traceUiEvents.Value)
            {
                foreach (var videoPlayer in Resources.FindObjectsOfTypeAll<VideoPlayer>())
                {
                    if (videoPlayer == null)
                        continue;

                    var state = $"{videoPlayer.gameObject.name}|{videoPlayer.clip?.name ?? "<none>"}|active={videoPlayer.gameObject.activeInHierarchy}|playing={videoPlayer.isPlaying}";
                    if (startupTraceStates.Add(state))
                        Logger.LogInfo($"Startup playback trace: {state}");
                }

                foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                {
                    if (behaviour == null ||
                        (behaviour.GetType().Name != "CutscenePlayer" &&
                         behaviour.GetType().Name != "StartupDisplaysOrchestrator"))
                        continue;

                    var state = $"{behaviour.GetType().Name}|{behaviour.gameObject.name}|active={behaviour.gameObject.activeInHierarchy}";
                    if (startupTraceStates.Add(state))
                        Logger.LogInfo($"Startup controller state: {state}");
                }
            }

            yield return null;
        }
    }

    private IEnumerator TraceLanguageMenuState()
    {
        Logger.LogInfo("Language menu polling started.");
        for (var frame = 0; frame < 3600; frame++)
        {
            if (traceUiEvents.Value)
            {
                var dropdown = tracedLanguageDropdown;
                if (dropdown != null)
                {
                    TryAddBulgarianWhenRendered(dropdown);
                    var values = dropdown.options == null
                        ? "<null>"
                        : string.Join(" | ", dropdown.options.Select(option => option?.text ?? "<null>"));
                    var state = $"LanguageDropdown|active={dropdown.gameObject.activeInHierarchy}|interactable={dropdown.interactable}|value={dropdown.value}|count={dropdown.options?.Count ?? -1}|values=[{values}]";
                    if (languageMenuTraceStates.Add(state))
                        Logger.LogInfo($"Language menu polling: {state}");
                }

                var settingsState = $"SettingsMenu|active={tracedSettingsMenu != null && tracedSettingsMenu.gameObject.activeInHierarchy}|found={tracedSettingsMenu != null}";
                if (tracedSettingsMenu == null || !tracedSettingsMenu)
                {
                    tracedSettingsMenu = FindObjectsOfType<MonoBehaviour>(true)
                        .FirstOrDefault(component => component != null &&
                            component.GetType().FullName == "SettingsMenu");
                    if (tracedSettingsMenu != null)
                        Logger.LogInfo($"Settings menu rediscovered by polling: object={tracedSettingsMenu.gameObject.name}; path={GetHierarchyPath(tracedSettingsMenu.transform)}");
                }

                settingsState = $"SettingsMenu|active={tracedSettingsMenu != null && tracedSettingsMenu.gameObject.activeInHierarchy}|found={tracedSettingsMenu != null}";
                if (languageMenuTraceStates.Add(settingsState))
                    Logger.LogInfo($"Language menu polling: {settingsState}");

                if (tracedSettingsMenu != null && tracedSettingsMenu.gameObject.activeInHierarchy)
                    TraceSettingsTextComponents();
            }

            yield return new WaitForSecondsRealtime(0.25f);
        }
    }

    private void TraceSettingsTextComponents()
    {
        var texts = Resources.FindObjectsOfTypeAll<TMP_Text>()
            .Where(text => text != null &&
                text.gameObject.scene.IsValid() &&
                GetHierarchyPath(text.transform).IndexOf("PNL_SettingsMenu", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToArray();
        var countState = $"count={texts.Length}";
        if (settingsTextTraceStates.Add(countState))
            Logger.LogInfo($"Settings TMP text scan: found={texts.Length}; menuActive={tracedSettingsMenu.gameObject.activeInHierarchy}");

        foreach (var text in texts)
        {
            if (text == null)
                continue;

            var components = text.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => component.GetType().FullName);
            var state = $"{GetHierarchyPath(text.transform)}|text={text.text}|components={string.Join("|", components)}";
            if (settingsTextTraceStates.Add(state))
                Logger.LogInfo($"Settings TMP text trace: object={text.gameObject.name}; path={GetHierarchyPath(text.transform)}; text=[{text.text}]; components={string.Join("|", components)}");
        }
    }

    private void TryAddBulgarianWhenRendered(TMP_Dropdown dropdown)
    {
        if (dropdown == null || dropdown.options == null)
            return;

        if (string.Equals(dropdown.gameObject.name, "LanguageDropdown", StringComparison.Ordinal) &&
            dropdown.options.Count == 0)
        {
            TryPopulateEmptyLanguageDropdown(dropdown);
        }

        if (dropdown.options.Count < 10)
            return;

        EnsureBulgarianSettingsOption(dropdown, $"late rendered-language watcher ({dropdown.gameObject.name})");
    }

    private void TryPopulateEmptyLanguageDropdown(TMP_Dropdown dropdown)
    {
        if (localizationProbeStates.Add($"direct-candidate|{dropdown.GetInstanceID()}"))
            Logger.LogInfo($"Direct language dropdown candidate: object={dropdown.gameObject.name}; active={dropdown.gameObject.activeInHierarchy}; visible={dropdown.options?.Count ?? -1}");

        var localizeDropdown = dropdown.GetComponents<MonoBehaviour>()
            .FirstOrDefault(component => component != null &&
                component.GetType().FullName == "Utilities.Localization.LocalizeDropdown");
        if (localizeDropdown == null)
        {
            Logger.LogWarning("Direct language dropdown skipped: LocalizeDropdown component not found.");
            return;
        }

        var optionsField = AccessTools.Field(localizeDropdown.GetType(), "options");
        var internalOptions = optionsField?.GetValue(localizeDropdown) as IList;
        if (internalOptions == null || internalOptions.Count < 11)
        {
            if (localizationProbeStates.Add($"direct-internal|{dropdown.GetInstanceID()}|{internalOptions?.Count ?? -1}"))
                Logger.LogInfo($"Direct language dropdown waiting for internal options: count={internalOptions?.Count ?? -1}; field={(optionsField != null)}");
            return;
        }

        var optionType = internalOptions[0]?.GetType();
        var textField = optionType == null ? null : AccessTools.Field(optionType, "text");
        if (textField == null)
            return;

        try
        {
            for (var index = 0; index < internalOptions.Count; index++)
            {
                var internalOption = internalOptions[index];
                var localizedString = textField.GetValue(internalOption) as LocalizedString;
                var key = localizedString?.TableEntryReference.Key;
                // The game's ten serialized language references are numeric IDs,
                // so resolving them as textual keys under a runtime bg locale
                // produces "Missing loc" messages. Their order is stable and is
                // the same order as AvailableLocales; use the known display list.
                var value = index < BulgarianLanguageOptionLabels.Length
                    ? BulgarianLanguageOptionLabels[index]
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(key) &&
                    translations.TryGetValue(key, out var replacement))
                    value = replacement;
                else if (string.IsNullOrWhiteSpace(value) && localizedString != null)
                    value = localizedString.GetLocalizedString();

                if (string.IsNullOrWhiteSpace(value))
                    value = string.IsNullOrWhiteSpace(key) ? "Missing loc" : $"Missing loc '{key}'";

                dropdown.options.Add(new TMP_Dropdown.OptionData(value));
            }

            var selectedIndex = LocalizationSettings.AvailableLocales?.Locales?.FindIndex(locale =>
                locale != null && locale == LocalizationSettings.SelectedLocale) ?? -1;
            if (selectedIndex >= 0 && selectedIndex < dropdown.options.Count)
                dropdown.SetValueWithoutNotify(selectedIndex);
            dropdown.RefreshShownValue();
            Logger.LogInfo($"Direct language dropdown population: internal={internalOptions.Count}; visible={dropdown.options.Count}; selected={LocalizationSettings.SelectedLocale?.Identifier.Code ?? "<null>"}.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Direct language dropdown population failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void PatchUiDiagnostics()
    {
        var onPointerClick = AccessTools.Method(typeof(Button), "OnPointerClick", new[] { typeof(PointerEventData) });
        if (onPointerClick == null)
        {
            Logger.LogWarning("UI trace: Button.OnPointerClick was not found.");
            return;
        }

        harmony.Patch(onPointerClick,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(Button_OnPointerClick_Prefix)));
        var press = AccessTools.Method(typeof(Button), "Press");
        if (press != null)
        {
            harmony.Patch(press,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(Button_Press_Prefix)));
        }
        var videoPlay = AccessTools.Method(typeof(VideoPlayer), "Play");
        if (videoPlay != null)
        {
            harmony.Patch(videoPlay,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(VideoPlayer_Play_Prefix)));
        }
        var videoStop = AccessTools.Method(typeof(VideoPlayer), "Stop");
        if (videoStop != null)
        {
            harmony.Patch(videoStop,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(VideoPlayer_Stop_Prefix)));
        }
        var videoPause = AccessTools.Method(typeof(VideoPlayer), "Pause");
        if (videoPause != null)
        {
            harmony.Patch(videoPause,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(VideoPlayer_Pause_Prefix)));
        }
        var directorPlay = AccessTools.Method(typeof(PlayableDirector), "Play");
        if (directorPlay != null)
        {
            harmony.Patch(directorPlay,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(PlayableDirector_Play_Prefix)));
        }
        var directorStop = AccessTools.Method(typeof(PlayableDirector), "Stop");
        if (directorStop != null)
        {
            harmony.Patch(directorStop,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(PlayableDirector_Stop_Prefix)));
        }
        var animatorPlay = AccessTools.Method(typeof(Animator), "Play", new[] { typeof(string) });
        if (animatorPlay != null)
        {
            harmony.Patch(animatorPlay,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(Animator_Play_Prefix)));
        }
        var animatorTrigger = AccessTools.Method(typeof(Animator), "SetTrigger", new[] { typeof(string) });
        if (animatorTrigger != null)
        {
            harmony.Patch(animatorTrigger,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(Animator_SetTrigger_Prefix)));
        }
        var canvasAlpha = AccessTools.Method(typeof(CanvasGroup), "set_alpha");
        if (canvasAlpha != null)
        {
            harmony.Patch(canvasAlpha,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(CanvasGroup_SetAlpha_Prefix)));
        }
        PatchInteractiveTypeDiagnostics("SettingsSelectable");
        PatchInteractiveTypeDiagnostics("InputContext");
        var eventTriggerExecute = AccessTools.Method(typeof(EventTrigger), "Execute");
        if (eventTriggerExecute != null)
        {
            harmony.Patch(eventTriggerExecute,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(InteractiveMethod_Prefix)));
        }
        var getKeyDown = AccessTools.Method(typeof(Input), "GetKeyDown", new[] { typeof(KeyCode) });
        if (getKeyDown != null)
        {
            harmony.Patch(getKeyDown,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(Input_GetKeyDown_Postfix)));
        }
        var getKey = AccessTools.Method(typeof(Input), "GetKey", new[] { typeof(KeyCode) });
        if (getKey != null)
        {
            harmony.Patch(getKey,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(Input_GetKey_Postfix)));
        }
        Logger.LogInfo("UI trace enabled: buttons, activation, Animator, CanvasGroup, and VideoPlayer hooks.");
    }

    private void PatchInteractiveTypeDiagnostics(string typeName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            Logger.LogWarning($"UI trace: type not found: {typeName}.");
            return;
        }

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => !method.IsSpecialName && method.DeclaringType == type &&
                method.Name != "Equals" && method.Name != "GetHashCode" && method.Name != "ToString")
            .ToArray();
        foreach (var method in methods)
        {
            try
            {
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(InteractiveMethod_Prefix)));
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"UI trace: could not patch {typeName}.{method.Name}: {ex.Message}");
            }
        }
        Logger.LogInfo($"UI trace: patched {methods.Length} methods on {typeName}.");
    }

    private static void InteractiveMethod_Prefix(object __instance, MethodBase __originalMethod, object[] __args)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null || __originalMethod == null)
            return;

        if (!IsSettingsTraceObject((__instance as Component)?.transform))
            return;

        var argumentText = __args == null ? string.Empty : string.Join(",", __args.Select(argument => argument == null ? "<null>" : argument.ToString()));
        Instance.Logger.LogInfo($"Interactive callback: type={__originalMethod.DeclaringType?.FullName}; method={__originalMethod.Name}; object={(__instance as Component)?.gameObject.name ?? __instance.GetType().Name}; args=[{argumentText}]");
    }

    private static void Input_GetKeyDown_Postfix(KeyCode key, bool __result)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || !__result)
            return;

        Instance.Logger.LogInfo($"Input key down: scene={SceneManager.GetActiveScene().name}; key={key}");
    }

    private static void Input_GetKey_Postfix(KeyCode key, bool __result)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || key != KeyCode.Escape)
            return;

        if (__result != Instance.escapeHeldLastFrame)
        {
            Instance.escapeHeldLastFrame = __result;
            Instance.Logger.LogInfo($"Input key held state: scene={SceneManager.GetActiveScene().name}; key=Escape; held={__result}");
        }
    }

    private static void Button_OnPointerClick_Prefix(Button __instance)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null)
            return;

        Instance.Logger.LogInfo($"UI click: scene={SceneManager.GetActiveScene().name}; object={__instance.gameObject.name}; path={GetHierarchyPath(__instance.transform)}; interactable={__instance.interactable}");
    }

    private static void Button_Press_Prefix(Button __instance)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null)
            return;

        Instance.Logger.LogInfo($"UI press: scene={SceneManager.GetActiveScene().name}; object={__instance.gameObject.name}; path={GetHierarchyPath(__instance.transform)}; interactable={__instance.interactable}");
    }

    private static void VideoPlayer_Play_Prefix(VideoPlayer __instance)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null)
            return;

        Instance.Logger.LogInfo($"Video Play: scene={SceneManager.GetActiveScene().name}; object={__instance.gameObject.name}; path={GetHierarchyPath(__instance.transform)}; clip={(__instance.clip == null ? "<none>" : __instance.clip.name)}; url={__instance.url}; playOnAwake={__instance.playOnAwake}; length={__instance.length:0.###}");
    }

    private static void VideoPlayer_Stop_Prefix(VideoPlayer __instance)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null)
            return;

        Instance.Logger.LogInfo($"Video Stop: scene={SceneManager.GetActiveScene().name}; object={__instance.gameObject.name}; clip={(__instance.clip == null ? "<none>" : __instance.clip.name)}");
    }

    private static void VideoPlayer_Pause_Prefix(VideoPlayer __instance)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null)
            return;

        Instance.Logger.LogInfo($"Video Pause: scene={SceneManager.GetActiveScene().name}; object={__instance.gameObject.name}; clip={(__instance.clip == null ? "<none>" : __instance.clip.name)}");
    }

    private static void PlayableDirector_Play_Prefix(PlayableDirector __instance)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null)
            return;

        Instance.Logger.LogInfo($"Timeline Play: scene={SceneManager.GetActiveScene().name}; object={__instance.gameObject.name}; path={GetHierarchyPath(__instance.transform)}; asset={(__instance.playableAsset == null ? "<none>" : __instance.playableAsset.name)}");
    }

    private static void PlayableDirector_Stop_Prefix(PlayableDirector __instance)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null)
            return;

        Instance.Logger.LogInfo($"Timeline Stop: scene={SceneManager.GetActiveScene().name}; object={__instance.gameObject.name}; path={GetHierarchyPath(__instance.transform)}; asset={(__instance.playableAsset == null ? "<none>" : __instance.playableAsset.name)}");
    }

    private static bool IsSettingsTraceObject(Transform transform)
    {
        if (transform == null)
            return false;

        var path = GetHierarchyPath(transform);
        return path.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("Language", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("PNL_Menu", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void GameObject_SetActive_Prefix(GameObject __instance, bool value)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null || !IsSettingsTraceObject(__instance.transform))
            return;

        Instance.Logger.LogInfo($"UI active change: scene={SceneManager.GetActiveScene().name}; object={__instance.name}; path={GetHierarchyPath(__instance.transform)}; active={value}");
        if (value && string.Equals(__instance.name, "PNL_SettingsMenu", StringComparison.Ordinal))
            Instance.StartCoroutine(WatchSettingsMenuActivation());
    }

    private static IEnumerator WatchSettingsMenuActivation()
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var dropdown = Resources.FindObjectsOfTypeAll<TMP_Dropdown>()
                .FirstOrDefault(candidate => candidate != null &&
                    candidate.gameObject.name == "LanguageDropdown" &&
                    candidate.gameObject.scene.IsValid());
            if (dropdown != null && dropdown.options != null && dropdown.options.Count == 10)
            {
                Instance.Logger.LogInfo("Settings activation watcher found LanguageDropdown with options=10.");
                EnsureBulgarianSettingsOption(dropdown, "Settings activation watcher");
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.1f);
        }

        Instance.Logger.LogWarning("Settings activation watcher timed out without finding LanguageDropdown options=10.");
    }

    private static void Animator_Play_Prefix(Animator __instance, string stateName)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || !IsSettingsTraceObject(__instance?.transform))
            return;

        Instance.Logger.LogInfo($"UI Animator.Play: object={__instance.gameObject.name}; path={GetHierarchyPath(__instance.transform)}; state={stateName}");
    }

    private static void Animator_SetTrigger_Prefix(Animator __instance, string name)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || !IsSettingsTraceObject(__instance?.transform))
            return;

        Instance.Logger.LogInfo($"UI Animator.SetTrigger: object={__instance.gameObject.name}; path={GetHierarchyPath(__instance.transform)}; trigger={name}");
    }

    private static void CanvasGroup_SetAlpha_Prefix(CanvasGroup __instance, float value)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || !IsSettingsTraceObject(__instance?.transform))
            return;

        Instance.Logger.LogInfo($"UI CanvasGroup.alpha: object={__instance.gameObject.name}; path={GetHierarchyPath(__instance.transform)}; alpha={value:0.###}");
    }

    private static string GetHierarchyPath(Transform transform)
    {
        var names = new List<string>();
        for (var current = transform; current != null; current = current.parent)
            names.Add(current.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private void TraceSceneObjects(Scene scene)
    {
        if (!traceUiEvents.Value)
            return;

        var videos = FindObjectsOfType<VideoPlayer>(true);
        var behaviours = FindObjectsOfType<MonoBehaviour>(true);
        var localizeDropdowns = behaviours.Where(component => component.GetType().FullName == "Utilities.Localization.LocalizeDropdown").ToArray();
        var settingsMenus = behaviours.Where(component => component.GetType().FullName == "SettingsMenu").ToArray();
        tracedLanguageDropdown = localizeDropdowns
            .FirstOrDefault(component => component.gameObject.name == "LanguageDropdown")?
            .GetComponent<TMP_Dropdown>();
        tracedSettingsMenu = settingsMenus.FirstOrDefault();
        Logger.LogInfo($"Scene trace: scene={scene.name}; VideoPlayers={videos.Length}; Buttons={FindObjectsOfType<Button>(true).Length}; LocalizeDropdowns={localizeDropdowns.Length}; SettingsMenus={settingsMenus.Length}");
        foreach (var dropdown in localizeDropdowns)
        {
            var optionsField = AccessTools.Field(dropdown.GetType(), "options");
            var internalOptions = optionsField?.GetValue(dropdown) as System.Collections.IList;
            if (string.Equals(dropdown.gameObject.name, "LanguageDropdown", StringComparison.Ordinal))
            {
                TryAddBulgarianInternalOption(dropdown, optionsField, internalOptions, scene.name);
                var visibleDropdown = dropdown.GetComponent<TMP_Dropdown>();
                if (visibleDropdown != null && visibleDropdown.options != null && visibleDropdown.options.Count == 0)
                    TryPopulateEmptyLanguageDropdown(visibleDropdown);
            }
            var optionTypes = internalOptions == null
                ? "<null>"
                : string.Join(" | ", Enumerable.Range(0, internalOptions.Count)
                    .Select(index => internalOptions[index]?.GetType().FullName ?? "<null>"));
            var optionKeys = internalOptions == null
                ? "<null>"
                : string.Join(" | ", Enumerable.Range(0, internalOptions.Count)
                    .Select(index => DescribeLocalizedDropdownOption(internalOptions[index])));
            Logger.LogInfo($"Dropdown trace: object={dropdown.gameObject.name}; path={GetHierarchyPath(dropdown.transform)}; internalOptions={internalOptions?.Count ?? -1}; optionTypes=[{optionTypes}]; optionKeys=[{optionKeys}]");
        }
        foreach (var settings in settingsMenus)
            Logger.LogInfo($"Settings object trace: object={settings.gameObject.name}; path={GetHierarchyPath(settings.transform)}");
        TraceCurrentLanguageMenuState();
        foreach (var behaviour in behaviours.Where(component =>
            component.GetType().FullName.IndexOf("Video", StringComparison.OrdinalIgnoreCase) >= 0 ||
            component.GetType().FullName.IndexOf("Splash", StringComparison.OrdinalIgnoreCase) >= 0 ||
            component.GetType().FullName.IndexOf("Intro", StringComparison.OrdinalIgnoreCase) >= 0 ||
            component.GetType().FullName.IndexOf("Cutscene", StringComparison.OrdinalIgnoreCase) >= 0 ||
            component.GetType().FullName.IndexOf("Startup", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            Logger.LogInfo($"Startup controller trace: type={behaviour.GetType().FullName}; object={behaviour.gameObject.name}; path={GetHierarchyPath(behaviour.transform)}");
        }
        var directors = FindObjectsOfType<PlayableDirector>(true);
        Logger.LogInfo($"Timeline trace: scene={scene.name}; PlayableDirectors={directors.Length}");
        foreach (var director in directors)
            Logger.LogInfo($"Timeline object trace: object={director.gameObject.name}; path={GetHierarchyPath(director.transform)}; asset={(director.playableAsset == null ? "<none>" : director.playableAsset.name)}; state={director.state}");
        foreach (var target in localizeDropdowns.Cast<Component>().Concat(settingsMenus))
        {
            var components = target.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => component.GetType().FullName);
            Logger.LogInfo($"Component trace: object={target.gameObject.name}; path={GetHierarchyPath(target.transform)}; components={string.Join("|", components)}");
        }
        foreach (var video in videos)
        {
            var components = video.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => component.GetType().FullName);
            Logger.LogInfo($"Video trace: object={video.gameObject.name}; path={GetHierarchyPath(video.transform)}; active={video.gameObject.activeInHierarchy}; playing={video.isPlaying}; clip={(video.clip == null ? "<none>" : video.clip.name)}; url={video.url}; playOnAwake={video.playOnAwake}; length={video.length:0.###}; components={string.Join("|", components)}");
        }
    }

    private void TryAddBulgarianInternalOption(Component dropdown, FieldInfo optionsField, IList internalOptions, string sceneName)
    {
        Logger.LogInfo($"Language option boundary: scene={sceneName}; object={dropdown.gameObject.name}; optionsField={(optionsField != null)}; before={internalOptions?.Count ?? -1}");
        if (optionsField == null || internalOptions == null)
        {
            Logger.LogWarning("Language option boundary skipped: internal options field/list unavailable.");
            return;
        }

        if (internalOptions.Count >= 11)
        {
            Logger.LogInfo($"Language option boundary skipped: already has {internalOptions.Count} options.");
            return;
        }

        if (internalOptions.Count != 10)
        {
            Logger.LogWarning($"Language option boundary skipped: expected 10 stock options, found {internalOptions.Count}.");
            return;
        }

        try
        {
            var optionType = AccessTools.TypeByName("Utilities.Localization.LocalizedDropdownOption");
            var textField = optionType == null ? null : AccessTools.Field(optionType, "text");
            if (optionType == null || textField == null)
            {
                Logger.LogWarning($"Language option boundary skipped: optionType={(optionType == null ? "missing" : "ok")}; textField={(textField == null ? "missing" : "ok")}");
                return;
            }

            var option = Activator.CreateInstance(optionType, nonPublic: true);
            // Keep UpdateDropdownOptions on a known-valid table reference while
            // Settings initializes. The visible slot is renamed to Български by
            // the InitializeValues postfix.
            var seedText = textField.GetValue(internalOptions[9]);
            if (seedText == null)
            {
                Logger.LogWarning("Language option boundary skipped: stock option 9 has no localized text reference.");
                return;
            }
            textField.SetValue(option, seedText);
            internalOptions.Add(option);
            Logger.LogInfo($"Language option boundary success: added safe slot 10; after={internalOptions.Count}; source={sceneName}.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Language option boundary failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string DescribeLocalizedDropdownOption(object option)
    {
        if (option == null)
            return "<null>";

        try
        {
            var textField = AccessTools.Field(option.GetType(), "text");
            var localizedString = textField?.GetValue(option);
            if (localizedString == null)
                return "<no-text>";

            var type = localizedString.GetType();
            var table = type.GetProperty("TableReference")?.GetValue(localizedString)?.ToString() ?? "<no-table>";
            var entry = type.GetProperty("TableEntryReference")?.GetValue(localizedString)?.ToString() ?? "<no-entry>";
            return $"{table}/{entry}";
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
    }

    private void RestorePendingBulgarianLocale()
    {
        if (!restoreBulgarianAfterGameStateAwake)
            return;

        var locales = LocalizationSettings.AvailableLocales?.Locales;
        var bgIndex = locales?.FindIndex(locale =>
            string.Equals(locale.Identifier.Code, "bg", StringComparison.OrdinalIgnoreCase)) ?? -1;
        if (bgIndex < 0)
            return;

        try
        {
            var gameStateType = AccessTools.TypeByName("GameState");
            var gameState = gameStateType == null ? null : AccessTools.Property(gameStateType, "Instance")?.GetValue(null);
            var settingsState = gameState == null ? null : AccessTools.Property(gameStateType, "SettingsState")?.GetValue(gameState);
            var languageIndex = settingsState == null ? null : AccessTools.Field(settingsState.GetType(), "languageIndex");
            languageIndex?.SetValue(settingsState, bgIndex);
            LocalizationSettings.SelectedLocale = locales[bgIndex];
            restoreBulgarianAfterGameStateAwake = false;
            Logger.LogInfo($"Restored pending Bulgarian language selection after GameState.Awake; index={bgIndex}.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not restore pending Bulgarian language selection: {ex.Message}");
        }
    }

    private void RestoreSavedBulgarianLocale(string sceneName)
    {
        var locales = LocalizationSettings.AvailableLocales?.Locales;
        if (locales == null)
            return;

        var bgIndex = locales.FindIndex(locale =>
            string.Equals(locale.Identifier.Code, "bg", StringComparison.OrdinalIgnoreCase));
        if (bgIndex < 0)
            return;

        try
        {
            var gameStateType = AccessTools.TypeByName("GameState");
            var gameState = gameStateType == null ? null : AccessTools.Property(gameStateType, "Instance")?.GetValue(null);
            var settingsState = gameState == null ? null : AccessTools.Property(gameStateType, "SettingsState")?.GetValue(gameState);
            var languageIndex = settingsState == null ? null : AccessTools.Field(settingsState.GetType(), "languageIndex");
            if (languageIndex == null)
                return;

            var savedIndex = (int)languageIndex.GetValue(settingsState);
            Logger.LogInfo($"Scene locale check ({sceneName}): savedIndex={savedIndex}, bgIndex={bgIndex}, selected={(LocalizationSettings.SelectedLocale == null ? "<null>" : LocalizationSettings.SelectedLocale.Identifier.Code)}");

            if (string.Equals(translationLocaleCode.Value.Trim(), "bg", StringComparison.OrdinalIgnoreCase))
            {
                if (savedIndex != bgIndex)
                    languageIndex.SetValue(settingsState, bgIndex);
                if (!string.Equals(LocalizationSettings.SelectedLocale?.Identifier.Code,
                        "bg", StringComparison.OrdinalIgnoreCase))
                    LocalizationSettings.SelectedLocale = locales[bgIndex];
                Logger.LogInfo($"Activated Bulgarian locale during scene loading; index={bgIndex}.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not restore Bulgarian locale during scene loading: {ex.Message}");
        }
    }

    private void PatchLanguageDropdown()
    {
        var dropdownType = AccessTools.TypeByName("Utilities.Localization.LocalizeDropdown");
        var populateDropdown = dropdownType == null ? null : AccessTools.Method(dropdownType, "PopulateDropdown");
        var addOptions = dropdownType == null ? null : AccessTools.Method(dropdownType, "AddOptions", new[] { typeof(List<string>) });
        var start = dropdownType == null ? null : AccessTools.Method(dropdownType, "Start");
        var addOptionsAsync = dropdownType == null ? null : AccessTools.Method(dropdownType, "AddOptionsAsync", new[] { typeof(List<string>) });
        if (populateDropdown == null)
            Logger.LogWarning("Could not find LocalizeDropdown.PopulateDropdown.");
        else
        {
            harmony.Patch(populateDropdown,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalizeDropdown_PopulateDropdown_Prefix)));
            LogHarmonyPatchStatus("LocalizeDropdown.PopulateDropdown", populateDropdown);
        }

        if (addOptions == null)
            Logger.LogWarning("Could not find LocalizeDropdown.AddOptions(List<string>).");
        else
        {
            harmony.Patch(addOptions,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalizeDropdown_AddOptions_Prefix)));
            LogHarmonyPatchStatus("LocalizeDropdown.AddOptions", addOptions);
        }

        if (start != null)
        {
            harmony.Patch(start,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalizeDropdown_Start_Prefix)));
            LogHarmonyPatchStatus("LocalizeDropdown.Start", start);
        }

        if (addOptionsAsync != null)
        {
            harmony.Patch(addOptionsAsync,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalizeDropdown_AddOptionsAsync_Prefix)));
            LogHarmonyPatchStatus("LocalizeDropdown.AddOptionsAsync", addOptionsAsync);
        }

        PatchLocalizeDropdownStateMachine(dropdownType, "<PopulateDropdown>d__8", "PopulateDropdown");
        PatchLocalizeDropdownStateMachine(dropdownType, "<AddOptionsRoutine>d__13", "AddOptionsRoutine");

        var settingsMenuType = AccessTools.TypeByName("SettingsMenu");
        var initializeValues = settingsMenuType == null ? null : AccessTools.Method(settingsMenuType, "InitializeValues");
        if (initializeValues != null)
        {
            harmony.Patch(initializeValues,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(SettingsMenu_InitializeValues_Prefix)));
            LogHarmonyPatchStatus("SettingsMenu.InitializeValues", initializeValues);
        }

        var changeLanguage = settingsMenuType == null ? null :
            AccessTools.Method(settingsMenuType, "GeneralTab_ChangeLanguage", new[] { typeof(int) });
        if (changeLanguage != null)
        {
            harmony.Patch(changeLanguage,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(SettingsMenu_ChangeLanguage_Postfix)));
            LogHarmonyPatchStatus("SettingsMenu.GeneralTab_ChangeLanguage", changeLanguage);
        }

        var display = settingsMenuType == null ? null : AccessTools.Method(settingsMenuType, "Display");
        if (display != null)
        {
            harmony.Patch(display,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(SettingsMenu_Display_Postfix)));
            LogHarmonyPatchStatus("SettingsMenu.Display", display);
        }


        var onEnable = settingsMenuType == null ? null : AccessTools.Method(settingsMenuType, "OnEnable");
        if (onEnable != null)
        {
            harmony.Patch(onEnable,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(SettingsMenu_OnEnable_Postfix)));
            LogHarmonyPatchStatus("SettingsMenu.OnEnable", onEnable);
        }

        var dropdownShow = AccessTools.Method(typeof(TMP_Dropdown), "Show");
        if (dropdownShow != null)
        {
            harmony.Patch(dropdownShow,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(TMP_Dropdown_Show_Prefix)));
            LogHarmonyPatchStatus("TMP_Dropdown.Show", dropdownShow);
        }

        var dropdownValueSetter = AccessTools.PropertySetter(typeof(TMP_Dropdown), "value");
        if (dropdownValueSetter != null)
        {
            harmony.Patch(dropdownValueSetter,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(TMP_Dropdown_Value_Prefix)));
            LogHarmonyPatchStatus("TMP_Dropdown.value setter", dropdownValueSetter);
        }

        var dropdownClear = AccessTools.Method(typeof(TMP_Dropdown), "ClearOptions");
        if (dropdownClear != null)
        {
            harmony.Patch(dropdownClear,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(TMP_Dropdown_ClearOptions_Postfix)));
            LogHarmonyPatchStatus("TMP_Dropdown.ClearOptions", dropdownClear);
        }

        var dropdownAdd = AccessTools.Method(typeof(TMP_Dropdown), "AddOptions", new[] { typeof(List<TMP_Dropdown.OptionData>) });
        if (dropdownAdd != null)
        {
            harmony.Patch(dropdownAdd,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(TMP_Dropdown_AddOptions_Postfix)));
            LogHarmonyPatchStatus("TMP_Dropdown.AddOptions(List<OptionData>)", dropdownAdd);
        }

        var refreshShownValue = AccessTools.Method(typeof(TMP_Dropdown), "RefreshShownValue");
        if (refreshShownValue != null)
        {
            harmony.Patch(refreshShownValue,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(TMP_Dropdown_RefreshShownValue_Postfix)));
            LogHarmonyPatchStatus("TMP_Dropdown.RefreshShownValue", refreshShownValue);
        }

        Logger.LogInfo("Patched LocalizeDropdown language diagnostics for both PopulateDropdown and AddOptions paths.");
    }

    private void PatchSettingsLanguageInitialization()
    {
        var settingsMenuType = AccessTools.TypeByName("SettingsMenu");
        var initializeValues = settingsMenuType == null
            ? null
            : AccessTools.Method(settingsMenuType, "InitializeValues");
        if (initializeValues == null)
        {
            Logger.LogWarning("Could not find SettingsMenu.InitializeValues; language index 10 may be clamped when Settings opens.");
            return;
        }

        // SettingsMenu assigns the saved language index to TMP_Dropdown.value.
        // TMP clamps 10 to 9 when its visible option list still contains only the
        // ten stock languages, and the resulting callback switches the locale to
        // pt-BR. Populate the eleventh slot before that assignment takes place.
        harmony.Patch(initializeValues,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SettingsMenu_InitializeValues_Prefix)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(SettingsMenu_InitializeValues_Postfix)));
        LogHarmonyPatchStatus("SettingsMenu.InitializeValues language guard", initializeValues);
    }

    private void PatchLocalizeDropdownStateMachine(Type dropdownType, string nestedTypeName, string label)
    {
        if (dropdownType == null)
            return;

        var stateMachineType = dropdownType.GetNestedType(nestedTypeName, BindingFlags.NonPublic);
        var moveNext = stateMachineType == null ? null :
            AccessTools.Method(stateMachineType, "MoveNext");
        if (moveNext == null)
        {
            Logger.LogWarning($"Harmony diagnostic: LocalizeDropdown.{nestedTypeName}.MoveNext was not found.");
            return;
        }

        harmony.Patch(moveNext,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalizeDropdown_StateMachine_MoveNext_Prefix)));
        LogHarmonyPatchStatus($"LocalizeDropdown.{label} state machine MoveNext", moveNext);
    }

    private void LogHarmonyPatchStatus(string label, MethodBase original)
    {
        if (original == null)
        {
            Logger.LogWarning($"Harmony diagnostic: {label} method was not found.");
            return;
        }

        var patchInfo = Harmony.GetPatchInfo(original);
        var prefixOwners = patchInfo?.Prefixes == null
            ? string.Empty
            : string.Join(",", patchInfo.Prefixes.Select(patch => patch.owner));
        var postfixOwners = patchInfo?.Postfixes == null
            ? string.Empty
            : string.Join(",", patchInfo.Postfixes.Select(patch => patch.owner));
        Logger.LogInfo($"Harmony diagnostic: {label}; prefixes=[{prefixOwners}]; postfixes=[{postfixOwners}].");
    }

    private void PatchGameStateAwake()
    {
        var gameStateType = AccessTools.TypeByName("GameState");
        var awake = gameStateType == null ? null : AccessTools.Method(gameStateType, "Awake");
        if (awake == null)
        {
            Logger.LogWarning("Could not find GameState.Awake for language-index protection.");
            return;
        }

        harmony.Patch(awake,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(GameState_Awake_Prefix)));
        Logger.LogInfo("Patched GameState.Awake to protect the saved language index.");
    }

    private static void GameState_Awake_Prefix(object __instance)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        try
        {
            var settingsState = AccessTools.Field(__instance.GetType(), "_settingsState")?.GetValue(__instance);
            var languageIndex = settingsState == null ? null : AccessTools.Field(settingsState.GetType(), "languageIndex");
            if (settingsState == null || languageIndex == null)
                return;

            var savedIndex = (int)languageIndex.GetValue(settingsState);
            // GameState.Awake can run before Unity Localization has populated
            // AvailableLocales. At this point index 10 is unsafe regardless of
            // whether the locale list is temporarily null or still has 10 items.
            if (savedIndex < 10)
                return;

            if (savedIndex == 10)
                Instance.restoreBulgarianAfterGameStateAwake = true;

            languageIndex.SetValue(settingsState, 0);
            Instance.Logger.LogWarning($"Protected GameState.Awake from invalid language index {savedIndex}; temporary index=0.");
        }
        catch (Exception ex)
        {
            Instance.Logger.LogWarning($"Could not protect GameState.Awake language index: {ex.Message}");
        }
    }

    private static void SettingsMenu_ChangeLanguage_Postfix(int value)
    {
        if (ReferenceEquals(Instance, null))
            return;

        var locales = LocalizationSettings.AvailableLocales?.Locales;
        var bg = locales?.FirstOrDefault(locale =>
            string.Equals(locale.Identifier.Code, "bg", StringComparison.OrdinalIgnoreCase));
        var selected = LocalizationSettings.SelectedLocale;
        Instance.Logger.LogInfo($"Language selection observed: index={value}, selected={(selected == null ? "<null>" : selected.Identifier.Code)}, bgIndex={(bg == null || locales == null ? -1 : locales.IndexOf(bg))}");

        if (bg == null || locales == null || value != locales.IndexOf(bg))
            return;

        if (!ReferenceEquals(LocalizationSettings.SelectedLocale, bg))
        {
            LocalizationSettings.SelectedLocale = bg;
            Instance.Logger.LogInfo("Forced SelectedLocale to bg after the Bulgarian dropdown option was chosen.");
        }
    }

    private static void SettingsMenu_InitializeValues_Prefix(TMP_Dropdown ____languageDropdown)
    {
        if (ReferenceEquals(Instance, null))
            return;

        Instance.Logger.LogInfo($"Settings language guard entered before InitializeValues; dropdown={(____languageDropdown == null ? "<null>" : ____languageDropdown.gameObject.name)}; options={____languageDropdown?.options?.Count ?? -1}; value={____languageDropdown?.value ?? -1}; selected={LocalizationSettings.SelectedLocale?.Identifier.Code ?? "<null>"}");
        EnsureBulgarianSettingsOption(____languageDropdown, "InitializeValues prefix");
    }

    private static void SettingsMenu_InitializeValues_Postfix(TMP_Dropdown ____languageDropdown)
    {
        EnsureBulgarianSettingsOption(____languageDropdown, "InitializeValues postfix");
    }

    private static void SettingsMenu_Display_Postfix(object __instance)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        var field = AccessTools.Field(__instance.GetType(), "_languageDropdown");
        var dropdown = field?.GetValue(__instance) as TMP_Dropdown;
        if (Instance.traceUiEvents.Value)
        {
            Instance.Logger.LogInfo($"Settings trace: Display; object={__instance.GetType().FullName}; dropdown={(dropdown == null ? "<null>" : dropdown.gameObject.name)}; options={dropdown?.options?.Count ?? -1}; value={dropdown?.value ?? -1}");
        }
        EnsureBulgarianSettingsOption(dropdown, "Display");
    }

    private static void SettingsMenu_OnEnable_Postfix(object __instance)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        var field = AccessTools.Field(__instance.GetType(), "_languageDropdown");
        var dropdown = field?.GetValue(__instance) as TMP_Dropdown;
        Instance.Logger.LogInfo($"Settings trace: OnEnable; dropdown={(dropdown == null ? "<null>" : dropdown.gameObject.name)}; options={dropdown?.options?.Count ?? -1}; value={dropdown?.value ?? -1}");
        EnsureBulgarianSettingsOption(dropdown, "OnEnable");
    }

    private static void TMP_Dropdown_Show_Prefix(TMP_Dropdown __instance)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        var isLanguage = string.Equals(__instance.gameObject.name, "LanguageDropdown", StringComparison.OrdinalIgnoreCase);
        Instance.Logger.LogInfo($"TMP_Dropdown.Show: object={__instance.gameObject.name}; language={isLanguage}; options={__instance.options?.Count ?? -1}; value={__instance.value}");
        if (isLanguage)
            EnsureBulgarianSettingsOption(__instance, "TMP_Dropdown.Show");
    }

    private static void TMP_Dropdown_Value_Prefix(TMP_Dropdown __instance, int value)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        var isLanguage = string.Equals(__instance.gameObject.name, "LanguageDropdown", StringComparison.OrdinalIgnoreCase);
        if (Instance.traceUiEvents.Value || isLanguage)
            Instance.Logger.LogInfo($"TMP_Dropdown.value changed: object={__instance.gameObject.name}; language={isLanguage}; value={value}; options={__instance.options?.Count ?? -1}");

        if (isLanguage)
            EnsureBulgarianSettingsOption(__instance, "TMP_Dropdown.value");
    }

    private static void TMP_Dropdown_ClearOptions_Postfix(TMP_Dropdown __instance)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        Instance.Logger.LogInfo($"TMP_Dropdown.ClearOptions: object={__instance.gameObject.name}; remaining={__instance.options?.Count ?? -1}");
    }

    private static void TMP_Dropdown_AddOptions_Postfix(TMP_Dropdown __instance, List<TMP_Dropdown.OptionData> options)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        var values = options == null
            ? "<null>"
            : string.Join(" | ", options.Select(option => option?.text ?? "<null>"));
        Instance.Logger.LogInfo($"TMP_Dropdown.AddOptions: object={__instance.gameObject.name}; added={options?.Count ?? -1}; values=[{values}]; total={__instance.options?.Count ?? -1}");
    }

    private static void TMP_Dropdown_RefreshShownValue_Postfix(TMP_Dropdown __instance)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        var values = __instance.options == null
            ? "<null>"
            : string.Join(" | ", __instance.options.Select(option => option?.text ?? "<null>"));
        Instance.Logger.LogInfo($"TMP_Dropdown.RefreshShownValue: object={__instance.gameObject.name}; value={__instance.value}; options={__instance.options?.Count ?? -1}; values=[{values}]");
    }

    private static void EnsureBulgarianSettingsOption(TMP_Dropdown dropdown, string source)
    {
        if (ReferenceEquals(Instance, null) || dropdown == null ||
            !string.Equals(Instance.translationLocaleCode.Value.Trim(), "bg", StringComparison.OrdinalIgnoreCase))
            return;

        var options = dropdown.options;
        if (options == null)
        {
            Instance.Logger.LogWarning($"Settings trace: {source}; dropdown options list is null.");
            return;
        }

        var localizeDropdown = dropdown.GetComponents<MonoBehaviour>()
            .FirstOrDefault(component => component != null &&
                component.GetType().FullName == "Utilities.Localization.LocalizeDropdown");
        var internalOptionsField = localizeDropdown == null
            ? null
            : AccessTools.Field(localizeDropdown.GetType(), "options");
        var internalOptions = internalOptionsField?.GetValue(localizeDropdown) as IList;
        if (localizeDropdown == null || internalOptionsField == null || internalOptions == null)
        {
            Instance.Logger.LogWarning($"Settings language guard skipped ({source}): LocalizeDropdown internal options are unavailable.");
            return;
        }

        if (internalOptions.Count == 10)
            Instance.TryAddBulgarianInternalOption(localizeDropdown, internalOptionsField, internalOptions, source);
        if (internalOptions.Count != 11)
        {
            Instance.Logger.LogWarning($"Settings language guard skipped visible repair ({source}): internal options={internalOptions.Count}; expected=11.");
            return;
        }

        var before = options.Count;
        Instance.Logger.LogInfo($"Settings trace: {source}; before repair visible={before}; internal={internalOptions.Count}; selected={LocalizationSettings.SelectedLocale?.Identifier.Code ?? "<null>"}");

        if (options.Count == 10)
        {
            options.Add(new TMP_Dropdown.OptionData(BulgarianLanguageOptionLabels[10]));
        }
        else if (options.Count < 10)
        {
            // An empty/partial list is the LocalizeDropdown coroutine's transient
            // state. A single appended item would make Bulgarian index 0, so build
            // the complete ordered selector instead.
            options.Clear();
            foreach (var label in BulgarianLanguageOptionLabels)
                options.Add(new TMP_Dropdown.OptionData(label));
        }
        else if (options.Count > 10)
        {
            // The locale order is fixed by AvailableLocales; slot 10 belongs to bg.
            if (options[10] == null)
                options[10] = new TMP_Dropdown.OptionData(BulgarianLanguageOptionLabels[10]);
            else
                options[10].text = BulgarianLanguageOptionLabels[10];
        }

        if (options.Count <= 10)
        {
            Instance.Logger.LogWarning($"Settings language guard could not create index 10 ({source}); options={options.Count}.");
            return;
        }

        dropdown.RefreshShownValue();
        Instance.Logger.LogInfo($"Settings language guard ready ({source}); visibleBefore={before}; visibleAfter={options.Count}; internal={internalOptions.Count}; index10=[{options[10]?.text ?? "<null>"}].");
    }

    private void PatchLocalizedStringGeneration()
    {
        var formattedEntryMethod = typeof(StringTableEntry)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "GetLocalizedString" &&
                method.ReturnType == typeof(string) && method.GetParameters().Length == 3);
        if (formattedEntryMethod == null)
        {
            Logger.LogWarning("Could not find the formatted StringTableEntry localization method.");
            return;
        }

        harmony.Patch(formattedEntryMethod,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(StringTableEntry_Format_Prefix)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(StringTableEntry_Format_Postfix)),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(StringTableEntry_Format_Finalizer)));
        Logger.LogInfo("Patched the formatted StringTableEntry value boundary.");

        var invokeChangeHandler = AccessTools.Method(typeof(LocalizedString), "InvokeChangeHandler", new[] { typeof(string) });
        if (invokeChangeHandler != null)
        {
            harmony.Patch(invokeChangeHandler,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalizedString_InvokeChangeHandler_Prefix)));
            Logger.LogInfo("Patched the final LocalizedString change-notification boundary.");
        }

        var localizeTextMeshType = AccessTools.TypeByName("LocalizeTextMesh");
        var localizeTextMeshStart = localizeTextMeshType == null ? null : AccessTools.Method(localizeTextMeshType, "Start");
        if (localizeTextMeshStart != null)
        {
            harmony.Patch(localizeTextMeshStart,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(LocalizeTextMesh_Start_Postfix)));
            Logger.LogInfo("Patched the game's key-based LocalizeTextMesh component.");
        }

        var localizeStringEventUpdate = AccessTools.Method(typeof(LocalizeStringEvent), "UpdateString", new[] { typeof(string) });
        if (localizeStringEventUpdate != null)
        {
            harmony.Patch(localizeStringEventUpdate,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalizeStringEvent_UpdateString_Prefix)));
            Logger.LogInfo("Patched the game's LocalizeStringEvent UI text path.");
        }

        var textAsLocKeyType = AccessTools.TypeByName("TextAsLocKey");
        var textAsLocKeyOnEnable = textAsLocKeyType == null ? null : AccessTools.Method(textAsLocKeyType, "OnEnable");
        if (textAsLocKeyOnEnable != null)
        {
            harmony.Patch(textAsLocKeyOnEnable,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(TextAsLocKey_OnEnable_Postfix)));
            Logger.LogInfo("Patched the game's TextAsLocKey UI component.");
        }

        var locUtilityType = AccessTools.TypeByName("LocUtility");
        var getLocString = locUtilityType == null
            ? null
            : AccessTools.Method(locUtilityType, "GetLocString", new[] { typeof(string) });
        if (getLocString != null)
        {
            harmony.Patch(getLocString,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(LocUtility_GetLocString_Postfix)));
            Logger.LogInfo("Patched the game's LocUtility.GetLocString path.");
        }

        var getLocStringText = locUtilityType == null ? null :
            AccessTools.Method(locUtilityType, "GetLocStringText", new[] { typeof(string) });
        if (getLocStringText != null)
        {
            harmony.Patch(getLocStringText,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LocUtility_GetLocStringText_Prefix)));
            Logger.LogInfo("Patched the game's LocUtility text lookup path.");
        }

        var updateLocKey = locUtilityType == null ? null :
            AccessTools.Method(locUtilityType, "UpdateLocKey", new[] { typeof(LocalizeStringEvent), typeof(string), typeof(bool) });
        if (updateLocKey != null)
        {
            harmony.Patch(updateLocKey,
                postfix: new HarmonyMethod(typeof(Plugin), nameof(LocUtility_UpdateLocKey_Postfix)));
            Logger.LogInfo("Patched the game's LocUtility.UpdateLocKey path.");
        }

    }

    private sealed class EntryOverrideState
    {
        internal string OriginalValue;
        internal bool Replaced;
    }

    private static void StringTableEntry_Format_Prefix(StringTableEntry __instance, ref EntryOverrideState __state)
    {
        __state = new EntryOverrideState();
        if (ReferenceEquals(Instance, null) || __instance == null || !Instance.enableTranslationOverrides.Value ||
            !string.Equals(LocalizationSettings.SelectedLocale?.Identifier.Code,
                Instance.translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !Instance.translations.TryGetValue(__instance.Key, out var replacement))
            return;

        __state.OriginalValue = __instance.Value;
        __state.Replaced = true;
        __instance.Value = replacement;
        Instance.replacementsApplied++;
        if (!Instance.replacementActivationLogged)
        {
            Instance.replacementActivationLogged = true;
            Instance.Logger.LogInfo($"Bulgarian formatted translation replacement active; first key: {__instance.Key}.");
        }
    }

    private static void StringTableEntry_Format_Postfix(StringTableEntry __instance, EntryOverrideState __state)
    {
        RestoreStringTableEntry(__instance, __state);
    }

    private static Exception StringTableEntry_Format_Finalizer(
        Exception __exception,
        StringTableEntry __instance,
        EntryOverrideState __state)
    {
        RestoreStringTableEntry(__instance, __state);
        return __exception;
    }

    private static void RestoreStringTableEntry(StringTableEntry entry, EntryOverrideState state)
    {
        if (entry != null && state != null && state.Replaced)
        {
            entry.Value = state.OriginalValue;
            state.Replaced = false;
        }
    }

    private static void LocalizedString_InvokeChangeHandler_Prefix(LocalizedString __instance, ref string value)
    {
        TryResolveAndFormatTranslation(__instance, ref value);
    }

    private static bool TryResolveAndFormatTranslation(LocalizedString localizedString, ref string value)
    {
        if (ReferenceEquals(Instance, null) || localizedString == null || !Instance.enableTranslationOverrides.Value ||
            !string.Equals(LocalizationSettings.SelectedLocale?.Identifier.Code,
                Instance.translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        var englishLocale = LocalizationSettings.AvailableLocales?.GetLocale("en");
        var entry = englishLocale == null
            ? null
            : LocalizationSettings.StringDatabase.GetTableEntry(
                localizedString.TableReference,
                localizedString.TableEntryReference,
                englishLocale,
                FallbackBehavior.DontUseFallback).Entry;
        if (entry == null)
        {
            var marker = $"resolve-failed|{localizedString.TableReference}|{localizedString.TableEntryReference}";
            if (Instance.traceUiEvents.Value && Instance.localizationProbeStates.Add(marker))
                Instance.Logger.LogInfo($"Bulgarian key resolution unavailable: table={localizedString.TableReference}; reference={localizedString.TableEntryReference}.");
            return false;
        }
        if (!Instance.translations.TryGetValue(entry.Key, out var replacement))
            return false;

        var original = entry.Value;
        try
        {
            entry.Value = replacement;
            value = entry.GetLocalizedString(localizedString.Arguments);
            Instance.replacementsApplied++;
            if (!Instance.numericKeyActivationLogged && localizedString.TableEntryReference.KeyId != 0)
            {
                Instance.numericKeyActivationLogged = true;
                Instance.Logger.LogInfo($"Bulgarian numeric-key translation active: id={localizedString.TableEntryReference.KeyId}; key={entry.Key}; value=[{value}].");
            }
            if (!Instance.replacementActivationLogged)
            {
                Instance.replacementActivationLogged = true;
                Instance.Logger.LogInfo($"Bulgarian numeric-key translation active; first key: {entry.Key}.");
            }
            return true;
        }
        finally
        {
            entry.Value = original;
        }
    }

    private static void LocalizeTextMesh_Start_Postfix(string ____locKey, TMPro.TextMeshPro ____tm)
    {
        if (____tm == null || string.IsNullOrWhiteSpace(____locKey) || ReferenceEquals(Instance, null) ||
            !string.Equals(LocalizationSettings.SelectedLocale?.Identifier.Code,
                Instance.translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Instance.translations.TryGetValue(____locKey, out var replacement))
        {
            ____tm.text = replacement;
            if (!Instance.replacementActivationLogged)
            {
                Instance.replacementActivationLogged = true;
                Instance.Logger.LogInfo($"Bulgarian LocalizeTextMesh replacement active for locale {LocalizationSettings.SelectedLocale.Identifier.Code}; first key: {____locKey}");
            }
        }
    }

    private static void LocalizeStringEvent_UpdateString_Prefix(LocalizeStringEvent __instance, ref string value)
    {
        if (__instance == null)
            return;

        var key = __instance.StringReference.TableEntryReference.Key;
        LogLocalizationPath("LocalizeStringEvent.UpdateString", key, value);
    }

    private static void TextAsLocKey_OnEnable_Postfix(
        Component __instance,
        LocalizeStringEvent ____ls,
        TextMeshProUGUI ____tm)
    {
        if (ReferenceEquals(Instance, null) || __instance == null || ____ls == null || ____tm == null)
            return;

        Instance.StartCoroutine(ApplyTextAsLocKeyAfterLocalization(____ls, ____tm));
    }

    private static IEnumerator ApplyTextAsLocKeyAfterLocalization(
        LocalizeStringEvent localizeStringEvent,
        TextMeshProUGUI textMesh)
    {
        yield return null;
        yield return new WaitForSeconds(0.1f);

        var key = localizeStringEvent.StringReference.TableEntryReference.Key;
        if (string.IsNullOrWhiteSpace(key) || !Instance.translations.TryGetValue(key, out var replacement))
            yield break;

        textMesh.text = replacement;
        ApplyTranslationForKey(key, ref replacement);
    }

    private static bool LocUtility_GetLocStringText_Prefix(string locKey, ref string __result)
    {
        LogLocalizationPath("LocUtility.GetLocStringText", locKey, __result);
        if (ReferenceEquals(Instance, null) || string.IsNullOrWhiteSpace(locKey) ||
            !string.Equals(LocalizationSettings.SelectedLocale?.Identifier.Code,
                Instance.translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !Instance.translations.TryGetValue(locKey, out var replacement))
            return true;

        __result = replacement;
        ApplyTranslationForKey(locKey, ref __result);
        return false;
    }

    private static void LocUtility_GetLocString_Postfix(string __0, ref LocalizedString __result)
    {
        LogLocalizationPath("LocUtility.GetLocString", __0, __result?.TableEntryReference.Key ?? "<null>");
        if (ReferenceEquals(Instance, null) || __result == null ||
            !string.Equals(LocalizationSettings.SelectedLocale?.Identifier.Code,
                Instance.translationLocaleCode.Value.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(__0) || !Instance.translations.ContainsKey(__0))
            return;

        __result = new LocalizedString((TableReference)"Base", (TableEntryReference)__0);
        Instance.Logger.LogInfo($"LocUtility.GetLocString redirected to Base/{__0} for locale bg.");
    }

    private static void LogLocalizationPath(string path, string key, string value)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value)
            return;

        var locale = LocalizationSettings.SelectedLocale?.Identifier.Code ?? "<null>";
        var normalizedKey = string.IsNullOrWhiteSpace(key) ? "<empty>" : key;
        var marker = $"{path}|{locale}|{normalizedKey}";
        if (!Instance.localizationProbeStates.Add(marker))
            return;

        var known = !string.IsNullOrWhiteSpace(key) && Instance.translations.ContainsKey(key);
        Instance.Logger.LogInfo($"Localization path: method={path}; locale={locale}; key={normalizedKey}; labelsEntry={known}; value={(value ?? "<null>")}");
    }

    private static void LocUtility_UpdateLocKey_Postfix(LocalizeStringEvent __0, string __1)
    {
        if (__0 == null || string.IsNullOrWhiteSpace(__1) || ReferenceEquals(Instance, null) ||
            !Instance.translations.TryGetValue(__1, out var replacement))
            return;

        var text = __0.GetComponent<TMP_Text>();
        if (text == null)
            return;

        text.text = replacement;
        ApplyTranslationForKey(__1, ref replacement);
    }

    private static void LocalizeDropdown_PopulateDropdown_Prefix(object __instance)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        var typeName = __instance.GetType().FullName;
        var field = AccessTools.Field(__instance.GetType(), "options");
        var list = field?.GetValue(__instance) as IList;
        var localeCount = LocalizationSettings.AvailableLocales?.Locales?.Count ?? -1;
        Instance.Logger.LogInfo($"PopulateDropdown details: type={typeName}, options={list?.Count ?? -1}, locales={localeCount}, optionsField={(field != null)}");
        if (field != null && list != null && localeCount > 0 && list.Count < localeCount)
        {
            Instance.Logger.LogInfo($"Bulgarian option insertion check: options={list.Count}, locales={localeCount}, configured={Instance.translationLocaleCode.Value.Trim()}");
            try
            {
                var optionType = __instance.GetType().Assembly.GetType("Utilities.Localization.LocalizedDropdownOption")
                    ?? AccessTools.TypeByName("Utilities.Localization.LocalizedDropdownOption");
                var textField = optionType == null ? null : AccessTools.Field(optionType, "text");
                Instance.Logger.LogInfo($"Preparing Bulgarian dropdown option: optionType={(optionType == null ? "missing" : optionType.FullName)}, textField={(textField != null)}");
                if (optionType != null && textField != null)
                {
                    var option = Activator.CreateInstance(optionType, nonPublic: true);
                    textField.SetValue(option, GetLocalizedStringSeed(list, textField));
                    list.Add(option);
                    Instance.Logger.LogInfo("Added a Bulgarian locale option to LocalizeDropdown.options before population.");
                }
            }
            catch (Exception ex)
            {
                Instance.Logger.LogWarning($"Could not add Bulgarian dropdown option: {ex}");
            }
        }

        Instance.Logger.LogInfo("LocalizeDropdown.PopulateDropdown observed.");
    }

    private static void LocalizeDropdown_AddOptions_Prefix(List<string> optionStrings)
    {
        if (ReferenceEquals(Instance, null) || optionStrings == null)
            return;

        var hadBulgarian = optionStrings.Contains("label_Bulgarian", StringComparer.Ordinal);
        Instance.Logger.LogInfo($"LocalizeDropdown.AddOptions observed: options={optionStrings.Count}; hasBulgarian={hadBulgarian}; values=[{string.Join(",", optionStrings)}]");
        if (string.Equals(Instance.translationLocaleCode.Value.Trim(), "bg", StringComparison.OrdinalIgnoreCase) && !hadBulgarian)
        {
            optionStrings.Add("label_Bulgarian");
            Instance.Logger.LogInfo($"Added label_Bulgarian to AddOptions input; options={optionStrings.Count}.");
        }
    }

    private static void LocalizeDropdown_Start_Prefix(object __instance)
    {
        if (ReferenceEquals(Instance, null) || __instance == null)
            return;

        var component = __instance as Component;
        Instance.Logger.LogInfo($"LocalizeDropdown.Start entered: object={component?.gameObject.name ?? __instance.GetType().Name}; path={(component == null ? "<unknown>" : GetHierarchyPath(component.transform))}");
    }

    private static void LocalizeDropdown_AddOptionsAsync_Prefix(List<string> optionStrings)
    {
        if (ReferenceEquals(Instance, null))
            return;

        Instance.Logger.LogInfo($"LocalizeDropdown.AddOptionsAsync entered: options={optionStrings?.Count ?? -1}; values=[{(optionStrings == null ? "<null>" : string.Join(",", optionStrings))}]");
    }

    private static void LocalizeDropdown_StateMachine_MoveNext_Prefix(object __instance)
    {
        if (ReferenceEquals(Instance, null) || !Instance.traceUiEvents.Value || __instance == null)
            return;

        var stateField = AccessTools.Field(__instance.GetType(), "<>1__state");
        var ownerField = AccessTools.Field(__instance.GetType(), "<>4__this");
        var state = stateField?.GetValue(__instance)?.ToString() ?? "<unknown>";
        var owner = ownerField?.GetValue(__instance) as Component;
        var dropdown = owner?.GetComponent<TMP_Dropdown>();
        var marker = $"state-machine|{__instance.GetType().FullName}|{state}|{owner?.gameObject.name ?? "<unknown>"}";
        if (!Instance.localizationProbeStates.Add(marker))
            return;

        var internalCount = owner == null ? -1 :
            AccessTools.Field(owner.GetType(), "options")?.GetValue(owner) is IList internalOptions
                ? internalOptions.Count
                : -1;
        Instance.Logger.LogInfo($"LocalizeDropdown state machine MoveNext: type={__instance.GetType().FullName}; state={state}; object={owner?.gameObject.name ?? "<unknown>"}; internalOptions={internalCount}; visibleOptions={dropdown?.options?.Count ?? -1}");
    }

    private static LocalizedString GetLocalizedStringSeed(IList options, FieldInfo textField)
    {
        if (options != null && textField != null)
        {
            for (var index = options.Count - 1; index >= 0; index--)
            {
                if (options[index] == null)
                    continue;

                if (textField.GetValue(options[index]) is LocalizedString existing && !existing.IsEmpty)
                    return existing;
            }
        }

        // This is only a construction-safe empty value. The visible label
        // replacement will be implemented separately once the game's actual
        // TMP option-writing callback is identified.
        return new LocalizedString();
    }

    private void ProbeBundles(string source)
    {
        var bundles = AssetBundle.GetAllLoadedAssetBundles().ToArray();
        Logger.LogInfo($"AssetBundle probe ({source}): {bundles.Length} loaded bundle(s)");

        if (!dumpLoadedBundles.Value)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(dumpPath));
        using var writer = new StreamWriter(dumpPath, append: true);
        foreach (var bundle in bundles)
        {
            var assetNames = bundle.GetAllAssetNames();
            foreach (var assetName in assetNames)
                writer.WriteLine($"{DateTime.UtcNow:O}\t{source}\t{bundle.name}\t{assetName}");

            if (dumpLocalizationTables.Value)
                DumpLocalizationTables(source, bundle);
        }
    }

    private void DumpLocalizationTables(string source, AssetBundle bundle)
    {
        foreach (var asset in bundle.LoadAllAssets())
        {
            DumpLocalizationAsset(source, bundle, asset);
        }

        // Addressables may expose locale assets by name without instantiating them
        // through LoadAllAssets(). Load them explicitly so Russian/other locales
        // can be used as QA references even when English is the active locale.
        foreach (var assetName in bundle.GetAllAssetNames().Where(name =>
            name.IndexOf("Assets/Localization/", StringComparison.OrdinalIgnoreCase) >= 0 &&
            name.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)))
        {
            var asset = bundle.LoadAsset(assetName);
            Logger.LogInfo($"Localization asset lookup: {assetName} -> {(asset == null ? "<null>" : asset.GetType().FullName)}");
            if (asset != null)
                DumpLocalizationAsset(source, bundle, asset);
        }
    }

    private void DumpLocalizationAsset(string source, AssetBundle bundle, UnityEngine.Object asset)
    {
        var type = asset.GetType();
        if (type.FullName.IndexOf("StringTable", StringComparison.Ordinal) < 0)
            return;

        var tableName = GetPropertyString(asset, "TableCollectionName");
        var locale = GetPropertyString(asset, "LocaleIdentifier");
        var tableId = bundle.name + "\t" + tableName + "\t" + locale;
        if (!dumpedTables.Add(tableId))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(localizationDumpPath));
        using var writer = new StreamWriter(localizationDumpPath, append: true);
        var values = type.GetProperty("Values", BindingFlags.Instance | BindingFlags.Public)?.GetValue(asset) as IEnumerable;
        if (values == null)
            return;

        foreach (var entry in values)
        {
            if (entry == null)
                continue;

            var key = GetPropertyString(entry, "Key");
            var keyId = GetPropertyString(entry, "KeyId");
            var value = GetPropertyString(entry, "Value");
            writer.WriteLine($"{DateTime.UtcNow:O}\t{source}\t{bundle.name}\t{tableName}\t{locale}\t{Escape(key)}\t{keyId}\t{Escape(value)}");
        }

        Logger.LogInfo($"Localization table dump: {tableName} ({locale}) from {bundle.name}");
    }

    private static string GetPropertyString(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(instance)?.ToString() ?? string.Empty;
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }
}
