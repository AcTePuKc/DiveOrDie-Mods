using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

namespace DiveOrDieSurvivorNameLoader;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.diveordie.translationbulgarian.survivornames";
    public const string PluginName = "Dive or Die Bulgarian Survivor Names";
    public const string PluginVersion = "0.1.0";

    private readonly Dictionary<string, List<string>> namePools =
        new Dictionary<string, List<string>>(StringComparer.Ordinal);
    private Harmony harmony;
    private bool applied;
    private static bool fontOverrideLogged;
    private static Plugin Instance { get; set; }

    private void Awake()
    {
        Instance = this;
        harmony = new Harmony(PluginGuid);
        PatchLocalizedFontLoaders();

        var jsonPath = Path.Combine(Paths.PluginPath, "DiveOrDieTranslationMod", "survivor-names.json");
        if (!LoadNamePools(jsonPath))
            return;

        var gameDatabaseType = AccessTools.TypeByName("GameDatabase");
        var awake = gameDatabaseType == null ? null : AccessTools.Method(gameDatabaseType, "Awake");
        if (awake != null)
            harmony.Patch(awake, postfix: new HarmonyMethod(typeof(Plugin), nameof(GameDatabase_Awake_Postfix)));

        var database = gameDatabaseType == null
            ? null
            : AccessTools.Property(gameDatabaseType, "Instance")?.GetValue(null);
        if (database != null)
            ApplyNamePools(database);
    }

    private void PatchLocalizedFontLoaders()
    {
        foreach (var typeName in new[] { "LocalizeFont", "LocalizeFontNotUI" })
        {
            var type = AccessTools.TypeByName(typeName);
            var loadFont = type == null ? null : AccessTools.Method(type, "LoadFont");
            if (loadFont == null)
            {
                Logger.LogWarning($"Could not find {typeName}.LoadFont for the Bulgarian font override.");
                continue;
            }

            harmony.Patch(loadFont,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalizeFont_LoadFont_Prefix)));
        }
    }

    private static void LocalizeFont_LoadFont_Prefix(LocalizedTmpFont ___localizedFont)
    {
        if (___localizedFont == null)
            return;

        var selectedCode = LocalizationSettings.SelectedLocale?.Identifier.Code;
        if (!string.Equals(selectedCode, "bg", StringComparison.OrdinalIgnoreCase))
        {
            if (___localizedFont.LocaleOverride != null)
                ___localizedFont.LocaleOverride = null;
            return;
        }

        var russianLocale = LocalizationSettings.AvailableLocales?.GetLocale("ru");
        if (russianLocale == null)
            return;

        ___localizedFont.LocaleOverride = russianLocale;
        if (!fontOverrideLogged && !ReferenceEquals(Instance, null))
        {
            fontOverrideLogged = true;
            Instance.Logger.LogInfo("Bulgarian font override active: localized TMP fonts use the Russian Cyrillic asset table.");
        }
    }

    private bool LoadNamePools(string path)
    {
        if (!File.Exists(path))
        {
            Logger.LogInfo($"Survivor name file not found; original names remain active: {path}");
            return false;
        }

        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var id in new[] { "MALE_NAMES", "FEMALE_NAMES", "SURNAMES" })
            {
                var values = root[id]?["Values"]?
                    .Values<string>()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .ToList();
                if (values == null || values.Count == 0)
                    throw new InvalidDataException($"{id}.Values is missing or empty.");

                namePools[id] = values;
            }

            Logger.LogInfo($"Loaded survivor names: male={namePools["MALE_NAMES"].Count}, female={namePools["FEMALE_NAMES"].Count}, surnames={namePools["SURNAMES"].Count}.");
            return true;
        }
        catch (Exception ex)
        {
            namePools.Clear();
            Logger.LogWarning($"Invalid survivor-names.json; original names remain active: {ex.Message}");
            return false;
        }
    }

    private static void GameDatabase_Awake_Postfix(object __instance)
    {
        if (!ReferenceEquals(Instance, null) && __instance != null)
            Instance.ApplyNamePools(__instance);
    }

    private void ApplyNamePools(object database)
    {
        if (applied || database == null || namePools.Count != 3)
            return;

        try
        {
            var nameDataType = AccessTools.TypeByName("SurvivorNameGenerationData");
            var getDataDefinition = database.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "GetData" && method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(string));
            var valuesProperty = nameDataType == null ? null : AccessTools.Property(nameDataType, "Values");
            if (nameDataType == null || getDataDefinition == null || valuesProperty == null)
                throw new MissingMemberException("The survivor name database API was not found.");

            var getNameData = getDataDefinition.MakeGenericMethod(nameDataType);
            foreach (var pair in namePools)
            {
                var data = getNameData.Invoke(database, new object[] { pair.Key });
                var values = data == null ? null : valuesProperty.GetValue(data) as IList;
                if (values == null)
                    throw new InvalidDataException($"The game's {pair.Key} pool is unavailable.");

                values.Clear();
                foreach (var value in pair.Value)
                    values.Add(value);
            }

            applied = true;
            Logger.LogInfo($"Applied survivor names: male={namePools["MALE_NAMES"].Count}, female={namePools["FEMALE_NAMES"].Count}, surnames={namePools["SURNAMES"].Count}.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not apply survivor names; original names remain active: {ex.GetBaseException().Message}");
        }
    }

}
